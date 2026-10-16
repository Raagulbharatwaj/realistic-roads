using ColossalFramework;
using ICities;
using UnityEngine;

namespace RealisticRoads
{
    // Smooth split tool: hover a junction, click to turn the smooth split shape
    // on or off. Esc returns to the vanilla tool.
    //   green  = smoothed split
    //   yellow = a split that can be smoothed
    //   red    = not a split (the log says why when you click it)
    public class SplitTool : ToolBase
    {
        private const float MaxNodeDistance = 40f;

        private static readonly Color OnColor = new Color(0.2f, 1f, 0.3f, 0.8f);
        private static readonly Color CanColor = new Color(1f, 0.85f, 0.1f, 0.8f);
        private static readonly Color NoColor = new Color(1f, 0.25f, 0.2f, 0.6f);
        private static readonly Color CornerColor = new Color(0.2f, 0.8f, 1f, 0.9f);

        // Written on the main thread in OnToolUpdate, read by SimulationStep.
        private Ray mouseRay;
        private float mouseRayLength;
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
            mouseRay = camera.ScreenPointToRay(Input.mousePosition);
            mouseRayLength = camera.farClipPlane;
            mouseRayValid = !m_toolController.IsInsideUI && Cursor.visible;
        }

        public override void SimulationStep()
        {
            base.SimulationStep();
            hoverNode = mouseRayValid ? FindHoveredNode() : (ushort)0;
        }

        // Simulation thread. A node directly under the mouse, or else the
        // nearer end of the road under the mouse.
        private ushort FindHoveredNode()
        {
            var input = new RaycastInput(mouseRay, mouseRayLength);
            input.m_netService = new RaycastService { m_itemLayers = ItemClass.Layer.Default };
            input.m_ignoreTerrain = true;
            input.m_ignoreNodeFlags = NetNode.Flags.None;
            RaycastOutput output;
            if (RayCast(input, out output) && output.m_netNode != 0) return output.m_netNode;

            input = new RaycastInput(mouseRay, mouseRayLength);
            input.m_netService = new RaycastService { m_itemLayers = ItemClass.Layer.Default };
            input.m_ignoreTerrain = true;
            input.m_ignoreSegmentFlags = NetSegment.Flags.None;
            if (!RayCast(input, out output) || output.m_netSegment == 0) return 0;

            var segment = NetManager.instance.m_segments.m_buffer[output.m_netSegment];
            var nodes = NetManager.instance.m_nodes.m_buffer;
            float toStart = (output.m_hitPos - nodes[segment.m_startNode].m_position).magnitude;
            float toEnd = (output.m_hitPos - nodes[segment.m_endNode].m_position).magnitude;
            if (Mathf.Min(toStart, toEnd) > MaxNodeDistance) return 0;
            return toStart <= toEnd ? segment.m_startNode : segment.m_endNode;
        }

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

            if (e.type != EventType.MouseDown || e.button != 0 || m_toolController.IsInsideUI) return;
            ushort node = hoverNode;
            if (node == 0) return;
            SmoothSplits.Toggle(node);
            e.Use();
        }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);
            ushort node = hoverNode;
            if (node == 0) return;

            var overlay = Singleton<RenderManager>.instance.OverlayEffect;
            var position = NetManager.instance.m_nodes.m_buffer[node].m_position;

            SplitShape shape;
            string reason;
            bool isSplit = SmoothSplits.TryAnalyze(node, out shape, out reason);
            bool isOn = SmoothSplits.IsEnabled(node);
            var color = isOn ? OnColor : isSplit ? CanColor : NoColor;
            overlay.DrawCircle(cameraInfo, color, position, 20f, -1f, 1280f, false, true);

            if (!isSplit) return;

            // Show where the new corners go, so a bad shape is easy to spot.
            var segments = new[] { shape.Trunk, shape.LeftBranch, shape.RightBranch };
            foreach (var segment in segments)
            {
                for (int side = 0; side < 2; side++)
                {
                    Vector3 corner, direction;
                    if (SmoothSplits.TryGetCorner(segment, node, side == 0, out corner, out direction))
                        overlay.DrawCircle(cameraInfo, CornerColor, corner, 2.5f, -1f, 1280f, false, true);
                }
            }
        }
    }

    // Saves the list of smoothed nodes in the city save. If the mod is removed
    // the data is ignored and the nodes are plain vanilla junctions.
    public class SmoothSplitsData : SerializableDataExtensionBase
    {
        public override void OnLoadData()
        {
            try
            {
                SmoothSplits.Deserialize(serializableDataManager.LoadData(SmoothSplits.DataId));
            }
            catch (System.Exception e)
            {
                Mod.LogError("Loading smooth splits failed, starting with none", e);
                SmoothSplits.Clear();
            }
        }

        public override void OnSaveData()
        {
            try
            {
                serializableDataManager.SaveData(SmoothSplits.DataId, SmoothSplits.Serialize());
            }
            catch (System.Exception e)
            {
                Mod.LogError("Saving smooth splits failed", e);
            }
        }
    }
}
