using HarmonyLib;

namespace MyRoadMod
{
    // Harmony lets you run your own code before/after the game's methods.
    public static class Patcher
    {
        private const string HarmonyId = "com.yourname.myroadmod";
        private static bool patched;

        public static void PatchAll()
        {
            if (patched) return;
            new Harmony(HarmonyId).PatchAll(typeof(Patcher).Assembly);
            patched = true;
            Mod.Log("Harmony patches applied");
        }

        public static void UnpatchAll()
        {
            if (!patched) return;
            new Harmony(HarmonyId).UnpatchAll(HarmonyId);
            patched = false;
        }
    }

    // Example patch: runs every time the game creates a road segment.
    // Proves you can hook into road building, which a curve tool needs.
    [HarmonyPatch(typeof(NetManager), nameof(NetManager.CreateSegment))]
    public static class CreateSegmentPatch
    {
        // "segment" is an out parameter in the game, so Harmony needs "ref"
        public static void Postfix(bool __result, ref ushort segment)
        {
            if (__result) Mod.Log("Road segment created: #" + segment);
        }
    }
}
