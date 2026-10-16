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
        public static void BuildCurve(NetInfo info, List<Vector3> points, List<Vector3> tangents, ushort startNode, ushort endNode)
        {
            // Copy: the tool keeps changing its lists on the main thread.
            var p = new List<Vector3>(points);
            var t = new List<Vector3>(tangents);
            SimulationManager.instance.AddAction(() => BuildNow(info, p, t, startNode, endNode));
        }

        public static void UndoLast()
        {
            SimulationManager.instance.AddAction(UndoNow);
        }

        public static void ClearUndo()
        {
            SimulationManager.instance.AddAction(() => undoStack.Clear());
        }

        private static void BuildNow(NetInfo info, List<Vector3> points, List<Vector3> tangents, ushort startNode, ushort endNode)
        {
            var build = new Build();
            try
            {
                var net = NetManager.instance;
                var sim = SimulationManager.instance;
                var terrain = TerrainManager.instance;
                int last = points.Count - 1;

                // Nodes: reuse snapped ones, create the rest on the ground.
                var nodeIds = new ushort[points.Count];
                var heights = new float[points.Count];
                for (int i = 0; i <= last; i++)
                {
                    ushort existing = i == 0 ? startNode : i == last ? endNode : (ushort)0;
                    if (existing != 0 && NodeExists(existing))
                    {
                        nodeIds[i] = existing;
                        heights[i] = net.m_nodes.m_buffer[existing].m_position.y;
                        continue;
                    }

                    Vector3 position = points[i];
                    position.y = terrain.SampleRawHeightSmooth(position);
                    ushort node;
                    if (!net.CreateNode(out node, ref sim.m_randomizer, info, position, sim.m_currentBuildIndex))
                        throw new Exception("CreateNode failed at point " + i + " " + position);
                    sim.m_currentBuildIndex++;
                    nodeIds[i] = node;
                    heights[i] = position.y;
                    build.Nodes.Add(node);
                }

                // Segments: directions follow the curve, with the slope between the two nodes.
                for (int i = 1; i <= last; i++)
                {
                    float run = CurveMath.FlatDistance(points[i - 1], points[i]);
                    float slope = run > 0.01f ? (heights[i] - heights[i - 1]) / run : 0f;
                    Vector3 startDir = new Vector3(tangents[i - 1].x, slope, tangents[i - 1].z);
                    Vector3 endDir = -new Vector3(tangents[i].x, slope, tangents[i].z);

                    ushort segment;
                    if (!net.CreateSegment(out segment, ref sim.m_randomizer, info, nodeIds[i - 1], nodeIds[i],
                            startDir, endDir, sim.m_currentBuildIndex, sim.m_currentBuildIndex, false))
                        throw new Exception("CreateSegment failed for piece " + i);
                    sim.m_currentBuildIndex++;
                    build.Segments.Add(segment);
                }

                undoStack.Add(build);
                if (undoStack.Count > MaxUndo) undoStack.RemoveAt(0);
                Mod.Log("Built " + build.Segments.Count + " segment(s) of " + info.name +
                        " (" + build.Nodes.Count + " new nodes" +
                        (startNode != 0 ? ", connected at start" : "") +
                        (endNode != 0 ? ", connected at end" : "") + "). Ctrl+Z in the tool undoes it.");
            }
            catch (Exception e)
            {
                Mod.LogError("Building the curve failed; removing the part that was built", e);
                Remove(build);
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

        // Direction an existing road leaves 'node' in, if exactly one road ends
        // there (a dead end the curve can continue smoothly). Zero otherwise.
        public static Vector3 DeadEndDirection(ushort node)
        {
            if (!NodeExists(node) || SegmentCount(node) != 1) return Vector3.zero;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var segments = NetManager.instance.m_segments.m_buffer;
            for (int i = 0; i < 8; i++)
            {
                ushort s = nodes[node].GetSegment(i);
                if (s == 0) continue;
                var dir = segments[s].m_startNode == node ? segments[s].m_startDirection : segments[s].m_endDirection;
                return CurveMath.FlatNormal(dir);
            }
            return Vector3.zero;
        }
    }
}
