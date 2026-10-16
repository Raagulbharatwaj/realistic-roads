using ColossalFramework;
using UnityEngine;

namespace RealisticRoads
{
    // CS2-style junction tool: hover a junction, click to switch the CS2 look
    // on or off, + / - to make its corners bigger or smaller. Esc leaves.
    //   green  = CS2-style junction
    //   yellow = a junction that can get it
    //   red    = can't (the log says why when you click it)
    public class JunctionTool : ToolBase
    {
        private const float MaxNodeDistance = 40f;
        private const float CornerStep = 1f;

        private static readonly Color OnColor = new Color(0.2f, 1f, 0.3f, 0.8f);
        private static readonly Color CanColor = new Color(1f, 0.85f, 0.1f, 0.8f);
        private static readonly Color NoColor = new Color(1f, 0.25f, 0.2f, 0.6f);

        // Written on the main thread in OnToolUpdate, read by SimulationStep.
        // Shared under rayLock: a Ray is too big to be written and read atomically.
        private readonly object rayLock = new object();
        private Ray mouseRay;
        private float mouseRayLength;
        private Ray simRay;              // simulation thread's copy, taken under rayLock
        private float simRayLength;
        private volatile bool mouseRayValid;

        // Written by SimulationStep (simulation thread), read on the main thread.
        private volatile ushort hoverNode;

        public bool IsActive => ToolsModifierControl.toolController != null &&
                                ToolsModifierControl.toolController.CurrentTool == this;

        protected override void OnDisable()
        {
            base.OnDisable();
            hoverNode = 0;
            mouseRayValid = false;
            ToolActivator.SetInfo(null);
        }

        protected override void OnToolUpdate()
        {
            base.OnToolUpdate();
            var camera = Camera.main;
            if (camera == null)
            {
                mouseRayValid = false;
                return;
            }
            lock (rayLock)
            {
                mouseRay = camera.ScreenPointToRay(Input.mousePosition);
                mouseRayLength = camera.farClipPlane;
            }
            mouseRayValid = !m_toolController.IsInsideUI && Cursor.visible;
            ToolActivator.SetInfo(InfoText());
        }

        public override void SimulationStep()
        {
            base.SimulationStep();
            lock (rayLock)
            {
                simRay = mouseRay;
                simRayLength = mouseRayLength;
            }
            hoverNode = mouseRayValid ? FindHoveredNode() : (ushort)0;
        }

        // Simulation thread. A node under the mouse, or the nearer end of the
        // road under the mouse.
        private ushort FindHoveredNode()
        {
            var input = new RaycastInput(simRay, simRayLength);
            input.m_netService = new RaycastService { m_itemLayers = ItemClass.Layer.Default };
            input.m_ignoreTerrain = true;
            input.m_ignoreNodeFlags = NetNode.Flags.None;
            RaycastOutput output;
            if (RayCast(input, out output) && output.m_netNode != 0) return output.m_netNode;

            input = new RaycastInput(simRay, simRayLength);
            input.m_netService = new RaycastService { m_itemLayers = ItemClass.Layer.Default };
            input.m_ignoreTerrain = true;
            input.m_ignoreSegmentFlags = NetSegment.Flags.None;
            if (!RayCast(input, out output) || output.m_netSegment == 0) return 0;

            var segment = NetManager.instance.m_segments.m_buffer[output.m_netSegment];
            var nodes = NetManager.instance.m_nodes.m_buffer;
            float toStart = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_startNode].m_position);
            float toEnd = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_endNode].m_position);
            if (Mathf.Min(toStart, toEnd) > MaxNodeDistance) return 0;
            return toStart <= toEnd ? segment.m_startNode : segment.m_endNode;
        }

        protected override void OnToolGUI(Event e)
        {
            base.OnToolGUI(e);
            ushort node = hoverNode;

            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    Mod.Log("Esc pressed, back to the vanilla tool");
                    ToolsModifierControl.SetTool<DefaultTool>();
                }
                else if (node != 0 && (e.keyCode == KeyCode.Equals || e.keyCode == KeyCode.Plus || e.keyCode == KeyCode.KeypadPlus))
                {
                    e.Use();
                    SimulationManager.instance.AddAction(() => Junctions.AdjustCorner(node, CornerStep));
                }
                else if (e.keyCode == KeyCode.PageUp || e.keyCode == KeyCode.PageDown)
                {
                    // Asphalt tint, for all CS2-style junctions.
                    e.Use();
                    JunctionRenderer.AdjustShade(e.keyCode == KeyCode.PageUp ? 0.05f : -0.05f);
                }
                else if (node != 0 && (e.keyCode == KeyCode.Minus || e.keyCode == KeyCode.KeypadMinus))
                {
                    e.Use();
                    SimulationManager.instance.AddAction(() => Junctions.AdjustCorner(node, -CornerStep));
                }
                return;
            }

            if (e.type != EventType.MouseDown || e.button != 0 || m_toolController.IsInsideUI || node == 0) return;
            // Changes go through the simulation thread, like road building.
            SimulationManager.instance.AddAction(() => Junctions.Toggle(node));
            e.Use();
        }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);
            ushort node = hoverNode;
            if (node == 0) return;

            string reason;
            var color = Junctions.IsEnabled(node) ? OnColor : Junctions.CanStyle(node, out reason) ? CanColor : NoColor;
            var position = NetManager.instance.m_nodes.m_buffer[node].m_position;
            Singleton<RenderManager>.instance.OverlayEffect.DrawCircle(cameraInfo, color, position, 20f, -1f, 1280f, false, true);
        }

        private string InfoText()
        {
            ushort node = hoverNode;
            string text = "Junction tool: click a junction for the CS2 look (no crosswalks or median blocks,\n" +
                          "rounded sidewalk corners); click again for vanilla. + / -: bigger / smaller corners\n" +
                          "Page Up / Down: lighter / darker asphalt (now " + JunctionRenderer.Shade.ToString("F2") + ")";
            if (node == 0) return text;
            string reason;
            if (Junctions.IsEnabled(node))
                return text + "\nNode #" + node + ": CS2-style, corners " + Junctions.CornerExtra(node).ToString("F0") + " m further out";
            if (Junctions.CanStyle(node, out reason)) return text + "\nNode #" + node + ": vanilla (click to change)";
            return text + "\nNode #" + node + ": " + reason;
        }
    }
}
