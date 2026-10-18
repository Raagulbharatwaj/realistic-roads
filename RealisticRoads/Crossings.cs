using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealisticRoads
{
    // How the new road meets an existing one where they cross.
    public enum CrossingType
    {
        Junction,   // at ground: junction / level crossing; otherwise pass at our own height
        Bridge,     // ours goes over on a bridge/flyover, existing untouched
        Overpass,   // existing is lowered into a cutting, ours crosses on a bridge at ground level
        Underpass,  // existing gets a short bridge, ours is lowered into a cutting under it
        Tunnel,     // ours goes through a tunnel under the existing one
    }

    // One place where the new road crosses an existing segment.
    public class Crossing
    {
        public ushort Segment;          // the existing segment
        public Vector3 Point;           // on the ground plane; y = existing road's height there
        public float Distance;          // metres along the new road's curve
        public float ExistingT;         // position along the existing segment (0..1)
        public bool SameType;           // road x road, rail x rail, ...
        public float ExistingElevation; // existing height above the ground there
        public float ExistingHalfWidth;
        public string ExistingName;
        public CrossingType Type;
        public float ExtraHeight;       // player's +/- on a Bridge crossing
        public float Skew = 1f;         // sine of the crossing angle (1 = square), set by RoadPlanner
        public bool CanConnect;         // the two networks may join (road x road, rail x rail, road x rail)
        public int Occurrence;          // 0 for the first time our curve crosses this segment, 1 the second, ...
        public ushort SnapNode;         // an end node of the segment within NodeSnap of the crossing (else 0)
        // A rail line on the ground (overhead wires): a bridge over it is 2 m
        // higher so the wires pass under the deck. Decided once, here.
        public bool OverRail;
        // A ship or ferry route: only a bridge (high enough for ships) or a tunnel.
        public bool ShipPath;

        // A junction this close to an existing node joins at that node instead of cutting the road.
        public const float NodeSnap = 6f;

        // The crossing's identity while the curve is edited (for the player's choices).
        public int Key => (Segment << 4) | Mathf.Min(Occurrence, 15);

        public bool ExistingAtGround => Mathf.Abs(ExistingElevation) < 2f;

        // Junction or level crossing: the new road connects to the existing one.
        public bool Connects => Type == CrossingType.Junction && ExistingAtGround && CanConnect;

        // Which types make sense here. Lowering or bridging the existing
        // road only works where it is on the ground.
        public bool Allows(CrossingType type)
        {
            switch (type)
            {
                case CrossingType.Overpass:
                case CrossingType.Underpass:
                    return ExistingAtGround && !ShipPath;
                case CrossingType.Tunnel:
                    return ExistingElevation > -2f;
                case CrossingType.Junction:
                    // On the ground only networks that may join can meet there.
                    return !ShipPath && (!ExistingAtGround || CanConnect);
                default:
                    return true;
            }
        }

        public string Describe()
        {
            return Describe(Type);
        }

        public string Describe(CrossingType type)
        {
            switch (type)
            {
                case CrossingType.Bridge: return SameType ? "flyover" : "bridge over the " + ExistingName;
                case CrossingType.Overpass: return "overpass (" + ExistingName + " lowered under us)";
                case CrossingType.Underpass: return "underpass (" + ExistingName + " on a short bridge)";
                case CrossingType.Tunnel: return "tunnel";
                default:
                    if (ExistingAtGround) return SameType ? "junction" : "level crossing";
                    return ExistingElevation > 0f ? "passes under it" : "passes over it";
            }
        }
    }

    public static class CrossingFinder
    {
        private const float GridCell = 64f;     // NetManager segment grid: 270 x 270 cells of 64 m
        private const int GridSize = 270;
        private const int ExistingSamples = 12;
        private const int Chunk = 8;            // path samples per bounding box

        // Main thread. Existing segments the curve 'path' crosses, in order along
        // the path. Segments touching 'ignoreNodes' (where the curve connects
        // anyway) are skipped.
        public static List<Crossing> Find(List<Vector3> path, NetInfo info, ushort ignoreNodeA, ushort ignoreNodeB)
        {
            var result = new List<Crossing>();
            if (path.Count < 2 || info == null) return result;

            var net = NetManager.instance;
            var nodes = net.m_nodes.m_buffer;
            var segments = net.m_segments.m_buffer;
            var terrain = TerrainManager.instance;

            // Distance along the path at each sample, and the path's bounds.
            var along = new float[path.Count];
            float minX = path[0].x, maxX = minX, minZ = path[0].z, maxZ = minZ;
            for (int i = 1; i < path.Count; i++)
            {
                along[i] = along[i - 1] + CurveMath.FlatDistance(path[i - 1], path[i]);
                minX = Mathf.Min(minX, path[i].x); maxX = Mathf.Max(maxX, path[i].x);
                minZ = Mathf.Min(minZ, path[i].z); maxZ = Mathf.Max(maxZ, path[i].z);
            }

            // Bounding boxes of chunks of the path, to skip most pairs quickly.
            int chunks = (path.Count - 2) / Chunk + 1;
            var boxes = new Vector4[chunks];   // minX, minZ, maxX, maxZ
            for (int c = 0; c < chunks; c++)
            {
                int first = c * Chunk, last = Mathf.Min(first + Chunk, path.Count - 1);
                var box = new Vector4(path[first].x, path[first].z, path[first].x, path[first].z);
                for (int i = first + 1; i <= last; i++) box = Grow(box, path[i]);
                boxes[c] = box;
            }

            int x0 = Cell(minX - GridCell), x1 = Cell(maxX + GridCell);
            int z0 = Cell(minZ - GridCell), z1 = Cell(maxZ + GridCell);
            var seen = new HashSet<ushort>();
            var hits = new List<Crossing>();
            var samples = new Vector3[ExistingSamples + 1];

            for (int gz = z0; gz <= z1; gz++)
            {
                for (int gx = x0; gx <= x1; gx++)
                {
                    ushort segment = net.m_segmentGrid[gz * GridSize + gx];
                    int guard = 0;
                    while (segment != 0 && guard++ < 32768)
                    {
                        ushort current = segment;
                        segment = segments[current].m_nextGridSegment;
                        if (!seen.Add(current)) continue;
                        if ((segments[current].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                        NetInfo existing = segments[current].Info;
                        if (existing == null || existing.m_class == null || !IsPhysical(existing)) continue;

                        SampleSegment(current, samples);
                        hits.Clear();
                        AllCrossings(path, along, boxes, samples, hits);
                        foreach (var crossing in hits)
                        {
                            // Where our road starts or ends on an existing node, the roads
                            // at that node meet it there anyway; other crossings with them
                            // further along (a loop back over one of them) still count.
                            float near = existing.m_halfWidth + info.m_halfWidth + 2f;
                            if (NearNode(crossing.Point, ignoreNodeA, near) || NearNode(crossing.Point, ignoreNodeB, near)) continue;

                            crossing.Segment = current;
                            crossing.SameType = existing.m_class.m_service == info.m_class.m_service &&
                                                existing.m_class.m_subService == info.m_class.m_subService;
                            crossing.CanConnect = CanJoin(info, existing);
                            float length = segments[current].m_averageLength;
                            if (crossing.ExistingT * length < Crossing.NodeSnap) crossing.SnapNode = segments[current].m_startNode;
                            else if ((1f - crossing.ExistingT) * length < Crossing.NodeSnap) crossing.SnapNode = segments[current].m_endNode;
                            crossing.ShipPath = (existing.m_class.m_layer & ShipLayer) != ItemClass.Layer.None;
                            if (crossing.ShipPath)
                            {
                                // Measured from the water: the ships sail on its surface,
                                // whatever height the route itself is kept at.
                                crossing.Point.y = terrain.SampleRawHeightSmoothWithWater(crossing.Point, false, 0f);
                                crossing.ExistingElevation = 0f;
                            }
                            else crossing.ExistingElevation = crossing.Point.y - terrain.SampleRawHeightSmooth(crossing.Point);
                            crossing.ExistingHalfWidth = existing.m_halfWidth;
                            crossing.ExistingName = crossing.ShipPath ? "ship route" : existing.name;
                            crossing.OverRail = !crossing.ShipPath && RoadPlanner.IsRail(existing) && crossing.ExistingAtGround;
                            result.Add(crossing);
                        }
                    }
                }
            }

            result.Sort((p, q) => p.Distance.CompareTo(q.Distance));
            return result;
        }

        // Something to cross: roads, tracks, paths, canals, and ship and ferry
        // routes (a bridge must leave room for the ships). Not airplane or
        // blimp routes or power lines, which the game also keeps as networks.
        // The game's ship (and ferry) route layer, looked up by name.
        private static readonly ItemClass.Layer ShipLayer = LayerNamed("ShipPaths");

        private static ItemClass.Layer LayerNamed(string name)
        {
            try { return (ItemClass.Layer)Enum.Parse(typeof(ItemClass.Layer), name); }
            catch { return ItemClass.Layer.None; }
        }

        private static bool IsPhysical(NetInfo info)
        {
            var layer = info.m_class.m_layer;
            if ((layer & (ItemClass.Layer.Default | ItemClass.Layer.MetroTunnels | ShipLayer)) == ItemClass.Layer.None) return false;
            return info.m_class.m_service != ItemClass.Service.Electricity;
        }

        // Points along an existing segment's curve (y = its real height).
        public static void SampleSegment(ushort segment, Vector3[] samples)
        {
            Vector3 a, b, c, d;
            SegmentBezier(segment, out a, out b, out c, out d);
            int last = samples.Length - 1;
            for (int i = 0; i <= last; i++) samples[i] = CurveMath.Bezier(a, b, c, d, i / (float)last);
        }

        // The point of 'segment' closest to 'near' (flat distance): its
        // position along the segment (0..1) and where it is (y = road height).
        public static float ClosestT(ushort segment, Vector3 near, out Vector3 position)
        {
            Vector3 a, b, c, d;
            SegmentBezier(segment, out a, out b, out c, out d);
            float best = 0f, bestDistance = float.MaxValue;
            const int steps = 48;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float distance = CurveMath.FlatDistance(CurveMath.Bezier(a, b, c, d, t), near);
                if (distance < bestDistance) { bestDistance = distance; best = t; }
            }
            // Refine around the best sample.
            float step = 1f / steps;
            for (int round = 0; round < 12; round++)
            {
                step *= 0.5f;
                float lower = Mathf.Max(best - step, 0f), upper = Mathf.Min(best + step, 1f);
                float dl = CurveMath.FlatDistance(CurveMath.Bezier(a, b, c, d, lower), near);
                float du = CurveMath.FlatDistance(CurveMath.Bezier(a, b, c, d, upper), near);
                if (dl < bestDistance) { bestDistance = dl; best = lower; }
                if (du < bestDistance) { bestDistance = du; best = upper; }
            }
            position = CurveMath.Bezier(a, b, c, d, best);
            return best;
        }

        public static void SegmentBezier(ushort segment, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
        {
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var s = NetManager.instance.m_segments.m_buffer[segment];
            a = nodes[s.m_startNode].m_position;
            d = nodes[s.m_endNode].m_position;
            bool smoothStart = (nodes[s.m_startNode].m_flags & NetNode.Flags.Middle) != NetNode.Flags.None;
            bool smoothEnd = (nodes[s.m_endNode].m_flags & NetNode.Flags.Middle) != NetNode.Flags.None;
            NetSegment.CalculateMiddlePoints(a, s.m_startDirection, d, s.m_endDirection, smoothStart, smoothEnd, out b, out c);
        }

        // Every place the path crosses the existing curve 'samples' (a curve
        // can cross the same segment more than once). A crossing found again on
        // the next path piece (exactly at a sample) is only counted once.
        private static void AllCrossings(List<Vector3> path, float[] along, Vector4[] boxes, Vector3[] samples, List<Crossing> hits)
        {
            var box = new Vector4(samples[0].x, samples[0].z, samples[0].x, samples[0].z);
            for (int k = 1; k < samples.Length; k++) box = Grow(box, samples[k]);

            for (int c = 0; c < boxes.Length; c++)
            {
                if (!Overlap(boxes[c], box)) continue;
                int first = c * Chunk + 1, last = Mathf.Min(c * Chunk + Chunk, path.Count - 1);
                for (int i = first; i <= last; i++)
                for (int k = 1; k < samples.Length; k++)
                {
                    float tp, ts;
                    if (!CurveMath.Intersect(path[i - 1], path[i], samples[k - 1], samples[k], out tp, out ts)) continue;
                    float distance = Mathf.Lerp(along[i - 1], along[i], tp);
                    if (hits.Count > 0 && Mathf.Abs(hits[hits.Count - 1].Distance - distance) < 1f) continue;
                    hits.Add(new Crossing
                    {
                        Point = Vector3.Lerp(samples[k - 1], samples[k], ts),
                        Distance = distance,
                        ExistingT = (k - 1 + ts) / (samples.Length - 1),
                    });
                }
            }
        }

        private static bool NearNode(Vector3 point, ushort node, float radius)
        {
            if (node == 0) return false;
            return CurveMath.FlatDistance(point, NetManager.instance.m_nodes.m_buffer[node].m_position) < radius;
        }

        // May a junction join these two networks? Roads with roads (highways
        // included), rail with rail, and a road over a railway as a level
        // crossing. Paths, canals, power lines and the like never join a road.
        // Looked up by name once (no boxing or string per call).
        private static readonly ItemClass.SubService TrainSubService = ParseSubService("PublicTransportTrain");

        private static ItemClass.SubService ParseSubService(string name)
        {
            try { return (ItemClass.SubService)Enum.Parse(typeof(ItemClass.SubService), name); }
            catch { return (ItemClass.SubService)(-1); }
        }

        public static bool CanJoin(NetInfo ours, NetInfo theirs)
        {
            var a = ours.m_class;
            var b = theirs.m_class;
            if (a == null || b == null) return false;
            if (a.m_service == b.m_service && a.m_subService == b.m_subService) return true;
            bool aRoad = a.m_service == ItemClass.Service.Road, bRoad = b.m_service == ItemClass.Service.Road;
            bool aTrain = a.m_subService == TrainSubService;
            bool bTrain = b.m_subService == TrainSubService;
            return (aRoad && bRoad) || (aRoad && bTrain) || (aTrain && bRoad);
        }

        private static Vector4 Grow(Vector4 box, Vector3 p)
        {
            return new Vector4(Mathf.Min(box.x, p.x), Mathf.Min(box.y, p.z), Mathf.Max(box.z, p.x), Mathf.Max(box.w, p.z));
        }

        private static bool Overlap(Vector4 a, Vector4 b)
        {
            const float margin = 1f;
            return a.x <= b.z + margin && b.x <= a.z + margin && a.y <= b.w + margin && b.y <= a.w + margin;
        }

        private static int Cell(float coordinate)
        {
            return Mathf.Clamp((int)(coordinate / GridCell + GridSize / 2f), 0, GridSize - 1);
        }
    }
}
