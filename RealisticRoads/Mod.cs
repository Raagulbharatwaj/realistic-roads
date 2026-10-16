using System;
using CitiesHarmony.API;
using ColossalFramework.Plugins;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // This class is what makes CS1 recognise the DLL as a mod.
    // Its Name/Description show up in Content Manager > Mods.
    public class Mod : IUserMod
    {
        public string Name => "Realistic Roads";
        public string Description => "CS2-style smooth curves and flyovers with pillars on the road below.";

        // Called when you tick the mod's checkbox in Content Manager
        public void OnEnabled()
        {
            Log("Enabled, version " + typeof(Mod).Assembly.GetName().Version);
            HarmonyHelper.DoOnHarmonyReady(Patcher.PatchAll);
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
            var group = helper.AddGroup("Realistic Roads");
            group.AddButton("Say hello", () => Log("Hello from the settings page!"));
        }

        public static void Log(string msg)
        {
            // Goes to output_log.txt
            Debug.Log("[RealisticRoads] " + msg);
            // Shows in the in-game debug panel (press F7 in game)
            AddPanelMessage(PluginManager.MessageType.Message, "[RealisticRoads] " + msg);
        }

        public static void LogError(string msg, Exception e)
        {
            Debug.LogError("[RealisticRoads] " + msg + "\n" + e);
            AddPanelMessage(PluginManager.MessageType.Error, "[RealisticRoads] " + msg + ": " + e.Message);
        }

        private static void AddPanelMessage(PluginManager.MessageType type, string msg)
        {
            // The panel may not exist yet very early in startup; never let logging throw.
            try { DebugOutputPanel.AddMessage(type, msg); }
            catch { }
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

        public override void OnLevelUnloading()
        {
            Mod.Log("City unloading");
        }
    }
}
