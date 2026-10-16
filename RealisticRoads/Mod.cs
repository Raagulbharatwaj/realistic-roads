using System;
using System.Collections.Generic;
using System.Threading;
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

        // Unity's main thread. UI code (like the F7 panel) may only run here.
        private static Thread mainThread;
        private static readonly Queue<KeyValuePair<PluginManager.MessageType, string>> pendingPanelMessages =
            new Queue<KeyValuePair<PluginManager.MessageType, string>>();

        // Called when you tick the mod's checkbox in Content Manager
        public void OnEnabled()
        {
            mainThread = Thread.CurrentThread;
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

        // Safe to call from any thread, including the simulation thread.
        public static void Log(string msg)
        {
            // Goes to output_log.txt (Unity's Debug.Log is thread-safe)
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
            if (Thread.CurrentThread != mainThread)
            {
                // Touching UI off the main thread crashes the game natively (no
                // exception to catch), so hand it to the main thread instead.
                lock (pendingPanelMessages)
                {
                    pendingPanelMessages.Enqueue(new KeyValuePair<PluginManager.MessageType, string>(type, msg));
                }
                return;
            }

            // The panel may not exist yet very early in startup; never let logging throw.
            try { DebugOutputPanel.AddMessage(type, msg); }
            catch { }
        }

        // Called every frame on the main thread by LogFlusher.
        internal static void FlushPanelMessages()
        {
            if (mainThread == null) mainThread = Thread.CurrentThread;
            while (true)
            {
                KeyValuePair<PluginManager.MessageType, string> item;
                lock (pendingPanelMessages)
                {
                    if (pendingPanelMessages.Count == 0) return;
                    item = pendingPanelMessages.Dequeue();
                }
                try { DebugOutputPanel.AddMessage(item.Key, item.Value); }
                catch { }
            }
        }
    }

    // Runs when a city or the editor finishes loading
    public class Loading : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            Mod.Log("City loaded, mode = " + mode);
            try
            {
                RoadTypes.CreateMissing();
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: registering library roads failed", e);
            }
            ToolActivator.Create(mode);
        }

        public override void OnLevelUnloading()
        {
            Mod.Log("City unloading");
            ToolActivator.Destroy();
        }
    }

    // OnUpdate runs on the main thread every frame while a city is loaded.
    public class LogFlusher : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            Mod.FlushPanelMessages();
        }
    }
}
