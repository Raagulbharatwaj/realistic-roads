using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
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
    // The preview follows the mouse: green on the ground, blue raised (bridge /
    // flyover), orange lowered (tunnel / cutting), red where a bend is too sharp.
    // Where it crosses an existing road or track, UP/DN buttons choose how:
    //   same type:      tunnel  < junction (default) < flyover
    //   different type: underpass (cutting, existing on a short bridge) < level crossing (default) < overpass
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
        private static readonly Color LowColor = new Color(1f, 0.6f, 0.15f, 0.55f);
        private static readonly Color JunctionColor = new Color(1f, 1f, 1f, 0.8f);
        private const int MaxCrossingButtons = 12;

        // Chosen level per crossed existing segment (default: Same).
        private readonly Dictionary<ushort, CrossingLevel> crossingLevels = new Dictionary<ushort, CrossingLevel>();
        private readonly List<UIButton> upButtons = new List<UIButton>();
        private readonly List<UIButton> downButtons = new List<UIButton>();
        private readonly ushort[] buttonSegments = new ushort[MaxCrossingButtons];

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
            public Plan Plan;            // heights, road versions, crossings, problems
            public List<Crossing> Crossings = new List<Crossing>();
            public List<int> CrossingIndex = new List<int>();
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
            HideCrossingButtons();
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
            PlaceCrossingButtons();
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

            if (division != null && division.Points.Count >= 2 && division.Plan != null)
            {
                float width = SelectedNetwork != null ? SelectedNetwork.m_halfWidth * 2f : 8f;
                var kinds = division.Plan.Kinds;
                for (int i = 1; i < division.Points.Count; i++)
                {
                    var bezier = PieceBezier(division, i);
                    float bend = CurveMath.BendAngle(division.Tangents[i - 1], division.Tangents[i]);
                    Color color;
                    if (bend > MaxBend) color = BadColor;
                    else if (IsRaised(kinds[i - 1]) || IsRaised(kinds[i])) color = BridgeColor;
                    else if (IsLowered(kinds[i - 1]) || IsLowered(kinds[i])) color = LowColor;
                    else color = GoodColor;
                    overlay.DrawBezier(cameraInfo, color, bezier, width, 100000f, -100000f, -1f, 1280f, false, true);
                }

                var problems = !division.Plan.Ok;
                foreach (var crossing in division.Crossings)
                {
                    Color color = problems ? BadColor
                        : crossing.Level == CrossingLevel.Over ? BridgeColor
                        : crossing.Level == CrossingLevel.Under ? LowColor
                        : JunctionColor;
                    overlay.DrawCircle(cameraInfo, color, crossing.Point, width + 6f, -1f, 1280f, false, true);
                }
            }

            for (int i = 0; i < points.Count; i++)
                overlay.DrawCircle(cameraInfo, pointNodes[i] != 0 ? SnapColor : PointColor, pointMarkers[i], PointSize, -1f, 1280f, false, true);

            if (hoverValid && mouseRayValid)
                overlay.DrawCircle(cameraInfo, hoverNode != 0 ? SnapColor : HoverColor, hoverMarker, HoverSize, -1f, 1280f, false, true);
        }

        private static bool IsRaised(NodeKind kind)
        {
            return kind == NodeKind.Bridge || kind == NodeKind.Elevated;
        }

        private static bool IsLowered(NodeKind kind)
        {
            return kind == NodeKind.Cut || kind == NodeKind.Tunnel;
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
            if (!division.Plan.Ok)
            {
                Mod.Log("Not built: " + string.Join("; ", division.Plan.Problems.ToArray()));
                return;
            }

            Mod.Log("Building " + SelectedNetwork.name + ": " + division.Length.ToString("F0") + " m in " +
                    (division.Points.Count - 1) + " segment(s), " + division.Crossings.Count + " crossing(s)");
            NetworkBuilder.BuildCurve(SelectedNetwork, division.Plan, division.StartSplit, division.EndSplit);
            ClearPoints();
        }

        // Cuts the curve through 'controls' into buildable pieces. Where an end
        // is snapped to an existing road, the curve continues that road
        // smoothly; if that would bend too much it meets the road at an angle
        // instead (like a normal junction). Where an end is half-snapped to a
        // wider road, the curve runs parallel from that half first. Crossings
        // with existing roads get a node exactly on them, and RoadPlanner works
        // out the heights.
        private Division Divide(List<Vector3> controls, List<ushort> nodes, List<int> sides)
        {
            if (controls.Count < 2) return new Division { Points = new List<Vector3>(), Tangents = new List<Vector3>() };

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

            var division = DivideShaped(shaped, startDir, endDir, nodes[0], nodes[n]);
            division.Continues = (!halfStart && startDir != Vector3.zero) || (!halfEnd && endDir != Vector3.zero);
            if (!division.Ok && division.Continues)
            {
                // Drop only the optional continuations; half-snapped ends must stay parallel.
                var plain = DivideShaped(shaped, halfStart ? startDir : Vector3.zero, halfEnd ? endDir : Vector3.zero, nodes[0], nodes[n]);
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
                for (int i = 0; i < division.CrossingIndex.Count; i++) division.CrossingIndex[i]++;
            }
            if (halfEnd && division.Points.Count > 0)
            {
                division.Points.Add(controls[n]);
                division.Tangents.Add(-endHalf.Forward);
                division.EndSplit = nodes[n];
            }

            division.Plan = RoadPlanner.Make(SelectedNetwork, division.Points, division.Tangents,
                division.Crossings, division.CrossingIndex, nodes[0], nodes[n]);
            return division;
        }

        private Division DivideShaped(List<Vector3> controls, Vector3 startDir, Vector3 endDir, ushort startNode, ushort endNode)
        {
            var division = new Division { Points = new List<Vector3>(), Tangents = new List<Vector3>() };
            var polyline = CurveMath.SampleShaped(controls, startDir, endDir);
            division.Length = CurveMath.Length(polyline);
            if (division.Length < 1f) return division;

            // Crossings become fixed nodes; the curve is divided between them.
            var crossings = CrossingFinder.Find(polyline, SelectedNetwork, startNode, endNode);
            var cuts = new List<float> { 0f };
            foreach (var crossing in crossings)
            {
                if (crossing.Distance < MinSpacing * 0.5f || crossing.Distance > division.Length - MinSpacing * 0.5f) continue;
                if (crossing.Distance - cuts[cuts.Count - 1] < 4f) continue;
                CrossingLevel level;
                crossing.Level = crossingLevels.TryGetValue(crossing.Segment, out level) ? level : CrossingLevel.Same;
                division.Crossings.Add(crossing);
                cuts.Add(crossing.Distance);
            }
            cuts.Add(division.Length);

            for (int c = 1; c < cuts.Count; c++)
            {
                var slice = CurveMath.Slice(polyline, cuts[c - 1], cuts[c]);
                bool ignored;
                int count = CurveMath.ChoosePieceCount(slice, Spacing, MinSpacing, MaxBend, out ignored);
                List<Vector3> slicePoints, sliceTangents;
                CurveMath.Divide(slice, count, out slicePoints, out sliceTangents);
                int skip = division.Points.Count == 0 ? 0 : 1;
                for (int i = skip; i < slicePoints.Count; i++)
                {
                    division.Points.Add(slicePoints[i]);
                    division.Tangents.Add(sliceTangents[i]);
                }
                if (c < cuts.Count - 1)
                {
                    // Node on the crossing: direction from the whole curve, not one side of it.
                    int index = division.Points.Count - 1;
                    Vector3 point, tangent;
                    CurveMath.PointAt(polyline, cuts[c], out point, out tangent);
                    if (tangent != Vector3.zero) division.Tangents[index] = tangent;
                    division.CrossingIndex.Add(index);
                }
            }

            if (startDir != Vector3.zero) division.Tangents[0] = CurveMath.FlatNormal(startDir);
            if (endDir != Vector3.zero) division.Tangents[division.Tangents.Count - 1] = CurveMath.FlatNormal(endDir);
            division.Ok = CurveMath.MaxBend(division.Tangents) <= MaxBend;
            return division;
        }

        // --- crossing buttons ----------------------------------------------------

        // Main thread. UP/DN buttons next to each crossing in the preview.
        private void PlaceCrossingButtons()
        {
            var division = preview;
            var camera = Camera.main;
            var view = UIView.GetAView();
            int shown = 0;
            if (division != null && camera != null && view != null)
            {
                foreach (var crossing in division.Crossings)
                {
                    if (shown >= MaxCrossingButtons) break;
                    Vector3 screen = camera.WorldToScreenPoint(crossing.Point);
                    if (screen.z <= 0f) continue;
                    Vector2 gui = view.ScreenPointToGUI(screen / view.inputScale);
                    EnsureButtons(shown);
                    buttonSegments[shown] = crossing.Segment;
                    upButtons[shown].relativePosition = new Vector3(gui.x + 16f, gui.y - 26f);
                    downButtons[shown].relativePosition = new Vector3(gui.x + 16f, gui.y + 2f);
                    upButtons[shown].tooltip = "Raise: " + Next(crossing, 1);
                    downButtons[shown].tooltip = "Lower: " + Next(crossing, -1);
                    upButtons[shown].isVisible = crossing.Level != CrossingLevel.Over;
                    downButtons[shown].isVisible = crossing.Level != CrossingLevel.Under;
                    shown++;
                }
            }
            for (int i = shown; i < upButtons.Count; i++)
            {
                upButtons[i].isVisible = false;
                downButtons[i].isVisible = false;
            }
        }

        private static string Next(Crossing crossing, int step)
        {
            var copy = new Crossing
            {
                SameType = crossing.SameType,
                ExistingElevation = crossing.ExistingElevation,
                ExistingName = crossing.ExistingName,
                Level = (CrossingLevel)Mathf.Clamp((int)crossing.Level + step, -1, 1),
            };
            return copy.Describe();
        }

        private void EnsureButtons(int index)
        {
            while (upButtons.Count <= index)
            {
                int i = upButtons.Count;
                upButtons.Add(CrossingButton("UP", () => ChangeLevel(i, 1)));
                downButtons.Add(CrossingButton("DN", () => ChangeLevel(i, -1)));
            }
        }

        private static UIButton CrossingButton(string text, System.Action onClick)
        {
            var button = (UIButton)UIView.GetAView().AddUIComponent(typeof(UIButton));
            button.text = text;
            button.textScale = 0.75f;
            button.size = new Vector2(36f, 24f);
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.isVisible = false;
            button.eventClick += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Left) != 0) onClick();
            };
            return button;
        }

        private void ChangeLevel(int button, int step)
        {
            ushort segment = buttonSegments[button];
            if (segment == 0) return;
            CrossingLevel current;
            if (!crossingLevels.TryGetValue(segment, out current)) current = CrossingLevel.Same;
            var level = (CrossingLevel)Mathf.Clamp((int)current + step, -1, 1);
            crossingLevels[segment] = level;
            Mod.Log("Crossing with segment #" + segment + " set to " + level);
        }

        private void HideCrossingButtons()
        {
            for (int i = 0; i < upButtons.Count; i++)
            {
                upButtons[i].isVisible = false;
                downButtons[i].isVisible = false;
            }
        }

        // Called by ToolActivator before the tool is destroyed.
        public void DestroyButtons()
        {
            foreach (var button in upButtons) if (button != null) Destroy(button.gameObject);
            foreach (var button in downButtons) if (button != null) Destroy(button.gameObject);
            upButtons.Clear();
            downButtons.Clear();
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
            if (division == null || division.Points.Count < 2 || division.Plan == null)
                return "Curve tool: " + road + "\nClick to place points";

            var plan = division.Plan;
            int raised = 0, lowered = 0;
            foreach (var kind in plan.Kinds)
            {
                if (IsRaised(kind)) raised++;
                if (IsLowered(kind)) lowered++;
            }
            var text = "Curve tool: " + road +
                       "\n" + division.Length.ToString("F0") + " m, " + (division.Points.Count - 1) + " segment(s)" +
                       (raised > 0 ? ", raised in places" : "") +
                       (lowered > 0 ? ", lowered in places" : "") +
                       (division.Continues ? ", continues the road" : "") +
                       (division.StartSplit != 0 || division.EndSplit != 0 ? ", branches off a road half" : "") +
                       (division.Ok ? "" : "  -  TOO SHARP");

            var terrain = TerrainManager.instance;
            for (int c = 0; c < division.Crossings.Count; c++)
            {
                var crossing = division.Crossings[c];
                var at = plan.Points[division.CrossingIndex[c]];
                float relative = at.y - terrain.SampleRawHeightSmooth(at);
                text += "\nCrossing " + (c + 1) + " (" + crossing.ExistingName + "): " + crossing.Describe() +
                        (relative >= 1f ? ", " + relative.ToString("F0") + " m up" :
                         relative <= -1f ? ", " + (-relative).ToString("F0") + " m deep" : "");
            }
            foreach (var problem in plan.Problems) text += "\n! " + problem;

            return text + "\nEnter / double-click: build   Ctrl+Z: undo   Esc: clear   UP/DN: change a crossing";
        }

        private void ClearPoints()
        {
            points.Clear();
            pointNodes.Clear();
            pointSides.Clear();
            pointMarkers.Clear();
            crossingLevels.Clear();
            preview = null;
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F1") + ", " + v.y.ToString("F1") + ", " + v.z.ToString("F1") + ")";
        }
    }
}
