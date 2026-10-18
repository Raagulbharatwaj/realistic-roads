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
    // Where it crosses an existing road or track, icons next to the crossing
    // choose how: junction / level crossing (default), bridge (ours over),
    // overpass (existing lowered, ours over at ground), underpass (existing
    // on a short bridge, ours lowered), tunnel (ours under). Bridges and
    // water crossings get +/- to set their height.
    public class CurveTool : ToolBase
    {
        public const float Spacing = 120f;      // target segment length (CS1 segments look best long)
        public const float MinSpacing = 30f;    // shortest segment used to fit a tight bend
        public const float MaxBend = 40f;       // degrees one segment may turn
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
        private static readonly Color SteepColor = new Color(0.75f, 0.3f, 1f, 0.6f);
        private static readonly Color JunctionColor = new Color(1f, 1f, 1f, 0.8f);
        public const float BridgeSpacing = 100f;     // spans over water (elevated pieces, pillars at each node)
        public const float BridgeMinSpacing = 40f;
        private const float WaterJoin = 60f;          // wet patches closer than this are one water crossing
        private const float RaisedSpacing = 48f;      // node (pillar) spacing on raised stretches, like vanilla elevated roads
        private const float RailRaisedSpacing = 96f;  // rail: as few nodes as possible, long spans
        private const float HeightStep = 5f;
        private const float MaxExtraHeight = 60f;
        private const float IconSize = 34f;

        private static readonly CrossingType[] AllTypes =
            { CrossingType.Junction, CrossingType.Bridge, CrossingType.Overpass, CrossingType.Underpass, CrossingType.Tunnel };

        // The player's choices, kept while the curve is being edited.
        // Keyed by Crossing.Key: the segment plus which time along our curve it is
        // crossed, so two crossings of one road each keep their own choice.
        private readonly Dictionary<int, CrossingType> crossingTypes = new Dictionary<int, CrossingType>();
        private readonly Dictionary<int, float> crossingExtra = new Dictionary<int, float>();
        private readonly Dictionary<int, float> waterExtra = new Dictionary<int, float>();

        // On-screen buttons, reused every frame: button i runs buttonActions[i].
        private readonly List<UIButton> buttons = new List<UIButton>();
        private readonly List<System.Action> buttonActions = new List<System.Action>();

        // Clicked control points and, for each, the existing node it snapped to
        // (or 0), the half of that road end it attaches to (+1 left, -1 right,
        // 0 = the node itself) and where to draw it.
        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<ushort> pointNodes = new List<ushort>();
        private readonly List<int> pointSides = new List<int>();
        private readonly List<Vector3> pointMarkers = new List<Vector3>();
        // A point on the middle of an existing road: that road and where on it
        // (0..1). The curve's first and last points use it for a T-junction.
        private readonly List<ushort> pointSegments = new List<ushort>();
        private readonly List<float> pointTs = new List<float>();

        // Written on the main thread in OnToolUpdate, read by SimulationStep.
        // Shared under rayLock: a Ray is too big to be written and read atomically.
        private readonly object rayLock = new object();
        private Ray mouseRay;
        private float mouseRayLength;
        private Ray simRay;              // simulation thread's copy, taken under rayLock
        private float simRayLength;
        private volatile bool mouseRayValid;

        // Written by SimulationStep (simulation thread), read on the main thread.
        private Vector3 hoverPosition;
        private volatile bool hoverValid;
        private volatile ushort hoverNode;
        private volatile int hoverSide;
        private volatile ushort hoverSegment;
        private float hoverT;
        private Vector3 hoverMarker;

        // Preview, recomputed on the main thread when its inputs change.
        private Division preview;
        private string previewKey;
        private int framesSincePreview;
        private int choicesVersion;              // bumped by every edit of points or crossing/water choices
        private const int PreviewRefreshFrames = 30;

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
            public List<WaterRun> WaterRuns = new List<WaterRun>();
            public ushort StartSplit;    // trunk end the start is half-snapped to (or 0)
            public ushort EndSplit;      // trunk end the end is half-snapped to (or 0)
            public int Cost;             // construction cost (game money units, /100 to show)
            // Crossing distances are measured from the curve's own start; a
            // half-snapped start adds the parallel piece before it.
            public float StartOffset;
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
            HideButtons(0);
        }

        // Main thread, every frame while the tool is active.
        protected override void OnToolUpdate()
        {
            base.OnToolUpdate();
            var camera = Camera.main;
            if (camera != null)
            {
                lock (rayLock)
                {
                    mouseRay = camera.ScreenPointToRay(Input.mousePosition);
                    mouseRayLength = camera.farClipPlane;
                }
                mouseRayValid = !m_toolController.IsInsideUI && Cursor.visible;
            }
            else
            {
                mouseRayValid = false;
            }

            // Live preview: the clicked points plus the mouse position. Only
            // worked out again when something it depends on changed (the mouse,
            // the points, a choice), and every half second for changes in the
            // city around it.
            bool withHover = hoverValid && mouseRayValid;
            Vector3 hover = hoverPosition;
            string key = choicesVersion + "|" + points.Count + "|" + withHover + "|" +
                         Mathf.RoundToInt(hover.x * 4f) + "," + Mathf.RoundToInt(hover.y * 4f) + "," + Mathf.RoundToInt(hover.z * 4f) + "|" +
                         hoverNode + "|" + hoverSegment + "|" + hoverSide + "|" + (SelectedNetwork != null ? SelectedNetwork.name : "");
            if (key == previewKey && ++framesSincePreview < PreviewRefreshFrames)
            {
                ToolActivator.SetInfo(InfoText());
                PlaceButtons();
                return;
            }
            previewKey = key;
            framesSincePreview = 0;

            var live = new List<Vector3>(points);
            var liveNodes = new List<ushort>(pointNodes);
            var liveSides = new List<int>(pointSides);
            var liveSegments = new List<ushort>(pointSegments);
            var liveTs = new List<float>(pointTs);
            if (withHover)
            {
                live.Add(hover);
                liveNodes.Add(hoverNode);
                liveSides.Add(hoverSide);
                liveSegments.Add(hoverSegment);
                liveTs.Add(hoverT);
            }
            preview = Divide(live, liveNodes, liveSides, liveSegments, liveTs);
            ToolActivator.SetInfo(InfoText());
            PlaceButtons();
        }

        // Simulation thread. Raycasting is done here, like the vanilla tools do.
        public override void SimulationStep()
        {
            base.SimulationStep();
            lock (rayLock)
            {
                simRay = mouseRay;
                simRayLength = mouseRayLength;
            }
            if (!mouseRayValid)
            {
                hoverValid = false;
                hoverNode = 0;
                hoverSide = 0;
                hoverSegment = 0;
                return;
            }

            Vector3 ground;
            bool onGround = GroundUnderMouse(out ground);

            ushort midSegment;
            Vector3 roadHit;
            ushort node = FindSnapNode(out midSegment, out roadHit);
            hoverSegment = 0;
            if (node == 0 && midSegment != 0)
            {
                // On the middle of a road: join it there (cut at build time).
                Vector3 onRoad;
                float t = CrossingFinder.ClosestT(midSegment, roadHit, out onRoad);
                float length = NetManager.instance.m_segments.m_buffer[midSegment].m_averageLength;
                if (t * length > Crossing.NodeSnap && (1f - t) * length > Crossing.NodeSnap)
                {
                    hoverT = t;
                    hoverSegment = midSegment;
                    hoverPosition = onRoad;
                    hoverMarker = onRoad;
                    hoverNode = 0;
                    hoverSide = 0;
                    hoverValid = true;
                    return;
                }
            }
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
        // under the mouse when the mouse is near that end. Otherwise 0, with
        // the road under the mouse (if any) in 'segmentHit'.
        private ushort FindSnapNode(out ushort segmentHit, out Vector3 hit)
        {
            segmentHit = 0;
            hit = Vector3.zero;
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
            float reach = segment.Info.m_halfWidth + SnapReach;
            float toStart = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_startNode].m_position);
            float toEnd = CurveMath.FlatDistance(output.m_hitPos, nodes[segment.m_endNode].m_position);
            if (toStart <= toEnd && toStart <= reach) return segment.m_startNode;
            if (toEnd < toStart && toEnd <= reach) return segment.m_endNode;
            segmentHit = output.m_netSegment;
            hit = output.m_hitPos;
            return 0;
        }

        // Simulation thread. Where the mouse ray meets the ground or the water
        // surface, ignoring trees, buildings and props, so points can be placed
        // anywhere (the game's own raycast stops at the first object it hits).
        private bool GroundUnderMouse(out Vector3 hit)
        {
            var terrain = TerrainManager.instance;
            Vector3 origin = simRay.origin;
            Vector3 direction = simRay.direction;
            const float step = 8f;
            Vector3 previous = origin;
            for (float d = step; d <= simRayLength; d += step)
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
                pointSegments.Add(hoverNode == 0 ? hoverSegment : (ushort)0);
                pointTs.Add(hoverT);
                choicesVersion++;
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
                pointSegments.RemoveAt(pointSegments.Count - 1);
                pointTs.RemoveAt(pointTs.Count - 1);
                choicesVersion++;
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
                    else if (division.Plan.Steep != null && division.Plan.Steep[i]) color = SteepColor;
                    else if (IsRaised(kinds[i - 1]) || IsRaised(kinds[i])) color = BridgeColor;
                    else if (IsLowered(kinds[i - 1]) || IsLowered(kinds[i])) color = LowColor;
                    else color = GoodColor;
                    overlay.DrawBezier(cameraInfo, color, bezier, width, 100000f, -100000f, -1f, 1280f, false, true);
                }

                var problems = !division.Plan.Ok;
                foreach (var crossing in division.Crossings)
                {
                    Color color = problems ? BadColor
                        : crossing.Type == CrossingType.Bridge || crossing.Type == CrossingType.Overpass ? BridgeColor
                        : crossing.Type == CrossingType.Underpass || crossing.Type == CrossingType.Tunnel ? LowColor
                        : JunctionColor;
                    overlay.DrawCircle(cameraInfo, color, crossing.Point, width + 6f, -1f, 1280f, false, true);
                }
            }

            for (int i = 0; i < points.Count; i++)
                overlay.DrawCircle(cameraInfo, pointNodes[i] != 0 || pointSegments[i] != 0 ? SnapColor : PointColor, pointMarkers[i], PointSize, -1f, 1280f, false, true);

            if (hoverValid && mouseRayValid)
                overlay.DrawCircle(cameraInfo, hoverNode != 0 || hoverSegment != 0 ? SnapColor : HoverColor, hoverMarker, HoverSize, -1f, 1280f, false, true);
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

            var division = Divide(points, pointNodes, pointSides, pointSegments, pointTs);
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
            NetworkBuilder.BuildCurve(SelectedNetwork, division.Plan);
            ClearPoints();
        }

        // Cuts the curve through 'controls' into buildable pieces. Where an end
        // is snapped to an existing road, the curve continues that road
        // smoothly; if that would bend too much it meets the road at an angle
        // instead (like a normal junction). Where an end is half-snapped to a
        // wider road, the curve runs parallel from that half first. Crossings
        // with existing roads get a node exactly on them, and RoadPlanner works
        // out the heights.
        private Division Divide(List<Vector3> controls, List<ushort> nodes, List<int> sides, List<ushort> segments, List<float> ts)
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

            if (halfStart && division.Points.Count > 1)
            {
                division.StartOffset = CurveMath.FlatDistance(division.Points[0], division.Points[1]);
                foreach (var run in division.WaterRuns) { run.From += division.StartOffset; run.To += division.StartOffset; }
            }
            // Ends on the middle of a road (only where the end isn't also half-snapped).
            ushort startSegment = !halfStart && nodes[0] == 0 ? segments[0] : (ushort)0;
            ushort endSegment = !halfEnd && nodes[n] == 0 ? segments[n] : (ushort)0;
            division.Plan = RoadPlanner.Make(SelectedNetwork, division.Points, division.Tangents,
                division.Crossings, division.CrossingIndex, division.WaterRuns, nodes[0], nodes[n],
                startSegment, ts[0], endSegment, ts[n]);
            // Raised stretches get a node (and pillar) every RaisedSpacing, like the
            // vanilla tool's elevated roads; plan again with them.
            if (AddRaisedNodes(division))
                division.Plan = RoadPlanner.Make(SelectedNetwork, division.Points, division.Tangents,
                    division.Crossings, division.CrossingIndex, division.WaterRuns, nodes[0], nodes[n],
                    startSegment, ts[0], endSegment, ts[n]);
            DropNodesOverCrossings(division);
            division.Cost = NetworkBuilder.Cost(SelectedNetwork, division.Plan);
            int blocked = BuildingsInTheWay(division);
            if (blocked > 0)
                division.Plan.Warnings.Add("Runs through " + blocked + " building(s): bulldoze them first (they are not removed)");
            return division;
        }

        // How far either side of a crossing our bridge's nodes go: past the other
        // road's edge and ours, measured along our road (longer at an angle).
        private float SideOffset(List<Vector3> polyline, Crossing crossing)
        {
            Vector3 point, ours;
            CurveMath.PointAt(polyline, crossing.Distance, out point, out ours);
            crossing.Skew = RoadPlanner.Skew(crossing, ours);
            return RoadPlanner.DeckHalf(crossing, SelectedNetwork, crossing.Type == CrossingType.Overpass);
        }

        // Splits raised pieces longer than RaisedSpacing (descending, so the
        // indices still to visit don't move). True if any were split.
        private bool AddRaisedNodes(Division division)
        {
            var plan = division.Plan;
            if (plan == null) return false;
            // Pieces touching the node on a crossed road stay whole: a node added
            // there could land on that road (its span is spread out at an angle).
            var keep = new HashSet<int>();
            for (int c = 0; c < division.Crossings.Count; c++)
                if (division.Crossings[c].Type == CrossingType.Bridge || division.Crossings[c].Type == CrossingType.Overpass)
                    keep.Add(division.CrossingIndex[c]);
            bool added = false;
            for (int i = plan.Points.Count - 1; i >= 1; i--)
            {
                // Elevated stretches over land only: bridges over water keep their
                // long, even spans (BridgeSpacing), with no extra pillar in the river.
                if (plan.Kinds[i - 1] != NodeKind.Elevated || plan.Kinds[i] != NodeKind.Elevated) continue;
                if (keep.Contains(i) || keep.Contains(i - 1)) continue;
                float length = CurveMath.FlatDistance(division.Points[i - 1], division.Points[i]);
                float spacing = RoadPlanner.IsRail(SelectedNetwork) ? RailRaisedSpacing : RaisedSpacing;
                int pieces = Mathf.CeilToInt(length / spacing - 0.15f);
                if (pieces < 2) continue;
                var bezier = PieceBezier(division, i);
                for (int k = pieces - 1; k >= 1; k--)
                {
                    float t = k / (float)pieces;
                    division.Points.Insert(i, CurveMath.Bezier(bezier.a, bezier.b, bezier.c, bezier.d, t));
                    division.Tangents.Insert(i, CurveMath.FlatNormal(CurveMath.BezierDerivative(bezier.a, bezier.b, bezier.c, bezier.d, t)));
                }
                for (int c = 0; c < division.CrossingIndex.Count; c++)
                    if (division.CrossingIndex[c] >= i) division.CrossingIndex[c] += pieces - 1;
                added = true;
            }
            return added;
        }

        // The node right on a road or rail we bridge over (bridge, overpass) is
        // dropped: the deck spans from the nodes either side, and the pillar that
        // node would get doesn't stand on the road or track.
        private static void DropNodesOverCrossings(Division division)
        {
            var plan = division.Plan;
            if (plan == null) return;
            for (int c = division.Crossings.Count - 1; c >= 0; c--)
            {
                var crossing = division.Crossings[c];
                if (crossing.Type != CrossingType.Bridge && crossing.Type != CrossingType.Overpass) continue;
                // Over a rail line the bridge keeps its node above the track (lifted,
                // no pillar) so the wires pass under the deck.
                if (crossing.Type == CrossingType.Bridge && RoadPlanner.CrossesRail(crossing)) continue;
                int k = division.CrossingIndex[c];
                if (k <= 0 || k >= plan.Points.Count - 1) continue;
                // Only with a raised node on each side to carry the deck, and only if
                // the one piece left doesn't bend more than a piece may.
                if (!IsRaised(plan.Kinds[k - 1]) || !IsRaised(plan.Kinds[k + 1])) continue;
                if (CurveMath.BendAngle(division.Tangents[k - 1], division.Tangents[k + 1]) > MaxBend) continue;
                division.Points.RemoveAt(k);
                division.Tangents.RemoveAt(k);
                RoadPlanner.RemoveNode(plan, k);
                division.CrossingIndex[c] = -1;
                for (int other = 0; other < division.CrossingIndex.Count; other++)
                    if (division.CrossingIndex[other] > k) division.CrossingIndex[other]--;
            }
        }

        // Buildings whose footprint the new road's ground-level stretches run
        // over. Raised and tunnelled stretches pass over or under them.
        private int BuildingsInTheWay(Division division)
        {
            var plan = division.Plan;
            if (plan == null || SelectedNetwork == null) return 0;
            var manager = BuildingManager.instance;
            var buildings = manager.m_buildings.m_buffer;
            var grid = manager.m_buildingGrid;
            const int resolution = BuildingManager.BUILDINGGRID_RESOLUTION;
            const float cell = BuildingManager.BUILDINGGRID_CELL_SIZE;
            var hit = new HashSet<ushort>();
            float half = SelectedNetwork.m_halfWidth;
            for (int i = 1; i < plan.Points.Count; i++)
            {
                if (IsRaised(plan.Kinds[i - 1]) && IsRaised(plan.Kinds[i])) continue;
                if (plan.Kinds[i - 1] == NodeKind.Tunnel && plan.Kinds[i] == NodeKind.Tunnel) continue;
                var bezier = PieceBezier(division, i);
                int steps = Mathf.Max(2, Mathf.RoundToInt(CurveMath.FlatDistance(bezier.a, bezier.d) / 8f));
                for (int k = 0; k <= steps; k++)
                {
                    Vector3 p = CurveMath.Bezier(bezier.a, bezier.b, bezier.c, bezier.d, k / (float)steps);
                    int gx = Mathf.Clamp((int)(p.x / cell + resolution / 2f), 0, resolution - 1);
                    int gz = Mathf.Clamp((int)(p.z / cell + resolution / 2f), 0, resolution - 1);
                    for (int z = Mathf.Max(gz - 1, 0); z <= Mathf.Min(gz + 1, resolution - 1); z++)
                        for (int x = Mathf.Max(gx - 1, 0); x <= Mathf.Min(gx + 1, resolution - 1); x++)
                        {
                            ushort id = grid[z * resolution + x];
                            int guard = 0;
                            while (id != 0 && guard++ < 49152)
                            {
                                var b = buildings[id];
                                if ((b.m_flags & Building.Flags.Created) != Building.Flags.None && b.Info != null)
                                {
                                    // Footprint as a circle: cells are 8 m.
                                    float radius = Mathf.Min(b.Info.m_cellWidth, b.Info.m_cellLength) * 4f;
                                    if (CurveMath.FlatDistance(p, b.m_position) < half + radius * 0.8f) hit.Add(id);
                                }
                                id = b.m_nextGridBuilding;
                            }
                        }
                }
            }
            return hit.Count;
        }

        private Division DivideShaped(List<Vector3> controls, Vector3 startDir, Vector3 endDir, ushort startNode, ushort endNode)
        {
            var division = new Division { Points = new List<Vector3>(), Tangents = new List<Vector3>() };
            var polyline = CurveMath.SampleShaped(controls, startDir, endDir);
            division.Length = CurveMath.Length(polyline);
            if (division.Length < 1f) return division;

            // Stretches over water get long bridge spans; crossings become fixed nodes.
            division.WaterRuns = FindWater(polyline);
            var cuts = new List<KeyValuePair<float, Crossing>>();
            foreach (var run in division.WaterRuns)
            {
                cuts.Add(new KeyValuePair<float, Crossing>(run.From, null));
                cuts.Add(new KeyValuePair<float, Crossing>(run.To, null));
            }
            var timesCrossed = new Dictionary<ushort, int>();
            var snapped = new List<int>();
            var found = new List<Crossing>();
            foreach (var crossing in CrossingFinder.Find(polyline, SelectedNetwork, startNode, endNode))
            {
                if (crossing.Distance < MinSpacing * 0.5f || crossing.Distance > division.Length - MinSpacing * 0.5f) continue;
                int times;
                timesCrossed.TryGetValue(crossing.Segment, out times);
                timesCrossed[crossing.Segment] = times + 1;
                crossing.Occurrence = times;
                CrossingType type;
                // Default: a junction where the two may join, else a bridge over the other.
                var fallback = crossing.Allows(CrossingType.Junction) ? CrossingType.Junction : CrossingType.Bridge;
                crossing.Type = crossingTypes.TryGetValue(crossing.Key, out type) && crossing.Allows(type) ? type : fallback;
                float extra;
                crossing.ExtraHeight = crossingExtra.TryGetValue(crossing.Key, out extra) ? extra : 0f;
                cuts.Add(new KeyValuePair<float, Crossing>(crossing.Distance, crossing));
                found.Add(crossing);
            }
            // Over another road or rail (bridge, overpass): nodes either side of
            // it too, so the node on it can be dropped later and no pillar
            // stands on the road or track below. Not where that side node would
            // land on another road or track we cross: the deck spans both.
            var footprint = new Dictionary<Crossing, float>();
            float ourHalf = SelectedNetwork != null ? SelectedNetwork.m_halfWidth : 8f;
            foreach (var crossing in found)
                footprint[crossing] = crossing.Type == CrossingType.Bridge || crossing.Type == CrossingType.Overpass
                    ? SideOffset(polyline, crossing)
                    : crossing.ExistingHalfWidth + ourHalf + RoadPlanner.PillarClearance;
            foreach (var crossing in found)
            {
                if (crossing.Type != CrossingType.Bridge && crossing.Type != CrossingType.Overpass) continue;
                foreach (float at in new[] { crossing.Distance - footprint[crossing], crossing.Distance + footprint[crossing] })
                {
                    bool onAnother = false;
                    foreach (var other in found)
                        if (other != crossing && Mathf.Abs(at - other.Distance) < footprint[other]) onAnother = true;
                    if (!onAnother) cuts.Add(new KeyValuePair<float, Crossing>(at, null));
                }
            }
            cuts.Sort((p, q) => p.Key.CompareTo(q.Key));

            var stops = new List<KeyValuePair<float, Crossing>> { new KeyValuePair<float, Crossing>(0f, null) };
            foreach (var cut in cuts)
            {
                if (cut.Key < 4f || cut.Key > division.Length - 4f) continue;
                if (cut.Key - stops[stops.Count - 1].Key < 4f)
                {
                    // Too close to the previous stop: keep the crossing if there is one.
                    if (cut.Value != null && stops[stops.Count - 1].Value == null && stops.Count > 1)
                        stops[stops.Count - 1] = cut;
                    continue;
                }
                stops.Add(cut);
            }
            stops.Add(new KeyValuePair<float, Crossing>(division.Length, null));

            for (int c = 1; c < stops.Count; c++)
            {
                float from = stops[c - 1].Key, to = stops[c].Key;
                var slice = CurveMath.Slice(polyline, from, to);
                bool overWater = InWater(division.WaterRuns, (from + to) * 0.5f);
                List<Vector3> slicePoints, sliceTangents;
                bool ignored;
                if (overWater)
                {
                    // Bridges: equal spans so the bridge model repeats evenly.
                    int count = CurveMath.ChoosePieceCount(slice, BridgeSpacing, BridgeMinSpacing, MaxBend, out ignored);
                    CurveMath.Divide(slice, count, out slicePoints, out sliceTangents);
                }
                else
                {
                    // Land: long pieces on straights, shorter only in bends.
                    CurveMath.DivideAdaptive(slice, Spacing, MinSpacing, MaxBend, out slicePoints, out sliceTangents, out ignored);
                }
                int skip = division.Points.Count == 0 ? 0 : 1;
                for (int i = skip; i < slicePoints.Count; i++)
                {
                    division.Points.Add(slicePoints[i]);
                    division.Tangents.Add(sliceTangents[i]);
                }
                if (c < stops.Count - 1)
                {
                    // Direction at a stop comes from the whole curve, not one side of it.
                    int index = division.Points.Count - 1;
                    Vector3 point, tangent;
                    CurveMath.PointAt(polyline, to, out point, out tangent);
                    if (tangent != Vector3.zero) division.Tangents[index] = tangent;
                    if (stops[c].Value != null)
                    {
                        // A junction right next to an existing node joins at that node:
                        // move our node onto it now, so the planner, the preview and the
                        // bend check all see the road that will be built.
                        var snap = stops[c].Value;
                        if (snap.Connects && snap.SnapNode != 0)
                        {
                            Vector3 node = NetManager.instance.m_nodes.m_buffer[snap.SnapNode].m_position;
                            division.Points[index] = new Vector3(node.x, division.Points[index].y, node.z);
                            snapped.Add(index);
                        }
                        division.Crossings.Add(stops[c].Value);
                        division.CrossingIndex.Add(index);
                    }
                }
            }

            // A snapped node's direction runs from its neighbour before to the one after.
            foreach (int index in snapped)
            {
                if (index <= 0 || index >= division.Points.Count - 1) continue;
                Vector3 through = CurveMath.FlatNormal(division.Points[index + 1] - division.Points[index - 1]);
                if (through != Vector3.zero) division.Tangents[index] = through;
            }
            if (startDir != Vector3.zero) division.Tangents[0] = CurveMath.FlatNormal(startDir);
            if (endDir != Vector3.zero) division.Tangents[division.Tangents.Count - 1] = CurveMath.FlatNormal(endDir);
            division.Ok = CurveMath.MaxBend(division.Tangents) <= MaxBend;
            return division;
        }

        // Stretches of the curve over water. Wet patches less than WaterJoin
        // apart are one crossing: a stream with gravel banks, a river with an
        // islet, the bottom of a gorge.
        private List<WaterRun> FindWater(List<Vector3> polyline)
        {
            var runs = new List<WaterRun>();
            float along = 0f;
            WaterRun current = null;
            for (int i = 0; i < polyline.Count; i++)
            {
                if (i > 0) along += CurveMath.FlatDistance(polyline[i - 1], polyline[i]);
                bool wet = NetworkBuilder.IsOverWater(polyline[i]);
                if (wet)
                {
                    if (current != null && along - current.To < WaterJoin) current.To = along;
                    else
                    {
                        current = new WaterRun { From = along, To = along };
                        runs.Add(current);
                    }
                }
            }
            foreach (var run in runs)
            {
                Vector3 middle, tangent;
                CurveMath.PointAt(polyline, (run.From + run.To) * 0.5f, out middle, out tangent);
                run.Middle = middle;
                // Remembered by roughly where it is, so the +/- survives small edits.
                run.Key = Mathf.RoundToInt(middle.x / 64f) * 100003 + Mathf.RoundToInt(middle.z / 64f);
                float extra;
                run.Extra = waterExtra.TryGetValue(run.Key, out extra) ? extra : 0f;
            }
            return runs;
        }

        private static bool InWater(List<WaterRun> runs, float at)
        {
            foreach (var run in runs) if (at > run.From && at < run.To) return true;
            return false;
        }

        // --- on-screen buttons ---------------------------------------------------

        // Main thread. Icons next to each crossing, +/- next to each bridge and
        // water crossing. Buttons are reused; each frame says what they do.
        private void PlaceButtons()
        {
            int used = 0;
            var division = preview;
            var camera = Camera.main;
            var view = UIView.GetAView();
            if (division != null && division.Plan != null && camera != null && view != null)
            {
                foreach (var crossing in division.Crossings)
                {
                    Vector2 gui;
                    if (!ToGui(camera, view, crossing.Point, out gui)) continue;
                    var allowed = new List<CrossingType>();
                    foreach (var type in AllTypes) if (crossing.Allows(type)) allowed.Add(type);
                    float x = gui.x - allowed.Count * (IconSize + 2f) * 0.5f;
                    float y = gui.y - IconSize - 24f;
                    var c = crossing;
                    foreach (var type in allowed)
                    {
                        var t = type;
                        UseButton(used++, IconFor(t), Letter(t), crossing.Describe(t), new Vector2(x, y), crossing.Type == t,
                            () => SetType(c, t));
                        x += IconSize + 2f;
                    }
                    if (crossing.Type == CrossingType.Bridge)
                    {
                        UseButton(used++, Icons.Plus, "+", "Higher (+" + HeightStep + " m)", new Vector2(x + 4f, y), false, () => AddExtra(c, HeightStep));
                        UseButton(used++, Icons.Minus, "-", "Lower (-" + HeightStep + " m)", new Vector2(x + 4f + IconSize + 2f, y), false, () => AddExtra(c, -HeightStep));
                    }
                }
                foreach (var run in division.WaterRuns)
                {
                    Vector2 gui;
                    if (!ToGui(camera, view, run.Middle, out gui)) continue;
                    var r = run;
                    UseButton(used++, Icons.Plus, "+", "Bridge higher over the water (+" + HeightStep + " m)",
                        new Vector2(gui.x - IconSize - 1f, gui.y - IconSize - 24f), false, () => AddWaterExtra(r, HeightStep));
                    UseButton(used++, Icons.Minus, "-", "Bridge lower over the water (-" + HeightStep + " m)",
                        new Vector2(gui.x + 1f, gui.y - IconSize - 24f), false, () => AddWaterExtra(r, -HeightStep));
                }
            }
            HideButtons(used);
        }

        private static bool ToGui(Camera camera, UIView view, Vector3 world, out Vector2 gui)
        {
            Vector3 screen = camera.WorldToScreenPoint(world);
            gui = Vector2.zero;
            if (screen.z <= 0f) return false;
            gui = view.ScreenPointToGUI(screen / view.inputScale);
            return true;
        }

        private void UseButton(int index, string icon, string letter, string tooltip, Vector2 position, bool selected, System.Action action)
        {
            while (buttons.Count <= index)
            {
                int i = buttons.Count;
                buttons.Add(CreateButton(() => { if (i < buttonActions.Count && buttonActions[i] != null) buttonActions[i](); }));
                buttonActions.Add(null);
            }
            var button = buttons[index];
            buttonActions[index] = action;
            button.relativePosition = new Vector3(position.x, position.y);
            button.tooltip = tooltip;
            if (button.atlas == Icons.Atlas && Icons.Atlas != null)
            {
                button.normalFgSprite = icon;
                button.hoveredFgSprite = icon;
                button.pressedFgSprite = icon;
                button.normalBgSprite = selected ? Icons.BackgroundSelected : Icons.Background;
                button.hoveredBgSprite = selected ? Icons.BackgroundSelected : Icons.BackgroundHover;
                button.text = "";
            }
            else
            {
                button.text = selected ? "[" + letter + "]" : letter;
            }
            button.isVisible = true;
        }

        private static UIButton CreateButton(System.Action onClick)
        {
            var button = (UIButton)UIView.GetAView().AddUIComponent(typeof(UIButton));
            button.size = new Vector2(IconSize, IconSize);
            var atlas = Icons.Atlas;
            if (atlas != null)
            {
                button.atlas = atlas;
                button.pressedBgSprite = Icons.BackgroundSelected;
            }
            else
            {
                button.normalBgSprite = "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed";
                button.textScale = 0.8f;
            }
            button.isVisible = false;
            button.eventClick += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Left) != 0) onClick();
            };
            return button;
        }

        private void HideButtons(int from)
        {
            for (int i = from; i < buttons.Count; i++)
            {
                buttons[i].isVisible = false;
                buttonActions[i] = null;
            }
        }

        private static string IconFor(CrossingType type)
        {
            switch (type)
            {
                case CrossingType.Bridge: return Icons.Bridge;
                case CrossingType.Overpass: return Icons.Overpass;
                case CrossingType.Underpass: return Icons.Underpass;
                case CrossingType.Tunnel: return Icons.Tunnel;
                default: return Icons.Junction;
            }
        }

        private static string Letter(CrossingType type)
        {
            switch (type)
            {
                case CrossingType.Bridge: return "B";
                case CrossingType.Overpass: return "O";
                case CrossingType.Underpass: return "U";
                case CrossingType.Tunnel: return "T";
                default: return "J";
            }
        }

        private void SetType(Crossing crossing, CrossingType type)
        {
            crossingTypes[crossing.Key] = type;
            choicesVersion++;
            Mod.Log("Crossing with " + crossing.ExistingName + " (#" + crossing.Segment + "): " + crossing.Describe(type));
        }

        private void AddExtra(Crossing crossing, float step)
        {
            float extra;
            crossingExtra.TryGetValue(crossing.Key, out extra);
            crossingExtra[crossing.Key] = Mathf.Clamp(extra + step, 0f, MaxExtraHeight);
            choicesVersion++;
        }

        private void AddWaterExtra(WaterRun run, float step)
        {
            float extra;
            waterExtra.TryGetValue(run.Key, out extra);
            waterExtra[run.Key] = Mathf.Clamp(extra + step, -5f, MaxExtraHeight);
            choicesVersion++;
        }

        // Called by ToolActivator before the tool is destroyed.
        public void DestroyButtons()
        {
            foreach (var button in buttons) if (button != null) Destroy(button.gameObject);
            buttons.Clear();
            buttonActions.Clear();
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
                int index = division.CrossingIndex[c];
                float relative = 0f;
                if (index >= 0)
                {
                    var at = plan.Points[index];
                    relative = at.y - terrain.SampleRawHeightSmooth(at);
                }
                else
                {
                    // Spanned by the deck: its height above the road or rail there.
                    relative = HeightAt(plan, crossing.Distance + division.StartOffset) - terrain.SampleRawHeightSmooth(crossing.Point);
                }
                text += "\nCrossing " + (c + 1) + " (" + crossing.ExistingName + "): " + crossing.Describe() +
                        (relative >= 1f ? ", " + relative.ToString("F0") + " m up" :
                         relative <= -1f ? ", " + (-relative).ToString("F0") + " m deep" : "");
            }
            for (int w = 0; w < division.WaterRuns.Count; w++)
            {
                var run = division.WaterRuns[w];
                float deck = HeightAt(plan, (run.From + run.To) * 0.5f) - terrain.SampleRawHeightSmoothWithWater(run.Middle, false, 0f);
                text += "\nWater " + (w + 1) + " (" + (run.To - run.From).ToString("F0") + " m wide): bridge deck " +
                        deck.ToString("F0") + " m above the water (+/- to change)";
            }
            if (division.Cost > 0) text += "\nCost: " + (division.Cost / 100).ToString("N0");
            foreach (var problem in plan.Problems) text += "\n! " + problem;
            foreach (var warning in plan.Warnings) text += "\n~ " + warning;

            return text + "\nEnter / double-click: build   Ctrl+Z: undo   Esc: clear   Icons: choose each crossing";
        }

        // The planned road height 'at' metres along the curve.
        private static float HeightAt(Plan plan, float at)
        {
            float along = 0f;
            for (int i = 1; i < plan.Points.Count; i++)
            {
                float piece = CurveMath.FlatDistance(plan.Points[i - 1], plan.Points[i]);
                if (along + piece >= at || i == plan.Points.Count - 1)
                    return Mathf.Lerp(plan.Points[i - 1].y, plan.Points[i].y, piece > 0.01f ? Mathf.Clamp01((at - along) / piece) : 0f);
                along += piece;
            }
            return plan.Points.Count > 0 ? plan.Points[0].y : 0f;
        }

        private void ClearPoints()
        {
            points.Clear();
            pointNodes.Clear();
            pointSides.Clear();
            pointMarkers.Clear();
            pointSegments.Clear();
            pointTs.Clear();
            crossingTypes.Clear();
            crossingExtra.Clear();
            waterExtra.Clear();
            preview = null;
            choicesVersion++;
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F1") + ", " + v.y.ToString("F1") + ", " + v.z.ToString("F1") + ")";
        }
    }
}
