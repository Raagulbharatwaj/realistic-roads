using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RealisticRoads
{
    // What a node of the new road is built as.
    public enum NodeKind
    {
        Ground,
        Elevated,   // above land: elevated version, pillars
        Bridge,     // above water, or over a lowered road: bridge version
        Cut,        // below the ground in an open cutting: normal version, the game digs the terrain
        Slope,      // tunnel ramp: slope version (walls, portal)
        Tunnel,     // deep underground: tunnel version
    }

    // A stretch of water the curve crosses, with the player's extra height.
    public class WaterRun
    {
        public float From, To;      // metres along the curve
        public float Extra;         // +/- from the player
        public int Key;             // stable id for remembering Extra while the curve changes
        public Vector3 Middle;
    }

    // An existing road lowered into a cutting for an overpass.
    public class Lowering
    {
        public ushort Segment;
        public float[] SplitAt;            // positions along the segment for new nodes (0..1)
        public float[] SplitHeights;       // their lowered heights
        public List<ushort> Nodes = new List<ushort>();       // existing nodes to move down
        public List<float> NodeHeights = new List<float>();
    }

    // Everything needed to build a curve: node positions and heights, what
    // each node is, the crossings and what happens at them, and any problems.
    public class Plan
    {
        public List<Vector3> Points;          // y = planned height
        public List<Vector3> Tangents;
        public NodeKind[] Kinds;
        public bool[] Middle;                 // suspension bridges: the node between two towers
        public List<Crossing> Crossings = new List<Crossing>();
        public List<int> CrossingIndex = new List<int>();   // node index of each crossing
        public Dictionary<int, Lowering> Lowerings = new Dictionary<int, Lowering>();  // by crossing number
        public List<WaterRun> WaterRuns = new List<WaterRun>();
        public List<string> Problems = new List<string>();
        public ushort StartNode, EndNode;
        public float StripHalfWidth;          // lane roads: squeeze to this half width (0 = a normal road)
        public List<ushort> LaneNodes = new List<ushort>();  // road ends the lane road starts/ends on
        public bool Ok => Problems.Count == 0;
    }

    // Main thread: works out heights and road versions for a curve.
    public static class RoadPlanner
    {
        public const float MaxGrade = 0.06f;          // ramps: 6 % slope
        public const float BridgeClearance = 10f;     // above water (before the player's +/-)
        public const float OverClearance = 9f;        // flyover deck above the road/rail below
        public const float UnderClearance = 7f;       // headroom under an elevated road we pass beneath
        public const float TunnelDepth = 12f;         // tunnel under the existing road
        public const float CutDepth = 8f;             // open cutting under a short bridge
        public const float TunnelMinDepth = 6f;       // shallower than this a tunnel is a ramp (slope)
        private const float Unlimited = 100000f;

        public static Plan Make(NetInfo info, List<Vector3> points, List<Vector3> tangents,
            List<Crossing> crossings, List<int> crossingIndex, List<WaterRun> waterRuns, ushort startNode, ushort endNode)
        {
            var plan = new Plan
            {
                Points = new List<Vector3>(points),
                Tangents = new List<Vector3>(tangents),
                Crossings = crossings,
                CrossingIndex = crossingIndex,
                WaterRuns = waterRuns,
                StartNode = startNode,
                EndNode = endNode,
            };
            int n = points.Count;
            plan.Kinds = new NodeKind[n];
            plan.Middle = new bool[n];
            if (n < 2 || info == null) return plan;

            var terrain = TerrainManager.instance;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var distance = new float[n];
            var ground = new float[n];
            var water = new bool[n];
            var minH = new float[n];
            var maxH = new float[n];
            var isFixed = new bool[n];
            var cutting = new bool[n];      // lowered for an underpass: never a tunnel
            var forceBridge = new bool[n];  // deck over a lowered road (overpass)

            bool hasBridge = Variant(info, "m_bridgeInfo") != null;
            bool hasElevated = Variant(info, "m_elevatedInfo") != null || hasBridge;

            for (int i = 0; i < n; i++)
            {
                if (i > 0) distance[i] = distance[i - 1] + CurveMath.FlatDistance(points[i - 1], points[i]);
                ground[i] = terrain.SampleRawHeightSmooth(points[i]);
                float surface = terrain.SampleRawHeightSmoothWithWater(points[i], false, 0f);
                water[i] = surface - ground[i] > 0.5f;
                minH[i] = water[i] && hasBridge ? surface + BridgeClearance + WaterExtra(waterRuns, distance[i]) : -Unlimited;
                maxH[i] = Unlimited;
            }

            if (NetworkBuilder.NodeExists(startNode)) Fix(0, nodes[startNode].m_position.y, minH, maxH, isFixed);
            if (NetworkBuilder.NodeExists(endNode)) Fix(n - 1, nodes[endNode].m_position.y, minH, maxH, isFixed);

            for (int c = 0; c < crossings.Count; c++)
            {
                var crossing = crossings[c];
                int k = crossingIndex[c];
                float window = crossing.ExistingHalfWidth + info.m_halfWidth + 2f;
                switch (crossing.Type)
                {
                    case CrossingType.Junction:
                        if (crossing.ExistingAtGround) Fix(k, crossing.Point.y, minH, maxH, isFixed);
                        break;
                    case CrossingType.Bridge:
                        Limit(distance, k, window, crossing.Point.y + OverClearance + crossing.ExtraHeight, true, minH);
                        break;
                    case CrossingType.Overpass:
                        // We stay at ground level on a bridge deck; the existing road goes down.
                        Fix(k, crossing.Point.y, minH, maxH, isFixed);
                        Mark(distance, k, window, forceBridge);
                        string problem;
                        var lowering = PlanLowering(crossing, info.m_halfWidth, out problem);
                        if (lowering != null) plan.Lowerings[c] = lowering;
                        else plan.Problems.Add(problem);
                        break;
                    case CrossingType.Underpass:
                        Limit(distance, k, window, crossing.Point.y - CutDepth, false, maxH);
                        Mark(distance, k, window * 4f, cutting);
                        break;
                    case CrossingType.Tunnel:
                        bool underElevated = crossing.ExistingElevation > UnderClearance;
                        Limit(distance, k, window, crossing.Point.y - (underElevated ? UnderClearance : TunnelDepth), false, maxH);
                        break;
                }
            }

            float[] heights;
            bool solved = CurveMath.SolveHeights(distance, ground, minH, maxH, isFixed, MaxGrade, out heights);
            // Level the bridges between their banks, then solve again with that.
            var levelled = (float[])minH.Clone();
            if (CurveMath.LevelSpans(distance, ground, heights, isFixed, levelled))
            {
                float[] again;
                bool solvedAgain = CurveMath.SolveHeights(distance, ground, levelled, maxH, isFixed, MaxGrade, out again);
                if (solvedAgain || !solved)
                {
                    heights = again;
                    solved = solvedAgain;
                }
            }
            if (!solved)
                plan.Problems.Add("Not enough room to ramp up/down at " + MaxGrade * 100f + "% slope: spread the points out or move the crossing away from the ends");

            for (int i = 0; i < n; i++)
            {
                var p = plan.Points[i];
                p.y = heights[i];
                plan.Points[i] = p;

                float elevation = heights[i] - ground[i];
                if (forceBridge[i] && elevation > -1f) plan.Kinds[i] = NodeKind.Bridge;
                else if (elevation >= 1f) plan.Kinds[i] = water[i] && hasBridge ? NodeKind.Bridge : NodeKind.Elevated;
                else if (elevation <= -1f)
                    plan.Kinds[i] = cutting[i] ? NodeKind.Cut : elevation <= -TunnelMinDepth ? NodeKind.Tunnel : NodeKind.Slope;
                else plan.Kinds[i] = NodeKind.Ground;
            }

            if (IsDoubleLength(info)) MarkMiddles(plan);

            CheckVariants(info, plan, hasElevated);
            CheckUnderground(info, plan, water, ground);
            CheckCrossings(plan);
            return plan;
        }

        // The road's version for a node kind, or null if it has none.
        public static NetInfo InfoFor(NetInfo info, NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Bridge: return Variant(info, "m_bridgeInfo") ?? Variant(info, "m_elevatedInfo");
                case NodeKind.Elevated: return Variant(info, "m_elevatedInfo") ?? Variant(info, "m_bridgeInfo");
                case NodeKind.Slope: return Variant(info, "m_slopeInfo");
                case NodeKind.Tunnel: return Variant(info, "m_tunnelInfo");
                default: return info;
            }
        }

        // The version for a segment between two node kinds.
        public static NetInfo SegmentInfo(NetInfo info, NodeKind a, NodeKind b)
        {
            if (a == NodeKind.Bridge || b == NodeKind.Bridge) return InfoFor(info, NodeKind.Bridge);
            if (a == NodeKind.Elevated || b == NodeKind.Elevated) return InfoFor(info, NodeKind.Elevated);
            if (a == NodeKind.Tunnel && b == NodeKind.Tunnel) return InfoFor(info, NodeKind.Tunnel);
            if (a == NodeKind.Tunnel || b == NodeKind.Tunnel || a == NodeKind.Slope || b == NodeKind.Slope)
                return InfoFor(info, NodeKind.Slope);
            return info;
        }

        // Suspension bridges (the vanilla six-lane road's, for one) are made
        // for a tower on every other node, with a plain "middle" node between
        // two towers where the cables hang lowest. The game's own tool builds
        // them that way; evenly spaced towers on every node look broken.
        public static bool IsDoubleLength(NetInfo info)
        {
            var bridge = Variant(info, "m_bridgeInfo");
            if (bridge == null || bridge.m_netAI == null) return false;
            var f = bridge.m_netAI.GetType().GetField("m_doubleLength", BindingFlags.Public | BindingFlags.Instance);
            return f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(bridge.m_netAI);
        }

        // Every second node of each bridge run is a middle node; the run's
        // first and last nodes keep their towers.
        private static void MarkMiddles(Plan plan)
        {
            int n = plan.Kinds.Length;
            int i = 0;
            while (i < n)
            {
                if (plan.Kinds[i] != NodeKind.Bridge) { i++; continue; }
                int first = i;
                while (i < n && plan.Kinds[i] == NodeKind.Bridge) i++;
                for (int k = first + 1; k < i - 1; k += 2) plan.Middle[k] = true;
            }
        }

        // A road's other version (m_elevatedInfo, m_bridgeInfo, m_tunnelInfo,
        // m_slopeInfo on roads, tracks and paths), or null.
        public static NetInfo Variant(NetInfo info, string field)
        {
            if (info == null || info.m_netAI == null) return null;
            var f = info.m_netAI.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            return f != null ? f.GetValue(info.m_netAI) as NetInfo : null;
        }

        private static float WaterExtra(List<WaterRun> runs, float at)
        {
            if (runs == null) return 0f;
            foreach (var run in runs)
                if (at >= run.From - 1f && at <= run.To + 1f) return run.Extra;
            return 0f;
        }

        // --- overpass: lowering the existing road -------------------------------

        // How to lower the existing road under an overpass: cut its segment
        // around the crossing, drop that part by CutDepth and ramp it back up
        // along the road on both sides. Null (with a reason) if the road has a
        // junction too close or runs over water.
        private static Lowering PlanLowering(Crossing crossing, float ourHalfWidth, out string problem)
        {
            problem = null;
            var net = NetManager.instance;
            var segment = net.m_segments.m_buffer[crossing.Segment];
            float length = Mathf.Max(segment.m_averageLength, 1f);
            float span = ourHalfWidth + 6f;
            float t0 = Mathf.Clamp(crossing.ExistingT - span / length, 0.02f, 0.96f);
            float t1 = Mathf.Clamp(crossing.ExistingT + span / length, 0.04f, 0.98f);
            float tx = Mathf.Clamp(crossing.ExistingT, t0 + 0.01f, t1 - 0.01f);
            float low = crossing.Point.y - CutDepth;
            float ramp = CutDepth / MaxGrade + 10f;

            // Existing nodes along the road on both sides, until the ramp fits.
            var back = WalkChain(crossing.Segment, segment.m_startNode, crossing.ExistingT * length, ramp, out problem);
            if (back == null) { problem = problem + " on the " + crossing.ExistingName; return null; }
            var forward = WalkChain(crossing.Segment, segment.m_endNode, (1f - crossing.ExistingT) * length, ramp, out problem);
            if (forward == null) { problem = problem + " on the " + crossing.ExistingName; return null; }

            // One line of nodes: far back ... start, split points, end ... far forward.
            var ids = new List<ushort>();
            var along = new List<float>();
            var current = new List<float>();
            var fixedEnd = new List<bool>();
            for (int i = back.Count - 1; i >= 0; i--)
            {
                ids.Add(back[i].Key); along.Add(-back[i].Value);
                current.Add(net.m_nodes.m_buffer[back[i].Key].m_position.y);
                fixedEnd.Add(i == back.Count - 1 && back.Count > 0 && LastIsFixed(back));
            }
            int firstSplit = ids.Count;
            float[] splitT = { t0, tx, t1 };
            foreach (float t in splitT)
            {
                ids.Add(0);
                along.Add((t - crossing.ExistingT) * length);
                current.Add(crossing.Point.y);
                fixedEnd.Add(false);
            }
            for (int i = 0; i < forward.Count; i++)
            {
                ids.Add(forward[i].Key); along.Add(forward[i].Value);
                current.Add(net.m_nodes.m_buffer[forward[i].Key].m_position.y);
                fixedEnd.Add(i == forward.Count - 1 && LastIsFixed(forward));
            }

            int m = ids.Count;
            var minH = new float[m];
            var maxH = new float[m];
            var isFixed = new bool[m];
            for (int i = 0; i < m; i++)
            {
                minH[i] = -Unlimited;
                maxH[i] = i >= firstSplit && i < firstSplit + 3 ? low : Unlimited;
                if (fixedEnd[i]) { minH[i] = maxH[i] = current[i]; isFixed[i] = true; }
            }
            float[] heights;
            if (!CurveMath.SolveHeights(along.ToArray(), current.ToArray(), minH, maxH, isFixed, MaxGrade, out heights))
            {
                problem = "Not enough room to lower the " + crossing.ExistingName;
                return null;
            }

            var lowering = new Lowering
            {
                Segment = crossing.Segment,
                SplitAt = splitT,
                SplitHeights = new[] { heights[firstSplit], heights[firstSplit + 1], heights[firstSplit + 2] },
            };
            var terrain = TerrainManager.instance;
            for (int i = 0; i < m; i++)
            {
                if (ids[i] == 0 || heights[i] > current[i] - 0.5f) continue;
                Vector3 p = net.m_nodes.m_buffer[ids[i]].m_position;
                if (terrain.SampleRawHeightSmoothWithWater(p, false, 0f) - terrain.SampleRawHeightSmooth(p) > 0.5f)
                {
                    problem = "Can't lower the " + crossing.ExistingName + ": there is water";
                    return null;
                }
                lowering.Nodes.Add(ids[i]);
                lowering.NodeHeights.Add(heights[i]);
            }
            return lowering;
        }

        // Nodes along an existing road from 'node' (reached by 'segment',
        // 'distance' metres from the crossing) until 'reach' metres. Each entry:
        // node id and its distance. Null if a junction comes first.
        private static List<KeyValuePair<ushort, float>> WalkChain(ushort segment, ushort node, float distance, float reach, out string problem)
        {
            problem = null;
            var net = NetManager.instance;
            var chain = new List<KeyValuePair<ushort, float>>();
            for (int guard = 0; guard < 64; guard++)
            {
                chain.Add(new KeyValuePair<ushort, float>(node, distance));
                if (distance >= reach) return chain;
                int count = NetworkBuilder.SegmentCount(node);
                if (count == 1) return chain;  // dead end: free to lower
                if (count != 2)
                {
                    problem = "A junction is too close (needs ~" + reach.ToString("F0") + " m of plain road each side)";
                    return null;
                }
                ushort next = 0;
                for (int i = 0; i < 8; i++)
                {
                    ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (s != 0 && s != segment) next = s;
                }
                var seg = net.m_segments.m_buffer[next];
                distance += seg.m_averageLength;
                node = seg.m_startNode == node ? seg.m_endNode : seg.m_startNode;
                segment = next;
            }
            problem = "Road too fragmented to lower";
            return null;
        }

        // The last node of a walk stays where it is unless the road ends there.
        private static bool LastIsFixed(List<KeyValuePair<ushort, float>> chain)
        {
            return NetworkBuilder.SegmentCount(chain[chain.Count - 1].Key) != 1;
        }

        // --- helpers -------------------------------------------------------------

        private static void Fix(int i, float height, float[] minH, float[] maxH, bool[] isFixed)
        {
            minH[i] = maxH[i] = height;
            isFixed[i] = true;
        }

        // Applies a height limit to every node within 'window' metres of node k.
        private static void Limit(float[] distance, int k, float window, float height, bool isMin, float[] limits)
        {
            for (int i = 0; i < distance.Length; i++)
            {
                if (i != k && Mathf.Abs(distance[i] - distance[k]) > window) continue;
                limits[i] = isMin ? Mathf.Max(limits[i], height) : Mathf.Min(limits[i], height);
            }
            // The pieces on both sides of the crossing must be level over it too.
            if (k > 0) limits[k - 1] = isMin ? Mathf.Max(limits[k - 1], height) : Mathf.Min(limits[k - 1], height);
            if (k < limits.Length - 1) limits[k + 1] = isMin ? Mathf.Max(limits[k + 1], height) : Mathf.Min(limits[k + 1], height);
        }

        private static void Mark(float[] distance, int k, float window, bool[] marks)
        {
            for (int i = 0; i < distance.Length; i++)
                if (Mathf.Abs(distance[i] - distance[k]) <= window) marks[i] = true;
            if (k > 0) marks[k - 1] = true;
            if (k < marks.Length - 1) marks[k + 1] = true;
        }

        private static void CheckVariants(NetInfo info, Plan plan, bool hasElevated)
        {
            bool elevated = false, tunnel = false;
            foreach (var kind in plan.Kinds)
            {
                if (kind == NodeKind.Elevated || kind == NodeKind.Bridge) elevated = true;
                if (kind == NodeKind.Tunnel || kind == NodeKind.Slope) tunnel = true;
            }
            if (elevated && !hasElevated) plan.Problems.Add(info.name + " has no elevated/bridge version");
            if (tunnel && (Variant(info, "m_tunnelInfo") == null || Variant(info, "m_slopeInfo") == null))
                plan.Problems.Add(info.name + " has no tunnel version");
        }

        // Going down: no water, and nothing else underground in the way.
        private static void CheckUnderground(NetInfo info, Plan plan, bool[] water, float[] ground)
        {
            var samples = new Vector3[9];
            var segments = NetManager.instance.m_segments.m_buffer;
            var checkedSegments = new HashSet<ushort>();
            foreach (var crossing in plan.Crossings) checkedSegments.Add(crossing.Segment);

            for (int i = 0; i < plan.Points.Count; i++)
            {
                var kind = plan.Kinds[i];
                if (kind != NodeKind.Cut && kind != NodeKind.Tunnel && kind != NodeKind.Slope) continue;
                if (water[i])
                {
                    plan.Problems.Add("Can't go under here: there is water");
                    return;
                }

                // Other networks near this lowered node and at about the same depth.
                Vector3 p = plan.Points[i];
                foreach (var segment in NearbySegments(p, info.m_halfWidth + 40f))
                {
                    if (checkedSegments.Contains(segment)) continue;
                    ushort a = segments[segment].m_startNode, b = segments[segment].m_endNode;
                    if (a == plan.StartNode || b == plan.StartNode || a == plan.EndNode || b == plan.EndNode) continue;
                    CrossingFinder.SampleSegment(segment, samples);
                    float reach = info.m_halfWidth + segments[segment].Info.m_halfWidth + 2f;
                    foreach (var q in samples)
                    {
                        if (CurveMath.FlatDistance(p, q) < reach && Mathf.Abs(q.y - p.y) < UnderClearance &&
                            q.y < ground[i] - 1f)
                        {
                            plan.Problems.Add("Too close to " + segments[segment].Info.name + " underground (" +
                                              Mathf.Abs(q.y - p.y).ToString("F0") + " m apart)");
                            return;
                        }
                    }
                }
            }
        }

        // Passing over or under something without connecting needs headroom.
        private static void CheckCrossings(Plan plan)
        {
            for (int c = 0; c < plan.Crossings.Count; c++)
            {
                var crossing = plan.Crossings[c];
                if (crossing.Connects || crossing.Type == CrossingType.Overpass || crossing.Type == CrossingType.Underpass) continue;
                float gap = Mathf.Abs(plan.Points[plan.CrossingIndex[c]].y - crossing.Point.y);
                if (gap < UnderClearance - 0.5f)
                    plan.Problems.Add("Only " + gap.ToString("F0") + " m from " + crossing.ExistingName +
                                      ": pick a bridge or tunnel for this crossing");
            }
        }

        private static IEnumerable<ushort> NearbySegments(Vector3 p, float radius)
        {
            var net = NetManager.instance;
            int x0 = Cell(p.x - radius), x1 = Cell(p.x + radius), z0 = Cell(p.z - radius), z1 = Cell(p.z + radius);
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                ushort segment = net.m_segmentGrid[z * 270 + x];
                int guard = 0;
                while (segment != 0 && guard++ < 32768)
                {
                    if ((net.m_segments.m_buffer[segment].m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None)
                        yield return segment;
                    segment = net.m_segments.m_buffer[segment].m_nextGridSegment;
                }
            }
        }

        private static int Cell(float coordinate)
        {
            return Mathf.Clamp((int)(coordinate / 64f + 135f), 0, 269);
        }
    }
}
