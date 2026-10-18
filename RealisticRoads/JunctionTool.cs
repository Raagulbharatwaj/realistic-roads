using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace RealisticRoads
{
    // Junction tool, two modes (Tab switches):
    //
    // Look: hover a junction, click to switch the CS2 look on or off, + / - to
    // make its corners bigger or smaller.
    //   green  = CS2-style junction
    //   yellow = a junction that can get it
    //   red    = can't (the log says why when you click it)
    //
    // Grade separation: click a junction, click lanes on one of its roads
    // (all going the same way), drag the split point along the road and the
    // circle at the junction up or down for the deck height, Enter builds.
    // The lanes split off, rise and fly over the junction to the road
    // straight across; the others stay on the ground beside them.
    //
    // Esc steps back (then leaves). Ctrl+Z undoes this tool's own last change:
    // the last look switched (look mode) or the last grade separation.
    public class JunctionTool : ToolBase
    {
        private const float MaxNodeDistance = 40f;
        private const float CornerStep = 1f;
        private const float HandlePixels = 28f;
        private const float HeightPerPixel = 0.05f;
        private const float DistanceStep = 10f;
        private const float HeightStep = 1f;

        private static readonly Color OnColor = new Color(0.2f, 1f, 0.3f, 0.8f);
        private static readonly Color CanColor = new Color(1f, 0.85f, 0.1f, 0.8f);
        private static readonly Color NoColor = new Color(1f, 0.25f, 0.2f, 0.6f);
        private static readonly Color InColor = new Color(1f, 0.85f, 0.1f, 0.6f);
        private static readonly Color OutColor = new Color(0.8f, 0.8f, 0.8f, 0.45f);
        private static readonly Color PickedColor = new Color(0.1f, 0.9f, 1f, 0.9f);
        private static readonly Color GroundColor = new Color(0.2f, 1f, 0.3f, 0.5f);
        private static readonly Color RaisedColor = new Color(0.3f, 0.5f, 1f, 0.6f);
        private static readonly Color HandleColor = new Color(1f, 1f, 1f, 0.9f);

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
        private Vector3 hoverPoint;      // under rayLock
        private bool hoverPointValid;    // under rayLock

        // Grade separation mode; main thread only.
        private bool gradeMode;
        private ushort gsJunction;
        private readonly List<ushort> toggled = new List<ushort>();   // look mode: junctions switched, for Ctrl+Z
        private List<Leg> gsLegs = new List<Leg>();
        private Leg gsLeg;
        private int gsFrom = -1, gsTo = -1;
        private float gsDistance = 150f, gsHeight = GradeSeparation.DefaultHeight;
        private GradePlan gsPlan;
        private string gsError;
        private enum Drag { None, Distance, Height }
        private Drag drag;
        private float dragStartY, dragStartHeight;

        public bool IsActive => ToolsModifierControl.toolController != null &&
                                ToolsModifierControl.toolController.CurrentTool == this;

        protected override void OnDisable()
        {
            base.OnDisable();
            hoverNode = 0;
            mouseRayValid = false;
            drag = Drag.None;
            ClearJunction();
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
            if (gradeMode) UpdateDrag();
            ToolActivator.SetInfo(gradeMode ? GradeText() : InfoText());
        }

        public override void SimulationStep()
        {
            base.SimulationStep();
            lock (rayLock)
            {
                simRay = mouseRay;
                simRayLength = mouseRayLength;
            }
            Vector3 point = Vector3.zero;
            bool valid = false;
            hoverNode = mouseRayValid ? FindHoveredNode(out point, out valid) : (ushort)0;
            lock (rayLock)
            {
                hoverPoint = point;
                hoverPointValid = valid;
            }
        }

        // Simulation thread. A node under the mouse, or the nearer end of the
        // road under the mouse; and the point under the mouse (road or ground).
        private ushort FindHoveredNode(out Vector3 point, out bool pointValid)
        {
            point = Vector3.zero;
            pointValid = false;
            var input = new RaycastInput(simRay, simRayLength);
            input.m_netService = new RaycastService { m_itemLayers = ItemClass.Layer.Default };
            input.m_ignoreTerrain = true;
            input.m_ignoreNodeFlags = NetNode.Flags.None;
            RaycastOutput output;
            ushort found = 0;
            if (RayCast(input, out output) && output.m_netNode != 0)
            {
                found = output.m_netNode;
                point = output.m_hitPos;
                pointValid = true;
            }

            input = new RaycastInput(simRay, simRayLength);
            input.m_netService = new RaycastService { m_itemLayers = ItemClass.Layer.Default };
            input.m_ignoreTerrain = true;
            input.m_ignoreSegmentFlags = NetSegment.Flags.None;
            if (RayCast(input, out output) && output.m_netSegment != 0)
            {
                point = output.m_hitPos;
                pointValid = true;
                if (found != 0) return found;
                var segment = NetManager.instance.m_segments.m_buffer[output.m_netSegment];
                var nodes = NetManager.instance.m_nodes.m_buffer;
                float toStart = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_startNode].m_position);
                float toEnd = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_endNode].m_position);
                if (Mathf.Min(toStart, toEnd) > MaxNodeDistance) return 0;
                return toStart <= toEnd ? segment.m_startNode : segment.m_endNode;
            }
            if (found != 0) return found;

            // Nothing there: the ground under the mouse, for dragging the split point.
            input = new RaycastInput(simRay, simRayLength);
            input.m_ignoreTerrain = false;
            if (RayCast(input, out output))
            {
                point = output.m_hitPos;
                pointValid = true;
            }
            return 0;
        }

        protected override void OnToolGUI(Event e)
        {
            base.OnToolGUI(e);
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Tab)
            {
                e.Use();
                gradeMode = !gradeMode;
                ClearJunction();
                Mod.Log(gradeMode ? "Junction tool: grade separation" : "Junction tool: junction look");
                return;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Z && e.control)
            {
                e.Use();
                if (gradeMode)
                {
                    NetworkBuilder.UndoLastGradeSeparation();
                    ClearJunction();
                }
                else if (toggled.Count > 0)
                {
                    ushort last = toggled[toggled.Count - 1];
                    toggled.RemoveAt(toggled.Count - 1);
                    SimulationManager.instance.AddAction(() => Junctions.Toggle(last));
                }
                else Mod.Log("Nothing to undo: no junction look switched in this session");
                return;
            }
            if (gradeMode) GradeGUI(e);
            else LookGUI(e);
        }

        private void LookGUI(Event e)
        {
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
            toggled.Add(node);
            if (toggled.Count > 50) toggled.RemoveAt(0);
            e.Use();
        }

        // ---- Grade separation ----

        private void GradeGUI(Event e)
        {
            if (e.type == EventType.KeyDown)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Escape:
                        e.Use();
                        if (gsFrom >= 0) ClearGroup();
                        else if (gsJunction != 0) ClearJunction();
                        else
                        {
                            Mod.Log("Esc pressed, back to the vanilla tool");
                            ToolsModifierControl.SetTool<DefaultTool>();
                        }
                        break;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        e.Use();
                        BuildGrade();
                        break;
                    case KeyCode.UpArrow:
                    case KeyCode.DownArrow:
                        e.Use();
                        gsHeight = Mathf.Clamp(gsHeight + (e.keyCode == KeyCode.UpArrow ? HeightStep : -HeightStep),
                            GradeSeparation.MinHeight, GradeSeparation.MaxHeight);
                        Replan();
                        break;
                    case KeyCode.LeftBracket:
                    case KeyCode.RightBracket:
                        e.Use();
                        gsDistance += e.keyCode == KeyCode.RightBracket ? DistanceStep : -DistanceStep;
                        Replan();
                        break;
                }
                return;
            }
            if (e.type != EventType.MouseDown || m_toolController.IsInsideUI) return;

            if (e.button == 1)
            {
                e.Use();
                if (gsFrom >= 0) ClearGroup(); else ClearJunction();
                return;
            }
            if (e.button != 0) return;
            e.Use();

            // The handles first.
            if (gsPlan != null && gsPlan.Pieces.Count > 0)
            {
                if (NearOnScreen(gsPlan.ApproachCut))
                {
                    drag = Drag.Distance;
                    return;
                }
                if (NearOnScreen(new Vector3(gsPlan.Peak.x, NetManager.instance.m_nodes.m_buffer[gsJunction].m_position.y, gsPlan.Peak.z)))
                {
                    drag = Drag.Height;
                    dragStartY = Input.mousePosition.y;
                    dragStartHeight = gsHeight;
                    return;
                }
            }

            Vector3 point;
            bool valid;
            lock (rayLock)
            {
                point = hoverPoint;
                valid = hoverPointValid;
            }
            Leg leg;
            int lane;
            if (gsJunction != 0 && valid && FindLane(point, out leg, out lane))
            {
                PickLane(leg, lane);
                return;
            }

            ushort node = hoverNode;
            if (node != 0 && node != gsJunction)
            {
                if (NetworkBuilder.SegmentCount(node) < 3)
                {
                    Mod.Log("Node #" + node + " is not a junction (it joins " + NetworkBuilder.SegmentCount(node) + " road(s))");
                    return;
                }
                SelectJunction(node);
            }
        }

        private void SelectJunction(ushort node)
        {
            ClearJunction();
            try
            {
                gsJunction = node;
                gsLegs = GradeSeparation.Legs(node);
            }
            catch (Exception e)
            {
                Mod.LogError("Reading the roads at junction #" + node + " failed", e);
                ClearJunction();
            }
        }

        private void ClearJunction()
        {
            gsJunction = 0;
            gsLegs = new List<Leg>();
            ClearGroup();
        }

        private void ClearGroup()
        {
            gsLeg = null;
            gsFrom = gsTo = -1;
            gsPlan = null;
            gsError = null;
            drag = Drag.None;
        }

        // A click on lane 'k' of 'leg': start a group there, or grow / shrink
        // the group (it stays one run of lanes going the same way).
        private void PickLane(Leg leg, int k)
        {
            var lanes = leg.Lanes;
            if (leg != gsLeg || gsFrom < 0 || lanes[k].Forward != lanes[gsFrom].Forward)
            {
                gsLeg = leg;
                gsFrom = gsTo = k;
            }
            else if (k >= gsFrom && k <= gsTo)
            {
                if (gsFrom == gsTo) { ClearGroup(); return; }
                if (k == gsFrom) gsFrom++;
                else if (k == gsTo) gsTo--;
                else gsFrom = gsTo = k;
            }
            else
            {
                int a = Mathf.Min(gsFrom, k), b = Mathf.Max(gsTo, k);
                if (GradeSeparation.IsGroup(leg, a, b)) { gsFrom = a; gsTo = b; }
                else gsFrom = gsTo = k;
            }
            Replan();
        }

        private void Replan()
        {
            if (gsLeg == null || gsFrom < 0)
            {
                gsPlan = null;
                return;
            }
            try
            {
                var far = GradeSeparation.FarLeg(gsLegs, gsLeg, gsLeg.Lanes[gsFrom].Forward);
                gsPlan = GradeSeparation.Make(gsJunction, gsLeg, far, gsFrom, gsTo, gsDistance, gsHeight);
                if (gsPlan.Pieces.Count > 0) gsDistance = gsPlan.Distance;
                gsHeight = gsPlan.Height;
                gsError = null;
            }
            catch (Exception e)
            {
                gsPlan = null;
                // Once per selection, not every frame of a drag.
                if (gsError == null) Mod.LogError("Planning the grade separation failed", e);
                gsError = e.Message;
            }
        }

        private void UpdateDrag()
        {
            if (drag == Drag.None) return;
            if (!Input.GetMouseButton(0) || gsLeg == null)
            {
                drag = Drag.None;
                return;
            }
            if (drag == Drag.Height)
            {
                float height = Mathf.Clamp(dragStartHeight + (Input.mousePosition.y - dragStartY) * HeightPerPixel,
                    GradeSeparation.MinHeight, GradeSeparation.MaxHeight);
                if (Mathf.Abs(height - gsHeight) < 0.05f) return;
                gsHeight = height;
                Replan();
                return;
            }
            Vector3 point;
            bool valid;
            lock (rayLock)
            {
                point = hoverPoint;
                valid = hoverPointValid;
            }
            if (!valid) return;
            float distance = DistanceAlong(gsLeg.Path, point);
            if (Mathf.Abs(distance - gsDistance) < 1f) return;
            gsDistance = distance;
            Replan();
        }

        // How far along 'path' the point nearest to 'point' is.
        private static float DistanceAlong(List<Vector3> path, Vector3 point)
        {
            float best = float.MaxValue, at = 0f, travelled = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                Vector3 a = path[i - 1], b = path[i];
                float length = CurveMath.FlatDistance(a, b);
                float t = 0f;
                if (length > 0.001f)
                    t = Mathf.Clamp01(((point.x - a.x) * (b.x - a.x) + (point.z - a.z) * (b.z - a.z)) / (length * length));
                Vector3 closest = Vector3.Lerp(a, b, t);
                float distance = CurveMath.FlatDistance(closest, point);
                if (distance < best)
                {
                    best = distance;
                    at = travelled + t * length;
                }
                travelled += length;
            }
            return at;
        }

        private static bool NearOnScreen(Vector3 world)
        {
            var camera = Camera.main;
            if (camera == null) return false;
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f) return false;
            Vector3 mouse = Input.mousePosition;
            float dx = screen.x - mouse.x, dy = screen.y - mouse.y;
            return dx * dx + dy * dy < HandlePixels * HandlePixels;
        }

        // The car lane of the selected junction's roads under 'point'.
        private bool FindLane(Vector3 point, out Leg found, out int index)
        {
            found = null;
            index = -1;
            float best = float.MaxValue;
            var lanes = NetManager.instance.m_lanes.m_buffer;
            foreach (var leg in gsLegs)
                for (int i = 0; i < leg.Lanes.Count; i++)
                {
                    var bezier = lanes[leg.Lanes[i].LaneId].m_bezier;
                    for (int k = 0; k <= 16; k++)
                    {
                        float distance = CurveMath.FlatDistance(bezier.Position(k / 16f), point);
                        if (distance < best && distance < leg.Lanes[i].Width * 0.5f + 0.5f)
                        {
                            best = distance;
                            found = leg;
                            index = i;
                        }
                    }
                }
            return found != null;
        }

        private void BuildGrade()
        {
            if (gsPlan == null)
            {
                Mod.Log("Grade separation: pick a junction and the lanes to raise first");
                return;
            }
            if (!gsPlan.Ok)
            {
                Mod.Log("Grade separation not built: " + string.Join("; ", gsPlan.Problems.ToArray()));
                return;
            }
            Mod.Log("Building a grade separation at node #" + gsJunction + ": " +
                    GradeSeparation.Describe(gsLeg, gsFrom, gsTo) + " raised, " + gsPlan.Pieces.Count + " road(s)");
            NetworkBuilder.BuildGradeSeparation(gsPlan);
            // The junction's roads change: start again from the junction.
            ClearJunction();
        }

        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);
            if (gradeMode)
            {
                RenderGrade(cameraInfo);
                return;
            }
            ushort node = hoverNode;
            if (node == 0) return;

            string reason;
            var color = Junctions.IsEnabled(node) ? OnColor : Junctions.CanStyle(node, out reason) ? CanColor : NoColor;
            var position = NetManager.instance.m_nodes.m_buffer[node].m_position;
            Singleton<RenderManager>.instance.OverlayEffect.DrawCircle(cameraInfo, color, position, 20f, -1f, 1280f, false, true);
        }

        private void RenderGrade(RenderManager.CameraInfo cameraInfo)
        {
            var overlay = Singleton<RenderManager>.instance.OverlayEffect;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            ushort hover = hoverNode;
            if (hover != 0 && hover != gsJunction && NetworkBuilder.SegmentCount(hover) >= 3)
                overlay.DrawCircle(cameraInfo, CanColor, nodes[hover].m_position, 20f, -1f, 1280f, false, true);
            if (gsJunction == 0) return;
            overlay.DrawCircle(cameraInfo, HandleColor, nodes[gsJunction].m_position, 8f, -1f, 1280f, false, true);

            var lanes = NetManager.instance.m_lanes.m_buffer;
            foreach (var leg in gsLegs)
                for (int i = 0; i < leg.Lanes.Count; i++)
                {
                    var lane = leg.Lanes[i];
                    bool picked = leg == gsLeg && i >= gsFrom && i <= gsTo;
                    var color = picked ? PickedColor : lane.Forward ? InColor : OutColor;
                    overlay.DrawBezier(cameraInfo, color, lanes[lane.LaneId].m_bezier, lane.Width * 0.6f, 100000f, -100000f, -1f, 1280f, false, true);
                }

            var plan = gsPlan;
            if (plan == null) return;
            foreach (var part in plan.Parts)
            {
                var preview = part.Preview;
                float width = part.Info != null ? (part.Raised ? GradeSeparation.Deck(part.Info) : part.Info).m_halfWidth * 2f : 4f;
                var color = !plan.Ok ? NoColor : part.Raised ? RaisedColor : GroundColor;
                for (int i = 1; i < preview.Count; i++)
                {
                    Vector3 a = preview[i - 1], d = preview[i];
                    var bezier = new Bezier3(a, a + (d - a) * (1f / 3f), a + (d - a) * (2f / 3f), d);
                    overlay.DrawBezier(cameraInfo, color, bezier, width, 100000f, -100000f, -1f, 1280f, false, true);
                }
            }
            if (plan.Pieces.Count > 0)
            {
                // Handles: the split point (drag along the road), its mirror on the
                // far side, and the deck height (drag up / down) at the junction.
                overlay.DrawCircle(cameraInfo, HandleColor, plan.ApproachCut, 10f, -1f, 1280f, false, true);
                overlay.DrawCircle(cameraInfo, OutColor, plan.FarCut, 8f, -1f, 1280f, false, true);
                var peak = new Vector3(plan.Peak.x, nodes[gsJunction].m_position.y, plan.Peak.z);
                overlay.DrawCircle(cameraInfo, PickedColor, peak, 10f, -1f, 1280f, false, true);
            }
        }

        private string GradeText()
        {
            const string keys = "Tab: junction look  ·  Esc: back  ·  Ctrl+Z: undo the last grade separation";
            if (gsJunction == 0)
                return "Grade separation: click a junction.\n" + keys;
            if (gsLeg == null || gsFrom < 0)
                return "Grade separation at node #" + gsJunction + ": click the lanes to raise on one of its roads\n" +
                       "(yellow lanes go into the junction, grey ones out; all picked lanes must go the same way).\n" + keys;
            var lines = new List<string>();
            lines.Add("Grade separation at node #" + gsJunction + ": " + GradeSeparation.Describe(gsLeg, gsFrom, gsTo) + " raised");
            if (gsError != null) lines.Add("Can't plan it: " + gsError + " (see the log)");
            var plan = gsPlan;
            if (plan != null)
            {
                if (plan.Pieces.Count > 0)
                    lines.Add("Split " + plan.Distance.ToString("F0") + " m out (" + plan.MinDistance.ToString("F0") + "-" +
                              plan.MaxDistance.ToString("F0") + " m), deck " + plan.Height.ToString("F0") +
                              " m above the junction, ramps at up to " + (RoadPlanner.RoadGrade * 100f).ToString("F0") + " %");
                foreach (var part in plan.Parts)
                {
                    if (part.Info == null) continue;
                    var info = part.Raised ? GradeSeparation.Deck(part.Info) : part.Info;
                    lines.Add("  " + part.Name + ": " + info.name + " (" + part.Forward + " lane(s) on, " + part.Backward + " back)");
                }
                foreach (var problem in plan.Problems) lines.Add("Problem: " + problem);
                foreach (var warning in plan.Warnings) lines.Add("Note: " + warning);
            }
            lines.Add("Enter: build  ·  drag the white circle along the road ([ / ]) for the split  ·  " +
                      "drag the blue circle at the junction up / down (Up / Down) for the height");
            lines.Add(keys);
            return string.Join("\n", lines.ToArray());
        }

        private string InfoText()
        {
            ushort node = hoverNode;
            string text = "Junction tool: click a junction for the CS2 look (no crosswalks or median blocks,\n" +
                          "rounded sidewalk corners); click again for vanilla. + / -: bigger / smaller corners\n" +
                          "Page Up / Down: lighter / darker asphalt (now " + JunctionRenderer.Shade.ToString("F2") + ")\n" +
                          "Ctrl+Z: switch the last junction back  ·  Tab: grade separation (raise lanes over the junction)";
            if (node == 0) return text;
            string reason;
            if (Junctions.IsEnabled(node))
                return text + "\nNode #" + node + ": CS2-style, corners " + Junctions.CornerExtra(node).ToString("F0") + " m further out";
            if (Junctions.CanStyle(node, out reason)) return text + "\nNode #" + node + ": vanilla (click to change)";
            return text + "\nNode #" + node + ": " + reason;
        }
    }
}
