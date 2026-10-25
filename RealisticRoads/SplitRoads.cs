using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealisticRoads
{
    // Split roads, VOP-style: a copy of a wide two-way road with its middle
    // (its inner lanes on both sides, and the median between them) taken out,
    // built under a ramp rising out of the road's middle until the ramp is
    // high enough to drive under. The ramp sits in the middle: no traffic,
    // street lights or raised median under it, and no more width than the road.
    // The middle stays paved (the median flattened into the road's surface),
    // so no ground shows under the ramp's raised part or beside a narrow ramp.
    //
    // The copies are made from the game's own roads (their lanes and 3D
    // models) while a city loads, before its roads are read,
    // so cities saved with them load them again. They are hidden from the menus.
    // A city saved with them needs this mod to load them.
    public static class SplitRoads
    {
        public const string Prefix = "RR Split ";

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
        private static readonly List<Mesh> madeMeshes = new List<Mesh>();   // freed when the city unloads
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
            int candidates = 0;
            foreach (var road in prefabs)
            {
                if (!CanSplit(road)) continue;
                candidates++;
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
            string collectionName = ((Component)collection).gameObject.name;
            int splits = made.Count;
            try
            {
                MakeRamps(prefabs, made);
            }
            catch (Exception e)
            {
                Mod.LogError("Making the median ramp roads failed", e);
            }
            if (made.Count == 0)
            {
                if (candidates > 0) Mod.Log("Split roads: " + candidates + " road(s) of " + collectionName + " could be split, but no copy was made");
                return;
            }
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
            Mod.Log("Split roads: " + splits + " made from " + candidates + " road(s) of " + collectionName);
        }

        // A two-way ground road with at least two vehicle lanes each way.
        private static bool CanSplit(NetInfo road)
        {
            if (road == null) return false;
            // While its collection wakes the road isn't set up yet (m_netAI is
            // filled in then): its AI is the component on it.
            var ai = road.m_netAI != null ? road.m_netAI : road.GetComponent<NetAI>();
            if (ai == null || ai.GetType().Name != "RoadAI") return false;
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
            OwnItems(copy);
            // Its lanes outside the gap (the median's street lights and trees go with it).
            var lanes = new List<NetInfo.Lane>();
            foreach (var lane in copy.m_lanes)
                if (lane != null && Mathf.Abs(lane.m_position) >= cut - 0.01f) lanes.Add(lane);
            copy.m_lanes = lanes.ToArray();
            // Its models (segments, and nodes: the ends of the split stretch) with
            // the middle flattened into the road's surface.
            float surface = road.m_surfaceLevel;
            if (copy.m_segments != null)
                foreach (var segment in copy.m_segments)
                {
                    if (segment == null) continue;
                    var mesh = FlattenMiddle(segment.m_mesh, cut, surface);
                    if (mesh != null) segment.m_mesh = mesh;
                    var lod = FlattenMiddle(segment.m_lodMesh, cut, surface);
                    if (lod != null) segment.m_lodMesh = lod;
                }
            if (copy.m_nodes != null)
                foreach (var node in copy.m_nodes)
                {
                    if (node == null) continue;
                    var mesh = FlattenMiddle(node.m_mesh, cut, surface);
                    if (mesh != null) node.m_mesh = mesh;
                    var lod = FlattenMiddle(node.m_lodMesh, cut, surface);
                    if (lod != null) node.m_lodMesh = lod;
                }
            HideFromMenus(copy);
            Name(copy, Title(road) + " (split, " + inner + " lane" + (inner > 1 ? "s" : "") + " each way)",
                "Built by Realistic Roads under a ramp rising out of the road's middle.");
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

        private static bool namingFailed;

        private static string Title(NetInfo road)
        {
            string title;
            try
            {
                title = road.GetUncheckedLocalizedTitle();
            }
            catch
            {
                title = null;
            }
            return string.IsNullOrEmpty(title) ? road.name : title;
        }

        // The copy's name and description in the game's text tables (it looks
        // them up for every road; without them it logs an error for each copy).
        private static void Name(NetInfo copy, string title, string description)
        {
            if (namingFailed) return;
            try
            {
                var managerType = Type.GetType("ColossalFramework.Globalization.LocaleManager, ColossalManaged");
                var manager = managerType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null, null);
                var locale = managerType.GetField("m_Locale", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(manager);
                var keyType = locale.GetType().GetNestedType("Key");
                var add = locale.GetType().GetMethod("AddLocalizedString", new[] { keyType, typeof(string) });
                var texts = new[]
                {
                    new KeyValuePair<string, string>("NET_TITLE", title),
                    new KeyValuePair<string, string>("NET_DESC", description),
                };
                foreach (var text in texts)
                {
                    object key = Activator.CreateInstance(keyType);
                    keyType.GetField("m_Identifier").SetValue(key, text.Key);
                    keyType.GetField("m_Key").SetValue(key, copy.name);
                    keyType.GetField("m_Index").SetValue(key, 0);
                    try
                    {
                        add.Invoke(locale, new[] { key, (object)text.Value });
                    }
                    catch
                    {
                        // Already there (a city loaded before).
                    }
                }
            }
            catch (Exception e)
            {
                namingFailed = true;
                Mod.Log("Split roads: naming them in the game's text tables failed (" + e.GetType().Name + ": " + e.Message +
                        "); the game logs a missing name for each, harmlessly");
            }
        }

        private static readonly MethodInfo memberwiseClone =
            typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        private static T Copy<T>(T item) where T : class
        {
            return item == null ? null : (T)memberwiseClone.Invoke(item, null);
        }

        // The copy's own lanes and model entries, so changing them never changes
        // the road it was copied from (Unity's copy should already make new
        // ones; this makes sure).
        private static void OwnItems(NetInfo copy)
        {
            if (copy.m_lanes != null)
                for (int i = 0; i < copy.m_lanes.Length; i++) copy.m_lanes[i] = Copy(copy.m_lanes[i]);
            if (copy.m_segments != null)
                for (int i = 0; i < copy.m_segments.Length; i++) copy.m_segments[i] = Copy(copy.m_segments[i]);
            if (copy.m_nodes != null)
                for (int i = 0; i < copy.m_nodes.Length; i++) copy.m_nodes[i] = Copy(copy.m_nodes[i]);
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

        // --- narrow ramp roads (for median ramps) ---------------------------------

        public const string RampPrefix = "RR Ramp ";
        private const float RampBarrier = 0.6f;    // the parapet each side of a ramp road
        private static readonly Dictionary<int, string> rampNames = new Dictionary<int, string>();   // by its lanes (2, 4)
        private static readonly Dictionary<int, NetInfo> ramps = new Dictionary<int, NetInfo>();

        // A two-lane and a four-lane ramp road (a ground and an elevated
        // version each), from the game's Two-Lane and Four-Lane Road, as wide as
        // the gap in the split Six-Lane Road they rise out of (its inner one or
        // two lanes each way): no sidewalks or parking, the lanes a little
        // narrower, a parapet at each edge.
        private static void MakeRamps(NetInfo[] prefabs, List<NetInfo> made)
        {
            NetInfo basic = null, medium = null, large = null;
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                if (prefab.name == "Basic Road") basic = prefab;
                else if (prefab.name == "Medium Road") medium = prefab;
                else if (prefab.name == "Large Road") large = prefab;
            }
            if (basic != null) MakeRamp(basic, 2, large != null ? CutFor(large, 1) : 3f, made);
            if (medium != null) MakeRamp(medium, 4, large != null ? CutFor(large, 2) : 6f, made);
        }

        private static void MakeRamp(NetInfo road, int lanes, float half, List<NetInfo> made)
        {
            if (half <= 1f) return;
            var ai = road.m_netAI != null ? road.m_netAI : road.GetComponent<NetAI>();
            var elevatedField = ai != null ? ai.GetType().GetField("m_elevatedInfo", BindingFlags.Public | BindingFlags.Instance) : null;
            var elevated = elevatedField != null ? elevatedField.GetValue(ai) as NetInfo : null;
            if (elevated == null) return;
            var narrowElevated = Narrow(elevated, RampPrefix + lanes + " Elevated", half);
            var narrow = Narrow(road, RampPrefix + lanes, half);
            if (narrow == null || narrowElevated == null) return;
            // The ground version's raised versions: the narrow elevated one (no bridge
            // trusses, no slope or tunnel: a ramp never goes below the ground).
            var narrowAi = narrow.GetComponent<NetAI>();
            foreach (var field in new[] { "m_elevatedInfo", "m_bridgeInfo", "m_slopeInfo", "m_tunnelInfo" })
            {
                var f = narrowAi != null ? narrowAi.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance) : null;
                if (f != null && f.FieldType == typeof(NetInfo)) f.SetValue(narrowAi, field == "m_elevatedInfo" || field == "m_bridgeInfo" ? narrowElevated : null);
            }
            string title = lanes + "-lane median ramp";
            Name(narrow, title, "A ramp rising out of a road's middle, built by Realistic Roads.");
            Name(narrowElevated, title + " (elevated)", "A ramp rising out of a road's middle, built by Realistic Roads.");
            made.Add(narrowElevated);
            made.Add(narrow);
            lock (variants) rampNames[lanes] = narrow.name;
            Mod.Log("Median ramps: " + narrow.name + " made from " + road.name + ", " + (2f * half).ToString("F1") + " m wide");
        }

        // A copy of 'road' 'half' either side of its centre: its vehicle lanes
        // (closer together if need be), its models squeezed to match: the
        // carriageway scaled, the sidewalks and parking gone, the outer edge (the
        // parapet or kerb) kept and moved in.
        private static NetInfo Narrow(NetInfo road, string name, float half)
        {
            // The carriageway: from the median's edge (0 without one) to the outer
            // vehicle lane's edge. A median is closed up (a ramp has none).
            float keep = 0f, median = float.MaxValue;
            foreach (var lane in road.m_lanes)
                if (lane != null && (lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None)
                {
                    keep = Mathf.Max(keep, Mathf.Abs(lane.m_position) + lane.m_width * 0.5f);
                    median = Mathf.Min(median, Mathf.Abs(lane.m_position) - lane.m_width * 0.5f);
                }
            median = Mathf.Max(0f, median == float.MaxValue ? 0f : median);
            float drive = keep - median;
            if (drive <= 0f) return null;
            // Lanes no narrower than 85 % of the road's (a car still fits).
            float inner = Mathf.Max(half - RampBarrier, drive * 0.85f);
            float scale = Mathf.Min(1f, inner / drive);
            inner = drive * scale;
            float roadHalf = road.m_halfWidth;
            var go = UnityEngine.Object.Instantiate(road.gameObject);
            go.name = name;
            if (road.transform != null && go.transform != null) go.transform.parent = road.transform.parent;
            var copy = go.GetComponent<NetInfo>();
            if (copy == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            OwnItems(copy);
            var lanes = new List<NetInfo.Lane>();
            foreach (var lane in copy.m_lanes)
            {
                if (lane == null || (lane.m_laneType & NetInfo.LaneType.Vehicle) == NetInfo.LaneType.None) continue;
                float side = lane.m_position < 0f ? -1f : 1f;
                lane.m_position = side * Mathf.Max(0f, Mathf.Abs(lane.m_position) - median) * scale;
                lane.m_width *= scale;
                lanes.Add(lane);
            }
            copy.m_lanes = lanes.ToArray();
            copy.m_halfWidth = inner + RampBarrier;
            copy.m_pavementWidth = RampBarrier;
            float surface = road.m_surfaceLevel;
            if (copy.m_segments != null)
            {
                var kept = new List<NetInfo.Segment>();
                foreach (var segment in copy.m_segments)
                {
                    if (segment == null) continue;
                    // A model of the median alone (an elevated road's median strip) goes.
                    if (median > 0.5f && MaxX(segment.m_mesh) <= median + 0.5f) continue;
                    var mesh = Squeeze(segment.m_mesh, roadHalf, median, keep, scale, inner, surface);
                    if (mesh != null) segment.m_mesh = mesh;
                    var lod = Squeeze(segment.m_lodMesh, roadHalf, median, keep, scale, inner, surface);
                    if (lod != null) segment.m_lodMesh = lod;
                    kept.Add(segment);
                }
                copy.m_segments = kept.ToArray();
            }
            if (copy.m_nodes != null)
                foreach (var node in copy.m_nodes)
                {
                    if (node == null) continue;
                    var mesh = Squeeze(node.m_mesh, roadHalf, median, keep, scale, inner, surface);
                    if (mesh != null) node.m_mesh = mesh;
                    var lod = Squeeze(node.m_lodMesh, roadHalf, median, keep, scale, inner, surface);
                    if (lod != null) node.m_lodMesh = lod;
                }
            HideFromMenus(copy);
            return copy;
        }

        // A model squeezed across, measured from the road's half width (a ground
        // road's model reaches past it into the terrain): a median (within
        // 'median' of the centre) closed up and flattened; the carriageway
        // (to 'keep') scaled to 'inner'; the sidewalk and parking collapsed onto
        // 'inner'; the last RampBarrier of the road (its kerb or parapet) and
        // anything past its half width (the parapet's outside, the skirt into
        // the ground) moved in unchanged.
        internal static Mesh Squeeze(Mesh mesh, float roadHalf, float median, float keep, float scale, float inner, float surface)
        {
            if (mesh == null) return null;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0) return null;
            float edgeStart = Mathf.Max(keep, roadHalf - RampBarrier);
            float shift = edgeStart - inner;   // how far the edge moves in
            for (int i = 0; i < vertices.Length; i++)
            {
                float a = Mathf.Abs(vertices[i].x), y = vertices[i].y, squeezed;
                if (a <= median)
                {
                    squeezed = 0f;
                    y = Mathf.Min(y, surface);
                }
                else if (a <= keep) squeezed = (a - median) * scale;
                else if (a < edgeStart) squeezed = inner;
                else squeezed = a - shift;
                vertices[i] = new Vector3(vertices[i].x < 0f ? -squeezed : squeezed, y, vertices[i].z);
            }
            return CopyWith(mesh, vertices, triangles, " (ramp)");
        }

        private static float MaxX(Mesh mesh)
        {
            if (mesh == null) return float.MaxValue;
            try
            {
                var vertices = mesh.vertices;
                if (vertices == null || vertices.Length == 0) return float.MaxValue;
                float max = 0f;
                foreach (var v in vertices) max = Mathf.Max(max, Mathf.Abs(v.x));
                return max;
            }
            catch
            {
                return float.MaxValue;
            }
        }

        private static Mesh CopyWith(Mesh mesh, Vector3[] vertices, int[] triangles, string suffix)
        {
            var result = new Mesh();
            result.name = mesh.name + suffix;
            result.vertices = vertices;
            var normals = mesh.normals;
            if (normals != null && normals.Length == vertices.Length) result.normals = normals;
            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length == vertices.Length) result.tangents = tangents;
            var uv = mesh.uv;
            if (uv != null && uv.Length == vertices.Length) result.uv = uv;
            var uv2 = mesh.uv2;
            if (uv2 != null && uv2.Length == vertices.Length) result.uv2 = uv2;
            var colors = mesh.colors;
            if (colors != null && colors.Length == vertices.Length) result.colors = colors;
            result.triangles = triangles;
            result.bounds = mesh.bounds;   // the game bends the model along the road: its bounds stay the road's
            lock (madeMeshes) madeMeshes.Add(result);
            return result;
        }

        // One of the ramp roads (either version).
        public static bool IsRamp(NetInfo info)
        {
            return info != null && info.name != null && info.name.StartsWith(RampPrefix);
        }

        // The ramp road with 'lanes' lanes (2 or 4), once the city is loaded; null if none.
        public static NetInfo RampRoad(int lanes)
        {
            NetInfo info;
            lock (variants) return ramps.TryGetValue(lanes, out info) ? info : null;
        }

        // --- flattening a model's middle -----------------------------------------

        // The model with everything closer than 'cut' to its centre line (x) and
        // above the road's surface (a raised median, its kerbs) brought down to
        // the surface: the middle stays paved, only flat. Null if it can't be read.
        internal static Mesh FlattenMiddle(Mesh mesh, float cut, float surface)
        {
            if (mesh == null) return null;
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0) return null;
            int moved = 0;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (Mathf.Abs(vertices[i].x) >= cut - 0.01f || vertices[i].y <= surface) continue;
                vertices[i] = new Vector3(vertices[i].x, surface, vertices[i].z);
                moved++;
            }
            if (moved == 0) return null;   // nothing raised in the middle: the model as it is
            return CopyWith(mesh, vertices, triangles, " (split)");
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
            lock (variants)
            {
                ramps.Clear();
                foreach (var pair in rampNames)
                {
                    var info = PrefabCollection<NetInfo>.FindLoaded(pair.Value);
                    if (info != null) ramps[pair.Key] = info;
                }
            }
            // The ramps' elevated versions on the highway ramp's slim pillar (the
            // road's own is made for its full width).
            var pillar = PrefabCollection<BuildingInfo>.FindLoaded("HighwayRampPillar");
            if (pillar != null)
                foreach (var ramp in ramps.Values)
                {
                    var elevated = RoadPlanner.Variant(ramp, "m_elevatedInfo");
                    var ai = elevated != null ? elevated.m_netAI : null;
                    if (ai == null) continue;
                    foreach (var field in new[] { "m_bridgePillarInfo", "m_middlePillarInfo" })
                    {
                        var f = ai.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
                        if (f != null && f.FieldType == typeof(BuildingInfo) && f.GetValue(ai) != null) f.SetValue(ai, pillar);
                    }
                }
            active = found > 0;
            if (variants.Count > 0) Mod.Log("Split roads: " + found + " ready; median ramp roads: " + ramps.Count);
        }

        // Main thread, the city unloading: its copies go (made again for the next).
        public static void Clear()
        {
            active = false;
            lock (variants)
            {
                splitInfos.Clear();
                ramps.Clear();
                foreach (var list in variants.Values) foreach (var variant in list) variant.Info = null;
            }
            lock (madeMeshes)
            {
                foreach (var mesh in madeMeshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
                madeMeshes.Clear();
            }
        }

        // The road a split copy was made from (the road itself if it is none).
        public static NetInfo OriginalOf(NetInfo info)
        {
            if (!IsSplit(info)) return info;
            lock (variants)
                foreach (var pair in variants)
                    foreach (var variant in pair.Value)
                        if (variant.Info == info) return PrefabCollection<NetInfo>.FindLoaded(pair.Key) ?? info;
            return info;
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
        // direction (within 10 degrees: a ramp rising out of its middle), they
        // and the road coming in the other way end square there, instead of
        // being mitred against each other (the game stretches or pinches a
        // corner between roads whose edges meet far out, or never).
        internal static bool SquareEnd(ushort segment, ushort node, Vector3 direction)
        {
            if (!active || node == 0) return false;
            Vector3 dir = CurveMath.FlatNormal(direction);
            if (dir == Vector3.zero) return false;
            var net = NetManager.instance;
            float limit = Mathf.Cos(10f * Mathf.Deg2Rad);
            var away = new Vector3[8];
            var split = new bool[8];
            var ids = new ushort[8];
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                if (s == 0) continue;
                var seg = net.m_segments.m_buffer[s];
                ids[count] = s;
                away[count] = CurveMath.FlatNormal(seg.m_startNode == node ? seg.m_startDirection : seg.m_endDirection);
                split[count] = IsSplit(seg.Info);
                count++;
            }
            // A split road and another one leaving together: their line.
            for (int a = 0; a < count; a++)
                for (int b = a + 1; b < count; b++)
                {
                    if (!split[a] && !split[b]) continue;
                    if (Vector3.Dot(away[a], away[b]) <= limit) continue;
                    // This segment along that line, either way.
                    return Mathf.Abs(Vector3.Dot(dir, away[a])) > limit;
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

        public static void Postfix(ref NetSegment __instance, ushort __0, bool __2, bool __3, ref Vector3 __4, ref Vector3 __5, ref bool __6)
        {
            try
            {
                if (!SplitRoads.Active) return;
                bool start = __2;
                ushort node = start ? __instance.m_startNode : __instance.m_endNode;
                Vector3 dir = start ? __instance.m_startDirection : __instance.m_endDirection;
                var info = __instance.Info;
                if (info == null || !SplitRoads.SquareEnd(__0, node, dir)) return;
                Vector3 centre = NetManager.instance.m_nodes.m_buffer[node].m_position;
                Vector3 flat = CurveMath.FlatNormal(dir);
                Vector3 right = new Vector3(flat.z, 0f, -flat.x);
                // Square across at the road's half width. Left and right are the
                // segment's own (from its start node to its end node); 'dir' points
                // into it, so at its end node they are the other way round.
                float side = start != __3 ? 1f : -1f;
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
