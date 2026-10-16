using System;
using System.Collections.Generic;
using System.IO;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // One lane at a road end, where a lane road can start or end.
    public struct LaneSlot
    {
        public int Index;          // position in the road's lane list (stable while the road exists)
        public Vector3 Position;   // where the lane meets the road end
        public float Lateral;      // sideways from the node, + = left when looking out of the road
        public float HalfWidth;    // half the lane's width
        public bool Into;          // traffic on this lane drives towards the node
        public bool Taken;         // a lane road already starts here
    }

    // Lane bundles: a road end (a "lane node") where each lane carries on as
    // its own one-lane road, so lanes can be drawn one by one like train
    // tracks, split off, merge in and cross over freely.
    //
    // Each lane road ("strip") is squeezed to the width of the lane it came
    // from, so strips side by side look like the lanes of one road. At a lane
    // node the strips sit exactly on their lanes across the road end (see
    // CalculateCornerPatch); cars follow the same corners.
    //
    // Like smooth splits, nothing is stored in the roads themselves: without
    // the mod the strips render as ordinary one-lane roads.
    public static class LaneBundles
    {
        public const string DataId = "RealisticRoads.LaneBundles";
        public const string DefaultLaneRoad = "Highway Ramp";
        public const float RunIn = 16f;             // a lane leaves its road end parallel for this far
        private const float DefaultHalfWidth = 1.5f;
        private const float MovedTolerance = 2f;
        private const float TakenTolerance = 0.6f;
        private const int MaxNodes = 32768;
        private const int MaxSegments = NetManager.MAX_SEGMENT_COUNT;

        // Written on the simulation/main thread, read from any thread.
        private static readonly float[] stripHalf = new float[MaxSegments];     // 0 = not a strip
        private static readonly ushort[] stripStart = new ushort[MaxSegments];  // to spot a reused segment id
        private static readonly ushort[] stripEnd = new ushort[MaxSegments];
        private static readonly bool[] laneNode = new bool[MaxNodes];
        private static readonly Vector3[] laneNodePosition = new Vector3[MaxNodes];

        public static bool IsStrip(ushort segment)
        {
            if (stripHalf[segment] <= 0f) return false;
            var s = NetManager.instance.m_segments.m_buffer[segment];
            return (s.m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None &&
                   s.m_startNode == stripStart[segment] && s.m_endNode == stripEnd[segment];
        }

        // A marked lane node that still has its road and at least one strip.
        public static bool IsLaneNode(ushort node)
        {
            if (!laneNode[node]) return false;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            if ((nodes[node].m_flags & NetNode.Flags.Created) == NetNode.Flags.None) return false;
            if ((nodes[node].m_position - laneNodePosition[node]).sqrMagnitude > MovedTolerance * MovedTolerance) return false;
            ushort trunk;
            int strips;
            return Analyze(node, out trunk, out strips) && strips > 0;
        }

        // Simulation thread (right after building).
        public static void AddStrip(ushort segment, float halfWidth)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            stripStart[segment] = s.m_startNode;
            stripEnd[segment] = s.m_endNode;
            stripHalf[segment] = halfWidth;
        }

        public static void AddLaneNode(ushort node)
        {
            laneNodePosition[node] = NetManager.instance.m_nodes.m_buffer[node].m_position;
            laneNode[node] = true;
        }

        public static void Clear()
        {
            Array.Clear(stripHalf, 0, stripHalf.Length);
            Array.Clear(laneNode, 0, laneNode.Length);
        }

        // The road a lane road should be built with: the road picked in the
        // Roads menu if it is a one-lane one-way road, else the Highway Ramp.
        public static NetInfo LaneRoad(NetInfo picked)
        {
            if (picked != null && CarLanes(picked) == 1 && IsOneWay(picked)) return picked;
            return PrefabCollection<NetInfo>.FindLoaded(DefaultLaneRoad);
        }

        // The lanes at a road end (a dead end, or a lane node) that lane roads
        // can start from. Empty if the node is neither.
        public static List<LaneSlot> Slots(ushort node)
        {
            var result = new List<LaneSlot>();
            ushort trunk;
            int strips;
            if (!Analyze(node, out trunk, out strips)) return result;

            var net = NetManager.instance;
            var segment = net.m_segments.m_buffer[trunk];
            var info = segment.Info;
            if (info == null || info.m_lanes == null) return result;
            Vector3 centre = net.m_nodes.m_buffer[node].m_position;
            Vector3 forward = Forward(trunk, node);
            Vector3 left = CurveMath.Left(forward);
            bool atStart = segment.m_startNode == node;
            bool inverted = (segment.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
            var taken = TakenLaterals(node, trunk, forward);

            uint lane = segment.m_lanes;
            for (int i = 0; i < info.m_lanes.Length && lane != 0; i++)
            {
                var laneInfo = info.m_lanes[i];
                var bezier = net.m_lanes.m_buffer[lane].m_bezier;
                lane = net.m_lanes.m_buffer[lane].m_nextLane;
                if ((laneInfo.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) == NetInfo.LaneType.None) continue;
                if ((laneInfo.m_vehicleType & VehicleInfo.VehicleType.Car) == VehicleInfo.VehicleType.None) continue;

                var direction = laneInfo.m_finalDirection;
                if (inverted) direction = Invert(direction);
                bool forwardTraffic = direction == NetInfo.Direction.Forward;   // start -> end
                if (!forwardTraffic && direction != NetInfo.Direction.Backward) continue;  // two-way lane

                Vector3 position = atStart ? bezier.a : bezier.d;
                float lateral = Vector3.Dot(position - centre, left);
                var slot = new LaneSlot
                {
                    Index = i,
                    Position = position,
                    Lateral = lateral,
                    HalfWidth = laneInfo.m_width > 0.5f ? laneInfo.m_width * 0.5f : DefaultHalfWidth,
                    Into = atStart ? !forwardTraffic : forwardTraffic,
                };
                foreach (float t in taken) if (Mathf.Abs(t - lateral) < TakenTolerance) slot.Taken = true;
                result.Add(slot);
            }
            return result;
        }

        public static bool TryGetSlot(ushort node, int index, out LaneSlot slot)
        {
            foreach (var s in Slots(node))
            {
                if (s.Index != index) continue;
                slot = s;
                return true;
            }
            slot = default(LaneSlot);
            return false;
        }

        // The slot nearest 'cursor' that a lane road may use: free, and (when
        // 'into' is set) with traffic in that direction.
        public static bool TryNearestSlot(ushort node, Vector3 cursor, bool? into, out LaneSlot slot)
        {
            slot = default(LaneSlot);
            float best = float.MaxValue;
            foreach (var s in Slots(node))
            {
                if (s.Taken || (into.HasValue && s.Into != into.Value)) continue;
                float d = CurveMath.FlatDistance(s.Position, cursor);
                if (d >= best) continue;
                best = d;
                slot = s;
            }
            return best < float.MaxValue;
        }

        // The slot as a half-snap, so the curve tool runs the lane in parallel
        // from its place on the road end exactly like a split branch.
        public static NetworkBuilder.HalfSnap AsSnap(ushort node, LaneSlot slot)
        {
            ushort trunk;
            int strips;
            Analyze(node, out trunk, out strips);
            return new NetworkBuilder.HalfSnap
            {
                Side = slot.Lateral >= 0f ? 1 : -1,
                Offset = Mathf.Abs(slot.Lateral),
                Forward = Forward(trunk, node),
                RunIn = RunIn,
            };
        }

        // The corner of a road end at a lane node, or of a strip anywhere.
        // Called from the CalculateCorner postfix: thread-safe and quick.
        public static bool TryGetCorner(ushort segment, ushort node, bool leftSide, ref Vector3 position, ref Vector3 direction)
        {
            var net = NetManager.instance;
            Vector3 centre = net.m_nodes.m_buffer[node].m_position;
            bool strip = IsStrip(segment);

            if (laneNode[node] && IsLaneNode(node))
            {
                ushort trunk;
                int strips;
                Analyze(node, out trunk, out strips);
                Vector3 f = Forward(trunk, node);
                Vector3 left = CurveMath.Left(f);
                if (segment == trunk)
                {
                    // The trunk ends square at full width; it faces the other way.
                    float half = net.m_segments.m_buffer[trunk].Info.m_halfWidth;
                    position = centre + left * (leftSide ? -half : half);
                    direction = -f;
                    return true;
                }
                if (!strip) return false;
                // The strip sits on its lane: where its next node is across the road end.
                float lateral = Vector3.Dot(FarNode(segment, node) - centre, left);
                position = centre + left * (lateral + (leftSide ? stripHalf[segment] : -stripHalf[segment]));
                direction = f;
                return true;
            }

            if (!strip) return false;
            // Anywhere else a strip is squeezed to its lane's width around the node.
            var info = net.m_segments.m_buffer[segment].Info;
            if (info == null || info.m_halfWidth < 0.1f) return false;
            float scale = stripHalf[segment] / info.m_halfWidth;
            float y = position.y;
            position = centre + (position - centre) * scale;
            position.y = y;
            return true;
        }

        // The trunk (the one road at the node that isn't a strip) and the
        // number of strips. False if the node has no trunk or more than one.
        private static bool Analyze(ushort node, out ushort trunk, out int strips)
        {
            trunk = 0;
            strips = 0;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            if ((nodes[node].m_flags & NetNode.Flags.Created) == NetNode.Flags.None) return false;
            for (int i = 0; i < 8; i++)
            {
                ushort s = nodes[node].GetSegment(i);
                if (s == 0) continue;
                if (IsStrip(s)) { strips++; continue; }
                if (trunk != 0) return false;
                trunk = s;
            }
            return trunk != 0 && NetManager.instance.m_segments.m_buffer[trunk].Info != null;
        }

        private static List<float> TakenLaterals(ushort node, ushort trunk, Vector3 forward)
        {
            var result = new List<float>();
            var nodes = NetManager.instance.m_nodes.m_buffer;
            Vector3 centre = nodes[node].m_position;
            Vector3 left = CurveMath.Left(forward);
            for (int i = 0; i < 8; i++)
            {
                ushort s = nodes[node].GetSegment(i);
                if (s == 0 || s == trunk || !IsStrip(s)) continue;
                result.Add(Vector3.Dot(FarNode(s, node) - centre, left));
            }
            return result;
        }

        // Out of the trunk, across the node (flat).
        private static Vector3 Forward(ushort trunk, ushort node)
        {
            var s = NetManager.instance.m_segments.m_buffer[trunk];
            return -CurveMath.FlatNormal(s.m_startNode == node ? s.m_startDirection : s.m_endDirection);
        }

        private static Vector3 FarNode(ushort segment, ushort node)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            return NetManager.instance.m_nodes.m_buffer[s.m_startNode == node ? s.m_endNode : s.m_startNode].m_position;
        }

        private static NetInfo.Direction Invert(NetInfo.Direction d)
        {
            if (d == NetInfo.Direction.Forward) return NetInfo.Direction.Backward;
            if (d == NetInfo.Direction.Backward) return NetInfo.Direction.Forward;
            return d;
        }

        private static int CarLanes(NetInfo info)
        {
            int count = 0;
            if (info.m_lanes == null) return 0;
            foreach (var lane in info.m_lanes)
                if ((lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None &&
                    (lane.m_vehicleType & VehicleInfo.VehicleType.Car) != VehicleInfo.VehicleType.None) count++;
            return count;
        }

        private static bool IsOneWay(NetInfo info)
        {
            foreach (var lane in info.m_lanes)
                if ((lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None &&
                    lane.m_finalDirection != NetInfo.Direction.Forward) return false;
            return true;
        }

        // --- save/load -------------------------------------------------------

        public static byte[] Serialize()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); // format version
                var strips = new List<ushort>();
                for (int i = 1; i < MaxSegments; i++) if (IsStrip((ushort)i)) strips.Add((ushort)i);
                writer.Write(strips.Count);
                foreach (ushort s in strips)
                {
                    writer.Write(s);
                    writer.Write(stripStart[s]);
                    writer.Write(stripEnd[s]);
                    writer.Write(stripHalf[s]);
                }
                var nodes = new List<ushort>();
                for (int i = 1; i < MaxNodes; i++) if (laneNode[i]) nodes.Add((ushort)i);
                writer.Write(nodes.Count);
                foreach (ushort n in nodes)
                {
                    writer.Write(n);
                    writer.Write(laneNodePosition[n].x);
                    writer.Write(laneNodePosition[n].y);
                    writer.Write(laneNodePosition[n].z);
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
                if (version != 1) throw new InvalidDataException("unknown lane bundle data version " + version);
                int strips = reader.ReadInt32();
                for (int i = 0; i < strips; i++)
                {
                    ushort s = reader.ReadUInt16();
                    stripStart[s] = reader.ReadUInt16();
                    stripEnd[s] = reader.ReadUInt16();
                    stripHalf[s] = reader.ReadSingle();
                }
                int nodes = reader.ReadInt32();
                for (int i = 0; i < nodes; i++)
                {
                    ushort n = reader.ReadUInt16();
                    laneNodePosition[n] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    laneNode[n] = true;
                }
                Mod.Log("Loaded " + strips + " lane road segment(s) and " + nodes + " lane node(s)");
            }
        }

        // Main thread, after loading: rebuild the shapes of the lane nodes and strips.
        public static void RefreshAll()
        {
            var nodes = new HashSet<ushort>();
            for (int i = 1; i < MaxNodes; i++) if (laneNode[i]) nodes.Add((ushort)i);
            for (int i = 1; i < MaxSegments; i++)
            {
                if (!IsStrip((ushort)i)) continue;
                nodes.Add(stripStart[i]);
                nodes.Add(stripEnd[i]);
            }
            foreach (ushort n in nodes)
            {
                ushort node = n;
                SimulationManager.instance.AddAction(() => NetManager.instance.UpdateNode(node, 0, -1));
            }
        }
    }

    // Saves the lane bundles with the city.
    public class LaneBundlesData : SerializableDataExtensionBase
    {
        public override void OnLoadData()
        {
            try
            {
                LaneBundles.Deserialize(serializableDataManager.LoadData(LaneBundles.DataId));
            }
            catch (Exception e)
            {
                Mod.LogError("Loading lane bundles failed, starting with none", e);
                LaneBundles.Clear();
            }
        }

        public override void OnSaveData()
        {
            try
            {
                serializableDataManager.SaveData(LaneBundles.DataId, LaneBundles.Serialize());
            }
            catch (Exception e)
            {
                Mod.LogError("Saving lane bundles failed", e);
            }
        }
    }
}
