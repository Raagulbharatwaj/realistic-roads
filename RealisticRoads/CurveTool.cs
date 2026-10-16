using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace RealisticRoads
{
    // CS2-style curve tool.
    //   Left-click        add a control point (snaps to an existing road end/junction;
    //                     the curve then continues that road smoothly). On the end
    //                     of a wider road it snaps to the half under the cursor: the
    //                     new road starts there, runs parallel, then curves away,
    //                     and a second branch on the other half makes a smooth split.
    //   Right-click       remove the last point
    //   Enter/double-click build the curve with the road picked in the Roads menu
    //   Ctrl+Z            undo the last curve built
    //   Esc               clear the points; Esc again leaves the tool
    // The preview follows the mouse: green where it can be built, red where a
    // bend is too sharp.
    public class CurveTool : ToolBase
    {
        public const float Spacing = 40f;       // target segment length
        public const float MinSpacing = 16f;    // shortest segment used to fit a tight bend
        public const float MaxBend = 30f;       // degrees one segment may turn
        private const float PointSize = 6f;
        private const float HoverSize = 4f;
        private const float MinPointSpacing = 2f;
        private const float SnapReach = 12f;    // snap to a road end this far beyond the road's edge

        private static readonly Color PointColor = new Color(0.2f, 0.8f, 1f, 0.9f);
        private static readonly Color SnapColor = new Color(1f, 0.85f, 0.1f, 0.9f);
        private static readonly Color HoverColor = new Color(1f, 1f, 1f, 0.6f);
        private static readonly Color GoodColor = new Color(0.25f, 0.9f, 0.35f, 0.55f);
        private static readonly Color BadColor = new Color(1f, 0.2f, 0.15f, 0.6f);
        private static readonly Color BridgeColor = new Color(0.25f, 0.6f, 1f, 0.55f);

        // Clicked control points and, for each, the existing node it snapped to
        // (or 0), the half of that road end it attaches to (+1 left, -1 right,
        // 0 = the node itself) and where to draw it.
        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<ushort> pointNodes = new List<ushort>();
        private readonly List<int> pointSides = new List<int>();
        private readonly List<Vector3> pointMarkers = new List<Vector3>();

        // Written on the main thread in OnToolUpdate, read by SimulationStep.
        private Ray mouseRay;
        private float mouseRayLength;
        private volatile bool mouseRayValid;

        // Written by SimulationStep (simulation thread), read on the main thread.
        private Vector3 hoverPosition;
        private volatile bool hoverValid;
        private volatile ushort hoverNode;
        private volatile int hoverSide;
        private Vector3 hoverMarker;

        // Preview, recomputed on the main thread every frame.
        private Division preview;

        // The road/track the player last picked in the vanilla Roads menu.
        public NetInfo SelectedNetwork { get; set; }

        public bool IsActive => ToolsModifierControl.toolController != null &&
                                ToolsModifierControl.toolController.CurrentTool == this;

        // A curve cut into buildable pieces.
        private class Division
        {
            public List<Vector3> Points;
            public List<Vector3> Tangents;
            public float Length;
            public bool Ok;
            public bool Continues;       // follows an existing road at the start or end
            public List<bool> OverWater; // per piece: will be a bridge
            public ushort StartSplit;    // trunk end the start is half-snapped to (or 0)
            public ushort EndSplit;      // trunk end the end is half-snapped to (or 0)
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            ClearPoints();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            ClearPoints();
            hoverValid = false;
            mouseRayValid = false;
            ToolActivator.SetInfo(null);
        }

        // Main thread, every frame while the tool is active.
        protected override void OnToolUpdate()
        {
            base.OnToolUpdate();
            var camera = Camera.main;
            if (camera != null)
            {
                mouseRay = camera.ScreenPointToRay(Input.mousePosition);
                mouseRayLength = camera.farClipPlane;
                mouseRayValid = !m_toolController.IsInsideUI && Cursor.visible;
            }
            else
            {
                mouseRayValid = false;
            }

            // Live preview: the clicked points plus the mouse position.
            var live = new List<Vector3>(points);
            var liveNodes = new List<ushort>(pointNodes);
            var liveSides = new List<int>(pointSides);
            if (hoverValid && mouseRayValid)
            {
                live.Add(hoverPosition);
                liveNodes.Add(hoverNode);
                liveSides.Add(hoverSide);
            }
            preview = Divide(live, liveNodes, liveSides);
            ToolActivator.SetInfo(InfoText());
        }

        // Simulation thread. Raycasting is done here, like the vanilla tools do.
        public override void SimulationStep()
        {
            base.SimulationStep();
            if (!mouseRayValid)
            {
                hoverValid = false;
                hoverNode = 0;
                hoverSide = 0;
                return;
            }

            Vector3 ground;
            bool onGround = GroundUnderMouse(out ground);

            ushort node = FindSnapNode();
            if (node != 0)
            {
                Vector3 position = NetManager.instance.m_nodes.m_buffer[node].m_position;
                NetworkBuilder.HalfSnap half;
                bool isHalf = NetworkBuilder.TryHalfSnap(node, SelectedNetwork, onGround ? ground : position, 0, out half);
                hoverMarker = isHalf ? NetworkBuilder.HalfSnapMarker(node, half) : position;
                hoverPosition = position;
                hoverSide = isHalf ? half.Side : 0;
                hoverNode = node;
                hoverValid = true;
                return;
            }

            hoverValid = onGround;
            hoverPosition = ground;
            hoverMarker = ground;
            hoverNode = 0;
            hoverSide = 0;
        }

        // Simulation thread. A node under the mouse, or the end of the road
        // under the mouse when the mouse is near that end.
        private ushort FindSnapNode()
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
            float reach = segment.Info.m_halfWidth + SnapReach;
            float toStart = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_startNode].m_position);
            float toEnd = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_endNode].m_position);
            if (toStart <= toEnd && toStart <= reach) return segment.m_startNode;
            if (toEnd < toStart && toEnd <= reach) return segment.m_endNode;
            return 0;
        }

        // Simulation thread. Where the mouse ray meets the ground or the water
        // surface, ignoring trees, buildings and props, so points can be placed
        // anywhere (the game's own raycast stops at the first object it hits).
        private bool GroundUnderMouse(out Vector3 hit)
        {
            var terrain = TerrainManager.instance;
            Vector3 origin = mouseRay.origin;
            Vector3 direction = mouseRay.direction;
            const float step = 8f;
            Vector3 previous = origin;
            for (float d = step; d <= mouseRayLength; d += step)
            {
                Vector3 p = origin + direction * d;
                if (p.y > terrain.SampleRawHeightSmoothWithWater(p, false, 0f))
                {
                    previous = p;
                    continue;
                }

                // Crossed the surface between 'previous' and 'p': narrow it down.
                Vector3 above = previous, below = p;
                for (int i = 0; i < 12; i++)
                {
                    Vector3 mid = (above + below) * 0.5f;
                    if (mid.y > terrain.SampleRawHeightSmoothWithWater(mid, false, 0f)) above = mid;
                    else below = mid;
                }
                hit = below;
                hit.y = terrain.SampleRawHeightSmoothWithWater(hit, false, 0f);
                return true;
            }
            hit = Vector3.zero;
            return false;
        }

        // Main thread, for mouse and keyboard events.
        protected override void OnToolGUI(Event e)
        {
            base.OnToolGUI(e);

            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    if (points.Count > 0)
                    {
                        ClearPoints();
                        Mod.Log("Curve cleared (Esc again leaves the tool)");
                    }
                    else
                    {
                        Mod.Log("Esc pressed, back to the vanilla tool");
                        ToolsModifierControl.SetTool<DefaultTool>();
                    }
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    e.Use();
                    BuildCurrent();
                }
                else if (e.keyCode == KeyCode.Z && e.control)
                {
                    e.Use();
                    NetworkBuilder.UndoLast();
                }
                return;
            }

            if (e.type != EventType.MouseDown || m_toolController.IsInsideUI) return;

            if (e.button == 0 && hoverValid)
            {
                e.Use();
                var p = hoverPosition;
                if (points.Count > 0 && (p - points[points.Count - 1]).sqrMagnitude < MinPointSpacing * MinPointSpacing)
                {
                    // Double-click (or a second click on the last point): build.
                    BuildCurrent();
                    return;
                }
                int side = hoverSide;
                points.Add(p);
                pointNodes.Add(hoverNode);
                pointSides.Add(side);
                pointMarkers.Add(hoverMarker);
                Mod.Log("Point " + points.Count + " at " + Format(p) +
                        (hoverNode != 0 ? " (on node #" + hoverNode + (side > 0 ? ", left half" : side < 0 ? ", right half" : "") + ")" : ""));
            }
            else if (e.button == 1 && points.Count > 0)
            {
                e.Use();
                points.RemoveAt(points.Count - 1);
                pointNodes.RemoveAt(pointNodes.Count - 1);
                pointSides.RemoveAt(pointSides.Count - 1);
                pointMarkers.RemoveAt(pointMarkers.Count - 1);
                Mod.Log("Removed last point, " + points.Count + " left");
            }
        }

        // Main thread. Draws the curve as it will be built, and the points.
        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);
            var overlay = Singleton<RenderManager>.instance.OverlayEffect;
            var division = preview;

            if (division != null && division.Points.Count >= 2)
            {
                float width = SelectedNetwork != null ? SelectedNetwork.m_halfWidth * 2f : 8f;
                for (int i = 1; i < division.Points.Count; i++)
                {
                    var bezier = PieceBezier(division, i);
                    float bend = CurveMath.BendAngle(division.Tangents[i - 1], division.Tangents[i]);
                    var color = bend > MaxBend ? BadColor : division.OverWater[i - 1] ? BridgeColor : GoodColor;
                    overlay.DrawBezier(cameraInfo, color, bezier, width, 100000f, -100000f, -1f, 1280f, false, true);
                }
            }

            for (int i = 0; i < points.Count; i++)
                overlay.DrawCircle(cameraInfo, pointNodes[i] != 0 ? SnapColor : PointColor, pointMarkers[i], PointSize, -1f, 1280f, false, true);

            if (hoverValid && mouseRayValid)
                overlay.DrawCircle(cameraInfo, hoverNode != 0 ? SnapColor : HoverColor, hoverMarker, HoverSize, -1f, 1280f, false, true);
        }

        private void BuildCurrent()
        {
            if (SelectedNetwork == null)
            {
                Mod.Log("Pick a road in the vanilla Roads menu first, then open the curve tool again");
                return;
            }
            if (points.Count < 2)
            {
                Mod.Log("Need at least 2 points to build");
                return;
            }

            var division = Divide(points, pointNodes, pointSides);
            if (!division.Ok)
            {
                Mod.Log("Not built: a bend is too sharp (over " + MaxBend + " degrees per segment). Spread the points out.");
                return;
            }

            ushort startNode = pointNodes[0];
            ushort endNode = pointNodes[pointNodes.Count - 1];
            Mod.Log("Building " + SelectedNetwork.name + ": " + division.Length.ToString("F0") + " m in " +
                    (division.Points.Count - 1) + " segment(s)");
            NetworkBuilder.BuildCurve(SelectedNetwork, division.Points, division.Tangents, startNode, endNode,
                division.StartSplit, division.EndSplit);
            ClearPoints();
        }

        // Cuts the curve through 'controls' into buildable pieces. Where an end
        // is snapped to an existing road, the curve continues that road
        // smoothly; if that would bend too much it meets the road at an angle
        // instead (like a normal junction). Where an end is half-snapped to a
        // wider road, the curve runs parallel from that half first.
        private Division Divide(List<Vector3> controls, List<ushort> nodes, List<int> sides)
        {
            if (controls.Count < 2) return new Division { Points = new List<Vector3>(), Tangents = new List<Vector3>(), OverWater = new List<bool>() };

            int n = controls.Count - 1;
            var shaped = new List<Vector3>(controls);
            var startHalf = default(NetworkBuilder.HalfSnap);
            var endHalf = default(NetworkBuilder.HalfSnap);
            bool halfStart = sides[0] != 0 && NetworkBuilder.TryHalfSnap(nodes[0], SelectedNetwork, Vector3.zero, sides[0], out startHalf);
            bool halfEnd = sides[n] != 0 && NetworkBuilder.TryHalfSnap(nodes[n], SelectedNetwork, Vector3.zero, sides[n], out endHalf);
            if (halfStart) shaped[0] = NetworkBuilder.HalfSnapRunIn(nodes[0], startHalf);
            if (halfEnd) shaped[n] = NetworkBuilder.HalfSnapRunIn(nodes[n], endHalf);

            Vector3 startDir = halfStart ? startHalf.Forward
                : NetworkBuilder.ContinuationDirection(nodes[0], shaped[1] - shaped[0], true);
            Vector3 endDir = halfEnd ? -endHalf.Forward
                : NetworkBuilder.ContinuationDirection(nodes[n], shaped[n] - shaped[n - 1], false);

            var division = DivideShaped(shaped, startDir, endDir);
            division.Continues = (!halfStart && startDir != Vector3.zero) || (!halfEnd && endDir != Vector3.zero);
            if (!division.Ok && division.Continues)
            {
                // Drop only the optional continuations; half-snapped ends must stay parallel.
                var plain = DivideShaped(shaped, halfStart ? startDir : Vector3.zero, halfEnd ? endDir : Vector3.zero);
                if (plain.Ok)
                {
                    division = plain;
                    division.Continues = false;
                }
            }

            // Half-snapped ends: add the short parallel piece from the trunk end.
            if (halfStart && division.Points.Count > 0)
            {
                division.Points.Insert(0, controls[0]);
                division.Tangents.Insert(0, startHalf.Forward);
                division.StartSplit = nodes[0];
            }
            if (halfEnd && division.Points.Count > 0)
            {
                division.Points.Add(controls[n]);
                division.Tangents.Add(-endHalf.Forward);
                division.EndSplit = nodes[n];
            }

            division.OverWater = new List<bool>();
            for (int i = 1; i < division.Points.Count; i++)
                division.OverWater.Add(NetworkBuilder.IsOverWater(division.Points[i - 1]) ||
                                       NetworkBuilder.IsOverWater(division.Points[i]) ||
                                       NetworkBuilder.IsOverWater((division.Points[i - 1] + division.Points[i]) * 0.5f));
            return division;
        }

        private static Division DivideShaped(List<Vector3> controls, Vector3 startDir, Vector3 endDir)
        {
            var division = new Division { Points = new List<Vector3>(), Tangents = new List<Vector3>() };
            var polyline = CurveMath.SampleShaped(controls, startDir, endDir);
            division.Length = CurveMath.Length(polyline);
            if (division.Length < 1f) return division;

            bool ok;
            int count = CurveMath.ChoosePieceCount(polyline, Spacing, MinSpacing, MaxBend, out ok);
            CurveMath.Divide(polyline, count, out division.Points, out division.Tangents);
            if (startDir != Vector3.zero) division.Tangents[0] = CurveMath.FlatNormal(startDir);
            if (endDir != Vector3.zero) division.Tangents[division.Tangents.Count - 1] = CurveMath.FlatNormal(endDir);
            division.Ok = CurveMath.MaxBend(division.Tangents) <= MaxBend;
            return division;
        }

        // The Bezier the game will build for piece i (between points i-1 and i).
        private static Bezier3 PieceBezier(Division division, int i)
        {
            var bezier = new Bezier3();
            bezier.a = division.Points[i - 1];
            bezier.d = division.Points[i];
            NetSegment.CalculateMiddlePoints(bezier.a, division.Tangents[i - 1], bezier.d, -division.Tangents[i],
                false, false, out bezier.b, out bezier.c);
            return bezier;
        }

        private string InfoText()
        {
            string road = SelectedNetwork != null ? SelectedNetwork.name : "no road picked (choose one in the Roads menu)";
            var division = preview;
            if (division == null || division.Points.Count < 2)
                return "Curve tool: " + road + "\nClick to place points";
            int bridges = 0;
            foreach (bool water in division.OverWater) if (water) bridges++;
            return "Curve tool: " + road +
                   "\n" + division.Length.ToString("F0") + " m, " + (division.Points.Count - 1) + " segment(s)" +
                   (bridges > 0 ? ", bridge over water" : "") +
                   (division.Continues ? ", continues the road" : "") +
                   (division.StartSplit != 0 || division.EndSplit != 0 ? ", branches off a road half" : "") +
                   (division.Ok ? "" : "  -  TOO SHARP") +
                   "\nEnter / double-click: build   Ctrl+Z: undo   Esc: clear";
        }

        private void ClearPoints()
        {
            points.Clear();
            pointNodes.Clear();
            pointSides.Clear();
            pointMarkers.Clear();
            preview = null;
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F1") + ", " + v.y.ToString("F1") + ", " + v.z.ToString("F1") + ")";
        }
    }
}
