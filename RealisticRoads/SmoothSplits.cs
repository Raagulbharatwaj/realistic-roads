using System;
using System.IO;
using ColossalFramework;
using UnityEngine;

namespace RealisticRoads
{
    // The shape of one smoothed split: a trunk road and two branches that
    // leave it at a shallow angle (or the same layout as a merge).
    public struct SplitShape
    {
        public ushort Node;
        public ushort Trunk;
        public ushort LeftBranch;   // left/right as seen from the trunk looking at the branches
        public ushort RightBranch;
        public float BranchAngle;   // degrees between the two branches
        public Vector3 Forward;     // trunk direction, pointing towards the branches
        public float LeftShare;     // fraction of the trunk width the left branch takes over
    }

    // Junction nodes the player has marked as smooth splits, and the corner
    // geometry that makes them look like one (see CalculateCornerPatch).
    //
    // Nothing here changes the roads themselves: the shape is computed every
    // time the game asks for a corner. If the mod is removed the node simply
    // renders as a vanilla junction again.
    public static class SmoothSplits
    {
        public const string DataId = "RealisticRoads.SmoothSplits";
        private const int MaxNodes = 32768;          // NetManager.MAX_NODE_COUNT
        private const float MaxSplitAngle = 75f;     // branches must be closer than this
        private const float MinTrunkOpposite = 135f; // trunk must come in from the other side
        // The branches start this far past the trunk end, so the vanilla
        // junction piece between them shrinks to a thin, invisible strip.
        private const float BranchGap = 0.5f;
        private const float MovedTolerance = 2f;     // node moved/replaced => forget it

        // Written on the main thread, read from any thread. A node only counts
        // while it still sits where it was when it was marked, so a deleted
        // node whose id is reused elsewhere is not affected.
        private static readonly bool[] enabled = new bool[MaxNodes];
        private static readonly Vector3[] positions = new Vector3[MaxNodes];

        public static bool IsEnabled(ushort node)
        {
            if (!enabled[node]) return false;
            var current = NetManager.instance.m_nodes.m_buffer[node].m_position;
            return (current - positions[node]).sqrMagnitude < MovedTolerance * MovedTolerance;
        }

        // Main thread. Returns the new state.
        public static bool Toggle(ushort node)
        {
            if (IsEnabled(node))
            {
                enabled[node] = false;
                Mod.Log("Smooth split OFF at node #" + node);
                RefreshNode(node);
                return false;
            }

            SplitShape shape;
            string reason;
            if (!TryAnalyze(node, out shape, out reason))
            {
                Mod.Log("Node #" + node + " is not a split: " + reason);
                return false;
            }

            positions[node] = NetManager.instance.m_nodes.m_buffer[node].m_position;
            enabled[node] = true;
            Mod.Log("Smooth split ON at node #" + node + ": " + Describe(shape));
            RefreshNode(node);
            return true;
        }

        // Turns the smooth shape on if 'node' is a split; used automatically
        // when the curve tool attaches a second branch to a trunk end. Safe on
        // the simulation thread.
        public static void EnableIfSplit(ushort node)
        {
            if (IsEnabled(node)) return;
            SplitShape shape;
            string reason;
            if (!TryAnalyze(node, out shape, out reason)) return;
            positions[node] = NetManager.instance.m_nodes.m_buffer[node].m_position;
            enabled[node] = true;
            Mod.Log("Smooth split applied automatically at node #" + node + ": " + Describe(shape));
            RefreshNode(node);
        }

        // Asks the game to rebuild the node and its roads, on the simulation thread.
        public static void RefreshNode(ushort node)
        {
            SimulationManager.instance.AddAction(() => NetManager.instance.UpdateNode(node, 0, -1));
        }

        public static void RefreshAll()
        {
            int count = 0;
            for (int i = 1; i < MaxNodes; i++)
            {
                if (!enabled[i]) continue;
                RefreshNode((ushort)i);
                count++;
            }
            if (count > 0) Mod.Log("Refreshing " + count + " smooth split(s)");
        }

