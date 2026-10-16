using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace RealisticRoads
{
    // Milestone 2: the tool skeleton. Left-click places control points on the
    // ground, right-click removes the last one, Esc returns to the vanilla tool.
    // Points are drawn as circles; no curve or road building yet.
    public class CurveTool : ToolBase
    {
        private const float PointSize = 6f;
        private const float HoverSize = 4f;

        private static readonly Color PointColor = new Color(0.2f, 0.8f, 1f, 0.9f);
        private static readonly Color HoverColor = new Color(1f, 1f, 1f, 0.6f);

        private readonly List<Vector3> points = new List<Vector3>();

        // Written on the main thread in OnToolUpdate, read by SimulationStep.
        private Ray mouseRay;
        private float mouseRayLength;
        private volatile bool mouseRayValid;

        // Written by SimulationStep (simulation thread), read on the main thread.
        private Vector3 hoverPosition;
        private volatile bool hoverValid;

        // The road/track the player last picked in the vanilla Roads menu.
        // Not used until milestone 4; logged so we can see what we'd build with.
        public NetInfo SelectedNetwork { get; set; }

        public bool IsActive => ToolsModifierControl.toolController != null &&
                                ToolsModifierControl.toolController.CurrentTool == this;

        protected override void OnEnable()
        {
            base.OnEnable();
            points.Clear();
            hoverValid = false;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            points.Clear();
            hoverValid = false;
            mouseRayValid = false;
        }

        // Main thread, every frame while the tool is active.
        protected override void OnToolUpdate()
        {
            base.OnToolUpdate();
            var camera = Camera.main;
            if (camera == null)
            {
                mouseRayValid = false;
                return;
            }
            mouseRay = camera.ScreenPointToRay(Input.mousePosition);
            mouseRayLength = camera.farClipPlane;
            mouseRayValid = !m_toolController.IsInsideUI && Cursor.visible;
        }

        // Simulation thread. Raycasting is done here, like the vanilla tools do.
        public override void SimulationStep()
        {
            base.SimulationStep();
            if (!mouseRayValid)
            {
                hoverValid = false;
                return;
            }

            var input = new RaycastInput(mouseRay, mouseRayLength);
            input.m_ignoreTerrain = false;
            RaycastOutput output;
            if (RayCast(input, out output))
            {
                hoverPosition = output.m_hitPos;
                hoverValid = true;
            }
            else
            {
                hoverValid = false;
            }
        }

        // Main thread, for mouse and keyboard events.
        protected override void OnToolGUI(Event e)
        {
            base.OnToolGUI(e);

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                Mod.Log("Esc pressed, back to the vanilla tool");
                ToolsModifierControl.SetTool<DefaultTool>();
                return;
            }

            if (e.type != EventType.MouseDown || m_toolController.IsInsideUI) return;

            if (e.button == 0 && hoverValid)
            {
                var p = hoverPosition;
                points.Add(p);
                Mod.Log("Point " + points.Count + " at " + Format(p));
                e.Use();
            }
            else if (e.button == 1 && points.Count > 0)
            {
                points.RemoveAt(points.Count - 1);
                Mod.Log("Removed last point, " + points.Count + " left");
                e.Use();
            }
        }

        // Main thread. Draws the points onto the terrain.
        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);
            var overlay = Singleton<RenderManager>.instance.OverlayEffect;

            foreach (var p in points)
                overlay.DrawCircle(cameraInfo, PointColor, p, PointSize, -1f, 1280f, false, true);

            if (hoverValid)
                overlay.DrawCircle(cameraInfo, HoverColor, hoverPosition, HoverSize, -1f, 1280f, false, true);
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F1") + ", " + v.y.ToString("F1") + ", " + v.z.ToString("F1") + ")";
        }
    }
}
