using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealisticRoads
{
    // Split roads, VOP-style: a copy of a wide two-way road with its middle
    // (its inner lanes on both sides, and the median between them) cut out,
    // built under a ramp rising out of the road's middle until the ramp is
    // high enough to drive under. The ramp sits in the gap: no traffic, street
    // lights or median under it, and no more width than the road.
    //
    // The copies are made from the game's own roads (their lanes and 3D
    // models, cut along a line) while a city loads, before its roads are read,
    // so cities saved with them load them again. They are hidden from the menus.
    // A city saved with them needs this mod to load them.
    public static class SplitRoads
    {
        public const string Prefix = "RR Split ";
        public const float SplitClear = 5.5f;     // the split road runs on until the ramp is this high above it

        private class Variant
        {
            public string Name;
            public int Inner;     // inner lanes cut out on each side
            public float Cut;     // the gap: this far either side of the centre
            public NetInfo Info;  // once loaded
        }

        // By the road each was made from.
        private static readonly Dictionary<string, List<Variant>> variants = new Dictionary<string, List<Variant>>();
        private static readonly HashSet<NetInfo> splitInfos = new HashSet<NetInfo>();
        private static volatile bool active;

        public static bool Active => active;

        // --- making the copies (while a city loads) ------------------------------

        // Prefix of NetCollection.Awake: the collection's roads that can be split
        // get their split copies added to it, so the game sets them up with the
        // rest (before a saved city's roads are read).
        public static void AddTo(object collection)
        {
            var prefabsField = collection.GetType().GetField("m_prefabs", BindingFlags.Public | BindingFlags.Instance);
            var prefabs = prefabsField != null ? prefabsField.GetValue(collection) as NetInfo[] : null;
            if (prefabs == null || prefabs.Length == 0) return;
            var made = new List<NetInfo>();
            foreach (var road in prefabs)
            {
                if (!CanSplit(road)) continue;
                int perSide = Mathf.Min(VehicleLanes(road, -1), VehicleLanes(road, 1));
                for (int inner = 1; inner < perSide && inner <= 2; inner++)
                {
                    try
                    {
                        var copy = Make(road, inner);
                        if (copy != null) made.Add(copy);
                    }
                    catch (Exception e)
                    {
                        Mod.LogError("Making the split " + road.name + " (" + inner + " inner lane(s)) failed", e);
                    }
                }
            }
            if (made.Count == 0) return;
            var all = new NetInfo[prefabs.Length + made.Count];
            prefabs.CopyTo(all, 0);
            made.CopyTo(all, prefabs.Length);
            prefabsField.SetValue(collection, all);
            // Names the copies replace: none (kept as long as the prefabs, if set).
            var replacedField = collection.GetType().GetField("m_replacedNames", BindingFlags.Public | BindingFlags.Instance);
            var replaced = replacedField != null ? replacedField.GetValue(collection) as string[] : null;
            if (replaced != null && replaced.Length >= prefabs.Length)
            {
                var longer = new string[all.Length];
                replaced.CopyTo(longer, 0);
                for (int i = replaced.Length; i < longer.Length; i++) longer[i] = string.Empty;
                replacedField.SetValue(collection, longer);
            }
            Mod.Log("Split roads: " + made.Count + " made from the roads of " + ((Component)collection).gameObject.name);
        }

        // A two-way ground road with at least two vehicle lanes each way.
        private static bool CanSplit(NetInfo road)
        {
            if (road == null || road.m_netAI == null || road.m_netAI.GetType().Name != "RoadAI") return false;
            if (road.name != null && road.name.StartsWith(Prefix)) return false;
            return VehicleLanes(road, -1) >= 2 && VehicleLanes(road, 1) >= 2 && road.m_segments != null && road.m_lanes != null;
        }

        // Vehicle lanes on one side of the centre (-1 left, 1 right).
        private static int VehicleLanes(NetInfo road, int side)
        {
            int count = 0;
            if (road.m_lanes != null)
                foreach (var lane in road.m_lanes)
                    if (lane != null && (lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None && lane.m_position * side > 0f) count++;
            return count;
        }

        // How far from the centre 'inner' vehicle lanes on each side reach.
        private static float CutFor(NetInfo road, int inner)
        {
            var left = new List<float>();
            var right = new List<float>();
            foreach (var lane in road.m_lanes)
            {
                if (lane == null || (lane.m_laneType & NetInfo.LaneType.Vehicle) == NetInfo.LaneType.None) continue;
                float edge = Mathf.Abs(lane.m_position) + lane.m_width * 0.5f;
                (lane.m_position < 0f ? left : right).Add(edge);
            }
            left.Sort();
            right.Sort();
            if (left.Count < inner || right.Count < inner) return 0f;
            return Mathf.Max(left[inner - 1], right[inner - 1]);
        }

        private static NetInfo Make(NetInfo road, int inner)
        {
            float cut = CutFor(road, inner);
            if (cut <= 0f) return null;
            string name = Prefix + road.name + " " + inner;
            var go = UnityEngine.Object.Instantiate(road.gameObject);
            go.name = name;
            if (road.transform != null && go.transform != null) go.transform.parent = road.transform.parent;
            var copy = go.GetComponent<NetInfo>();
            if (copy == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            // Its lanes outside the gap (the median's street lights and trees go with it).
            var lanes = new List<NetInfo.Lane>();
            foreach (var lane in copy.m_lanes)
                if (lane != null && Mathf.Abs(lane.m_position) >= cut - 0.01f) lanes.Add(lane);
            copy.m_lanes = lanes.ToArray();
            // Its models with the gap cut out.
            if (copy.m_segments != null)
                foreach (var segment in copy.m_segments)
                {
                    if (segment == null) continue;
                    var mesh = CutMesh(segment.m_mesh, cut);
                    if (mesh != null) segment.m_mesh = mesh;
                    var lod = CutMesh(segment.m_lodMesh, cut);
                    if (lod != null) segment.m_lodMesh = lod;
                }
            HideFromMenus(copy);
            lock (variants)
            {
                List<Variant> list;
                if (!variants.TryGetValue(road.name, out list)) variants[road.name] = list = new List<Variant>();
                list.RemoveAll(v => v.Name == name);
                list.Add(new Variant { Name = name, Inner = inner, Cut = cut });
                list.Sort((a, b) => a.Cut.CompareTo(b.Cut));
            }
            return copy;
        }

        private static void HideFromMenus(NetInfo info)
        {
            try
            {
                var field = typeof(PrefabInfo).GetField("m_placementStyle", BindingFlags.Public | BindingFlags.Instance);
                if (field != null && field.FieldType.IsEnum) field.SetValue(info, Enum.Parse(field.FieldType, "Procedural"));
            }
            catch (Exception e)
            {
                Mod.LogError("Hiding a split road from the menus failed", e);
            }
        }

        // --- cutting a model along two lines -------------------------------------

        private struct Vert
        {
            public Vector3 P, N;
            public Vector4 T;
            public Vector2 A, B;
            public Color C;
        }

        // The model with everything closer than 'cut' to its centre line (x)
        // removed; triangles across the line are clipped to it. Null if it
        // can't be read.
        internal static Mesh CutMesh(Mesh mesh, float cut)
        {
            if (mesh == null) return null;
            var p = mesh.vertices;
            var tris = mesh.triangles;
            if (p == null || p.Length == 0 || tris == null || tris.Length == 0) return null;
            var n = mesh.normals;
            var t = mesh.tangents;
            var a = mesh.uv;
            var b = mesh.uv2;
            var c = mesh.colors;
            bool hasN = n != null && n.Length == p.Length, hasT = t != null && t.Length == p.Length, hasA = a != null && a.Length == p.Length,
                 hasB = b != null && b.Length == p.Length, hasC = c != null && c.Length == p.Length;
            var verts = new List<Vert>(p.Length);
            for (int i = 0; i < p.Length; i++)
                verts.Add(new Vert
                {
                    P = p[i], N = hasN ? n[i] : Vector3.up, T = hasT ? t[i] : new Vector4(1f, 0f, 0f, -1f),
                    A = hasA ? a[i] : Vector2.zero, B = hasB ? b[i] : Vector2.zero, C = hasC ? c[i] : new Color(1f, 1f, 1f, 1f),
                });
            var outTris = new List<int>(tris.Length);
            int original = verts.Count;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                foreach (float side in new[] { -1f, 1f })
                {
                    bool k0 = p[i0].x * side >= cut, k1 = p[i1].x * side >= cut, k2 = p[i2].x * side >= cut;
                    if (k0 && k1 && k2)
                    {
                        outTris.Add(i0); outTris.Add(i1); outTris.Add(i2);
                        continue;
                    }
                    if (!k0 && !k1 && !k2) continue;
                    // Clip the triangle to this side of the line (it keeps its winding).
                    var poly = new List<Vert> { verts[i0], verts[i1], verts[i2] };
                    var clipped = new List<Vert>();
                    for (int e = 0; e < 3; e++)
                    {
                        Vert from = poly[e], to = poly[(e + 1) % 3];
                        bool inFrom = from.P.x * side >= cut, inTo = to.P.x * side >= cut;
                        if (inFrom) clipped.Add(from);
                        if (inFrom != inTo)
                        {
                            float f = (side * cut - from.P.x) / (to.P.x - from.P.x);
                            clipped.Add(Lerp(from, to, f));
                        }
                    }
                    if (clipped.Count < 3) continue;
                    int first = verts.Count;
                    verts.AddRange(clipped);
                    for (int k = 1; k + 1 < clipped.Count; k++)
                    {
                        outTris.Add(first); outTris.Add(first + k); outTris.Add(first + k + 1);
                    }
                }
            }
            var result = new Mesh();
            result.name = mesh.name + " (split)";
            var vp = new Vector3[verts.Count];
            var vn = new Vector3[verts.Count];
            var vt = new Vector4[verts.Count];
            var va = new Vector2[verts.Count];
            var vb = new Vector2[verts.Count];
            var vc = new Color[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                vp[i] = verts[i].P; vn[i] = verts[i].N; vt[i] = verts[i].T; va[i] = verts[i].A; vb[i] = verts[i].B; vc[i] = verts[i].C;
            }
            result.vertices = vp;
            if (hasN) result.normals = vn;
            if (hasT) result.tangents = vt;
            if (hasA) result.uv = va;
            if (hasB) result.uv2 = vb;
            if (hasC) result.colors = vc;
            result.triangles = outTris.ToArray();
            result.bounds = mesh.bounds;   // the game bends the model along the road: its bounds stay the road's
            return result;
        }

        private static Vert Lerp(Vert x, Vert y, float f)
        {
            return new Vert
            {
                P = x.P + (y.P - x.P) * f,
                N = (x.N + (y.N - x.N) * f).normalized,
                T = new Vector4(x.T.x + (y.T.x - x.T.x) * f, x.T.y + (y.T.y - x.T.y) * f, x.T.z + (y.T.z - x.T.z) * f, x.T.w),
                A = x.A + (y.A - x.A) * f,
                B = x.B + (y.B - x.B) * f,
                C = new Color(x.C.r + (y.C.r - x.C.r) * f, x.C.g + (y.C.g - x.C.g) * f, x.C.b + (y.C.b - x.C.b) * f, x.C.a + (y.C.a - x.C.a) * f),
            };
        }

        // --- using them --------------------------------------------------------

        // Main thread, a city loaded: the copies as the game set them up.
        public static void Resolve()
        {
            int found = 0;
            lock (variants)
            {
                splitInfos.Clear();
                foreach (var list in variants.Values)
                    foreach (var variant in list)
                    {
                        variant.Info = PrefabCollection<NetInfo>.FindLoaded(variant.Name);
                        if (variant.Info == null) continue;
                        splitInfos.Add(variant.Info);
                        found++;
                    }
            }
            active = found > 0;
            if (variants.Count > 0) Mod.Log("Split roads: " + found + " ready");
        }

        public static bool IsSplit(NetInfo info)
        {
            // Read on any thread; only changed while a city loads (Resolve).
            return info != null && active && splitInfos.Contains(info);
        }

        // The split copy of 'road' for a ramp taking 'inner' of its lanes on
        // each side (the ramp's own lanes each way: a two-lane ramp takes the
        // inner lane of each side into it, the others go on beside it); the
        // nearest there is. Null if the road has none.
        public static NetInfo For(NetInfo road, int inner)
        {
            if (road == null) return null;
            lock (variants)
            {
                List<Variant> list;
                if (!variants.TryGetValue(road.name, out list)) return null;
                Variant best = null;
                foreach (var variant in list)
                {
                    if (variant.Info == null) continue;
                    if (best == null || Mathf.Abs(variant.Inner - inner) < Mathf.Abs(best.Inner - inner)) best = variant;
                }
                return best != null ? best.Info : null;
            }
        }

        // A ramp's lanes each way (its busier way), at least 1.
        public static int RampLanes(NetInfo ramp)
        {
            return ramp == null ? 1 : Mathf.Max(1, Mathf.Max(ramp.m_forwardVehicleLaneCount, ramp.m_backwardVehicleLaneCount));
        }

        // Where a split road and another road leave a node in the same
        // direction (within 10 degrees: a ramp rising out of its gap), both
        // end square there, instead of being mitred against each other (the
        // game stretches a corner between two roads that close far out).
        internal static bool SquareEnd(ushort segment, ushort node, Vector3 direction, NetInfo info)
        {
            if (!active || node == 0) return false;
            var net = NetManager.instance;
            bool ownSplit = IsSplit(info);
            Vector3 dir = CurveMath.FlatNormal(direction);
            if (dir == Vector3.zero) return false;
            float limit = Mathf.Cos(10f * Mathf.Deg2Rad);
            for (int i = 0; i < 8; i++)
            {
                ushort other = net.m_nodes.m_buffer[node].GetSegment(i);
                if (other == 0 || other == segment) continue;
                var s = net.m_segments.m_buffer[other];
                if (!ownSplit && !IsSplit(s.Info)) continue;
                Vector3 away = CurveMath.FlatNormal(s.m_startNode == node ? s.m_startDirection : s.m_endDirection);
                if (Vector3.Dot(away, dir) > limit) return true;
            }
            return false;
        }
    }

    // Adds the split copies to each road collection as it wakes.
    [HarmonyPatch]
    public static class SplitRoadsCollectionPatch
    {
        public static MethodBase TargetMethod()
        {
            var type = typeof(NetInfo).Assembly.GetType("NetCollection");
            return type != null ? type.GetMethod("Awake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
        }

        public static void Prefix(object __instance)
        {
            try
            {
                SplitRoads.AddTo(__instance);
            }
            catch (Exception e)
            {
                Mod.LogError("Adding the split roads failed (ramps rise out of the plain road)", e);
            }
        }
    }

    // Square ends where a split road and a ramp leave a node together.
    [HarmonyPatch]
    public static class SplitRoadsCornerPatch
    {
        public static MethodBase TargetMethod()
        {
            foreach (var method in typeof(NetSegment).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != "CalculateCorner") continue;
                var p = method.GetParameters();
                if (p.Length == 7 && p[0].ParameterType == typeof(ushort)) return method;
            }
            return null;
        }

        public static void Postfix(ref NetSegment __instance, ushort __0, bool __2, ref Vector3 __4, ref Vector3 __5, ref bool __6)
        {
            try
            {
                if (!SplitRoads.Active) return;
                bool start = __2;
                ushort node = start ? __instance.m_startNode : __instance.m_endNode;
                Vector3 dir = start ? __instance.m_startDirection : __instance.m_endDirection;
                var info = __instance.Info;
                if (info == null || !SplitRoads.SquareEnd(__0, node, dir, info)) return;
                Vector3 centre = NetManager.instance.m_nodes.m_buffer[node].m_position;
                Vector3 flat = CurveMath.FlatNormal(dir);
                Vector3 right = new Vector3(flat.z, 0f, -flat.x);
                // The side the game put this corner on, at the road's half width, square across.
                float side = Vector3.Dot(__4 - centre, right) >= 0f ? 1f : -1f;
                __4 = new Vector3(centre.x + right.x * side * info.m_halfWidth, __4.y, centre.z + right.z * side * info.m_halfWidth);
                __5 = dir;
                __6 = true;
            }
            catch
            {
                // The game's corner stays.
            }
        }
    }
}
