using System;
using System.Collections.Generic;
using System.IO;
using ColossalFramework.Math;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // CS2-style junctions: at a marked node the vanilla junction piece (with
    // its crosswalks and median blocks) is not drawn. Instead we draw a flat
    // asphalt floor and raised sidewalk corners that curve from one road to
    // the next, and push the road ends back so the corners are rounder.
    //
    // Traffic is untouched: the game builds vehicle lanes from the same road
    // ends (CalculateCorner), so cars follow the new shape. Without the mod a
    // marked node is a plain vanilla junction again.
    public static class Junctions
    {
        public const string DataId = "RealisticRoads.Junctions";
        public const float DefaultCornerExtra = 3f;  // metres the road ends move back
        public const float MaxCornerExtra = 20f;
        private const int MaxNodes = 32768;
        private const float MovedTolerance = 2f;
        private const float MaxGroundElevation = 2f;

        // Written on the main/simulation thread, read from any thread.
        private static readonly bool[] enabled = new bool[MaxNodes];
        private static readonly Vector3[] positions = new Vector3[MaxNodes];
        private static readonly float[] cornerExtra = new float[MaxNodes];

        public static bool IsEnabled(ushort node)
        {
            if (!enabled[node]) return false;
            var current = NetManager.instance.m_nodes.m_buffer[node].m_position;
            if ((current - positions[node]).sqrMagnitude > MovedTolerance * MovedTolerance) return false;
            return !SmoothSplits.IsEnabled(node);
        }

        // A junction of at least three ground-level roads.
        public static bool CanStyle(ushort node, out string reason)
        {
            var net = NetManager.instance;
            var n = net.m_nodes.m_buffer[node];
            reason = null;
            if ((n.m_flags & NetNode.Flags.Created) == NetNode.Flags.None) { reason = "node does not exist"; return false; }
            if (SmoothSplits.IsEnabled(node)) { reason = "it is a smooth split"; return false; }
            int roads = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort segment = n.GetSegment(i);
                if (segment == 0) continue;
                var info = net.m_segments.m_buffer[segment].Info;
                if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Road)
                {
                    reason = "only road junctions (not rail or paths)";
                    return false;
                }
                roads++;
            }
            if (roads < 3) { reason = "needs at least 3 roads, has " + roads; return false; }
            float elevation = n.m_position.y - TerrainManager.instance.SampleRawHeightSmooth(n.m_position);
            if ((n.m_flags & NetNode.Flags.Underground) != NetNode.Flags.None || Mathf.Abs(elevation) > MaxGroundElevation)
            {
                reason = "only junctions on the ground for now";
                return false;
            }
            return true;
        }

        public static float CornerExtra(ushort node)
        {
            return cornerExtra[node];
        }

        // Main thread. Returns the new state.
        public static bool Toggle(ushort node)
        {
            if (IsEnabled(node))
            {
                enabled[node] = false;
                Mod.Log("CS2-style junction OFF at node #" + node);
                Refresh(node);
                return false;
            }
            string reason;
            if (!CanStyle(node, out reason))
            {
                Mod.Log("Node #" + node + " can't get a CS2-style junction: " + reason);
                return false;
            }
            Enable(node, DefaultCornerExtra);
            Mod.Log("CS2-style junction ON at node #" + node);
            return true;
        }

        // Simulation thread: used by the curve tool for the junctions it builds.
        // Automatic look for a junction the curve tool just made: only when the
        // setting is on, and only where every road has sidewalks and none is a
        // highway (ramps don't get curbs).
        public static void EnableIfJunction(ushort node)
        {
            string reason;
            if (!Mod.AutoJunctions || IsEnabled(node) || !CanStyle(node, out reason) || !AllStreets(node)) return;
            Enable(node, DefaultCornerExtra);
            Mod.Log("CS2-style junction applied at node #" + node);
        }

        public static void AdjustCorner(ushort node, float step)
        {
            if (!IsEnabled(node)) return;
            cornerExtra[node] = Mathf.Clamp(cornerExtra[node] + step, 0f, MaxCornerExtra);
            Mod.Log("Junction #" + node + ": corners " + cornerExtra[node].ToString("F0") + " m further out");
            Refresh(node);
        }

        private static bool AllStreets(ushort node)
        {
            var net = NetManager.instance;
            var n = net.m_nodes.m_buffer[node];
            for (int i = 0; i < 8; i++)
            {
                ushort segment = n.GetSegment(i);
                if (segment == 0) continue;
                var info = net.m_segments.m_buffer[segment].Info;
                if (info == null || info.m_pavementWidth < 0.5f) return false;
                var ai = info.m_netAI;
                var rules = ai != null ? ai.GetType().GetField("m_highwayRules") : null;
                if (rules != null && rules.GetValue(ai) is bool && (bool)rules.GetValue(ai)) return false;
            }
            return true;
        }

        private static void Enable(ushort node, float extra)
        {
            positions[node] = NetManager.instance.m_nodes.m_buffer[node].m_position;
            cornerExtra[node] = extra;
            enabled[node] = true;
            Refresh(node);
        }

        public static void Refresh(ushort node)
        {
            JunctionRenderer.Forget(node);
            SimulationManager.instance.AddAction(() => NetManager.instance.UpdateNode(node, 0, -1));
        }

        public static void RefreshAll()
        {
            int count = 0;
            for (int i = 1; i < MaxNodes; i++)
            {
                if (!enabled[i]) continue;
                Refresh((ushort)i);
                count++;
            }
            if (count > 0) Mod.Log("Refreshing " + count + " CS2-style junction(s)");
        }

        public static void Clear()
        {
            Array.Clear(enabled, 0, enabled.Length);
            JunctionRenderer.ForgetAll();
        }

        // CalculateCorner postfix: move this road end back along the road so
        // the junction (and its corner curves) gets bigger. Thread-safe.
        public static bool TryAdjustCorner(ushort segment, ushort node, ref Vector3 position, Vector3 direction)
        {
            if (!enabled[node] || !IsEnabled(node)) return false;
            float extra = cornerExtra[node];
            if (extra <= 0f) return true;
            // Never more than 40 % of the road, so short roads keep a middle.
            var s = NetManager.instance.m_segments.m_buffer[segment];
            extra = Mathf.Min(extra, s.m_averageLength * 0.4f);
            Vector3 along = CurveMath.FlatNormal(direction);   // points away from the node, into the road
            position += along * extra;
            return true;
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
                    writer.Write(cornerExtra[i]);
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
                if (version != 1) throw new InvalidDataException("unknown junction data version " + version);
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    ushort node = reader.ReadUInt16();
                    positions[node] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    cornerExtra[node] = reader.ReadSingle();
                    enabled[node] = true;
                }
                Mod.Log("Loaded " + count + " CS2-style junction(s)");
            }
        }
    }

    // Draws the CS2-style junction surface with the game's own road shader.
    // The shader bends a flat mesh between a left and a right edge curve
    // (the way the game draws road segments); every piece here is such a
    // strip: asphalt wedges from the junction's edge to its middle, and
    // sidewalk strips along each corner curve with a curb face.
    // Material and surface textures follow Intersection Marking Tool
    // (MacSergey, MIT licence), which draws its fillers the same way.
    public static class JunctionRenderer
    {
        private const int RoadLayer = 9;
        private const int Steps = 8;               // mesh rows along each strip
        private const float CurbDrop = 0.3f;       // curb face height below the sidewalk
        private const float Lift = 0.02f;          // a hair above the road ends, against flicker

        private struct Strip
        {
            public Vector3 Position;
            public Matrix4x4 Left, Right;
            public bool Sidewalk;
            public bool Flipped;                   // the edges run the other way round: use the mirrored mesh
            public float Offset;                   // y offset when drawn
        }

        private class Shape
        {
            public int Key;
            public List<Strip> Strips = new List<Strip>();
        }

        private struct End
        {
            public ushort Segment;
            public float Angle;
            public Vector3 Direction;              // away from the node, flat
            public Vector3 Left, Right;            // outer corners (road edge incl. sidewalk)
            public Vector3 InnerLeft, InnerRight;  // curb line
            public float SurfaceLevel;             // asphalt below the node height
        }

        // Main thread only (rendering).
        private static readonly Dictionary<ushort, Shape> shapes = new Dictionary<ushort, Shape>();
        private static Mesh flatMesh, sidewalkMesh, flatMeshFlipped, sidewalkMeshFlipped;
        private static Material asphalt, pavement;
        private static Texture2D pavementA, pavementB;
        private static bool failed;                    // resources can't be made (no road shader): off for the session
        private static readonly HashSet<ushort> failedNodes = new HashSet<ushort>();   // logged once each, drawn vanilla

        // Asphalt tint (0 black .. 1 like the sidewalk). Adjustable in the JN
        // tool (Page Up / Page Down) until the default matches the roads.
        private const string ShadeKey = "RealisticRoads.AsphaltShade";
        private const float DefaultShade = 0.3f;
        private static float shade = -1f;

        public static float Shade
        {
            get
            {
                if (shade < 0f)
                {
                    try { shade = PlayerPrefs.HasKey(ShadeKey) ? PlayerPrefs.GetFloat(ShadeKey) : DefaultShade; }
                    catch { shade = DefaultShade; }
                }
                return shade;
            }
        }

        // Main thread.
        public static void AdjustShade(float step)
        {
            shade = Mathf.Clamp(Shade + step, 0.05f, 1f);
            if (asphalt != null) asphalt.color = new Color(shade, shade, shade, 0f);
            try
            {
                PlayerPrefs.SetFloat(ShadeKey, shade);
                PlayerPrefs.Save();
            }
            catch { }
            Mod.Log("Junction asphalt shade " + shade.ToString("F2"));
        }

        public static void Forget(ushort node)
        {
            lock (shapes) shapes.Remove(node);
            lock (failedNodes) failedNodes.Remove(node);   // its roads changed: try again (any thread)
        }

        public static void ForgetAll()
        {
            lock (shapes) shapes.Clear();
            lock (failedNodes) failedNodes.Clear();
        }

        // Postfix of NetNode.RenderInstance(cameraInfo, nodeID, layerMask): only
        // when that pass draws the road layer, like the vanilla node meshes.
        public static void Render(ushort node, int layerMask)
        {
            if (failed || (layerMask & (1 << RoadLayer)) == 0 || !Junctions.IsEnabled(node)) return;
            lock (failedNodes) { if (failedNodes.Contains(node)) return; }
            try
            {
                if (!EnsureResources()) return;
                Shape shape = GetShape(node);
                if (shape == null) return;

                var net = NetManager.instance;
                foreach (var strip in shape.Strips)
                {
                    var block = net.m_materialBlock;
                    block.Clear();
                    block.SetMatrix(net.ID_LeftMatrix, strip.Left);
                    block.SetMatrix(net.ID_RightMatrix, strip.Right);
                    block.SetVector(net.ID_MeshScale, new Vector4(1f, 1f, 1f, 1f));
                    block.SetTexture(net.ID_SurfaceTexA, pavementA);
                    block.SetTexture(net.ID_SurfaceTexB, pavementB);
                    Mesh mesh = strip.Sidewalk ? (strip.Flipped ? sidewalkMeshFlipped : sidewalkMesh)
                                               : (strip.Flipped ? flatMeshFlipped : flatMesh);
                    Graphics.DrawMesh(mesh,
                        strip.Position + Vector3.up * strip.Offset, Quaternion.identity,
                        strip.Sidewalk ? pavement : asphalt, RoadLayer, null, 0, block);
                }
            }
            catch (Exception e)
            {
                // Only this junction: the others keep their look.
                int count;
                lock (failedNodes) { failedNodes.Add(node); count = failedNodes.Count; }
                if (count <= 32)
                    Mod.LogError("Drawing the CS2-style junction at node #" + node + " failed; it shows nothing special until its roads change", e);
            }
        }

        private static Shape GetShape(ushort node)
        {
            int key = Key(node);
            Shape shape;
            lock (shapes)
            {
                if (shapes.TryGetValue(node, out shape) && shape.Key == key) return shape;
            }
            shape = Build(node);
            if (shape == null) return null;
            shape.Key = key;
            lock (shapes) shapes[node] = shape;
            return shape;
        }

        // Changes whenever the roads at the node change (rebuilt shape then).
        private static int Key(ushort node)
        {
            var net = NetManager.instance;
            var n = net.m_nodes.m_buffer[node];
            int key = n.m_position.GetHashCode() ^ Junctions.CornerExtra(node).GetHashCode();
            for (int i = 0; i < 8; i++)
            {
                ushort segment = n.GetSegment(i);
                if (segment == 0) continue;
                var s = net.m_segments.m_buffer[segment];
                key = key * 31 + segment;
                key = key * 31 + (s.m_startNode == node ? s.m_startDirection : s.m_endDirection).GetHashCode();
                key = key * 31 + net.m_nodes.m_buffer[s.m_startNode == node ? s.m_endNode : s.m_startNode].m_position.GetHashCode();
            }
            return key;
        }

        private static Shape Build(ushort node)
        {
            var net = NetManager.instance;
            var n = net.m_nodes.m_buffer[node];
            var ends = new List<End>();
            for (int i = 0; i < 8; i++)
            {
                ushort segment = n.GetSegment(i);
                if (segment == 0) continue;
                var info = net.m_segments.m_buffer[segment].Info;
                if (info == null) continue;
                bool start = net.m_segments.m_buffer[segment].m_startNode == node;

                Vector3 left, right, leftDir, rightDir;
                bool smooth;
                net.m_segments.m_buffer[segment].CalculateCorner(segment, true, start, true, out left, out leftDir, out smooth);
                net.m_segments.m_buffer[segment].CalculateCorner(segment, true, start, false, out right, out rightDir, out smooth);

                var end = new End { Segment = segment, Left = left, Right = right, SurfaceLevel = info.m_surfaceLevel };
                end.Direction = CurveMath.FlatNormal(leftDir + rightDir);
                end.Angle = Mathf.Atan2(end.Direction.z, end.Direction.x);
                Vector3 across = CurveMath.FlatNormal(right - left);
                float width = CurveMath.FlatDistance(left, right);
                float pavement = Mathf.Min(info.m_pavementWidth, width * 0.4f);
                end.InnerLeft = left + across * pavement;
                end.InnerRight = right - across * pavement;
                ends.Add(end);
            }
            if (ends.Count < 3) return null;

            // Round the junction anticlockwise; each road's left corner faces the next road.
            ends.Sort((a, b) => a.Angle.CompareTo(b.Angle));

            // Fan the asphalt from the node itself: it is where the roads' centre
            // lines meet, so the curb curves (half a road width out) stay around
            // it. The average of the corners can sit outside a skewed junction
            // with a wide road, folding the wedges over.
            Vector3 centre = n.m_position;
            float surface = 0f, height = 0f;
            foreach (var end in ends)
            {
                height += end.InnerLeft.y + end.InnerRight.y;
                surface += end.SurfaceLevel;
            }
            centre.y = height / (ends.Count * 2);
            surface /= ends.Count;

            var shape = new Shape();
            for (int i = 0; i < ends.Count; i++)
            {
                var a = ends[i];
                var b = ends[(i + 1) % ends.Count];

                // Asphalt: the road end's line, then the curb curve to the next road.
                AddWedge(shape, Straight(a.InnerRight, a.InnerLeft), centre, surface + Lift);
                var curb = Curve(a.InnerLeft, a.Direction, b.InnerRight, b.Direction);
                AddWedge(shape, curb, centre, surface + Lift);

                // Sidewalk round the corner, from the road edge in to the curb.
                if (CurveMath.FlatDistance(a.Left, a.InnerLeft) > 0.5f || CurveMath.FlatDistance(b.Right, b.InnerRight) > 0.5f)
                    AddStrip(shape, Curve(a.Left, a.Direction, b.Right, b.Direction), curb, true, 0f);
            }
            return shape;
        }

        // From one road end into the junction and out along the next road.
        private static Bezier3 Curve(Vector3 from, Vector3 fromDirection, Vector3 to, Vector3 toDirection)
        {
            var bezier = new Bezier3 { a = from, d = to };
            NetSegment.CalculateMiddlePoints(from, -fromDirection, to, -toDirection, true, true, out bezier.b, out bezier.c);
            return bezier;
        }

        private static Bezier3 Straight(Vector3 from, Vector3 to)
        {
            return new Bezier3 { a = from, b = Vector3.Lerp(from, to, 1f / 3f), c = Vector3.Lerp(from, to, 2f / 3f), d = to };
        }

        private static void AddWedge(Shape shape, Bezier3 edge, Vector3 centre, float offset)
        {
            var point = new Bezier3 { a = centre, b = centre, c = centre, d = centre };
            AddStrip(shape, edge, point, false, offset);
        }

        private static void AddStrip(Shape shape, Bezier3 left, Bezier3 right, bool sidewalk, float offset)
        {
            Vector3 position = (left.a + left.d + right.a + right.d) * 0.25f;
            // The mesh faces up when "along the left edge" turned towards "across
            // to the right edge" points up; otherwise draw the mirrored mesh.
            Vector3 along = left.d - left.a;
            Vector3 across = (right.a + right.d) * 0.5f - (left.a + left.d) * 0.5f;
            bool flipped = along.z * across.x - along.x * across.z < 0f;
            shape.Strips.Add(new Strip
            {
                Position = position,
                Flipped = flipped,
                Left = NetSegment.CalculateControlMatrix(left.a, left.b, left.c, left.d, right.a, right.b, right.c, right.d, position, 0.05f),
                Right = NetSegment.CalculateControlMatrix(right.a, right.b, right.c, right.d, left.a, left.b, left.c, left.d, position, 0.05f),
                Sidewalk = sidewalk,
                Offset = offset,
            });
        }

        // --- meshes and materials --------------------------------------------

        private static bool EnsureResources()
        {
            if (flatMesh != null) return true;
            var shader = Shader.Find("Custom/Net/Road");
            if (shader == null)
            {
                failed = true;
                Mod.LogError("CS2-style junctions: road shader not found", null);
                return false;
            }
            // The asphalt is the sidewalk surface (which renders correctly) tinted dark.
            pavement = RoadMaterial(shader, "RealisticRoadsPavement", Texture(128, new Color32(0, 0, 0, 255)));
            asphalt = RoadMaterial(shader, "RealisticRoadsAsphalt", Texture(128, new Color32(0, 0, 0, 255)));
            asphalt.color = new Color(Shade, Shade, Shade, 0f);
            pavementA = Texture(512, new Color32(255, 255, 127, 127));
            pavementB = Texture(512, new Color32(0, 0, 0, 0));
            flatMesh = StripMesh(false, false);
            sidewalkMesh = StripMesh(true, false);
            flatMeshFlipped = StripMesh(false, true);
            sidewalkMeshFlipped = StripMesh(true, true);
            Mod.Log("CS2-style junction materials ready");
            return true;
        }

        private static Material RoadMaterial(Shader shader, string name, Texture2D apr)
        {
            var material = new Material(shader)
            {
                name = name,
                mainTexture = Texture(128, new Color32(255, 255, 255, 255)),
                color = new Color(0.6f, 0.6f, 0.6f, 0f),
                renderQueue = 2465,
            };
            material.SetTexture("_APRMap", apr);
            material.EnableKeyword("NET_SEGMENT");
            return material;
        }

        private static Texture2D Texture(int size, Color32 color)
        {
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels32(pixels);
            texture.Apply();
            return texture;
        }

        // A strip x in [-0.5, 0.5] (left edge curve to right edge curve),
        // z in [-0.5, 0.5] (along the curves). The sidewalk version is the
        // same at sidewalk height, with a curb face down along the right edge.
        // 'flipped' mirrors the winding for strips whose edges run the other way.
        private static Mesh StripMesh(bool sidewalk, bool flipped)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();

            AddGrid(vertices, normals, uvs, triangles, -0.5f, 0f, 0.5f, 0f, Vector3.up, flipped);
            if (sidewalk) AddGrid(vertices, normals, uvs, triangles, 0.5f, 0f, 0.5f, -CurbDrop, new Vector3(1f, 0f, 0f), flipped);

            var mesh = new Mesh { name = sidewalk ? "RealisticRoadsSidewalk" : "RealisticRoadsAsphalt" };
            mesh.vertices = vertices.ToArray();
            mesh.normals = normals.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.triangles = triangles.ToArray();
            // The shader moves vertices far from their mesh positions.
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(512f, 128f, 512f));
            mesh.UploadMeshData(false);
            return mesh;
        }

        // A band of Steps rows from (x0, y0) to (x1, y1) across, -0.5..0.5 along.
        private static void AddGrid(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles,
            float x0, float y0, float x1, float y1, Vector3 normal, bool flipped)
        {
            int first = vertices.Count;
            for (int row = 0; row <= Steps; row++)
            {
                float z = row / (float)Steps - 0.5f;
                vertices.Add(new Vector3(x0, y0, z));
                vertices.Add(new Vector3(x1, y1, z));
                normals.Add(normal);
                normals.Add(normal);
                uvs.Add(new Vector2(0f, z + 0.5f));
                uvs.Add(new Vector2(1f, z + 0.5f));
            }
            for (int row = 0; row < Steps; row++)
            {
                int a = first + row * 2, b = a + 1, c = a + 2, d = a + 3;
                if (!flipped)
                {
                    triangles.Add(a); triangles.Add(c); triangles.Add(b);
                    triangles.Add(b); triangles.Add(c); triangles.Add(d);
                }
                else
                {
                    triangles.Add(a); triangles.Add(b); triangles.Add(c);
                    triangles.Add(b); triangles.Add(d); triangles.Add(c);
                }
            }
        }
    }

    // Saves the CS2-style junctions with the city.
    public class JunctionsData : SerializableDataExtensionBase
    {
        public override void OnLoadData()
        {
            try
            {
                Junctions.Deserialize(serializableDataManager.LoadData(Junctions.DataId));
            }
            catch (Exception e)
            {
                Mod.LogError("Loading CS2-style junctions failed, starting with none", e);
                Junctions.Clear();
            }
        }

        public override void OnSaveData()
        {
            try
            {
                serializableDataManager.SaveData(Junctions.DataId, Junctions.Serialize());
            }
            catch (Exception e)
            {
                Mod.LogError("Saving CS2-style junctions failed", e);
            }
        }
    }
}
