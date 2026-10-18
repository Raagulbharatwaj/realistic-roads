using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace RealisticRoads
{
    // Harmony lets you run your own code before/after the game's methods.
    public static class Patcher
    {
        private const string HarmonyId = "com.raagul.realisticroads";
        private static bool patched;

        public static void PatchAll()
        {
            if (patched) return;
            var harmony = new Harmony(HarmonyId);
            int failures = 0;
            // One class at a time, so a patch that can't find its target (e.g.
            // after a game update) only switches off that one feature.
            foreach (var type in typeof(Patcher).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failures++;
                    Mod.LogError("Patch " + type.Name + " failed, that feature is off", e);
                }
            }
            patched = true;
            foreach (var method in harmony.GetPatchedMethods()) Mod.Log("Patched " + Describe(method));
            Mod.Log("Harmony patches applied" + (failures > 0 ? " (" + failures + " failed)" : ""));
        }

        public static void UnpatchAll()
        {
            try
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                if (patched) Mod.Log("Harmony patches removed");
            }
            catch (Exception e)
            {
                Mod.LogError("Removing Harmony patches failed", e);
            }
            patched = false;
        }

        internal static string Describe(MethodBase method)
        {
            var parts = new List<string>();
            foreach (var p in method.GetParameters()) parts.Add(p.ParameterType.Name + " " + p.Name);
            return method.DeclaringType.Name + "." + method.Name + "(" + string.Join(", ", parts.ToArray()) + ")";
        }
    }

    // Example patch: runs every time the game creates a road segment.
    // Proves you can hook into road building, which a curve tool needs.
    // NOTE: CreateSegment runs on the simulation thread. Only thread-safe code here.
    [HarmonyPatch]
    public static class CreateSegmentPatch
    {
        // One overload calls the other, so a single new segment passes through
        // this patch twice with the same id. Remember the last id logged on this
        // thread and skip the repeat.
        [ThreadStatic] private static ushort lastLogged;

        // The game has more than one CreateSegment overload (with and without
        // TreeInfo), and a plain [HarmonyPatch(typeof(NetManager), "CreateSegment")]
        // throws when the name is ambiguous. Patch every overload that starts with
        // "out ushort segment" and returns bool instead.
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(NetManager).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != "CreateSegment" || method.ReturnType != typeof(bool)) continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 0) continue;
                var first = parameters[0];
                if (first.Name == "segment" && first.IsOut && first.ParameterType == typeof(ushort).MakeByRefType())
                    yield return method;
            }
        }

        // "segment" is an out parameter in the game, so Harmony needs "ref"
        public static void Postfix(bool __result, ref ushort segment)
        {
            try
            {
                if (!__result || !Mod.DebugLogging || segment == lastLogged) return;
                lastLogged = segment;
                Mod.Log("Road segment created: #" + segment + DescribeBridge(segment));
            }
            catch (Exception e)
            {
                Mod.LogError("CreateSegment postfix failed", e);
            }
        }

        // For suspension bridges (m_doubleLength): the length and which ends
        // are towers or middle nodes, to compare our bridges with vanilla ones.
        private static string DescribeBridge(ushort segment)
        {
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[segment];
            var info = s.Info;
            if (info == null || info.m_netAI == null) return "";
            var field = info.m_netAI.GetType().GetField("m_doubleLength", BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(bool) || !(bool)field.GetValue(info.m_netAI)) return "";
            var nodes = net.m_nodes.m_buffer;
            float length = CurveMath.FlatDistance(nodes[s.m_startNode].m_position, nodes[s.m_endNode].m_position);
            return " (" + info.name + ", " + length.ToString("F0") + " m, start node #" + s.m_startNode +
                   " " + NodeState(nodes[s.m_startNode]) + ", end node #" + s.m_endNode + " " + NodeState(nodes[s.m_endNode]) + ")";
        }

        private static string NodeState(NetNode node)
        {
            bool middle = (node.m_flags & NetNode.Flags.Double) != NetNode.Flags.None;
            return (middle ? "middle" : "tower") + " elev " + node.m_elevation;
        }
    }
}
