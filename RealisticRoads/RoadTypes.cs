using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RealisticRoads
{
    // R3 road builder, the game part: turns a RoadConfig into CS1 road types
    // (NetInfo prefabs) by cloning the closest vanilla road and its elevated,
    // bridge, slope and tunnel versions, putting in the new lanes and stretching
    // the vanilla meshes to the new width (milestone R3.2: markings still come
    // from the vanilla road; generated meshes are R3.3).
    //
    // When: after a city has loaded (CreateMissing), and straight away for
    // roads made in the panel. Nothing runs while the city loads: hooking the
    // game's road collections then stopped cities from loading. So a saved
    // city's roads of these types are missing on load for now.
    // Main thread only.
    public static class RoadTypes
    {
        private const string CollectionName = "RealisticRoads";
        private static readonly string[] VariantFields = { "m_elevatedInfo", "m_bridgeInfo", "m_slopeInfo", "m_tunnelInfo" };
        private static readonly string[] VariantSuffixes = { " Elevated", " Bridge", " Slope", " Tunnel" };

        // Ground prefab and config by prefab name.
        private static readonly Dictionary<string, NetInfo> created = new Dictionary<string, NetInfo>();
        private static readonly Dictionary<string, RoadConfig> createdConfigs = new Dictionary<string, RoadConfig>();
        private static readonly HashSet<string> reported = new HashSet<string>();
        private static readonly Dictionary<Mesh, Dictionary<string, Mesh>> stretched = new Dictionary<Mesh, Dictionary<string, Mesh>>();

        public static NetInfo Find(RoadConfig config)
        {
            NetInfo info;
            lock (created)
            {
                return created.TryGetValue(config.PrefabName, out info) && info != null ? info : null;
            }
        }

        public static bool IsRoadBuilderRoad(NetInfo info) => info != null && info.name != null && info.name.StartsWith("RR ");

        // The configs of the road builder roads this city has segments of (for
        // the save). Reads the segment buffer only; any thread.
        public static List<RoadConfig> UsedConfigs()
        {
            var ids = new HashSet<string>();
            var segments = NetManager.instance.m_segments.m_buffer;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                var info = segments[i].Info;
                string name = !ReferenceEquals(info, null) ? info.name : null;
                // "RR <id>" or "RR <id> Elevated" and so on.
                if (name == null || !name.StartsWith("RR ")) continue;
                int end = name.IndexOf(' ', 3);
                ids.Add(end > 0 ? name.Substring(3, end - 3) : name.Substring(3));
            }
            var list = new List<RoadConfig>();
            foreach (var id in ids)
            {
                var config = RoadLibrary.Find(id);
                if (config == null)
                    lock (created)
                        foreach (var pair in createdConfigs) if (pair.Value.Id == id) { config = pair.Value; break; }
                if (config != null) list.Add(config);
            }
            return list;
        }

        // A road deleted from the library: no longer remembered as made (its
        // prefab stays registered until the game restarts; nothing uses it).
        public static void Forget(RoadConfig config)
        {
            lock (created)
            {
                created.Remove(config.PrefabName);
                createdConfigs.Remove(config.PrefabName);
            }
        }

        // After a city loads: every library road is registered (main thread).
        // Registering while the city loads (a NetCollection.Awake patch) kept
        // the city from finishing loading next to Loading Screen Mod, so this
        // runs after loading: a saved city's roads of these types are missing
        // until that is solved (see docs/R3_ROAD_BUILDER.md).
        public static void CreateMissing()
        {
            foreach (var config in RoadLibrary.All())
            {
                if (Find(config) != null) continue;
                if (CreateNow(config) != null)
                    Mod.Log("Road builder: '" + config.Name + "' was registered late. If this city had roads of it, " +
                            "don't save: load the city again.");
            }
        }

        // Registers a road straight away (from the panel, or late). Returns the
        // ground road type, or null (the log says why).
        public static NetInfo CreateNow(RoadConfig config)
        {
            var existing = Find(config);
            if (existing != null) return existing;
            var cs = CrossSection.Compose(config);
            if (!cs.Ok)
            {
                ReportOnce(config, "can't be built: " + string.Join("; ", cs.Errors.ToArray()));
                return null;
            }

            NetInfo baseInfo = null;
            foreach (var name in cs.BaseCandidates(true))
            {
                baseInfo = PrefabCollection<NetInfo>.FindLoaded(name);
                if (baseInfo != null) break;
                Mod.Log("Road builder: no vanilla road called '" + name + "', trying the next one");
            }
            if (baseInfo == null)
            {
                ReportOnce(config, "has no vanilla road to start from");
                return null;
            }

            try
            {
                var parent = baseInfo.transform.parent != null ? baseInfo.transform.parent : null;
                var infos = Build(config, cs, baseInfo, Holder(parent));
                Register(infos.ToArray(), baseInfo);
                return Find(config);
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: registering '" + config.Name + "' failed; it is in your library and is made when a city loads", e);
                return null;
            }
        }

        private static void ReportOnce(RoadConfig config, string message)
        {
            if (reported.Add(config.Id + message)) Mod.Log("Road builder: '" + config.Name + "' " + message);
        }

        // An inactive object to keep our prefabs under, so cloning them doesn't
        // wake their scripts. Under the base road's collection, it lives and dies
        // with the vanilla prefabs.
        private static Transform Holder(Transform parent)
        {
            const string name = "RealisticRoads road types";
            if (parent != null)
            {
                var found = parent.Find(name);
                if (found != null) return found;
            }
            var holder = new GameObject(name);
            holder.SetActive(false);
            if (parent != null) holder.transform.parent = parent;
            else UnityEngine.Object.DontDestroyOnLoad(holder);
            return holder.transform;
        }

        private static void Register(NetInfo[] infos, NetInfo baseInfo)
        {
            var type = typeof(PrefabCollection<NetInfo>);
            var initialize = type.GetMethod("InitializePrefabs", BindingFlags.Static | BindingFlags.Public, null,
                new[] { typeof(string), typeof(NetInfo[]), typeof(string[]) }, null);
            var bind = type.GetMethod("BindPrefabs", BindingFlags.Static | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (initialize == null) throw new MissingMethodException("PrefabCollection<NetInfo>.InitializePrefabs");

            int baseIndex = PrefabIndex(baseInfo);
            initialize.Invoke(null, new object[] { CollectionName, infos, null });
            if (bind != null) bind.Invoke(null, null);
            int after = PrefabIndex(baseInfo);
            Mod.Log("Road builder: registered " + infos.Length + " road type(s), " + infos[0].name + " has prefab index " +
                    PrefabIndex(infos[0]));
            if (baseIndex != after)
                Mod.Log("Road builder WARNING: the vanilla roads' prefab indices changed (" + baseIndex + " -> " + after +
                        "). Don't save this city; load it again and send the log.");
        }

        private static int PrefabIndex(NetInfo info)
        {
            var field = AccessTools.Field(info.GetType(), "m_prefabDataIndex");
            return field != null ? (int)field.GetValue(info) : -1;
        }

        // ---- Building the prefabs ----

        private static List<NetInfo> Build(RoadConfig config, CrossSection cs, NetInfo baseInfo, Transform parent)
        {
            var list = new List<NetInfo>();
            var clones = new Dictionary<NetInfo, NetInfo>();
            var ground = Clone(baseInfo, config.PrefabName, parent);
            Shape(ground, baseInfo, baseInfo, cs, config, true);
            clones[baseInfo] = ground;
            list.Add(ground);

            for (int v = 0; v < VariantFields.Length; v++)
            {
                var original = GetVariant(ground, VariantFields[v]);
                if (original == null) continue;
                NetInfo clone;
                if (!clones.TryGetValue(original, out clone))
                {
                    clone = Clone(original, config.PrefabName + VariantSuffixes[v], parent);
                    Shape(clone, original, baseInfo, cs, config, false);
                    clones[original] = clone;
                    list.Add(clone);
                }
            }
            // Point every variant field (on the ground road and on the variants)
            // at our clones instead of the vanilla roads.
            foreach (var info in list)
                foreach (var name in VariantFields)
                {
                    var original = GetVariant(info, name);
                    NetInfo clone;
                    if (original != null && clones.TryGetValue(original, out clone)) SetVariant(info, name, clone);
                }

            AddTitle(ground, config, cs);
            lock (created)
            {
                created[config.PrefabName] = ground;
                createdConfigs[config.PrefabName] = config;
            }
            Mod.Log("Road builder: built '" + config.Name + "' (" + config.PrefabName + ") from " + baseInfo.name + ": " +
                    (cs.HalfWidth * 2f).ToString("0.0") + " m, " + cs.BackwardLanes + " < " + cs.ForwardLanes + " >, " +
                    list.Count + " versions");
            return list;
        }

        private static NetInfo Clone(NetInfo original, string name, Transform parent)
        {
            var go = (GameObject)UnityEngine.Object.Instantiate(original.gameObject, parent);
            go.name = name;
            var info = go.GetComponent<NetInfo>();
            // Instantiate points references inside the copied object at the copy;
            // make sure the AI is the copy's own.
            var ai = go.GetComponent<NetAI>();
            if (ai != null) info.m_netAI = ai;
            return info;
        }

        private static NetInfo GetVariant(NetInfo info, string field)
        {
            if (info == null || info.m_netAI == null) return null;
            var f = AccessTools.Field(info.m_netAI.GetType(), field);
            return f != null ? f.GetValue(info.m_netAI) as NetInfo : null;
        }

        private static void SetVariant(NetInfo info, string field, NetInfo value)
        {
            var f = AccessTools.Field(info.m_netAI.GetType(), field);
            if (f != null) f.SetValue(info.m_netAI, value);
        }

        // Widths, lanes, meshes and costs of one version (ground, elevated, ...).
        // Each version keeps its own difference to the ground road: an elevated
        // road with narrower sidewalks keeps them narrower.
        private static void Shape(NetInfo info, NetInfo original, NetInfo baseGround, CrossSection cs, RoadConfig config, bool isGround)
        {
            float groundPave = baseGround.m_pavementWidth;
            float groundInner = baseGround.m_halfWidth - groundPave;
            float basePave = original.m_pavementWidth;
            float baseInner = original.m_halfWidth - basePave;

            float newPave = cs.PavementWidth > 0f && groundPave > 0.01f ? basePave * cs.PavementWidth / groundPave : basePave;
            float newInner = Mathf.Max(baseInner + (cs.InnerHalfWidth - groundInner), 1f);
            info.m_pavementWidth = newPave;
            info.m_halfWidth = newInner + newPave;

            info.m_lanes = BuildLanes(original, cs, config, isGround, basePave, newPave, original.m_halfWidth, info.m_halfWidth);
            StretchMeshes(info, baseInner, basePave, newInner, newPave);
            ScaleCost(info, original.m_halfWidth > 0.1f ? info.m_halfWidth / original.m_halfWidth : 1f);
        }

        private static NetInfo.Lane[] BuildLanes(NetInfo original, CrossSection cs, RoadConfig config, bool isGround,
                                                 float basePave, float newPave, float baseHalf, float newHalf)
        {
            var source = original.m_lanes ?? new NetInfo.Lane[0];
            var parkingType = ParseEnum<NetInfo.LaneType>("Parking");
            var bicycle = ParseEnum<VehicleInfo.VehicleType>("Bicycle");
            float speed = Mathf.Clamp(config.SpeedKmh, CrossSection.MinSpeedKmh, CrossSection.MaxSpeedKmh) / 50f;
            bool hasParking = false;
            foreach (var lane in source) if ((lane.m_laneType & parkingType) != 0) hasParking = true;

            var lanes = new List<NetInfo.Lane>();
            foreach (var spec in cs.Lanes)
            {
                NetInfo.Lane template;
                NetInfo.Lane lane;
                switch (spec.Kind)
                {
                    case PieceKind.Sidewalk:
                        template = Pick(source, l => (l.m_laneType & NetInfo.LaneType.Pedestrian) != 0, spec.Position, false);
                        if (template == null) continue;
                        lane = CopyLane(template);
                        // Same distance from the road edge as on the vanilla road,
                        // scaled with the sidewalk.
                        float ratio = basePave > 0.01f ? newPave / basePave : 1f;
                        float fromEdge = (baseHalf - Mathf.Abs(template.m_position)) * ratio;
                        lane.m_position = Mathf.Sign(spec.Position) * (newHalf - fromEdge);
                        lane.m_width = template.m_width * ratio;
                        break;

                    case PieceKind.Car:
                    case PieceKind.Bus:
                    {
                        bool bus = spec.Kind == PieceKind.Bus;
                        template = PickTraffic(source, spec, bus);
                        if (template == null) continue;
                        lane = CopyLane(template);
                        if (Dir(template) != Dir(spec)) lane.m_laneProps = null;   // its arrows would point the wrong way
                        lane.m_direction = Dir(spec);
                        if (bus) lane.m_laneType = NetInfo.LaneType.TransportVehicle;
                        else if ((lane.m_laneType & NetInfo.LaneType.TransportVehicle) != 0) lane.m_laneType = NetInfo.LaneType.Vehicle;
                        lane.m_position = spec.Position;
                        lane.m_width = spec.Width;
                        lane.m_speedLimit = speed;
                        break;
                    }

                    case PieceKind.Bike:
                        template = PickTraffic(source, spec, false);
                        if (template == null) continue;
                        lane = CopyLane(template);
                        lane.m_laneType = NetInfo.LaneType.Vehicle;
                        lane.m_vehicleType = bicycle;
                        lane.m_direction = Dir(spec);
                        lane.m_laneProps = null;
                        lane.m_stopType = VehicleInfo.VehicleType.None;
                        lane.m_position = spec.Position;
                        lane.m_width = spec.Width;
                        lane.m_speedLimit = Mathf.Min(speed, 1f);
                        break;

                    case PieceKind.Parking:
                        // Elevated roads, bridges and tunnels have no parking in vanilla.
                        if (!isGround && !hasParking) continue;
                        template = Pick(source, l => (l.m_laneType & parkingType) != 0, spec.Position, false);
                        if (template != null) lane = CopyLane(template);
                        else
                        {
                            template = PickTraffic(source, spec, false);
                            if (template == null) continue;
                            lane = CopyLane(template);
                            lane.m_laneType = parkingType;
                            lane.m_vehicleType = VehicleInfo.VehicleType.Car;
                            lane.m_laneProps = null;
                            lane.m_stopType = VehicleInfo.VehicleType.None;
                        }
                        lane.m_direction = Dir(spec);
                        lane.m_position = spec.Position;
                        lane.m_width = spec.Width;
                        break;

                    case PieceKind.Median:
                        // Trees: the vanilla road's decoration lane (props only).
                        template = Pick(source, l => l.m_laneType == NetInfo.LaneType.None && l.m_laneProps != null, spec.Position, true);
                        if (template == null) continue;
                        lane = CopyLane(template);
                        lane.m_position = spec.Position;
                        break;

                    default:
                        continue;
                }
                lanes.Add(lane);
            }
            return lanes.ToArray();
        }

        private static NetInfo.Direction Dir(LaneSpec spec) =>
            spec.Direction == LaneDirection.Forward ? NetInfo.Direction.Forward : NetInfo.Direction.Backward;

        private static NetInfo.Direction Dir(NetInfo.Lane lane) => lane.m_direction & NetInfo.Direction.Both;

        private static bool IsCarLane(NetInfo.Lane l) =>
            (l.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0 &&
            (l.m_vehicleType & VehicleInfo.VehicleType.Car) != 0;

        // The vanilla car lane to copy: same direction (bus lane for a bus lane,
        // if there is one), the outer one for the lane by the curb, else the
        // inner one. Falls back to any car lane.
        private static NetInfo.Lane PickTraffic(NetInfo.Lane[] source, LaneSpec spec, bool bus)
        {
            var dir = Dir(spec);
            NetInfo.Lane best = null;
            for (int pass = 0; pass < 3 && best == null; pass++)
            {
                foreach (var l in source)
                {
                    if (!IsCarLane(l)) continue;
                    bool isBus = (l.m_laneType & NetInfo.LaneType.TransportVehicle) != 0;
                    if (pass == 0 && (Dir(l) != dir || isBus != bus)) continue;
                    if (pass == 1 && Dir(l) != dir) continue;
                    if (best == null) { best = l; continue; }
                    bool further = Mathf.Abs(l.m_position) > Mathf.Abs(best.m_position);
                    if (spec.Outer == further) best = l;
                }
            }
            return best;
        }

        // A lane matching the filter, on the same side if possible (or nearest
        // to the middle).
        private static NetInfo.Lane Pick(NetInfo.Lane[] source, Func<NetInfo.Lane, bool> match, float position, bool middle)
        {
            NetInfo.Lane best = null;
            float bestScore = float.MaxValue;
            foreach (var l in source)
            {
                if (!match(l)) continue;
                float score = middle ? Mathf.Abs(l.m_position) : (Mathf.Sign(l.m_position) == Mathf.Sign(position) ? 0f : 1000f) + Mathf.Abs(l.m_position - position) * 0.001f;
                if (score < bestScore) { best = l; bestScore = score; }
            }
            return best;
        }

        // A field-by-field copy, so every setting the game expects (props, stop
        // types, flags) comes along. Only saved fields: the game works out the
        // rest (final direction, similar lanes, ...) when it initialises the road.
        private static NetInfo.Lane CopyLane(NetInfo.Lane source)
        {
            var copy = new NetInfo.Lane();
            foreach (var field in typeof(NetInfo.Lane).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (!field.IsNotSerialized) field.SetValue(copy, field.GetValue(source));
            // Our own copy of the props (lights, signs, arrows), so nothing the game
            // or we do to them touches the vanilla road's.
            if (copy.m_laneProps != null)
            {
                var props = UnityEngine.Object.Instantiate(copy.m_laneProps);
                props.name = copy.m_laneProps.name + " RR";
                copy.m_laneProps = props;
            }
            return copy;
        }

        private static T ParseEnum<T>(string name)
        {
            try { return (T)Enum.Parse(typeof(T), name); }
            catch { return default(T); }
        }

        // ---- Meshes ----

        private static void StretchMeshes(NetInfo info, float baseInner, float basePave, float newInner, float newPave)
        {
            string key = baseInner.ToString("0.###") + "/" + basePave.ToString("0.###") + "/" + newInner.ToString("0.###") + "/" + newPave.ToString("0.###");
            if (info.m_segments != null)
                foreach (var segment in info.m_segments)
                {
                    if (segment == null) continue;
                    segment.m_mesh = Stretch(segment.m_mesh, key, baseInner, basePave, newInner, newPave);
                    segment.m_lodMesh = Stretch(segment.m_lodMesh, key, baseInner, basePave, newInner, newPave);
                }
            if (info.m_nodes != null)
                foreach (var node in info.m_nodes)
                {
                    if (node == null) continue;
                    node.m_mesh = Stretch(node.m_mesh, key, baseInner, basePave, newInner, newPave);
                    node.m_lodMesh = Stretch(node.m_lodMesh, key, baseInner, basePave, newInner, newPave);
                }
        }

        private static Mesh Stretch(Mesh mesh, string key, float baseInner, float basePave, float newInner, float newPave)
        {
            if (mesh == null) return null;
            Dictionary<string, Mesh> byKey;
            if (!stretched.TryGetValue(mesh, out byKey)) stretched[mesh] = byKey = new Dictionary<string, Mesh>();
            Mesh result;
            if (byKey.TryGetValue(key, out result) && result != null) return result;

            result = mesh;
            try
            {
                var vertices = mesh.vertices;
                if (vertices == null || vertices.Length == 0)
                {
                    if (reported.Add("unreadable " + mesh.name)) Mod.Log("Road builder: mesh " + mesh.name + " can't be read, used as it is");
                }
                else
                {
                    var copy = UnityEngine.Object.Instantiate(mesh);
                    for (int i = 0; i < vertices.Length; i++)
                        vertices[i].x = CrossSection.StretchX(vertices[i].x, baseInner, basePave, newInner, newPave);
                    copy.vertices = vertices;
                    copy.RecalculateBounds();
                    copy.name = mesh.name + " RR";
                    result = copy;
                    if (reported.Add("layout " + mesh.name)) LogLayout(mesh);
                }
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: stretching mesh " + mesh.name + " failed, used as it is", e);
            }
            byKey[key] = result;
            return result;
        }

        // For R3.3 (generated meshes): what a vanilla road mesh looks like.
        private static void LogLayout(Mesh mesh)
        {
            try
            {
                var b = mesh.bounds;
                Mod.Log("Mesh " + mesh.name + ": " + mesh.vertexCount + " vertices, x " + b.min.x.ToString("0.00") + ".." +
                        b.max.x.ToString("0.00") + ", y " + b.min.y.ToString("0.00") + ".." + b.max.y.ToString("0.00") +
                        ", z " + b.min.z.ToString("0.00") + ".." + b.max.z.ToString("0.00") +
                        ", uv " + Count(mesh.uv) + ", uv2 " + Count(mesh.uv2) + ", colors " + Count(mesh.colors) +
                        ", tangents " + Count(mesh.tangents) + ", normals " + Count(mesh.normals));
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: reading mesh " + mesh.name + " failed", e);
            }
        }

        private static int Count(Array a) => a != null ? a.Length : 0;

        // ---- Costs and names ----

        private static void ScaleCost(NetInfo info, float factor)
        {
            if (info.m_netAI == null) return;
            foreach (var name in new[] { "m_constructionCost", "m_maintenanceCost" })
            {
                var field = AccessTools.Field(info.m_netAI.GetType(), name);
                if (field == null || field.FieldType != typeof(int)) continue;
                field.SetValue(info.m_netAI, Mathf.RoundToInt((int)field.GetValue(info.m_netAI) * factor));
            }
        }

        // The menu shows NET_TITLE / NET_DESC for the prefab name; add ours.
        private static void AddTitle(NetInfo info, RoadConfig config, CrossSection cs)
        {
            try
            {
                var managerType = AccessTools.TypeByName("ColossalFramework.Globalization.LocaleManager");
                var localeType = AccessTools.TypeByName("ColossalFramework.Globalization.Locale");
                if (managerType == null || localeType == null) return;
                var instance = AccessTools.Property(managerType, "instance");
                var manager = instance != null ? instance.GetValue(null, null) : null;
                var localeField = AccessTools.Field(managerType, "m_Locale");
                var locale = manager != null && localeField != null ? localeField.GetValue(manager) : null;
                var keyType = localeType.GetNestedType("Key");
                var add = AccessTools.Method(localeType, "AddLocalizedString");
                if (locale == null || keyType == null || add == null) return;

                string description = (cs.HalfWidth * 2f).ToString("0.0") + " m road builder road: " + cs.BackwardLanes + " lane(s) one way, " +
                                     cs.ForwardLanes + " the other, " + config.SpeedKmh.ToString("0") + " km/h.";
                foreach (var pair in new[] { new[] { "NET_TITLE", config.Name }, new[] { "NET_DESC", description } })
                {
                    object key = Activator.CreateInstance(keyType);
                    AccessTools.Field(keyType, "m_Identifier").SetValue(key, pair[0]);
                    AccessTools.Field(keyType, "m_Key").SetValue(key, info.name);
                    add.Invoke(locale, new[] { key, (object)pair[1] });
                }
            }
            catch (Exception e)
            {
                if (reported.Add("locale")) Mod.LogError("Road builder: naming roads in the menu failed (they show their internal name)", e);
            }
        }
    }
}
