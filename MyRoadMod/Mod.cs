using CitiesHarmony.API;
using ColossalFramework.Plugins;
using ICities;
using UnityEngine;

namespace MyRoadMod
{
    // This class is what makes CS1 recognise the DLL as a mod.
    // Its Name/Description show up in Content Manager > Mods.
    public class Mod : IUserMod
    {
        public string Name => "My Road Mod";
        public string Description => "Starter mod for custom road tools.";

        // Called when you tick the mod's checkbox in Content Manager
        public void OnEnabled()
        {
            HarmonyHelper.DoOnHarmonyReady(Patcher.PatchAll);
            Log("Enabled");
        }

        // Called when you untick it (or the game closes)
        public void OnDisabled()
        {
            if (HarmonyHelper.IsHarmonyInstalled) Patcher.UnpatchAll();
            Log("Disabled");
        }

        // Adds a settings page under Options > Mod Settings
        public void OnSettingsUI(UIHelperBase helper)
        {
            var group = helper.AddGroup("My Road Mod");
            group.AddButton("Say hello", () => Log("Hello from the settings page!"));
        }

        public static void Log(string msg)
        {
            // Goes to output_log.txt
            Debug.Log("[MyRoadMod] " + msg);
            // Shows in the in-game debug panel (press F7 in game)
            DebugOutputPanel.AddMessage(PluginManager.MessageType.Message, "[MyRoadMod] " + msg);
        }
    }

    // Runs when a city or the editor finishes loading
    public class Loading : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            Mod.Log("City loaded, mode = " + mode);
            // Later: this is where you'll add your road-tool button / tool.
        }
    }
}
