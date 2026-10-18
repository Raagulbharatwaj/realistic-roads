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
        public bool Rail;
    }

    // Decks over existing roads: double (and triple) deckers.
    public static class Decks
    {
        // The deck's clearance over the road below, like a flyover's; over rail
        // on the ground it is higher, so the wires pass under it.
        public const float Clearance = RoadPlanner.OverClearance;
        private const float PillarHalf = 1.5f;        // a pillar needs this much free either side of a road's middle
        private const float MedianOffset = 1f;        // the deck's node this close to the lower road's middle uses its median
        private const float Search = 40f;             // how far around a point roads are looked for
        private const int MaxPathSegments = 60;       // how far a deck follows a road
        private const float PathStep = 4f;            // metres between samples along a followed road

        // Main thread. The road under 'p' (its centre within its half width of
        // p), the nearest if several; Segment 0 if none.
        public static DeckBelow Under(Vector3 p)
        {
            var best = new DeckBelow();
            float bestDistance = float.MaxValue;
            var segments = NetManager.instance.m_segments.m_buffer;
            foreach (ushort segment in RoadPlanner.NearbySegments(p, Search))
            {
                var info = segments[segment].Info;
                if (info == null || info.m_class == null || !CrossingFinder.IsPhysical(info)) continue;
                Vector3 onRoad;
                float t = CrossingFinder.ClosestT(segment, p, out onRoad);
                float distance = CurveMath.FlatDistance(onRoad, p);
                if (distance > info.m_halfWidth || distance >= bestDistance) continue;
                Vector3 a, b, c, d;
                CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
                bestDistance = distance;
                best = new DeckBelow
                {
                    Segment = segment,
                    Center = onRoad,
                    Along = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t)),
                    HalfWidth = info.m_halfWidth,
                    Median = HasMedian(info),
                    Rail = RoadPlanner.IsRail(info),
                };
            }
            return best;
        }

        // The lowest the deck may be at 'p': clear of the road below (or of
        // the ground where there is none).
        public static float Floor(Vector3 p, DeckBelow below)
        {
            if (below.Segment == 0) return TerrainManager.instance.SampleRawHeightSmoothWithWater(p, false, 0f) + Clearance;
            return below.Center.y + Clearance + (below.Rail && Mathf.Abs(below.Center.y - TerrainManager.instance.SampleRawHeightSmooth(below.Center)) < 2f
                ? RoadPlanner.RailDeckLift : 0f);
        }

        // Room for a pillar in the road's middle: no lane (traffic, parking,
        // sidewalk, track) within PillarHalf of its centre line.
        public static bool HasMedian(NetInfo info)
        {
            if (info == null || info.m_lanes == null || info.m_lanes.Length == 0) return false;
            foreach (var lane in info.m_lanes)
            {
                if (lane == null || lane.m_laneType == NetInfo.LaneType.None) continue;
                if (Mathf.Abs(lane.m_position) - lane.m_width * 0.5f < PillarHalf) return false;
            }
            return true;
        }

        // Is this node's pillar fine where the game puts it (on the lower
        // road's median, or with no road below)? Else it gets a frame.
        public static bool PillarFits(Vector3 node, DeckBelow below)
        {
            return below.Segment == 0 || (below.Median && CurveMath.FlatDistance(node, below.Center) < MedianOffset);
        }

        // Main thread. The way along existing roads from (segA, tA) to (segB,
        // tB): the fewest segments, through any junction, at most
        // MaxPathSegments. Null if there is none; 'used' gets its segments.
        public static List<Vector3> RoadPath(ushort segA, float tA, ushort segB, float tB, HashSet<ushort> used)
        {
            if (segA == 0 || segB == 0) return null;
            var net = NetManager.instance;
            var segments = net.m_segments.m_buffer;
            var path = new List<Vector3>();
            if (segA == segB)
            {
                AddPart(path, segA, tA, tB);
                if (used != null) used.Add(segA);
                return path.Count >= 2 ? path : null;
            }

            // Breadth first over segments; each step leaves a segment through one of its nodes.
            var from = new Dictionary<ushort, KeyValuePair<ushort, ushort>>();   // segment -> (previous segment, node entered by)
            var queue = new Queue<KeyValuePair<ushort, ushort>>();               // (segment, node it is left by)
            var depth = new Dictionary<ushort, int> { { segA, 0 } };
            queue.Enqueue(new KeyValuePair<ushort, ushort>(segA, segments[segA].m_startNode));
            queue.Enqueue(new KeyValuePair<ushort, ushort>(segA, segments[segA].m_endNode));
            ushort found = 0;
            while (queue.Count > 0 && found == 0)
            {
                var step = queue.Dequeue();
                ushort node = step.Value;
                if (depth[step.Key] >= MaxPathSegments) continue;
                for (int i = 0; i < 8; i++)
                {
                    ushort next = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (next == 0 || next == step.Key || depth.ContainsKey(next)) continue;
                    var info = segments[next].Info;
                    if (info == null || info.m_class == null || !CrossingFinder.IsPhysical(info)) continue;
                    depth[next] = depth[step.Key] + 1;
                    from[next] = new KeyValuePair<ushort, ushort>(step.Key, node);
                    if (next == segB)
                    {
                        found = next;
                        break;
                    }
                    ushort other = segments[next].m_startNode == node ? segments[next].m_endNode : segments[next].m_startNode;
                    queue.Enqueue(new KeyValuePair<ushort, ushort>(next, other));
                }
            }
            if (found == 0) return null;

            // Back from segB to segA: each segment with the node it was entered by.
            var chain = new List<KeyValuePair<ushort, ushort>>();
            for (ushort s = segB; s != segA; s = from[s].Key) chain.Add(new KeyValuePair<ushort, ushort>(s, from[s].Value));
            chain.Reverse();
            ushort firstExit = chain[0].Value;
            AddPart(path, segA, tA, segments[segA].m_startNode == firstExit ? 0f : 1f);
            for (int k = 0; k < chain.Count; k++)
            {
                ushort s = chain[k].Key;
                float entry = segments[s].m_startNode == chain[k].Value ? 0f : 1f;
                float exit = k + 1 < chain.Count ? (segments[s].m_startNode == chain[k + 1].Value ? 0f : 1f) : tB;
                AddPart(path, s, entry, exit);
            }
            if (used != null)
            {
                used.Add(segA);
                foreach (var link in chain) used.Add(link.Key);
            }
            return path.Count >= 2 ? path : null;
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
        private const int Version = 1;
        private const float ColumnMargin = 1.5f;     // columns stand this far outside the lower road (and our deck)
        private const float Column = 1.2f;           // column width
        private const float BeamDepth = 1.4f;        // beam size along the road
        private const float BeamHeight = 1.4f;
        private const float DeckUnderside = 1.2f;    // the beam's top this far below the node (the deck's surface)
        private const float DrawDistance = 4000f;

        private class Frame
        {
            public ushort Node;
            public uint BuildIndex;
            public Vector3 A, B;                 // the columns' feet (x, z)
            public string Pillar;                // the pillar building whose material the frame uses
            public Mesh Mesh;                    // main thread
            public float MeshHeight;             // the node's height the mesh was made for
        }

        private static readonly Dictionary<ushort, Frame> frames = new Dictionary<ushort, Frame>();
        private static readonly Dictionary<string, KeyValuePair<Material, Vector2>> materials = new Dictionary<string, KeyValuePair<Material, Vector2>>();
        private static readonly HashSet<string> missing = new HashSet<string>();
        private static MaterialPropertyBlock block;
        private static bool failed;

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

        // Simulation thread, while building. A frame at 'node' across the road
        // below; the node's own pillar is removed.
        public static void Add(ushort node, DeckBelow below, float ourHalfWidth)
        {
            var nodes = NetManager.instance.m_nodes.m_buffer;
            Vector3 a, b;
            if (!Columns(below, ourHalfWidth, out a, out b)) return;
            var frame = new Frame
            {
                Node = node,
                BuildIndex = nodes[node].m_buildIndex,
                A = a,
                B = b,
                Pillar = PillarName(nodes[node].Info),
            };
            lock (frames) frames[node] = frame;
            nodes[node].UpdateBuilding(node, null, 0f);
        }

        // A new city: no frames, and materials looked up afresh.
        // Where a frame's two columns stand: either side of the road below,
        // outside it and outside our deck.
        public static bool Columns(DeckBelow below, float ourHalfWidth, out Vector3 a, out Vector3 b)
        {
            Vector3 across = CurveMath.Rotate(below.Along, 90f);
            float half = Mathf.Max(below.HalfWidth, ourHalfWidth) + ColumnMargin;
            a = below.Center + across * half;
            b = below.Center - across * half;
            return across != Vector3.zero;
        }

        public static void Clear()
        {
            lock (frames)
            {
                foreach (var frame in frames.Values) if (frame.Mesh != null) UnityEngine.Object.Destroy(frame.Mesh);
                frames.Clear();
            }
            materials.Clear();
            missing.Clear();
            failed = false;
        }

        // The pillar building of a road's elevated version (by name), for its material.
        private static string PillarName(NetInfo info)
        {
            if (info == null || info.m_netAI == null) return "";
            foreach (var field in new[] { "m_bridgePillarInfo", "m_middlePillarInfo" })
            {
                var f = info.m_netAI.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
                var pillar = f != null ? f.GetValue(info.m_netAI) as BuildingInfo : null;
                if (pillar != null) return pillar.name;
            }
            return "";
        }

        // --- drawing (main thread) ---------------------------------------------

        // Postfix of NetManager.EndRenderingImpl: every frame near the camera.
        public static void Render(RenderManager.CameraInfo cameraInfo)
        {
            if (failed) return;
            List<Frame> list;
            lock (frames)
            {
                if (frames.Count == 0) return;
                list = new List<Frame>(frames.Values);
            }
            try
            {
                var nodes = NetManager.instance.m_nodes.m_buffer;
                var stale = new List<ushort>();
                foreach (var frame in list)
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
                    if (block == null) block = new MaterialPropertyBlock();
                    block.Clear();
                    block.SetVector(Shader.PropertyToID("_BuildingState"), new Vector4(0f, 1000f, 0f, 256f));
                    block.SetVector(Shader.PropertyToID("_ObjectIndex"), RenderManager.DefaultColorLocation);
                    block.SetColor(Shader.PropertyToID("_Color"), new Color(1f, 1f, 1f, 1f));
                    var pillar = PrefabCollection<BuildingInfo>.FindLoaded(frame.Pillar);
                    int layer = pillar != null ? pillar.m_prefabDataLayer : 0;
                    Graphics.DrawMesh(frame.Mesh, n.m_position, Quaternion.identity, material.Key, layer, null, 0, block);
                }
                if (stale.Count > 0)
                    lock (frames)
                        foreach (ushort node in stale)
                        {
                            Frame frame;
                            if (frames.TryGetValue(node, out frame) && frame.Mesh != null) UnityEngine.Object.Destroy(frame.Mesh);
                            frames.Remove(node);
                        }
            }
            catch (Exception e)
            {
                failed = true;
                Mod.LogError("Drawing deck frames failed; they are not shown this session", e);
            }
        }

        // The pillar's material, and a spot on its texture (where one of its
        // own vertices maps: its concrete) to colour the whole frame with.
        private static bool MaterialFor(string pillarName, out KeyValuePair<Material, Vector2> material)
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
            foreach (var foot in new[] { a, b })
            {
                float ground = terrain.SampleRawHeightSmooth(foot) - 1f;
                Box(vertices, normals, triangles, new Vector3(foot.x, (ground + beamBottom) * 0.5f, foot.z) - node,
                    across, along, Column, beamBottom - ground, Column);
            }
            Vector3 middle = (a + b) * 0.5f;
            float span = CurveMath.FlatDistance(a, b) + Column;
            Box(vertices, normals, triangles, new Vector3(middle.x, (beamBottom + top) * 0.5f, middle.z) - node,
                across, along, span, BeamHeight, BeamDepth);

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
        private static void Face(List<Vector3> vertices, List<Vector3> normals, List<int> triangles, Vector3 c,
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
                if (version != Version)
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
