using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ColossalFramework;
using HarmonyLib;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // The road (or track) under one node of a deck: a double decker's lower
    // level. Segment 0: the deck is over open ground there.
    public struct DeckBelow
    {
        public ushort Segment;
        public Vector3 Center;        // the lower road's centre line under the node (y = its height)
        public Vector3 Along;         // its direction there, flat
        public float HalfWidth;
        public bool Median;           // room in its middle for a pillar
        public float MedianHalf;      // how far its middle is free of lanes either side
        public bool Rail;
        public bool OnGround;         // at ground level (not a deck or bridge itself)
        public float PropTop;         // how high the props and trees under our deck reach (lights, trees)
    }

    // Decks over existing roads: double (and triple) deckers.
    public static class Decks
    {
        // The deck's clearance over the road below, like a flyover's; over rail
        // on the ground it is higher, so the wires pass under it.
        public const float Clearance = RoadPlanner.OverClearance;
        private const float MinMedianHalf = 0.75f;    // a median: no lane within this of a road's middle
        private const float PropMargin = 3f;          // the deck's surface this far above the props under it
        private const float PropReach = 1f;           // props this far beyond our deck's edge still count (lamp arms)
        private const float MaxClearance = 25f;
        private const float MedianOffset = 1f;        // the deck's node this close to the lower road's middle uses its median
        private const float Search = 40f;             // how far around a point roads are looked for
        private const float MaxPathLength = 20000f;   // how far a deck follows roads (metres)
        private const int MaxPathNodes = 4000;        // nodes looked at, at most
        private const float PathStep = 4f;            // metres between samples along a followed road
        private const float OnTheNode = 0.01f;        // a point this close (in t) to a road's end is at its node

        // Main thread. The road under 'p' (its centre within its half width of
        // p); of roads stacked there (a ground road and a deck over it) the
        // highest, so a third level is measured from the second. Segment 0 if
        // none. 'ourHalfWidth': our deck's, for the props under it.
        // 'underground': for a deck under the road, the road at ground level
        // there (not one above it); one underground already is still returned,
        // so the planner can refuse a second level. 'ignore': roads not looked
        // at (our own deck where the new one joins it, not a road under it).
        public static DeckBelow Under(Vector3 p, float ourHalfWidth, bool underground = false, ICollection<ushort> ignore = null)
        {
            var best = new DeckBelow();
            float bestHeight = float.MinValue;
            var terrain = TerrainManager.instance;
            var segments = NetManager.instance.m_segments.m_buffer;
            foreach (ushort segment in RoadPlanner.NearbySegments(p, Search))
            {
                if (ignore != null && ignore.Contains(segment)) continue;
                var info = segments[segment].Info;
                if (info == null || info.m_class == null || !CrossingFinder.IsPhysical(info)) continue;
                Vector3 onRoad;
                float t = CrossingFinder.ClosestT(segment, p, out onRoad);
                float distance = CurveMath.FlatDistance(onRoad, p);
                if (distance > info.m_halfWidth || onRoad.y <= bestHeight) continue;
                if (underground && onRoad.y > terrain.SampleRawHeightSmooth(onRoad) + 2f) continue;   // above the ground: not this one
                Vector3 a, b, c, d;
                CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
                bestHeight = onRoad.y;
                float free = MedianFree(info);
                best = new DeckBelow
                {
                    Segment = segment,
                    Center = onRoad,
                    Along = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t)),
                    HalfWidth = info.m_halfWidth,
                    Median = free >= MinMedianHalf,
                    MedianHalf = free,
                    Rail = RoadPlanner.IsRail(info),
                    OnGround = Mathf.Abs(onRoad.y - TerrainManager.instance.SampleRawHeightSmooth(onRoad)) < 2f,
                    // Props under our deck: as far out from the road's middle as our
                    // deck reaches from it (it may sit off the middle).
                    PropTop = PropTop(info, distance + ourHalfWidth + PropReach),
                };
            }
            return best;
        }

        // The lowest the deck may be at 'p': clear of the road below (or of
        // the ground where there is none).
        public static float Floor(Vector3 p, DeckBelow below)
        {
            float clearance = Clearance;
            if (below.Segment == 0) return TerrainManager.instance.SampleRawHeightSmoothWithWater(p, false, 0f) + clearance;
            // Above the lights and trees under it too (with the deck's own depth under it).
            clearance = Mathf.Max(clearance, Mathf.Min(below.PropTop + PropMargin, MaxClearance));
            return below.Center.y + clearance + (below.Rail && below.OnGround ? RoadPlanner.RailDeckLift : 0f);
        }

        // A deck under the road: at least one level (a tunnel's depth) below
        // the road there, or below the ground where there is none. Deeper only
        // where its grade needs it (under a street climbing more steeply).
        public const float UnderDepth = RoadPlanner.TunnelDepth;

        public static float Ceiling(Vector3 p, DeckBelow below)
        {
            float top = below.Segment != 0 ? below.Center.y : TerrainManager.instance.SampleRawHeightSmooth(p);
            return top - UnderDepth;
        }

        // How far a road's middle is free of lanes (traffic, parking, sidewalk,
        // track) either side; 0 if a lane runs down the middle.
        public static float MedianFree(NetInfo info)
        {
            if (info == null || info.m_lanes == null || info.m_lanes.Length == 0) return 0f;
            float free = info.m_halfWidth;
            foreach (var lane in info.m_lanes)
            {
                if (lane == null || lane.m_laneType == NetInfo.LaneType.None) continue;
                free = Mathf.Min(free, Mathf.Abs(lane.m_position) - lane.m_width * 0.5f);
            }
            return Mathf.Max(free, 0f);
        }

        public static bool HasMedian(NetInfo info) => MedianFree(info) >= MinMedianHalf;

        // A road's props and trees (street lights, median trees): how far each
        // stands from the road's middle and how high it reaches above the road.
        // Main thread (mesh bounds); kept per road.
        private static readonly Dictionary<string, List<KeyValuePair<float, float>>> props =
            new Dictionary<string, List<KeyValuePair<float, float>>>();

        private static List<KeyValuePair<float, float>> PropsOf(NetInfo info)
        {
            List<KeyValuePair<float, float>> list;
            if (props.TryGetValue(info.name ?? "", out list)) return list;
            list = new List<KeyValuePair<float, float>>();
            try
            {
                foreach (var lane in info.m_lanes)
                {
                    if (lane == null || lane.m_laneProps == null || lane.m_laneProps.m_props == null) continue;
                    foreach (var prop in lane.m_laneProps.m_props)
                    {
                        if (prop == null) continue;
                        var propInfo = prop.m_finalProp ?? prop.m_prop;
                        var tree = prop.m_finalTree ?? prop.m_tree;
                        Mesh mesh = propInfo != null ? propInfo.m_mesh : tree != null ? tree.m_mesh : null;
                        if (mesh == null) continue;
                        // A prop's x is mirrored on lanes running the other way: the nearer
                        // of the two to the middle, so none is missed.
                        float out1 = Mathf.Abs(lane.m_position + prop.m_position.x), out2 = Mathf.Abs(lane.m_position - prop.m_position.x);
                        list.Add(new KeyValuePair<float, float>(Mathf.Min(out1, out2), prop.m_position.y + mesh.bounds.max.y));
                    }
                }
            }
            catch { }
            props[info.name ?? ""] = list;
            return list;
        }

        // The top of the road's props within 'reach' of its middle; 0 if none.
        public static float PropTop(NetInfo info, float reach)
        {
            if (info == null || info.m_lanes == null) return 0f;
            float top = 0f;
            foreach (var prop in PropsOf(info))
                if (prop.Key <= reach) top = Mathf.Max(top, prop.Value);
            return top;
        }

        // What carries a deck's node.
        public enum Support { Pillar, Column, Portal }

        // The game's own pillar over open ground (it won't put one on another
        // road); over a road with a median and the node over its middle, our
        // column on the median; over any other road, our frame across it.
        public static Support SupportFor(Vector3 node, DeckBelow below)
        {
            if (below.Segment == 0) return Support.Pillar;
            return below.Median && CurveMath.FlatDistance(node, below.Center) < MedianOffset ? Support.Column : Support.Portal;
        }

        // Main thread. The way along existing roads from (segA, tA) to (segB,
        // tB): the shortest in metres, through any junction, at most MaxPathLength.
        // A point at a node (t 0 or 1) is that node, whichever of its roads it
        // was given with. Null if there is none; 'used' gets its segments.
        // With 'kindOf', only along roads of its kind (SameKind).
        public static List<Vector3> RoadPath(ushort segA, float tA, ushort segB, float tB, HashSet<ushort> used, NetInfo kindOf = null)
        {
            if (segA == 0 || segB == 0) return null;
            var net = NetManager.instance;
            var segments = net.m_segments.m_buffer;
            var path = new List<Vector3>();
            float lengthA = Mathf.Max(segments[segA].m_averageLength, 1f), lengthB = Mathf.Max(segments[segB].m_averageLength, 1f);
            ushort aStart = segments[segA].m_startNode, aEnd = segments[segA].m_endNode;
            ushort bStart = segments[segB].m_startNode, bEnd = segments[segB].m_endNode;

            // Along the one segment, unless going round through the network is shorter.
            float direct = segA == segB ? Mathf.Abs(tA - tB) * lengthA : float.MaxValue;

            // Shortest distances to nodes from the start point (Dijkstra over nodes;
            // each road is an edge as long as the road).
            var distance = new Dictionary<ushort, float> { { aStart, tA * lengthA } };
            float toEnd = (1f - tA) * lengthA;
            if (!distance.ContainsKey(aEnd) || toEnd < distance[aEnd]) distance[aEnd] = toEnd;
            var previous = new Dictionary<ushort, KeyValuePair<ushort, ushort>>();   // node -> (node before, road between)
            var done = new HashSet<ushort>();
            var open = new Heap();
            foreach (var pair in distance) open.Push(pair.Value, pair.Key);
            float best = direct;
            ushort bestNode = 0;
            while (open.Count > 0)
            {
                float nodeDistance;
                ushort node = open.Pop(out nodeDistance);
                if (done.Contains(node) || nodeDistance > distance[node]) continue;   // an outdated entry
                if (nodeDistance >= best || nodeDistance > MaxPathLength || done.Count > MaxPathNodes) break;
                done.Add(node);
                // Onto the target road from this node.
                float finish = node == bStart ? tB * lengthB : node == bEnd ? (1f - tB) * lengthB : float.MaxValue;
                if (nodeDistance + finish < best)
                {
                    best = nodeDistance + finish;
                    bestNode = node;
                }
                for (int i = 0; i < 8; i++)
                {
                    ushort road = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (road == 0) continue;
                    var info = segments[road].Info;
                    if (info == null || info.m_class == null || !CrossingFinder.IsPhysical(info)) continue;
                    if (kindOf != null && !RoadPlanner.SameKind(info, kindOf)) continue;   // only along roads of this kind
                    ushort other = segments[road].m_startNode == node ? segments[road].m_endNode : segments[road].m_startNode;
                    float through = nodeDistance + Mathf.Max(segments[road].m_averageLength, 1f);
                    float known;
                    if (done.Contains(other) || (distance.TryGetValue(other, out known) && known <= through)) continue;
                    distance[other] = through;
                    previous[other] = new KeyValuePair<ushort, ushort>(node, road);
                    open.Push(through, other);
                }
            }

            if (bestNode == 0)
            {
                if (segA != segB) return null;
                AddPart(path, segA, tA, tB);
                if (used != null) used.Add(segA);
                return path.Count >= 2 ? path : null;
            }

            // Back from the node we leave by onto segB to the one we started from.
            var chain = new List<KeyValuePair<ushort, ushort>>();   // (road, node it is entered by), in order
            ushort at = bestNode;
            while (previous.ContainsKey(at))
            {
                var step = previous[at];
                chain.Add(new KeyValuePair<ushort, ushort>(step.Value, step.Key));
                at = step.Key;
            }
            chain.Reverse();
            // 'at' is now the first node: off segA towards it. A road is only
            // "used" (run along) if the path really goes along it: a point
            // clicked on a junction comes with any one of its roads, maybe a
            // cross street the deck never follows.
            float exitA = at == aStart ? 0f : 1f, entryB = bestNode == bStart ? 0f : 1f;
            AddPart(path, segA, tA, exitA);
            if (used != null && Mathf.Abs(tA - exitA) > OnTheNode) used.Add(segA);
            foreach (var link in chain)
            {
                ushort road = link.Key;
                float entry = segments[road].m_startNode == link.Value ? 0f : 1f;
                AddPart(path, road, entry, 1f - entry);
                if (used != null) used.Add(road);
            }
            AddPart(path, segB, entryB, tB);
            if (used != null && Mathf.Abs(tB - entryB) > OnTheNode) used.Add(segB);
            return path.Count >= 2 ? path : null;
        }

        // A min-heap of (distance, node) for the search above.
        private class Heap
        {
            private readonly List<KeyValuePair<float, ushort>> items = new List<KeyValuePair<float, ushort>>();
            public int Count => items.Count;

            public void Push(float key, ushort node)
            {
                items.Add(new KeyValuePair<float, ushort>(key, node));
                for (int i = items.Count - 1; i > 0;)
                {
                    int parent = (i - 1) / 2;
                    if (items[parent].Key <= items[i].Key) break;
                    var swap = items[i]; items[i] = items[parent]; items[parent] = swap;
                    i = parent;
                }
            }

            public ushort Pop(out float key)
            {
                var top = items[0];
                key = top.Key;
                items[0] = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                for (int i = 0; ;)
                {
                    int left = 2 * i + 1, right = left + 1, smallest = i;
                    if (left < items.Count && items[left].Key < items[smallest].Key) smallest = left;
                    if (right < items.Count && items[right].Key < items[smallest].Key) smallest = right;
                    if (smallest == i) break;
                    var swap = items[i]; items[i] = items[smallest]; items[smallest] = swap;
                    i = smallest;
                }
                return top.Value;
            }
        }

        // The preview plans again on every mouse move: paths between points
        // already placed are kept (until the curve is built or cleared).
        private static readonly Dictionary<string, KeyValuePair<List<Vector3>, ushort[]>> pathCache =
            new Dictionary<string, KeyValuePair<List<Vector3>, ushort[]>>();

        public static List<Vector3> CachedRoadPath(ushort segA, float tA, ushort segB, float tB, HashSet<ushort> used)
        {
            string key = segA + ":" + tA.ToString("F3") + ">" + segB + ":" + tB.ToString("F3");
            KeyValuePair<List<Vector3>, ushort[]> hit;
            if (!pathCache.TryGetValue(key, out hit))
            {
                var segs = new HashSet<ushort>();
                var path = RoadPath(segA, tA, segB, tB, segs);
                hit = new KeyValuePair<List<Vector3>, ushort[]>(path, new List<ushort>(segs).ToArray());
                if (pathCache.Count >= 64) pathCache.Clear();
                pathCache[key] = hit;
            }
            if (used != null) foreach (ushort s in hit.Value) used.Add(s);
            return hit.Key;
        }

        public static void ForgetPaths()
        {
            pathCache.Clear();
        }

        // Samples of a segment's curve from t0 to t1 (either way round), after
        // what 'path' already has (the joint isn't repeated).
        private static void AddPart(List<Vector3> path, ushort segment, float t0, float t1)
        {
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            float length = NetManager.instance.m_segments.m_buffer[segment].m_averageLength * Mathf.Abs(t1 - t0);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / PathStep));
            for (int k = 0; k <= steps; k++)
            {
                Vector3 p = CurveMath.Bezier(a, b, c, d, Mathf.Lerp(t0, t1, k / (float)steps));
                if (path.Count > 0 && CurveMath.FlatDistance(path[path.Count - 1], p) < 0.05f) continue;
                path.Add(p);
            }
        }
    }

    // Portal frames: where a deck's node stands over a road with no median,
    // two columns stand beside the lower road and a beam across carries the
    // deck, instead of the game's pillar (which would stand on the road). The
    // frames are our own meshes, drawn with the material of the road's
    // pillars, and saved with the city.
    public static class DeckFrames
    {
        public const string DataId = "RealisticRoads.DeckFrames";
        private const int Version = 3;
        private const float ColumnMargin = 1.5f;     // columns stand this far outside the lower road (and our deck)
        private const float Column = 2f;             // column width (a frame's two columns)
        private const float MinMedianColumn = 2.4f;  // a single column on a median: as wide as the median allows,
        private const float MaxMedianColumn = 3.6f;  //   within these
        private const float CapInset = 1f;           // a median column's cap stops this far inside our deck's edges
        private const float MinColumn = 0.5f;
        private const string FallbackPillar = "HighwayBridgePillar";        // shortest column
        private const float BeamDepth = 2.4f;        // beam size along the road
        private const float BeamHeight = 1.8f;
        private const float DeckUnderside = 1.2f;    // the beam's top this far below the node (the deck's surface)
        private const float DrawDistance = 4000f;

        private class Frame
        {
            public ushort Node;
            public uint BuildIndex;
            public Vector3 A, B;                 // the beam's ends (x, z); a frame's columns stand under them
            public bool Median;                  // one column on the lower road's median instead of two beside it
            public Vector3 Foot;                 // a median column: where it stands (y = the road's surface)
            public float Width = Column;         // the column's width
            public string Pillar;                // the pillar building whose material the frame uses
            public Mesh Mesh;                    // main thread
            public float MeshHeight;             // the node's height the mesh was made for
        }

        private static readonly Dictionary<ushort, Frame> frames = new Dictionary<ushort, Frame>();
        private static readonly Dictionary<string, KeyValuePair<Material, Vector2>> materials = new Dictionary<string, KeyValuePair<Material, Vector2>>();
        private static readonly HashSet<string> missing = new HashSet<string>();
        private static MaterialPropertyBlock block;
        private static readonly HashSet<ushort> failedFrames = new HashSet<ushort>();   // drawing threw: skipped (logged once)
        private static readonly List<Frame> drawn = new List<Frame>();   // this frame's frames (kept, not made anew)

        // Any thread (the game asks for node pillars on the simulation thread).
        // True if this node carries one of our frames (so no pillar of its own).
        public static bool Holds(ushort node)
        {
            lock (frames)
            {
                Frame frame;
                if (!frames.TryGetValue(node, out frame)) return false;
                var n = NetManager.instance.m_nodes.m_buffer[node];
                return (n.m_flags & NetNode.Flags.Created) != NetNode.Flags.None && n.m_buildIndex == frame.BuildIndex;
            }
        }

        // Simulation thread, while building. Our support at 'node': a column
        // on the median or a frame across the road below; the node's own
        // pillar is removed (the game wouldn't put it on the road anyway).
        public static void Add(ushort node, DeckBelow below, float ourHalfWidth, bool median)
        {
            var nodes = NetManager.instance.m_nodes.m_buffer;
            Vector3 a, b;
            if (!Beam(below, ourHalfWidth, median, out a, out b)) return;
            var frame = new Frame
            {
                Node = node,
                BuildIndex = nodes[node].m_buildIndex,
                A = a,
                B = b,
                Median = median,
                Foot = below.Center,
                Width = median ? Mathf.Clamp(2f * below.MedianHalf, MinMedianColumn, MaxMedianColumn) : Column,
                Pillar = PillarName(nodes[node].Info),
            };
            lock (frames) frames[node] = frame;
            nodes[node].UpdateBuilding(node, null, 0f);
        }

        // The beam's ends. A frame: either side of the road below, outside it
        // and outside our deck (its columns stand there). A median column: a
        // cap under our deck, a little narrower than it.
        public static bool Beam(DeckBelow below, float ourHalfWidth, bool median, out Vector3 a, out Vector3 b)
        {
            Vector3 across = CurveMath.Rotate(below.Along, 90f);
            float half = median ? Mathf.Max(ourHalfWidth - CapInset, MaxMedianColumn)
                : Mathf.Max(below.HalfWidth, ourHalfWidth) + ColumnMargin;
            a = below.Center + across * half;
            b = below.Center - across * half;
            return across != Vector3.zero;
        }

        // A new city: no frames, and materials looked up afresh.
        public static void Clear()
        {
            // Loading runs off the main thread: meshes go to Render to be destroyed.
            lock (frames)
            {
                lock (unused)
                    foreach (var frame in frames.Values) if (frame.Mesh != null) unused.Add(frame.Mesh);
                frames.Clear();
            }
            materials.Clear();
            missing.Clear();
            failedFrames.Clear();
        }

        // The pillar building of a road's elevated version (by name), for its material.
        internal static string PillarName(NetInfo info)
        {
            if (info == null || info.m_netAI == null) return FallbackPillar;
            foreach (var field in new[] { "m_bridgePillarInfo", "m_middlePillarInfo" })
            {
                var f = info.m_netAI.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
                var pillar = f != null ? f.GetValue(info.m_netAI) as BuildingInfo : null;
                if (pillar != null) return pillar.name;
            }
            // No concrete pillar of its own: the highway's.
            return FallbackPillar;
        }

        // --- drawing (main thread) ---------------------------------------------

        // Postfix of NetManager.EndRenderingImpl: every frame near the camera.
        public static void Render(RenderManager.CameraInfo cameraInfo)
        {
            lock (unused)
            {
                foreach (var mesh in unused) if (mesh != null) UnityEngine.Object.Destroy(mesh);
                unused.Clear();
            }
            var list = drawn;
            lock (frames)
            {
                if (frames.Count == 0) return;
                list.Clear();
                list.AddRange(frames.Values);
            }
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var stale = new List<ushort>();
            foreach (var frame in list)
            {
                if (failedFrames.Contains(frame.Node)) continue;
                try
                {
                    var n = nodes[frame.Node];
                    if ((n.m_flags & NetNode.Flags.Created) == NetNode.Flags.None || n.m_buildIndex != frame.BuildIndex)
                    {
                        stale.Add(frame.Node);
                        continue;
                    }
                    if (CurveMath.FlatDistance(n.m_position, cameraInfo.m_position) > DrawDistance) continue;
                    KeyValuePair<Material, Vector2> material;
                    if (!MaterialFor(frame.Pillar, out material)) continue;
                    if (frame.Mesh == null || Mathf.Abs(frame.MeshHeight - n.m_position.y) > 0.05f)
                    {
                        if (frame.Mesh != null) UnityEngine.Object.Destroy(frame.Mesh);
                        frame.Mesh = MakeMesh(frame, n.m_position, material.Value);
                        frame.MeshHeight = n.m_position.y;
                    }
                    Draw(frame.Mesh, n.m_position, frame.Pillar, material.Key);
                }
                catch (Exception e)
                {
                    // Only this frame: the others are still drawn.
                    failedFrames.Add(frame.Node);
                    if (failedFrames.Count <= 16) Mod.LogError("Drawing the deck frame at node #" + frame.Node + " failed; it is not shown", e);
                }
            }
            if (stale.Count > 0) Forget(stale);
        }

        // A mesh of ours in the pillar's material, lit like a building. Main thread.
        internal static void Draw(Mesh mesh, Vector3 position, string pillarName, Material material)
        {
            if (block == null) block = new MaterialPropertyBlock();
            block.Clear();
            block.SetVector(Shader.PropertyToID("_BuildingState"), new Vector4(0f, 1000f, 0f, 256f));
            block.SetVector(Shader.PropertyToID("_ObjectIndex"), RenderManager.DefaultColorLocation);
            block.SetColor(Shader.PropertyToID("_Color"), new Color(1f, 1f, 1f, 1f));
            var pillar = PrefabCollection<BuildingInfo>.FindLoaded(pillarName);
            int layer = pillar != null ? pillar.m_prefabDataLayer : 0;
            Graphics.DrawMesh(mesh, position, Quaternion.identity, material, layer, null, 0, block);
        }

        // A mesh from faces, every vertex on the material's concrete ('uv').
        internal static Mesh MakeMesh(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, Vector2 uv)
        {
            var mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.normals = normals.ToArray();
            var uvs = new Vector2[vertices.Count];
            var colors = new Color[vertices.Count];
            var tangents = new Vector4[vertices.Count];
            for (int i = 0; i < uvs.Length; i++)
            {
                uvs[i] = uv;
                colors[i] = new Color(1f, 1f, 1f, 1f);
                tangents[i] = new Vector4(1f, 0f, 0f, -1f);
            }
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.tangents = tangents;
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateBounds();
            return mesh;
        }

        // Frames whose nodes are gone (Ctrl+Z, bulldozed). Any thread.
        public static void Forget(IEnumerable<ushort> nodes)
        {
            var meshes = new List<Mesh>();
            lock (frames)
                foreach (ushort node in nodes)
                {
                    Frame frame;
                    if (!frames.TryGetValue(node, out frame)) continue;
                    if (frame.Mesh != null) meshes.Add(frame.Mesh);
                    frames.Remove(node);
                }
            // Meshes are Unity objects: destroyed on the main thread, in Render.
            lock (unused) unused.AddRange(meshes);
        }

        private static readonly List<Mesh> unused = new List<Mesh>();

        // After a city loads (simulation thread): nodes carrying a frame have
        // no pillar of their own, whatever was saved.
        public static void RemovePillars()
        {
            try
            {
                List<Frame> list;
                lock (frames) list = new List<Frame>(frames.Values);
                var nodes = NetManager.instance.m_nodes.m_buffer;
                int removed = 0;
                foreach (var frame in list)
                {
                    if (!Holds(frame.Node) || nodes[frame.Node].m_building == 0) continue;
                    nodes[frame.Node].UpdateBuilding(frame.Node, null, 0f);
                    removed++;
                }
                if (removed > 0) Mod.Log("Deck frames: removed " + removed + " pillar(s) standing on the road below");
            }
            catch (Exception e)
            {
                Mod.LogError("Checking the pillars under deck frames failed", e);
            }
        }

        // The pillar's material, and a spot on its texture (where one of its
        // own vertices maps: its concrete) to colour the whole frame with.
        internal static bool MaterialFor(string pillarName, out KeyValuePair<Material, Vector2> material)
        {
            if (materials.TryGetValue(pillarName ?? "", out material)) return true;
            if (missing.Contains(pillarName ?? "")) return false;
            var pillar = string.IsNullOrEmpty(pillarName) ? null : PrefabCollection<BuildingInfo>.FindLoaded(pillarName);
            if (pillar == null || pillar.m_material == null)
            {
                missing.Add(pillarName ?? "");
                Mod.Log("Deck frames: no pillar material for '" + pillarName + "'; those frames are not drawn");
                return false;
            }
            Vector2 uv = new Vector2(0.5f, 0.5f);
            try
            {
                var uvs = pillar.m_mesh != null ? pillar.m_mesh.uv : null;
                if (uvs != null && uvs.Length > 0) uv = uvs[0];
            }
            catch { }
            material = new KeyValuePair<Material, Vector2>(pillar.m_material, uv);
            materials[pillarName] = material;
            Mod.Log("Deck frames: using the material of " + pillarName);
            return true;
        }

        // Two columns from the ground to the beam, and the beam across under
        // the deck, relative to the node.
        private static Mesh MakeMesh(Frame frame, Vector3 node, Vector2 uv)
        {
            var terrain = TerrainManager.instance;
            float top = node.y - DeckUnderside;
            float beamBottom = top - BeamHeight;
            Vector3 a = new Vector3(frame.A.x, 0f, frame.A.z), b = new Vector3(frame.B.x, 0f, frame.B.z);
            Vector3 across = CurveMath.FlatNormal(a - b);
            Vector3 along = CurveMath.Rotate(across, 90f);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            if (frame.Median)
            {
                // One column from the median (just into the road's surface) up to the cap.
                float bottom = Mathf.Min(frame.Foot.y - 0.5f, beamBottom - MinColumn);
                Box(vertices, normals, triangles, new Vector3(frame.Foot.x, (bottom + beamBottom) * 0.5f, frame.Foot.z) - node,
                    across, along, frame.Width, beamBottom - bottom, frame.Width);
            }
            else
                foreach (var foot in new[] { a, b })
                {
                    // From just under the ground up to the beam; never upside down (the
                    // ground beside an elevated road below can be higher than the beam).
                    float ground = Mathf.Min(terrain.SampleRawHeightSmooth(foot) - 1f, beamBottom - MinColumn);
                    Box(vertices, normals, triangles, new Vector3(foot.x, (ground + beamBottom) * 0.5f, foot.z) - node,
                        across, along, frame.Width, beamBottom - ground, frame.Width);
                }
            Vector3 middle = (a + b) * 0.5f;
            float span = CurveMath.FlatDistance(a, b) + (frame.Median ? 0f : frame.Width);
            Box(vertices, normals, triangles, new Vector3(middle.x, (beamBottom + top) * 0.5f, middle.z) - node,
                across, along, span, BeamHeight, BeamDepth);
            return MakeMesh(vertices, normals, triangles, uv);
        }

        // A box at 'center': 'width' across, 'height' up, 'depth' along.
        private static void Box(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, Vector3 center,
            Vector3 across, Vector3 along, float width, float height, float depth)
        {
            Vector3 x = across * (width * 0.5f), y = Vector3.up * (height * 0.5f), z = along * (depth * 0.5f);
            Face(vertices, normals, triangles, center + x, y, z, across);
            Face(vertices, normals, triangles, center - x, y, z, -across);
            Face(vertices, normals, triangles, center + z, y, x, along);
            Face(vertices, normals, triangles, center - z, y, x, -along);
            Face(vertices, normals, triangles, center + y, x, z, Vector3.up);
            Face(vertices, normals, triangles, center - y, x, z, -Vector3.up);
        }

        // One face: centred at 'c', spanning ±u and ±v, facing 'normal'.
        internal static void Face(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, Vector3 c,
            Vector3 u, Vector3 v, Vector3 normal)
        {
            int first = vertices.Count;
            vertices.Add(c - u - v);
            vertices.Add(c + u - v);
            vertices.Add(c + u + v);
            vertices.Add(c - u + v);
            for (int i = 0; i < 4; i++) normals.Add(normal);
            // Wound so the face is seen from outside (from 'normal').
            Vector3 cross = Vector3.Cross(u, v);
            bool flip = Vector3.Dot(cross, normal) < 0f;
            if (flip)
            {
                triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1);
                triangles.Add(first); triangles.Add(first + 3); triangles.Add(first + 2);
            }
            else
            {
                triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
                triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
            }
        }

        // --- saving ------------------------------------------------------------

        public static byte[] Serialize()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                List<Frame> list;
                lock (frames) list = new List<Frame>(frames.Values);
                writer.Write(Version);
                writer.Write(list.Count);
                foreach (var frame in list)
                {
                    writer.Write(frame.Node);
                    writer.Write(frame.BuildIndex);
                    writer.Write(frame.A.x); writer.Write(frame.A.z);
                    writer.Write(frame.B.x); writer.Write(frame.B.z);
                    writer.Write(frame.Pillar ?? "");
                    writer.Write(frame.Median);
                    writer.Write(frame.Foot.x); writer.Write(frame.Foot.y); writer.Write(frame.Foot.z);
                    writer.Write(frame.Width);
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
                int version = reader.ReadInt32();
                if (version < 1 || version > Version)
                {
                    Mod.Log("Deck frames: saved with an unknown version " + version + ", not loaded");
                    return;
                }
                int count = reader.ReadInt32();
                lock (frames)
                    for (int i = 0; i < count; i++)
                    {
                        var frame = new Frame
                        {
                            Node = reader.ReadUInt16(),
                            BuildIndex = reader.ReadUInt32(),
                        };
                        float ax = reader.ReadSingle(), az = reader.ReadSingle(), bx = reader.ReadSingle(), bz = reader.ReadSingle();
                        frame.A = new Vector3(ax, 0f, az);
                        frame.B = new Vector3(bx, 0f, bz);
                        frame.Pillar = reader.ReadString();
                        if (version >= 2)
                        {
                            frame.Median = reader.ReadBoolean();
                            float fx = reader.ReadSingle(), fy = reader.ReadSingle(), fz = reader.ReadSingle();
                            frame.Foot = new Vector3(fx, fy, fz);
                        }
                        // Saved before the width was kept: the width these supports now get.
                        frame.Width = version >= 3 ? reader.ReadSingle() : frame.Median ? MinMedianColumn : Column;
                        frames[frame.Node] = frame;
                    }
                Mod.Log("Deck frames loaded: " + count);
            }
        }
    }

    // Saves the deck frames with the city.
    public class DeckFramesData : SerializableDataExtensionBase
    {
        public override void OnLoadData()
        {
            try
            {
                DeckFrames.Deserialize(serializableDataManager.LoadData(DeckFrames.DataId));
            }
            catch (Exception e)
            {
                Mod.LogError("Loading deck frames failed, starting with none", e);
                DeckFrames.Clear();
            }
        }

        public override void OnSaveData()
        {
            try
            {
                serializableDataManager.SaveData(DeckFrames.DataId, DeckFrames.Serialize());
            }
            catch (Exception e)
            {
                Mod.LogError("Saving deck frames failed", e);
            }
        }
    }

    // A node carrying a deck frame has no pillar of its own: the game asks the
    // road's AI for the node's pillar whenever the node is updated.
    [HarmonyPatch]
    public static class DeckPillarPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in typeof(NetAI).Assembly.GetTypes())
            {
                if (type != typeof(NetAI) && !type.IsSubclassOf(typeof(NetAI))) continue;
                var method = type.GetMethod("GetNodeBuilding", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method != null && method.GetParameters().Length == 4) yield return method;
            }
        }

        public static bool Prefix(ushort __0, ref BuildingInfo __2, ref float __3)
        {
            if (!DeckFrames.Holds(__0)) return true;
            __2 = null;
            __3 = 0f;
            return false;
        }
    }

    // Draws the deck frames with the rest of the roads.
    [HarmonyPatch(typeof(NetManager), "EndRenderingImpl")]
    public static class DeckRenderPatch
    {
        public static void Postfix(RenderManager.CameraInfo __0)
        {
            DeckFrames.Render(__0);
        }
    }
}
