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
        public float BranchOffset;  // metres from the node where the branches start
        public float TrunkOffset;   // metres from the node where the trunk ends
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
        private const float GoreGap = 1f;            // metres between the branches where they start
        private const float MaxBranchOffset = 80f;
        private const float TrunkOffset = 1f;
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

            // Looking from the trunk towards the branches, which one is on the left?
            var forward = -dirs[t];
            float sideA = Vector3.Dot(dirs[a], Left(forward));
            float sideB = Vector3.Dot(dirs[b], Left(forward));
            if (sideA < sideB)
            {
                int tmp = a; a = b; b = tmp;
                float tmpSide = sideA; sideA = sideB; sideB = tmpSide;
            }

            // Branch edges separate at (sideA - sideB) metres per metre travelled.
            // Start the branches where their inner edges are GoreGap apart.
            float widthLeft = segments[ids[a]].Info.m_halfWidth;
            float widthRight = segments[ids[b]].Info.m_halfWidth;
            float spread = Mathf.Max(sideA - sideB, 0.01f);
            float offset = (widthLeft + widthRight + GoreGap) / spread;
            float shortest = Mathf.Min(segments[ids[a]].m_averageLength, segments[ids[b]].m_averageLength);
            offset = Mathf.Min(offset, MaxBranchOffset, shortest * 0.45f);

            shape.Trunk = ids[t];
            shape.LeftBranch = ids[a];
            shape.RightBranch = ids[b];
            shape.BranchAngle = branchAngle;
            shape.BranchOffset = offset;
            shape.TrunkOffset = TrunkOffset;
            reason = null;
            return true;
        }

        // The corner of one road end at a smoothed split. Called from the
        // CalculateCorner postfix, so it must be thread-safe and quick.
        public static bool TryGetCorner(ushort segment, ushort node, bool leftSide, out Vector3 position, out Vector3 direction)
        {
            position = direction = Vector3.zero;
            SplitShape shape;
            string reason;
            if (!TryAnalyze(node, out shape, out reason)) return false;

            float offset = segment == shape.Trunk ? shape.TrunkOffset : shape.BranchOffset;
            var segments = NetManager.instance.m_segments.m_buffer;
            Vector3 centre, tangent;
            PointAlong(ref segments[segment], node, offset, out centre, out tangent);

            float halfWidth = segments[segment].Info.m_halfWidth;
            position = centre + Left(tangent) * (leftSide ? halfWidth : -halfWidth);
            direction = tangent;
            return true;
        }

        public static string Describe(SplitShape s)
        {
            var segments = NetManager.instance.m_segments.m_buffer;
            return "trunk #" + s.Trunk + " (" + segments[s.Trunk].Info.name + ")" +
                   ", left #" + s.LeftBranch + " (" + segments[s.LeftBranch].Info.name + ")" +
                   ", right #" + s.RightBranch + " (" + segments[s.RightBranch].Info.name + ")" +
                   ", branch angle " + s.BranchAngle.ToString("F1") + " deg" +
                   ", branches start " + s.BranchOffset.ToString("F1") + " m from the node";
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

        // Point and direction on the segment's centre line, 'distance' metres from
        // the node, following the segment's curve.
        private static void PointAlong(ref NetSegment segment, ushort node, float distance, out Vector3 point, out Vector3 tangent)
        {
            var nodes = NetManager.instance.m_nodes.m_buffer;
            ushort other = segment.m_startNode == node ? segment.m_endNode : segment.m_startNode;
            Vector3 a = nodes[node].m_position;
            Vector3 d = nodes[other].m_position;
            Vector3 dirA = DirectionAway(ref segment, node);
            Vector3 dirD = DirectionAway(ref segment, other);
            Vector3 b, c;
            NetSegment.CalculateMiddlePoints(a, dirA, d, dirD, false, false, out b, out c);

            const int steps = 64;
            Vector3 previous = a;
            float travelled = 0f;
            float t = 1f;
            for (int i = 1; i <= steps; i++)
            {
                float u = i / (float)steps;
                Vector3 p = Cubic(a, b, c, d, u);
                float step = FlatDistance(previous, p);
                if (travelled + step >= distance)
                {
                    t = (i - 1 + (distance - travelled) / Mathf.Max(step, 0.0001f)) / steps;
                    break;
                }
                travelled += step;
                previous = p;
            }

            point = Cubic(a, b, c, d, t);
            tangent = FlatNormal(CubicTangent(a, b, c, d, t));
            if (tangent == Vector3.zero) tangent = FlatNormal(dirA);
        }

        private static Vector3 Cubic(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1f - t;
            return a * (u * u * u) + b * (3f * u * u * t) + c * (3f * u * t * t) + d * (t * t * t);
        }

        private static Vector3 CubicTangent(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1f - t;
            return (b - a) * (3f * u * u) + (c - b) * (6f * u * t) + (d - c) * (3f * t * t);
        }

        // The left-hand direction when facing 'd' (matches the game's leftSide).
        private static Vector3 Left(Vector3 d)
        {
            return new Vector3(-d.z, 0f, d.x);
        }

        private static Vector3 FlatNormal(Vector3 v)
        {
            v.y = 0f;
            float length = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            return length > 0.0001f ? v / length : Vector3.zero;
        }

        private static float FlatDistance(Vector3 p, Vector3 q)
        {
            float dx = q.x - p.x, dz = q.z - p.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
