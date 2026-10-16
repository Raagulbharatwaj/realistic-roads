using System;
using System.Collections.Generic;
using System.Reflection;
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
            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(typeof(Patcher).Assembly);
                patched = true;
                foreach (var method in harmony.GetPatchedMethods()) Mod.Log("Patched " + Describe(method));
                Mod.Log("Harmony patches applied");
            }
            catch (Exception e)
            {
                // A failed patch must not take the game down; log it and run vanilla.
                Mod.LogError("Applying Harmony patches failed, running without them", e);
                UnpatchAll();
            }
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
                if (!__result || segment == lastLogged) return;
                lastLogged = segment;
                Mod.Log("Road segment created: #" + segment);
            }
            catch (Exception e)
            {
                Mod.LogError("CreateSegment postfix failed", e);
            }
        }
    }
}
