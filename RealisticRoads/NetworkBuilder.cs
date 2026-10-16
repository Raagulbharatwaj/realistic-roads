using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using UnityEngine;

namespace RealisticRoads
{
    // Creates and removes roads. All game changes happen on the simulation
    // thread; the public methods only queue the work.
    public static class NetworkBuilder
    {
        private const int MaxUndo = 20;
        private const float BridgeClearance = 10f;   // metres above the water
        private const float MaxGrade = 0.06f;        // ramps up to bridges: 6 % slope
        private const float MinElevation = 1f;       // below this a node sits on the ground
        private const float ContinueAngle = 55f;     // continue an existing road if within this angle

        // What one build created, so one undo removes all of it.
        private class Build
        {
            public readonly List<ushort> Segments = new List<ushort>();
            public readonly List<ushort> Nodes = new List<ushort>();   // nodes we created (not snapped ones)
        }

        // Only touched on the simulation thread.
        private static readonly List<Build> undoStack = new List<Build>();

        // points/tangents: one entry per node (see CurveMath.Divide).
        // startNode/endNode: existing nodes to connect to, or 0 for new ones.
        // splitNodes: half-snapped trunk ends; if one becomes a split (two
        // branches on a trunk end) it is smoothed automatically.
        public static void BuildCurve(NetInfo info, List<Vector3> points, List<Vector3> tangents, ushort startNode, ushort endNode,
            params ushort[] splitNodes)
        {
            // Copy: the tool keeps changing its lists on the main thread.
            var p = new List<Vector3>(points);
            var t = new List<Vector3>(tangents);
            SimulationManager.instance.AddAction(() =>
            {
                if (!BuildNow(info, p, t, startNode, endNode)) return;
                foreach (var node in splitNodes)
                    if (node != 0) SmoothSplits.EnableIfSplit(node);
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

        private static bool BuildNow(NetInfo info, List<Vector3> points, List<Vector3> tangents, ushort startNode, ushort endNode)
        {
            var build = new Build();
            try
            {
                var net = NetManager.instance;
                var sim = SimulationManager.instance;
                int last = points.Count - 1;

                ushort[] fixedNodes = new ushort[points.Count];
                if (NodeExists(startNode)) fixedNodes[0] = startNode;
                if (NodeExists(endNode)) fixedNodes[last] = endNode;

                Profile profile = HeightProfile(info, points, fixedNodes);

                // Nodes: reuse snapped ones, create the rest.
                var nodeIds = new ushort[points.Count];
                for (int i = 0; i <= last; i++)
                {
                    if (fixedNodes[i] != 0)
                    {
                        nodeIds[i] = fixedNodes[i];
                        continue;
                    }

                    Vector3 position = points[i];
                    position.y = profile.Heights[i];
                    ushort node;
                    if (!net.CreateNode(out node, ref sim.m_randomizer, profile.NodeInfos[i], position, sim.m_currentBuildIndex))
                        throw new Exception("CreateNode failed at point " + i + " " + position);
                    sim.m_currentBuildIndex++;
                    // How high the node stands above the ground: the game uses it for pillars.
                    net.m_nodes.m_buffer[node].m_elevation = (byte)Mathf.Clamp(Mathf.RoundToInt(profile.Elevations[i]), 0, 255);
                    nodeIds[i] = node;
                    build.Nodes.Add(node);
                }

                // Segments: directions follow the curve, with the slope between the two nodes.
                for (int i = 1; i <= last; i++)
                {
                    float run = CurveMath.FlatDistance(points[i - 1], points[i]);
                    float slope = run > 0.01f ? (profile.Heights[i] - profile.Heights[i - 1]) / run : 0f;
                    Vector3 startDir = new Vector3(tangents[i - 1].x, slope, tangents[i - 1].z);
                    Vector3 endDir = -new Vector3(tangents[i].x, slope, tangents[i].z);

                    ushort segment;
                    if (!net.CreateSegment(out segment, ref sim.m_randomizer, profile.SegmentInfos[i - 1], nodeIds[i - 1], nodeIds[i],
                            startDir, endDir, sim.m_currentBuildIndex, sim.m_currentBuildIndex, false))
                        throw new Exception("CreateSegment failed for piece " + i);
                    sim.m_currentBuildIndex++;
                    build.Segments.Add(segment);
                }

                if (profile.BridgeSegments > 0 || profile.ElevatedSegments > 0)
                    Mod.Log("  " + profile.BridgeSegments + " bridge segment(s) over water, " +
                            profile.ElevatedSegments + " elevated ramp segment(s)");

                undoStack.Add(build);
                if (undoStack.Count > MaxUndo) undoStack.RemoveAt(0);
                Mod.Log("Built " + build.Segments.Count + " segment(s) of " + info.name +
                        " (" + build.Nodes.Count + " new nodes" +
                        (startNode != 0 ? ", connected at start" : "") +
                        (endNode != 0 ? ", connected at end" : "") + "). Ctrl+Z in the tool undoes it.");
                return true;
            }
            catch (Exception e)
            {
                Mod.LogError("Building the curve failed; removing the part that was built", e);
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
            Mod.Log("Undo: removed " + build.Segments.Count + " segment(s)");
        }

        // Simulation thread. Removes the segments, then any node we created
        // that nothing else has connected to since.
        private static void Remove(Build build)
        {
            var net = NetManager.instance;
            foreach (var segment in build.Segments)
            {
                if ((net.m_segments.m_buffer[segment].m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None)
                    net.ReleaseSegment(segment, true);
            }
            foreach (var node in build.Nodes)
            {
                if (NodeExists(node) && SegmentCount(node) == 0)
                    net.ReleaseNode(node);
            }
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

        private const float MinHalfRunIn = 16f;

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
                // The other road must be a branch already leaving this end.
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
            snap.RunIn = Mathf.Max(MinHalfRunIn, snap.Offset * 4f);
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

        // Heights and road variants along the curve: on the ground normally,
        // a bridge over water, and elevated ramps on land leading up to it.
        private class Profile
        {
            public float[] Heights;
            public float[] Elevations;
            public NetInfo[] NodeInfos;
            public NetInfo[] SegmentInfos;
            public int BridgeSegments;
            public int ElevatedSegments;
        }

        private static Profile HeightProfile(NetInfo info, List<Vector3> points, ushort[] fixedNodes)
        {
            var terrain = TerrainManager.instance;
            var net = NetManager.instance;
            int count = points.Count;
            var ground = new float[count];
            var water = new bool[count];
            var heights = new float[count];

            NetInfo bridge = Variant(info, "m_bridgeInfo");
            NetInfo elevated = Variant(info, "m_elevatedInfo") ?? bridge;

            for (int i = 0; i < count; i++)
            {
                ground[i] = terrain.SampleRawHeightSmooth(points[i]);
                float surface = terrain.SampleRawHeightSmoothWithWater(points[i], false, 0f);
                water[i] = surface - ground[i] > 0.5f && bridge != null;
                heights[i] = fixedNodes[i] != 0 ? net.m_nodes.m_buffer[fixedNodes[i]].m_position.y
                    : water[i] ? surface + BridgeClearance
                    : ground[i];
            }

            // Ramps: never climb or drop faster than MaxGrade, by raising the
            // lower side (forward pass for the way down, backward for the way up).
            for (int i = 1; i < count; i++)
                if (fixedNodes[i] == 0)
                    heights[i] = Mathf.Max(heights[i], heights[i - 1] - MaxGrade * CurveMath.FlatDistance(points[i - 1], points[i]));
            for (int i = count - 2; i >= 0; i--)
                if (fixedNodes[i] == 0)
                    heights[i] = Mathf.Max(heights[i], heights[i + 1] - MaxGrade * CurveMath.FlatDistance(points[i], points[i + 1]));

            var profile = new Profile
            {
                Heights = heights,
                Elevations = new float[count],
                NodeInfos = new NetInfo[count],
                SegmentInfos = new NetInfo[count - 1],
            };

            // 0 = ground, 1 = elevated, 2 = bridge
            var kind = new int[count];
            for (int i = 0; i < count; i++)
            {
                float elevation = heights[i] - ground[i];
                if (elevation < MinElevation || elevated == null)
                {
                    if (fixedNodes[i] == 0) heights[i] = ground[i];
                    kind[i] = 0;
                }
                else
                {
                    kind[i] = water[i] ? 2 : 1;
                    profile.Elevations[i] = elevation;
                }
                profile.NodeInfos[i] = kind[i] == 2 ? bridge : kind[i] == 1 ? elevated : info;
            }
            for (int i = 1; i < count; i++)
            {
                int k = Math.Max(kind[i - 1], kind[i]);
                profile.SegmentInfos[i - 1] = k == 2 ? bridge : k == 1 ? elevated : info;
                if (k == 2) profile.BridgeSegments++;
                else if (k == 1) profile.ElevatedSegments++;
            }
            return profile;
        }

        // A road's bridge/elevated version, read from its AI (m_bridgeInfo,
        // m_elevatedInfo exist on roads, tracks and paths). Null if it has none.
        private static NetInfo Variant(NetInfo info, string field)
        {
            if (info == null || info.m_netAI == null) return null;
            var f = info.m_netAI.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            return f != null ? f.GetValue(info.m_netAI) as NetInfo : null;
        }
    }
}
