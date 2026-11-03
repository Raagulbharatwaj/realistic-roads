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
        // The pieces at the node where a median ramp leaves (or lands in) the
        // road: the full road's last stretch and the
        // ground road's first, copies that draw nothing at a junction, so
        // there only the three roads' own surfaces meet (no crosswalks, kerbs
        // or junction corners).
        public const string StartPrefix = "RR Median Start ";
        public const float StartLength = 40f;
        public const float MedianGap2 = 4.5f;   // half the gap for (and the width of) a 2-lane median ramp: 9 m
        public const float MedianGap4 = 7.5f;   // and a 4-lane one: 15 m

        private class Variant
        {
            public string Name;
            public int Inner;     // inner lanes cut out on each side
            public float Cut;     // the gap: this far either side of the centre
            public bool Start;    // the start piece (no junction models) of that ground road
            public string Tag;    // its name's last word: the inner lanes ("1", "2"), or a median cut's ("Cut12")
            public bool Open;     // a median cut's: the gap left open (no paving; the ground shows, the cut is dug in it)
            public NetInfo Info;  // once loaded
        }

        // A median cut's ground roads: a gap as wide as the track in it (and its
        // open cut), left open. By road: its inner lanes cut out, the gap's half.
        public const string CutTag = "Cut";
        private static readonly KeyValuePair<int, float>[] LargeCuts = { new KeyValuePair<int, float>(1, 6f), new KeyValuePair<int, float>(2, 7.5f), new KeyValuePair<int, float>(2, 9f) };
        private static readonly KeyValuePair<int, float>[] MediumCuts = { new KeyValuePair<int, float>(1, 6f) };

        private static string CutName(float gapHalf) => CutTag + Mathf.RoundToInt(2f * gapHalf);
        private const string PavedTag = "P";   // a median cut road's paved twin: its tag + this

        // A tag as the tapers know it: a paved twin's as its open road's.
        private static string TaperTag(string tag) =>
            tag != null && tag.StartsWith(CutTag) && tag.EndsWith(PavedTag) ? tag.Substring(0, tag.Length - PavedTag.Length) : tag;

        // A median cut road's (or its start piece's) paved twin; null for anything else.
        public static NetInfo PavedTwinOf(NetInfo open)
        {
            if (open == null) return null;
            lock (variants)
                foreach (var pair in variants)
                {
                    var match = pair.Value.Find(v => v.Info == open);
                    if (match == null) continue;
                    if (!match.Open) return null;
                    var twin = pair.Value.Find(v => v.Start == match.Start && v.Tag == match.Tag + PavedTag);
                    return twin != null ? twin.Info : null;
                }
            return null;
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
            // Already added to this collection (it woke twice): not again.
            foreach (var prefab in prefabs)
                if (prefab != null && prefab.name != null &&
                    (prefab.name.StartsWith(MedianPrefix) || prefab.name.StartsWith(MedianRampPrefix) || prefab.name.StartsWith(StartPrefix))) return;
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
                        var copy = Make(road, inner, inner == 1 ? MedianGap2 : MedianGap4, false);
                        if (copy != null) made.Add(copy);
                        var start = Make(road, inner, inner == 1 ? MedianGap2 : MedianGap4, true);
                        if (start != null) made.Add(start);
                    }
                    catch (Exception e)
                    {
                        Mod.LogError("Making the median ground road from " + road.name + " (" + inner + " inner lane(s)) failed", e);
                    }
                }
                // The median cuts' ground roads (an open gap for a metro or train track).
                var cuts = road.name == "Large Road" ? LargeCuts : road.name == "Medium Road" ? MediumCuts : new KeyValuePair<int, float>[0];
                foreach (var cut in cuts)
                    try
                    {
                        string tag = CutName(cut.Value);
                        var copy = Make(road, cut.Key, cut.Value, false, true, tag);
                        if (copy != null) made.Add(copy);
                        var start = Make(road, cut.Key, cut.Value, true, true, tag);
                        if (start != null) made.Add(start);
                        // Its paved twin: the same lanes, the gap paved (where the
                        // track is on pillars over the road, or in its tunnel under it).
                        var paved = Make(road, cut.Key, cut.Value, false, false, tag + PavedTag);
                        if (paved != null) made.Add(paved);
                        var pavedStart = Make(road, cut.Key, cut.Value, true, false, tag + PavedTag);
                        if (pavedStart != null) made.Add(pavedStart);
                    }
                    catch (Exception e)
                    {
                        Mod.LogError("Making the median cut road from " + road.name + " (" + CutName(cut.Value) + ") failed", e);
                    }
            }
            // The full roads' start pieces.
            foreach (var road in prefabs)
            {
                if (road == null || (road.name != "Large Road" && road.name != "Medium Road")) continue;
                try
                {
                    var start = MakeFullStart(road);
                    if (start != null) made.Add(start);
                }
                catch (Exception e)
                {
                    Mod.LogError("Making the median ramp start piece of " + road.name + " failed", e);
                }
                // Its tapered start pieces: one for each ground road (its lanes
                // slide out to that ground road's), each way along the segment.
                int[] tapered = road.name == "Large Road" ? new[] { 1, 2 } : road.name == "Medium Road" ? new[] { 1 } : new int[0];
                foreach (int inner in tapered)
                    foreach (bool atEnd in new[] { false, true })
                        foreach (bool invert in new[] { false, true })
                            try
                            {
                                var taper = MakeTaper(road, inner, inner == 1 ? MedianGap2 : MedianGap4, atEnd, invert, inner.ToString());
                                if (taper != null) made.Add(taper);
                            }
                            catch (Exception e)
                            {
                                Mod.LogError("Making the median ramp taper of " + road.name + " (" + inner + ") failed", e);
                            }
                // And for each median cut's ground road (its lanes slide out to the open gap).
                var cutsHere = road.name == "Large Road" ? LargeCuts : road.name == "Medium Road" ? MediumCuts : new KeyValuePair<int, float>[0];
                foreach (var cut in cutsHere)
                    foreach (bool atEnd in new[] { false, true })
                        foreach (bool invert in new[] { false, true })
                            try
                            {
                                var taper = MakeTaper(road, cut.Key, cut.Value, atEnd, invert, CutName(cut.Value));
                                if (taper != null) made.Add(taper);
                            }
                            catch (Exception e)
                            {
                                Mod.LogError("Making the median cut taper of " + road.name + " (" + CutName(cut.Value) + ") failed", e);
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
            if (road.name != null && (road.name.StartsWith(MedianPrefix) || road.name.StartsWith(StartPrefix))) return false;
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
        // 'start': its start piece (see StartPrefix).
        private static NetInfo Make(NetInfo road, int inner, float gapHalf, bool start, bool open = false, string tag = null)
        {
            float cut = CutFor(road, inner);
            if (cut <= 0f || gapHalf <= 0f) return null;
            float widen = gapHalf - cut;
            tag = tag ?? inner.ToString();
            string name = (start ? StartPrefix + "Ground " : MedianPrefix) + road.name + " " + tag;
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
            // The middle of the innermost lane: plain road surface (its markings are
            // at its edges), what the gap is drawn with.
            float plain = cut;
            foreach (var lane in road.m_lanes)
                if (lane != null && (lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None)
                    plain = Mathf.Min(plain, Mathf.Abs(lane.m_position));
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
                    var mesh = Widen(segment.m_mesh, cut, widen, surface, inside ? kerb : -1f, edge, plain, open);
                    if (mesh != null) segment.m_mesh = mesh;
                    var lod = Widen(segment.m_lodMesh, cut, widen, surface, inside ? kerb : -1f, edge, plain, open);
                    if (lod != null) segment.m_lodMesh = lod;
                }
            if (copy.m_nodes != null)
                foreach (var node in copy.m_nodes)
                {
                    if (node == null) continue;
                    var mesh = Widen(node.m_mesh, cut, widen, surface, inside ? kerb : -1f, edge, plain, open);
                    if (mesh != null) node.m_mesh = mesh;
                    var lod = Widen(node.m_lodMesh, cut, widen, surface, inside ? kerb : -1f, edge, plain, open);
                    if (lod != null) node.m_lodMesh = lod;
                }
            // A median cut's gap is open: no paving there, so the track's open cut
            // (dug and walled by its own slope piece) shows in it. The road still
            // hides the ground under itself (m_clipTerrain as the road's): the
            // ground is raised to the sidewalk's level under a road, so shown it
            // would cover the carriageways, which are lower (the grey road).
            if (start) SetUpStartPiece(copy);
            HideFromMenus(copy);
            bool cutRoad = tag.StartsWith(CutTag);
            Name(copy, Title(road) + " (" + (VehicleLanes(copy, -1) + VehicleLanes(copy, 1)) + " ground lanes beside a median " + (cutRoad ? "cut" : "ramp") +
                       (start ? ", where it starts" : "") + ")",
                "Built by Realistic Roads beside a ramp or track in the road's middle.");
            lock (variants)
            {
                List<Variant> list;
                if (!variants.TryGetValue(road.name, out list)) variants[road.name] = list = new List<Variant>();
                list.RemoveAll(v => v.Name == name);
                list.Add(new Variant { Name = name, Inner = inner, Cut = cut + widen, Start = start, Tag = tag, Open = open });
                list.Sort((a, b) => a.Cut.CompareTo(b.Cut));
            }
            Mod.Log("Median ramp roads: " + name + " from " + road.name + " (" + (2f * road.m_halfWidth).ToString("F1") + " m, inner lane edge " +
                        cut.ToString("F1") + " m): " + (VehicleLanes(copy, -1) + VehicleLanes(copy, 1)) + " ground lanes, halves moved " +
                        widen.ToString("+0.0;-0.0") + " m" + (inside ? " into its sidewalks (now " + copy.m_pavementWidth.ToString("F1") + " m)" : " (the road wider)") +
                        ", a " + (2f * (cut + widen)).ToString("F1") + " m " + (open ? "open " : "") + "gap in the middle, " + (2f * copy.m_halfWidth).ToString("F1") + " m wide");
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
        // A start piece: no node models at all, corners at the node itself (no corner
        // offset; the game itself squares the ends of the roads meeting there),
        // segment ends not clipped, junctions flat. Their ground settings stay
        // the road's (they flatten and clip the ground, like the Six-Lane Road).
        private static void SetUpStartPiece(NetInfo piece)
        {
            piece.m_nodes = new NetInfo.Node[0];
            piece.m_minCornerOffset = 0f;
            piece.m_maxCornerOffset = 0f;
            piece.m_clipSegmentEnds = false;
            piece.m_flatJunctions = true;
            Mod.Log("Median ramp roads: " + piece.name + ": start piece (no node models, corner offset 0, ends not clipped)");
        }

        // A ramp set up on the ground so it never raises or clips the ground,
        // only lowers it (it runs in the ground road's gap, which already shapes
        // the ground there).
        private static void SetUpRampGround(NetInfo ramp)
        {
            ramp.m_flattenTerrain = false;
            ramp.m_lowerTerrain = true;
            ramp.m_clipTerrain = false;
        }

        // The ramp's start piece: only its segment at the node where it leaves
        // (or lands in) the road is this (NetworkBuilder.MedianTransition). No
        // junction pieces there and its corners at the node. The rest of the
        // ramp keeps the road's own junction pieces and corners, so its far end
        // joins a deck or road like any road does.
        private static void SetUpRampStart(NetInfo ramp)
        {
            NoJunctionPieces(ramp);
            ramp.m_minCornerOffset = 0f;
            ramp.m_maxCornerOffset = 0f;
        }

        private static void NoJunctionPieces(NetInfo ramp)
        {
            if (ramp.m_nodes == null) return;
            if (!Mod.HideRampJunctions)
            {
                Mod.Log("Median ramps: " + ramp.name + ": junction models kept (test setting)");
                return;
            }
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
        private const string RampStartTag = " Start";
        private static readonly Dictionary<string, string> rampStartNames = new Dictionary<string, string>();   // ramp -> its start piece, by name
        private static readonly Dictionary<NetInfo, NetInfo> rampStarts = new Dictionary<NetInfo, NetInfo>();

        // A ramp's ground version's raised versions: its elevated one only.
        private static void LinkVersions(NetInfo ground, NetInfo elevated)
        {
            var ai = ground.GetComponent<NetAI>();
            foreach (var field in new[] { "m_elevatedInfo", "m_bridgeInfo", "m_slopeInfo", "m_tunnelInfo" })
            {
                var f = ai != null ? ai.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance) : null;
                if (f != null && f.FieldType == typeof(NetInfo)) f.SetValue(ai, field == "m_elevatedInfo" || field == "m_bridgeInfo" ? elevated : null);
            }
        }

        // The start piece of a ramp (the same version: ground or elevated); the
        // piece itself if it is one; null if none.
        public static NetInfo RampStartFor(NetInfo ramp)
        {
            if (ramp == null) return null;
            lock (variants)
            {
                NetInfo start;
                if (rampStarts.TryGetValue(ramp, out start)) return start;
                foreach (var made in rampStarts.Values) if (made == ramp) return ramp;
            }
            return null;
        }

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
            // Its start pieces (the segment at the ramp's node only).
            var startElevated = Narrow(elevated, MedianRampPrefix + lanes + RampStartTag + " Elevated", half);
            var start = Narrow(road, MedianRampPrefix + lanes + RampStartTag, half);
            if (start == null || startElevated == null) return;
            SetUpRampStart(startElevated);
            SetUpRampStart(start);
            // The ground version's raised versions: the narrow elevated one (no bridge
            // trusses, no slope or tunnel: a ramp never goes below the ground).
            LinkVersions(narrow, narrowElevated);
            LinkVersions(start, startElevated);
            string title = lanes + "-lane median ramp";
            Name(narrow, title, "A ramp rising out of a road's middle, built by Realistic Roads.");
            Name(narrowElevated, title + " (elevated)", "A ramp rising out of a road's middle, built by Realistic Roads.");
            Name(start, title + " (start)", "A ramp rising out of a road's middle, built by Realistic Roads.");
            Name(startElevated, title + " (start, elevated)", "A ramp rising out of a road's middle, built by Realistic Roads.");
            made.Add(narrowElevated);
            made.Add(narrow);
            made.Add(startElevated);
            made.Add(start);
            lock (variants)
            {
                rampNames[lanes] = narrow.name;
                allRampNames.Add(narrow.name);
                allRampNames.Add(start.name);
                rampStartNames[narrow.name] = start.name;
                rampStartNames[narrowElevated.name] = startElevated.name;
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
            SetUpRampGround(copy);
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
        // Any piece RR makes for its median ramp roads and median cuts (by name).
        public static bool IsOwnPiece(PrefabInfo info)
        {
            if (info == null || info.name == null) return false;
            string name = info.name;
            return name.StartsWith(MedianPrefix) || name.StartsWith(MedianRampPrefix) || name.StartsWith(StartPrefix);
        }

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
        // 'plain' > 0: the gap is drawn with the road surface found that far
        // from the centre (the innermost lane's middle): no lane markings, no
        // median under the ramp, only paving.
        internal static Mesh Widen(Mesh mesh, float cut, float widen, float surface, float kerb = -1f, float edge = 0f, float plain = 0f, bool open = false)
        {
            var lines = new List<float> { -cut, cut };
            if (kerb >= 0f) lines.AddRange(new[] { -kerb, kerb, -edge, edge });
            if (plain > 0.05f && plain < cut - 0.05f) lines.AddRange(new[] { -plain, plain });
            var sliced = SliceAcross(mesh, lines.ToArray());
            if (sliced == null) return null;
            if (open) DropGap(sliced, cut);
            else if (plain > 0.05f && plain < cut - 0.05f && sliced.Uv != null) PlainGap(sliced, cut, plain);
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

        // A median cut's gap: every triangle within 'cut' of the centre goes (the
        // model was cut along it), so nothing is drawn there.
        private static void DropGap(Sliced sliced, float cut)
        {
            var v = sliced.Vertices;
            var kept = new List<int>();
            var t = sliced.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                bool inside = Mathf.Abs(v[t[i]].x) < cut - 0.01f || Mathf.Abs(v[t[i + 1]].x) < cut - 0.01f || Mathf.Abs(v[t[i + 2]].x) < cut - 0.01f;
                bool outside = Mathf.Abs(v[t[i]].x) > cut + 0.01f || Mathf.Abs(v[t[i + 1]].x) > cut + 0.01f || Mathf.Abs(v[t[i + 2]].x) > cut + 0.01f;
                if (inside && !outside) continue;
                kept.Add(t[i]); kept.Add(t[i + 1]); kept.Add(t[i + 2]);
            }
            sliced.Triangles = kept.ToArray();
        }

        // Every corner in the gap (within 'cut') takes the texture place of the
        // corner at +'plain' nearest along the road: the gap shows that one line
        // of road surface all across (one side's, so nothing between the two
        // sides, like the centre line, is drawn in it).
        private static void PlainGap(Sliced sliced, float cut, float plain)
        {
            var v = sliced.Vertices;
            var refs = new List<int>();
            for (int i = 0; i < v.Length; i++)
                if (Mathf.Abs(v[i].x - plain) < 0.02f) refs.Add(i);
            if (refs.Count == 0) return;
            var uv = (Vector2[])sliced.Uv.Clone();
            for (int i = 0; i < v.Length; i++)
            {
                if (Mathf.Abs(v[i].x) >= cut - 0.01f) continue;
                int best = -1;
                float bestScore = float.MaxValue;
                foreach (int r in refs)
                {
                    float score = Mathf.Abs(v[r].z - v[i].z);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = r;
                }
                if (best >= 0) uv[i] = sliced.Uv[best];
            }
            sliced.Uv = uv;
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

        // A model cut across its length into 'pieces' rows (lines of constant z):
        // SliceAcross on the model turned so z is across (x and z swapped, and
        // back). Null if it can't be read.
        internal static Mesh SliceAlong(Mesh mesh, int pieces)
        {
            if (mesh == null || pieces < 2) return null;
            var vertices = mesh.vertices;
            if (vertices == null || vertices.Length == 0) return null;
            float zMin = float.MaxValue, zMax = float.MinValue;
            foreach (var v in vertices)
            {
                zMin = Mathf.Min(zMin, v.z);
                zMax = Mathf.Max(zMax, v.z);
            }
            if (zMax - zMin < 0.01f) return null;
            var lines = new float[pieces - 1];
            for (int k = 1; k < pieces; k++) lines[k - 1] = zMin + (zMax - zMin) * k / pieces;
            var turned = new Mesh();
            var swapped = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) swapped[i] = new Vector3(vertices[i].z, vertices[i].y, vertices[i].x);
            turned.vertices = swapped;
            var normals = mesh.normals;
            if (normals != null && normals.Length == vertices.Length)
            {
                var n = new Vector3[normals.Length];
                for (int i = 0; i < n.Length; i++) n[i] = new Vector3(normals[i].z, normals[i].y, normals[i].x);
                turned.normals = n;
            }
            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length == vertices.Length)
            {
                var t = new Vector4[tangents.Length];
                for (int i = 0; i < t.Length; i++) t[i] = new Vector4(tangents[i].z, tangents[i].y, tangents[i].x, tangents[i].w);
                turned.tangents = t;
            }
            turned.uv = mesh.uv;
            turned.uv2 = mesh.uv2;
            turned.colors = mesh.colors;
            turned.triangles = mesh.triangles;
            var sliced = SliceAcross(turned, lines);
            UnityEngine.Object.Destroy(turned);
            if (sliced == null) return null;
            for (int i = 0; i < sliced.Vertices.Length; i++)
                sliced.Vertices[i] = new Vector3(sliced.Vertices[i].z, sliced.Vertices[i].y, sliced.Vertices[i].x);
            if (sliced.Normals != null)
                for (int i = 0; i < sliced.Normals.Length; i++)
                    sliced.Normals[i] = new Vector3(sliced.Normals[i].z, sliced.Normals[i].y, sliced.Normals[i].x);
            if (sliced.Tangents != null)
                for (int i = 0; i < sliced.Tangents.Length; i++)
                    sliced.Tangents[i] = new Vector4(sliced.Tangents[i].z, sliced.Tangents[i].y, sliced.Tangents[i].x, sliced.Tangents[i].w);
            return sliced.Make(mesh, " (rows)");
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
                fullStarts.Clear();
                foreach (var pair in fullStartNames)
                {
                    var info = PrefabCollection<NetInfo>.FindLoaded(pair.Value);
                    if (info != null) fullStarts[pair.Key] = info;
                }
                ramps.Clear();
                foreach (var pair in rampNames)
                {
                    var info = PrefabCollection<NetInfo>.FindLoaded(pair.Value);
                    if (info != null) ramps[pair.Key] = info;
                }
                tapers.Clear();
                foreach (var pair in taperNames)
                {
                    var info = PrefabCollection<NetInfo>.FindLoaded(pair.Value);
                    if (info != null) tapers[pair.Key] = info;
                }
                rampStarts.Clear();
                foreach (var pair in rampStartNames)
                {
                    var ramp = PrefabCollection<NetInfo>.FindLoaded(pair.Key);
                    var start = PrefabCollection<NetInfo>.FindLoaded(pair.Value);
                    if (ramp != null && start != null) rampStarts[ramp] = start;
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
            if (variants.Count > 0) Mod.Log("Median ramp roads: " + found + " ground road(s) and " + ramps.Count + " ramp(s) ready (" + rampStarts.Count + " ramp start piece(s), " +
                                            tapers.Count + " taper(s))");
        }

        // Main thread, the city unloading: its copies go (made again for the next).
        public static void Clear()
        {
            active = false;
            lock (variants)
            {
                splitInfos.Clear();
                ramps.Clear();
                rampStarts.Clear();
                tapers.Clear();
                fullStarts.Clear();
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
            if (IsFullStart(info))
            {
                string roadName;
                lock (variants) if (!taperRoads.TryGetValue(info.name, out roadName)) roadName = info.name.Substring(StartPrefix.Length);
                return PrefabCollection<NetInfo>.FindLoaded(roadName) ?? info;
            }
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
                    if (variant.Info != null && !variant.Start && !variant.Open && variant.Inner == inner &&
                        (variant.Tag == null || !variant.Tag.StartsWith(CutTag))) return variant.Info;
                return null;
            }
        }

        // A median cut's ground road for 'road': the narrowest whose open gap is at
        // least 'half' either side of the centre (the track and its cut); null if
        // none is that wide. 'gapHalf': its gap's half.
        public static NetInfo CutRoadFor(NetInfo road, float half, out float gapHalf)
        {
            gapHalf = 0f;
            if (road == null) return null;
            lock (variants)
            {
                List<Variant> list;
                if (!variants.TryGetValue(road.name, out list)) return null;
                Variant best = null;
                foreach (var variant in list)
                    if (variant.Info != null && !variant.Start && variant.Open && variant.Cut >= half - 0.01f && (best == null || variant.Cut < best.Cut)) best = variant;
                if (best == null) return null;
                gapHalf = best.Cut;
                return best.Info;
            }
        }

        // The widest median cut gap 'road' has (its half), 0 if none.
        public static float WidestCut(NetInfo road)
        {
            if (road == null) return 0f;
            lock (variants)
            {
                List<Variant> list;
                if (!variants.TryGetValue(road.name, out list)) return 0f;
                float widest = 0f;
                foreach (var variant in list) if (variant.Info != null && variant.Open) widest = Mathf.Max(widest, variant.Cut);
                return widest;
            }
        }

        // The start piece of a ground road (For's), and of a full road (Large or
        // Medium Road, the one the median ramp road is for); null if none.
        public static NetInfo StartFor(NetInfo ground)
        {
            if (ground == null) return null;
            lock (variants)
                foreach (var pair in variants)
                {
                    var match = pair.Value.Find(v => v.Info == ground);
                    if (match == null) continue;
                    var start = pair.Value.Find(v => v.Start && v.Tag == match.Tag);
                    return start != null ? start.Info : null;
                }
            return null;
        }

        public static NetInfo FullStartFor(NetInfo road)
        {
            NetInfo info;
            lock (variants) return road != null && fullStarts.TryGetValue(road.name, out info) ? info : null;
        }

        public static bool IsFullStart(NetInfo info)
        {
            return info != null && info.name != null && info.name.StartsWith(StartPrefix) && !info.name.StartsWith(StartPrefix + "Ground ");
        }

        private static readonly Dictionary<string, string> fullStartNames = new Dictionary<string, string>();   // by the road's name
        private static readonly Dictionary<string, NetInfo> fullStarts = new Dictionary<string, NetInfo>();

        // --- the full road's taper before a median ramp's node --------------------
        // The full road's start piece (StartLength by the node) with its outer
        // lanes, their markings, the kerb and the sidewalk sliding out (or in) along
        // it to where the ground road has them, so at the node they meet the
        // ground road's edge to edge instead of jumping across there. Its inner
        // lanes (those going up the ramp) stay. Its lanes for traffic stay the
        // road's (vehicles move over at the node); its sidewalk lanes (and their
        // lights) are where the ground road has them, inside the sidewalk all along.
        // 'atEnd': the full shift at the segment's end node (else its start);
        // 'invert': for a segment with the Invert flag (the game may draw some
        // models turned round then: each model is slanted for how it is drawn).
        private static readonly Dictionary<string, string> taperNames = new Dictionary<string, string>();   // by TaperKey
        private static readonly Dictionary<string, NetInfo> tapers = new Dictionary<string, NetInfo>();
        private static readonly Dictionary<string, string> taperRoads = new Dictionary<string, string>();   // taper's name -> its road's
        private const float TaperMarking = 0.25f;   // the lane's edge (its marking) kept out of the stretched strip

        private static string TaperKey(string road, string tag, bool atEnd, bool invert) => road + "|" + tag + "|" + atEnd + "|" + invert;

        private static NetInfo MakeTaper(NetInfo road, int inner, float gapHalf, bool atEnd, bool invert, string tag)
        {
            float cut = CutFor(road, inner);
            if (cut <= 0f) return null;
            float widen = gapHalf - cut;
            float edge = road.m_halfWidth, kerb = road.m_halfWidth - road.m_pavementWidth;
            bool inside = kerb > cut + 0.5f && edge - (kerb + widen) >= MinSidewalk;
            if (!inside || Mathf.Abs(widen) < 0.05f) return null;   // the carriageway moves within the sidewalk (as the ground road)
            // The strip that stretches: the first ground lane's plain middle.
            float laneOuter = float.MaxValue, median = float.MaxValue;
            foreach (var lane in road.m_lanes)
                if (lane != null && (lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None)
                {
                    median = Mathf.Min(median, Mathf.Abs(lane.m_position) - lane.m_width * 0.5f);
                    if (Mathf.Abs(lane.m_position) - lane.m_width * 0.5f >= cut - 0.01f)
                        laneOuter = Mathf.Min(laneOuter, Mathf.Abs(lane.m_position) + lane.m_width * 0.5f);
                }
            if (laneOuter == float.MaxValue) return null;
            // A median (the Four-Lane Road's) closes up: the inner lanes slide in
            // onto the ramp's lanes, the median under them.
            median = median > 0.05f && median < cut ? median : 0f;
            float innerSlide = -median;
            float from = cut + TaperMarking, to = laneOuter - TaperMarking;
            if (to - from < 0.5f || (to + widen) - (from + innerSlide) < 0.5f) return null;
            string name = StartPrefix + road.name + " Taper " + tag + (atEnd ? " End" : " Start") + (invert ? " Inverted" : "");
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
            var keptLanes = new List<NetInfo.Lane>();
            foreach (var lane in copy.m_lanes)
            {
                if (lane == null) continue;
                bool vehicle = (lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None;
                float a = Mathf.Abs(lane.m_position);
                // The median's own lanes (its lights and trees) go: it closes up.
                if (!vehicle && a < cut - 0.01f) continue;
                if (!vehicle && a >= kerb - 0.01f)
                {
                    float moved = Across(a, cut, widen, kerb, edge);
                    lane.m_position = lane.m_position < 0f ? -moved : moved;
                }
                keptLanes.Add(lane);
            }
            copy.m_lanes = keptLanes.ToArray();
            var flags = NetSegment.Flags.Created | (invert ? NetSegment.Flags.Invert : NetSegment.Flags.None);
            int turned = 0, models = 0;
            if (copy.m_segments != null)
                foreach (var segment in copy.m_segments)
                {
                    if (segment == null) continue;
                    models++;
                    bool turnAround = TurnsAround(segment, flags);
                    if (turnAround) turned++;
                    bool atMaxZ = atEnd != turnAround;   // the model's +z towards the segment's end node
                    var mesh = TaperMesh(segment.m_mesh, cut, widen, kerb, edge, median, innerSlide, from, to, atMaxZ);
                    if (mesh != null) segment.m_mesh = mesh;
                    var lod = TaperMesh(segment.m_lodMesh, cut, widen, kerb, edge, median, innerSlide, from, to, atMaxZ);
                    if (lod != null) segment.m_lodMesh = lod;
                }
            SetUpStartPiece(copy);
            HideFromMenus(copy);
            Name(copy, Title(road) + " (widening where a median ramp leaves it)", "Built by Realistic Roads where a ramp rises out of the road's middle.");
            lock (variants)
            {
                taperNames[TaperKey(road.name, tag, atEnd, invert)] = name;
                taperRoads[name] = road.name;
            }
            Mod.Log("Median ramp roads: " + name + ": outer lanes, kerb and sidewalk slide " + widen.ToString("+0.0;-0.0") + " m along it" +
                    (median > 0.05f ? ", inner lanes " + innerSlide.ToString("+0.0;-0.0") + " m (the median closes)" : "") + " (stretching " +
                    from.ToString("F2") + "-" + to.ToString("F2") + " m from the centre), " + models + " model(s), " + turned + " drawn turned round");
            return copy;
        }

        // Whether the game draws this model turned round on a segment with 'flags'
        // (its forward flags don't fit, its backward ones do).
        private static bool TurnsAround(NetInfo.Segment segment, NetSegment.Flags flags)
        {
            try
            {
                var type = typeof(NetInfo.Segment);
                long Get(string field)
                {
                    var f = type.GetField(field, BindingFlags.Public | BindingFlags.Instance);
                    return f != null ? Convert.ToInt64(f.GetValue(segment)) : 0L;
                }
                long have = Convert.ToInt64(flags);
                long fr = Get("m_forwardRequired"), ff = Get("m_forwardForbidden"), br = Get("m_backwardRequired"), bf = Get("m_backwardForbidden");
                bool forward = (have & (fr | ff)) == fr;
                bool backward = (have & (br | bf)) == br;
                return !forward && backward;
            }
            catch
            {
                return false;
            }
        }

        // A road model with its outer part slid by 'widen' at one end (none at the
        // other, in proportion between): from 'median' to 'from' slid by 'inner'
        // (0, or a median's width in: it closes up, squeezed to nothing);
        // 'from' to 'to' stretched between the two; past 'to' as the ground road
        // has it (Across). 'atMaxZ': the full slide at the model's +z end.
        private const int TaperRows = 10;   // the model cut into this many rows along it, for the S-curve

        private static Mesh TaperMesh(Mesh mesh, float cut, float widen, float kerb, float edge, float median, float inner, float from, float to, bool atMaxZ)
        {
            var lines = new List<float> { -from, from, -to, to, -kerb, kerb, -edge, edge };
            if (median > 0.05f) lines.AddRange(new[] { -median, median });
            // Rows along it first: the game draws straight between a model's corners,
            // and a road model has few along its length, so the slide's S-curve needs them.
            var rows = SliceAlong(mesh, TaperRows);
            var sliced = SliceAcross(rows ?? mesh, lines.ToArray());
            if (sliced == null) return null;
            var vertices = sliced.Vertices;
            float zMin = float.MaxValue, zMax = float.MinValue;
            foreach (var v in vertices)
            {
                zMin = Mathf.Min(zMin, v.z);
                zMax = Mathf.Max(zMax, v.z);
            }
            if (zMax - zMin < 0.01f) return null;
            for (int i = 0; i < vertices.Length; i++)
            {
                float x = vertices[i].x, a = Mathf.Abs(x);
                float f = (vertices[i].z - zMin) / (zMax - zMin);
                if (!atMaxZ) f = 1f - f;
                // Smoothstep: the slide starts and ends along the road (no kink at
                // either end, where it meets the full road and the ground road).
                f = f * f * (3f - 2f * f);
                float target;
                if (median > 0.05f && a < median) target = a * (median + inner) / median;
                else if (a <= from) target = a + inner;
                else if (a < to) target = from + inner + (a - from) * ((to + widen) - (from + inner)) / (to - from);
                else target = Across(a, cut, widen, kerb, edge);
                a += f * (target - a);
                vertices[i] = new Vector3(x < 0f ? -a : a, vertices[i].y, vertices[i].z);
            }
            return sliced.Make(mesh, " (taper)");
        }

        // The tapered start piece of 'road' for the ground road 'ground' (or its
        // start piece), its full slide at the segment's end node ('atEnd') or start;
        // null if none (that road keeps its plain start piece).
        public static NetInfo FullTaperFor(NetInfo road, NetInfo ground, bool atEnd, bool invert)
        {
            if (road == null || ground == null) return null;
            lock (variants)
                foreach (var pair in variants)
                {
                    var match = pair.Value.Find(v => v.Info == ground);
                    if (match == null) continue;
                    NetInfo taper;
                    return tapers.TryGetValue(TaperKey(road.name, TaperTag(match.Tag), atEnd, invert), out taper) ? taper : null;
                }
            return null;
        }

        // A copy of the full road that draws nothing at a junction (a start piece).
        private static NetInfo MakeFullStart(NetInfo road)
        {
            string name = StartPrefix + road.name;
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
            SetUpStartPiece(copy);
            HideFromMenus(copy);
            Name(copy, Title(road) + " (where a median ramp leaves it)", "Built by Realistic Roads where a ramp rises out of the road's middle.");
            lock (variants) fullStartNames[road.name] = name;
            Mod.Log("Median ramp roads: " + name + " made from " + road.name + " (drawn without junction pieces)");
            return copy;
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
            if (!active || node == 0 || !Mod.SquareRampEnds) return false;
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
