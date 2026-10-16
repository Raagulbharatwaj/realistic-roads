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
        private static SplitTool splitTool;
        private static JunctionTool junctionTool;
        private static UIButton junctionButton;
        private static UIButton curveButton;
        private static UIButton splitButton;
        private static UILabel infoLabel;

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
                splitTool = controller.gameObject.AddComponent<SplitTool>();
                splitTool.enabled = false;
                junctionTool = controller.gameObject.AddComponent<JunctionTool>();
                junctionTool.enabled = false;

                curveButton = CreateButton("RealisticRoadsCurveButton", "RR",
                    "Realistic Roads curve tool (Ctrl+Shift+C)", 120f, ToggleCurveTool);
                splitButton = CreateButton("RealisticRoadsSplitButton", "SPL",
                    "Realistic Roads smooth split tool (Ctrl+Shift+J)\nClick a junction where a road splits in two", 172f, ToggleSplitTool);

                junctionButton = CreateButton("RealisticRoadsJunctionButton", "JN",
                    "Realistic Roads junction tool (Ctrl+Shift+N)\nClick a junction for the CS2 look", 224f, ToggleJunctionTool);

                infoLabel = CreateInfoLabel(276f);
                NetworkBuilder.ClearUndo();

                Mod.Log("Tools ready: RR / Ctrl+Shift+C = curve tool, SPL / Ctrl+Shift+J = smooth split tool, " +
                        "JN / Ctrl+Shift+N = junction tool");
                SmoothSplits.RefreshAll();
                Junctions.RefreshAll();
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
                if (current != null && (current == curveTool || current == splitTool || current == junctionTool))
                    ToolsModifierControl.SetTool<DefaultTool>();
                if (curveTool != null)
                {
                    curveTool.DestroyButtons();
                    UnityEngine.Object.Destroy(curveTool);
                }
                if (splitTool != null) UnityEngine.Object.Destroy(splitTool);
                if (junctionTool != null) UnityEngine.Object.Destroy(junctionTool);
                if (junctionButton != null) UnityEngine.Object.Destroy(junctionButton.gameObject);
                if (curveButton != null) UnityEngine.Object.Destroy(curveButton.gameObject);
                if (splitButton != null) UnityEngine.Object.Destroy(splitButton.gameObject);
                if (infoLabel != null) UnityEngine.Object.Destroy(infoLabel.gameObject);
            }
            catch (Exception e)
            {
                Mod.LogError("Removing the tools failed", e);
            }
            curveTool = null;
            splitTool = null;
            curveButton = null;
            splitButton = null;
            junctionTool = null;
            junctionButton = null;
            infoLabel = null;
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

        public static void ToggleJunctionTool()
        {
            if (junctionTool == null) return;
            try
            {
                if (junctionTool.IsActive)
                {
                    ToolsModifierControl.SetTool<DefaultTool>();
                    Mod.Log("Junction tool off");
                    return;
                }
                ToolsModifierControl.toolController.CurrentTool = junctionTool;
                Mod.Log("Junction tool on: green = CS2-style, yellow = can be, red = can't");
            }
            catch (Exception e)
            {
                Mod.LogError("Switching the junction tool failed", e);
            }
        }

        public static void ToggleSplitTool()
        {
            if (splitTool == null) return;
            try
            {
                if (splitTool.IsActive)
                {
                    ToolsModifierControl.SetTool<DefaultTool>();
                    Mod.Log("Smooth split tool off");
                    return;
                }
                ToolsModifierControl.toolController.CurrentTool = splitTool;
                Mod.Log("Smooth split tool on: green = smoothed, yellow = can smooth, red = not a split");
            }
            catch (Exception e)
            {
                Mod.LogError("Switching the smooth split tool failed", e);
            }
        }

        // Main thread. Shows text under the buttons; null hides it.
        public static void SetInfo(string text)
        {
            if (infoLabel == null) return;
            if (string.IsNullOrEmpty(text))
            {
                infoLabel.isVisible = false;
                return;
            }
            if (infoLabel.text != text) infoLabel.text = text;
            infoLabel.isVisible = true;
        }

        private static UILabel CreateInfoLabel(float y)
        {
            var label = (UILabel)UIView.GetAView().AddUIComponent(typeof(UILabel));
            label.name = "RealisticRoadsInfo";
            label.backgroundSprite = "GenericPanel";
            label.padding = new RectOffset(8, 8, 6, 6);
            label.textScale = 0.8f;
            label.autoSize = true;
            label.relativePosition = new Vector3(10f, y);
            label.isVisible = false;
            return label;
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

    // Ctrl+Shift+C toggles the curve tool, Ctrl+Shift+J the smooth split tool,
    // Ctrl+Shift+N the junction tool.
    // OnUpdate runs on the main thread.
    public class HotkeyHandler : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            bool c = Input.GetKeyDown(KeyCode.C);
            bool j = Input.GetKeyDown(KeyCode.J);
            bool n = Input.GetKeyDown(KeyCode.N);
            if (!c && !j && !n) return;
            var ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift || UIView.HasInputFocus()) return;
            if (c) ToolActivator.ToggleCurveTool();
            else if (j) ToolActivator.ToggleSplitTool();
            else ToolActivator.ToggleJunctionTool();
        }
    }
}
