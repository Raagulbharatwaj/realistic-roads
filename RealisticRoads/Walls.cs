using System;
using System.Collections.Generic;
using System.IO;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // Retaining walls along the sides of a road below the ground: our cuttings
    // and tunnel ramps, and the roads we lower for an overpass. A wall stands at
    // each edge of the road, from just under it up to the ground beside it,
    // with a concrete top over the slope the game digs behind it. Drawn with
    // the road's pillar material, like the deck frames, and saved with the city.
    public static class RetainingWalls
    {
        public const string DataId = "RealisticRoads.Walls";
        private const int Version = 1;
        private const float Gap = 0.2f;          // the wall's face this far outside the road's edge
        private const float Thickness = 0.6f;
        private const float Footing = 0.4f;      // the wall goes this far below the road
        private const float MinWall = 1.5f;      // lower than this above the road: no wall there
        private const float MaxWall = 16f;
        private const float SampleStep = 4f;     // metres between wall sections
        private const float ReachStep = 1.5f;    // looking out from the edge for the ground beside the cutting
        private const float MaxReach = 30f;
        private const float Level = 0.15f;       // rising less than this per metre: the dug slope has ended
        private const float DrawDistance = 4000f;
        private const int RefreshFrames = 120;   // the ground is looked at again this often (the game digs after building)

        private class Wall
        {
            public ushort Segment;
            public uint BuildIndex;
            public string Pillar;
            public Mesh Mesh;                    // main thread
            public Vector3 Origin;
            public float Signature = float.NaN;  // of the ground the mesh was made for
            public int Checked = -RefreshFrames;
        }

        private static readonly Dictionary<ushort, Wall> walls = new Dictionary<ushort, Wall>();
        private static readonly List<Mesh> unused = new List<Mesh>();
        private static readonly HashSet<ushort> failed = new HashSet<ushort>();
        private static int frame;

        // Simulation thread, while building: walls along 'segment' wherever it
        // is below the ground, in the material of 'road''s pillars.
        public static void Add(ushort segment, NetInfo road)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) return;
            var wall = new Wall
            {
                Segment = segment,
                BuildIndex = s.m_buildIndex,
                Pillar = DeckFrames.PillarName(RoadPlanner.InfoFor(road, NodeKind.Elevated) ?? road),
            };
            lock (walls) walls[segment] = wall;
        }

        // Undo: the walls of these segments go. Any thread.
        public static void Forget(IEnumerable<ushort> segments)
        {
            lock (walls)
                foreach (ushort segment in segments)
                {
                    Wall wall;
                    if (!walls.TryGetValue(segment, out wall)) continue;
                    if (wall.Mesh != null) lock (unused) unused.Add(wall.Mesh);
                    walls.Remove(segment);
                }
        }

        public static void Clear()
        {
            lock (walls)
            {
                lock (unused)
                    foreach (var wall in walls.Values) if (wall.Mesh != null) unused.Add(wall.Mesh);
                walls.Clear();
            }
            failed.Clear();
        }

        // --- drawing (main thread) ---------------------------------------------

        public static void Render(RenderManager.CameraInfo cameraInfo)
        {
            lock (unused)
            {
                foreach (var mesh in unused) if (mesh != null) UnityEngine.Object.Destroy(mesh);
                unused.Clear();
            }
            List<Wall> list;
            lock (walls)
            {
                if (walls.Count == 0) return;
                list = new List<Wall>(walls.Values);
            }
            frame++;
            var segments = NetManager.instance.m_segments.m_buffer;
            var stale = new List<ushort>();
            foreach (var wall in list)
            {
                if (failed.Contains(wall.Segment)) continue;
                try
                {
                    var s = segments[wall.Segment];
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.m_buildIndex != wall.BuildIndex)
                    {
                        stale.Add(wall.Segment);
                        continue;
                    }
                    var nodes = NetManager.instance.m_nodes.m_buffer;
                    Vector3 middle = (nodes[s.m_startNode].m_position + nodes[s.m_endNode].m_position) * 0.5f;
                    if (CurveMath.FlatDistance(middle, cameraInfo.m_position) > DrawDistance) continue;
                    KeyValuePair<Material, Vector2> material;
                    if (!DeckFrames.MaterialFor(wall.Pillar, out material)) continue;
                    // Looked at again now and then: the game digs the cutting after
                    // the road is built, and the player may reshape the ground.
                    if (frame - wall.Checked >= RefreshFrames)
                    {
                        wall.Checked = frame + (wall.Segment % RefreshFrames);   // spread over the frames
                        var sections = Sections(wall.Segment);
                        float signature = Signature(sections);
                        if (float.IsNaN(wall.Signature) || Mathf.Abs(signature - wall.Signature) > 0.05f)
                        {
                            if (wall.Mesh != null) UnityEngine.Object.Destroy(wall.Mesh);
                            wall.Origin = middle;
                            wall.Mesh = MakeMesh(sections, middle, material.Value);
                            wall.Signature = signature;
                        }
                    }
                    if (wall.Mesh != null) DeckFrames.Draw(wall.Mesh, wall.Origin, wall.Pillar, material.Key);
                }
                catch (Exception e)
                {
                    failed.Add(wall.Segment);
                    if (failed.Count <= 16) Mod.LogError("Drawing the retaining wall along segment #" + wall.Segment + " failed; it is not shown", e);
                }
            }
            if (stale.Count > 0) Forget(stale);
        }

        // Where the wall stands at one spot on one side of the road.
        private struct Section
        {
            public bool Wall;
            public Vector3 Edge;        // the wall's face, at the road's height
            public Vector3 Out;         // across the road, away from it
            public float Top;           // the ground beside the cutting
            public float Reach;         // how far out the dug slope goes
        }

        // Both sides of the road, every SampleStep metres: [side][sample].
        private static Section[][] Sections(ushort segment)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            float half = s.Info != null ? s.Info.m_halfWidth : 4f;
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(s.m_averageLength, 1f) / SampleStep), 2, 64);
            var terrain = TerrainManager.instance;
            var result = new Section[2][];
            for (int side = 0; side < 2; side++)
            {
                result[side] = new Section[steps + 1];
                for (int k = 0; k <= steps; k++)
                {
                    float t = k / (float)steps;
                    Vector3 p = CurveMath.Bezier(a, b, c, d, t);
                    Vector3 along = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t));
                    Vector3 right = new Vector3(along.z, 0f, -along.x) * (side == 0 ? 1f : -1f);
                    Vector3 edge = p + right * (half + Gap);
                    // Out from the edge until the ground stops rising: the top of the
                    // dug slope (or the untouched ground, if not dug yet).
                    float previous = terrain.SampleRawHeightSmooth(edge), top = previous, reach = 0f;
                    for (float r = ReachStep; r <= MaxReach; r += ReachStep)
                    {
                        float h = terrain.SampleRawHeightSmooth(edge + right * r);
                        if (h - previous < Level * ReachStep) break;
                        previous = top = h;
                        reach = r;
                    }
                    top = Mathf.Min(top, p.y + MaxWall);
                    result[side][k] = new Section
                    {
                        Wall = top - p.y >= MinWall,
                        Edge = edge,
                        Out = right,
                        Top = top,
                        Reach = Mathf.Max(reach, Thickness),
                    };
                }
            }
            return result;
        }

        private static float Signature(Section[][] sections)
        {
            float sum = 0f;
            foreach (var side in sections)
                foreach (var section in side)
                    if (section.Wall) sum += section.Top * 1.3f + section.Reach + section.Edge.y * 0.7f;
            return sum;
        }

        // Each section between two spots with a wall: the face towards the road,
        // the wall's top, and the concrete over the dug slope behind it.
        private static Mesh MakeMesh(Section[][] sections, Vector3 origin, Vector2 uv)
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var side in sections)
                for (int k = 1; k < side.Length; k++)
                {
                    var p = side[k - 1];
                    var q = side[k];
                    if (!p.Wall || !q.Wall) continue;
                    Vector3 pBottom = p.Edge + Vector3.up * -Footing - origin, qBottom = q.Edge + Vector3.up * -Footing - origin;
                    Vector3 pTop = new Vector3(p.Edge.x, p.Top, p.Edge.z) - origin, qTop = new Vector3(q.Edge.x, q.Top, q.Edge.z) - origin;
                    Vector3 inward = -CurveMath.FlatNormal(p.Out + q.Out);
                    Quad(vertices, normals, triangles, pBottom, qBottom, qTop, pTop, inward);
                    Vector3 pBack = pTop + p.Out * Thickness, qBack = qTop + q.Out * Thickness;
                    Quad(vertices, normals, triangles, pTop, qTop, qBack, pBack, Vector3.up);
                    // The concrete top, out to where the ground is as high again.
                    Vector3 pFar = pTop + p.Out * p.Reach, qFar = qTop + q.Out * q.Reach;
                    Quad(vertices, normals, triangles, pBack, qBack, qFar, pFar, Vector3.up);
                }
            if (vertices.Count == 0) return null;
            return DeckFrames.MakeMesh(vertices, normals, triangles, uv);
        }

        // A face through four corners (in order round it), seen from 'normal'.
        private static void Quad(List<Vector3> vertices, List<Vector3> normals, List<int> triangles,
            Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3, Vector3 normal)
        {
            int first = vertices.Count;
            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);
            for (int i = 0; i < 4; i++) normals.Add(normal);
            // Wound like DeckFrames.Face: by which way its two edges turn.
            bool flip = Vector3.Dot(Vector3.Cross(v1 - v0, v3 - v0), normal) < 0f;
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
                List<Wall> list;
                lock (walls) list = new List<Wall>(walls.Values);
                writer.Write(Version);
                writer.Write(list.Count);
                foreach (var wall in list)
                {
                    writer.Write(wall.Segment);
                    writer.Write(wall.BuildIndex);
                    writer.Write(wall.Pillar ?? "");
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
                    Mod.Log("Retaining walls: saved with an unknown version " + version + ", not loaded");
                    return;
                }
                int count = reader.ReadInt32();
                lock (walls)
                    for (int i = 0; i < count; i++)
                    {
                        var wall = new Wall { Segment = reader.ReadUInt16(), BuildIndex = reader.ReadUInt32(), Pillar = reader.ReadString() };
                        walls[wall.Segment] = wall;
                    }
                Mod.Log("Retaining walls loaded: " + count);
            }
        }
    }

    // Saves the retaining walls with the city.
    public class RetainingWallsData : SerializableDataExtensionBase
    {
        public override void OnLoadData()
        {
            try
            {
                RetainingWalls.Deserialize(serializableDataManager.LoadData(RetainingWalls.DataId));
            }
            catch (Exception e)
            {
                Mod.LogError("Loading retaining walls failed, starting with none", e);
                RetainingWalls.Clear();
            }
        }

        public override void OnSaveData()
        {
            try
            {
                serializableDataManager.SaveData(RetainingWalls.DataId, RetainingWalls.Serialize());
            }
            catch (Exception e)
            {
                Mod.LogError("Saving retaining walls failed", e);
            }
        }
    }
}
