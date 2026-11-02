using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace RealisticRoads
{
    // Cutting a drawn curve into the pieces RR builds: the nodes (crossings,
    // water, corners, the player's heights, spacing), the crossings found
    // along it, the water runs it passes over, the decks it is on, and the
    // checks after planning (nodes over bridged roads, buildings in the way).
    // Split out of CurveTool.cs, unchanged.
    public partial class CurveTool
    {
        // The node right on a road or rail we bridge over (bridge, overpass) is
        // dropped: the deck spans from the nodes either side, and the pillar that
        // node would get doesn't stand on the road or track.
        private static void DropNodesOverCrossings(Division division)
        {
            var plan = division.Plan;
            if (plan == null) return;
            for (int c = division.Crossings.Count - 1; c >= 0; c--)
            {
                var crossing = division.Crossings[c];
                if (crossing.Type != CrossingType.Bridge && crossing.Type != CrossingType.Overpass) continue;
                int k = division.CrossingIndex[c];
                if (k <= 0 || k >= plan.Points.Count - 1) continue;
                // Only with a raised node on each side to carry the deck, and only if
                // the one piece left doesn't bend more than a piece may.
                if (!IsRaised(plan.Kinds[k - 1]) || !IsRaised(plan.Kinds[k + 1])) continue;
                if (IsCorner(division, k)) continue;
                if (CurveMath.BendAngle(division.Tangents[k - 1], division.InTangents[k + 1]) > MaxBend) continue;
                // Not where the one span left would be too long for the game (a
                // crossing at a shallow angle, or several side by side): the node
                // stays, with no pillar (over the road or track it crosses).
                if (CurveMath.FlatDistance(division.Points[k - 1], division.Points[k + 1]) > MaxDroppedSpan) continue;
                division.Points.RemoveAt(k);
                division.Tangents.RemoveAt(k);
                division.InTangents.RemoveAt(k);
                RoadPlanner.RemoveNode(plan, k);
                division.CrossingIndex[c] = -1;
                for (int other = 0; other < division.CrossingIndex.Count; other++)
                    if (division.CrossingIndex[other] > k) division.CrossingIndex[other]--;
            }
        }

        // Buildings whose footprint the new road's ground-level stretches run
        // over. Raised and tunnelled stretches pass over or under them. Each
        // building grid cell is walked once; each building's footprint is its
        // rectangle (its cells, turned as it stands), our road's half width round it.
        private const float BuildingSample = 8f;
        private const float BuildingTouch = 1f;   // overlapping a footprint by less than this isn't running through it

        private int BuildingsInTheWay(Division division)
        {
            var plan = division.Plan;
            if (plan == null || Net == null) return 0;
            var manager = BuildingManager.instance;
            var buildings = manager.m_buildings.m_buffer;
            var grid = manager.m_buildingGrid;
            const int resolution = BuildingManager.BUILDINGGRID_RESOLUTION;
            const float cell = BuildingManager.BUILDINGGRID_CELL_SIZE;
            float half = Net.m_halfWidth;
            var samples = new List<Vector3>();
            var cells = new HashSet<int>();
            for (int i = 1; i < plan.Points.Count; i++)
            {
                if (IsRaised(plan.Kinds[i - 1]) && IsRaised(plan.Kinds[i])) continue;
                if (plan.Kinds[i - 1] == NodeKind.Tunnel && plan.Kinds[i] == NodeKind.Tunnel) continue;
                var bezier = PieceBezier(division, i);
                int steps = Mathf.Max(2, Mathf.RoundToInt(CurveMath.FlatDistance(bezier.a, bezier.d) / BuildingSample));
                for (int k = 0; k <= steps; k++)
                {
                    Vector3 p = CurveMath.Bezier(bezier.a, bezier.b, bezier.c, bezier.d, k / (float)steps);
                    samples.Add(p);
                    int gx = Mathf.Clamp((int)(p.x / cell + resolution / 2f), 0, resolution - 1);
                    int gz = Mathf.Clamp((int)(p.z / cell + resolution / 2f), 0, resolution - 1);
                    for (int z = Mathf.Max(gz - 1, 0); z <= Mathf.Min(gz + 1, resolution - 1); z++)
                        for (int x = Mathf.Max(gx - 1, 0); x <= Mathf.Min(gx + 1, resolution - 1); x++)
                            cells.Add(z * resolution + x);
                }
            }
            var hit = new HashSet<ushort>();
            foreach (int index in cells)
            {
                ushort id = grid[index];
                int guard = 0;
                while (id != 0 && guard++ < 49152)
                {
                    var b = buildings[id];
                    if ((b.m_flags & Building.Flags.Created) != Building.Flags.None && b.Info != null && !hit.Contains(id))
                    {
                        // The footprint: its width along (cos, sin) of its angle, its length across that.
                        float angle = b.m_angle;
                        Vector3 across = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                        Vector3 along = new Vector3(across.z, 0f, -across.x);
                        float w = b.Info.m_cellWidth * 4f + half - BuildingTouch, l = b.Info.m_cellLength * 4f + half - BuildingTouch;
                        float reach = Mathf.Sqrt(w * w + l * l);
                        foreach (var p in samples)
                        {
                            float dx = p.x - b.m_position.x, dz = p.z - b.m_position.z;
                            if (dx * dx + dz * dz > reach * reach) continue;
                            if (Mathf.Abs(dx * across.x + dz * across.z) < w && Mathf.Abs(dx * along.x + dz * along.z) < l)
                            {
                                hit.Add(id);
                                break;
                            }
                        }
                    }
                    id = b.m_nextGridBuilding;
                }
            }
            return hit.Count;
        }

        private Division DivideShaped(List<Vector3> controls, Vector3 startDir, Vector3 endDir, ushort startNode, ushort endNode,
            List<float> lifts, List<bool> straight, List<bool> deck, List<bool> below, List<List<Vector3>> paths, HashSet<ushort> under)
        {
            int[] controlSamples;
            List<float> corners;
            var polyline = CurveMath.SampleMixed(controls, straight, cornerRadius, startDir, endDir, out controlSamples, out corners, paths);
            return DivideAlong(polyline, ControlDistances(polyline, controlSamples), corners, startDir, endDir, startNode, endNode,
                lifts, deck, below, under);
        }

        // Cuts a sampled curve into nodes: crossings, water, corners and the
        // player's heights get nodes of their own, the rest is spaced out.
        // 'controlAt': each point's distance along it; 'lifts': each point's height.
        private Division DivideAlong(List<Vector3> polyline, float[] controlAt, List<float> corners, Vector3 startDir, Vector3 endDir,
            ushort startNode, ushort endNode, List<float> lifts, List<bool> deck, List<bool> below, HashSet<ushort> under)
        {
            var division = new Division { Points = new List<Vector3>(), Tangents = new List<Vector3>(), InTangents = new List<Vector3>() };
            division.Length = CurveMath.Length(polyline);
            division.Polyline = polyline;
            division.Corners = corners;
            division.Setup = line;
            if (division.Length < 1f) return division;

            // Stretches over water get long bridge spans; crossings become fixed nodes.
            division.WaterRuns = FindWater(polyline);
            var cuts = new List<KeyValuePair<float, Crossing>>();
            // A point with a height of its own gets a node, so the height is held exactly there.
            division.ControlAt = controlAt;
            // A height dragged in the side view holds the node there (it was a
            // node already: the drag picked it). Stretches marked in it are
            // measured along the curve, like the decks.
            foreach (var pin in line.Pins)
            {
                float at;
                if (!CurveMath.PlaceAlong(controlAt, pin.At, out at)) continue;
                cuts.Add(new KeyValuePair<float, Crossing>(at, null));
                Vector3 point, ignore;
                CurveMath.PointAt(polyline, at, out point, out ignore);
                division.Heights.Add(new KeyValuePair<Vector3, float>(point, pin.Height));
            }
            foreach (var stretch in line.Stretches)
            {
                float from, to;
                if (!CurveMath.PlaceAlong(controlAt, stretch.From, out from)) continue;
                CurveMath.PlaceAlong(controlAt, Mathf.Min(stretch.To, controlAt.Length - 1), out to);
                if (to > from) division.ForcedRanges.Add(new ForcedStretch { From = from, To = to, Kind = stretch.Kind });
            }
            for (int i = 0; i < deck.Count; i++)
            {
                if (deck[i]) division.DeckRanges.Add(new KeyValuePair<float, float>(controlAt[i], controlAt[i + 1]));
                if (below[i]) division.UnderRanges.Add(new KeyValuePair<float, float>(controlAt[i], controlAt[i + 1]));
            }
            // A sharp corner (two straight pieces meeting at an angle) is a node.
            foreach (float corner in corners) cuts.Add(new KeyValuePair<float, Crossing>(corner, null));
            for (int i = 1; i < controlAt.Length - 1 && i < lifts.Count; i++)
                if (Mathf.Abs(lifts[i]) > 0.01f) cuts.Add(new KeyValuePair<float, Crossing>(controlAt[i], null));
            foreach (var run in division.WaterRuns)
            {
                cuts.Add(new KeyValuePair<float, Crossing>(run.From, null));
                cuts.Add(new KeyValuePair<float, Crossing>(run.To, null));
            }
            // A median cut's track: a node where it reaches the ground and one where
            // it reaches the tunnel's depth (the open cut between them is one piece).
            if (cutFrom >= 0f && !planningGround)
            {
                cuts.Add(new KeyValuePair<float, Crossing>(cutFrom, null));
                cuts.Add(new KeyValuePair<float, Crossing>(cutTo, null));
            }
            var timesCrossed = new Dictionary<ushort, int>();
            var takenKeys = new HashSet<int>();
            var snapped = new List<int>();
            var found = new List<Crossing>();
            var crossedSegments = NetManager.instance.m_segments.m_buffer;
            foreach (var crossing in CrossingFinder.Find(polyline, Net, startNode, endNode))
            {
                // A median ramp's ground road runs under its own ramp: not a crossing.
                if (planningGround && SplitRoads.IsRamp(crossedSegments[crossing.Segment].Info)) continue;
                // A median cut's track runs along its road (not across it), and its
                // ground road runs either side of the track.
                if (MedianCut && !planningGround && RunsAlong(polyline, crossing)) continue;
                if ((MedianCut || buildingCut) && planningGround && RoadPlanner.IsRail(crossedSegments[crossing.Segment].Info)) continue;
                if (crossing.Distance < MinSpacing * 0.5f || crossing.Distance > division.Length - MinSpacing * 0.5f) continue;
                if (InDeck(division.DeckRanges, crossing.Distance))
                {
                    // On a deck: the road it runs along isn't crossed; any other is
                    // passed over (or under, below the road).
                    if (under.Contains(crossing.Segment) || RunsAlong(polyline, crossing)) continue;
                    if (InDeck(division.UnderRanges, crossing.Distance)) crossing.UnderDeck = true;
                    else crossing.OnDeck = true;
                }
                int times;
                timesCrossed.TryGetValue(crossing.Segment, out times);
                timesCrossed[crossing.Segment] = times + 1;
                crossing.Occurrence = line.CrossingPlaces != null ? PlaceOccurrence(line.CrossingPlaces, crossing, takenKeys) : times;
                CrossingType type;
                // Default: a junction where the two may join, else a bridge over the other.
                // (Under a deck under the road: a tunnel, the only one allowed there.)
                // In a stretch marked Elevated or Tunnel: over it, or under it.
                var forcedHere = ForcedAt(division.ForcedRanges, crossing.Distance);
                var fallback = crossing.UnderDeck ? CrossingType.Tunnel
                    : forcedHere == Forced.Tunnel && crossing.Allows(CrossingType.Tunnel) ? CrossingType.Tunnel
                    : forcedHere == Forced.Elevated && crossing.Allows(CrossingType.Bridge) ? CrossingType.Bridge
                    : crossing.Allows(CrossingType.Junction) ? CrossingType.Junction : CrossingType.Bridge;
                crossing.Type = ChosenType(crossing, out type) && crossing.Allows(type) ? type : fallback;
                // A ship route on a water crossing goes the way the whole crossing does:
                // over it on the bridge, or under it in the tunnel.
                var shipRun = crossing.ShipPath ? RunAt(division.WaterRuns, crossing.Distance) : null;
                if (shipRun != null) crossing.Type = shipRun.Tunnel ? CrossingType.Tunnel : CrossingType.Bridge;
                crossing.ExtraHeight = ChosenExtra(crossing);
                cuts.Add(new KeyValuePair<float, Crossing>(crossing.Distance, crossing));
                found.Add(crossing);
            }
            // Over another road or rail (bridge, overpass): nodes either side of
            // it too, so the node on it can be dropped later and no pillar
            // stands on the road or track below. Not where that side node would
            // land on another road or track we cross: the deck spans both.
            var footprint = new Dictionary<Crossing, float>();
            float ourHalf = Net != null ? Net.m_halfWidth : 8f;
            foreach (var crossing in found)
                footprint[crossing] = crossing.Type == CrossingType.Bridge || crossing.Type == CrossingType.Overpass
                    ? SideOffset(polyline, crossing)
                    : crossing.ExistingHalfWidth + ourHalf + RoadPlanner.PillarClearance;
            foreach (var crossing in found)
            {
                if (crossing.Type != CrossingType.Bridge && crossing.Type != CrossingType.Overpass) continue;
                foreach (float at in new[] { crossing.Distance - footprint[crossing], crossing.Distance + footprint[crossing] })
                {
                    bool onAnother = false;
                    foreach (var other in found)
                        if (other != crossing && Mathf.Abs(at - other.Distance) < footprint[other]) onAnother = true;
                    if (!onAnother) cuts.Add(new KeyValuePair<float, Crossing>(at, null));
                }
            }
            cuts.Sort((p, q) => p.Key.CompareTo(q.Key));

            var stops = new List<KeyValuePair<float, Crossing>> { new KeyValuePair<float, Crossing>(0f, null) };
            // A crossing too close to another one for a node of its own shares that
            // one's node: its height still holds there (it is never dropped).
            var sharing = new Dictionary<Crossing, List<Crossing>>();
            foreach (var cut in cuts)
            {
                if (cut.Key < 4f || cut.Key > division.Length - 4f) continue;
                if (cut.Key - stops[stops.Count - 1].Key < 4f)
                {
                    // Too close to the previous stop: keep the crossing if there is one,
                    // else a sharp corner (the road turns there, so it needs the node).
                    var previous = stops[stops.Count - 1];
                    if (cut.Value != null && previous.Value != null)
                    {
                        List<Crossing> list;
                        if (!sharing.TryGetValue(previous.Value, out list)) sharing[previous.Value] = list = new List<Crossing>();
                        list.Add(cut.Value);
                        continue;
                    }
                    bool better = cut.Value != null || (IsCornerAt(corners, cut.Key) && !IsCornerAt(corners, previous.Key));
                    if (better && previous.Value == null && stops.Count > 1)
                        stops[stops.Count - 1] = cut;
                    continue;
                }
                stops.Add(cut);
            }
            stops.Add(new KeyValuePair<float, Crossing>(division.Length, null));

            for (int c = 1; c < stops.Count; c++)
            {
                float from = stops[c - 1].Key, to = stops[c].Key;
                var slice = CurveMath.Slice(polyline, from, to);
                bool overWater = InWater(division.WaterRuns, (from + to) * 0.5f);
                List<Vector3> slicePoints, sliceTangents;
                bool ignored;
                if (cutFrom >= 0f && !planningGround && Mathf.Abs(from - cutFrom) < 0.5f && Mathf.Abs(to - cutTo) < 0.5f)
                {
                    // The open cut: one piece, the game's slope piece (OneSlope checks its bend).
                    CurveMath.Divide(slice, 1, out slicePoints, out sliceTangents);
                }
                else if (overWater)
                {
                    // Bridges: equal spans so the bridge model repeats evenly.
                    int count = CurveMath.ChoosePieceCount(slice, BridgeSpacing, BridgeMinSpacing, MaxBend, out ignored);
                    CurveMath.Divide(slice, count, out slicePoints, out sliceTangents);
                }
                else
                {
                    // Land: long pieces on straights, shorter only in bends.
                    CurveMath.DivideAdaptive(slice, Spacing, MinSpacing, MaxBend, out slicePoints, out sliceTangents, out ignored);
                }
                int skip = division.Points.Count == 0 ? 0 : 1;
                for (int i = skip; i < slicePoints.Count; i++)
                {
                    division.Points.Add(slicePoints[i]);
                    division.Tangents.Add(sliceTangents[i]);
                    division.InTangents.Add(sliceTangents[i]);
                }
                if (c < stops.Count - 1)
                {
                    // Direction at a stop comes from the whole curve, not one side of it.
                    int index = division.Points.Count - 1;
                    Vector3 point, tangent;
                    CurveMath.PointAt(polyline, to, out point, out tangent);
                    if (tangent != Vector3.zero) division.Tangents[index] = division.InTangents[index] = tangent;
                    if (IsCornerAt(corners, to))
                    {
                        // A sharp corner: the road arrives along one straight and leaves along the other.
                        Vector3 before, after, ignore;
                        CurveMath.PointAt(polyline, to - CornerProbe, out ignore, out before);
                        CurveMath.PointAt(polyline, to + CornerProbe, out ignore, out after);
                        if (before != Vector3.zero) division.InTangents[index] = before;
                        if (after != Vector3.zero) division.Tangents[index] = after;
                    }
                    if (stops[c].Value != null)
                    {
                        // A junction right next to an existing node joins at that node:
                        // move our node onto it now, so the planner, the preview and the
                        // bend check all see the road that will be built.
                        var snap = stops[c].Value;
                        if (snap.Connects && snap.SnapNode != 0)
                        {
                            Vector3 node = NetManager.instance.m_nodes.m_buffer[snap.SnapNode].m_position;
                            division.Points[index] = new Vector3(node.x, division.Points[index].y, node.z);
                            snapped.Add(index);
                        }
                        division.Crossings.Add(stops[c].Value);
                        division.CrossingIndex.Add(index);
                        List<Crossing> sharers;
                        if (sharing.TryGetValue(stops[c].Value, out sharers))
                            foreach (var sharer in sharers)
                            {
                                division.Crossings.Add(sharer);
                                division.CrossingIndex.Add(index);
                            }
                    }
                }
            }

            // A snapped node's direction runs from its neighbour before to the one after.
            foreach (int index in snapped)
            {
                if (index <= 0 || index >= division.Points.Count - 1) continue;
                Vector3 through = CurveMath.FlatNormal(division.Points[index + 1] - division.Points[index - 1]);
                if (through != Vector3.zero && !IsCorner(division, index)) division.Tangents[index] = division.InTangents[index] = through;
            }
            if (startDir != Vector3.zero) division.Tangents[0] = division.InTangents[0] = CurveMath.FlatNormal(startDir);
            int lastIndex = division.Tangents.Count - 1;
            if (endDir != Vector3.zero) division.Tangents[lastIndex] = division.InTangents[lastIndex] = CurveMath.FlatNormal(endDir);
            division.Ok = MaxPieceBend(division) <= MaxBend;
            return division;
        }

        private const float CornerProbe = 1f;
        private const float AlongAngle = 20f;
        private const float NodeSearchSlack = 150f;   // looking along the curve for a node stops this far past the closest     // a road within this angle of a deck is the one it runs along

        private static bool InDeck(List<KeyValuePair<float, float>> ranges, float at)
        {
            foreach (var range in ranges) if (at >= range.Key - 1f && at <= range.Value + 1f) return true;
            return false;
        }

        // The crossed road runs the same way as the curve there (a deck drawn along it).
        private static bool RunsAlong(List<Vector3> polyline, Crossing crossing)
        {
            Vector3 point, ours;
            CurveMath.PointAt(polyline, crossing.Distance, out point, out ours);
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(crossing.Segment, out a, out b, out c, out d);
            Vector3 theirs = CurveMath.BezierDerivative(a, b, c, d, crossing.ExistingT);
            float angle = CurveMath.BendAngle(ours, theirs);
            return angle < AlongAngle || angle > 180f - AlongAngle;
        }

        // Which nodes are on a deck, and the road under each of them.
        // Per node: on a deck (and what is below it), or in a stretch marked in
        // the side view.
        // How far a deck may round a junction's corner inside it: to a metre
        // short of the edge of the narrowest of the roads it follows.
        private static float InwardAllowed(HashSet<ushort> roads)
        {
            float narrowest = float.MaxValue;
            var segments = NetManager.instance.m_segments.m_buffer;
            foreach (ushort segment in roads)
            {
                var info = segments[segment].Info;
                if (info != null) narrowest = Mathf.Min(narrowest, info.m_halfWidth);
            }
            return narrowest == float.MaxValue ? 4f : Mathf.Max(narrowest - 1f, 2f);
        }

        // The roads at the curve's joined ends: a deck joined to one of ours is
        // not a level above it (Decks.Under leaves them out).
        private static HashSet<ushort> JoinedRoads(ushort startNode, ushort endNode, ushort startSegment, ushort endSegment)
        {
            var result = new HashSet<ushort>();
            var nodes = NetManager.instance.m_nodes.m_buffer;
            foreach (ushort node in new[] { startNode, endNode })
                if (node != 0)
                    for (int i = 0; i < 8; i++)
                    {
                        ushort s = nodes[node].GetSegment(i);
                        if (s != 0) result.Add(s);
                    }
            if (startSegment != 0) result.Add(startSegment);
            if (endSegment != 0) result.Add(endSegment);
            return result;
        }

        private static void FindDecks(Division division, float ourHalfWidth, HashSet<ushort> joined = null)
        {
            int n = division.Points.Count;
            division.OnDeck = new bool[n];
            division.UnderDeck = new bool[n];
            division.Below = new DeckBelow[n];
            division.NodeAlong = NodeDistances(division);
            division.Forced = new Forced[n];
            for (int i = 0; i < n; i++) division.Forced[i] = ForcedAt(division.ForcedRanges, division.NodeAlong[i]);
            for (int i = 0; i < n; i++)
            {
                if (!InDeck(division.DeckRanges, division.NodeAlong[i])) continue;
                division.OnDeck[i] = true;
                division.UnderDeck[i] = InDeck(division.UnderRanges, division.NodeAlong[i]);
                division.Below[i] = Decks.Under(division.Points[i], ourHalfWidth, division.UnderDeck[i], joined);
            }
        }

        // Each node's distance along the sampled curve (the ranges are measured
        // along it; adding up the straight pieces between nodes falls short on bends).
        private static float[] NodeDistances(Division division)
        {
            int n = division.Points.Count;
            var result = new float[n];
            var line = division.Polyline;
            if (line == null || line.Count == 0)
            {
                for (int i = 1; i < n; i++) result[i] = result[i - 1] + CurveMath.FlatDistance(division.Points[i - 1], division.Points[i]);
                return result;
            }
            int sample = 0;
            float sampleAlong = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = division.Points[i];
                int best = sample;
                float bestDistance = CurveMath.FlatDistance(line[sample], p), bestAlong = sampleAlong, walked = sampleAlong;
                for (int k = sample + 1; k < line.Count; k++)
                {
                    walked += CurveMath.FlatDistance(line[k - 1], line[k]);
                    float d = CurveMath.FlatDistance(line[k], p);
                    if (d < bestDistance)
                    {
                        best = k;
                        bestDistance = d;
                        bestAlong = walked;
                    }
                    else if (d > bestDistance + NodeSearchSlack) break;
                }
                sample = best;
                sampleAlong = bestAlong;
                result[i] = bestAlong;
            }
            return result;
        }

        private static Forced ForcedAt(List<ForcedStretch> ranges, float at)
        {
            foreach (var range in ranges) if (at >= range.From && at <= range.To) return range.Kind;
            return Forced.None;
        }     // metres either side of a sharp corner its two directions are read

        private static bool IsCornerAt(List<float> corners, float at)
        {
            foreach (float corner in corners) if (Mathf.Abs(corner - at) < 0.5f) return true;
            return false;
        }

        // A node where the road turns on the spot (a sharp corner).
        private static bool IsCorner(Division division, int i) =>
            CurveMath.BendAngle(division.InTangents[i], division.Tangents[i]) > 0.5f;

        // How much the road turns within one piece, at most (a sharp corner
        // turns at a node, between pieces, so it doesn't count).
        private static float MaxPieceBend(Division division)
        {
            float most = 0f;
            for (int i = 1; i < division.Tangents.Count; i++)
                most = Mathf.Max(most, CurveMath.BendAngle(division.Tangents[i - 1], division.InTangents[i]));
            return most;
        }

        // Stretches of the curve over water. Wet patches a short way apart are
        // one crossing: a stream with gravel banks, a river with an islet, the
        // bottom of a gorge (see the joining below).
        private List<WaterRun> FindWater(List<Vector3> polyline)
        {
            var runs = new List<WaterRun>();
            float along = 0f, previous = 0f;
            bool wasWet = false;
            WaterRun current = null;
            for (int i = 0; i < polyline.Count; i++)
            {
                if (i > 0) along += CurveMath.FlatDistance(polyline[i - 1], polyline[i]);
                bool wet = NetworkBuilder.IsOverWater(polyline[i]);
                // Each wet stretch reaches halfway to the dry samples either side,
                // so one wet sample is as wide as the sample spacing, not 0 m.
                if (wet && !wasWet)
                {
                    float from = i > 0 ? (previous + along) * 0.5f : along;
                    current = new WaterRun { From = from, To = along };
                    runs.Add(current);
                }
                else if (wet) current.To = along;
                else if (wasWet) current.To = (previous + along) * 0.5f;
                wasWet = wet;
                previous = along;
            }
            // Wet patches with a short dry gap between are one crossing (gravel
            // banks, a small islet): always within WaterJoinShort; up to WaterJoin
            // only between two stretches of water each at least as wide as the gap
            // (a river with an islet), not a pond and a ditch along a coast.
            for (int i = runs.Count - 1; i > 0; i--)
            {
                var a = runs[i - 1];
                var b = runs[i];
                float gap = b.From - a.To;
                bool join = gap < WaterJoinShort || (gap < WaterJoin && a.To - a.From >= gap && b.To - b.From >= gap);
                if (!join) continue;
                a.To = b.To;
                runs.RemoveAt(i);
            }
            // A wet sample or two at a shore or a puddle isn't a crossing.
            runs.RemoveAll(run => run.To - run.From < MinWaterWidth);
            var taken = new HashSet<int>();
            foreach (var run in runs)
            {
                Vector3 middle, tangent;
                CurveMath.PointAt(polyline, (run.From + run.To) * 0.5f, out middle, out tangent);
                run.Middle = middle;
                // Remembered by where it is: the crossing remembered nearest its
                // middle (each once), which then follows it, so the bridge or
                // tunnel and the +/- stay as the curve grows across the water or
                // its points are dragged. A new one gets a new key.
                var places = line.WaterPlaces;
                int nearest = -1;
                float best = WaterMatch;
                for (int i = 0; i < places.Count; i++)
                {
                    if (taken.Contains(places[i].Key)) continue;
                    float d = CurveMath.FlatDistance(places[i].Value, middle);
                    if (d >= best) continue;
                    best = d;
                    nearest = i;
                }
                run.Key = nearest >= 0 ? places[nearest].Key : ++lastWaterKey;
                if (nearest >= 0) places[nearest] = new KeyValuePair<int, Vector3>(run.Key, middle);
                else places.Add(new KeyValuePair<int, Vector3>(run.Key, middle));
                taken.Add(run.Key);
                float extra;
                run.Extra = line.WaterExtra.TryGetValue(run.Key, out extra) ? extra : 0f;
                bool tunnel;
                run.Tunnel = line.WaterTunnel.TryGetValue(run.Key, out tunnel) && tunnel;
            }
            return runs;
        }

        // Where each control point is along the curve through them (the
        // curve passes through every one, in order).
        // Metres along the polyline at each control point, from its sample index.
        private static float[] ControlDistances(List<Vector3> polyline, int[] controlSamples)
        {
            var result = new float[controlSamples.Length];
            float distance = 0f;
            int sample = 0;
            for (int c = 0; c < controlSamples.Length; c++)
            {
                int index = Mathf.Min(controlSamples[c], polyline.Count - 1);
                for (; sample < index; sample++) distance += CurveMath.FlatDistance(polyline[sample], polyline[sample + 1]);
                result[c] = distance;
            }
            return result;
        }

        private static bool InWater(List<WaterRun> runs, float at)
        {
            foreach (var run in runs) if (at > run.From && at < run.To) return true;
            return false;
        }
    }
}
