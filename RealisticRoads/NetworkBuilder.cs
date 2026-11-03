using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace RealisticRoads
{
    // Creates and removes roads. All game changes happen on the simulation
    // thread; the public methods only queue the work.
    public static partial class NetworkBuilder
    {
        private const int MaxUndo = 20;
        private const float ContinueAngle = 55f;     // continue an existing road if within this angle
        // The bridge put into the road above an underpass reaches past our road's
        // edges and the banks of our cutting (8 m deep, sloping out), so its ends
        // stand on untouched ground.
        private const float UnderpassBridgeExtra = RoadPlanner.CutDepth * 1.5f + 6f;

        // An existing segment we cut into pieces, so undo can put it back.
        private class Split
        {
            public ushort Original;                 // the segment that was cut (id reused for the restored one)
            public NetInfo Info;
            public ushort StartNode, EndNode;
            public Vector3 StartDirection, EndDirection;
            public bool Invert;
            public NetSegment.Flags Yield;          // its yield (stop) signs at either end
            public ushort NameSeed;                 // its generated street name
            public string CustomName;               // the name the player gave it (null: none)
            public bool Released;                   // the original has been removed (undo must rebuild it)
            public readonly List<ushort> Segments = new List<ushort>();
            public readonly List<ushort> Nodes = new List<ushort>();
            public readonly Stamps Stamps = new Stamps();
        }

        // The game reuses a freed id for the next road built, so ids alone don't
        // say a road is still ours: each one's build index (unique per thing
        // built) is remembered and must still match before undo touches it.
        private class Stamps
        {
            private readonly Dictionary<ushort, uint> segments = new Dictionary<ushort, uint>();
            private readonly Dictionary<ushort, uint> nodes = new Dictionary<ushort, uint>();

            public void Segment(ushort id) => segments[id] = NetManager.instance.m_segments.m_buffer[id].m_buildIndex;
            public void Node(ushort id) => nodes[id] = NetManager.instance.m_nodes.m_buffer[id].m_buildIndex;

            public bool OwnsSegment(ushort id)
            {
                uint stamp;
                var s = NetManager.instance.m_segments.m_buffer[id];
                return segments.TryGetValue(id, out stamp) && (s.m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None &&
                       s.m_buildIndex == stamp;
            }

            public bool OwnsNode(ushort id)
            {
                uint stamp;
                return nodes.TryGetValue(id, out stamp) && NodeExists(id) && NetManager.instance.m_nodes.m_buffer[id].m_buildIndex == stamp;
            }

            public void Replace(ushort oldSegment, ushort newSegment)
            {
                if (!segments.Remove(oldSegment)) return;
                Segment(newSegment);
            }
        }

        // What one build created or changed, so one undo reverses all of it.
        private class Build
        {
            public readonly List<ushort> Segments = new List<ushort>();
            public readonly List<ushort> Nodes = new List<ushort>();   // nodes we created (not snapped ones)
            public readonly List<Split> Splits = new List<Split>();
            public readonly Stamps Stamps = new Stamps();
            // Existing nodes we moved (overpass lowering) and where they were.
            public readonly List<KeyValuePair<ushort, Vector3>> Moves = new List<KeyValuePair<ushort, Vector3>>();
            // Existing segments whose end directions we turned (a slid node) and what they were.
            public readonly List<Turned> Turns = new List<Turned>();
            public int Paid;                         // construction cost charged (refunded on undo)
            public ItemClass PaidClass;
            public int Group;                        // builds with the same group (not 0) are undone together
            // Existing nodes we gave another road (an upgrade) and what they were.
            public readonly List<KeyValuePair<ushort, NetInfo>> NodeInfos = new List<KeyValuePair<ushort, NetInfo>>();
        }

        private struct Turned
        {
            public ushort Segment;
            public uint BuildIndex;
            public Vector3 StartDirection, EndDirection;
        }

        // Only touched on the simulation thread.
        private static readonly List<Build> undoStack = new List<Build>();
        // How many builds can be undone, for the panel's Undo button (any thread).
        public static volatile int UndoCount;

        private static void Keep(Build build)
        {
            undoStack.Add(build);
            if (undoStack.Count > MaxUndo) undoStack.RemoveAt(0);
            UndoCount = undoStack.Count;
        }

        // Builds 'plan' (see RoadPlanner) with 'info'. Builds given the same
        // 'group' (NewGroup) are undone together; 'done' is told on the
        // simulation thread whether it was built.
        public static void BuildCurve(NetInfo info, Plan plan, int group = 0, Action<bool> done = null)
        {
            SimulationManager.instance.AddAction(() =>
            {
                bool built = BuildNow(info, plan, group);
                if (done != null) done(built);
            });
        }

        // Upgrades an existing stretch (see RoadUpgrade); one Ctrl+Z puts it back.
        public static void Upgrade(UpgradePlan plan)
        {
            SimulationManager.instance.AddAction(() => UpgradeNow(plan));
        }

        // Simulation thread. Each segment is replaced on the same nodes (through
        // SplitSegment with no cuts, so undo rebuilds the original), then the
        // nodes between upgraded segments take the new road, and the pillars
        // follow it.
        private static bool UpgradeNow(UpgradePlan plan)
        {
            var build = new Build();
            try
            {
                var net = NetManager.instance;
                if (InGame() && plan.Cost > 0)
                {
                    var economy = EconomyManager.instance;
                    var construction = ResourceNamed("Construction");
                    if (economy.PeekResource(construction, plan.Cost) != plan.Cost)
                    {
                        Mod.Log("Not upgraded: it costs " + (plan.Cost / 100) + " and the city can't afford it");
                        return false;
                    }
                    economy.FetchResource(construction, plan.Cost, plan.Info.m_class);
                    build.Paid = plan.Cost;
                    build.PaidClass = plan.Info.m_class;
                }
                int changed = 0;
                for (int i = 0; i < plan.Segments.Count; i++)
                {
                    ushort segment = plan.Segments[i];
                    var s = net.m_segments.m_buffer[segment];
                    // Changed since it was planned: left alone.
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info != plan.OldInfos[i]) continue;
                    SplitSegment(segment, new float[0], new[] { plan.NewInfos[i] }, build, plan.Inverts != null ? plan.Inverts[i] : (bool?)null);
                    changed++;
                }
                foreach (var pair in plan.Nodes)
                {
                    ushort node = pair.Key;
                    if (!NodeExists(node)) continue;
                    build.NodeInfos.Add(new KeyValuePair<ushort, NetInfo>(node, net.m_nodes.m_buffer[node].Info));
                    SetNodeInfo(node, pair.Value);
                }
                Keep(build);
                Mod.Log("Upgraded " + changed + " segment(s) to " + plan.Info.name + ". Ctrl+Z in the tool puts the old road back.");
                return true;
            }
            catch (Exception e)
            {
                Mod.LogError("Upgrading the road failed; putting back what was changed", e);
                Remove(build);
                Refund(build);
                return false;
            }
        }

        private static void SetNodeInfo(ushort node, NetInfo info)
        {
            if (info == null) return;
            var net = NetManager.instance;
            net.m_nodes.m_buffer[node].m_infoIndex = (ushort)info.m_prefabDataIndex;
            net.UpdateNode(node);
            UpdatePillar(node);
        }

        private static int lastGroup;

        // Main thread: a group for builds that are undone together.
        public static int NewGroup()
        {
            return ++lastGroup;
        }

        // A median ramp road drawn along an existing road: that road, from the
        // ramp's node ('origin') on under the line ('line', from that node
        // outward), becomes the median ramp road's ground road, replacing the road beside the ramp: whole segments while the
        // road runs under the line (through junctions, which stay junctions),
        // and the one the line ends in is cut there (the rest stays the road
        // it was). Only the road the ramp rises out of: a ground road with
        // 'lanes' vehicle lanes. One build in 'group' (undone with the rest);
        // 'done' is told (simulation thread) the node it got to, how far along
        // the line that is, and how many segments became the ground road.
        public static void ConvertAlong(List<Vector3> line, ushort origin, NetInfo ground, int lanes, int group, Action<ushort, float, int> done)
        {
            SimulationManager.instance.AddAction(() =>
            {
                var build = new Build { Group = group };
                ushort node = origin;
                float along = 0f;
                int converted = 0;
                try
                {
                    var net = NetManager.instance;
                    var segments = net.m_segments.m_buffer;
                    var nodes = net.m_nodes.m_buffer;
                    float lineLength = CurveMath.Length(line);
                    Vector3 heading = CurveMath.FlatNormal(line[1] - line[0]);
                    float ahead = Mathf.Cos(30f * Mathf.Deg2Rad);
                    var passed = new List<ushort>();
                    for (int guard = 0; guard < 64; guard++)
                    {
                        // The road going on from the node along the line.
                        ushort segment = 0;
                        float best = ahead;
                        for (int i = 0; i < 8; i++)
                        {
                            ushort s = nodes[node].GetSegment(i);
                            if (s == 0 || !ConvertsToGround(segments[s].Info, lanes)) continue;
                            Vector3 away = CurveMath.FlatNormal(segments[s].m_startNode == node ? segments[s].m_startDirection : segments[s].m_endDirection);
                            float dot = Vector3.Dot(away, heading);
                            if (dot <= best) continue;
                            Vector3 a, b, c, d;
                            CrossingFinder.SegmentBezier(s, out a, out b, out c, out d);
                            float mid;
                            if (OffLine(line, CurveMath.Bezier(a, b, c, d, 0.5f), out mid)) continue;
                            best = dot;
                            segment = s;
                        }
                        if (segment == 0) break;
                        bool fromStart = segments[segment].m_startNode == node;
                        ushort far = fromStart ? segments[segment].m_endNode : segments[segment].m_startNode;
                        float farAlong;
                        bool farOff = OffLine(line, nodes[far].m_position, out farAlong);
                        var road = segments[segment].Info;
                        // The road leaves the line in this segment (it curves away) while the
                        // line goes on: this segment stays the road it is, and the ground road
                        // is built on from this node along the line (its 'along' is where).
                        Vector3 lineEnd = line[line.Count - 1], endOnRoad;
                        CrossingFinder.ClosestT(segment, lineEnd, out endOnRoad);
                        bool endsHere = CurveMath.FlatDistance(endOnRoad, lineEnd) <= LineCover;
                        if (farOff && !endsHere) break;
                        if (farOff || farAlong >= lineLength - 1f)
                        {
                            // The line ends in this segment: cut it there.
                            Vector3 onRoad;
                            float t = CrossingFinder.ClosestT(segment, line[line.Count - 1], out onRoad);
                            float length = Mathf.Max(segments[segment].m_averageLength, 1f);
                            if (t * length < Crossing.NodeSnap || (1f - t) * length < Crossing.NodeSnap)
                            {
                                // At (or by) one of its ends: whole, or not at all.
                                bool toFar = fromStart ? t > 0.5f : t < 0.5f;
                                if (!toFar) break;
                                SplitSegment(segment, new float[0], new[] { ground }, build);
                                converted++;
                                node = far;
                                along = lineLength;
                                break;
                            }
                            var cut = SplitSegment(segment, new[] { t }, fromStart ? new[] { ground, road } : new[] { road, ground }, build);
                            converted++;
                            if (cut != null && cut.Nodes.Count > 0) node = cut.Nodes[0];
                            along = lineLength;
                            break;
                        }
                        SplitSegment(segment, new float[0], new[] { ground }, build);
                        converted++;
                        heading = CurveMath.FlatNormal(nodes[far].m_position - nodes[node].m_position);
                        along = farAlong;
                        if (SegmentCount(far) == 2) passed.Add(far);
                        node = far;
                    }
                    // A node between two ground road segments is drawn as one.
                    foreach (ushort through in passed)
                    {
                        bool all = true;
                        for (int i = 0; i < 8; i++)
                        {
                            ushort s = nodes[through].GetSegment(i);
                            if (s != 0 && segments[s].Info != ground) all = false;
                        }
                        if (!all || nodes[through].Info == ground) continue;
                        build.NodeInfos.Add(new KeyValuePair<ushort, NetInfo>(through, nodes[through].Info));
                        SetNodeInfo(through, ground);
                    }
                    if (converted > 0) Keep(build);
                    Mod.Log("  " + converted + " segment(s) of the road under the line became " + ground.name + ", to node #" + node +
                            " (" + along.ToString("F0") + " of " + lineLength.ToString("F0") + " m)");
                }
                catch (Exception e)
                {
                    Mod.LogError("Turning the road under the median ramp into its ground road failed; put back", e);
                    Remove(build);
                    converted = -1;
                }
                if (done != null) done(node, along, converted);
            });
        }

        // A median cut's road node at 'p': the road there (a ground road with
        // 'lanes' lanes, running along 'heading' within 30 degrees) split at it,
        // or its end node if 'p' is by one. One build in 'group'; 'done' is told
        // (simulation thread) the node, 0 if no such road is there.
        public static void RoadNodeAt(Vector3 p, Vector3 heading, int lanes, int group, Action<ushort> done)
        {
            SimulationManager.instance.AddAction(() =>
            {
                var build = new Build { Group = group };
                ushort result = 0;
                try
                {
                    var net = NetManager.instance;
                    var segments = net.m_segments.m_buffer;
                    Vector3 way = CurveMath.FlatNormal(heading);
                    float ahead = Mathf.Cos(30f * Mathf.Deg2Rad);
                    ushort best = 0;
                    float bestDistance = float.MaxValue, bestT = 0f;
                    foreach (ushort s in RoadPlanner.NearbySegments(p, 40f))
                    {
                        if ((segments[s].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                        var info = segments[s].Info;
                        if (!ConvertsToGround(info, lanes)) continue;
                        Vector3 onRoad;
                        float t = CrossingFinder.ClosestT(s, p, out onRoad);
                        float distance = CurveMath.FlatDistance(onRoad, p);
                        if (distance > info.m_halfWidth || distance >= bestDistance) continue;
                        Vector3 a, b, c, d;
                        CrossingFinder.SegmentBezier(s, out a, out b, out c, out d);
                        if (way != Vector3.zero && Mathf.Abs(Vector3.Dot(CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t)), way)) < ahead) continue;
                        best = s;
                        bestDistance = distance;
                        bestT = t;
                    }
                    if (best != 0)
                    {
                        float length = Mathf.Max(segments[best].m_averageLength, 1f);
                        if (bestT * length < Crossing.NodeSnap) result = segments[best].m_startNode;
                        else if ((1f - bestT) * length < Crossing.NodeSnap) result = segments[best].m_endNode;
                        else
                        {
                            var cut = SplitSegment(best, new[] { bestT }, null, build);
                            if (cut != null && cut.Nodes.Count > 0) result = cut.Nodes[0];
                            Keep(build);
                        }
                    }
                    Mod.Log("  Median cut: the road under its start " + (result != 0 ? "is split at node #" + result : "wasn't found"));
                }
                catch (Exception e)
                {
                    Mod.LogError("Splitting the road under the median cut's start failed; put back", e);
                    Remove(build);
                    result = 0;
                }
                if (done != null) done(result);
            });
        }

        // The node where a median ramp leaves (or lands in) the road, made with
        // start pieces: the full road's last StartLength there becomes its start
        // piece ('fullStart'), the ground road's first becomes its
        // ('groundStart') and the ramp's segment there its own; none draws anything at that junction, so
        // only the roads' own surfaces meet there, squared off edge to edge
        // (SplitRoadsCornerPatch). A longer segment is cut, the piece by the
        // node changed, the rest kept; a shorter one changes whole, unless its
        // other end is a junction too (it would draw nothing there either): then
        // the build fails. One build in 'group' (undone with the rest); 'done' is
        // told (simulation thread) whether every piece was made.
        public static void MedianTransition(ushort node, NetInfo fullStart, NetInfo groundStart, int group, Action<bool, string> done)
        {
            SimulationManager.instance.AddAction(() =>
            {
                var build = new Build { Group = group };
                string why = null;
                var made = new List<string>();
                try
                {
                    var net = NetManager.instance;
                    var segments = net.m_segments.m_buffer;
                    var at = new List<ushort>();
                    for (int i = 0; i < 8; i++)
                    {
                        ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                        if (s != 0) at.Add(s);
                    }
                    // Only the node RR built: the full road, its ground road and at most
                        // one ramp, the full road going on along the ground road. Anything
                        // else (a side street at a junction) is never changed: checked
                        // before anything is.
                    why = TransitionTopology(node, at);
                    if (why != null)
                    {
                        Mod.Log("Median ramp node #" + node + ": start pieces not made: " + why);
                        if (done != null) done(false, why);
                        return;
                    }
                    bool full = false, ground = false;
                    foreach (ushort s in at)
                    {
                        var info = segments[s].Info;
                        if (SplitRoads.IsRamp(info))
                        {
                            // The ramp's segment at the node becomes its start piece
                            // (whole); the rest of the ramp stays a normal road.
                            var rampStart = SplitRoads.RampStartFor(info);
                            if (rampStart == null)
                            {
                                why = "the " + info.name + "'s start piece wasn't made when the city loaded";
                                break;
                            }
                            if (rampStart == info) continue;
                            float rampLength = segments[s].m_averageLength;
                            SplitSegment(s, new float[0], new[] { rampStart }, build);
                            made.Add(info.name + " #" + s + " whole (" + rampLength.ToString("F0") + " m)");
                            continue;
                        }
                        bool isGround = SplitRoads.IsSplit(info);
                        var target = isGround ? groundStart : fullStart;
                        if (isGround) ground = true; else full = true;
                        if (info == target || (!isGround && SplitRoads.IsFullStart(info))) continue;
                        float length = Mathf.Max(segments[s].m_averageLength, 1f);
                        bool fromStart = segments[s].m_startNode == node;
                        if (!isGround)
                        {
                            // The full road's piece by the node widens to the ground
                            // road's lanes along it (the slide all done at the node:
                            // its end node if the node isn't its start).
                            bool invert = (segments[s].m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
                            // The full road's own taper, else (a road like it: with a median, trees)
                            // the one of the road its start piece is made from.
                            var taper = SplitRoads.FullTaperFor(info, groundStart, fromStart == Mod.FlipRampTaper, invert) ??
                                        SplitRoads.FullTaperFor(SplitRoads.OriginalOf(fullStart), groundStart, fromStart == Mod.FlipRampTaper, invert);
                            if (taper != null) target = taper;
                        }
                        ushort far = fromStart ? segments[s].m_endNode : segments[s].m_startNode;
                        if (length <= SplitRoads.StartLength + MinPiece)
                        {
                            if (SegmentCount(far) > 2 && length >= MinStartPiece + MinPiece)
                            {
                                // A junction close by: a shorter start piece, the road's own
                                // piece left at that junction (its junction pieces drawn there).
                                float shortT = fromStart ? (length - MinPiece) / length : MinPiece / length;
                                SplitSegment(s, new[] { shortT }, fromStart ? new[] { target, info } : new[] { info, target }, build);
                                made.Add(info.name + " #" + s + " (its " + (length - MinPiece).ToString("F0") + " m by the node: a junction " + length.ToString("F0") + " m away)");
                                continue;
                            }
                            if (SegmentCount(far) > 2)
                            {
                                why = "the " + info.name + " at the ramp's node is only " + length.ToString("F0") + " m to the next junction (at least " +
                                      (MinStartPiece + MinPiece).ToString("F0") + " m needed for its start piece)";
                                break;
                            }
                            SplitSegment(s, new float[0], new[] { target }, build);
                            made.Add(info.name + " #" + s + " whole (" + length.ToString("F0") + " m)");
                            continue;
                        }
                        float t = fromStart ? SplitRoads.StartLength / length : 1f - SplitRoads.StartLength / length;
                        SplitSegment(s, new[] { t }, fromStart ? new[] { target, info } : new[] { info, target }, build);
                        made.Add(info.name + " #" + s + " (its " + SplitRoads.StartLength.ToString("F0") + " m by the node)");
                    }
                    if (why == null && (!full || !ground)) why = "the ramp's node #" + node + " has no " + (full ? "ground road" : "full road");
                    if (why != null)
                    {
                        Remove(build);
                        Mod.Log("Median ramp node #" + node + ": start pieces not made: " + why);
                        if (done != null) done(false, why);
                        return;
                    }
                    if (build.Splits.Count > 0) Keep(build);
                    Mod.Log("Median ramp node #" + node + ": start pieces (no junction pieces drawn there): " + string.Join("; ", made.ToArray()));
                    if (done != null) done(true, null);
                }
                catch (Exception e)
                {
                    Mod.LogError("Making the median ramp node #" + node + "'s start pieces failed; put back", e);
                    Remove(build);
                    if (done != null) done(false, "making its start pieces failed (" + e.Message + ")");
                }
            });
        }

        // Why the node isn't one to make start pieces at, or null: exactly one
        // ground road, one other road (the full road, going on along the ground
        // road within TransitionBend) and at most one ramp.
        private const float TransitionBend = 45f;

        private static string TransitionTopology(ushort node, List<ushort> at)
        {
            var segments = NetManager.instance.m_segments.m_buffer;
            int ramps = 0, grounds = 0, others = 0;
            ushort full = 0, ground = 0;
            foreach (ushort s in at)
            {
                var info = segments[s].Info;
                if (SplitRoads.IsRamp(info)) ramps++;
                else if (SplitRoads.IsSplit(info)) { grounds++; ground = s; }
                else { others++; full = s; }
            }
            if (grounds != 1 || others != 1 || ramps > 1)
                return "node #" + node + " has " + others + " road(s), " + grounds + " ground road(s) and " + ramps + " ramp(s) at it (a junction?): " +
                       "it must be only the road, its ground road and the ramp, so nothing there was changed";
            if (Vector3.Dot(Away(full, node), Away(ground, node)) > -Mathf.Cos(TransitionBend * Mathf.Deg2Rad))
                return "at node #" + node + " the road doesn't go on along its ground road (they meet at more than " + TransitionBend.ToString("F0") + " degrees)";
            return null;
        }

        private const float LineCover = 3f;   // a road this close to the drawn line (flat) runs under it

        // Off the line (further than LineCover from it); 'along': how far along it the nearest place is.
        private static bool OffLine(List<Vector3> line, Vector3 p, out float along)
        {
            float best = float.MaxValue, run = 0f;
            along = 0f;
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Vector3 a = line[i], b = line[i + 1];
                float length = CurveMath.FlatDistance(a, b);
                if (length < 0.001f) continue;
                Vector3 dir = new Vector3((b.x - a.x) / length, 0f, (b.z - a.z) / length);
                float t = Mathf.Clamp((p.x - a.x) * dir.x + (p.z - a.z) * dir.z, 0f, length);
                Vector3 q = new Vector3(a.x + dir.x * t, 0f, a.z + dir.z * t);
                float d = CurveMath.FlatDistance(q, p);
                if (d < best)
                {
                    best = d;
                    along = run + t;
                }
                run += length;
            }
            return best > LineCover;
        }

        // A road on the ground (not a raised, sunk or median ramp version) with
        // 'lanes' vehicle lanes: one a median ramp road's ground road replaces.
        private static bool ConvertsToGround(NetInfo info, int lanes)
        {
            // Never one of RR's own pieces (a ramp, a ground road, or the full road's
            // start or taper piece at another median ramp's or cut's node).
            if (info == null || SplitRoads.IsOwnPiece(info) || SplitRoads.IsSplit(info) || RoadUpgrade.VersionOf(info) >= 0) return false;
            if (info.m_netAI == null || info.m_netAI.GetType().Name != "RoadAI") return false;
            return info.m_forwardVehicleLaneCount + info.m_backwardVehicleLaneCount == lanes &&
                   info.m_forwardVehicleLaneCount == info.m_backwardVehicleLaneCount;
        }

        // The node where a median ramp road's ramp leaves (or lands in) the
        // road, set up as a ramp junction: no traffic lights (the game's own setting, kept by marking
        // them custom), and with TM:PE no pedestrian crossing on any road end
        // there and entering a blocked junction allowed. Queued after the
        // roads at it are built.
        public static void MedianOrigin(ushort node, int attempt = 0)
        {
            if (node == 0) return;
            SimulationManager.instance.AddAction(() =>
            {
                try
                {
                    var net = NetManager.instance;
                    // By name: the game's flags (kept off by marking them custom, as its
                    // own traffic light toggle does).
                    var flags = net.m_nodes.m_buffer[node].m_flags;
                    string lights = "traffic lights left to the game (its flags not found)";
                    if (Enum.IsDefined(typeof(NetNode.Flags), "TrafficLights") && Enum.IsDefined(typeof(NetNode.Flags), "CustomTrafficLights"))
                    {
                        var on = (NetNode.Flags)Enum.Parse(typeof(NetNode.Flags), "TrafficLights");
                        var custom = (NetNode.Flags)Enum.Parse(typeof(NetNode.Flags), "CustomTrafficLights");
                        net.m_nodes.m_buffer[node].m_flags = (flags & ~on) | custom;
                        lights = "traffic lights off";
                    }
                    var ends = new List<string>();
                    int tmpeSet = 0;
                    string tmpe = TmpeManager("JunctionRestrictionsManager", out object restrictions);
                    // With TM:PE its own traffic light setting rules the node (it puts
                    // the game's back): off there too.
                    TmpeManager("TrafficLightManager", out object trafficLights);
                    bool tmpeLightsOff = trafficLights != null && TmpeLightsOff(trafficLights, node);
                    for (int i = 0; i < 8; i++)
                    {
                        ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                        if (s == 0) continue;
                        bool start = net.m_segments.m_buffer[s].m_startNode == node;
                        var info = net.m_segments.m_buffer[s].Info;
                        ends.Add("#" + s + " " + (info != null ? info.name : "?") + (start ? " (start)" : " (end)"));
                        if (restrictions == null) continue;
                        if (TmpeSet(restrictions, "SetPedestrianCrossingAllowed", s, start, false)) tmpeSet++;
                        TmpeSet(restrictions, "SetEnteringBlockedJunctionAllowed", s, start, true);
                    }
                    // TM:PE takes a road end only once it has set the new road up
                    // (not yet in the same step it was built in): asked again a
                    // little later, a few times.
                    if (((restrictions != null && tmpeSet < ends.Count) || (trafficLights != null && !tmpeLightsOff)) && attempt < TmpeAttempts)
                    {
                        lock (tmpeRetries) tmpeRetries.Add(new KeyValuePair<ushort, int>(node, attempt + 1));
                        return;
                    }
                    string laneLinks = TmpeConnectLanes(node);
                    lock (dumps) dumps.Add(new KeyValuePair<ushort, int>(node, DumpAfterFrames));
                    Mod.Log("Median ramp node #" + node + ": " + ends.Count + " road end(s): " + string.Join(", ", ends.ToArray()) +
                            "; " + lights + (trafficLights != null ? (tmpeLightsOff ? ", TM:PE traffic light off" : ", TM:PE traffic light NOT taken off") : "") +
                            "; " + (restrictions != null ? "TM:PE: pedestrian crossing off on " + tmpeSet + " of them" +
                            (attempt > 0 ? " (asked " + (attempt + 1) + " times)" : "") : "TM:PE: " + tmpe) + "; lanes: " + laneLinks);
                }
                catch (Exception e)
                {
                    Mod.LogError("Setting up the median ramp node #" + node + " failed", e);
                }
            });
        }

        // The lanes at a median ramp's node joined with TM:PE lane
        // connections: the full road's inner lanes each
        // way to the ramp's lanes, its outer ones to the ground road's, and back
        // the same way. Lanes are told apart by where they end at the node
        // across the ramp's direction there (which side, how far out), so it
        // holds whichever way each segment was drawn. With TM:PE only (RR
        // leaves the game's own lane choice alone otherwise); what was joined
        // goes to the log, lane by lane.
        private static string TmpeConnectLanes(ushort node)
        {
            try
            {
                var net = NetManager.instance;
                var segments = net.m_segments.m_buffer;
                ushort ramp = 0, ground = 0, full = 0;
                int others = 0;
                for (int i = 0; i < 8; i++)
                {
                    ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (s == 0) continue;
                    var info = segments[s].Info;
                    if (SplitRoads.IsRamp(info)) ramp = s;
                    else if (SplitRoads.IsSplit(info)) ground = s;
                    else if (full == 0) full = s;
                    else others++;
                }
                if (ramp == 0 || ground == 0 || full == 0 || others > 0)
                    return "not joined by RR (the node has " + (others > 0 ? "more roads than the full road, ground road and ramp" : "not all three of them") + ")";
                string tmpe = TmpeManager("LaneConnectionManager", out object manager);
                if (manager == null) return "left to the game (TM:PE " + tmpe + ")";
                Vector3 at = net.m_nodes.m_buffer[node].m_position;
                float keep = LeftHandTraffic() ? -1f : 1f;   // the side traffic heading away from the full road drives on
                // Each road's lanes across its own way through the node (the full
                // road's towards the node, the ramp's and ground road's away from
                // it), so the roads may meet at an angle.
                var fullLanes = LanesAt(full, node, at, -Away(full, node));
                var rampLanes = LanesAt(ramp, node, at, Away(ramp, node));
                var groundLanes = LanesAt(ground, node, at, Away(ground, node));
                var joined = new List<string>();
                int made = 0, wanted = 0;
                foreach (float side in new[] { keep, -keep })
                {
                    // Lanes on this side, inner (nearest the middle) first.
                    var f = Side(fullLanes, side);
                    var r = Side(rampLanes, side);
                    var g = Side(groundLanes, side);
                    bool outward = side == keep;   // traffic from the full road towards the ramp and ground road
                    for (int k = 0; k < f.Count; k++)
                    {
                        bool toRamp = k < r.Count;
                        var other = toRamp ? r : g;
                        int j = toRamp ? k : Mathf.Min(k - r.Count, g.Count - 1);
                        if (j < 0 || j >= other.Count) continue;
                        var from = outward ? f[k] : other[j];
                        var to = outward ? other[j] : f[k];
                        wanted++;
                        bool ok = TmpeAddLaneConnection(manager, from.Lane, to.Lane, from.StartNode);
                        if (ok) made++;
                        joined.Add((outward ? "full " + k : (toRamp ? "ramp " : "ground ") + j) + " -> " + (outward ? (toRamp ? "ramp " : "ground ") + j : "full " + k) +
                                   (ok ? "" : " (not taken)"));
                    }
                }
                return "TM:PE joined " + made + " of " + wanted + " (" + string.Join(", ", joined.ToArray()) + ")";
            }
            catch (Exception e)
            {
                return "not joined (" + e.GetType().Name + ": " + e.Message + ")";
            }
        }

        private struct LaneEnd
        {
            public uint Lane;
            public float Across;      // across its road's way through the node (+ right)
            public bool StartNode;    // the node is its segment's start
        }

        // The way a segment leaves 'node' (flat).
        private static Vector3 Away(ushort segment, ushort node)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            return CurveMath.FlatNormal(s.m_startNode == node ? s.m_startDirection : s.m_endDirection);
        }

        // A segment's car lanes where they end at 'node', across 'way' (+ right of it).
        private static List<LaneEnd> LanesAt(ushort segment, ushort node, Vector3 at, Vector3 way)
        {
            Vector3 right = new Vector3(way.z, 0f, -way.x);
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[segment];
            var result = new List<LaneEnd>();
            var info = s.Info;
            if (info == null || info.m_lanes == null) return result;
            bool start = s.m_startNode == node;
            uint lane = s.m_lanes;
            for (int i = 0; i < info.m_lanes.Length && lane != 0; i++)
            {
                var laneInfo = info.m_lanes[i];
                if ((laneInfo.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None &&
                    (laneInfo.m_vehicleType & VehicleInfo.VehicleType.Car) != VehicleInfo.VehicleType.None)
                {
                    var bezier = net.m_lanes.m_buffer[lane].m_bezier;
                    Vector3 end = start ? bezier.a : bezier.d;
                    result.Add(new LaneEnd { Lane = lane, Across = (end.x - at.x) * right.x + (end.z - at.z) * right.z, StartNode = start });
                }
                lane = net.m_lanes.m_buffer[lane].m_nextLane;
            }
            return result;
        }

        // The lanes on one side (sign of Across), the nearest the middle first.
        private static List<LaneEnd> Side(List<LaneEnd> lanes, float side)
        {
            var result = lanes.FindAll(l => l.Across * side > 0.1f);
            result.Sort((a, b) => Mathf.Abs(a.Across).CompareTo(Mathf.Abs(b.Across)));
            return result;
        }

        private static bool LeftHandTraffic()
        {
            try
            {
                var meta = typeof(SimulationManager).GetField("m_metaData").GetValue(SimulationManager.instance);
                var invert = meta.GetType().GetField("m_invertTraffic").GetValue(meta);
                return invert != null && invert.ToString() == "True";
            }
            catch
            {
                return false;
            }
        }

        private static bool TmpeAddLaneConnection(object manager, uint from, uint to, bool fromStartNode)
        {
            foreach (var m in manager.GetType().GetMethods())
            {
                if (m.Name != "AddLaneConnection") continue;
                var p = m.GetParameters();
                if (p.Length < 3 || p[0].ParameterType != typeof(uint) || p[1].ParameterType != typeof(uint) || p[2].ParameterType != typeof(bool)) continue;
                var args = new object[p.Length];
                args[0] = from;
                args[1] = to;
                args[2] = fromStartNode;
                for (int i = 3; i < p.Length; i++) args[i] = TmpeArgument(p[i]);
                if (!tmpeSignatureLogged)
                {
                    tmpeSignatureLogged = true;
                    var shown = new List<string>();
                    for (int i = 0; i < p.Length; i++) shown.Add(p[i].ParameterType.Name + (i >= 3 ? "=" + args[i] : ""));
                    Mod.Log("TM:PE lane connections made with " + m.Name + "(" + string.Join(", ", shown.ToArray()) + ")");
                }
                try
                {
                    var result = m.Invoke(manager, args);
                    if (result is bool && !(bool)result) return false;
                    // Read back where TM:PE can say: a call that took no group
                    // returns true and joins nothing.
                    bool? there = TmpeConnected(manager, from, to, fromStartNode);
                    return there ?? true;
                }
                catch (Exception e)
                {
                    Mod.Log("TM:PE AddLaneConnection " + from + " -> " + to + " failed: " + e.GetType().Name + ": " + e.Message);
                    return false;
                }
            }
            return false;
        }

        private static bool tmpeSignatureLogged;

        // An argument after (from, to, startNode): its default if it has one; a
        // group of lanes (TM:PE's LaneEndTransitionGroup, a flags enum) is the
        // road (car) group, so the connection is for cars (0 would be no group).
        private static object TmpeArgument(System.Reflection.ParameterInfo p)
        {
            var type = p.ParameterType;
            if (type.IsEnum)
            {
                foreach (string name in new[] { "Road", "Car", "Vehicle", "All" })
                    if (Enum.IsDefined(type, name)) return Enum.Parse(type, name);
                long all = 0;
                foreach (var value in Enum.GetValues(type)) all |= Convert.ToInt64(value);
                return Enum.ToObject(type, all);
            }
            if (p.IsOptional && p.DefaultValue != DBNull.Value) return p.DefaultValue;
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        // Whether TM:PE has the connection (its AreLanesConnected); null if it can't say.
        private static bool? TmpeConnected(object manager, uint from, uint to, bool fromStartNode)
        {
            foreach (var m in manager.GetType().GetMethods())
            {
                if (m.Name != "AreLanesConnected" || m.ReturnType != typeof(bool)) continue;
                var p = m.GetParameters();
                if (p.Length < 3 || p[0].ParameterType != typeof(uint) || p[1].ParameterType != typeof(uint) || p[2].ParameterType != typeof(bool)) continue;
                var args = new object[p.Length];
                args[0] = from;
                args[1] = to;
                args[2] = fromStartNode;
                for (int i = 3; i < p.Length; i++) args[i] = TmpeArgument(p[i]);
                try
                {
                    return (bool)m.Invoke(manager, args);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private const int TmpeAttempts = 6;
        private const int TmpeRetryFrames = 30;
        private static readonly List<KeyValuePair<ushort, int>> tmpeRetries = new List<KeyValuePair<ushort, int>>();
        private static int tmpeWait;

        // Main thread, every frame the tool runs: a median ramp node TM:PE
        // wasn't ready for is set up again after TmpeRetryFrames.
        // A median ramp node's geometry, written to the log a little after it is
        // built (the game has redrawn it and dug the ground by then): the node,
        // each road at it (its piece, its end there, its corners as the game
        // draws them, heights along it) and the ground around it, as heights
        // above or below the node: what the game actually made there.
        private const int DumpAfterFrames = 120;
        private static readonly List<KeyValuePair<ushort, int>> dumps = new List<KeyValuePair<ushort, int>>();

        private static void PollDumps()
        {
            var due = new List<ushort>();
            lock (dumps)
            {
                for (int i = dumps.Count - 1; i >= 0; i--)
                {
                    int left = dumps[i].Value - 1;
                    if (left > 0) dumps[i] = new KeyValuePair<ushort, int>(dumps[i].Key, left);
                    else
                    {
                        due.Add(dumps[i].Key);
                        dumps.RemoveAt(i);
                    }
                }
            }
            foreach (ushort node in due) SimulationManager.instance.AddAction(() => DumpNode(node));
        }

        private static System.Reflection.MethodInfo detailHeight;
        private static bool detailHeightLooked;

        // The ground's height as the game draws it (roads' digging included), if
        // the game has SampleDetailHeight; NaN if not.
        private static float FinalHeight(Vector3 p)
        {
            if (!detailHeightLooked)
            {
                detailHeightLooked = true;
                detailHeight = typeof(TerrainManager).GetMethod("SampleDetailHeight", new[] { typeof(Vector3) });
                if (detailHeight == null) Mod.Log("Node dump: the game has no SampleDetailHeight(Vector3); drawn ground heights not shown");
            }
            if (detailHeight == null) return float.NaN;
            try
            {
                return (float)detailHeight.Invoke(TerrainManager.instance, new object[] { p });
            }
            catch
            {
                return float.NaN;
            }
        }

        private static string V(Vector3 v) => "(" + v.x.ToString("F1") + ", " + v.y.ToString("F2") + ", " + v.z.ToString("F1") + ")";

        // Heights at 'p' against 'level': the raw ground, the ground as drawn.
        private static string Ground(Vector3 p, float level)
        {
            float raw = TerrainManager.instance.SampleRawHeightSmooth(p), drawn = FinalHeight(p);
            return "ground " + (raw - level).ToString("+0.00;-0.00") + (float.IsNaN(drawn) ? "" : " / drawn " + (drawn - level).ToString("+0.00;-0.00"));
        }

        private static void DumpNode(ushort node)
        {
            try
            {
                var net = NetManager.instance;
                var n = net.m_nodes.m_buffer[node];
                if ((n.m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
                {
                    Mod.Log("Node dump #" + node + ": gone");
                    return;
                }
                var lines = new List<string>();
                Vector3 at = n.m_position;
                lines.Add("Node dump #" + node + ": info " + (n.Info != null ? n.Info.name : "?") + ", flags " + n.m_flags + ", elevation " + n.m_elevation +
                          ", at " + V(at) + ", " + Ground(at, at.y) + " (heights below are against the node's)");
                Vector3 axis = Vector3.zero;
                for (int i = 0; i < 8; i++)
                {
                    ushort s = n.GetSegment(i);
                    if (s == 0) continue;
                    var seg = net.m_segments.m_buffer[s];
                    bool start = seg.m_startNode == node;
                    ushort far = start ? seg.m_endNode : seg.m_startNode;
                    Vector3 dir = start ? seg.m_startDirection : seg.m_endDirection;
                    if (axis == Vector3.zero && SplitRoads.IsRamp(seg.Info)) axis = CurveMath.FlatNormal(dir);
                    var farNode = net.m_nodes.m_buffer[far];
                    lines.Add("  segment #" + s + " " + (seg.Info != null ? seg.Info.name : "?") + " (half width " + (seg.Info != null ? seg.Info.m_halfWidth.ToString("F1") : "?") +
                              "), its " + (start ? "start" : "end") + " here, " + seg.m_averageLength.ToString("F1") + " m long, flags " + seg.m_flags +
                              ", way out of the node " + V(dir) + ", square end " + SplitRoads.SquareEnd(s, node, dir));
                    lines.Add("    far node #" + far + " (" + (farNode.Info != null ? farNode.Info.name : "?") + ", flags " + farNode.m_flags + ") at " +
                              V(farNode.m_position) + ", " + Ground(farNode.m_position, farNode.m_position.y));
                    lines.Add("    its piece: " + NodeModels(seg.Info, n.m_flags) + "; " + LaneEnds(s, node));
                    foreach (bool left in new[] { true, false })
                    {
                        Vector3 corner, cornerDir;
                        bool smooth;
                        seg.CalculateCorner(s, true, start, left, out corner, out cornerDir, out smooth);
                        lines.Add("    corner " + (left ? "left " : "right") + " " + V(corner) + " (" + CurveMath.FlatDistance(corner, at).ToString("F1") + " m from the node), way " +
                                  V(cornerDir) + ", " + Ground(corner, corner.y));
                    }
                    Vector3 a, b, c, d;
                    CrossingFinder.SegmentBezier(s, out a, out b, out c, out d);
                    var along = new List<string>();
                    foreach (float metres in new[] { 2f, 5f, 10f, 20f, 40f })
                    {
                        if (metres >= seg.m_averageLength) break;
                        float t = metres / Mathf.Max(seg.m_averageLength, 1f);
                        Vector3 p = CurveMath.Bezier(a, b, c, d, start ? t : 1f - t);
                        along.Add(metres.ToString("F0") + " m: road " + (p.y - at.y).ToString("+0.00;-0.00") + ", " + Ground(p, at.y));
                    }
                    lines.Add("    along it: " + string.Join("; ", along.ToArray()));
                }
                // The ground in a cross through the node: along the ramp's way and across it.
                if (axis != Vector3.zero)
                {
                    Vector3 across = new Vector3(axis.z, 0f, -axis.x);
                    foreach (var line in new[] { new KeyValuePair<string, Vector3>("along the ramp", axis), new KeyValuePair<string, Vector3>("across it", across) })
                    {
                        var samples = new List<string>();
                        for (int k = -8; k <= 8; k++)
                        {
                            Vector3 p = at + line.Value * (k * 5f);
                            float raw = TerrainManager.instance.SampleRawHeightSmooth(p), drawn = FinalHeight(p);
                            samples.Add((k * 5).ToString("+0;-0") + ":" + (float.IsNaN(drawn) ? (raw - at.y).ToString("+0.0;-0.0") : (drawn - at.y).ToString("+0.0;-0.0")));
                        }
                        lines.Add("  ground " + line.Key + " (m: height vs node, " + (float.IsNaN(FinalHeight(at)) ? "raw" : "as drawn") + "): " + string.Join(" ", samples.ToArray()));
                    }
                }
                // One log line each (the log is read by its "[RealisticRoads]" lines).
                foreach (var line in lines) Mod.Log(line);
            }
            catch (Exception e)
            {
                Mod.LogError("Node dump #" + node + " failed", e);
            }
        }

        // A road's junction pieces (node models) and how many of them the
        // node's flags let it draw there; its corner offsets.
        private static string NodeModels(NetInfo info, NetNode.Flags flags)
        {
            if (info == null) return "no info";
            int all = 0, drawn = 0;
            if (info.m_nodes != null)
            {
                var requiredField = typeof(NetInfo.Node).GetField("m_flagsRequired");
                var forbiddenField = typeof(NetInfo.Node).GetField("m_flagsForbidden");
                foreach (var model in info.m_nodes)
                {
                    if (model == null) continue;
                    all++;
                    if (requiredField == null || forbiddenField == null) continue;
                    var required = (NetNode.Flags)requiredField.GetValue(model);
                    var forbidden = (NetNode.Flags)forbiddenField.GetValue(model);
                    if ((flags & (required | forbidden)) == required) drawn++;
                }
            }
            return all + " junction piece(s), " + drawn + " drawn with these flags; corner offset " + info.m_minCornerOffset.ToString("F1") + "/" +
                   info.m_maxCornerOffset.ToString("F1") + ", ends clipped " + info.m_clipSegmentEnds;
        }

        // A segment's car lanes at 'node': how many, and with TM:PE how many
        // have lane connections of their own there.
        private static string LaneEnds(ushort segment, ushort node)
        {
            var net = NetManager.instance;
            var seg = net.m_segments.m_buffer[segment];
            var info = seg.Info;
            if (info == null || info.m_lanes == null) return "no lanes";
            bool start = seg.m_startNode == node;
            int cars = 0, connected = 0;
            bool asked = false;
            object manager = null;
            System.Reflection.MethodInfo has = null;
            TmpeManager("LaneConnectionManager", out manager);
            if (manager != null)
                foreach (var m in manager.GetType().GetMethods())
                {
                    if ((m.Name != "HasOutgoingConnections" && m.Name != "HasConnections") || m.ReturnType != typeof(bool)) continue;
                    var p = m.GetParameters();
                    if (p.Length >= 2 && p[0].ParameterType == typeof(uint) && p[1].ParameterType == typeof(bool)) { has = m; break; }
                }
            uint lane = seg.m_lanes;
            for (int i = 0; i < info.m_lanes.Length && lane != 0; i++)
            {
                var laneInfo = info.m_lanes[i];
                if ((laneInfo.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None &&
                    (laneInfo.m_vehicleType & VehicleInfo.VehicleType.Car) != VehicleInfo.VehicleType.None)
                {
                    cars++;
                    if (has != null)
                    {
                        var p = has.GetParameters();
                        var args = new object[p.Length];
                        args[0] = lane;
                        args[1] = start;
                        for (int k = 2; k < p.Length; k++) args[k] = TmpeArgument(p[k]);
                        try
                        {
                            if ((bool)has.Invoke(manager, args)) connected++;
                            asked = true;
                        }
                        catch { }
                    }
                }
                lane = net.m_lanes.m_buffer[lane].m_nextLane;
            }
            return cars + " car lane(s)" + (asked ? ", " + connected + " with TM:PE lane connections out of here" : "");
        }

        // The Ctrl+Shift+D key in RR's tool: the node aimed at (else the nearest
        // within NearReach of 'p'), and every node stacked with it (a deck's node
        // over a road's), written to the log as the median ramp nodes are.
        private const float NearReach = 20f;
        private const float StackReach = 6f;

        public static void DumpNodesNear(ushort aimed, Vector3 p)
        {
            SimulationManager.instance.AddAction(() =>
            {
                try
                {
                    var nodes = NetManager.instance.m_nodes.m_buffer;
                    ushort best = aimed;
                    if (best == 0 || (nodes[best].m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
                    {
                        best = 0;
                        float nearest = NearReach;
                        for (int i = 1; i < nodes.Length; i++)
                        {
                            if ((nodes[i].m_flags & NetNode.Flags.Created) == NetNode.Flags.None) continue;
                            float d = CurveMath.FlatDistance(nodes[i].m_position, p);
                            if (d < nearest)
                            {
                                nearest = d;
                                best = (ushort)i;
                            }
                        }
                    }
                    if (best == 0)
                    {
                        Mod.Log("Node dump asked at " + V(p) + ": no node within " + NearReach.ToString("F0") + " m");
                        return;
                    }
                    Vector3 at = nodes[best].m_position;
                    var stacked = new List<ushort> { best };
                    for (int i = 1; i < nodes.Length; i++)
                        if (i != best && (nodes[i].m_flags & NetNode.Flags.Created) != NetNode.Flags.None && CurveMath.FlatDistance(nodes[i].m_position, at) <= StackReach)
                            stacked.Add((ushort)i);
                    Mod.Log("Node dump asked at " + V(p) + ": " + stacked.Count + " node(s) there");
                    foreach (ushort node in stacked) DumpNode(node);
                }
                catch (Exception e)
                {
                    Mod.LogError("Node dump asked at " + V(p) + " failed", e);
                }
            });
        }

        public static void PollMedianOrigins()
        {
            PollDumps();
            List<KeyValuePair<ushort, int>> due;
            lock (tmpeRetries)
            {
                if (tmpeRetries.Count == 0)
                {
                    tmpeWait = 0;
                    return;
                }
                if (++tmpeWait < TmpeRetryFrames) return;
                tmpeWait = 0;
                due = new List<KeyValuePair<ushort, int>>(tmpeRetries);
                tmpeRetries.Clear();
            }
            foreach (var retry in due) MedianOrigin(retry.Key, retry.Value);
        }

        // TM:PE's traffic light for 'node' set off (its SetHasTrafficLight).
        private static bool TmpeLightsOff(object manager, ushort node)
        {
            try
            {
                foreach (var m in manager.GetType().GetMethods())
                {
                    if (m.Name != "SetHasTrafficLight") continue;
                    var p = m.GetParameters();
                    if (p.Length < 2 || p[0].ParameterType != typeof(ushort)) continue;
                    if (p[1].ParameterType != typeof(bool) && p[1].ParameterType != typeof(bool?)) continue;
                    var args = new object[p.Length];
                    args[0] = node;
                    args[1] = false;
                    for (int i = 2; i < p.Length; i++) args[i] = p[i].ParameterType.IsValueType ? Activator.CreateInstance(p[i].ParameterType) : null;
                    var result = m.Invoke(manager, args);
                    return !(result is bool) || (bool)result;
                }
            }
            catch (Exception e)
            {
                Mod.Log("TM:PE SetHasTrafficLight on node #" + node + " failed: " + e.GetType().Name + ": " + e.Message);
            }
            return false;
        }

        // One of TM:PE's managers by its property name, if TM:PE is loaded (found
        // by name: RR doesn't need TM:PE). Null with why not.
        private static string TmpeManager(string name, out object manager)
        {
            manager = null;
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name != "TrafficManager") continue;
                    var constants = assembly.GetType("TrafficManager.Constants");
                    if (constants == null) return "TrafficManager.Constants not found";
                    const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                                                               System.Reflection.BindingFlags.Static;
                    var factoryProperty = constants.GetProperty("ManagerFactory", any);
                    var factory = factoryProperty != null ? factoryProperty.GetValue(null, null) : null;
                    if (factory == null) return "its manager factory not found";
                    var managerProperty = factory.GetType().GetProperty(name);
                    manager = managerProperty != null ? managerProperty.GetValue(factory, null) : null;
                    return manager != null ? null : "its " + name + " not found";
                }
                return "not loaded";
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        private static bool TmpeSet(object manager, string method, ushort segment, bool startNode, bool value)
        {
            try
            {
                foreach (var m in manager.GetType().GetMethods())
                {
                    if (m.Name != method) continue;
                    var p = m.GetParameters();
                    if (p.Length != 3 || p[0].ParameterType != typeof(ushort) || p[1].ParameterType != typeof(bool) || p[2].ParameterType != typeof(bool)) continue;
                    var result = m.Invoke(manager, new object[] { segment, startNode, value });
                    return !(result is bool) || (bool)result;
                }
            }
            catch (Exception e)
            {
                Mod.Log("TM:PE " + method + " on #" + segment + " failed: " + e.GetType().Name + ": " + e.Message);
            }
            return false;
        }

        public static void UndoLast()
        {
            SimulationManager.instance.AddAction(UndoNow);
        }

        public static void ClearUndo()
        {
            SimulationManager.instance.AddAction(() =>
            {
                undoStack.Clear();
                UndoCount = 0;
            });
        }

        private static bool BuildNow(NetInfo info, Plan plan, int undoGroup)
        {
            var build = new Build { Group = undoGroup };
            try
            {
                var net = NetManager.instance;
                var sim = SimulationManager.instance;
                var points = plan.Points;
                int last = points.Count - 1;

                // Room for everything first: running out half way through a cut
                // would leave the crossed road broken.
                int needNodes = points.Count + 3 * plan.Crossings.Count + 8;
                int needSegments = points.Count + 4 * plan.Crossings.Count + 8;
                if (net.m_nodes.ItemCount() + needNodes > NetManager.MAX_NODE_COUNT - 64 ||
                    net.m_segments.ItemCount() + needSegments > NetManager.MAX_SEGMENT_COUNT - 64)
                    throw new Exception("the city has no room for this many more road nodes/segments (the game's limit)");

                // Pay for it first, like the vanilla road tool (not in the editors).
                if (!Pay(info, plan, build)) return false;

                var nodeIds = new ushort[points.Count];
                if (NodeExists(plan.StartNode)) nodeIds[0] = plan.StartNode;
                if (NodeExists(plan.EndNode)) nodeIds[last] = plan.EndNode;
                JoinMidRoad(plan, nodeIds, points, build);

                // Existing network first: junctions / level crossings split the
                // existing segment at the crossing; underpasses put a short
                // bridge into it.
                var touched = new HashSet<ushort>();
                for (int c = 0; c < plan.Crossings.Count; c++)
                {
                    var crossing = plan.Crossings[c];
                    bool underpass = crossing.Type == CrossingType.Underpass && crossing.ExistingAtGround;
                    bool overpass = crossing.Type == CrossingType.Overpass && plan.Lowerings.ContainsKey(c);
                    if (!crossing.Connects && !underpass && !overpass) continue;
                    if (!touched.Add(crossing.Segment))
                    {
                        // Junctions on the same segment were all made with the first one.
                        if (crossing.Connects && nodeIds[plan.CrossingIndex[c]] != 0) continue;
                        // The planner refuses other repeats; never build straight through a road.
                        throw new Exception("crossing " + (c + 1) + " crosses a road that is already being changed");
                    }
                    if ((net.m_segments.m_buffer[crossing.Segment].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                        throw new Exception("the road at crossing " + (c + 1) + " no longer exists");

                    if (overpass)
                    {
                        // Lower the existing road: a new node at the crossing (unless
                        // one is right there) dropped into the cutting, and its nodes on
                        // both sides moved down along the ramp.
                        var lowering = plan.Lowerings[c];
                        if (lowering.SplitAt.Length > 0)
                        {
                            var split = SplitSegment(crossing.Segment, lowering.SplitAt, null, build);
                            for (int i = 0; i < split.Nodes.Count; i++)
                            {
                                Vector3 position = net.m_nodes.m_buffer[split.Nodes[i]].m_position;
                                position.y = lowering.SplitHeights[i];
                                net.MoveNode(split.Nodes[i], position);
                                RefreshDirections(split.Nodes[i]);
                            }
                        }
                        for (int i = 0; i < lowering.Nodes.Count; i++)
                        {
                            ushort node = lowering.Nodes[i];
                            if (!NodeExists(node)) continue;
                            Vector3 position = net.m_nodes.m_buffer[node].m_position;
                            build.Moves.Add(new KeyValuePair<ushort, Vector3>(node, position));
                            position.y = lowering.NodeHeights[i];
                            net.MoveNode(node, position);
                            RefreshDirections(node);
                        }
                        continue;
                    }

                    if (crossing.Connects)
                    {
                        // Every junction with this segment (a curve can cross it more
                        // than once) in one cut, in order along the segment.
                        var existingSegment = net.m_segments.m_buffer[crossing.Segment];
                        var group = new List<int>();
                        for (int other = c; other < plan.Crossings.Count; other++)
                            if (plan.Crossings[other].Segment == crossing.Segment && plan.Crossings[other].Connects) group.Add(other);
                        group.Sort((x, y) => plan.Crossings[x].ExistingT.CompareTo(plan.Crossings[y].ExistingT));

                        var cutAt = new List<float>();
                        var cutFor = new List<int>();
                        foreach (int g in group)
                        {
                            // Right next to an existing node: connect there instead of cutting,
                            // and move our point onto it so the road doesn't kink.
                            float fromStart = plan.Crossings[g].ExistingT * existingSegment.m_averageLength;
                            float fromEnd = (1f - plan.Crossings[g].ExistingT) * existingSegment.m_averageLength;
                            int k = plan.CrossingIndex[g];
                            if (fromStart < Crossing.NodeSnap || fromEnd < Crossing.NodeSnap)
                            {
                                nodeIds[k] = fromStart < fromEnd ? existingSegment.m_startNode : existingSegment.m_endNode;
                                points[k] = net.m_nodes.m_buffer[nodeIds[k]].m_position;
                                continue;
                            }
                            if (cutAt.Count > 0 && plan.Crossings[g].ExistingT - cutAt[cutAt.Count - 1] < 0.01f)
                            {
                                nodeIds[k] = ushort.MaxValue;   // same spot as the previous cut: filled in below
                                cutFor.Add(-k - 1);
                                continue;
                            }
                            cutAt.Add(plan.Crossings[g].ExistingT);
                            cutFor.Add(k);
                        }
                        if (cutAt.Count > 0)
                        {
                            var split = SplitSegment(crossing.Segment, cutAt.ToArray(), null, build);
                            int next = 0;
                            ushort previous = 0;
                            foreach (int k in cutFor)
                            {
                                if (k >= 0) { previous = split.Nodes[next++]; nodeIds[k] = previous; }
                                else nodeIds[-k - 1] = previous;
                            }
                        }
                    }
                    else
                    {
                        // Over our cutting and its banks, measured along the road crossed;
                        // at an angle that road's own edge reaches further along the
                        // cutting too (its half width x cot of the angle).
                        BridgeOver(plan, crossing, UnderpassReach(crossing, info), touched, build);
                    }
                }

                // Our nodes.
                for (int i = 0; i <= last; i++)
                {
                    if (nodeIds[i] != 0) continue;
                    NodeKind kind = plan.Kinds[i];
                    NetInfo nodeInfo = RoadPlanner.InfoFor(info, kind);
                    ushort node;
                    if (!net.CreateNode(out node, ref sim.m_randomizer, nodeInfo, points[i], sim.m_currentBuildIndex))
                        throw new Exception("CreateNode failed at point " + i + " " + points[i]);
                    sim.m_currentBuildIndex++;
                    // Height above (or depth below) the ground: the game uses it for pillars
                    // and tunnels, and its upgrade tool picks the version by it (at least
                    // 1 on a raised node, like its own tool). Ground and cutting
                    // nodes keep 0 like vanilla ground roads.
                    if (kind == NodeKind.Elevated || kind == NodeKind.Bridge || kind == NodeKind.Tunnel || kind == NodeKind.Slope)
                    {
                        float elevation = points[i].y - TerrainManager.instance.SampleRawHeightSmooth(points[i]);
                        net.m_nodes.m_buffer[node].m_elevation = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(elevation)), 1, 255);
                    }
                    if (kind == NodeKind.Tunnel) net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.Underground;
                    // Like the vanilla tool: nodes on (or dug into) the ground follow the terrain.
                    // Not on an embankment (above the ground): there the game would snap the
                    // node back onto the terrain. A cutting's node does get it, as the vanilla
                    // tool, Network Multitool and Move It build them: its road digs the
                    // ground down to it, so the node stays on (dug) terrain.
                    bool onTerrain = Mathf.Abs(points[i].y - TerrainManager.instance.SampleRawHeightSmooth(points[i])) < 0.5f;
                    if ((kind == NodeKind.Ground && onTerrain) || kind == NodeKind.Cut) net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.OnGround;
                    if (plan.Middle != null && plan.Middle[i]) net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.Double;
                    nodeIds[i] = node;
                    build.Nodes.Add(node);
                    build.Stamps.Node(node);
                }

                // Our segments: directions follow the curve, with the slope between the two nodes.
                int bridges = 0, elevated = 0, tunnels = 0;
                for (int i = 1; i <= last; i++)
                {
                    float run = CurveMath.FlatDistance(points[i - 1], points[i]);
                    float slope = run > 0.01f ? (points[i].y - points[i - 1].y) / run : 0f;
                    Vector3 startDir = new Vector3(plan.Tangents[i - 1].x, slope, plan.Tangents[i - 1].z);
                    Vector3 endDir = -new Vector3(plan.InTangent(i).x, slope, plan.InTangent(i).z);
                    NetInfo segmentInfo = RoadPlanner.SegmentInfo(info, plan.Kinds[i - 1], plan.Kinds[i]) ?? info;
                    if (segmentInfo != info)
                    {
                        if (plan.Kinds[i - 1] == NodeKind.Bridge || plan.Kinds[i] == NodeKind.Bridge) bridges++;
                        else if (plan.Kinds[i - 1] == NodeKind.Tunnel || plan.Kinds[i] == NodeKind.Tunnel ||
                                 plan.Kinds[i - 1] == NodeKind.Slope || plan.Kinds[i] == NodeKind.Slope) tunnels++;
                        else elevated++;
                    }

                    // A tunnel ramp (slope) runs from its underground end up to the
                    // surface, like the vanilla tool builds it, so the portal faces
                    // the right way; 'invert' keeps the traffic direction.
                    ushort from = nodeIds[i - 1], to = nodeIds[i];
                    if (from == to)
                    {
                        // Two points of the curve snapped to the same existing node.
                        Mod.Log("  Piece " + i + " starts and ends on node #" + from + "; skipped");
                        continue;
                    }
                    bool invert = false;
                    if (plan.Kinds[i] == NodeKind.Tunnel && plan.Kinds[i - 1] != NodeKind.Tunnel)
                    {
                        ushort t = from; from = to; to = t;
                        Vector3 d = startDir; startDir = endDir; endDir = d;
                        invert = true;
                    }

                    ushort segment;
                    if (!net.CreateSegment(out segment, ref sim.m_randomizer, segmentInfo, from, to,
                            startDir, endDir, sim.m_currentBuildIndex, sim.m_currentBuildIndex, invert))
                        throw new Exception("CreateSegment failed for piece " + i);
                    CalculateSegmentDirections(segment);
                    sim.m_currentBuildIndex++;
                    build.Segments.Add(segment);
                    build.Stamps.Segment(segment);
                }

                // Pillars under our raised nodes. The vanilla tool adds them as its own
                // step after building (the game doesn't), so without this the deck floats.
                // None on a node sitting on a road or track we bridge (one the
                // planner couldn't drop): the pillar would stand on it.
                var overCrossing = new HashSet<ushort>();
                for (int c = 0; c < plan.Crossings.Count; c++)
                {
                    var type = plan.Crossings[c].Type;
                    int k = plan.CrossingIndex[c];
                    if ((type == CrossingType.Bridge || type == CrossingType.Overpass) && k >= 0 && k <= last) overCrossing.Add(nodeIds[k]);
                }
                // A deck over a road: its pillar on the road's median if it has
                // one, else a frame across the road (none on the road itself).
                int frames = 0, columns = 0;
                if (plan.OnDeck != null && plan.Decks != null)
                    for (int i = 0; i <= last && i < plan.OnDeck.Length && i < plan.Decks.Length; i++)
                    {
                        var below = plan.Decks[i];
                        bool raised = plan.Kinds[i] == NodeKind.Elevated || plan.Kinds[i] == NodeKind.Bridge;
                        if (!plan.OnDeck[i] || !raised || below.Segment == 0 || !build.Nodes.Contains(nodeIds[i])) continue;
                        if (plan.UnderDeck != null && i < plan.UnderDeck.Length && plan.UnderDeck[i]) continue;
                        var support = Decks.SupportFor(points[i], below);
                        if (support == Decks.Support.Pillar) continue;
                        DeckFrames.Add(nodeIds[i], below, info.m_halfWidth, support == Decks.Support.Column);
                        if (support == Decks.Support.Column) columns++;
                        else frames++;
                    }
                foreach (ushort node in build.Nodes)
                    if (!overCrossing.Contains(node)) UpdatePillar(node);
                if (frames + columns > 0)
                    Mod.Log("  Deck: " + columns + " column(s) on the median, " + frames + " frame(s) across the road below");

                int middles = 0;
                if (plan.Middle != null) foreach (bool m in plan.Middle) if (m) middles++;
                if (middles > 0) Mod.Log("  Suspension bridge: towers on every other node, " + middles + " middle node(s)");
                if (bridges + elevated + tunnels > 0)
                    Mod.Log("  " + bridges + " bridge, " + elevated + " elevated, " + tunnels + " tunnel/slope segment(s)");
                // Nodes below the ground (a cutting dug for them), and how deep:
                // a road that digs a trench shows here.
                int sunk = 0;
                float deepest = 0f;
                var belowAt = new List<string>();
                for (int i = 0; i < plan.Kinds.Length && i < points.Count; i++)
                {
                    if (plan.Kinds[i] != NodeKind.Cut && plan.Kinds[i] != NodeKind.Slope && plan.Kinds[i] != NodeKind.Tunnel) continue;
                    float depth = TerrainManager.instance.SampleRawHeightSmooth(points[i]) - points[i].y;
                    sunk++;
                    deepest = Mathf.Max(deepest, depth);
                    if (belowAt.Count < 6) belowAt.Add("node " + i + " " + plan.Kinds[i] + " " + depth.ToString("F1") + " m down");
                }
                if (sunk > 0)
                    Mod.Log("  " + sunk + " node(s) below the ground, deepest " + deepest.ToString("F1") + " m (" + string.Join(", ", belowAt.ToArray()) + ")");
                for (int c = 0; c < plan.Crossings.Count; c++)
                    Mod.Log("  Crossing " + (c + 1) + " with " + plan.Crossings[c].ExistingName + ": " + plan.Crossings[c].Describe());

                Keep(build);
                Mod.Log("Built " + build.Segments.Count + " segment(s) of " + info.name +
                        " (" + build.Nodes.Count + " new nodes" +
                        (plan.StartNode != 0 ? ", connected at start" : "") +
                        (plan.EndNode != 0 ? ", connected at end" : "") +
                        (build.Splits.Count > 0 ? ", " + build.Splits.Count + " existing segment(s) changed" : "") +
                        "). Ctrl+Z in the tool undoes it.");
                return true;
            }
            catch (Exception e)
            {
                Mod.LogError("Building the curve failed; undoing the part that was built", e);
                Remove(build);
                Refund(build);
                return false;
            }
        }

        private const float MinPiece = 8f;
        private const float MinStartPiece = 20f;   // a start piece no shorter than this (when a junction is close)   // no road piece shorter than this is left between cuts
        private const float MinRailPiece = 30f;   // on rail: as few nodes as possible, no short pieces

        private static float MinPieceOf(NetInfo info) => RoadPlanner.IsRail(info) ? MinRailPiece : MinPiece;

        // Underpass bridge ends: how far a node may be slid along the road to
        // become a bridge end instead of cutting a new one, and how long the
        // segment beyond may get. Rail is long and straight: every node means
        // another set of catenary masts, so it slides further.
        private const float RoadKeepSlack = 4f, RailKeepSlack = 12f;
        private const float RoadMaxSlide = 30f, RailMaxSlide = 80f;
        private const float RoadMaxLength = 120f, RailMaxLength = 160f;

        // One end of the bridge put into the road above an underpass.
        private class BridgeSide
        {
            public CurveMath.BridgeEnd Choice;
            public ushort Node;          // the crossed segment's end node on this side
            public ushort Beyond;        // the segment past that node (0 at a dead end)
            public float Over;           // Continue: metres the bridge reaches into 'Beyond'
            public Vector3 Target;       // SlideIn / SlideOut: where the node goes
            public Vector3 Tangent;      // and the road's direction there
            public float CutT;           // Cut: where on the crossed segment (0..1)
        }

        // How far the bridge put into the road above an underpass reaches
        // either side of the crossing: over our cutting and its banks, measured
        // along the road crossed; at an angle that road's own edge reaches
        // further along the cutting too (its half width x cot of the angle).
        public static float UnderpassReach(Crossing crossing, NetInfo info)
        {
            float skew = Mathf.Clamp(crossing.Skew, 0.2f, 1f);
            float edge = crossing.ExistingHalfWidth * Mathf.Sqrt(1f - skew * skew) / skew;
            return info.m_halfWidth / skew + UnderpassBridgeExtra + edge;
        }

        // The existing road above an underpass becomes a bridge for 'reach'
        // metres either side of the crossing, so it spans our cutting and its
        // banks. Each end of the bridge uses an existing node where it can: the
        // bridge runs on to a node just past its end, or a node nearby is slid
        // along the road to the end. Only if neither works is a new node cut.
        private static void BridgeOver(Plan plan, Crossing crossing, float reach, HashSet<ushort> touched, Build build)
        {
            var net = NetManager.instance;
            ushort segment = crossing.Segment;
            var s = net.m_segments.m_buffer[segment];
            NetInfo existing = s.Info;
            NetInfo bridge = UnderpassBridge(existing);
            float length = Mathf.Max(s.m_averageLength, 1f);
            float at = crossing.ExistingT * length;
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            var start = PlanBridgeSide(plan, segment, s.m_startNode, at, reach, a, b, c, d, length, true, touched);
            var end = PlanBridgeSide(plan, segment, s.m_endNode, length - at, reach, a, b, c, d, length, false, touched);
            // Both slid ends on one road beyond (a short loop): only one of them.
            if (start.Beyond != 0 && start.Beyond == end.Beyond && IsSlide(start.Choice) && IsSlide(end.Choice))
                end.Choice = end.Choice == CurveMath.BridgeEnd.SlideIn ? CurveMath.BridgeEnd.Cut : CurveMath.BridgeEnd.Continue;

            var cuts = new List<float>();
            var infos = new List<NetInfo>();
            if (start.Choice == CurveMath.BridgeEnd.Cut)
            {
                cuts.Add(start.CutT);
                infos.Add(existing);
            }
            infos.Add(bridge);
            if (end.Choice == CurveMath.BridgeEnd.Cut)
            {
                cuts.Add(end.CutT);
                infos.Add(existing);
            }
            var split = SplitSegment(segment, cuts.ToArray(), infos.ToArray(), build);
            FinishBridgeSide(plan, start, touched, build);
            FinishBridgeSide(plan, end, touched, build);
            // The bridge's ends (no new nodes for this): their pillar or none, as the game picks it.
            foreach (ushort node in split.Nodes) UpdatePillar(node);
            if (NodeExists(start.Node)) UpdatePillar(start.Node);
            if (NodeExists(end.Node)) UpdatePillar(end.Node);
            Mod.Log("  Underpass: " + existing.name + " bridged " + (2f * reach).ToString("F0") + " m; start " + Describe(start) +
                    ", end " + Describe(end) + ", " + split.Nodes.Count + " new node(s) on it");
        }

        private static bool IsSlide(CurveMath.BridgeEnd choice) =>
            choice == CurveMath.BridgeEnd.SlideIn || choice == CurveMath.BridgeEnd.SlideOut;

        private static string Describe(BridgeSide side)
        {
            switch (side.Choice)
            {
                case CurveMath.BridgeEnd.Keep: return "runs to node #" + side.Node;
                case CurveMath.BridgeEnd.SlideIn:
                case CurveMath.BridgeEnd.SlideOut: return "node #" + side.Node + " slid to it";
                case CurveMath.BridgeEnd.Continue: return "carries on " + side.Over.ToString("F0") + " m into #" + side.Beyond;
                default: return "new node";
            }
        }

        // Works out one end of the underpass bridge ('atStart': the crossed
        // segment's start side). 'toNode' is how far that side's node is from
        // the crossing; a, b, c, d the crossed segment's curve.
        private static BridgeSide PlanBridgeSide(Plan plan, ushort segment, ushort node, float toNode, float reach,
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, float length, bool atStart, HashSet<ushort> touched)
        {
            var net = NetManager.instance;
            var side = new BridgeSide { Node = node };
            if (SegmentCount(node) == 2)
                for (int i = 0; i < 8; i++)
                {
                    ushort other = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (other != 0 && other != segment) side.Beyond = other;
                }
            NetInfo existing = net.m_segments.m_buffer[segment].Info;
            bool rail = RoadPlanner.IsRail(existing);
            bool hasBeyond = side.Beyond != 0;
            float beyond = hasBeyond ? Mathf.Max(net.m_segments.m_buffer[side.Beyond].m_averageLength, 1f) : 0f;
            bool movable = CanSlide(plan, node, segment, side.Beyond, touched);
            float slide;
            side.Choice = CurveMath.ChooseBridgeEnd(toNode, reach, movable, hasBeyond, beyond,
                rail ? RailKeepSlack : RoadKeepSlack, rail ? RailMaxSlide : RoadMaxSlide, MinPieceOf(existing),
                rail ? RailMaxLength : RoadMaxLength, out slide);
            // A bridge on into a road the curve also crosses, or one already changed, can't be: stop at the node.
            if (side.Choice == CurveMath.BridgeEnd.Continue && (touched.Contains(side.Beyond) || CrossedByPlan(plan, side.Beyond)))
                side.Choice = CurveMath.BridgeEnd.Keep;

            // Where the bridge ends: on the crossed segment (a cut there), or
            // 'Over' metres into the road beyond. Both kept, so a choice changed
            // later (see BridgeOver) still has them.
            float endAlong = atStart ? toNode - reach : length - (toNode - reach);
            side.CutT = Mathf.Clamp01(endAlong / length);
            side.Over = Mathf.Max(reach - toNode, 0f);
            switch (side.Choice)
            {
                case CurveMath.BridgeEnd.SlideIn:
                {
                    float t = Mathf.Clamp01(endAlong / length);
                    side.Target = CurveMath.Bezier(a, b, c, d, t);
                    side.Tangent = CurveMath.BezierDerivative(a, b, c, d, t);
                    break;
                }
                case CurveMath.BridgeEnd.SlideOut:
                {
                    Vector3 ba, bb, bc, bd;
                    CrossingFinder.SegmentBezier(side.Beyond, out ba, out bb, out bc, out bd);
                    bool fromStart = net.m_segments.m_buffer[side.Beyond].m_startNode == node;
                    float t = Mathf.Clamp01(fromStart ? slide / beyond : 1f - slide / beyond);
                    side.Target = CurveMath.Bezier(ba, bb, bc, bd, t);
                    side.Tangent = CurveMath.BezierDerivative(ba, bb, bc, bd, t);
                    break;
                }
            }
            return side;
        }

        // After the crossed segment is cut: slide the node, or carry the bridge on.
        private static void FinishBridgeSide(Plan plan, BridgeSide side, HashSet<ushort> touched, Build build)
        {
            if (IsSlide(side.Choice))
            {
                if (side.Beyond != 0) touched.Add(side.Beyond);
                SlideNode(side.Node, side.Target, side.Tangent, build);
            }
            else if (side.Choice == CurveMath.BridgeEnd.Continue)
            {
                ContinueBridge(side.Node, side.Beyond, side.Over, touched, build);
            }
        }

        // May this existing node be slid along its road? Only a plain node of
        // this one road: no junction, no building (station, pillar), not one
        // the curve uses or another crossing or lowering touches.
        private static bool CanSlide(Plan plan, ushort node, ushort crossed, ushort beyond, HashSet<ushort> touched)
        {
            var net = NetManager.instance;
            if (!NodeExists(node) || SegmentCount(node) > 2) return false;
            var n = net.m_nodes.m_buffer[node];
            if ((n.m_flags & NetNode.Flags.Untouchable) != NetNode.Flags.None || n.m_building != 0) return false;
            if (node == plan.StartNode || node == plan.EndNode) return false;
            if (beyond != 0)
            {
                if (touched.Contains(beyond) || CrossedByPlan(plan, beyond)) return false;
                if ((net.m_segments.m_buffer[beyond].m_flags & NetSegment.Flags.Untouchable) != NetSegment.Flags.None) return false;
            }
            foreach (var other in plan.Crossings)
            {
                if (other.Segment == crossed) continue;
                var s = net.m_segments.m_buffer[other.Segment];
                if (s.m_startNode == node || s.m_endNode == node) return false;
            }
            foreach (var lowering in plan.Lowerings.Values)
                if (lowering.Nodes.Contains(node)) return false;
            return true;
        }

        private static bool CrossedByPlan(Plan plan, ushort segment)
        {
            foreach (var crossing in plan.Crossings) if (crossing.Segment == segment) return true;
            return segment != 0 && (segment == plan.StartSegment || segment == plan.EndSegment);
        }

        // Moves an existing node along its road to 'target' (undo moves it
        // back) and points its roads along 'tangent' there, so they keep
        // following the road's line.
        private static void SlideNode(ushort node, Vector3 target, Vector3 tangent, Build build)
        {
            var net = NetManager.instance;
            build.Moves.Add(new KeyValuePair<ushort, Vector3>(node, net.m_nodes.m_buffer[node].m_position));
            net.MoveNode(node, target);
            tangent = CurveMath.FlatNormal(tangent);
            if (tangent != Vector3.zero)
                for (int i = 0; i < 8; i++)
                {
                    ushort segment = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (segment == 0) continue;
                    var s = net.m_segments.m_buffer[segment];
                    build.Turns.Add(new Turned { Segment = segment, BuildIndex = s.m_buildIndex,
                        StartDirection = s.m_startDirection, EndDirection = s.m_endDirection });
                    bool isStart = s.m_startNode == node;
                    // Keep the way the road leaves the node (its own direction there,
                    // not the chord to its far end, which a curved road can point
                    // the other way round).
                    Vector3 current = CurveMath.FlatNormal(isStart ? s.m_startDirection : s.m_endDirection);
                    if (current == Vector3.zero)
                        current = net.m_nodes.m_buffer[isStart ? s.m_endNode : s.m_startNode].m_position - target;
                    Vector3 away = Vector3.Dot(current, tangent) >= 0f ? tangent : -tangent;
                    if (isStart) net.m_segments.m_buffer[segment].m_startDirection = away;
                    else net.m_segments.m_buffer[segment].m_endDirection = away;
                }
            RefreshDirections(node);
        }

        private static NetInfo UnderpassBridge(NetInfo existing)
        {
            // Elevated piece (not the bridge one): no trusses, and no holes cut
            // in the ground beside its ends.
            NetInfo bridge = RoadPlanner.Variant(existing, "m_elevatedInfo") ?? RoadPlanner.Variant(existing, "m_bridgeInfo");
            if (bridge == null) throw new Exception(existing.name + " has no bridge version for the underpass");
            return bridge;
        }

        // The underpass bridge reaching 'metres' past 'node' into 'next', the
        // road carrying on there. 'next' is found before the crossed segment is
        // cut: afterwards the node also holds the new bridge piece, which must
        // not be mistaken for the road beyond (that cut the bridge again).
        private static void ContinueBridge(ushort node, ushort next, float metres, HashSet<ushort> touched, Build build)
        {
            var net = NetManager.instance;
            if (!NodeExists(node) || next == 0 || touched.Contains(next)) return;
            if ((net.m_segments.m_buffer[next].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) return;
            touched.Add(next);

            var s = net.m_segments.m_buffer[next];
            NetInfo existing = s.Info;
            // The road on beyond has no raised piece: the underpass deck would stop
            // short over the cutting, so the build fails (and is put back) instead.
            NetInfo bridge = UnderpassBridge(existing);
            float length = Mathf.Max(s.m_averageLength, 1f);
            if (metres > length - MinPieceOf(existing))
            {
                SplitSegment(next, new float[0], new[] { bridge }, build);
                return;
            }
            bool fromStart = s.m_startNode == node;
            float t = fromStart ? metres / length : 1f - metres / length;
            var split = SplitSegment(next, new[] { t }, fromStart ? new[] { bridge, existing } : new[] { existing, bridge }, build);
            foreach (ushort end in split.Nodes) UpdatePillar(end);
        }

        // What the vanilla tool would charge for the new segments (the road's
        // own price per piece, by length and height). Changes to the roads we
        // cross aren't charged. Any thread.
        public static int Cost(NetInfo info, Plan plan)
        {
            long total = 0;
            var terrain = TerrainManager.instance;
            for (int i = 1; i < plan.Points.Count; i++)
            {
                NetInfo piece = RoadPlanner.SegmentInfo(info, plan.Kinds[i - 1], plan.Kinds[i]) ?? info;
                if (piece.m_netAI == null) continue;
                Vector3 a = plan.Points[i - 1], b = plan.Points[i];
                float ha = a.y - terrain.SampleRawHeightSmooth(a), hb = b.y - terrain.SampleRawHeightSmooth(b);
                total += PieceCost(piece, info, a, b, ha, hb);
            }
            return (int)Math.Min(total, int.MaxValue);
        }

        private static readonly HashSet<string> costFailed = new HashSet<string>();

        // A road whose AI can't price itself (a mod's road not fully set up)
        // is priced as the ground road, or free; logged once per road, not
        // every preview frame.
        internal static long PieceCost(NetInfo piece, NetInfo ground, Vector3 a, Vector3 b, float ha, float hb)
        {
            foreach (var candidate in new[] { piece, ground })
            {
                var ai = candidate.m_netAI;
                if (ai == null) continue;
                bool failed;
                lock (costFailed) failed = costFailed.Contains(candidate.name);
                if (failed) continue;
                if (ai.m_info == null) ai.m_info = candidate;
                try
                {
                    return ai.GetConstructionCost(a, b, ha, hb);
                }
                catch (Exception e)
                {
                    bool first;
                    lock (costFailed) first = costFailed.Add(candidate.name);
                    if (first) Mod.LogError("Can't get the price of " + candidate.name + "; it is priced as the ground road, or free", e);
                }
            }
            return 0;
        }

        // Enum values looked up by name: compiled-in numbers could be wrong.
        private static EconomyManager.Resource ResourceNamed(string name) =>
            (EconomyManager.Resource)Enum.Parse(typeof(EconomyManager.Resource), name);

        private static bool InGame()
        {
            try
            {
                var game = (ItemClass.Availability)Enum.Parse(typeof(ItemClass.Availability), "Game");
                return (ToolManager.instance.m_properties.m_mode & game) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool Pay(NetInfo info, Plan plan, Build build)
        {
            if (!InGame()) return true;
            int cost = Cost(info, plan);
            if (cost <= 0) return true;
            var economy = EconomyManager.instance;
            var construction = ResourceNamed("Construction");
            if (economy.PeekResource(construction, cost) != cost)
            {
                Mod.Log("Not built: it costs " + (cost / 100) + " and the city can't afford it");
                return false;
            }
            economy.FetchResource(construction, cost, info.m_class);
            build.Paid = cost;
            build.PaidClass = info.m_class;
            return true;
        }

        // Undo (or a failed build) gives the money back, like bulldozing a road
        // the vanilla tool has just built.
        private static void Refund(Build build)
        {
            if (build.Paid <= 0) return;
            try
            {
                EconomyManager.instance.AddResource(ResourceNamed("RefundAmount"), build.Paid, build.PaidClass);
            }
            catch (Exception e)
            {
                Mod.LogError("Refunding the road failed", e);
            }
            build.Paid = 0;
        }

        // The node's pillar (or none), as the road's AI chooses it: by height,
        // over water, and so on.
        private static void UpdatePillar(ushort node)
        {
            try
            {
                var nodes = NetManager.instance.m_nodes.m_buffer;
                var info = nodes[node].Info;
                if (info == null || info.m_netAI == null) return;
                BuildingInfo building;
                float heightOffset;
                info.m_netAI.GetNodeBuilding(node, ref nodes[node], out building, out heightOffset);
                nodes[node].UpdateBuilding(node, building, heightOffset);
            }
            catch (Exception e)
            {
                Mod.LogError("Placing the pillar at node #" + node + " failed", e);
            }
        }

        // Ends placed on the middle of a road: cut that road there and use the
        // new node as the end (a T-junction). Both ends on the same road: one cut
        // with two nodes.
        private static void JoinMidRoad(Plan plan, ushort[] nodeIds, List<Vector3> points, Build build)
        {
            int last = points.Count - 1;
            var net = NetManager.instance;
            bool start = plan.StartSegment != 0 && nodeIds[0] == 0;
            bool end = plan.EndSegment != 0 && nodeIds[last] == 0;
            foreach (ushort segment in new[] { start ? plan.StartSegment : (ushort)0, end ? plan.EndSegment : (ushort)0 })
                if (segment != 0 && (net.m_segments.m_buffer[segment].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                    throw new Exception("the road the curve starts or ends on no longer exists");

            if (start && end && plan.StartSegment == plan.EndSegment)
            {
                bool startFirst = plan.StartT <= plan.EndT;
                var split = SplitSegment(plan.StartSegment, startFirst ? new[] { plan.StartT, plan.EndT } : new[] { plan.EndT, plan.StartT }, null, build);
                nodeIds[0] = split.Nodes[startFirst ? 0 : 1];
                nodeIds[last] = split.Nodes[startFirst ? 1 : 0];
            }
            else
            {
                if (start) nodeIds[0] = SplitSegment(plan.StartSegment, new[] { plan.StartT }, null, build).Nodes[0];
                if (end) nodeIds[last] = SplitSegment(plan.EndSegment, new[] { plan.EndT }, null, build).Nodes[0];
            }
            if (start) points[0] = net.m_nodes.m_buffer[nodeIds[0]].m_position;
            if (end) points[last] = net.m_nodes.m_buffer[nodeIds[last]].m_position;
        }

        private static void UndoNow()
        {
            if (undoStack.Count == 0)
            {
                Mod.Log("Nothing to undo");
                return;
            }
            // A build and the ones made with it (a parallel pair), newest first.
            int group;
            do
            {
                var build = undoStack[undoStack.Count - 1];
                undoStack.RemoveAt(undoStack.Count - 1);
                UndoCount = undoStack.Count;
                Remove(build);
                Refund(build);
                Mod.Log("Undo: removed " + build.Segments.Count + " segment(s)" +
                        (build.Splits.Count > 0 ? " and restored " + build.Splits.Count + " existing segment(s)" : ""));
                group = build.Group;
            } while (group != 0 && undoStack.Count > 0 && undoStack[undoStack.Count - 1].Group == group);
        }

        // Simulation thread. Removes our segments, then any node we created
        // that nothing else has connected to since, then restores existing
        // segments we cut.
        private static void Remove(Build build)
        {
            var net = NetManager.instance;
            foreach (var segment in build.Segments)
                if (build.Stamps.OwnsSegment(segment)) net.ReleaseSegment(segment, true);
            foreach (var node in build.Nodes)
                if (build.Stamps.OwnsNode(node) && SegmentCount(node) == 0) net.ReleaseNode(node);
            DeckFrames.Forget(build.Nodes);

            // Existing nodes we lowered go back up.
            for (int i = build.Moves.Count - 1; i >= 0; i--)
            {
                if (!NodeExists(build.Moves[i].Key)) continue;
                net.MoveNode(build.Moves[i].Key, build.Moves[i].Value);
                RefreshDirections(build.Moves[i].Key);
            }
            // And the roads at a slid node point the way they did.
            for (int i = build.Turns.Count - 1; i >= 0; i--)
            {
                var turn = build.Turns[i];
                var s = net.m_segments.m_buffer[turn.Segment];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.m_buildIndex != turn.BuildIndex) continue;
                net.m_segments.m_buffer[turn.Segment].m_startDirection = turn.StartDirection;
                net.m_segments.m_buffer[turn.Segment].m_endDirection = turn.EndDirection;
                net.UpdateSegment(turn.Segment);
            }

            // Nodes an upgrade gave the new road go back to the old one.
            for (int i = build.NodeInfos.Count - 1; i >= 0; i--)
                if (NodeExists(build.NodeInfos[i].Key)) SetNodeInfo(build.NodeInfos[i].Key, build.NodeInfos[i].Value);

            for (int i = build.Splits.Count - 1; i >= 0; i--)
            {
                var split = build.Splits[i];
                // Only put the original back if its pieces are still the ones we
                // made: if the player bulldozed or rebuilt any of them, leave it.
                bool intact = true;
                foreach (var segment in split.Segments) if (!split.Stamps.OwnsSegment(segment)) intact = false;
                if (!intact && split.Released)
                {
                    Mod.Log("  Left a " + split.Info.name + " as it is: it was changed after this curve was built");
                    continue;
                }
                foreach (var segment in split.Segments)
                    if (split.Stamps.OwnsSegment(segment)) net.ReleaseSegment(segment, true);
                foreach (var node in split.Nodes)
                    if (split.Stamps.OwnsNode(node) && SegmentCount(node) == 0) net.ReleaseNode(node);
                if (!split.Released) continue;   // the original was never removed
                if (!NodeExists(split.StartNode) || !NodeExists(split.EndNode))
                {
                    Mod.Log("  Could not restore a " + split.Info.name + " segment: its end was removed");
                    continue;
                }
                var sim = SimulationManager.instance;
                ushort restored;
                if (net.CreateSegment(out restored, ref sim.m_randomizer, split.Info, split.StartNode, split.EndNode,
                        split.StartDirection, split.EndDirection, sim.m_currentBuildIndex, sim.m_currentBuildIndex, split.Invert))
                {
                    sim.m_currentBuildIndex++;
                    KeepName(restored, split, split.Yield);
                    // The restored road has a new id: older builds still on the undo
                    // stack may refer to it by the old one (e.g. they had cut it).
                    Remap(split.Original, restored);
                }
            }
        }

        private static void Remap(ushort oldSegment, ushort newSegment)
        {
            foreach (var build in undoStack) Remap(build, oldSegment, newSegment);
        }

        private static void Remap(Build build, ushort oldSegment, ushort newSegment)
        {
            for (int i = 0; i < build.Segments.Count; i++)
                if (build.Segments[i] == oldSegment) build.Segments[i] = newSegment;
            build.Stamps.Replace(oldSegment, newSegment);
            foreach (var split in build.Splits)
            {
                for (int i = 0; i < split.Segments.Count; i++)
                    if (split.Segments[i] == oldSegment) split.Segments[i] = newSegment;
                split.Stamps.Replace(oldSegment, newSegment);
                if (split.Original == oldSegment) split.Original = newSegment;
            }
        }

        // Simulation thread. Cuts an existing segment at the given positions
        // along it (0..1, increasing): new nodes there, and pieces between
        // them following the original curve. pieceInfos (one per piece) lets a
        // piece become e.g. a bridge; null keeps the original type.
        // Safe order: the new nodes are made first (a failure there leaves the
        // road untouched), and the split is in 'build' before the original is
        // removed, so a failure after that is undone by rebuilding it.
        private static Split SplitSegment(ushort segment, float[] at, NetInfo[] pieceInfos, Build build, bool? invert = null)
        {
            var net = NetManager.instance;
            var sim = SimulationManager.instance;
            var s = net.m_segments.m_buffer[segment];
            var split = new Split
            {
                Original = segment,
                Info = s.Info,
                StartNode = s.m_startNode,
                EndNode = s.m_endNode,
                StartDirection = s.m_startDirection,
                EndDirection = s.m_endDirection,
                Invert = (s.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None,
                Yield = s.m_flags & (NetSegment.Flags.YieldStart | NetSegment.Flags.YieldEnd),
                NameSeed = s.m_nameSeed,
                CustomName = CustomNameOf(segment),
            };

            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            var nodes = net.m_nodes.m_buffer;
            bool onGround = (nodes[split.StartNode].m_flags & nodes[split.EndNode].m_flags & NetNode.Flags.OnGround) != NetNode.Flags.None;
            byte startElevation = nodes[split.StartNode].m_elevation, endElevation = nodes[split.EndNode].m_elevation;

            var chain = new List<ushort> { split.StartNode };
            var directions = new List<Vector3> { split.StartDirection };
            foreach (float t in at)
            {
                Vector3 position = CurveMath.Bezier(a, b, c, d, t);
                Vector3 derivative = CurveMath.BezierDerivative(a, b, c, d, t);
                float flat = Mathf.Max(Mathf.Sqrt(derivative.x * derivative.x + derivative.z * derivative.z), 0.0001f);
                ushort node;
                if (!net.CreateNode(out node, ref sim.m_randomizer, split.Info, position, sim.m_currentBuildIndex))
                {
                    foreach (var made in split.Nodes) net.ReleaseNode(made);
                    throw new Exception("could not add a node to " + split.Info.name + " (it is left as it was)");
                }
                sim.m_currentBuildIndex++;
                // A cut in a road on the ground stays on the ground; one in a raised
                // road (or a tunnel) keeps its height over the ground, as the game's
                // own nodes there have it (its upgrade tool reads it).
                if (onGround) net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.OnGround;
                if (startElevation != 0 || endElevation != 0)
                    net.m_nodes.m_buffer[node].m_elevation = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(startElevation, endElevation, t)), 1, 255);
                if ((nodes[split.StartNode].m_flags & nodes[split.EndNode].m_flags & NetNode.Flags.Underground) != NetNode.Flags.None)
                    net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.Underground;
                split.Nodes.Add(node);
                split.Stamps.Node(node);
                chain.Add(node);
                directions.Add(new Vector3(derivative.x / flat, 0f, derivative.z / flat));
            }
            chain.Add(split.EndNode);

            build.Splits.Add(split);
            net.ReleaseSegment(segment, true);
            split.Released = true;

            for (int i = 1; i < chain.Count; i++)
            {
                Vector3 startDir = i == 1 ? split.StartDirection : directions[i - 1];
                Vector3 endDir = i == chain.Count - 1 ? split.EndDirection : -directions[i];
                NetInfo pieceInfo = pieceInfos != null ? pieceInfos[i - 1] : split.Info;
                ushort piece;
                if (!net.CreateSegment(out piece, ref sim.m_randomizer, pieceInfo, chain[i - 1], chain[i],
                        startDir, endDir, sim.m_currentBuildIndex, sim.m_currentBuildIndex, invert ?? split.Invert))
                    throw new Exception("could not rebuild " + split.Info.name);
                sim.m_currentBuildIndex++;
                CalculateSegmentDirections(piece);
                // The pieces keep the road's name, and its yield signs at its own ends.
                var yield = (i == 1 ? split.Yield & NetSegment.Flags.YieldStart : NetSegment.Flags.None) |
                            (i == chain.Count - 1 ? split.Yield & NetSegment.Flags.YieldEnd : NetSegment.Flags.None);
                KeepName(piece, split, yield, false);
                split.Segments.Add(piece);
                split.Stamps.Segment(piece);
            }
            // Named once all the pieces are there and joined.
            if (split.CustomName != null) foreach (ushort piece in split.Segments) KeepCustomName(piece, split.CustomName);
            return split;
        }

        // A segment's name and yield signs, as 'split''s road had them.
        private static void KeepName(ushort segment, Split split, NetSegment.Flags yield, bool rename = true)
        {
            var net = NetManager.instance;
            net.m_segments.m_buffer[segment].m_nameSeed = split.NameSeed;
            if (yield != NetSegment.Flags.None)
            {
                net.m_segments.m_buffer[segment].m_flags |= yield;
                net.UpdateSegment(segment);
            }
            if (rename && split.CustomName != null) KeepCustomName(segment, split.CustomName);
        }

        private static void KeepCustomName(ushort segment, string name)
        {
            try
            {
                Rename(segment, name);
            }
            catch (Exception e)
            {
                Mod.LogError("Keeping the name '" + name + "' failed", e);
            }
        }

        // The name the player gave a segment (null: none). The game's calls are
        // kept in methods of their own, so a game version without them only
        // loses the names (the failure comes when such a method is called).
        private static string CustomNameOf(ushort segment)
        {
            try
            {
                return ReadName(segment);
            }
            catch (Exception e)
            {
                Mod.LogError("Reading a street name failed", e);
                return null;
            }
        }

        private static string ReadName(ushort segment)
        {
            var net = NetManager.instance;
            if ((net.m_segments.m_buffer[segment].m_flags & NetSegment.Flags.CustomName) == NetSegment.Flags.None) return null;
            return net.GetSegmentName(segment);
        }

        // Simulation thread (where the game's own renaming runs).
        private static void Rename(ushort segment, string name)
        {
            var steps = NetManager.instance.SetSegmentName(segment, name);
            while (steps.MoveNext()) { }
        }

        public static bool NodeExists(ushort node)
        {
            return node != 0 &&
                   (NetManager.instance.m_nodes.m_buffer[node].m_flags & NetNode.Flags.Created) != NetNode.Flags.None;
        }

        public static int SegmentCount(ushort node)
        {
            int count = 0;
            for (int i = 0; i < 8; i++)
                if (NetManager.instance.m_nodes.m_buffer[node].GetSegment(i) != 0) count++;
            return count;
        }

        // The direction to leave (atStart) or arrive at 'node' in so the new
        // road continues the existing road there that best matches 'heading'
        // (the way the new road is going). Zero when no road there is within
        // ContinueAngle: the new road then meets the node at its own angle.
        public static Vector3 ContinuationDirection(ushort node, Vector3 heading, bool atStart)
        {
            if (!NodeExists(node)) return Vector3.zero;
            heading = CurveMath.FlatNormal(heading);
            if (heading == Vector3.zero) return Vector3.zero;

            var nodes = NetManager.instance.m_nodes.m_buffer;
            var segments = NetManager.instance.m_segments.m_buffer;
            float best = Mathf.Cos(ContinueAngle * Mathf.Deg2Rad);
            Vector3 result = Vector3.zero;
            for (int i = 0; i < 8; i++)
            {
                ushort s = nodes[node].GetSegment(i);
                if (s == 0) continue;
                Vector3 away = CurveMath.FlatNormal(segments[s].m_startNode == node ? segments[s].m_startDirection : segments[s].m_endDirection);
                // Leaving a node we head opposite to the road we continue;
                // arriving we head into the road we continue.
                Vector3 candidate = atStart ? -away : away;
                float dot = Vector3.Dot(candidate, heading);
                if (dot > best && IsFree(node, atStart ? candidate : -candidate))
                {
                    best = dot;
                    result = candidate;
                }
            }
            return result;
        }

        // True if no road at 'node' already leaves it within 30 degrees of
        // 'direction' (so a new road leaving that way won't lie on top of it).
        private static bool IsFree(ushort node, Vector3 direction)
        {
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var segments = NetManager.instance.m_segments.m_buffer;
            float limit = Mathf.Cos(30f * Mathf.Deg2Rad);
            for (int i = 0; i < 8; i++)
            {
                ushort s = nodes[node].GetSegment(i);
                if (s == 0) continue;
                Vector3 away = CurveMath.FlatNormal(segments[s].m_startNode == node ? segments[s].m_startDirection : segments[s].m_endDirection);
                if (Vector3.Dot(away, direction) > limit) return false;
            }
            return true;
        }

        // Flat directions, then the game works out the slope between the two
        // nodes itself (as Network Multitool and Move It do).
        private static void CalculateSegmentDirections(ushort segment)
        {
            var segments = NetManager.instance.m_segments.m_buffer;
            segments[segment].m_startDirection = CurveMath.FlatNormal(segments[segment].m_startDirection);
            segments[segment].m_endDirection = CurveMath.FlatNormal(segments[segment].m_endDirection);
            segments[segment].m_startDirection = segments[segment].FindDirection(segment, segments[segment].m_startNode);
            segments[segment].m_endDirection = segments[segment].FindDirection(segment, segments[segment].m_endNode);
        }

        // After a node moved up or down: fix the slope of its roads and redraw them.
        private static void RefreshDirections(ushort node)
        {
            var net = NetManager.instance;
            Vector3 min = net.m_nodes.m_buffer[node].m_position, max = min;
            for (int i = 0; i < 8; i++)
            {
                ushort segment = net.m_nodes.m_buffer[node].GetSegment(i);
                if (segment == 0) continue;
                CalculateSegmentDirections(segment);
                net.UpdateSegment(segment);
                var s = net.m_segments.m_buffer[segment];
                Vector3 other = net.m_nodes.m_buffer[s.m_startNode == node ? s.m_endNode : s.m_startNode].m_position;
                min = Vector3.Min(min, other);
                max = Vector3.Max(max, other);
            }
            // Re-dig / re-fill the terrain under the moved roads (as Move It does).
            const float margin = 32f;
            TerrainModify.UpdateArea(min.x - margin, min.z - margin, max.x + margin, max.z + margin, true, true, false);
        }

        private static Vector3 DirectionAway(ushort segment, ushort node)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            return s.m_startNode == node ? s.m_startDirection : s.m_endDirection;
        }

        // Is the ground under this point covered by water?
        public static bool IsOverWater(Vector3 point)
        {
            var terrain = TerrainManager.instance;
            return terrain.SampleRawHeightSmoothWithWater(point, false, 0f) - terrain.SampleRawHeightSmooth(point) > 0.5f;
        }
    }
}
