using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace RealisticRoads
{
    // Creates and removes roads. All game changes happen on the simulation
    // thread; the public methods only queue the work.
    public static class NetworkBuilder
    {
        private const int MaxUndo = 20;
        private const float ContinueAngle = 55f;     // continue an existing road if within this angle
        private const float UnderpassBridgeExtra = 6f; // existing bridge reaches this far past our road's edges

        // An existing segment we cut into pieces, so undo can put it back.
        private class Split
        {
            public ushort Original;                 // the segment that was cut (id reused for the restored one)
            public NetInfo Info;
            public ushort StartNode, EndNode;
            public Vector3 StartDirection, EndDirection;
            public bool Invert;
            public readonly List<ushort> Segments = new List<ushort>();
            public readonly List<ushort> Nodes = new List<ushort>();
        }

        // Simulation thread: nodes at both ends of every segment of the last build.
        private static readonly HashSet<ushort> lastBuiltEnds = new HashSet<ushort>();

        // What one build created or changed, so one undo reverses all of it.
        private class Build
        {
            public readonly List<ushort> Segments = new List<ushort>();
            public readonly List<ushort> Nodes = new List<ushort>();   // nodes we created (not snapped ones)
            public readonly List<Split> Splits = new List<Split>();
            // Existing nodes we moved (overpass lowering) and where they were.
            public readonly List<KeyValuePair<ushort, Vector3>> Moves = new List<KeyValuePair<ushort, Vector3>>();
        }

        // Only touched on the simulation thread.
        private static readonly List<Build> undoStack = new List<Build>();

        // Builds 'plan' (see RoadPlanner) with 'info'. splitNodes: half-snapped
        // trunk ends; if one becomes a split it is smoothed automatically.
        public static void BuildCurve(NetInfo info, Plan plan, params ushort[] splitNodes)
        {
            SimulationManager.instance.AddAction(() =>
            {
                if (!BuildNow(info, plan)) return;
                foreach (var node in splitNodes)
                    if (node != 0) SmoothSplits.EnableIfSplit(node);
                // Junctions the new road makes get the CS2 look (smooth splits excepted).
                foreach (var node in lastBuiltEnds) Junctions.EnableIfJunction(node);
            });
        }

        public static void UndoLast()
        {
            SimulationManager.instance.AddAction(UndoNow);
        }

        public static void ClearUndo()
        {
            SimulationManager.instance.AddAction(() => undoStack.Clear());
        }

        private static bool BuildNow(NetInfo info, Plan plan)
        {
            var build = new Build();
            try
            {
                var net = NetManager.instance;
                var sim = SimulationManager.instance;
                var points = plan.Points;
                int last = points.Count - 1;

                var nodeIds = new ushort[points.Count];
                if (NodeExists(plan.StartNode)) nodeIds[0] = plan.StartNode;
                if (NodeExists(plan.EndNode)) nodeIds[last] = plan.EndNode;

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
                        // Lower the existing road: three new nodes around the crossing, all
                        // dropped into the cutting, and its nodes on both sides moved down
                        // along the ramp.
                        var lowering = plan.Lowerings[c];
                        var split = SplitSegment(crossing.Segment, lowering.SplitAt, null);
                        build.Splits.Add(split);
                        for (int i = 0; i < split.Nodes.Count; i++)
                        {
                            Vector3 position = net.m_nodes.m_buffer[split.Nodes[i]].m_position;
                            position.y = lowering.SplitHeights[i];
                            net.MoveNode(split.Nodes[i], position);
                            RefreshDirections(split.Nodes[i]);
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
                            var split = SplitSegment(crossing.Segment, cutAt.ToArray(), null);
                            build.Splits.Add(split);
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
                        NetInfo existing = net.m_segments.m_buffer[crossing.Segment].Info;
                        // Elevated piece (not the bridge one): no trusses, and no holes cut
                        // in the ground beside its ends.
                        NetInfo bridge = RoadPlanner.Variant(existing, "m_elevatedInfo") ?? RoadPlanner.Variant(existing, "m_bridgeInfo");
                        if (bridge == null) throw new Exception(existing.name + " has no bridge version for the underpass");
                        float length = Mathf.Max(net.m_segments.m_buffer[crossing.Segment].m_averageLength, 1f);
                        float half = (info.m_halfWidth / crossing.Skew + UnderpassBridgeExtra) / length;
                        float t0 = Mathf.Max(crossing.ExistingT - half, 0.05f);
                        float t1 = Mathf.Min(crossing.ExistingT + half, 0.95f);
                        build.Splits.Add(SplitSegment(crossing.Segment, new[] { t0, t1 }, new[] { existing, bridge, existing }));
                    }
                }

                // Our nodes.
                for (int i = 0; i <= last; i++)
                {
                    if (nodeIds[i] != 0) continue;
                    NodeKind kind = plan.Kinds[i];
                    ushort node;
                    if (!net.CreateNode(out node, ref sim.m_randomizer, RoadPlanner.InfoFor(info, kind), points[i], sim.m_currentBuildIndex))
                        throw new Exception("CreateNode failed at point " + i + " " + points[i]);
                    sim.m_currentBuildIndex++;
                    // Height above (or depth below) the ground: the game uses it for pillars
                    // and tunnels. Ground and cutting nodes keep 0 like vanilla ground roads.
                    if (kind == NodeKind.Elevated || kind == NodeKind.Bridge || kind == NodeKind.Tunnel || kind == NodeKind.Slope)
                    {
                        float elevation = points[i].y - TerrainManager.instance.SampleRawHeightSmooth(points[i]);
                        net.m_nodes.m_buffer[node].m_elevation = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(elevation)), 0, 255);
                    }
                    if (kind == NodeKind.Tunnel) net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.Underground;
                    // Like the vanilla tool: nodes on (or dug into) the ground follow the terrain.
                    // Not on an embankment or in a shallow cut: there the game would snap the
                    // node back onto the terrain, while the road itself reshapes the ground.
                    bool onTerrain = Mathf.Abs(points[i].y - TerrainManager.instance.SampleRawHeightSmooth(points[i])) < 0.5f;
                    if ((kind == NodeKind.Ground && onTerrain) || kind == NodeKind.Cut) net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.OnGround;
                    if (plan.Middle != null && plan.Middle[i]) net.m_nodes.m_buffer[node].m_flags |= NetNode.Flags.Double;
                    nodeIds[i] = node;
                    build.Nodes.Add(node);
                }

                // Our segments: directions follow the curve, with the slope between the two nodes.
                int bridges = 0, elevated = 0, tunnels = 0;
                for (int i = 1; i <= last; i++)
                {
                    float run = CurveMath.FlatDistance(points[i - 1], points[i]);
                    float slope = run > 0.01f ? (points[i].y - points[i - 1].y) / run : 0f;
                    Vector3 startDir = new Vector3(plan.Tangents[i - 1].x, slope, plan.Tangents[i - 1].z);
                    Vector3 endDir = -new Vector3(plan.Tangents[i].x, slope, plan.Tangents[i].z);
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
                }

                int middles = 0;
                if (plan.Middle != null) foreach (bool m in plan.Middle) if (m) middles++;
                if (middles > 0) Mod.Log("  Suspension bridge: towers on every other node, " + middles + " middle node(s)");
                if (bridges + elevated + tunnels > 0)
                    Mod.Log("  " + bridges + " bridge, " + elevated + " elevated, " + tunnels + " tunnel/slope segment(s)");
                for (int c = 0; c < plan.Crossings.Count; c++)
                    Mod.Log("  Crossing " + (c + 1) + " with " + plan.Crossings[c].ExistingName + ": " + plan.Crossings[c].Describe());

                lastBuiltEnds.Clear();
                foreach (ushort segment in build.Segments)
                {
                    lastBuiltEnds.Add(net.m_segments.m_buffer[segment].m_startNode);
                    lastBuiltEnds.Add(net.m_segments.m_buffer[segment].m_endNode);
                }
                undoStack.Add(build);
                if (undoStack.Count > MaxUndo) undoStack.RemoveAt(0);
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
                return false;
            }
        }

        private static void UndoNow()
        {
            if (undoStack.Count == 0)
            {
                Mod.Log("Nothing to undo");
                return;
            }
            var build = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            Remove(build);
            Mod.Log("Undo: removed " + build.Segments.Count + " segment(s)" +
                    (build.Splits.Count > 0 ? " and restored " + build.Splits.Count + " existing segment(s)" : ""));
        }

        // Simulation thread. Removes our segments, then any node we created
        // that nothing else has connected to since, then restores existing
        // segments we cut.
        private static void Remove(Build build)
        {
            var net = NetManager.instance;
            foreach (var segment in build.Segments) ReleaseSegmentIfExists(segment);
            foreach (var node in build.Nodes)
                if (NodeExists(node) && SegmentCount(node) == 0) net.ReleaseNode(node);

            // Existing nodes we lowered go back up.
            for (int i = build.Moves.Count - 1; i >= 0; i--)
            {
                if (!NodeExists(build.Moves[i].Key)) continue;
                net.MoveNode(build.Moves[i].Key, build.Moves[i].Value);
                RefreshDirections(build.Moves[i].Key);
            }

            for (int i = build.Splits.Count - 1; i >= 0; i--)
            {
                var split = build.Splits[i];
                foreach (var segment in split.Segments) ReleaseSegmentIfExists(segment);
                foreach (var node in split.Nodes)
                    if (NodeExists(node) && SegmentCount(node) == 0) net.ReleaseNode(node);
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
                    // The restored road has a new id: older builds still on the undo
                    // stack may refer to it by the old one (e.g. they had cut it).
                    Remap(split.Original, restored);
                }
            }
        }

        private static void Remap(ushort oldSegment, ushort newSegment)
        {
            foreach (var build in undoStack)
            {
                for (int i = 0; i < build.Segments.Count; i++)
                    if (build.Segments[i] == oldSegment) build.Segments[i] = newSegment;
                foreach (var split in build.Splits)
                    for (int i = 0; i < split.Segments.Count; i++)
                        if (split.Segments[i] == oldSegment) split.Segments[i] = newSegment;
            }
        }

        private static void ReleaseSegmentIfExists(ushort segment)
        {
            var net = NetManager.instance;
            if ((net.m_segments.m_buffer[segment].m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None)
                net.ReleaseSegment(segment, true);
        }

        // Simulation thread. Cuts an existing segment at the given positions
        // along it (0..1, increasing): new nodes there, and pieces between
        // them following the original curve. pieceInfos (one per piece) lets a
        // piece become e.g. a bridge; null keeps the original type.
        private static Split SplitSegment(ushort segment, float[] at, NetInfo[] pieceInfos)
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
            };

            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            net.ReleaseSegment(segment, true);

            var chain = new List<ushort> { split.StartNode };
            var directions = new List<Vector3> { split.StartDirection };
            foreach (float t in at)
            {
                Vector3 position = CurveMath.Bezier(a, b, c, d, t);
                Vector3 derivative = CurveMath.BezierDerivative(a, b, c, d, t);
                float flat = Mathf.Max(Mathf.Sqrt(derivative.x * derivative.x + derivative.z * derivative.z), 0.0001f);
                ushort node;
                if (!net.CreateNode(out node, ref sim.m_randomizer, split.Info, position, sim.m_currentBuildIndex))
                    throw new Exception("could not add a node to " + split.Info.name);
                sim.m_currentBuildIndex++;
                // A cut in a road on the ground stays on the ground.
                var nodes = net.m_nodes.m_buffer;
                if ((nodes[split.StartNode].m_flags & nodes[split.EndNode].m_flags & NetNode.Flags.OnGround) != NetNode.Flags.None)
                    nodes[node].m_flags |= NetNode.Flags.OnGround;
                split.Nodes.Add(node);
                chain.Add(node);
                directions.Add(new Vector3(derivative.x / flat, 0f, derivative.z / flat));
            }
            chain.Add(split.EndNode);

            for (int i = 1; i < chain.Count; i++)
            {
                Vector3 startDir = i == 1 ? split.StartDirection : directions[i - 1];
                Vector3 endDir = i == chain.Count - 1 ? split.EndDirection : -directions[i];
                NetInfo pieceInfo = pieceInfos != null ? pieceInfos[i - 1] : split.Info;
                ushort piece;
                if (!net.CreateSegment(out piece, ref sim.m_randomizer, pieceInfo, chain[i - 1], chain[i],
                        startDir, endDir, sim.m_currentBuildIndex, sim.m_currentBuildIndex, split.Invert))
                    throw new Exception("could not rebuild " + split.Info.name);
                sim.m_currentBuildIndex++;
                CalculateSegmentDirections(piece);
                split.Segments.Add(piece);
            }
            return split;
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

        // --- half snapping: attach a branch to one half of a wide road's end ---

        // Where a narrower road attaches to one half of a wide road's end.
        public struct HalfSnap
        {
            public int Side;          // +1 = left half, -1 = right half (seen from the trunk looking out)
            public Vector3 Forward;   // trunk direction at its end, pointing out of the trunk
            public float Offset;      // sideways distance of the branch centre line from the trunk's
            public float RunIn;       // how far the branch runs parallel before it may curve away
        }

        private const float MinHalfRunIn = 24f;

        // Can a 'branch' road attach to a half of the road ending at 'node'?
        // True for a dead end of a road at least ~1.33x as wide as the branch,
        // or such a road end with one branch already on a half (the new one
        // then gets the other half). 'cursor' picks the half; 'forcedSide' (if
        // not 0) overrides it.
        public static bool TryHalfSnap(ushort node, NetInfo branch, Vector3 cursor, int forcedSide, out HalfSnap snap)
        {
            snap = default(HalfSnap);
            if (branch == null || !NodeExists(node)) return false;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var segments = NetManager.instance.m_segments.m_buffer;

            // Trunk = the widest road at the node.
            ushort trunk = 0, other = 0;
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort s = nodes[node].GetSegment(i);
                if (s == 0) continue;
                count++;
                if (trunk == 0 || segments[s].Info.m_halfWidth > segments[trunk].Info.m_halfWidth)
                {
                    other = trunk;
                    trunk = s;
                }
                else
                {
                    other = s;
                }
            }
            if (count < 1 || count > 2) return false;

            float trunkHalf = segments[trunk].Info.m_halfWidth;
            float branchHalf = branch.m_halfWidth;
            if (branchHalf > trunkHalf * 0.75f) return false;

            Vector3 position = nodes[node].m_position;
            Vector3 forward = -CurveMath.FlatNormal(DirectionAway(trunk, node));
            Vector3 left = CurveMath.Left(forward);
            int side = forcedSide != 0 ? forcedSide : (Vector3.Dot(cursor - position, left) >= 0f ? 1 : -1);

            if (count == 2)
            {
                // The other road must be a narrower branch already leaving this
                // end, not the same road carrying on (a node mid-road).
                if (segments[other].Info.m_halfWidth > trunkHalf * 0.75f) return false;
                if (Vector3.Dot(CurveMath.FlatNormal(DirectionAway(other, node)), forward) < Mathf.Cos(45f * Mathf.Deg2Rad))
                    return false;
                ushort far = segments[other].m_startNode == node ? segments[other].m_endNode : segments[other].m_startNode;
                int taken = Vector3.Dot(nodes[far].m_position - position, left) >= 0f ? 1 : -1;
                side = -taken;
            }

            snap.Side = side;
            snap.Forward = forward;
            // Branches side by side without overlapping; outer edge at least at the trunk's edge.
            snap.Offset = Mathf.Max(branchHalf, trunkHalf - branchHalf);
            // A long, gentle taper before the branch curves away.
            snap.RunIn = Mathf.Max(MinHalfRunIn, snap.Offset * 8f);
            return true;
        }

        // Point on the trunk end where the branch attaches (shown under the cursor).
        public static Vector3 HalfSnapMarker(ushort node, HalfSnap snap)
        {
            Vector3 position = NetManager.instance.m_nodes.m_buffer[node].m_position;
            return position + CurveMath.Left(snap.Forward) * (snap.Side * snap.Offset);
        }

        // End of the parallel run-in, where the branch's own curve begins.
        public static Vector3 HalfSnapRunIn(ushort node, HalfSnap snap)
        {
            return HalfSnapMarker(node, snap) + snap.Forward * snap.RunIn;
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
