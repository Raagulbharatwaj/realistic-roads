using System;
using System.Collections.Generic;
using System.Threading;
using CitiesHarmony.API;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // This class is what makes CS1 recognise the DLL as a mod.
    // Its Name/Description show up in Content Manager > Mods.
    public class Mod : IUserMod
    {
        public string Name => "Realistic Roads (beta)";
        public string Description => "Plan your roads and add layers to your city: draw a whole route, choose how it crosses everything, stack decks over and under streets.";

        // Settings (Options > Mod Settings), kept between games.
        // Read on the main thread when the mod starts; plain fields after that,
        // so any thread may read them (PlayerPrefs is main-thread only).
        private static bool debugLogging;

        public static bool DebugLogging
        {
            get { return debugLogging; }
            set { debugLogging = value; SetSetting("RealisticRoads.DebugLogging", value); }
        }

        private static void LoadSettings()
        {
            debugLogging = Setting("RealisticRoads.DebugLogging", false);
        }

        private static bool Setting(string key, bool fallback)
        {
            try { return PlayerPrefs.HasKey(key) ? PlayerPrefs.GetInt(key) != 0 : fallback; }
            catch { return fallback; }
        }

        private static void SetSetting(string key, bool value)
        {
            try { PlayerPrefs.SetInt(key, value ? 1 : 0); PlayerPrefs.Save(); }
            catch { }
        }

        // A message box for the player, shown when the city has finished loading.
        private static string pendingTitle, pendingMessage;

        public static void WarnAfterLoad(string title, string message)
        {
            pendingTitle = title;
            pendingMessage = message;
            Log(title + ": " + message);
        }

        // Main thread: the same message box, now.
        public static void WarnNow(string title, string message)
        {
            WarnAfterLoad(title, message);
            ShowPendingWarning();
        }

        // Two copies of the mod (a local build and the Workshop item, or two
        // Workshop items) both run their tools: the player should keep one.
        internal static void WarnIfLoadedTwice()
        {
            try
            {
                var copies = new List<string>();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    if (assembly.GetName().Name == typeof(Mod).Assembly.GetName().Name) copies.Add(assembly.GetName().Version.ToString());
                if (copies.Count > 1)
                    WarnAfterLoad("Loaded twice", "Realistic Roads is loaded " + copies.Count + " times (versions " +
                                  string.Join(", ", copies.ToArray()) + "), probably a local build and the Workshop item. " +
                                  "Untick one of them in Content Manager > Mods and restart the game.");
            }
            catch (Exception e)
            {
                LogError("Checking for a second copy failed", e);
            }
        }

        internal static void ShowPendingWarning()
        {
            if (pendingMessage == null) return;
            try
            {
                UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel").SetMessage("Realistic Roads: " + pendingTitle, pendingMessage, false);
            }
            catch (Exception e)
            {
                LogError("Showing a warning failed", e);
            }
            pendingTitle = pendingMessage = null;
        }

        // Unity's main thread. UI code (like the F7 panel) may only run here.
        private static Thread mainThread;
        private static readonly Queue<KeyValuePair<PluginManager.MessageType, string>> pendingPanelMessages =
            new Queue<KeyValuePair<PluginManager.MessageType, string>>();

        // Called when you tick the mod's checkbox in Content Manager
        public void OnEnabled()
        {
            mainThread = Thread.CurrentThread;
            LoadSettings();
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
            group.AddCheckbox("Detailed logging (every segment the game creates)", DebugLogging, value => DebugLogging = value);
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
            Mod.WarnIfLoadedTwice();
            Mod.ShowPendingWarning();
            ToolActivator.Create(mode);
            RoadUpgrade.Forget();   // this city's roads (their versions are looked up afresh)
            NetworkBuilder.ForgetQuay();
            // Nodes carrying a deck frame keep no pillar of their own, whatever was saved.
            SimulationManager.instance.AddAction(DeckFrames.RemovePillars);
        }

        public override void OnLevelUnloading()
        {
            Mod.Log("City unloading");
            ToolActivator.Destroy();
            // Nothing of this city kept (its roads, meshes) once it is gone.
            RoadUpgrade.Forget();
            DeckFrames.Clear();
            NetworkBuilder.ForgetQuay();
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