        public static void Clear()
        {
            Array.Clear(enabled, 0, enabled.Length);
        }

        // Is this node a split (or merge) we know how to smooth?
        public static bool TryAnalyze(ushort node, out SplitShape shape, out string reason)
        {
            shape = default(SplitShape);
            shape.Node = node;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var segments = NetManager.instance.m_segments.m_buffer;

            if ((nodes[node].m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
            {
                reason = "node does not exist";
                return false;
            }

            var ids = new ushort[3];
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort s = nodes[node].GetSegment(i);
                if (s == 0) continue;
                if (count == 3) { count++; break; }
                ids[count++] = s;
            }
            if (count != 3)
            {
                reason = "needs exactly 3 roads, has " + (count > 3 ? "more than 3" : count.ToString());
                return false;
            }

            var dirs = new Vector3[3];
            for (int i = 0; i < 3; i++) dirs[i] = FlatNormal(DirectionAway(ref segments[ids[i]], node));

            // The two branches are the pair with the smallest angle between them.
            int a = 0, b = 1, t = 2;
            float best = Vector3.Dot(dirs[0], dirs[1]);
            if (Vector3.Dot(dirs[0], dirs[2]) > best) { best = Vector3.Dot(dirs[0], dirs[2]); a = 0; b = 2; t = 1; }
            if (Vector3.Dot(dirs[1], dirs[2]) > best) { best = Vector3.Dot(dirs[1], dirs[2]); a = 1; b = 2; t = 0; }

            float branchAngle = Mathf.Acos(Mathf.Clamp(best, -1f, 1f)) * Mathf.Rad2Deg;
            if (branchAngle > MaxSplitAngle)
            {
                reason = "closest two roads are " + branchAngle.ToString("F0") + " degrees apart (max " + MaxSplitAngle + ")";
                return false;
            }

            var bisector = FlatNormal(dirs[a] + dirs[b]);
            float trunkAngle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(dirs[t], bisector), -1f, 1f)) * Mathf.Rad2Deg;
            if (trunkAngle < MinTrunkOpposite)
            {
                reason = "third road is only " + trunkAngle.ToString("F0") + " degrees from the branches (min " + MinTrunkOpposite + ")";
                return false;
            }

            // Looking from the trunk towards the branches, which one is on the
            // left? Judged by where each branch goes (its far end), which still
            // works when a branch leaves parallel to the trunk.
            var forward = -dirs[t];
            float sideA = Vector3.Dot(FarDirection(ref segments[ids[a]], node), Left(forward));
            float sideB = Vector3.Dot(FarDirection(ref segments[ids[b]], node), Left(forward));
            if (sideA < sideB)
            {
                int tmp = a; a = b; b = tmp;
                float tmpSide = sideA; sideA = sideB; sideB = tmpSide;
            }

            // Each branch takes over a share of the trunk's width, in proportion
            // to its own width: the left branch from the trunk's left edge, the
            // right branch from its right edge.
            float widthLeft = segments[ids[a]].Info.m_halfWidth;
            float widthRight = segments[ids[b]].Info.m_halfWidth;

            shape.Trunk = ids[t];
            shape.LeftBranch = ids[a];
            shape.RightBranch = ids[b];
            shape.BranchAngle = branchAngle;
            shape.Forward = forward;
            shape.LeftShare = widthLeft / (widthLeft + widthRight);
            reason = null;
            return true;
        }

