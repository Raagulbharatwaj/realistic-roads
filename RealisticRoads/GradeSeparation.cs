using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealisticRoads
{
    // A car lane on a leg next to the junction, seen facing the way the
    // approach leg's traffic goes into the junction (the "approach frame").
    public class LegLane
    {
        public int Number;          // 1 = leftmost
        public float Lateral;       // metres right of the road's centre line
        public float Width;
        public bool Forward;        // travels toward the junction on the approach (away from it on the far side)
        public uint LaneId;         // for drawing
    }

    // A road leaving the junction: the straight chain of segments up to the
    // next junction (or MaxDistance and a bit), with its centre line.
    public class Leg
    {
        public ushort Junction;
        public readonly List<ushort> Segments = new List<ushort>();   // from the junction outward
        public readonly List<Vector3> Path = new List<Vector3>();     // centre line from the junction outward (y = road height)
        public float Length;
        public NetInfo Info;                                          // of the segment at the junction
        public Vector3 Direction;                                     // away from the junction, flat
        public readonly List<LegLane> Lanes = new List<LegLane>();    // car lanes, left to right (facing the junction)

        public ushort First => Segments.Count > 0 ? Segments[0] : (ushort)0;
    }

    // One road the split makes: a ground part beside the raised one, or the
    // raised part itself.
    public class GradePart
    {
        public string Name;                // "left", "raised", "right"
        public bool Raised;
        public int Forward, Backward;      // lanes in the approach frame
        public float Offset;               // centre line, metres right of the original one
        public NetInfo Info;               // matched ground road type (raised: its elevated version is used)
        public bool Reversed;              // the road type's forward lanes run against the approach frame
        public readonly List<Vector3> Preview = new List<Vector3>();   // for drawing
    }

    // One road to build: points from start to end, which existing node each end is.
    public enum GradeEnd { Junction, ApproachCut, FarCut }

    public class GradePiece
    {
        public NetInfo Info;
        public Plan Plan;
        public GradeEnd Start, End;
        public string What;
    }

    public class GradePlan
    {
        public ushort Junction;
        public Leg Approach, Far;
        public float Distance, Height;
        public float MinDistance, MaxDistance;
        public List<ushort> ApproachSegments = new List<ushort>(), FarSegments = new List<ushort>();
        public float ApproachT = -1f, FarT = -1f;   // where the last segment is cut (<0: removed whole)
        public Vector3 ApproachCut, FarCut, Peak;
        public readonly List<GradePart> Parts = new List<GradePart>();
        public readonly List<GradePiece> Pieces = new List<GradePiece>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Ok => Problems.Count == 0 && Pieces.Count > 0;
    }

    // Main thread. Works out a grade separation: a group of lanes on one leg
    // of a junction is split off, rises at a road grade, flies over the
    // junction and lands on the leg straight across; the other lanes stay on
    // the ground as their own roads beside it. Each part uses the loaded road
    // type (vanilla or Workshop) closest to its lanes.
    public static class GradeSeparation
    {
        public const float MaxDistance = 500f;
        public const float MinHeight = RoadPlanner.OverClearance;
        public const float MaxHeight = 40f;
        public const float DefaultHeight = 10f;
        private const float Grade = RoadPlanner.RoadGrade;
        private const float StraightAcross = 35f;    // the far leg is within this of straight on
        private const float SideBySide = 40f;        // the parts move apart over this length after the split
        private const float VerticalCurve = 30f;     // rounding at the top and bottom of a ramp
        private const float RaisedSpacing = 40f;     // nodes (pillars) on the raised part
        private const float GroundSpacing = 80f;
        private const float PartGap = 0.5f;          // between the parts' edges
        private const float CutSnap = 8f;            // a cut this close to a node uses the node
        private const float MaxSpan = 60f;           // longest deck span over the junction

        // ---- Legs and lanes ----

        public static List<Leg> Legs(ushort junction)
        {
            var legs = new List<Leg>();
            if (!NetworkBuilder.NodeExists(junction)) return legs;
            var node = NetManager.instance.m_nodes.m_buffer[junction];
            for (int i = 0; i < 8; i++)
            {
                ushort segment = node.GetSegment(i);
                if (segment == 0) continue;
                var leg = Walk(junction, segment);
                if (leg != null) legs.Add(leg);
            }
            return legs;
        }

        private static Leg Walk(ushort junction, ushort first)
        {
            var net = NetManager.instance;
            var segments = net.m_segments.m_buffer;
            var nodes = net.m_nodes.m_buffer;
            var info = segments[first].Info;
            if (info == null) return null;
            var leg = new Leg { Junction = junction, Info = info };
            ushort from = junction, segment = first;
            var samples = new Vector3[17];
            while (segment != 0 && leg.Length < MaxDistance + 40f && leg.Segments.Count < 64)
            {
                leg.Segments.Add(segment);
                CrossingFinder.SampleSegment(segment, samples);
                bool forward = segments[segment].m_startNode == from;
                for (int k = 0; k < samples.Length; k++)
                {
                    Vector3 p = samples[forward ? k : samples.Length - 1 - k];
                    if (leg.Path.Count > 0)
                    {
                        if (k == 0) continue;
                        leg.Length += CurveMath.FlatDistance(leg.Path[leg.Path.Count - 1], p);
                    }
                    leg.Path.Add(p);
                }
                ushort to = forward ? segments[segment].m_endNode : segments[segment].m_startNode;
                // Carry on through plain road nodes, not through junctions or dead ends.
                if (NetworkBuilder.SegmentCount(to) != 2) break;
                ushort next = 0;
                for (int i = 0; i < 8; i++)
                {
                    ushort s = nodes[to].GetSegment(i);
                    if (s != 0 && s != segment) next = s;
                }
                if (next == 0 || leg.Segments.Contains(next)) break;
                from = to;
                segment = next;
            }
            if (leg.Path.Count >= 2) leg.Direction = CurveMath.FlatNormal(leg.Path[Mathf.Min(4, leg.Path.Count - 1)] - leg.Path[0]);
            ReadLanes(leg);
            return leg;
        }

        // The car lanes of the segment at the junction, measured from the
        // game's own lane curves, so left/right and direction are what is drawn.
        private static void ReadLanes(Leg leg)
        {
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[leg.First];
            var info = s.Info;
            if (info == null || info.m_lanes == null) return;
            bool invert = (s.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
            bool endAtJunction = s.m_endNode == leg.Junction;

            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(leg.First, out a, out b, out c, out d);
            Vector3 middle = CurveMath.Bezier(a, b, c, d, 0.5f);
            // Facing the junction along this segment.
            Vector3 towardJunction = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, 0.5f)) * (endAtJunction ? 1f : -1f);
            Vector3 right = -CurveMath.Left(towardJunction);

            uint lane = s.m_lanes;
            var found = new List<LegLane>();
            for (int i = 0; i < info.m_lanes.Length && lane != 0; i++)
            {
                var data = info.m_lanes[i];
                uint id = lane;
                lane = net.m_lanes.m_buffer[lane].m_nextLane;
                if (!IsCarLane(data)) continue;
                var direction = data.m_finalDirection;
                if (invert) direction = Flip(direction);
                if (direction != NetInfo.Direction.Forward && direction != NetInfo.Direction.Backward) continue;
                var bezier = net.m_lanes.m_buffer[id].m_bezier;
                Vector3 lanePoint = bezier.Position(0.5f);
                found.Add(new LegLane
                {
                    LaneId = id,
                    Width = Mathf.Max(data.m_width, 2f),
                    Lateral = Vector3.Dot(lanePoint - middle, right),
                    // Forward runs start to end: toward the junction if the segment ends there.
                    Forward = (direction == NetInfo.Direction.Forward) == endAtJunction,
                });
            }
            found.Sort((x, y) => x.Lateral.CompareTo(y.Lateral));
            for (int i = 0; i < found.Count; i++) found[i].Number = i + 1;
            leg.Lanes.AddRange(found);
        }

        private static NetInfo.Direction Flip(NetInfo.Direction direction)
        {
            if (direction == NetInfo.Direction.Forward) return NetInfo.Direction.Backward;
            if (direction == NetInfo.Direction.Backward) return NetInfo.Direction.Forward;
            return direction;
        }

        private static bool IsCarLane(NetInfo.Lane lane)
        {
            return (lane.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != NetInfo.LaneType.None &&
                   (lane.m_vehicleType & VehicleInfo.VehicleType.Car) != VehicleInfo.VehicleType.None;
        }

        // The leg straight across from 'approach', or null.
        public static Leg FarLeg(List<Leg> legs, Leg approach)
        {
            Leg best = null;
            float bestDot = -Mathf.Cos(StraightAcross * Mathf.Deg2Rad);
            foreach (var leg in legs)
            {
                if (leg == approach) continue;
                float dot = Vector3.Dot(leg.Direction, approach.Direction);
                if (dot < bestDot)
                {
                    bestDot = dot;
                    best = leg;
                }
            }
            return best;
        }

        // ---- Lane groups ----

        // Lanes 'from'..'to' (indices into leg.Lanes) as a group: all one way.
        public static bool IsGroup(Leg leg, int from, int to)
        {
            if (from < 0 || to >= leg.Lanes.Count || from > to) return false;
            for (int i = from; i <= to; i++) if (leg.Lanes[i].Forward != leg.Lanes[from].Forward) return false;
            return true;
        }

        public static string Describe(Leg leg, int from, int to)
        {
            string lanes = from == to ? "lane " + leg.Lanes[from].Number : "lanes " + leg.Lanes[from].Number + "-" + leg.Lanes[to].Number;
            return lanes + (leg.Lanes[from].Forward ? " (into the junction)" : " (out of the junction)");
        }

        // ---- Road types ----

        private class Candidate
        {
            public NetInfo Info;
            public int Forward, Backward;
            public bool Sidewalks, Highway, Elevated, ElevatedSidewalks;
            public float LaneWidth;
        }

        private static List<Candidate> candidates;
        private static int candidatesCount = -1;

        // Ground road types loaded in the game (vanilla, Workshop and road builder).
        private static List<Candidate> Candidates()
        {
            int count = PrefabCollection<NetInfo>.LoadedCount();
            if (candidates != null && count == candidatesCount) return candidates;
            var list = new List<Candidate>();
            for (uint i = 0; i < count; i++)
            {
                NetInfo info;
                try { info = PrefabCollection<NetInfo>.GetLoaded(i); }
                catch { continue; }
                if (info == null || info.m_netAI == null || info.m_lanes == null || info.m_class == null) continue;
                if (info.m_class.m_service.ToString() != "Road") continue;
                string ai = info.m_netAI.GetType().Name;
                if (!ai.Contains("Road") || ai.Contains("Bridge") || ai.Contains("Tunnel") || ai.Contains("Elevated") || ai.Contains("Slope")) continue;
                var candidate = new Candidate { Info = info };
                float widths = 0f;
                foreach (var lane in info.m_lanes)
                {
                    if ((lane.m_laneType & NetInfo.LaneType.Pedestrian) != NetInfo.LaneType.None) candidate.Sidewalks = true;
                    if (!IsCarLane(lane)) continue;
                    if (lane.m_finalDirection == NetInfo.Direction.Forward) candidate.Forward++;
                    else if (lane.m_finalDirection == NetInfo.Direction.Backward) candidate.Backward++;
                    else { candidate.Forward = -100; break; }   // shared middle lanes: not a clean match
                    widths += lane.m_width;
                }
                if (candidate.Forward < 0 || candidate.Forward + candidate.Backward == 0) continue;
                candidate.LaneWidth = widths / (candidate.Forward + candidate.Backward);
                var rules = info.m_netAI.GetType().GetField("m_highwayRules");
                candidate.Highway = rules != null && rules.GetValue(info.m_netAI) is bool && (bool)rules.GetValue(info.m_netAI);
                var elevated = RoadPlanner.Variant(info, "m_elevatedInfo");
                candidate.Elevated = elevated != null && elevated != info;
                if (candidate.Elevated && elevated.m_lanes != null)
                    foreach (var lane in elevated.m_lanes)
                        if ((lane.m_laneType & NetInfo.LaneType.Pedestrian) != NetInfo.LaneType.None) candidate.ElevatedSidewalks = true;
                list.Add(candidate);
            }
            candidates = list;
            candidatesCount = count;
            return list;
        }

        // The closest road type for a part: same lanes each way, the lane width
        // and overall width nearest. Ground parts keep sidewalks; the raised
        // part needs an elevated version (barriers rather than sidewalks preferred).
        private static Candidate Match(int forward, int backward, bool raised, float laneWidth, float width, out bool reversed)
        {
            reversed = false;
            Candidate best = null;
            float bestScore = float.MaxValue;
            foreach (var c in Candidates())
            {
                bool same = c.Forward == forward && c.Backward == backward;
                bool swapped = c.Forward == backward && c.Backward == forward;
                if (!same && !swapped) continue;
                if (raised ? !c.Elevated : (!c.Sidewalks || c.Highway)) continue;
                float score = Mathf.Abs(c.LaneWidth - laneWidth) * 3f + Mathf.Abs(c.Info.m_halfWidth * 2f - width) * 0.5f;
                if (raised && c.ElevatedSidewalks) score += 4f;
                if (!same) score += 0.1f;   // the same way round when both fit
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                    reversed = !same;
                }
            }
            return best;
        }

        // ---- Planning ----

        public static GradePlan Make(ushort junction, Leg approach, Leg far, int from, int to, float distance, float height)
        {
            var plan = new GradePlan
            {
                Junction = junction, Approach = approach, Far = far,
                Height = Mathf.Clamp(height, MinHeight, MaxHeight),
            };
            if (far == null)
            {
                plan.Problems.Add("No road straight across the junction (within " + StraightAcross + " degrees) for the raised lanes to land on");
                return plan;
            }
            if (!IsGroup(approach, from, to))
            {
                plan.Problems.Add("Pick lanes that all go the same way");
                return plan;
            }
            if (from == 0 && to == approach.Lanes.Count - 1)
            {
                plan.Problems.Add("Leave at least one lane on the ground for the junction");
                return plan;
            }

            if (!LayOut(plan, approach, from, to))
            {
                plan.Problems.Add(Suggest(approach, from, to));
                return plan;
            }

            Vector3 junctionPosition = NetManager.instance.m_nodes.m_buffer[junction].m_position;
            float span = SpanHalf(junction, approach, far, plan);
            float converge = Converge(plan);
            plan.MaxDistance = Mathf.Min(MaxDistance, Mathf.Min(approach.Length, far.Length) - 2f);
            plan.MinDistance = MinimumDistance(plan, junctionPosition.y, span);
            if (plan.MinDistance > plan.MaxDistance)
            {
                plan.Problems.Add("The roads either side are too short: the ramps need " + plan.MinDistance.ToString("F0") +
                                  " m each side at " + (Grade * 100f).ToString("F0") + " %, there are " + plan.MaxDistance.ToString("F0") +
                                  " m before the next junction. Lower the deck.");
                plan.Distance = plan.MaxDistance;
                return plan;
            }
            plan.Distance = Mathf.Clamp(distance, plan.MinDistance, plan.MaxDistance);

            Cut(approach, plan.Distance, plan.ApproachSegments, out plan.ApproachT, out plan.ApproachCut, ref plan.Distance);
            float farDistance = plan.Distance;
            Cut(far, farDistance, plan.FarSegments, out plan.FarT, out plan.FarCut, ref farDistance);

            // The centre line in the approach frame: approach cut -> junction -> far cut.
            var aSlice = CurveMath.Slice(approach.Path, 0f, plan.Distance);
            aSlice.Reverse();
            var fSlice = CurveMath.Slice(far.Path, 0f, farDistance);
            var centre = new List<Vector3>(aSlice);
            for (int i = 1; i < fSlice.Count; i++) centre.Add(fSlice[i]);
            float total = CurveMath.Length(centre);
            float atJunction = CurveMath.Length(aSlice);

            foreach (var part in plan.Parts)
            {
                if (part.Raised)
                    BuildRaised(plan, part, centre, total, atJunction, span, junctionPosition.y);
                else
                {
                    BuildGround(plan, part, aSlice, converge, GradeEnd.ApproachCut, GradeEnd.Junction, "approach");
                    // The far side mirrors it, the same way round (junction -> far cut).
                    BuildGround(plan, part, fSlice, converge, GradeEnd.Junction, GradeEnd.FarCut, "far side", true);
                }
            }
            var raisedPart = plan.Parts.Find(p => p.Raised);
            plan.Peak = Offset(centre, atJunction, raisedPart.Offset);
            plan.Peak.y = junctionPosition.y + plan.Height;
            if (2f * span > MaxSpan)
                plan.Warnings.Add("The deck spans " + (2f * span).ToString("F0") + " m over the junction");
            return plan;
        }

        // Splits the leg's lanes into left / raised / right, matches each to a
        // road type and places them side by side. False if a part has no match.
        private static bool LayOut(GradePlan plan, Leg leg, int from, int to)
        {
            var lanes = leg.Lanes;
            float edge = leg.Info.m_halfWidth;
            var ranges = new List<KeyValuePair<int, int>>();
            if (from > 0) ranges.Add(new KeyValuePair<int, int>(0, from - 1));
            ranges.Add(new KeyValuePair<int, int>(from, to));
            if (to < lanes.Count - 1) ranges.Add(new KeyValuePair<int, int>(to + 1, lanes.Count - 1));

            var extents = new List<float[]>();
            foreach (var range in ranges)
            {
                bool raised = range.Key == from;
                var part = new GradePart { Name = raised ? "raised" : range.Key == 0 ? "left" : "right", Raised = raised };
                float width = 0f;
                for (int i = range.Key; i <= range.Value; i++)
                {
                    if (lanes[i].Forward) part.Forward++; else part.Backward++;
                    width += lanes[i].Width;
                }
                // The part's share of the road: from boundary to boundary between
                // lane groups, out to the road's edge (with the sidewalk) for outer ones.
                float left = range.Key == 0 ? -edge : Boundary(lanes[range.Key - 1], lanes[range.Key]);
                float right = range.Value == lanes.Count - 1 ? edge : Boundary(lanes[range.Value], lanes[range.Value + 1]);
                extents.Add(new[] { left, right });
                bool reversed;
                var match = Match(part.Forward, part.Backward, raised, width / (range.Value - range.Key + 1), right - left, out reversed);
                if (match == null) return false;
                part.Info = match.Info;
                part.Reversed = reversed;
                plan.Parts.Add(part);
            }

            // Side by side: the raised part where its lanes were, the others
            // pushed out until they clear it.
            int r = plan.Parts.FindIndex(p => p.Raised);
            var raisedPart = plan.Parts[r];
            raisedPart.Offset = (extents[r][0] + extents[r][1]) * 0.5f;
            float raisedHalf = Deck(raisedPart.Info).m_halfWidth;
            for (int i = 0; i < plan.Parts.Count; i++)
            {
                if (i == r) continue;
                var part = plan.Parts[i];
                float natural = (extents[i][0] + extents[i][1]) * 0.5f;
                float clear = raisedHalf + part.Info.m_halfWidth + PartGap;
                part.Offset = i < r ? Mathf.Min(natural, raisedPart.Offset - clear) : Mathf.Max(natural, raisedPart.Offset + clear);
            }
            if (plan.Parts.Count == 2)
                plan.Warnings.Add("The raised lanes are at the road's edge: the sidewalk on that side stops at the split");
            return true;
        }

        private static float Boundary(LegLane a, LegLane b) => (a.Lateral + a.Width * 0.5f + b.Lateral - b.Width * 0.5f) * 0.5f;

        // The raised part's deck: the elevated version of its road type.
        public static NetInfo Deck(NetInfo info) => RoadPlanner.Variant(info, "m_elevatedInfo") ?? info;

        // Why it can't be built, and which lane groups on this leg would work.
        private static string Suggest(Leg leg, int from, int to)
        {
            var working = new List<string>();
            int n = leg.Lanes.Count;
            for (int a = 0; a < n && working.Count < 4; a++)
                for (int b = a; b < n && working.Count < 4; b++)
                {
                    if ((a == from && b == to) || (a == 0 && b == n - 1) || !IsGroup(leg, a, b)) continue;
                    var test = new GradePlan();
                    if (LayOut(test, leg, a, b)) working.Add(Describe(leg, a, b));
                }
            string why = "No loaded road type fits every part of this split (" + Describe(leg, from, to) +
                         " raised; ground parts need a road with sidewalks and the same lanes each way, the raised part one with an elevated version)";
            return working.Count > 0 ? why + ". These would work: " + string.Join("; ", working.ToArray())
                                     : why + ". No other lane group on this road works with the loaded road types either.";
        }

        // Half the deck's span over the junction: past the crossing roads' edges
        // plus room for the pillar, and past where the ground parts come back
        // together (so no pillar stands on them).
        private static float SpanHalf(ushort junction, Leg approach, Leg far, GradePlan plan)
        {
            float half = 8f;
            var net = NetManager.instance;
            var node = net.m_nodes.m_buffer[junction];
            for (int i = 0; i < 8; i++)
            {
                ushort segment = node.GetSegment(i);
                if (segment == 0 || segment == approach.First || segment == far.First) continue;
                var info = net.m_segments.m_buffer[segment].Info;
                if (info == null) continue;
                Vector3 away = CurveMath.FlatNormal(net.m_segments.m_buffer[segment].m_startNode == junction
                    ? net.m_segments.m_buffer[segment].m_startDirection : net.m_segments.m_buffer[segment].m_endDirection);
                float skew = Mathf.Max(Mathf.Abs(away.x * approach.Direction.z - away.z * approach.Direction.x), 0.35f);
                half = Mathf.Max(half, info.m_halfWidth / skew + RoadPlanner.PillarClearance);
            }
            return Mathf.Max(half, Converge(plan) + RoadPlanner.PillarClearance);
        }

        // How long the ground parts take to come back together at the junction.
        private static float Converge(GradePlan plan)
        {
            float widest = 0f;
            foreach (var part in plan.Parts) if (!part.Raised) widest = Mathf.Max(widest, Mathf.Abs(part.Offset));
            return Mathf.Clamp(widest * 4f, 16f, 36f);
        }

        // Shortest split distance with the ramps at the road grade on both sides.
        private static float MinimumDistance(GradePlan plan, float junctionHeight, float span)
        {
            float top = junctionHeight + plan.Height;
            for (float d = span + SideBySide + 10f; d <= MaxDistance + 0.1f; d += 5f)
            {
                if (d > plan.MaxDistance) break;
                Vector3 a, f, t;
                CurveMath.PointAt(plan.Approach.Path, d, out a, out t);
                CurveMath.PointAt(plan.Far.Path, d, out f, out t);
                if (RampGrade(top - a.y, d - span) <= Grade && RampGrade(top - f.y, d - span) <= Grade) return d;
            }
            return float.MaxValue;
        }

        private static float RampGrade(float rise, float length)
        {
            float v = Mathf.Min(VerticalCurve, length * 0.25f);
            return length - v > 1f ? Mathf.Abs(rise) / (length - v) : float.MaxValue;
        }

        // Height 'u' metres along a ramp of 'length' from h0 to h1: a straight
        // grade with rounded ends.
        private static float Ramp(float h0, float h1, float length, float u)
        {
            float v = Mathf.Min(VerticalCurve, length * 0.25f);
            float g = (h1 - h0) / Mathf.Max(length - v, 1f);
            u = Mathf.Clamp(u, 0f, length);
            if (u < v) return h0 + g * u * u / (2f * v);
            if (u < length - v) return h0 + g * (u - v * 0.5f);
            return h1 - g * (length - u) * (length - u) / (2f * v);
        }

        // Where the leg is cut: the segments to remove and the cut on the last
        // one. Near a node the node itself is used ('distance' moves to it).
        private static void Cut(Leg leg, float distance, List<ushort> segments, out float t, out Vector3 cut, ref float actual)
        {
            var net = NetManager.instance;
            var nodes = net.m_nodes.m_buffer;
            ushort from = leg.Junction;
            float travelled = 0f;
            t = -1f;
            cut = Vector3.zero;
            foreach (ushort segment in leg.Segments)
            {
                var s = net.m_segments.m_buffer[segment];
                ushort to = s.m_startNode == from ? s.m_endNode : s.m_startNode;
                float length = Mathf.Max(s.m_averageLength, 0.1f);
                segments.Add(segment);
                if (travelled + length + CutSnap >= distance)
                {
                    float into = distance - travelled;
                    if (into >= length - CutSnap)
                    {
                        // At (or just short of) the far node: remove the segment whole.
                        actual = travelled + length;
                        cut = nodes[to].m_position;
                        return;
                    }
                    // Just past the previous node: a little further on, so the
                    // piece left between them isn't a sliver (the ramps only get gentler).
                    if (into < CutSnap) distance = travelled + CutSnap;
                    Vector3 point, tangent;
                    CurveMath.PointAt(leg.Path, distance, out point, out tangent);
                    float along = CrossingFinder.ClosestT(segment, point, out cut);
                    t = Mathf.Clamp(along, 0.02f, 0.98f);
                    actual = distance;
                    return;
                }
                travelled += length;
                from = to;
            }
            actual = travelled;
            cut = nodes[from].m_position;
        }

        // A point 'offset' metres right of the line at 'distance' along it (y = the line's height).
        private static Vector3 Offset(List<Vector3> line, float distance, float offset)
        {
            Vector3 point, tangent;
            CurveMath.PointAt(line, distance, out point, out tangent);
            Vector3 right = -CurveMath.Left(CurveMath.FlatNormal(tangent));
            return point + right * offset;
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        // The raised part: from the approach cut, up the ramp beside the ground
        // parts, level over the junction, down to the far cut.
        private static void BuildRaised(GradePlan plan, GradePart part, List<Vector3> centre, float total, float atJunction,
            float span, float junctionHeight)
        {
            float top = junctionHeight + plan.Height;
            float upEnd = atJunction - span, downStart = atJunction + span;
            float h0 = centre[0].y, h1 = centre[centre.Count - 1].y;
            Func<float, float> offsetAt = s => part.Offset * Smooth(Mathf.Min(s, total - s) / SideBySide);
            Func<float, float> heightAt = s =>
                s <= upEnd ? Ramp(h0, top, upEnd, s) : s >= downStart ? Ramp(top, h1, total - downStart, s - downStart) : top;

            var at = new List<float> { 0f };
            AddEvenly(at, 0f, upEnd, RaisedSpacing);
            AddEvenly(at, upEnd, downStart, 1000f);
            AddEvenly(at, downStart, total, RaisedSpacing);

            var points = new List<Vector3>();
            var tangents = new List<Vector3>();
            var kinds = new NodeKind[at.Count];
            var terrain = TerrainManager.instance;
            for (int i = 0; i < at.Count; i++)
            {
                float s = at[i];
                Vector3 p = Offset(centre, s, offsetAt(s));
                p.y = heightAt(s);
                points.Add(p);
                Vector3 ahead = Offset(centre, Mathf.Min(s + 0.5f, total), offsetAt(Mathf.Min(s + 0.5f, total)));
                Vector3 behind = Offset(centre, Mathf.Max(s - 0.5f, 0f), offsetAt(Mathf.Max(s - 0.5f, 0f)));
                tangents.Add(CurveMath.FlatNormal(ahead - behind));
                float above = p.y - terrain.SampleRawHeightSmooth(p);
                kinds[i] = i == 0 || i == at.Count - 1 || above < 3f ? NodeKind.Ground : NodeKind.Elevated;
            }
            for (float s = 0f; s <= total; s += 4f)
            {
                Vector3 p = Offset(centre, s, offsetAt(s));
                p.y = heightAt(s);
                part.Preview.Add(p);
            }
            AddPiece(plan, part, points, tangents, kinds, GradeEnd.ApproachCut, GradeEnd.FarCut, "raised lanes");
        }

        // A ground part from one end to the other along 'line' (its own
        // direction): moves out to its place after the split, comes back to
        // the junction node at the end.
        private static void BuildGround(GradePlan plan, GradePart part, List<Vector3> line, float converge,
            GradeEnd start, GradeEnd end, string where, bool junctionFirst = false)
        {
            float total = CurveMath.Length(line);
            // Distance from the split (not from the junction) decides moving out.
            Func<float, float> offsetAt = s =>
            {
                float fromSplit = junctionFirst ? total - s : s;
                float fromJunction = junctionFirst ? s : total - s;
                return part.Offset * Smooth(fromSplit / SideBySide) * Smooth(fromJunction / converge);
            };
            var at = new List<float> { 0f };
            float near = junctionFirst ? converge : total - converge;
            float far = junctionFirst ? total - SideBySide : SideBySide;
            float lo = Mathf.Min(near, far), hi = Mathf.Max(near, far);
            if (lo > 4f) AddEvenly(at, 0f, lo, GroundSpacing);
            if (hi - lo > 4f) AddEvenly(at, lo, hi, GroundSpacing);
            AddEvenly(at, Mathf.Max(hi, at[at.Count - 1]), total, GroundSpacing);
            if (at[at.Count - 1] < total - 0.5f) at.Add(total);

            var points = new List<Vector3>();
            var tangents = new List<Vector3>();
            var kinds = new NodeKind[at.Count];
            foreach (float s in at)
            {
                points.Add(Offset(line, s, offsetAt(s)));
                Vector3 ahead = Offset(line, Mathf.Min(s + 0.5f, total), offsetAt(Mathf.Min(s + 0.5f, total)));
                Vector3 behind = Offset(line, Mathf.Max(s - 0.5f, 0f), offsetAt(Mathf.Max(s - 0.5f, 0f)));
                tangents.Add(CurveMath.FlatNormal(ahead - behind));
            }
            for (float s = 0f; s <= total; s += 4f) part.Preview.Add(Offset(line, s, offsetAt(s)));
            AddPiece(plan, part, points, tangents, kinds, start, end, part.Name + " lanes, " + where);
        }

        // Adds stops after 'from' up to and including 'to', at most 'spacing' apart.
        private static void AddEvenly(List<float> at, float from, float to, float spacing)
        {
            if (to - from < 0.5f) return;
            int pieces = Mathf.Max(1, Mathf.CeilToInt((to - from) / spacing - 0.15f));
            for (int k = 1; k <= pieces; k++) at.Add(from + (to - from) * k / pieces);
        }

        private static void AddPiece(GradePlan plan, GradePart part, List<Vector3> points, List<Vector3> tangents, NodeKind[] kinds,
            GradeEnd start, GradeEnd end, string what)
        {
            if (part.Reversed)
            {
                // Built the other way round so its lanes run the right way.
                points.Reverse();
                tangents.Reverse();
                for (int i = 0; i < tangents.Count; i++) tangents[i] = -tangents[i];
                Array.Reverse(kinds);
                GradeEnd swap = start; start = end; end = swap;
            }
            var roadPlan = new Plan
            {
                Points = points,
                Tangents = tangents,
                Kinds = kinds,
                Middle = new bool[points.Count],
            };
            plan.Pieces.Add(new GradePiece { Info = part.Info, Plan = roadPlan, Start = start, End = end, What = what });
        }
    }
}
