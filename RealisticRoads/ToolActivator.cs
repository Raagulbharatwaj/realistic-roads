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
            }
            catch (Exception e)
            {
                Mod.LogError("Removing the tools failed", e);
            }
            curveTool = null;
            curveButton = null;
        }

        public static void ToggleCurveTool()
        {
            if (curveTool == null) return;
            try
            {
                if (curveTool.IsActive)
                {
                    ToolsModifierControl.SetTool<DefaultTool>();
                    Mod.Log("Curve tool off");
                    return;
                }

                var netTool = ToolsModifierControl.GetTool<NetTool>();
                curveTool.SelectedNetwork = netTool != null ? netTool.m_prefab : null;
                ToolsModifierControl.toolController.CurrentTool = curveTool;
                Mod.Log("Curve tool on, network = " +
                        (curveTool.SelectedNetwork != null ? curveTool.SelectedNetwork.name : "none selected"));
            }
            catch (Exception e)
            {
                Mod.LogError("Switching the curve tool failed", e);
            }
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
            if (!Input.GetKeyDown(KeyCode.C)) return;
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift || UIView.HasInputFocus()) return;
            ToolActivator.ToggleCurveTool();
        }
    }
}
