using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealisticRoads
{
    // Retaining walls along roads below the ground (our cuttings and tunnel
    // ramps, and the roads we lower for an overpass), built from the game's own
    // quay network: a chain of quay segments along each edge of the road, at
    // the height of the ground as it was, with the road on the quay's water
    // side. The quay holds the ground up to its top on its land side, as on a
    // shore, so the slope the road dug is filled back and only the wall is
    // seen, also under a bridge or deck. Built and undone with the road.
    public static partial class NetworkBuilder
    {
        private const float WallGap = 0.3f;        // the quay's wall this far outside the road's edge
        private const float MinWallHeight = 1.5f;  // the road less deep than this: no wall
        private const float WallSample = 8f;       // metres between the spots looked at along the road
        private const int WallNodeEvery = 5;       // a quay node every this many spots (and where a wall starts or ends)
        private const float WallJunctionMargin = 2f;

        private static NetInfo quay;
        private static bool quayLooked;

        // The game's quay network (null if this game has none). The plain one
        // ("Quay") if there, else any network with the quay's AI.
        internal static NetInfo QuayInfo()
        {
            if (quayLooked) return quay;
            quayLooked = true;
            try
            {
                int count = PrefabCollection<NetInfo>.LoadedCount();
                for (uint i = 0; i < count; i++)
                {
                    var info = PrefabCollection<NetInfo>.GetLoaded(i);
                    if (info == null || !(info.m_netAI is QuayAI)) continue;
                    if (quay == null || info.name == "Quay") quay = info;
                }
                Mod.Log(quay != null ? "Retaining walls: using the quay network " + quay.name
                                     : "Retaining walls: this game has no quay network, so no walls are built");
            }
            catch (Exception e)
            {
                Mod.LogError("Looking for the quay network failed; no walls are built", e);
            }
            return quay;
        }

        // A new city: its networks are looked up afresh.
        public static void ForgetQuay()
        {
            quay = null;
            quayLooked = false;
        }

        // Half the width of the quay's visible wall, either side of its centre
        // line (the rest of the network is the ground it shapes): its lanes'
        // extent, or 6 m.
        private static float QuayModelHalf(NetInfo info)
        {
            float e = 0f;
            if (info.m_lanes != null)
                foreach (var lane in info.m_lanes)
                {
                    if (lane == null || lane.m_laneType == NetInfo.LaneType.None) continue;
                    e = Mathf.Max(e, Mathf.Abs(lane.m_position) + lane.m_width * 0.5f);
                }
            if (e < 1f) e = Mathf.Min(6f, info.m_halfWidth);
            return Mathf.Min(e, info.m_halfWidth);
        }

        // Simulation thread, at the end of a build: quay walls along these
        // roads, both sides, wherever the road is MinWallHeight or more below
        // the ground as it was. Walls of neighbouring roads meet at shared nodes.
        private static void BuildWalls(List<ushort> roads, Build build)
        {
            if (roads.Count == 0) return;
            var info = QuayInfo();
            if (info == null) return;
            var net = NetManager.instance;
            float half = QuayModelHalf(info);
            var joins = new Dictionary<long, ushort>();   // quay nodes made here, by where they are (so walls join)
            int segments = 0;
            foreach (ushort road in roads)
            {
                var s = net.m_segments.m_buffer[road];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null) continue;
                for (int side = 0; side < 2; side++) segments += WallAlong(road, side == 0 ? 1f : -1f, info, half, joins, build);
            }
            if (segments > 0) Mod.Log("  Retaining walls: " + segments + " quay segment(s)");
        }

        private struct WallSpot
        {
            public bool Wall;
            public Vector3 Position;   // the quay's centre line, at its top
            public Vector3 Along;      // the road's direction there
        }

        // One side of one road ('side' 1: right of its direction, -1: left).
        private static int WallAlong(ushort road, float side, NetInfo quayInfo, float modelHalf, Dictionary<long, ushort> joins, Build build)
        {
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[road];
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(road, out a, out b, out c, out d);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(s.m_averageLength, 1f) / WallSample), 2, 64);
            var terrain = TerrainManager.instance;
            float roadHalf = s.Info.m_halfWidth;
            Vector3 startNode = net.m_nodes.m_buffer[s.m_startNode].m_position, endNode = net.m_nodes.m_buffer[s.m_endNode].m_position;
            float startClear = WallJunctionClear(road, s.m_startNode, roadHalf), endClear = WallJunctionClear(road, s.m_endNode, roadHalf);
            var spots = new WallSpot[steps + 1];
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                Vector3 p = CurveMath.Bezier(a, b, c, d, t);
                Vector3 along = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t));
                Vector3 outward = new Vector3(along.z, 0f, -along.x) * side;
                Vector3 centre = p + outward * (roadHalf + WallGap + modelHalf);
                // Its top: the ground as it was (the raw heights, before roads dig
                // it), the higher of at the wall and a little further out, like the
                // game's quay tool.
                float top = Mathf.Max(terrain.SampleRawHeightSmooth(centre), terrain.SampleRawHeightSmooth(centre + outward * (modelHalf + 8f)));
                Vector3 edge = p + outward * roadHalf;
                bool clear = CurveMath.FlatDistance(edge, startNode) >= startClear && CurveMath.FlatDistance(edge, endNode) >= endClear;
                spots[k] = new WallSpot
                {
                    Wall = clear && top - p.y >= MinWallHeight,
                    Position = new Vector3(centre.x, top, centre.z),
                    Along = along,
                };
            }

            // Each run of spots needing a wall: a chain of quay segments.
            int made = 0;
            int k0 = 0;
            while (k0 <= steps)
            {
                if (!spots[k0].Wall)
                {
                    k0++;
                    continue;
                }
                int k1 = k0;
                while (k1 < steps && spots[k1 + 1].Wall) k1++;
                if (k1 > k0)
                {
                    ushort previous = QuayNode(spots[k0].Position, quayInfo, joins, build);
                    int last = k0;
                    for (int k = k0 + 1; k <= k1; k++)
                    {
                        if (k != k1 && k - last < WallNodeEvery) continue;
                        ushort next = QuayNode(spots[k].Position, quayInfo, joins, build);
                        if (previous != 0 && next != 0 && previous != next &&
                            QuaySegment(previous, next, spots[last].Along, spots[k].Along, side < 0f, quayInfo, build)) made++;
                        previous = next;
                        last = k;
                    }
                }
                k0 = k1 + 1;
            }
            return made;
        }

        // A quay node here: one made in this build within a metre (walls along
        // neighbouring roads meet there), else a new one.
        private static ushort QuayNode(Vector3 position, NetInfo info, Dictionary<long, ushort> joins, Build build)
        {
            long key = ((long)Mathf.RoundToInt(position.x) << 32) ^ (uint)Mathf.RoundToInt(position.z);
            ushort node;
            if (joins.TryGetValue(key, out node) && NodeExists(node)) return node;
            var net = NetManager.instance;
            var sim = SimulationManager.instance;
            if (!net.CreateNode(out node, ref sim.m_randomizer, info, position, sim.m_currentBuildIndex)) return 0;
            sim.m_currentBuildIndex++;
            build.Nodes.Add(node);
            build.Stamps.Node(node);
            joins[key] = node;
            return node;
        }

        // A quay segment from 'from' to 'to'. A quay holds the ground on its
        // right, or on its left when inverted: the land is always away from the
        // road ('landLeft' for a wall on the road's left).
        private static bool QuaySegment(ushort from, ushort to, Vector3 startAlong, Vector3 endAlong, bool landLeft, NetInfo info, Build build)
        {
            var net = NetManager.instance;
            var sim = SimulationManager.instance;
            Vector3 a = net.m_nodes.m_buffer[from].m_position, b = net.m_nodes.m_buffer[to].m_position;
            float run = CurveMath.FlatDistance(a, b);
            float slope = run > 0.01f ? (b.y - a.y) / run : 0f;
            Vector3 startDir = new Vector3(startAlong.x, slope, startAlong.z).normalized;
            Vector3 endDir = -new Vector3(endAlong.x, slope, endAlong.z).normalized;
            ushort segment;
            if (!net.CreateSegment(out segment, ref sim.m_randomizer, info, from, to, startDir, endDir,
                    sim.m_currentBuildIndex, sim.m_currentBuildIndex, landLeft))
                return false;
            sim.m_currentBuildIndex++;
            build.Segments.Add(segment);
            build.Stamps.Segment(segment);
            return true;
        }

        // How far from a junction node a wall along 'road' stops: past the widest
        // other road there, so it doesn't cross a side road's mouth (0 where the
        // road just goes on).
        private static float WallJunctionClear(ushort road, ushort node, float roadHalf)
        {
            var net = NetManager.instance;
            int roads = 0;
            float widest = 0f;
            for (int i = 0; i < 8; i++)
            {
                ushort other = net.m_nodes.m_buffer[node].GetSegment(i);
                if (other == 0) continue;
                roads++;
                if (other == road) continue;
                var info = net.m_segments.m_buffer[other].Info;
                if (info != null) widest = Mathf.Max(widest, info.m_halfWidth);
            }
            return roads > 2 ? widest + roadHalf + WallJunctionMargin : 0f;
        }
    }
}
