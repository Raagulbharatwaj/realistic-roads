using System;
using System.Collections.Generic;
using System.IO;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // Retaining walls along the sides of a road below the ground: our cuttings
    // and tunnel ramps, and the roads we lower for an overpass. Like a quay: at
    // each edge of the road a solid wall from just under it up to the ground as
    // it was, and its top reaching out over the whole slope the game dug there,
    // to where the ground is untouched, so no dug slope is seen. Drawn with the
    // road's pillar material, like the deck frames, and saved with the city.
    public static class RetainingWalls
    {
        public const string DataId = "RealisticRoads.Walls";
        private const int Version = 1;
        private const float Gap = 0.2f;          // the wall's face this far outside the road's edge
        private const float Thickness = 1f;      // the least the wall's top reaches out
        private const float Footing = 0.5f;      // the wall goes this far below the road
        private const float MinWall = 1.5f;      // lower than this above the road: no wall there
        private const float MaxWall = 16f;
        private const float SampleStep = 4f;     // metres between wall sections
        private const float ReachStep = 1.5f;    // looking out from the edge for the end of the dug slope
        private const float MaxReach = 30f;
        private const float Dug = 0.3f;          // the ground seen this far below the ground as it was: dug by the road
        private const float DrawDistance = 4000f;
        private const int RefreshFrames = 120;   // the ground is looked at again this often (the game digs after building)
        private const int SettledFrames = 600;   // ... and this often once it has stayed the same a few times
        private const int SettledAfter = 5;
        private const float JunctionMargin = 2f; // walls stop this far before the side roads at a junction

        private class Wall
        {
            public ushort Segment;
            public uint BuildIndex;
            public string Pillar;
            public Mesh Mesh;                    // main thread
            public Vector3 Origin;
            public float Signature = float.NaN;  // of the ground the mesh was made for
            public int Checked = -RefreshFrames;
            public int Unchanged;                // checks in a row that found the ground as it was
        }

        private static readonly Dictionary<ushort, Wall> walls = new Dictionary<ushort, Wall>();
        private static readonly List<Mesh> unused = new List<Mesh>();
        private static readonly HashSet<ushort> failed = new HashSet<ushort>();
        private static int frame;
        private static readonly List<Wall> drawn = new List<Wall>();   // this frame's walls (kept, not made anew)

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
            var list = drawn;
            lock (walls)
            {
                if (walls.Count == 0) return;
                list.Clear();
                list.AddRange(walls.Values);
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
                    int interval = wall.Unchanged >= SettledAfter ? SettledFrames : RefreshFrames;
                    if (frame - wall.Checked >= interval)
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
                            wall.Unchanged = 0;
                        }
                        else wall.Unchanged++;
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
            public float Top;           // the ground as it was at the wall: the wall's top
            public float Reach;         // how far out the dug slope goes: the top covers it
            public float FarTop;        // the ground as it was there
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
            // At a junction the side roads come in: no wall across their mouths.
            var nodes = NetManager.instance.m_nodes.m_buffer;
            float startClear = JunctionClear(segment, s.m_startNode, half), endClear = JunctionClear(segment, s.m_endNode, half);
            Vector3 startNode = nodes[s.m_startNode].m_position, endNode = nodes[s.m_endNode].m_position;
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
                    // The wall's top: the ground as it was (the raw heights: before
                    // roads dig into it). Its reach: out from the edge as far as the
                    // ground seen (the detail heights, dug by the road) is lower than
                    // that, to the top of the slope the game dug.
                    float top = Mathf.Min(terrain.SampleRawHeightSmooth(edge), p.y + MaxWall);
                    float reach = 0f;
                    for (float r = 0f; r <= MaxReach; r += ReachStep)
                    {
                        Vector3 at = edge + right * r;
                        if (terrain.SampleDetailHeight(at) < terrain.SampleRawHeightSmooth(at) - Dug) reach = r + ReachStep;
                        else if (r > reach + ReachStep) break;   // past the slope (one undug spot may sit on it)
                    }
                    reach = Mathf.Max(reach, Thickness);
                    float farTop = Mathf.Min(terrain.SampleRawHeightSmooth(edge + right * reach), p.y + MaxWall);
                    bool clear = CurveMath.FlatDistance(edge, startNode) >= startClear && CurveMath.FlatDistance(edge, endNode) >= endClear &&
                                 !Bridged(segment, edge, right, reach, top);
                    result[side][k] = new Section
                    {
                        Wall = clear && top - p.y >= MinWall,
                        Edge = edge,
                        Out = right,
                        Top = top,
                        Reach = reach,
                        FarTop = farTop,
                    };
                }
            }
            return result;
        }

        // True where another road passes over the wall at the ground's height
        // (our road on its deck over a lowered one): the wall's top would come
        // up through it, so the bridge has that span.
        private static bool Bridged(ushort segment, Vector3 edge, Vector3 outward, float reach, float top)
        {
            var net = NetManager.instance;
            foreach (ushort other in RoadPlanner.NearbySegments(edge, reach + 40f))
            {
                if (other == segment) continue;
                var s = net.m_segments.m_buffer[other];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null) continue;
                for (int k = 0; k <= 2; k++)
                {
                    Vector3 at = edge + outward * (reach * k * 0.5f), on;
                    CrossingFinder.ClosestT(other, at, out on);
                    if (CurveMath.FlatDistance(on, at) <= s.Info.m_halfWidth + 1f && on.y > top - 3f) return true;
                }
            }
            return false;
        }

        // How far from a junction node the walls of 'segment' (half as wide as
        // 'half') stop: past the widest other road there (0 where the road just
        // goes on).
        private static float JunctionClear(ushort segment, ushort node, float half)
        {
            var net = NetManager.instance;
            int roads = 0;
            float widest = 0f;
            for (int i = 0; i < 8; i++)
            {
                ushort other = net.m_nodes.m_buffer[node].GetSegment(i);
                if (other == 0) continue;
                roads++;
                if (other == segment) continue;
                var info = net.m_segments.m_buffer[other].Info;
                if (info != null) widest = Mathf.Max(widest, info.m_halfWidth);
            }
            return roads > 2 ? widest + half + JunctionMargin : 0f;
        }

        private static float Signature(Section[][] sections)
        {
            float sum = 0f;
            foreach (var side in sections)
                foreach (var section in side)
                    if (section.Wall) sum += section.Top * 1.3f + section.Reach + section.FarTop * 0.7f + section.Edge.y * 0.3f;
            return sum;
        }

        // Each section between two spots with a wall: a solid block with no
        // face missing above the ground (so it is never seen through): the face
        // towards the road, its top reaching out over the dug slope, the far
        // side going down into the ground, and its ends where a run starts or
        // stops.
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
                    Vector3 pBottom, pTop, pFar, pFarBottom, qBottom, qTop, qFar, qFarBottom;
                    Corners(p, origin, out pBottom, out pTop, out pFar, out pFarBottom);
                    Corners(q, origin, out qBottom, out qTop, out qFar, out qFarBottom);
                    Vector3 outward = CurveMath.FlatNormal(p.Out + q.Out);
                    Quad(vertices, normals, triangles, pBottom, qBottom, qTop, pTop, -outward);
                    Quad(vertices, normals, triangles, pTop, qTop, qFar, pFar, Vector3.up);
                    Quad(vertices, normals, triangles, pFar, qFar, qFarBottom, pFarBottom, outward);
                    Vector3 along = CurveMath.FlatNormal(q.Edge - p.Edge);
                    if (k == 1 || !side[k - 2].Wall)
                        Quad(vertices, normals, triangles, pBottom, pTop, pFar, pFarBottom, -along);
                    if (k == side.Length - 1 || !side[k + 1].Wall)
                        Quad(vertices, normals, triangles, qBottom, qTop, qFar, qFarBottom, along);
                }
            if (vertices.Count == 0) return null;
            return DeckFrames.MakeMesh(vertices, normals, triangles, uv);
        }

        // A section's outline across the road (relative to 'origin'): the foot
        // and top of the wall's face, and the far edge of its top and below it.
        private static void Corners(Section s, Vector3 origin, out Vector3 bottom, out Vector3 top, out Vector3 far, out Vector3 farBottom)
        {
            bottom = s.Edge + Vector3.up * -Footing - origin;
            top = new Vector3(s.Edge.x, s.Top, s.Edge.z) - origin;
            Vector3 farEdge = s.Edge + s.Out * s.Reach;
            far = new Vector3(farEdge.x, s.FarTop, farEdge.z) - origin;
            farBottom = new Vector3(farEdge.x, s.Edge.y - Footing, farEdge.z) - origin;
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
