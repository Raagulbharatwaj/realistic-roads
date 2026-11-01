using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealisticRoads
{
    // The pieces of the three median ramp roads, built the way the Vanilla
    // Overpass Project builds its overpasses: a ground road (the Six- or
    // Four-Lane Road without its inner lanes, its halves moved so the gap in
    // its middle is as wide as the ramp, the gap paved flat) and a narrow ramp
    // (9 m with two lanes, 15 m with four; no sidewalks) that runs in that gap.
    //
    // They are made from the game's own roads (their lanes and 3D models)
    // while a city loads, before its roads are read, so cities saved with
    // them load them again. They are hidden from the menus. A city saved with
    // them needs this mod to load them.
    public static class SplitRoads
    {
        // The ground roads of the three median ramp roads: a split road whose two
        // halves (each carriageway with its sidewalk) are moved out (or in, past
        // a median) so the gap is as wide as the median ramp (9 m for a 2-lane ramp, 15 m
        // for a 4-lane one) and its parapets stay clear of the ground lanes.
        public const string MedianPrefix = "RR Median Ground ";
        public const float MedianGap2 = 4.5f;   // half the gap for (and the width of) a 2-lane median ramp: 9 m
        public const float MedianGap4 = 7.5f;   // and a 4-lane one: 15 m

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

        // Prefix of NetCollection.Awake: the median ramp roads' pieces (their
        // ground roads and ramps) are added to the collection holding the
        // game's roads they are made from, so the game sets them up with the
        // rest (before a saved city's roads are read). Nothing else is made.
        public static void AddTo(object collection)
        {
            var prefabsField = collection.GetType().GetField("m_prefabs", BindingFlags.Public | BindingFlags.Instance);
            var prefabs = prefabsField != null ? prefabsField.GetValue(collection) as NetInfo[] : null;
            if (prefabs == null || prefabs.Length == 0) return;
            var made = new List<NetInfo>();
            // The median ramp roads' ground roads: the Six-Lane Road without its
            // inner one or two lanes each way, the Four-Lane Road without its inner one.
            foreach (var road in prefabs)
            {
                if (road == null || !CanSplit(road)) continue;
                int[] inners = road.name == "Large Road" ? new[] { 1, 2 } : road.name == "Medium Road" ? new[] { 1 } : new int[0];
                foreach (int inner in inners)
                {
                    try
                    {
                        // Its gap as wide as the ramp: the halves moved out (or in,
                        // past a median) to it.
                        var copy = Make(road, inner, inner == 1 ? MedianGap2 : MedianGap4);
                        if (copy != null) made.Add(copy);
                    }
                    catch (Exception e)
                    {
                        Mod.LogError("Making the median ground road from " + road.name + " (" + inner + " inner lane(s)) failed", e);
                    }
                }
            }
            string collectionName = ((Component)collection).gameObject.name;
            try
            {
                MakeRamps(prefabs, made);
            }
            catch (Exception e)
            {
                Mod.LogError("Making the median ramp roads failed", e);
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
            Mod.Log("Median ramp roads: " + made.Count + " piece(s) made in " + collectionName);
        }

        // A two-way ground road with at least two vehicle lanes each way.
        private static bool CanSplit(NetInfo road)
        {
            if (road == null) return false;
            // While its collection wakes the road isn't set up yet (m_netAI is
            // filled in then): its AI is the component on it.
            var ai = road.m_netAI != null ? road.m_netAI : road.GetComponent<NetAI>();
            if (ai == null || ai.GetType().Name != "RoadAI") return false;
            if (road.name != null && road.name.StartsWith(MedianPrefix)) return false;
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

        // A median ramp road's ground road: 'road' without its 'inner' inner
        // lanes each way, each half moved (out, or in past a median) so the gap
        // reaches 'gapHalf' either side.
        private static NetInfo Make(NetInfo road, int inner, float gapHalf)
        {
            float cut = CutFor(road, inner);
            if (cut <= 0f || gapHalf <= 0f) return null;
            float widen = gapHalf - cut;
            string name = MedianPrefix + road.name + " " + inner;
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
            // The road keeps its width: each carriageway moves into its sidewalk
            // (narrower, or wider moving in), the kerb with it, the outer edge
            // stays (no step where it meets the full road, nothing past the
            // road's own footprint). If the sidewalk is too narrow for that, the
            // whole half moves and the road gets wider.
            float edge = road.m_halfWidth, kerb = road.m_halfWidth - road.m_pavementWidth;
            bool inside = kerb > cut + 0.5f && edge - (kerb + widen) >= MinSidewalk;
            var lanes = new List<NetInfo.Lane>();
            foreach (var lane in copy.m_lanes)
                if (lane != null && Mathf.Abs(lane.m_position) >= cut - 0.01f)
                {
                    float a = Mathf.Abs(lane.m_position);
                    float moved = inside ? Across(a, cut, widen, kerb, edge) : a + widen;
                    lane.m_position = lane.m_position < 0f ? -moved : moved;
                    lanes.Add(lane);
                }
            copy.m_lanes = lanes.ToArray();
            if (inside) copy.m_pavementWidth = edge - (kerb + widen);
            else copy.m_halfWidth += widen;
            // Its models (segments, and nodes: the ends of the split stretch) with
            // the middle flattened into the road's surface.
            float surface = road.m_surfaceLevel;
            if (copy.m_segments != null)
                foreach (var segment in copy.m_segments)
                {
                    if (segment == null) continue;
                    var mesh = Widen(segment.m_mesh, cut, widen, surface, inside ? kerb : -1f, edge);
                    if (mesh != null) segment.m_mesh = mesh;
                    var lod = Widen(segment.m_lodMesh, cut, widen, surface, inside ? kerb : -1f, edge);
                    if (lod != null) segment.m_lodMesh = lod;
                }
            if (copy.m_nodes != null)
                foreach (var node in copy.m_nodes)
                {
                    if (node == null) continue;
                    var mesh = Widen(node.m_mesh, cut, widen, surface, inside ? kerb : -1f, edge);
                    if (mesh != null) node.m_mesh = mesh;
                    var lod = Widen(node.m_lodMesh, cut, widen, surface, inside ? kerb : -1f, edge);
                    if (lod != null) node.m_lodMesh = lod;
                }
            HideFromMenus(copy);
            Name(copy, Title(road) + " (" + (VehicleLanes(copy, -1) + VehicleLanes(copy, 1)) + " ground lanes beside a median ramp)",
                "Built by Realistic Roads beside a ramp in the road's middle.");
            lock (variants)
            {
                List<Variant> list;
                if (!variants.TryGetValue(road.name, out list)) variants[road.name] = list = new List<Variant>();
                list.RemoveAll(v => v.Name == name);
                list.Add(new Variant { Name = name, Inner = inner, Cut = cut + widen });
                list.Sort((a, b) => a.Cut.CompareTo(b.Cut));
            }
            Mod.Log("Median ramp roads: " + name + " from " + road.name + " (" + (2f * road.m_halfWidth).ToString("F1") + " m, inner lane edge " +
                        cut.ToString("F1") + " m): " + (VehicleLanes(copy, -1) + VehicleLanes(copy, 1)) + " ground lanes, halves moved " +
                        widen.ToString("+0.0;-0.0") + " m" + (inside ? " into its sidewalks (now " + copy.m_pavementWidth.ToString("F1") + " m)" : " (the road wider)") +
                        ", a " + (2f * (cut + widen)).ToString("F1") + " m gap in the middle, " + (2f * copy.m_halfWidth).ToString("F1") + " m wide");
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
                Mod.Log("Median ramp roads: naming them in the game's text tables failed (" + e.GetType().Name + ": " + e.Message +
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

        // A ramp draws nothing of its own where it meets other roads at a node
        // (a junction): its models there are a parapet wrapping round the
        // corner, which at the node it shares with the road it rises out of
        // would wrap across that road's lanes. The other roads' junction
        // pieces and the ramp's square end meet there instead. Its own joins
        // (two ramp segments, a bend) keep theirs. By name: the game's fields.
        private static void NoJunctionPieces(NetInfo ramp)
        {
            if (ramp.m_nodes == null) return;
            try
            {
                if (!Enum.IsDefined(typeof(NetNode.Flags), "Junction")) return;
                var junction = (NetNode.Flags)Enum.Parse(typeof(NetNode.Flags), "Junction");
                var field = typeof(NetInfo.Node).GetField("m_flagsForbidden", BindingFlags.Public | BindingFlags.Instance);
                if (field == null || field.FieldType != typeof(NetNode.Flags)) return;
                int changed = 0;
                foreach (var node in ramp.m_nodes)
                {
                    if (node == null) continue;
                    field.SetValue(node, (NetNode.Flags)field.GetValue(node) | junction);
                    changed++;
                }
                Mod.Log("Median ramps: " + ramp.name + ": " + changed + " node model(s) not drawn at junctions");
            }
            catch (Exception e)
            {
                Mod.LogError("Keeping " + ramp.name + "'s parapet off its junctions failed", e);
            }
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

        public const string MedianRampPrefix = "RR Median Ramp ";   // the median ramp roads' ramps (9 and 15 m)
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
            NetInfo basic = null, medium = null;
            foreach (var prefab in prefabs)
            {
                if (prefab == null) continue;
                if (prefab.name == "Basic Road") basic = prefab;
                else if (prefab.name == "Medium Road") medium = prefab;
            }
            // The median ramp roads' ramps: as wide as their ground road's gap
            // (9 m with two lanes, 15 m with four), the lanes at full width and a
            // shoulder to the parapet.
            if (basic != null) MakeRamp(basic, 2, MedianGap2, made);
            if (medium != null) MakeRamp(medium, 4, MedianGap4, made);
        }

        private static readonly List<string> allRampNames = new List<string>();   // every ramp road made (their pillars)

        private static void MakeRamp(NetInfo road, int lanes, float half, List<NetInfo> made)
        {
            if (half <= 1f) return;
            var ai = road.m_netAI != null ? road.m_netAI : road.GetComponent<NetAI>();
            var elevatedField = ai != null ? ai.GetType().GetField("m_elevatedInfo", BindingFlags.Public | BindingFlags.Instance) : null;
            var elevated = elevatedField != null ? elevatedField.GetValue(ai) as NetInfo : null;
            if (elevated == null) return;
            var narrowElevated = Narrow(elevated, MedianRampPrefix + lanes + " Elevated", half);
            var narrow = Narrow(road, MedianRampPrefix + lanes, half);
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
            lock (variants)
            {
                rampNames[lanes] = narrow.name;
                allRampNames.Add(narrow.name);
            }
            Mod.Log("Median ramps: " + narrow.name + " made from " + road.name + ", " + (2f * narrow.m_halfWidth).ToString("F1") + " m wide (elevated " +
                    (2f * narrowElevated.m_halfWidth).ToString("F1") + " m), lanes " + LaneList(narrow));
        }

        // A copy of 'road' 'half' either side of its centre: its vehicle lanes
        // (closer together if need be), its models squeezed to match: the
        // carriageway scaled, the sidewalks and parking gone, the outer edge (the
        // parapet or kerb) kept and moved in.
        private static string LaneList(NetInfo road)
        {
            var parts = new List<string>();
            foreach (var lane in road.m_lanes)
                if (lane != null && (lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None)
                    parts.Add(lane.m_position.ToString("F2") + "/" + lane.m_width.ToString("F2"));
            return string.Join(" ", parts.ToArray());
        }

        // The road is exactly 'half' either side (the parapet at its edge), the
        // lanes filling it or a shoulder between (see below).
        private const float MaxLaneStretch = 1.35f;   // a ramp's lanes at most this much wider than its road's

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
            float roadHalf = road.m_halfWidth;
            // The carriageway's edge (past the outer lane: a gutter), where the sidewalk starts.
            float gutter = Mathf.Clamp(road.m_halfWidth - road.m_pavementWidth, keep, Mathf.Max(keep, roadHalf - RampBarrier));
            // With a gutter, the lanes keep their width (at most) and the gutter
            // becomes the shoulder to the parapet; without one (the sidewalk's
            // kerb at the outer lane) the lanes widen to the parapet instead, so
            // no sidewalk is drawn squeezed into a strip beside them.
            bool hasGutter = gutter > keep + 0.3f;
            float scale = hasGutter ? Mathf.Min(1f, inner / drive) : Mathf.Min(MaxLaneStretch, inner / drive);
            float laneEdge = drive * scale;
            if (inner < laneEdge) inner = laneEdge;
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
                    var mesh = Squeeze(segment.m_mesh, roadHalf, median, keep, scale, inner, surface, gutter);
                    if (mesh != null) segment.m_mesh = mesh;
                    var lod = Squeeze(segment.m_lodMesh, roadHalf, median, keep, scale, inner, surface, gutter);
                    if (lod != null) segment.m_lodMesh = lod;
                    kept.Add(segment);
                }
                copy.m_segments = kept.ToArray();
            }
            if (copy.m_nodes != null)
                foreach (var node in copy.m_nodes)
                {
                    if (node == null) continue;
                    var mesh = Squeeze(node.m_mesh, roadHalf, median, keep, scale, inner, surface, gutter);
                    if (mesh != null) node.m_mesh = mesh;
                    var lod = Squeeze(node.m_lodMesh, roadHalf, median, keep, scale, inner, surface, gutter);
                    if (lod != null) node.m_lodMesh = lod;
                }
            NoJunctionPieces(copy);
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
        // 'gutter' >= 0 (a median ramp road's ramp): the model is first cut
        // along each of those edges, so the lanes keep their markings, and the
        // gutter (from the outer lane to 'gutter') becomes the shoulder from the
        // lanes to 'inner'.
        internal static Mesh Squeeze(Mesh mesh, float roadHalf, float median, float keep, float scale, float inner, float surface, float gutter = -1f)
        {
            if (mesh == null) return null;
            float edgeStart = Mathf.Max(keep, roadHalf - RampBarrier);
            Sliced sliced = null;
            Vector3[] vertices;
            int[] triangles;
            if (gutter >= 0f)
            {
                var lines = new List<float>();
                foreach (float x in new[] { median, keep, gutter, edgeStart })
                    if (x > 0.01f && !lines.Contains(x)) { lines.Add(x); lines.Add(-x); }
                sliced = SliceAcross(mesh, lines.ToArray());
                if (sliced == null) return null;
                vertices = sliced.Vertices;
                triangles = sliced.Triangles;
            }
            else
            {
                vertices = mesh.vertices;
                triangles = mesh.triangles;
                if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0) return null;
            }
            float shift = edgeStart - inner;   // how far the edge moves in
            float laneEdge = (keep - median) * scale;
            for (int i = 0; i < vertices.Length; i++)
            {
                float a = Mathf.Abs(vertices[i].x), y = vertices[i].y, squeezed;
                if (a <= median)
                {
                    squeezed = 0f;
                    y = Mathf.Min(y, surface);
                }
                else if (a <= keep) squeezed = (a - median) * scale;
                else if (gutter > keep && a < gutter) squeezed = laneEdge + (inner - laneEdge) * (a - keep) / (gutter - keep);
                else if (a < edgeStart) squeezed = inner;
                else squeezed = a - shift;
                vertices[i] = new Vector3(vertices[i].x < 0f ? -squeezed : squeezed, y, vertices[i].z);
            }
            return sliced != null ? sliced.Make(mesh, " (ramp)") : CopyWith(mesh, vertices, triangles, " (ramp)");
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
            return info != null && info.name != null && info.name.StartsWith(MedianRampPrefix);
        }

        // The ramp road with 'lanes' lanes (2 or 4), once the city is loaded; null if none.
        public static NetInfo RampRoad(int lanes)
        {
            NetInfo info;
            lock (variants) return ramps.TryGetValue(lanes, out info) ? info : null;
        }

        // --- flattening a model's middle -----------------------------------------

        // A median ramp road's ground road model: everything 'cut' or more from
        // the centre line (each carriageway's outer lanes, kerb, sidewalk and
        // skirt) moved 'widen' further out, unchanged; the middle stretched to
        // fill the wider gap and brought down to the road's surface (flat
        // paving under the ramp, no median kerbs).
        // 'kerb' >= 0: the carriageway (to the kerb) moves, the sidewalk (kerb to
        // 'edge') takes it up and the edge and past it stay (see Across).
        internal static Mesh Widen(Mesh mesh, float cut, float widen, float surface, float kerb = -1f, float edge = 0f)
        {
            var sliced = SliceAcross(mesh, kerb >= 0f ? new[] { -cut, cut, -kerb, kerb, -edge, edge } : new[] { -cut, cut });
            if (sliced == null) return null;
            var vertices = sliced.Vertices;
            var triangles = sliced.Triangles;
            float stretch = (cut + widen) / cut;
            for (int i = 0; i < vertices.Length; i++)
            {
                float x = vertices[i].x, a = Mathf.Abs(x), y = vertices[i].y;
                float side = x < 0f ? -1f : 1f;
                if (a >= cut - 0.01f) a = kerb >= 0f ? Across(a, cut, widen, kerb, edge) : a + widen;
                else
                {
                    a *= stretch;
                    y = Mathf.Min(y, surface);
                }
                vertices[i] = new Vector3(side * a, y, vertices[i].z);
            }
            return sliced.Make(mesh, " (median)");
        }

        private const float MinSidewalk = 2f;   // a sidewalk the carriageway moves into keeps at least this

        // Where something 'a' from the centre goes: from the gap's edge to the
        // kerb moved by 'widen'; the sidewalk (kerb to edge) squeezed (or
        // stretched) to fit; the edge and past it unchanged.
        internal static float Across(float a, float cut, float widen, float kerb, float edge)
        {
            if (a <= kerb + 0.01f) return a + widen;
            if (a >= edge) return a;
            return kerb + widen + (a - kerb) * (edge - kerb - widen) / (edge - kerb);
        }

        // A model's parts, its triangles cut along lines across the road
        // (x = each of 'xs') so no triangle spans one: a carriageway drawn as
        // one strip, its lane markings in the texture, can then be moved apart
        // at a lane's edge without stretching the markings. The new corners
        // take everything (texture places, normals, colours) in proportion.
        internal class Sliced
        {
            public Vector3[] Vertices;
            public int[] Triangles;
            public Vector3[] Normals;
            public Vector4[] Tangents;
            public Vector2[] Uv, Uv2;
            public Color[] Colors;

            public Mesh Make(Mesh from, string suffix)
            {
                var result = new Mesh();
                result.name = from.name + suffix;
                result.vertices = Vertices;
                if (Normals != null) result.normals = Normals;
                if (Tangents != null) result.tangents = Tangents;
                if (Uv != null) result.uv = Uv;
                if (Uv2 != null) result.uv2 = Uv2;
                if (Colors != null) result.colors = Colors;
                result.triangles = Triangles;
                result.bounds = from.bounds;   // the game bends the model along the road: its bounds stay the road's
                lock (madeMeshes) madeMeshes.Add(result);
                return result;
            }
        }

        internal static Sliced SliceAcross(Mesh mesh, float[] xs)
        {
            if (mesh == null) return null;
            var v = new List<Vector3>(mesh.vertices ?? new Vector3[0]);
            var tri = mesh.triangles;
            int count = v.Count;
            if (count == 0 || tri == null || tri.Length == 0) return null;
            var n = Attribute(mesh.normals, count);
            var t = Attribute(mesh.tangents, count);
            var uv = Attribute(mesh.uv, count);
            var uv2 = Attribute(mesh.uv2, count);
            var c = Attribute(mesh.colors, count);
            var made = new Dictionary<long, int>();   // the corner on an edge at a line, by its ends and the line
            System.Func<int, int, int, int> between = (a, b, line) =>
            {
                int lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
                long key = ((long)lo * 1000003L + hi) * 16L + line;
                int index;
                if (made.TryGetValue(key, out index)) return index;
                float f = (xs[line] - v[lo].x) / (v[hi].x - v[lo].x);
                index = v.Count;
                v.Add(Vector3.Lerp(v[lo], v[hi], f));
                if (n != null) n.Add(Vector3.Lerp(n[lo], n[hi], f).normalized);
                if (t != null) t.Add(Vector4.Lerp(t[lo], t[hi], f));
                if (uv != null) uv.Add(Vector2.Lerp(uv[lo], uv[hi], f));
                if (uv2 != null) uv2.Add(Vector2.Lerp(uv2[lo], uv2[hi], f));
                if (c != null) c.Add(Color.Lerp(c[lo], c[hi], f));
                made[key] = index;
                return index;
            };
            var polygons = new List<List<int>>();
            for (int i = 0; i + 2 < tri.Length; i += 3) polygons.Add(new List<int> { tri[i], tri[i + 1], tri[i + 2] });
            for (int line = 0; line < xs.Length; line++)
            {
                float x = xs[line];
                var next = new List<List<int>>();
                foreach (var polygon in polygons)
                {
                    bool below = false, above = false;
                    foreach (int i in polygon)
                    {
                        if (v[i].x < x - 0.001f) below = true;
                        if (v[i].x > x + 0.001f) above = true;
                    }
                    if (!below || !above)
                    {
                        next.Add(polygon);
                        continue;
                    }
                    // Both sides: the part on each, in the same winding.
                    var left = new List<int>();
                    var right = new List<int>();
                    for (int k = 0; k < polygon.Count; k++)
                    {
                        int a = polygon[k], b = polygon[(k + 1) % polygon.Count];
                        float da = v[a].x - x, db = v[b].x - x;
                        if (da <= 0.001f) left.Add(a);
                        if (da >= -0.001f) right.Add(a);
                        if ((da < -0.001f && db > 0.001f) || (da > 0.001f && db < -0.001f))
                        {
                            int m = between(a, b, line);
                            left.Add(m);
                            right.Add(m);
                        }
                    }
                    if (left.Count >= 3) next.Add(left);
                    if (right.Count >= 3) next.Add(right);
                }
                polygons = next;
            }
            var triangles = new List<int>();
            foreach (var polygon in polygons)
                for (int k = 1; k + 1 < polygon.Count; k++)
                {
                    triangles.Add(polygon[0]);
                    triangles.Add(polygon[k]);
                    triangles.Add(polygon[k + 1]);
                }
            return new Sliced
            {
                Vertices = v.ToArray(), Triangles = triangles.ToArray(), Normals = n != null ? n.ToArray() : null,
                Tangents = t != null ? t.ToArray() : null, Uv = uv != null ? uv.ToArray() : null,
                Uv2 = uv2 != null ? uv2.ToArray() : null, Colors = c != null ? c.ToArray() : null,
            };
        }

        private static List<T> Attribute<T>(T[] values, int count)
        {
            return values != null && values.Length == count ? new List<T>(values) : null;
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
            var everyRamp = new List<NetInfo>();
            lock (variants)
                foreach (var rampName in allRampNames)
                {
                    var info = PrefabCollection<NetInfo>.FindLoaded(rampName);
                    if (info != null && !everyRamp.Contains(info)) everyRamp.Add(info);
                }
            if (pillar != null)
                foreach (var ramp in everyRamp)
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
            if (variants.Count > 0) Mod.Log("Median ramp roads: " + found + " ground road(s) and " + ramps.Count + " ramp(s) ready");
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
                foreach (var variant in list)
                    if (variant.Info != null && variant.Inner == inner) return variant.Info;
                return null;
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
