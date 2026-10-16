using System;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // Creates the curve tool and its on-screen button when a city loads,
    // removes them when it unloads, and switches the tool on and off.
    // Everything here runs on the main thread.
    public static class ToolActivator
    {
        private static CurveTool tool;
        private static UIButton button;

        public static void Create(LoadMode mode)
        {
            if (mode == LoadMode.NewAsset || mode == LoadMode.LoadAsset ||
                mode == LoadMode.NewTheme || mode == LoadMode.LoadTheme)
            {
                Mod.Log("Curve tool not available in the asset/theme editor");
                return;
            }

            try
            {
                var controller = ToolsModifierControl.toolController;
                tool = controller.gameObject.AddComponent<CurveTool>();
                tool.enabled = false;
                CreateButton();
                Mod.Log("Curve tool ready: click the RR button or press Ctrl+Shift+C");
            }
            catch (Exception e)
            {
                Mod.LogError("Creating the curve tool failed", e);
                Destroy();
            }
        }

        public static void Destroy()
        {
            try
            {
                if (tool != null)
                {
                    if (tool.IsActive) ToolsModifierControl.SetTool<DefaultTool>();
                    UnityEngine.Object.Destroy(tool);
                }
                if (button != null) UnityEngine.Object.Destroy(button.gameObject);
            }
            catch (Exception e)
            {
                Mod.LogError("Removing the curve tool failed", e);
            }
            tool = null;
            button = null;
        }

        public static void Toggle()
        {
            if (tool == null) return;
            try
            {
                if (tool.IsActive)
                {
                    ToolsModifierControl.SetTool<DefaultTool>();
                    Mod.Log("Curve tool off");
                    return;
                }

                var netTool = ToolsModifierControl.GetTool<NetTool>();
                tool.SelectedNetwork = netTool != null ? netTool.m_prefab : null;
                ToolsModifierControl.toolController.CurrentTool = tool;
                Mod.Log("Curve tool on, network = " +
                        (tool.SelectedNetwork != null ? tool.SelectedNetwork.name : "none selected"));
            }
            catch (Exception e)
            {
                Mod.LogError("Switching the curve tool failed", e);
            }
        }

        private static void CreateButton()
        {
            var view = UIView.GetAView();
            button = (UIButton)view.AddUIComponent(typeof(UIButton));
            button.name = "RealisticRoadsButton";
            button.text = "RR";
            button.tooltip = "Realistic Roads curve tool (Ctrl+Shift+C)\nRight-drag to move this button";
            button.size = new Vector2(46f, 46f);
            button.relativePosition = new Vector3(10f, 120f);
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.focusedBgSprite = "ButtonMenu";
            button.textScale = 1.1f;

            button.eventClick += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Left) != 0) Toggle();
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
            ToolActivator.Toggle();
        }
    }
}