        // The corner of one road end at a smoothed split. Called from the
        // CalculateCorner postfix, so it must be thread-safe and quick.
        //
        // The branches are pulled back to meet the trunk side by side:
        //
        //   trunk left edge  ───────────┬──── left branch, outer edge
        //                               │     left branch
        //   trunk           (node)  split ◄── inner edges meet: the gore nose
        //                               │     right branch
        //   trunk right edge ───────────┴──── right branch, outer edge
        //
        // Their outer edges continue the trunk's edges and they leave parallel
        // to the trunk before curving onto their own path, so the vanilla
        // junction piece (pavement block, crosswalks) has almost no area left.
        public static bool TryGetCorner(ushort segment, ushort node, bool leftSide, out Vector3 position, out Vector3 direction)
        {
            position = direction = Vector3.zero;
            SplitShape shape;
            string reason;
            if (!TryAnalyze(node, out shape, out reason)) return false;

            var nodes = NetManager.instance.m_nodes.m_buffer;
            var segments = NetManager.instance.m_segments.m_buffer;
            Vector3 centre = nodes[node].m_position;
            Vector3 f = shape.Forward;
            Vector3 left = Left(f);
            float trunkHalf = segments[shape.Trunk].Info.m_halfWidth;

            // Points across the trunk end, seen from the trunk looking at the branches.
            Vector3 leftEdge = centre + left * trunkHalf;
            Vector3 rightEdge = centre - left * trunkHalf;
            Vector3 split = leftEdge + (rightEdge - leftEdge) * shape.LeftShare;

            if (segment == shape.Trunk)
            {
                // The trunk faces the other way, so its own left is our right.
                position = leftSide ? rightEdge : leftEdge;
                direction = -f;
                return true;
            }

            Vector3 gap = f * BranchGap;
            if (segment == shape.LeftBranch)
                position = (leftSide ? leftEdge : split) + gap;
            else if (segment == shape.RightBranch)
                position = (leftSide ? split : rightEdge) + gap;
            else
                return false;
            direction = f;
            return true;
        }

        public static string Describe(SplitShape s)
        {
            var segments = NetManager.instance.m_segments.m_buffer;
            return "trunk #" + s.Trunk + " (" + segments[s.Trunk].Info.name + ")" +
                   ", left #" + s.LeftBranch + " (" + segments[s.LeftBranch].Info.name + ")" +
                   ", right #" + s.RightBranch + " (" + segments[s.RightBranch].Info.name + ")" +
                   ", branch angle " + s.BranchAngle.ToString("F1") + " deg" +
                   ", left branch takes " + (s.LeftShare * 100f).ToString("F0") + "% of the trunk width";
        }

        // --- save/load -------------------------------------------------------

        public static byte[] Serialize()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); // format version
                int count = 0;
                for (int i = 1; i < MaxNodes; i++) if (enabled[i]) count++;
                writer.Write(count);
                for (int i = 1; i < MaxNodes; i++)
                {
                    if (!enabled[i]) continue;
                    writer.Write((ushort)i);
                    writer.Write(positions[i].x);
                    writer.Write(positions[i].y);
                    writer.Write(positions[i].z);
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static void Deserialize(byte[] data)
        {
            Clear();
            if (data == null || data.Length == 0) return;
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                byte version = reader.ReadByte();
                if (version != 1) throw new InvalidDataException("unknown smooth split data version " + version);
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    ushort node = reader.ReadUInt16();
                    positions[node] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    enabled[node] = true;
                }
            }
        }

        // --- geometry helpers -----------------------------------------------

        // A segment's direction at this node, pointing away from it into the segment.
        private static Vector3 DirectionAway(ref NetSegment segment, ushort node)
        {
            return segment.m_startNode == node ? segment.m_startDirection : segment.m_endDirection;
        }

        // Direction from this node to the segment's other end.
        private static Vector3 FarDirection(ref NetSegment segment, ushort node)
        {
            var nodes = NetManager.instance.m_nodes.m_buffer;
            ushort other = segment.m_startNode == node ? segment.m_endNode : segment.m_startNode;
            return FlatNormal(nodes[other].m_position - nodes[node].m_position);
        }

        private static Vector3 Left(Vector3 d)
        {
            return CurveMath.Left(d);
        }

        private static Vector3 FlatNormal(Vector3 v)
        {
            v.y = 0f;
            float length = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            return length > 0.0001f ? v / length : Vector3.zero;
        }
    }
}
