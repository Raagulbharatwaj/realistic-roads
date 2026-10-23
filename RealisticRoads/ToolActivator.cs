using System;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // Creates the mod's tools and their on-screen buttons when a city loads,
    // removes them when it unloads, and switches the tools on and off.
    // Everything here runs on the main thread.
    public static class ToolActivator
    {
        private static CurveTool curveTool;
        private static UIButton curveButton;

        // RR as a mode of the game's road tool: while it is on, picking a road
        // in the game's Roads menu (which starts the game's road tool) opens RR
        // with that road instead. On with the RR button or the RR button in the
        // road tool's options; off with Esc, X, "Game tool" or the RR button.
        public static bool Mode { get; private set; }
        private static UIButton modeButton;      // RR toggle shown next to the game's road tool options
        private static UIComponent modeHost;     // the options panel it sits on
        private static int searchWait;
        private const string ModeButtonName = "RealisticRoadsModeButton";
        private static bool reportedNoHost;

        public static void Create(LoadMode mode)
        {
            if (mode == LoadMode.NewAsset || mode == LoadMode.LoadAsset ||
                mode == LoadMode.NewTheme || mode == LoadMode.LoadTheme)
            {
                Mod.Log("Tools not available in the asset/theme editor");
                return;
            }

            try
            {
                var controller = ToolsModifierControl.toolController;
                curveTool = controller.gameObject.AddComponent<CurveTool>();
                curveTool.enabled = false;

                curveButton = CreateButton("RealisticRoadsCurveButton", "RR",
                    "Realistic Roads (Ctrl+Shift+C)\nPick a road in the Roads menu first, then draw it with curves", 120f, ToggleCurveTool);
                NetworkBuilder.ClearUndo();

                Mod.Log("Tools ready: RR / Ctrl+Shift+C = curve tool");
            }
            catch (Exception e)
            {
                Mod.LogError("Creating the tools failed", e);
                Destroy();
            }
        }

        public static void Destroy()
        {
            try
            {
                var current = ToolsModifierControl.toolController != null
                    ? ToolsModifierControl.toolController.CurrentTool
                    : null;
                if (current != null && current == curveTool)
                    ToolsModifierControl.SetTool<DefaultTool>();
                if (curveTool != null)
                {
                    curveTool.DestroyButtons();
                    UnityEngine.Object.Destroy(curveTool);
                }
                if (curveButton != null) UnityEngine.Object.Destroy(curveButton.gameObject);
                if (modeButton != null) UnityEngine.Object.Destroy(modeButton.gameObject);
            }
            catch (Exception e)
            {
                Mod.LogError("Removing the tools failed", e);
            }
            curveTool = null;
            curveButton = null;
            modeButton = null;
            modeHost = null;
            Mode = false;
        }

        public static void ToggleCurveTool()
        {
            if (curveTool == null) return;
            try
            {
                if (curveTool.IsActive)
                {
                    LeaveMode();
                    ToolsModifierControl.SetTool<DefaultTool>();
                    Mod.Log("Curve tool off");
                    return;
                }

                var netTool = ToolsModifierControl.GetTool<NetTool>();
                Open(netTool != null ? netTool.m_prefab : null);
            }
            catch (Exception e)
            {
                Mod.LogError("Switching the curve tool failed", e);
            }
        }

        // RR on with this road, and the mode on.
        private static void Open(NetInfo road)
        {
            Mode = true;
            curveTool.SelectedNetwork = road;
            ToolsModifierControl.toolController.CurrentTool = curveTool;
            Mod.Log("Curve tool on, network = " + (road != null ? road.name : "none selected"));
        }

        public static void LeaveMode()
        {
            if (Mode) Mod.Log("RR mode off: the game's road tool is used again");
            Mode = false;
        }

        // The game's road tool with this road (from the panel's "Game tool").
        public static void SwitchToGameTool(NetInfo road)
        {
            try
            {
                LeaveMode();
                var netTool = ToolsModifierControl.GetTool<NetTool>();
                if (netTool == null || road == null)
                {
                    ToolsModifierControl.SetTool<DefaultTool>();
                    return;
                }
                netTool.m_prefab = road;
                ToolsModifierControl.toolController.CurrentTool = netTool;
            }
            catch (Exception e)
            {
                Mod.LogError("Switching to the game's road tool failed", e);
            }
        }

        // Every frame (main thread). In RR mode, a road picked in the game's
        // menu opens RR with it; and the RR toggle is kept next to the game's
        // road tool options while that tool is open.
        public static void Update()
        {
            if (curveTool == null) return;
            try
            {
                var controller = ToolsModifierControl.toolController;
                var current = controller != null ? controller.CurrentTool : null;
                var netTool = current != null && current.GetType() == typeof(NetTool) ? (NetTool)current : null;
                if (Mode && netTool != null && netTool.m_prefab != null && CanDraw(netTool.m_prefab))
                {
                    Open(netTool.m_prefab);
                    netTool = null;
                }
                UpdateModeButton(netTool != null);
            }
            catch (Exception e)
            {
                Mod.LogError("RR mode failed; it is off", e);
                Mode = false;
            }
        }

        // Roads, tracks and paths: not pipes, power lines or the like.
        private static bool CanDraw(NetInfo info)
        {
            if (info.m_class == null) return false;
            var service = info.m_class.m_service;
            return service == ItemClass.Service.Road || service == ItemClass.Service.PublicTransport ||
                   service == ItemClass.Service.Beautification;
        }

        // The RR toggle on the game's road tool options panel (the Straight /
        // Curved / Freeform / Upgrade row), found by name while the game's road
        // tool is open. If the panel can't be found the floating RR button
        // still does the same.
        private static void UpdateModeButton(bool gameRoadTool)
        {
            if (!gameRoadTool)
            {
                if (modeButton != null && modeButton.isVisible) modeButton.isVisible = false;
                return;
            }
            if (modeHost == null || !modeHost.isVisible)
            {
                if (modeButton != null && modeButton.isVisible) modeButton.isVisible = false;
                // Looking through the whole UI is slow: at most every 60 frames.
                if (--searchWait > 0) return;
                searchWait = 60;
                var host = FindOptionsPanel();
                if (host == null)
                {
                    searchWait = 120;
                    if (!reportedNoHost) Mod.Log("RR mode: the game's road options panel wasn't found; use the RR button");
                    reportedNoHost = true;
                    return;
                }
                // Each road group (roads, rail, paths...) has its own options
                // panel: move to the one now open, reusing a toggle already on it.
                if (host != modeHost) Mod.Log("RR mode: toggle on " + host.name);
                modeHost = host;
                var existing = host.Find<UIButton>(ModeButtonName);
                if (modeButton != null && modeButton != existing) UnityEngine.Object.Destroy(modeButton.gameObject);
                modeButton = existing ?? CreateModeButton(host);
                PlaceModeButton();
            }
            if (!modeButton.isVisible) modeButton.isVisible = true;
        }

        // Right of the options, or left of them if that would be off the screen.
        private static void PlaceModeButton()
        {
            float x = modeHost.width + 6f;
            var view = UIView.GetAView();
            if (view != null && view.fixedWidth > 0 && modeHost.absolutePosition.x + x + 36f > view.fixedWidth) x = -42f;
            modeButton.relativePosition = new Vector3(x, Mathf.Max((modeHost.height - 36f) * 0.5f, 0f));
        }

        // The game's options panels with the road tool's modes (straight, curved,
        // upgrade...): roads, and the tracks and paths menus, which have their own.
        private static readonly string[] OptionPanels = { "RoadsOptionPanel", "TracksOptionPanel", "PathsOptionPanel" };

        private static UIComponent FindOptionsPanel()
        {
            var view = UIView.GetAView();
            if (view == null) return null;
            UIComponent other = null;
            foreach (var panel in view.GetComponentsInChildren<UIPanel>())
            {
                if (panel == null || !panel.isVisible || panel.name == null) continue;
                foreach (var prefix in OptionPanels) if (panel.name.StartsWith(prefix)) return panel;
                // A menu (or a mod's) with an options panel of another name.
                if (other == null && panel.name.EndsWith("OptionPanel")) other = panel;
            }
            return other;
        }

        private static UIButton CreateModeButton(UIComponent host)
        {
            var button = (UIButton)host.AddUIComponent(typeof(UIButton));
            button.name = ModeButtonName;
            button.size = new Vector2(36f, 36f);
            button.tooltip = "Realistic Roads: draw this road with RR (curves, bridges, crossings)\nRR stays on for every road you pick until you leave it";
            if (Icons.Has(Icons.Logo, Icons.ButtonBackground, Icons.ButtonBackgroundHover, Icons.ButtonBackgroundPressed))
            {
                button.atlas = Icons.Atlas;
                button.normalBgSprite = Icons.ButtonBackground;
                button.hoveredBgSprite = Icons.ButtonBackgroundHover;
                button.pressedBgSprite = Icons.ButtonBackgroundPressed;
                button.normalFgSprite = button.hoveredFgSprite = button.pressedFgSprite = Icons.Logo;
            }
            else
            {
                button.normalBgSprite = "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed";
                button.text = "RR";
                button.textScale = 0.9f;
            }
            button.eventClick += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Left) == 0) return;
                var netTool = ToolsModifierControl.GetTool<NetTool>();
                Open(netTool != null ? netTool.m_prefab : null);
            };
            return button;
        }

        private static UIButton CreateButton(string name, string text, string tooltip, float y, Action onClick)
        {
            var view = UIView.GetAView();
            var button = (UIButton)view.AddUIComponent(typeof(UIButton));
            button.name = name;
            button.text = text;
            button.tooltip = tooltip + "\nRight-drag to move this button";
            button.size = new Vector2(46f, 46f);
            button.relativePosition = new Vector3(10f, y);
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.focusedBgSprite = "ButtonMenu";
            button.textScale = text.Length > 2 ? 0.9f : 1.1f;
            // The logo on the vanilla button background; the letters only if the icons can't load.
            if (Icons.Has(Icons.Logo, Icons.ButtonBackground, Icons.ButtonBackgroundHover, Icons.ButtonBackgroundPressed))
            {
                button.atlas = Icons.Atlas;
                button.normalBgSprite = Icons.ButtonBackground;
                button.hoveredBgSprite = Icons.ButtonBackgroundHover;
                button.pressedBgSprite = Icons.ButtonBackgroundPressed;
                button.focusedBgSprite = Icons.ButtonBackground;
                button.normalFgSprite = button.hoveredFgSprite = button.pressedFgSprite = button.focusedFgSprite = Icons.Logo;
                button.text = "";
            }

            button.eventClick += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Left) != 0) onClick();
            };

            // Right-drag moves the button, so it can be put out of the way.
            button.eventMouseMove += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Right) == 0) return;
                var ratio = UIView.GetAView().ratio;
                component.position = new Vector3(
                    component.position.x + param.moveDelta.x * ratio,
                    component.position.y + param.moveDelta.y * ratio,
                    component.position.z);
            };
            // Remember where it was put (per button, across cities and restarts).
            button.eventMouseUp += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Right) == 0) return;
                try
                {
                    PlayerPrefs.SetFloat(name + ".x", component.relativePosition.x);
                    PlayerPrefs.SetFloat(name + ".y", component.relativePosition.y);
                    PlayerPrefs.Save();
                }
                catch (Exception e)
                {
                    Mod.LogError("Saving the button position failed", e);
                }
            };
            if (PlayerPrefs.HasKey(name + ".x"))
                button.relativePosition = new Vector3(PlayerPrefs.GetFloat(name + ".x"), PlayerPrefs.GetFloat(name + ".y"));
            return button;
        }
    }

    // Ctrl+Shift+C toggles the curve tool. OnUpdate runs on the main thread.
    public class HotkeyHandler : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            ToolActivator.Update();
            if (!Input.GetKeyDown(KeyCode.C)) return;
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift || UIView.HasInputFocus()) return;
            ToolActivator.ToggleCurveTool();
        }
    }
}
