using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace RealisticRoads
{
    // The median ramps' look under the deck, in the game's own concrete (the
    // material of the ramps' pillar, as the deck frames use):
    //   - where a ramp segment climbs and no road passes under it, solid walls
    //     from under its edges down into the ground, so the ramp is closed
    //     underneath;
    //   - where it is open (level, or over a road), a solid wall pillar as wide
    //     as the ramp at each of its raised nodes, instead of the slim pillar.
    // Nothing is saved: it is worked out from the ramps themselves (any ramp,
    // whenever it was built), checked every ScanFrames frames and drawn each frame.
    public static class RampSupports
    {
        private const int ScanFrames = 90;
        private const float DrawDistance = 4000f;
        private const float Inset = 0.4f;          // the walls stand this far inside the ramp's edges
        private const float WallThickness = 0.3f;  // their inner face this far inside the outer one
        private const float PierProud = 0.05f;     // a wall pillar's sides this far outside the walls' outer faces (not in their plane)
        private const float UnderSurface = 0.3f;   // their top this far under the road's surface (under the deck)
        private const float IntoGround = 1f;       // their foot this far into the ground
        private const float MinWall = 0.3f;        // lower than this: no wall there
        // The walls go up to where the ramp is this high over the ground (the
        // owner's choice, 11 m: a solid abutment, open on wall pillars above);
        // there an end face closes them.
        private const float MaxWallHeight = 11f;
        private const float Step = 4f;             // metres between the walls' pieces along the ramp
        private const float PierDepth = 1.6f;      // a wall pillar's thickness along the ramp
        private const float PierUnderside = 1.2f;  // its top this far under the node (the deck's underside)
        private const float Climbs = 0.3f;         // a segment whose ends differ by more than this climbs
        private const float CrossingStep = 4f;     // how often along a segment roads under it are looked for

        private class Part
        {
            public ushort Id;                      // the segment (walls) or node (pillar)
            public uint BuildIndex;
            public string Key;                     // what the mesh was made for
            public Vector3 Origin;
            public string Pillar;
            public Mesh Mesh;
        }

        private static readonly Dictionary<ushort, Part> walls = new Dictionary<ushort, Part>();
        private static readonly Dictionary<ushort, Part> piers = new Dictionary<ushort, Part>();
        private static readonly HashSet<ushort> solid = new HashSet<ushort>();   // ramp segments drawn closed (any thread reads)
        private static int wait;
        private static bool failed;

        // Any thread (the game asks for node pillars on the simulation thread).
        // A raised node only ramps meet at: its wall pillar (or the walls) stand
        // there, not the road's own pillar.
        public static bool Holds(ushort node)
        {
            if (node == 0) return false;
            var net = NetManager.instance;
            var n = net.m_nodes.m_buffer[node];
            if ((n.m_flags & NetNode.Flags.Created) == NetNode.Flags.None || (n.m_flags & NetNode.Flags.OnGround) != NetNode.Flags.None) return false;
            int ramps = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort s = n.GetSegment(i);
                if (s == 0) continue;
                if (!SplitRoads.IsRamp(net.m_segments.m_buffer[s].Info)) return false;
                ramps++;
            }
            return ramps > 0;
        }

        public static void Clear()
        {
            lock (walls)
            {
                foreach (var part in walls.Values) if (part.Mesh != null) UnityEngine.Object.Destroy(part.Mesh);
                foreach (var part in piers.Values) if (part.Mesh != null) UnityEngine.Object.Destroy(part.Mesh);
                walls.Clear();
                piers.Clear();
                solid.Clear();
            }
            wait = 0;
            failed = false;
        }

        // Main thread, every frame (with the deck frames).
        public static void Render(RenderManager.CameraInfo cameraInfo)
        {
            if (failed) return;
            try
            {
                if (--wait <= 0)
                {
                    wait = ScanFrames;
                    Scan();
                }
                lock (walls)
                {
                    foreach (var part in walls.Values) DrawPart(part, cameraInfo);
                    foreach (var part in piers.Values) DrawPart(part, cameraInfo);
                }
            }
            catch (Exception e)
            {
                failed = true;
                Mod.LogError("Drawing the median ramps' walls and pillars failed; they are not drawn", e);
            }
        }

        private static void DrawPart(Part part, RenderManager.CameraInfo cameraInfo)
        {
            if (part.Mesh == null || CurveMath.FlatDistance(part.Origin, cameraInfo.m_position) > DrawDistance) return;
            KeyValuePair<Material, Vector4> material;
            if (!DeckFrames.MaterialFor(part.Pillar, out material)) return;
            DeckFrames.Draw(part.Mesh, part.Origin, part.Pillar, material.Key);
        }

        // The ramps as they are now: which segments are closed, their walls,
        // and the wall pillars; meshes made again only where something changed.
        private static void Scan()
        {
            var net = NetManager.instance;
            var segments = net.m_segments.m_buffer;
            var nodes = net.m_nodes.m_buffer;
            var rampSegments = new List<ushort>();
            var rampSet = new HashSet<ushort>();
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                if (!SplitRoads.IsRamp(segments[i].Info)) continue;
                rampSegments.Add((ushort)i);
                rampSet.Add((ushort)i);
            }
            var closed = new HashSet<ushort>();
            foreach (ushort s in rampSegments)
                if (Closes(s, rampSet)) closed.Add(s);
            var pierNodes = new HashSet<ushort>();
            foreach (ushort s in rampSegments)
                foreach (ushort node in new[] { segments[s].m_startNode, segments[s].m_endNode })
                {
                    if (pierNodes.Contains(node) || !Holds(node)) continue;
                    // A pillar where the ramp is open on either side of the node, or
                    // higher than its walls go.
                    bool open = nodes[node].m_position.y - TerrainManager.instance.SampleRawHeightSmooth(nodes[node].m_position) > MaxWallHeight;
                    for (int i = 0; i < 8; i++)
                    {
                        ushort other = nodes[node].GetSegment(i);
                        if (other != 0 && !closed.Contains(other)) open = true;
                    }
                    if (open) pierNodes.Add(node);
                }
            var oldPillars = new List<ushort>();
            lock (walls)
            {
                solid.Clear();
                foreach (ushort s in closed) solid.Add(s);
                Update(walls, closed, id => WallKey(id), id => MakeWalls(id));
                Update(piers, pierNodes, id => PierKey(id), id => MakePier(id));
            }
            // The road's own pillar off nodes our walls or pillars stand at.
            foreach (ushort s in rampSegments)
                foreach (ushort node in new[] { segments[s].m_startNode, segments[s].m_endNode })
                    if (nodes[node].m_building != 0 && Holds(node) && !oldPillars.Contains(node)) oldPillars.Add(node);
            if (oldPillars.Count > 0)
                SimulationManager.instance.AddAction(() =>
                {
                    foreach (ushort node in oldPillars)
                        if (Holds(node) && NetManager.instance.m_nodes.m_buffer[node].m_building != 0)
                            NetManager.instance.m_nodes.m_buffer[node].UpdateBuilding(node, null, 0f);
                });
        }

        private static void Update(Dictionary<ushort, Part> parts, HashSet<ushort> wanted, Func<ushort, string> key, Func<ushort, Part> make)
        {
            var gone = new List<ushort>();
            foreach (var pair in parts) if (!wanted.Contains(pair.Key)) gone.Add(pair.Key);
            foreach (ushort id in gone)
            {
                if (parts[id].Mesh != null) UnityEngine.Object.Destroy(parts[id].Mesh);
                parts.Remove(id);
            }
            foreach (ushort id in wanted)
            {
                string k = key(id);
                Part part;
                if (parts.TryGetValue(id, out part) && part.Key == k) continue;
                if (part != null && part.Mesh != null) UnityEngine.Object.Destroy(part.Mesh);
                part = make(id);
                if (part == null)
                {
                    parts.Remove(id);
                    continue;
                }
                part.Key = k;
                parts[id] = part;
            }
        }

        // A ramp segment drawn closed: raised, climbing (or rising off the
        // ground), and no road (but the ramp's own ground road) under it.
        private static bool Closes(ushort segment, HashSet<ushort> ramps)
        {
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[segment];
            var start = net.m_nodes.m_buffer[s.m_startNode];
            var end = net.m_nodes.m_buffer[s.m_endNode];
            bool onGround = (start.m_flags & NetNode.Flags.OnGround) != NetNode.Flags.None || (end.m_flags & NetNode.Flags.OnGround) != NetNode.Flags.None;
            if (!onGround && Mathf.Abs(start.m_position.y - end.m_position.y) <= Climbs) return false;
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            float length = Mathf.Max(s.m_averageLength, 1f);
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / CrossingStep));
            for (int k = 1; k < steps; k++)
            {
                Vector3 p = CurveMath.Bezier(a, b, c, d, (float)k / steps);
                foreach (ushort other in RoadPlanner.NearbySegments(p, 40f))
                {
                    if (ramps.Contains(other)) continue;
                    var o = net.m_segments.m_buffer[other];
                    // Roads joining this one's nodes (the road it rises out of) aren't under it.
                    if (o.m_startNode == s.m_startNode || o.m_startNode == s.m_endNode || o.m_endNode == s.m_startNode || o.m_endNode == s.m_endNode) continue;
                    var otherInfo = o.Info;
                    if (otherInfo == null || SplitRoads.IsSplit(otherInfo) || SplitRoads.IsFullStart(otherInfo) || !CrossingFinder.IsPhysical(otherInfo)) continue;
                    Vector3 onRoad;
                    CrossingFinder.ClosestT(other, p, out onRoad);
                    if (CurveMath.FlatDistance(onRoad, p) > otherInfo.m_halfWidth + 1f) continue;
                    if (onRoad.y < p.y - 0.5f) return false;   // a road under it
                }
            }
            return true;
        }

        private static string WallKey(ushort segment)
        {
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[segment];
            Vector3 p = net.m_nodes.m_buffer[s.m_startNode].m_position, q = net.m_nodes.m_buffer[s.m_endNode].m_position;
            return s.m_buildIndex + "|" + p + "|" + q + "|" + s.m_startDirection + "|" + s.m_endDirection;
        }

        private static string PierKey(ushort node)
        {
            var n = NetManager.instance.m_nodes.m_buffer[node];
            return n.m_buildIndex + "|" + n.m_position + "|" + Way(node);
        }

        // The ramp's way through a raised node (flat), from its first ramp segment.
        private static Vector3 Way(ushort node)
        {
            var net = NetManager.instance;
            for (int i = 0; i < 8; i++)
            {
                ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                if (s == 0) continue;
                var seg = net.m_segments.m_buffer[s];
                return CurveMath.FlatNormal(seg.m_startNode == node ? seg.m_startDirection : seg.m_endDirection);
            }
            return Vector3.zero;
        }

        // Two walls under a closed segment's edges, from just under its surface
        // into the ground, in pieces Step long (each textured once).
        private static Part MakeWalls(ushort segment)
        {
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[segment];
            var info = s.Info;
            if (info == null) return null;
            string pillar = DeckFrames.PillarName(info);
            KeyValuePair<Material, Vector4> material;
            if (!DeckFrames.MaterialFor(pillar, out material)) return null;
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            Vector3 origin = CurveMath.Bezier(a, b, c, d, 0.5f);
            float half = Mathf.Max(info.m_halfWidth - Inset, 0.5f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(s.m_averageLength, 1f) / Step));
            var terrain = TerrainManager.instance;
            var tops = new List<Vector3>[2];
            var feet = new List<float>[2];
            var low = new List<bool>();          // the ramp no higher than MaxWallHeight there: walled
            var alongs = new List<Vector3>();
            for (int side = 0; side < 2; side++)
            {
                tops[side] = new List<Vector3>();
                feet[side] = new List<float>();
            }
            for (int k = 0; k <= steps; k++)
            {
                float t = (float)k / steps;
                Vector3 p = CurveMath.Bezier(a, b, c, d, t);
                Vector3 along = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t));
                if (along == Vector3.zero) along = CurveMath.FlatNormal(d - a);
                Vector3 across = CurveMath.Rotate(along, 90f);
                low.Add(p.y - terrain.SampleRawHeightSmooth(p) <= MaxWallHeight);
                alongs.Add(along);
                for (int side = 0; side < 2; side++)
                {
                    Vector3 edge = p + across * (side == 0 ? half : -half);
                    edge.y = p.y - UnderSurface;
                    tops[side].Add(edge);
                    feet[side].Add(terrain.SampleRawHeightSmooth(edge) - IntoGround);
                }
            }
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var texture = material.Value;
            for (int side = 0; side < 2; side++)
                for (int k = 0; k < steps; k++)
                {
                    if (!low[k] || !low[k + 1]) continue;   // higher than the walls go: open, on pillars
                    Vector3 top0 = tops[side][k], top1 = tops[side][k + 1];
                    float foot0 = Mathf.Min(feet[side][k], top0.y), foot1 = Mathf.Min(feet[side][k + 1], top1.y);
                    if (top0.y - foot0 < MinWall && top1.y - foot1 < MinWall) continue;
                    Vector3 bottom0 = new Vector3(top0.x, foot0, top0.z), bottom1 = new Vector3(top1.x, foot1, top1.z);
                    Vector3 run = CurveMath.FlatNormal(top1 - top0);
                    Vector3 outward = CurveMath.Rotate(run, side == 0 ? 90f : -90f);
                    // Its outer face, and its inner face a wall's thickness inside it (the
                    // two in one plane flickered through each other: the game draws both).
                    Quad(vertices, normals, uvs, triangles, texture, bottom0 - origin, bottom1 - origin, top1 - origin, top0 - origin, outward);
                    Vector3 inside = -outward * WallThickness;
                    Quad(vertices, normals, uvs, triangles, texture, bottom0 + inside - origin, bottom1 + inside - origin, top1 + inside - origin, top0 + inside - origin, -outward);
                }
            // Where the walls stop (the ramp higher on), an end face across the ramp
            // between them closes the abutment.
            for (int k = 0; k <= steps; k++)
            {
                if (!low[k]) continue;
                foreach (int toward in new[] { 1, -1 })
                {
                    int next = k + toward;
                    if (next < 0 || next > steps || low[next]) continue;
                    Vector3 left = tops[0][k], right = tops[1][k];
                    float leftFoot = Mathf.Min(feet[0][k], left.y), rightFoot = Mathf.Min(feet[1][k], right.y);
                    if (left.y - leftFoot < MinWall && right.y - rightFoot < MinWall) continue;
                    Vector3 facing = alongs[k] * toward;
                    Quad(vertices, normals, uvs, triangles, texture, new Vector3(left.x, leftFoot, left.z) - origin, new Vector3(right.x, rightFoot, right.z) - origin,
                        right - origin, left - origin, facing);
                }
            }
            if (vertices.Count == 0) return null;
            return new Part
            {
                Id = segment,
                BuildIndex = s.m_buildIndex,
                Origin = origin,
                Pillar = pillar,
                Mesh = DeckFrames.MakeMesh(vertices, normals, uvs, triangles),
            };
        }

        // A wall pillar across the ramp at a raised node: as wide as the ramp
        // (inside its edges), PierDepth thick, from the ground to under the deck.
        private static Part MakePier(ushort node)
        {
            var net = NetManager.instance;
            var n = net.m_nodes.m_buffer[node];
            Vector3 along = Way(node);
            if (along == Vector3.zero) return null;
            float half = 0f;
            NetInfo info = null;
            for (int i = 0; i < 8; i++)
            {
                ushort s = n.GetSegment(i);
                if (s == 0) continue;
                var segInfo = net.m_segments.m_buffer[s].Info;
                if (segInfo != null && segInfo.m_halfWidth > half)
                {
                    half = segInfo.m_halfWidth;
                    info = segInfo;
                }
            }
            if (info == null) return null;
            string pillar = DeckFrames.PillarName(info);
            KeyValuePair<Material, Vector4> material;
            if (!DeckFrames.MaterialFor(pillar, out material)) return null;
            Vector3 across = CurveMath.Rotate(along, 90f);
            Vector3 at = n.m_position;
            float top = at.y - PierUnderside;
            // Just past the walls' outer faces (not in their plane): at the end of a
            // walled stretch it covers the walls' open ends, a solid block.
            float width = 2f * Mathf.Max(half - Inset + PierProud, 0.5f);
            float ground = TerrainManager.instance.SampleRawHeightSmooth(at) - IntoGround;
            var terrain = TerrainManager.instance;
            // The lowest ground under its width (it stands on a slope too).
            foreach (float side in new[] { -1f, 1f })
                ground = Mathf.Min(ground, terrain.SampleRawHeightSmooth(at + across * (side * width * 0.5f)) - IntoGround);
            if (top - ground < MinWall) return null;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            var texture = material.Value;
            Vector3 center = new Vector3(0f, (top + ground) * 0.5f - at.y, 0f);
            Vector3 x = across * (width * 0.5f), y = Vector3.up * ((top - ground) * 0.5f), z = along * (PierDepth * 0.5f);
            // Its four sides with the concrete's texture upright (however wide the
            // pillar is: DeckFrames.Face lays it along the longer side), and its top.
            foreach (var side in new[] { z, -z, x, -x })
            {
                Vector3 run = side == z || side == -z ? x : z;
                Vector3 c = center + side;
                Quad(vertices, normals, uvs, triangles, texture, c - run - y, c + run - y, c + run + y, c - run + y, side.normalized);
            }
            DeckFrames.Face(vertices, normals, uvs, triangles, texture, center + y, x, z, Vector3.up);
            return new Part
            {
                Id = node,
                BuildIndex = n.m_buildIndex,
                Origin = at,
                Pillar = pillar,
                Mesh = DeckFrames.MakeMesh(vertices, normals, uvs, triangles),
            };
        }

        // One wall piece (corners in order round it), seen from 'outward', the
        // texture's side laid across it once (up the pillar: up the wall).
        private static void Quad(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector4 texture,
            Vector3 bottom0, Vector3 bottom1, Vector3 top1, Vector3 top0, Vector3 outward)
        {
            int first = vertices.Count;
            vertices.Add(bottom0);
            vertices.Add(bottom1);
            vertices.Add(top1);
            vertices.Add(top0);
            for (int i = 0; i < 4; i++) normals.Add(outward);
            uvs.Add(new Vector2(texture.x, texture.y));
            uvs.Add(new Vector2(texture.z, texture.y));
            uvs.Add(new Vector2(texture.z, texture.w));
            uvs.Add(new Vector2(texture.x, texture.w));
            Vector3 cross = Vector3.Cross(bottom1 - bottom0, top0 - bottom0);
            if (Vector3.Dot(cross, outward) >= 0f)
            {
                triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
                triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 3);
            }
            else
            {
                triangles.Add(first); triangles.Add(first + 2); triangles.Add(first + 1);
                triangles.Add(first); triangles.Add(first + 3); triangles.Add(first + 2);
            }
        }
    }
}
