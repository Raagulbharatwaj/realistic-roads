using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using UnityEngine;

namespace RealisticRoads
{
    // The RR button in UnifiedUI's toolbar, when that mod is on. Found by
    // reflection (no reference to its DLL), the same way its own helper finds
    // it: the enabled mod whose assembly has UnifiedUI.API.UUIAPI, and its
    // UnifiedUILib assembly. Without it (or if anything here fails) the
    // floating RR button is used as before.
    public static class UnifiedUIButton
    {
        private const string ApiName = "UnifiedUI.API.UUIAPI";
        private const string LibName = "UnifiedUILib";
        private static UIComponent button;
        private static PropertyInfo isActive;

        public static bool Registered => button != null;

        // The button that switches RR on and off ('toggle' with true: on).
        public static bool Register(string tooltip, Texture2D icon, Action<bool> toggle, Func<ToolBase, bool> isOurs)
        {
            Release();
            try
            {
                var api = FindApi();
                if (api == null) return false;
                if (icon == null)
                {
                    Mod.Log("UnifiedUI found, but the RR icon didn't load: the floating RR button is used");
                    return false;
                }
                // Register(name, groupName, tooltip, Texture2D, Action<bool> onToggle,
                //          Action<ToolBase> onToolChanged, SavedInputKey, Dictionary<SavedInputKey, Func<bool>>)
                MethodInfo register = null;
                foreach (var method in api.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "Register") continue;
                    var p = method.GetParameters();
                    if (p.Length == 8 && p[0].ParameterType == typeof(string) && p[3].ParameterType == typeof(Texture2D) &&
                        p[4].ParameterType == typeof(Action<bool>) && p[5].ParameterType == typeof(Action<ToolBase>))
                        register = method;
                }
                if (register == null)
                {
                    Mod.Log("UnifiedUI found, but not the way to add a button to it (another version?): the floating RR button is used");
                    return false;
                }
                Action<ToolBase> toolChanged = tool => SetPressed(isOurs(tool));
                button = register.Invoke(null, new object[]
                    { "RealisticRoadsUUIButton", null, tooltip, icon, toggle, toolChanged, null, null }) as UIComponent;
                if (button == null) return false;
                isActive = button.GetType().GetProperty("IsActive");
                Mod.Log("RR button added to UnifiedUI's toolbar");
                return true;
            }
            catch (Exception e)
            {
                Mod.LogError("Adding the RR button to UnifiedUI failed; the floating RR button is used", e);
                button = null;
                return false;
            }
        }

        // The button's look follows the tool when it is switched on or off
        // elsewhere (the key, Esc, the game's tools).
        public static void SetPressed(bool pressed)
        {
            if (button == null || isActive == null || !isActive.CanWrite) return;
            try { isActive.SetValue(button, pressed, null); }
            catch (Exception e) { Mod.LogError("UnifiedUI button state", e); isActive = null; }
        }

        public static void Release()
        {
            if (button != null)
            {
                try { UnityEngine.Object.Destroy(button.gameObject); }
                catch (Exception e) { Mod.LogError("Removing the UnifiedUI button failed", e); }
            }
            button = null;
            isActive = null;
        }

        private static Type FindApi()
        {
            foreach (var plugin in PluginManager.instance.GetPluginsInfo())
            {
                if (plugin == null || !plugin.isEnabled || plugin.userModInstance == null) continue;
                var modAssembly = plugin.userModInstance.GetType().Assembly;
                if (modAssembly.GetType(ApiName) == null) continue;
                foreach (var assembly in plugin.GetAssemblies())
                    if (assembly.GetName().Name == LibName)
                    {
                        var api = assembly.GetType(ApiName);
                        if (api != null) return api;
                    }
                return modAssembly.GetType(ApiName);
            }
            return null;
        }
    }
}
