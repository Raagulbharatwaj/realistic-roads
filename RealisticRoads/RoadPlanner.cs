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
        Bridge,     // above water: bridge version
        Cut,        // below the ground in an open cutting: normal version, the game digs the terrain
        Tunnel,     // deep underground: tunnel version
    }

    // Everything needed to build a curve: node positions and heights, what
    // each node is, the crossings and what happens at them, and any problems.
    public class Plan
    {
        public List<Vector3> Points;          // y = planned height
        public List<Vector3> Tangents;
        public NodeKind[] Kinds;
        public List<Crossing> Crossings = new List<Crossing>();
        public List<int> CrossingIndex = new List<int>();   // node index of each crossing
        public List<string> Problems = new List<string>();
        public ushort StartNode, EndNode;
        public bool Ok => Problems.Count == 0;
    }

    // Main thread: works out heights and road versions for a curve.
    public static class RoadPlanner
    {
        public const float MaxGrade = 0.06f;          // ramps: 6 % slope
        public const float BridgeClearance = 10f;     // above water
        public const float OverClearance = 9f;        // flyover deck above the road/rail below
        public const float UnderClearance = 7f;       // headroom under an elevated road we pass beneath
        public const float TunnelDepth = 12f;         // same type: tunnel under the existing road
        public const float CutDepth = 8f;             // different type: open cutting under a short bridge
        public const float TunnelMinDepth = 6f;       // shallower than this is an open cutting
        private const float Unlimited = 100000f;

        public static Plan Make(NetInfo info, List<Vector3> points, List<Vector3> tangents,
            List<Crossing> crossings, List<int> crossingIndex, ushort startNode, ushort endNode)
        {
            var plan = new Plan
            {
                Points = new List<Vector3>(points),
                Tangents = new List<Vector3>(tangents),
                Crossings = crossings,
                CrossingIndex = crossingIndex,
                StartNode = startNode,
                EndNode = endNode,
            };
            int n = points.Count;
            plan.Kinds = new NodeKind[n];
            if (n < 2 || info == null) return plan;

            var terrain = TerrainManager.instance;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var distance = new float[n];
            var ground = new float[n];
            var water = new bool[n];
            var minH = new float[n];
            var maxH = new float[n];
            var isFixed = new bool[n];
            var cutting = new bool[n];   // lowered for a different-type underpass: never a tunnel

            bool hasBridge = Variant(info, "m_bridgeInfo") != null;
            bool hasElevated = Variant(info, "m_elevatedInfo") != null || hasBridge;

            for (int i = 0; i < n; i++)
            {
                if (i > 0) distance[i] = distance[i - 1] + CurveMath.FlatDistance(points[i - 1], points[i]);
                ground[i] = terrain.SampleRawHeightSmooth(points[i]);
                float surface = terrain.SampleRawHeightSmoothWithWater(points[i], false, 0f);
                water[i] = surface - ground[i] > 0.5f;
                minH[i] = water[i] && hasBridge ? surface + BridgeClearance : -Unlimited;
                maxH[i] = Unlimited;
            }

            if (NetworkBuilder.NodeExists(startNode)) Fix(0, nodes[startNode].m_position.y, minH, maxH, isFixed);
            if (NetworkBuilder.NodeExists(endNode)) Fix(n - 1, nodes[endNode].m_position.y, minH, maxH, isFixed);

            for (int c = 0; c < crossings.Count; c++)
            {
                var crossing = crossings[c];
                int k = crossingIndex[c];
                float window = crossing.ExistingHalfWidth + info.m_halfWidth + 2f;
                switch (crossing.Level)
                {
                    case CrossingLevel.Same:
                        if (crossing.ExistingAtGround) Fix(k, crossing.Point.y, minH, maxH, isFixed);
                        break;
                    case CrossingLevel.Over:
                        Limit(distance, k, window, crossing.Point.y + OverClearance, true, minH);
                        break;
                    case CrossingLevel.Under:
                        bool underElevated = crossing.ExistingElevation > UnderClearance;
                        float depth = underElevated ? UnderClearance : crossing.SameType ? TunnelDepth : CutDepth;
                        Limit(distance, k, window, crossing.Point.y - depth, false, maxH);
                        if (!underElevated && !crossing.SameType) Mark(distance, k, window, cutting);
                        break;
                }
            }

            float[] heights;
            if (!CurveMath.SolveHeights(distance, ground, minH, maxH, isFixed, MaxGrade, out heights))
                plan.Problems.Add("Not enough room to ramp up/down at " + MaxGrade * 100f + "% slope: spread the points out or move the crossing away from the ends");

            for (int i = 0; i < n; i++)
            {
                var p = plan.Points[i];
                p.y = heights[i];
                plan.Points[i] = p;

                float elevation = heights[i] - ground[i];
                if (elevation >= 1f) plan.Kinds[i] = water[i] && hasBridge ? NodeKind.Bridge : NodeKind.Elevated;
                else if (elevation <= -1f) plan.Kinds[i] = !cutting[i] && elevation <= -TunnelMinDepth ? NodeKind.Tunnel : NodeKind.Cut;
                else plan.Kinds[i] = NodeKind.Ground;
            }

            CheckVariants(info, plan, hasElevated);
            CheckUnderground(info, plan, water, ground);
            CheckCrossings(plan, ground);
            return plan;
        }

        // The road's version for a node kind, or null if it has none.
        public static NetInfo InfoFor(NetInfo info, NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Bridge: return Variant(info, "m_bridgeInfo") ?? Variant(info, "m_elevatedInfo");
                case NodeKind.Elevated: return Variant(info, "m_elevatedInfo") ?? Variant(info, "m_bridgeInfo");
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
            if (a == NodeKind.Tunnel || b == NodeKind.Tunnel) return Variant(info, "m_slopeInfo");
            return info;
        }

        // A road's other version (m_elevatedInfo, m_bridgeInfo, m_tunnelInfo,
        // m_slopeInfo on roads, tracks and paths), or null.
        public static NetInfo Variant(NetInfo info, string field)
        {
            if (info == null || info.m_netAI == null) return null;
            var f = info.m_netAI.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            return f != null ? f.GetValue(info.m_netAI) as NetInfo : null;
        }

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
                if (Mathf.Abs(distance[i] - distance[k]) <= window * 4f) marks[i] = true;
        }

        private static void CheckVariants(NetInfo info, Plan plan, bool hasElevated)
        {
            bool elevated = false, tunnel = false;
            foreach (var kind in plan.Kinds)
            {
                if (kind == NodeKind.Elevated || kind == NodeKind.Bridge) elevated = true;
                if (kind == NodeKind.Tunnel) tunnel = true;
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
                if (plan.Kinds[i] != NodeKind.Cut && plan.Kinds[i] != NodeKind.Tunnel) continue;
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
        private static void CheckCrossings(Plan plan, float[] ground)
        {
            for (int c = 0; c < plan.Crossings.Count; c++)
            {
                var crossing = plan.Crossings[c];
                if (crossing.Connects) continue;
                float gap = Mathf.Abs(plan.Points[plan.CrossingIndex[c]].y - crossing.Point.y);
                if (gap < UnderClearance - 0.5f)
                    plan.Problems.Add("Only " + gap.ToString("F0") + " m from " + crossing.ExistingName +
                                      ": raise or lower this crossing");
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
