using System;
using System.Collections.Generic;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using UnityEngine;

namespace RealisticRoads
{
    // CS2-style curve tool.
    //   Left-click        add a control point (snaps to an existing road end/junction;
    //                     the curve then continues that road smoothly, or joins the
    //                     middle of a road there).
    //   Right-click       remove the last point
    //   Page Up / Down    height of the next point (3 m steps, Shift: 1 m), like the vanilla tool
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
    public partial class CurveTool : ToolBase
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
        private static readonly Color PillarColor = new Color(1f, 1f, 1f, 0.7f);
        private static readonly Color CursorColor = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color GuideColor = new Color(1f, 1f, 1f, 0.35f);
        private const float GuideBeyond = 150f;   // the guide line runs on this far past the point
        private const float GuideWidth = 1.5f;
        public const float BridgeSpacing = 100f;     // spans over water (elevated pieces, pillars at each node)
        public const float BridgeMinSpacing = 40f;
        private const float WaterJoin = 60f;          // wet patches closer than this are one water crossing, if both are as wide as the gap
        private const float WaterJoinShort = 24f;     // closer than this: always one crossing
        private const float MinWaterWidth = 4f;       // narrower wet patches are ignored
        // Node (pillar) spacing on raised stretches: a pillar this often, as a real
        // viaduct has (90 m left long spans with nothing under them).
        private const float RaisedSpacing = 45f;
        private const float RailRaisedSpacing = 60f;
        private const float DeckTurnRadius = 50f;      // a deck following roads turns at a junction no tighter than this
        private const float RailDeckTurnRadius = 90f;
        private const float MaxDroppedSpan = 160f;     // longer than this, a node over a crossed road is kept (pillarless)
        private const float MinPillarHeight = 3f;      // a raised node lower than this over the ground gets no pillar
        private const float HeightStep = LiftStep;   // a crossing's +/- (the same as Page Up / Page Down)

        // A +/- click's step: 1 m with Shift held, like Page Up / Page Down.
        private static float ClickStep()
        {
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 1f : HeightStep;
        }
        private const float MaxExtraHeight = 60f;
        private const float IconSize = 34f;

        private static readonly CrossingType[] AllTypes =
            { CrossingType.Junction, CrossingType.Bridge, CrossingType.Overpass, CrossingType.Underpass, CrossingType.Tunnel };

        // The player's choices, kept while the curve is being edited.
        // Keyed by Crossing.Key: the segment plus which time along our curve it is
        // crossed, so two crossings of one road each keep their own choice.
        private readonly Dictionary<int, CrossingType> crossingTypes = new Dictionary<int, CrossingType>();
        private readonly Dictionary<int, float> crossingExtra = new Dictionary<int, float>();
        private readonly Dictionary<int, CrossingType> parallelTypes = new Dictionary<int, CrossingType>();   // the parallel line's
        private readonly Dictionary<int, float> parallelExtra = new Dictionary<int, float>();
        private readonly Dictionary<int, float> waterExtra = new Dictionary<int, float>();
        private readonly Dictionary<int, bool> waterTunnel = new Dictionary<int, bool>();   // water crossings chosen as tunnels
        // Where each water crossing (by key) was last seen: its middle.
        private readonly List<KeyValuePair<int, Vector3>> waterPlaces = new List<KeyValuePair<int, Vector3>>();
        // The parallel line's own water choices: it may cross on a bridge where the curve tunnels.
        private readonly Dictionary<int, float> parallelWaterExtra = new Dictionary<int, float>();
        private readonly Dictionary<int, bool> parallelWaterTunnel = new Dictionary<int, bool>();
        private readonly List<KeyValuePair<int, Vector3>> parallelWaterPlaces = new List<KeyValuePair<int, Vector3>>();
        // Where each road crossing was last seen (by key), the curve's and the parallel line's.
        private readonly List<KeyValuePair<int, Vector3>> crossingPlaces = new List<KeyValuePair<int, Vector3>>();
        private readonly List<KeyValuePair<int, Vector3>> parallelCrossingPlaces = new List<KeyValuePair<int, Vector3>>();
        private const float CrossingMatch = 40f;   // a road crossing is the one of that road remembered this close
        private int lastWaterKey;
        private const float WaterMatch = 80f;   // a water crossing is the one remembered this close to its middle

        // On-screen buttons, reused every frame: button i runs buttonActions[i].
        private readonly List<UIButton> buttons = new List<UIButton>();
        private readonly List<System.Action> buttonActions = new List<System.Action>();
        private readonly List<System.Action<int>> buttonWheels = new List<System.Action<int>>();   // the mouse wheel over button i (+1 up, -1 down), or null

        // Clicked control points and, for each, the existing node it snapped to
        // (or 0) and where to draw it.
        private readonly List<Vector3> points = new List<Vector3>();
        private readonly List<ushort> pointNodes = new List<ushort>();
        private readonly List<Vector3> pointMarkers = new List<Vector3>();
        // A point on the middle of an existing road: that road and where on it
        // (0..1). The curve's first and last points use it for a T-junction.
        private readonly List<ushort> pointSegments = new List<ushort>();
        private readonly List<float> pointTs = new List<float>();
        // Each point's height above the ground (Page Up / Page Down, like the
        // vanilla road tool's elevation), and the height the next point gets.
        // 0 on a point snapped to a road: it takes that road's height.
        private readonly List<float> pointLifts = new List<float>();
        // Each point's piece (from the point before it): straight or curved.
        private readonly List<bool> pointStraight = new List<bool>();
        // Decks (double deckers): each point's piece is a deck over the roads
        // below, and the road a deck point was put on (its centre line), for
        // following that road to the next deck point.
        private readonly List<bool> pointDeck = new List<bool>();
        private readonly List<ushort> pointRoad = new List<ushort>();
        private readonly List<float> pointRoadT = new List<float>();
        private bool deckMode;
        // A deck below instead (one level under the road: a tunnel, e.g. metro
        // under a street), per point like pointDeck.
        private readonly List<bool> pointUnder = new List<bool>();
        private bool underMode;
        private ushort aimRoad;
        private float aimRoadT;
        private float lift;

        // Precision drawing (the panel's DRAW bar). The next piece is straight
        // or curved; corners between straight pieces are rounded with
        // cornerRadius (0: sharp). Straight pieces snap their direction (15°
        // steps, or along / across a road nearby) and length (8 m steps);
        // Shift draws freely. A typed length or turn holds the next point to it.
        private bool straightMode;
        private float cornerRadius = DefaultCornerRadius;
        private bool snapping = true;
        private float typedLength = -1f;          // metres (-1: not typed)
        private float typedTurn = float.NaN;      // degrees right of the last piece (or of north for the first)
        public const float DefaultCornerRadius = 60f;
        // Rounder than this a corner needs pieces shorter than MinSpacing to stay
        // within MaxBend; below it the corner is sharp (0) instead.
        public const float MinCornerRadius = 50f;
        public const float RadiusStep = 10f;
        public const float MaxCornerRadius = 300f;
        private const float AngleStep = 15f;
        private const float LengthStep = 8f;
        private const float RoadSnapTolerance = 5f;   // degrees from along / across a road that snap to it
        private const float RoadSnapReach = 100f;     // roads this close to the last point or the mouse count

        // Where the next point goes: the mouse, snapped (main thread).
        private bool aimValid;
        private Vector3 aimPosition, aimMarker;
        private ushort aimNode, aimSegment;
        private float aimT;
        private bool aimGuided;                   // a straight piece was snapped or typed: show its guide line
        private Vector3 guideFrom, guideDirection;
        private float guideLength, guideTurn;
        private CurveMath.SnapKind guideKind;
        public const float LiftStep = 3f;       // Page Up / Page Down (Shift: 1 m)
        public const float MaxLift = 60f;
        public const float MinLift = -30f;

        // Written on the main thread in OnToolUpdate, read by SimulationStep.
        // Shared under rayLock: a Ray is too big to be written and read atomically.
        private readonly object rayLock = new object();
        private Ray mouseRay;
        private float mouseRayLength;
        private Ray simRay;              // simulation thread's copy, taken under rayLock
        private float simRayLength;
        private volatile bool mouseRayValid;

        // What is under the mouse: written as one by SimulationStep (simulation
        // thread) under hoverLock, and copied once a frame on the main thread
        // (TakeHover) into the fields below, so all of them come from the same
        // raycast (a Vector3 isn't written or read atomically).
        private struct Hover
        {
            public bool Valid;
            public ushort Node, Segment;
            public float T;
            public Vector3 Position, Marker;
        }

        private readonly object hoverLock = new object();
        private Hover simHover;
        // Main thread's copy.
        private Vector3 hoverPosition;
        private bool hoverValid;
        private ushort hoverNode;
        private ushort hoverSegment;
        private float hoverT;
        private Vector3 hoverMarker;

        private void TakeHover()
        {
            Hover h;
            lock (hoverLock) h = simHover;
            hoverValid = h.Valid;
            hoverNode = h.Node;
            hoverSegment = h.Segment;
            hoverT = h.T;
            hoverPosition = h.Position;
            hoverMarker = h.Marker;
        }

        // Preview, recomputed on the main thread when its inputs change.
        private Division preview;
        private string previewKey;
        private int framesSincePreview;
        private int choicesVersion;              // bumped by every edit of points or crossing/water choices
        private const int PreviewRefreshFrames = 30;

        // The road/track the player last picked in the vanilla Roads menu.
        // The road (or track) picked in the game's menu. While the player is
        // picking what goes beside the curve (PickBeside), the next one picked
        // is the parallel line's, and the curve keeps its own.
        public NetInfo SelectedNetwork
        {
            get { return selectedNetwork; }
            set
            {
                // A median ramp road's piece (picked from one built) draws as the
                // game's road it was made from: the pieces are never drawn alone.
                if (SplitRoads.IsSplit(value) || SplitRoads.IsFullStart(value)) value = SplitRoads.OriginalOf(value);
                else if (SplitRoads.IsRamp(value))
                    value = PrefabCollection<NetInfo>.FindLoaded(SplitRoads.RampLanes(value) > 1 ? "Medium Road" : "Basic Road") ?? value;
                if (pickingBeside && value != null && selectedNetwork != null && value != selectedNetwork)
                {
                    pickingBeside = false;
                    besideNetwork = value;
                    parallelReverse = false;   // a road and a track beside it run the same way
                    parallelTypes.Clear();
                    parallelExtra.Clear();
                    parallelWaterExtra.Clear();
                    parallelWaterTunnel.Clear();
                    parallelWaterPlaces.Clear();
                    parallelCrossingPlaces.Clear();
                    choicesVersion++;
                    Mod.Log("Parallel line: " + value.name + " beside the " + selectedNetwork.name);
                    return;
                }
                selectedNetwork = value;
            }
        }

        private NetInfo selectedNetwork;

        public bool IsActive => ToolsModifierControl.toolController != null &&
                                ToolsModifierControl.toolController.CurrentTool == this;

        // A curve cut into buildable pieces.
        private class Division
        {
            // A median cut's: where its road has the open gap along the line (from
            // its elevated end to just past the tunnel's mouth), and where the open
            // cut is (from the ground to the tunnel's depth): see CutStops.
            public float GapFrom, GapTo = -1f;
            public List<ushort> Blocking = new List<ushort>();   // buildings its ground-level stretches run through (marked on the map)
            public float CutFrom = -1f, CutTo = -1f;
            public List<Vector3> Points;
            public List<Vector3> Tangents;      // leaving each point
            public List<Vector3> InTangents;    // arriving at it (differs at a sharp corner)
            public float Length;
            public bool Ok;
            public bool Continues;       // follows an existing road at the start or end
            public Plan Plan;            // heights, road versions, crossings, problems
            public List<Crossing> Crossings = new List<Crossing>();
            public List<int> CrossingIndex = new List<int>();
            public List<WaterRun> WaterRuns = new List<WaterRun>();
            // Deck stretches (metres along the curve), and per node: on a deck,
            // and the road under it.
            public List<KeyValuePair<float, float>> DeckRanges = new List<KeyValuePair<float, float>>();
            public List<Vector3> Polyline;     // the sampled curve the points are on (ranges are along it)
            public bool[] OnDeck;
            public List<KeyValuePair<float, float>> UnderRanges = new List<KeyValuePair<float, float>>();   // the deck stretches under the road
            public bool[] UnderDeck;
            public DeckBelow[] Below;
            public int Cost;             // construction cost (game money units, /100 to show)
            public float[] ControlAt;    // each point's distance along Polyline
            public float[] NodeAlong;    // each node's distance along Polyline
            public Forced[] Forced;      // per node: marked Elevated or Tunnel in the side view
            public List<ForcedStretch> ForcedRanges = new List<ForcedStretch>();   // those stretches, along Polyline
            public List<KeyValuePair<Vector3, float>> Heights = new List<KeyValuePair<Vector3, float>>();   // dragged heights: where, how high
            public List<float> Corners;  // sharp corners, along Polyline
            public List<float> Lifts;    // each point's own height (Page Up / Page Down)
            public LineSetup Setup;      // the road it is planned for, and the player's choices for it
        }

        // The road (or track) a division is planned for, with the player's
        // choices for it: the curve drawn, or the parallel line beside it.
        private class LineSetup
        {
            public NetInfo Info;
            public Dictionary<int, CrossingType> Types;
            public Dictionary<int, float> Extra;
            public Dictionary<int, bool> WaterTunnel;
            public Dictionary<int, float> WaterExtra;
            public List<KeyValuePair<int, Vector3>> WaterPlaces;
            // Where each road crossing (by Crossing.Key) was last seen, so its
            // choice stays with it while the curve is edited (null: by order).
            public List<KeyValuePair<int, Vector3>> CrossingPlaces;
            public List<HeightPin> Pins;
            public List<ForcedStretch> Stretches;
            // Choices found by where the crossing is: the parallel line is planned
            // again once the curve is built, and the roads it crossed may have
            // been cut into new pieces (new numbers) by then.
            public List<PlacedChoice> ByPlace;
        }

        private struct PlacedChoice
        {
            public Vector3 At;
            public CrossingType Type;
            public float Extra;
        }

        private const float PlaceMatch = 8f;    // a choice found by place: the crossing this close to where it was
        private LineSetup line;                 // the one being divided (main thread)
        private NetInfo Net => line != null ? line.Info : SelectedNetwork;

        private LineSetup MainSetup()
        {
            return new LineSetup
            {
                Info = DrawNetwork, Types = crossingTypes, Extra = crossingExtra, WaterTunnel = waterTunnel, WaterExtra = waterExtra,
                WaterPlaces = waterPlaces, CrossingPlaces = crossingPlaces,
                Pins = heightPins, Stretches = stretches,
            };
        }

        // The type the player chose for a crossing of the line being divided:
        // by where it is when planned again after a build (the roads' numbers
        // may have changed, and been given to other roads), else by the road.
        private bool ChosenType(Crossing crossing, out CrossingType type)
        {
            int choice = ChoiceAt(crossing);
            if (choice >= 0)
            {
                type = line.ByPlace[choice].Type;
                return true;
            }
            return line.Types.TryGetValue(crossing.Key, out type);
        }

        private float ChosenExtra(Crossing crossing)
        {
            int choice = ChoiceAt(crossing);
            if (choice >= 0) return line.ByPlace[choice].Extra;
            float extra;
            return line.Extra.TryGetValue(crossing.Key, out extra) ? extra : 0f;
        }

        // The choice kept by place nearest to the crossing (-1: none).
        private int ChoiceAt(Crossing crossing)
        {
            if (line.ByPlace == null) return -1;
            int best = -1;
            float bestDistance = PlaceMatch;
            for (int i = 0; i < line.ByPlace.Count; i++)
            {
                float d = CurveMath.FlatDistance(line.ByPlace[i].At, crossing.Point);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = i;
            }
            return best;
        }

        // --- parallel drawing: a second road or track beside the curve ------------

        // What the parallel line is drawn with. Taken from the tool for the
        // preview, and kept when building: it is planned again after the curve
        // is built, when the points are already gone.
        private class ParallelSpec
        {
            public NetInfo Main, Info;
            public float Gap;
            public bool Left, Reverse;
            public LineSetup Setup;
        }

        // The curve is being built; the parallel line follows once it is.
        private class PendingParallel
        {
            public Division Main;
            public ParallelSpec Spec;
            public NetInfo Ground;       // a median ramp road's ground road (instead of a parallel line)
            public NetInfo Full;         // and the full road its lanes come from (or go on to)
            public int Group;
            public volatile int State;   // 0: building the curve (or a median ramp road's next part), 1: built, 2: not built
            // A median ramp road is built part by part, each once the one before
            // is (see FinishMedian):
            public MedianStage Stage;
            public ushort Origin;        // the node where the ramp leaves (or lands in) the road
            public ushort Stop;          // along a road: the node its ground road got to
            public float StopAlong;      // and how far along the line (from the ramp's node)
            public int Converted;        // and how many segments of it became the ground road (-1: failed)
            public NetInfo RampInfo;     // the ramp road (built after the full road on open ground)
            public Vector3 LeadAt;       // where that full road ends (the ramp's node)
            public bool AnyBuilt;        // a part is built (a failure removes what is)
            public string RampLog;       // the ramp's "Building ..." line, logged when its build starts
            public string StartsWhy;     // why the start pieces couldn't be made
            public bool Cut;             // a median cut (see FinishCut)
            public bool TriedLineStart;  // and its road was looked for where the line starts too
        }

        private enum MedianStage { LeadIn, Ramp, Convert, Ground, Starts, CutTrack, CutSplit, CutConvert, CutLeadIn, CutGround, CutStarts, CutEndStarts }

        private bool parallelMode;
        private float parallelGap = DefaultGap;  // metres between the two roads' edges
        private bool parallelLeft = true;        // on the left of the curve as drawn (else the right)
        private bool parallelReverse = true;     // runs the other way (a one-way pair), else the same way (a double track)
        private bool pickingBeside;              // the next road picked in the game's menu goes beside the curve
        private NetInfo besideNetwork;           // null: the same road, the other way (a one-way pair)
        private Division second;                 // the parallel line's preview
        private PendingParallel pending;
        public const float DefaultGap = 4f;
        public const float GapStep = 1f;
        public const float MaxGap = 40f;
        private const float EndSnap = 8f;        // a parallel end this close to a node joins it

        // --- the side view: heights dragged there, stretches marked Elevated / Tunnel ---

        // Places are piece + fraction (CurveMath.PlaceAlong), so they stay on
        // their piece while points are dragged.
        private class HeightPin
        {
            public float At;
            public float Height;    // absolute
        }

        private class ForcedStretch
        {
            public float From, To;
            public Forced Kind;
        }

        private readonly List<HeightPin> heightPins = new List<HeightPin>();
        private readonly List<ForcedStretch> stretches = new List<ForcedStretch>();
        private HeightPin draggedPin;            // the height being dragged in the side view
        private float draggedGround;             // the ground under it (the drag stays within MinLift..MaxLift of it)
        private const float MinPinHeight = -40f; // a dragged height at most this far under the ground

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
            lock (hoverLock) simHover = new Hover();
            mouseRayValid = false;
            panel.Hide();
            HideButtons(0);
            foreach (var label in mapLabels) label.isVisible = false;
            if (cursorLabel != null) cursorLabel.isVisible = false;
        }

        // Main thread, every frame while the tool is active.
        protected override void OnToolUpdate()
        {
            base.OnToolUpdate();
            NetworkBuilder.PollMedianOrigins();
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

            // Everything below sees the same raycast.
            TakeHover();
            // Planning: a point being dragged follows the mouse.
            if (dragIndex >= 0) UpdateDrag();
            // A height dragged or a stretch selected in the side view.
            panel.Poll();
            // (The next part of a build in parts is started by PumpPending, every
            // frame whether this tool is active or not.)
            if (upgradeMode)
            {
                UpdateUpgrade();
                return;
            }

            // Live preview: the clicked points plus the mouse position while
            // drawing (not while planning: then the plan stays as it is). Only
            // worked out again when something it depends on changed (the mouse,
            // the points, a choice), and every half second for changes in the
            // city around it.
            UpdateAim();
            // A typed point shows even while the mouse is over the panel (typing it).
            bool withHover = aimValid && (mouseRayValid || aimGuided) && !planning;
            Vector3 hover = aimPosition;
            string key = choicesVersion + "|" + points.Count + "|" + withHover + "|" +
                         Mathf.RoundToInt(hover.x * 4f) + "," + Mathf.RoundToInt(hover.y * 4f) + "," + Mathf.RoundToInt(hover.z * 4f) + "|" +
                         aimNode + "|" + aimSegment + "|" + (SelectedNetwork != null ? SelectedNetwork.name : "") + "|" +
                         straightMode + cornerRadius + deckMode + underMode + aimRoad;
            if (key == previewKey && ++framesSincePreview < PreviewRefreshFrames)
            {
                // Nothing changed: the panel already shows this preview.
                PlaceButtons();
                UpdateCursorLabel(withHover && mouseRayValid);
                return;
            }
            previewKey = key;
            framesSincePreview = 0;

            var live = new List<Vector3>(points);
            var liveNodes = new List<ushort>(pointNodes);
            var liveSegments = new List<ushort>(pointSegments);
            var liveTs = new List<float>(pointTs);
            var liveLifts = new List<float>(pointLifts);
            var liveStraight = new List<bool>(pointStraight);
            var liveDeck = new List<bool>(pointDeck);
            var liveUnder = new List<bool>(pointUnder);
            var liveRoad = new List<ushort>(pointRoad);
            var liveRoadT = new List<float>(pointRoadT);
            if (withHover)
            {
                live.Add(hover);
                liveNodes.Add(aimNode);
                liveSegments.Add(aimSegment);
                liveTs.Add(aimT);
                liveLifts.Add(lift);
                liveStraight.Add(straightMode);
                liveDeck.Add(deckMode);
                liveUnder.Add(deckMode && underMode);
                liveRoad.Add(deckMode ? aimRoad : (ushort)0);
                liveRoadT.Add(aimRoadT);
            }
            preview = Divide(live, liveNodes, liveSegments, liveTs, liveLifts, liveStraight, liveDeck, liveUnder, liveRoad, liveRoadT);
            // The parallel line beside it (not along decks yet).
            second = parallelMode && !liveDeck.Contains(true) ? DivideParallel(preview, CurrentParallel()) : null;
            // A median ramp road: its ground road beside the ramp.
            if (medianRamp != 0 && preview.Plan != null && !preview.Plan.MedianAlong)
                second = MedianCut ? DivideGround(preview, MedianGroundSplit, 0, preview.GapFrom, preview.GapTo) : DivideGround(preview, MedianGroundSplit);
            cursorText = CursorText(withHover);
            ShowPanel();
            PlaceButtons();
            UpdateCursorLabel(withHover && mouseRayValid);
        }

        // --- precision drawing --------------------------------------------------------

        // Main thread. Where the next point goes. While drawing a straight piece
        // the mouse is snapped: its direction to 15° steps from the last piece
        // (north for the first) or along / across a road nearby, its length to
        // 8 m steps. A typed length or turn holds it (curved pieces too). Shift,
        // or snapping off, draws freely; a point on a road stays on it.
        private void UpdateAim()
        {
            aimGuided = false;
            bool typed = typedLength > 0f || !float.IsNaN(typedTurn);
            Vector3 mouse = hoverPosition;
            bool onMap = hoverValid && mouseRayValid;
            if (onMap) lastMouse = mouse;
            else if (typed && haveLastMouse) mouse = lastMouse;   // typing in the panel: the mouse is over it
            haveLastMouse |= onMap;
            aimValid = onMap;
            aimPosition = mouse;
            aimMarker = hoverMarker;
            aimNode = onMap ? hoverNode : (ushort)0;
            aimSegment = onMap && hoverNode == 0 ? hoverSegment : (ushort)0;
            aimT = hoverT;
            aimRoad = 0;
            aimRoadT = 0f;
            // A deck point on a deck of ours joins it at its level: the deck goes
            // on from there (over the road under it), not a level above it.
            if (deckMode && onMap && !typed && OwnDeckAt(hoverNode, hoverSegment, hoverT))
            {
                RoadUnderDeck(hoverNode, hoverSegment, hoverT, out aimRoad, out aimRoadT);
                return;
            }
            // A deck point on a road sits over its centre line: no junction with it.
            // Not a ramp's first point: that one starts on the road (and rises off it).
            // Nor a median ramp's points coming down: it lands on the road.
            if (deckMode && onMap && !typed && !((rampMode || medianRamp != 0) && points.Count == 0) && !MedianDown &&
                RoadAt(hoverNode, hoverSegment, hoverT, out aimRoad, out aimRoadT))
            {
                aimNode = 0;
                aimSegment = 0;
                return;
            }
            if (planning || points.Count == 0) return;
            if (!onMap && !(typed && haveLastMouse)) return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool snap = snapping && straightMode && !shift;
            if (!typed && (!snap || aimNode != 0 || aimSegment != 0)) return;

            Vector3 from = points[points.Count - 1];
            // The mouse back on the last point (a double-click to finish): not snapped away from it.
            if (typedLength <= 0f && CurveMath.FlatDistance(mouse, from) < LengthStep * 0.5f) return;
            Vector3 reference = points.Count >= 2 ? CurveMath.FlatNormal(from - points[points.Count - 2]) : Vector3.zero;
            if (reference == Vector3.zero) reference = North;
            Vector3 raw = CurveMath.FlatNormal(mouse - from);
            Vector3 direction;
            var kind = CurveMath.SnapKind.None;
            if (!float.IsNaN(typedTurn)) direction = CurveMath.Rotate(reference, -typedTurn);
            else if (snap) direction = CurveMath.SnapDirection(raw, reference, NearbyRoads(from, mouse), AngleStep, RoadSnapTolerance, out kind);
            else direction = raw;
            if (direction == Vector3.zero) return;   // the mouse is on the last point

            float length = typedLength;
            if (length <= 0f)
            {
                length = Mathf.Max(0f, (mouse.x - from.x) * direction.x + (mouse.z - from.z) * direction.z);
                if (snap) length = CurveMath.SnapLength(length, LengthStep);
            }
            if (length < MinPointSpacing) return;

            Vector3 p = from + direction * length;
            p.y = TerrainManager.instance.SampleRawHeightSmoothWithWater(p, false, 0f);
            aimValid = true;
            aimPosition = aimMarker = p;
            aimNode = 0;
            aimSegment = 0;
            aimGuided = true;
            guideFrom = from;
            guideDirection = direction;
            guideLength = length;
            guideTurn = -CurveMath.SignedAngle(reference, direction);
            guideKind = kind;
        }

        private static readonly Vector3 North = new Vector3(0f, 0f, 1f);

        // The road at a node (its first segment, at that end) or on a segment.
        private static bool RoadAt(ushort node, ushort segment, float t, out ushort road, out float roadT)
        {
            road = 0;
            roadT = 0f;
            var net = NetManager.instance;
            if (node != 0)
            {
                for (int i = 0; i < 8 && road == 0; i++) road = net.m_nodes.m_buffer[node].GetSegment(i);
                if (road == 0) return false;
                roadT = net.m_segments.m_buffer[road].m_startNode == node ? 0f : 1f;
                return true;
            }
            if (segment == 0) return false;
            road = segment;
            roadT = t;
            return true;
        }

        // Raised (or, under the road, sunk) this far from the ground, a network of
        // the kind being drawn is a deck to join, not a road to build a deck over.
        private const float DeckJoinHeight = 4f;

        // Whether the node or the point on the segment is on a deck of the
        // network being drawn (any of its versions), up on pillars (or down
        // under the road in Under mode): one to go on from, at its level.
        private bool OwnDeckAt(ushort node, ushort segment, float t)
        {
            var net = NetManager.instance;
            NetInfo info;
            Vector3 p;
            if (node != 0)
            {
                info = net.m_nodes.m_buffer[node].Info;
                p = net.m_nodes.m_buffer[node].m_position;
            }
            else if (segment != 0)
            {
                info = net.m_segments.m_buffer[segment].Info;
                Vector3 a, b, c, d;
                CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
                p = CurveMath.Bezier(a, b, c, d, t);
            }
            else return false;
            // Another road of the same kind (a ramp off a highway deck) joins it
            // too where it is a deck we built or a dead end; elsewhere it is a
            // raised road to build a level over.
            if (!SameRoad(info, SelectedNetwork) &&
                !(RoadPlanner.SameKind(info, SelectedNetwork) && (OurDeckOrEnd(node) ||
                  (node == 0 && (OurDeckOrEnd(net.m_segments.m_buffer[segment].m_startNode) || OurDeckOrEnd(net.m_segments.m_buffer[segment].m_endNode))))))
                return false;
            float elevation = p.y - TerrainManager.instance.SampleRawHeightSmooth(p);
            return underMode ? elevation <= -DeckJoinHeight : elevation >= DeckJoinHeight;
        }

        private static bool OurDeckOrEnd(ushort node)
        {
            if (node == 0) return false;
            if (DeckFrames.Holds(node)) return true;
            int roads = 0;
            for (int i = 0; i < 8; i++) if (NetManager.instance.m_nodes.m_buffer[node].GetSegment(i) != 0) roads++;
            return roads == 1;
        }

        // 'info' is 'road' or one of its versions (elevated, bridge, slope, tunnel).
        private static bool SameRoad(NetInfo info, NetInfo road)
        {
            if (info == null || road == null) return false;
            if (info == road) return true;
            foreach (var kind in new[] { NodeKind.Elevated, NodeKind.Bridge, NodeKind.Slope, NodeKind.Tunnel })
                if (RoadPlanner.InfoFor(road, kind) == info) return true;
            return false;
        }

        // The road under a deck of ours at the node or the point on the segment
        // (0 if none), so the deck going on from there can follow it.
        private void RoadUnderDeck(ushort node, ushort segment, float t, out ushort road, out float roadT)
        {
            road = 0;
            roadT = 0f;
            var net = NetManager.instance;
            var ours = new HashSet<ushort>();
            Vector3 p;
            if (node != 0)
            {
                p = net.m_nodes.m_buffer[node].m_position;
                for (int i = 0; i < 8; i++)
                {
                    ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (s != 0) ours.Add(s);
                }
            }
            else
            {
                Vector3 a, b, c, d;
                CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
                p = CurveMath.Bezier(a, b, c, d, t);
                ours.Add(segment);
            }
            var info = SelectedNetwork;
            var below = Decks.Under(p, info != null ? info.m_halfWidth : 8f, underMode, ours);
            if (below.Segment == 0) return;
            Vector3 onRoad;
            road = below.Segment;
            roadT = CrossingFinder.ClosestT(road, p, out onRoad);
        }

        private Vector3 lastMouse;            // the mouse on the map when it was last there
        private bool haveLastMouse;

        // Directions of the roads near the last point and the mouse (where
        // they are closest to it), for snapping along or across them. Kept
        // while the mouse stays within a few metres.
        private readonly List<Vector3> nearbyRoads = new List<Vector3>();
        private Vector3 nearbyFrom, nearbyMouse;
        private bool nearbyValid;

        private List<Vector3> NearbyRoads(Vector3 from, Vector3 mouse)
        {
            if (nearbyValid && CurveMath.FlatDistance(from, nearbyFrom) < 1f && CurveMath.FlatDistance(mouse, nearbyMouse) < LengthStep)
                return nearbyRoads;
            nearbyValid = true;
            nearbyFrom = from;
            nearbyMouse = mouse;
            nearbyRoads.Clear();
            var seen = new HashSet<ushort>();
            var segments = NetManager.instance.m_segments.m_buffer;
            foreach (var near in new[] { from, mouse })
                foreach (ushort segment in RoadPlanner.NearbySegments(near, RoadSnapReach))
                {
                    if (!seen.Add(segment) || nearbyRoads.Count >= 24) continue;
                    var info = segments[segment].Info;
                    if (info == null || info.m_class == null || !CrossingFinder.IsPhysical(info)) continue;
                    Vector3 onRoad;
                    float t = CrossingFinder.ClosestT(segment, near, out onRoad);
                    if (CurveMath.FlatDistance(onRoad, near) > RoadSnapReach) continue;
                    Vector3 a, b, c, d;
                    CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
                    Vector3 along = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t));
                    if (along != Vector3.zero) nearbyRoads.Add(along);
                }
            return nearbyRoads;
        }

        private void AddAimPoint()
        {
            points.Add(aimPosition);
            pointNodes.Add(aimNode);
            pointMarkers.Add(aimMarker);
            pointSegments.Add(aimSegment);
            pointTs.Add(aimT);
            // The height as set: a point on a road only takes the road's height
            // while it is an end of the curve (see Divide); in the middle it keeps this.
            pointLifts.Add(lift);
            pointStraight.Add(straightMode);
            pointDeck.Add(deckMode);
            pointUnder.Add(deckMode && underMode);
            pointRoad.Add(deckMode ? aimRoad : (ushort)0);
            pointRoadT.Add(aimRoadT);
            choicesVersion++;
            Mod.Log("Point " + points.Count + " at " + Format(aimPosition) + (straightMode ? " (straight)" : "") +
                    (deckMode ? (underMode ? " (under" : " (deck") + (aimRoad != 0 ? (underMode ? " #" : " over #") + aimRoad : "") + ")" : "") +
                    (aimNode != 0 ? " (on node #" + aimNode + ")" : ""));
            // A typed length or turn is for one point.
            ClearTyped();
        }

        // Deck (above) and Under: each on or off; on, the other is off.
        private void ToggleDeck()
        {
            deckMode = !(deckMode && !underMode);
            underMode = false;
            choicesVersion++;
        }

        private void ToggleUnder()
        {
            bool on = !(deckMode && underMode);
            deckMode = on;
            underMode = on;
            choicesVersion++;
        }

        private void SetStraight(bool straight)
        {
            straightMode = straight;
            choicesVersion++;
        }

        private void ChangeRadius(float step)
        {
            float radius = cornerRadius + step;
            if (step < 0f && radius < MinCornerRadius) radius = 0f;
            else if (step > 0f && radius < MinCornerRadius) radius = MinCornerRadius;
            cornerRadius = Mathf.Clamp(radius, 0f, MaxCornerRadius);
            choicesVersion++;
        }

        private void ToggleSnapping()
        {
            snapping = !snapping;
            choicesVersion++;
        }

        // From the panel's Length and Turn fields; empty or unreadable: not held.
        private void SetTyped(string length, string turn)
        {
            float value;
            typedLength = ReadNumber(length, out value) && value >= MinPointSpacing && value <= 10000f ? value : -1f;
            typedTurn = ReadNumber(turn, out value) && Mathf.Abs(value) <= 180f ? value : float.NaN;
            choicesVersion++;
        }

        private static bool ReadNumber(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(text)) return false;
            text = text.Trim().Replace(',', '.');
            return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private void ClearTyped()
        {
            bool had = typedLength > 0f || !float.IsNaN(typedTurn);
            typedLength = -1f;
            typedTurn = float.NaN;
            if (had) panel.ClearTyped();
        }

        // The first line of the label by the mouse: the piece being drawn.
        private string PieceText()
        {
            string mode = (deckMode ? (underMode ? "Under, " : "Deck, ") : "") + (straightMode ? "straight" : "curve");
            mode = char.ToUpper(mode[0]) + mode.Substring(1);
            if (deckMode && aimRoad != 0)
            {
                var road = NetManager.instance.m_segments.m_buffer[aimRoad].Info;
                return mode + "   ·   " + (underMode ? "under" : "over") + " the middle of the " +
                       (road != null ? road.GetUncheckedLocalizedTitle() ?? road.name : "road");
            }
            if (!aimGuided) return mode + "   (Tab: " + (straightMode ? "curve" : "straight") + ")";
            string turn;
            if (points.Count < 2)
                turn = "bearing " + Mathf.RoundToInt((guideTurn + 360f) % 360f) + "°";
            else if (Mathf.Abs(guideTurn) < 0.5f)
                turn = "straight on";
            else
                turn = Mathf.RoundToInt(Mathf.Abs(guideTurn)) + "° " + (guideTurn > 0f ? "right" : "left");
            string how = guideKind == CurveMath.SnapKind.Parallel ? "   ·   along the road"
                : guideKind == CurveMath.SnapKind.Perpendicular ? "   ·   square to the road" : "";
            return mode + "   ·   " + guideLength.ToString("N0") + " m   ·   " + turn + how + (straightMode ? "   (Shift: free)" : "");
        }

        // --- the label next to the mouse -------------------------------------------

        private UILabel cursorLabel;
        private string cursorText = "";

        // What the next point will be and how the curve ends there: its height
        // (Page Up / Page Down), the planned height at the mouse, the grade of
        // the last piece and the length so far.
        private string CursorText(bool withHover)
        {
            if (!withHover) return "";
            var lines = new List<string>();
            if (points.Count > 0 || deckMode) lines.Add(PieceText());
            if (aimNode != 0) lines.Add("Joins the road end here");
            else if (aimSegment != 0) lines.Add("Joins this road here (T-junction)");
            else if (deckMode) lines.Add(underMode ? "One level under the roads (a tunnel)" : "A level above the roads below");
            else lines.Add("Height " + FormatLift(lift) + "   (Page Up / Page Down)");
            var division = preview;
            if (division != null && division.Plan != null && division.Plan.Points.Count >= 2)
            {
                var plan = division.Plan;
                int last = plan.Points.Count - 1;
                Vector3 end = plan.Points[last], before = plan.Points[last - 1];
                float above = end.y - TerrainManager.instance.SampleRawHeightSmoothWithWater(end, false, 0f);
                float run = CurveMath.FlatDistance(before, end);
                float grade = run > 0.5f ? Mathf.Abs(end.y - before.y) / run : 0f;
                string here = above >= 1f ? above.ToString("F0") + " m up" : above <= -1f ? (-above).ToString("F0") + " m down" : "on the ground";
                lines.Add(division.Length.ToString("N0") + " m   ·   here " + here + "   ·   grade " + (grade * 100f).ToString("0.#") + " %");
                if (!division.Ok) lines.Add("A bend is too sharp");
                else if (!plan.Ok) lines.Add("Can't build yet: see the panel");
            }
            return string.Join("\n", lines.ToArray());
        }

        // Follows the mouse while it is over the map.
        private void UpdateCursorLabel(bool show)
        {
            var view = UIView.GetAView();
            if (view == null) return;
            if (cursorLabel == null)
            {
                cursorLabel = (UILabel)view.AddUIComponent(typeof(UILabel));
                cursorLabel.name = "RealisticRoadsCursor";
                cursorLabel.backgroundSprite = "GenericPanel";
                cursorLabel.padding = new RectOffset(8, 8, 5, 5);
                cursorLabel.textScale = 0.75f;
                cursorLabel.autoSize = true;
                cursorLabel.isInteractive = false;
                cursorLabel.isVisible = false;
            }
            if (!show || string.IsNullOrEmpty(cursorText))
            {
                if (cursorLabel.isVisible) cursorLabel.isVisible = false;
                return;
            }
            if (cursorLabel.text != cursorText) cursorLabel.text = cursorText;
            Vector2 gui = view.ScreenPointToGUI(Input.mousePosition / view.inputScale);
            cursorLabel.relativePosition = new Vector3(gui.x + 22f, gui.y + 18f);
            if (!cursorLabel.isVisible) cursorLabel.isVisible = true;
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
            var hover = new Hover();
            if (mouseRayValid) hover = FindHover();
            lock (hoverLock) simHover = hover;
        }

        // Simulation thread. What the mouse is on: a node, the middle of a
        // road, or the ground (or nothing).
        private Hover FindHover()
        {
            Vector3 ground;
            bool onGround = GroundUnderMouse(out ground);

            ushort midSegment;
            Vector3 roadHit;
            ushort node = FindSnapNode(out midSegment, out roadHit);
            // A median cut starts (or ends) on a track's end, which may be in a
            // tunnel under the road the mouse is on: that end is taken first.
            if (MedianCut && (node == 0 || !OnCutTrack(node, 0)))
            {
                Vector3 near = node != 0 ? NetManager.instance.m_nodes.m_buffer[node].m_position : midSegment != 0 ? roadHit : ground;
                ushort trackEnd = (node != 0 || midSegment != 0 || onGround) ? CutTrackEndNear(near) : (ushort)0;
                if (trackEnd != 0)
                {
                    Vector3 at = NetManager.instance.m_nodes.m_buffer[trackEnd].m_position;
                    return new Hover { Valid = true, Node = trackEnd, Position = at, Marker = at };
                }
            }
            if (node == 0 && midSegment != 0)
            {
                // On the middle of a road: join it there (cut at build time).
                Vector3 onRoad;
                float t = CrossingFinder.ClosestT(midSegment, roadHit, out onRoad);
                float length = NetManager.instance.m_segments.m_buffer[midSegment].m_averageLength;
                if (t * length > Crossing.NodeSnap && (1f - t) * length > Crossing.NodeSnap)
                    return new Hover { Valid = true, Segment = midSegment, T = t, Position = onRoad, Marker = onRoad };
            }
            if (node != 0)
            {
                Vector3 position = NetManager.instance.m_nodes.m_buffer[node].m_position;
                return new Hover { Valid = true, Node = node, Position = position, Marker = position };
            }
            return new Hover { Valid = onGround, Position = ground, Marker = ground };
        }

        // Simulation thread. A node under the mouse, or the end of the road
        // under the mouse when the mouse is near that end. Otherwise 0, with
        // the road under the mouse (if any) in 'segmentHit'.
        private ushort FindSnapNode(out ushort segmentHit, out Vector3 hit)
        {
            segmentHit = 0;
            hit = Vector3.zero;
            // A median cut starts (or ends) on a track's end, which may be in a
            // tunnel: the metro (and train) tunnels' layer is looked through too.
            var layers = MedianCut ? ItemClass.Layer.Default | ItemClass.Layer.MetroTunnels : ItemClass.Layer.Default;
            var input = new RaycastInput(simRay, simRayLength);
            input.m_netService = new RaycastService { m_itemLayers = layers };
            input.m_ignoreTerrain = true;
            input.m_ignoreNodeFlags = NetNode.Flags.None;
            RaycastOutput output;
            if (RayCast(input, out output) && output.m_netNode != 0) return output.m_netNode;

            input = new RaycastInput(simRay, simRayLength);
            input.m_netService = new RaycastService { m_itemLayers = layers };
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
            // Steps as long as half the height above the ground (2 to 64 m): few
            // samples high up, short ones near the ground, so a ridge seen at a
            // grazing angle isn't stepped over.
            const float minStep = 2f, maxStep = 64f;
            Vector3 previous = origin;
            for (float d = minStep; d <= simRayLength;)
            {
                Vector3 p = origin + direction * d;
                float surface = terrain.SampleRawHeightSmoothWithWater(p, false, 0f);
                if (p.y > surface)
                {
                    previous = p;
                    d += Mathf.Clamp((p.y - surface) * 0.5f, minStep, maxStep);
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
                // Typing a length or turn in the panel: those keys are the field's.
                if (UIView.HasInputFocus()) return;
                if (e.keyCode == KeyCode.Tab)
                {
                    e.Use();
                    SetStraight(!straightMode);
                }
                else if (e.keyCode == KeyCode.Escape)
                {
                    e.Use();
                    // Esc also stops waiting for a road for beside the curve (not
                    // ClearPoints: picking it in the menu leaves and reopens the tool).
                    // One step back at a time: planning -> drawing (the points kept)
                    // -> the curve cleared -> out of the tool.
                    pickingBeside = false;
                    if (planning)
                    {
                        planning = false;
                        dragIndex = -1;
                        choicesVersion++;
                        Mod.Log("Esc: back to drawing (the points are kept; Esc again clears them)");
                    }
                    else if (points.Count > 0 || upgradePoints.Count > 0)
                    {
                        ClearPoints();
                        Mod.Log("Curve cleared (Esc again leaves the tool)");
                    }
                    else
                    {
                        Mod.Log("Esc pressed, back to the vanilla tool");
                        ToolActivator.LeaveMode();
                        ToolsModifierControl.SetTool<DefaultTool>();
                    }
                }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                {
                    // Drawing: done, check and adjust the plan. Planning (or upgrading): build it.
                    e.Use();
                    if (planning || upgradeMode) BuildCurrent();
                    else StartPlanning();
                }
                else if (e.keyCode == KeyCode.Z && e.control)
                {
                    e.Use();
                    NetworkBuilder.UndoLast();
                }
                else if (e.keyCode == KeyCode.D && e.control && e.shift)
                {
                    // The node under the mouse (and any stacked with it) to the log.
                    e.Use();
                    NetworkBuilder.DumpNodesNear(aimNode, aimPosition);
                }
                else if (e.keyCode == KeyCode.PageUp || e.keyCode == KeyCode.PageDown)
                {
                    e.Use();
                    float step = e.shift ? 1f : LiftStep;
                    ChangeLift(e.keyCode == KeyCode.PageUp ? step : -step);
                }
                return;
            }

            if (e.type != EventType.MouseDown || m_toolController.IsInsideUI) return;

            if (upgradeMode)
            {
                // Upgrading: a click on a road is where it starts, the next where it
                // ends (a third starts again); right-click takes the last one back.
                UpgradePoint point;
                if (e.button == 0 && UpgradePointAt(out point))
                {
                    e.Use();
                    if (upgradePoints.Count >= 2) upgradePoints.Clear();
                    upgradePoints.Add(point);
                    choicesVersion++;
                    Mod.Log("Upgrade: " + (upgradePoints.Count == 1 ? "from" : "to") + " segment #" + point.Segment);
                }
                else if (e.button == 1 && upgradePoints.Count > 0)
                {
                    e.Use();
                    upgradePoints.RemoveAt(upgradePoints.Count - 1);
                    choicesVersion++;
                }
                return;
            }

            if (planning)
            {
                // Planning: clicks never add points. Grab a point to move it;
                // right-click goes back to drawing (adding points at the end).
                // Shift+click on the curve puts a point in there; right-click on a
                // point takes that one out.
                if (e.button == 0 && e.shift)
                {
                    e.Use();
                    InsertPointUnderMouse();
                }
                else if (e.button == 0)
                {
                    e.Use();
                    dragIndex = PointUnderMouse();
                }
                else if (e.button == 1 && PointUnderMouse() >= 0 && points.Count > 2)
                {
                    e.Use();
                    RemovePointAt(PointUnderMouse());
                }
                else if (e.button == 1)
                {
                    e.Use();
                    planning = false;
                    dragIndex = -1;
                    choicesVersion++;
                    Mod.Log("Drawing again: new points are added at the end");
                }
                return;
            }

            if (e.button == 0 && aimValid)
            {
                e.Use();
                if (points.Count > 0 && (aimPosition - points[points.Count - 1]).sqrMagnitude < MinPointSpacing * MinPointSpacing)
                {
                    // Double-click (or a second click on the last point): done drawing.
                    StartPlanning();
                    return;
                }
                AddAimPoint();
            }
            else if (e.button == 1 && points.Count > 0)
            {
                e.Use();
                points.RemoveAt(points.Count - 1);
                pointNodes.RemoveAt(pointNodes.Count - 1);
                pointMarkers.RemoveAt(pointMarkers.Count - 1);
                pointSegments.RemoveAt(pointSegments.Count - 1);
                pointTs.RemoveAt(pointTs.Count - 1);
                pointLifts.RemoveAt(pointLifts.Count - 1);
                pointStraight.RemoveAt(pointStraight.Count - 1);
                pointDeck.RemoveAt(pointDeck.Count - 1);
                pointUnder.RemoveAt(pointUnder.Count - 1);
                pointRoad.RemoveAt(pointRoad.Count - 1);
                pointRoadT.RemoveAt(pointRoadT.Count - 1);
                ForgetShapeBeyond(points.Count - 1);
                choicesVersion++;
                Mod.Log("Removed last point, " + points.Count + " left");
            }
        }

        // Main thread. The road itself, in 3D at its planned heights (bridges,
        // flyovers, ramps and cuttings as they will look), like the vanilla
        // road tool's preview.
        public override void RenderGeometry(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderGeometry(cameraInfo);
            foreach (var division in new[] { preview, second })
                if (division != null && division.Points.Count >= 2 && division.Plan != null)
                    RoadPreview.Render(division.Setup != null ? division.Setup.Info : SelectedNetwork, division.Plan);
        }

        // Main thread. Colours the curve as it will be built, marks where the
        // pillars stand, and draws the points.
        public override void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            base.RenderOverlay(cameraInfo);
            var overlay = Singleton<RenderManager>.instance.OverlayEffect;
            if (upgradeMode)
            {
                DrawUpgrade(cameraInfo, overlay);
                return;
            }
            DrawPlanned(cameraInfo, overlay, preview);
            DrawPlanned(cameraInfo, overlay, second);

            // Where the mouse is (or was pinned) on the panel's side view.
            Vector3 marked;
            if (panel.TryGetCursor(out marked))
            {
                float size = (SelectedNetwork != null ? SelectedNetwork.m_halfWidth * 2f : 8f) + 10f;
                overlay.DrawCircle(cameraInfo, CursorColor, marked, size, marked.y - 6f, marked.y + 6f, false, true);
            }
            DrawPoints(cameraInfo, overlay);
        }

        // A planned curve on the map in the colours of how it will be built,
        // its pillars and its crossings.
        private void DrawPlanned(RenderManager.CameraInfo cameraInfo, OverlayEffect overlay, Division division)
        {
            if (division != null && division.Points.Count >= 2 && division.Plan != null)
            {
                var info = division.Setup != null && division.Setup.Info != null ? division.Setup.Info : SelectedNetwork;
                float width = info != null ? info.m_halfWidth * 2f : 8f;
                var plan = division.Plan;
                var kinds = plan.Kinds;
                bool inThreeD = RoadPreview.Available;
                for (int i = 1; i < plan.Points.Count; i++)
                {
                    var style = StyleOf(plan, i);
                    bool lowered = IsLowered(kinds[i - 1]) || IsLowered(kinds[i]);
                    Color color = style == ProfileStyle.TooSharp ? BadColor
                        : style == ProfileStyle.Steep ? SteepColor
                        : style == ProfileStyle.Raised ? BridgeColor
                        : style == ProfileStyle.Lowered ? LowColor
                        : GoodColor;
                    if (inThreeD) DrawAtHeight(cameraInfo, overlay, color, RoadPreview.PieceBezier(plan, i), width, lowered);
                    else overlay.DrawBezier(cameraInfo, color, PieceBezier(division, i), width, 100000f, -100000f, -1f, 1280f, false, true);
                }
                DrawPillarSpots(cameraInfo, overlay, division);
                // Buildings it would run through: a red ring round each.
                var buildings = BuildingManager.instance.m_buildings.m_buffer;
                foreach (ushort id in division.Blocking)
                {
                    var b = buildings[id];
                    if ((b.m_flags & Building.Flags.Created) == Building.Flags.None || b.Info == null) continue;
                    float size = Mathf.Max(b.Info.m_cellWidth, b.Info.m_cellLength) * 8f * 1.15f + 4f;
                    overlay.DrawCircle(cameraInfo, BadColor, b.m_position, size, -1f, 1280f, false, true);
                }

                DrawCrossedRoads(cameraInfo, overlay, division, info, inThreeD);

                var problems = !division.Plan.Ok;
                foreach (var crossing in division.Crossings)
                {
                    Color color = problems ? BadColor
                        : crossing.Type == CrossingType.Bridge || crossing.Type == CrossingType.Overpass ? BridgeColor
                        : crossing.Type == CrossingType.Underpass || crossing.Type == CrossingType.Tunnel ? LowColor
                        : JunctionColor;
                    // On the crossed road only (not on our deck above it), when in 3D.
                    float low = inThreeD ? crossing.Point.y - 4f : -1f, high = inThreeD ? crossing.Point.y + 4f : 1280f;
                    overlay.DrawCircle(cameraInfo, color, crossing.Point, width + 6f, low, high, false, true);
                }
            }
        }

        // What happens to the roads crossed: orange along a road lowered under
        // an overpass (the crossed piece and the pieces on from the nodes moved
        // down with it), blue along the stretch of a road that becomes a short
        // bridge over an underpass. Drawn at that road's own height.
        private void DrawCrossedRoads(RenderManager.CameraInfo cameraInfo, OverlayEffect overlay, Division division, NetInfo info, bool inThreeD)
        {
            var plan = division.Plan;
            var net = NetManager.instance;
            var segments = net.m_segments.m_buffer;
            var drawn = new HashSet<ushort>();
            foreach (var pair in plan.Lowerings)
            {
                var lowering = pair.Value;
                DrawCrossedSegment(cameraInfo, overlay, lowering.Segment, 0f, 1f, LowColor, drawn, inThreeD);
                foreach (ushort node in lowering.Nodes)
                    for (int k = 0; k < 8; k++)
                    {
                        ushort segment = net.m_nodes.m_buffer[node].GetSegment(k);
                        if (segment != 0) DrawCrossedSegment(cameraInfo, overlay, segment, 0f, 1f, LowColor, drawn, inThreeD);
                    }
            }
            if (info == null) return;
            foreach (var crossing in plan.Crossings)
            {
                if (crossing.Type != CrossingType.Underpass || !crossing.ExistingAtGround) continue;
                if ((segments[crossing.Segment].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                float length = Mathf.Max(segments[crossing.Segment].m_averageLength, 1f);
                float reach = NetworkBuilder.UnderpassReach(crossing, info) / length;
                DrawCrossedSegment(cameraInfo, overlay, crossing.Segment, Mathf.Clamp01(crossing.ExistingT - reach),
                    Mathf.Clamp01(crossing.ExistingT + reach), BridgeColor, null, inThreeD);
            }
        }

        private static void DrawCrossedSegment(RenderManager.CameraInfo cameraInfo, OverlayEffect overlay, ushort segment, float from, float to,
            Color color, HashSet<ushort> drawn, bool inThreeD)
        {
            if (segment == 0 || to - from < 0.01f || (drawn != null && !drawn.Add(segment))) return;
            var s = NetManager.instance.m_segments.m_buffer[segment];
            if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null) return;
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
            var bezier = new Bezier3(a, b, c, d).Cut(from, to);
            float low = inThreeD ? Mathf.Min(bezier.a.y, bezier.d.y) - 6f : -1f;
            float high = inThreeD ? Mathf.Max(bezier.a.y, bezier.d.y) + 6f : 1280f;
            overlay.DrawBezier(cameraInfo, color, bezier, s.Info.m_halfWidth * 2f + 2f, 0f, 0f, low, high, false, true);
        }

        // The points (handles while planning), and the next point at the mouse.
        private void DrawPoints(RenderManager.CameraInfo cameraInfo, OverlayEffect overlay)
        {
            // While planning the points are handles: bigger, the grabbed one white.
            for (int i = 0; i < points.Count; i++)
            {
                // Yellow: joined to a road, which only an end of the curve is.
                bool end = i == 0 || i == points.Count - 1;
                Color color = i == dragIndex ? HoverColor : end && (pointNodes[i] != 0 || pointSegments[i] != 0) ? SnapColor : PointColor;
                overlay.DrawCircle(cameraInfo, color, pointMarkers[i], planning ? HandleSize : PointSize, -1f, 1280f, false, true);
            }

            if (aimValid && (mouseRayValid || aimGuided) && !planning)
            {
                overlay.DrawCircle(cameraInfo, aimNode != 0 || aimSegment != 0 ? SnapColor : HoverColor, aimMarker, HoverSize, -1f, 1280f, false, true);
                // The guide line of a snapped or typed straight piece, on past the point.
                if (aimGuided)
                {
                    Vector3 a = guideFrom, d = guideFrom + guideDirection * (guideLength + GuideBeyond);
                    var guide = new Bezier3 { a = a, b = Vector3.Lerp(a, d, 1f / 3f), c = Vector3.Lerp(a, d, 2f / 3f), d = d };
                    bool road = guideKind == CurveMath.SnapKind.Parallel || guideKind == CurveMath.SnapKind.Perpendicular;
                    overlay.DrawBezier(cameraInfo, road ? SnapColor : GuideColor, guide, GuideWidth, 0f, 0f, -1f, 1280f, false, true);
                }
            }
        }

        // The colour only on what is at the road's own height: the 3D preview's
        // deck for a raised piece (not the ground under it, which made a
        // flyover look like a flat band), the ground for a piece on it. A
        // lowered piece is shown on the ground above, where it will be dug.
        // In short pieces, each with its own height range.
        private static void DrawAtHeight(RenderManager.CameraInfo cameraInfo, OverlayEffect overlay, Color color, Bezier3 bezier,
            float width, bool lowered)
        {
            var terrain = TerrainManager.instance;
            float length = CurveMath.FlatDistance(bezier.a, bezier.d);
            float rise = Mathf.Abs(bezier.d.y - bezier.a.y);
            // Level pieces in one go; ramps in short parts (about 1.5 m of rise each).
            int pieces = rise < 3f ? 1 : Mathf.Clamp(Mathf.CeilToInt(rise / 1.5f), 2, 16);
            if (lowered) pieces = Mathf.Max(pieces, Mathf.Clamp(Mathf.CeilToInt(length / 24f), 1, 8));
            for (int k = 0; k < pieces; k++)
            {
                var part = bezier.Cut(k / (float)pieces, (k + 1) / (float)pieces);
                float low = Mathf.Min(Mathf.Min(part.a.y, part.b.y), Mathf.Min(part.c.y, part.d.y));
                float high = Mathf.Max(Mathf.Max(part.a.y, part.b.y), Mathf.Max(part.c.y, part.d.y));
                if (lowered) high = Mathf.Max(high, terrain.SampleRawHeightSmooth((part.a + part.d) * 0.5f));
                overlay.DrawBezier(cameraInfo, color, part, width, 100000f, -100000f, low - 2f, high + 2f, false, true);
            }
        }

        // A small ring on the ground (or the water) under every raised node
        // that gets a pillar, so it is easy to see none lands on a road or
        // track.
        private void DrawPillarSpots(RenderManager.CameraInfo cameraInfo, OverlayEffect overlay, Division division)
        {
            var plan = division.Plan;
            var terrain = TerrainManager.instance;
            var info = division.Setup != null && division.Setup.Info != null ? division.Setup.Info : SelectedNetwork;
            float ourHalf = info != null ? info.m_halfWidth : 8f;
            foreach (int i in PillarNodes(division))
            {
                Vector3 p = plan.Points[i];
                Vector3 a, b;
                var support = plan.OnDeck != null && plan.OnDeck[i] ? Decks.SupportFor(p, plan.Decks[i]) : Decks.Support.Pillar;
                if (support != Decks.Support.Pillar && DeckFrames.Beam(plan.Decks[i], ourHalf, support == Decks.Support.Column, out a, out b))
                {
                    // Our support: a column on the median, or a frame's two columns beside
                    // the road below; and the beam under the deck.
                    var feet = support == Decks.Support.Column ? new[] { plan.Decks[i].Center } : new[] { a, b };
                    foreach (var foot in feet)
                    {
                        float footGround = support == Decks.Support.Column ? foot.y : terrain.SampleRawHeightSmooth(foot);
                        overlay.DrawCircle(cameraInfo, PillarColor, new Vector3(foot.x, footGround, foot.z), 3f, footGround - 4f, footGround + 4f, false, true);
                    }
                    float beam = p.y - 2f;
                    Vector3 from = new Vector3(a.x, beam, a.z), to = new Vector3(b.x, beam, b.z);
                    var line = new Bezier3 { a = from, b = Vector3.Lerp(from, to, 1f / 3f), c = Vector3.Lerp(from, to, 2f / 3f), d = to };
                    overlay.DrawBezier(cameraInfo, PillarColor, line, 1.5f, 0f, 0f, beam - 3f, beam + 3f, false, true);
                    continue;
                }
                float ground = terrain.SampleRawHeightSmoothWithWater(p, false, 0f);
                overlay.DrawCircle(cameraInfo, PillarColor, new Vector3(p.x, ground, p.z), 5f, ground - 4f, ground + 4f, false, true);
            }
        }

        // The plan's nodes that get a pillar: raised ones (not the ends) high
        // enough for one, except a node over a road or track we bridge (the
        // builder gives that one none).
        private static List<int> PillarNodes(Division division)
        {
            var plan = division.Plan;
            var noPillar = new HashSet<int>();
            for (int c = 0; c < division.Crossings.Count; c++)
            {
                var type = division.Crossings[c].Type;
                if (type == CrossingType.Bridge || type == CrossingType.Overpass) noPillar.Add(division.CrossingIndex[c]);
            }
            var terrain = TerrainManager.instance;
            var result = new List<int>();
            for (int i = 1; i < plan.Points.Count - 1; i++)
            {
                if (!IsRaised(plan.Kinds[i]) || noPillar.Contains(i)) continue;
                Vector3 p = plan.Points[i];
                if (p.y - terrain.SampleRawHeightSmoothWithWater(p, false, 0f) < MinPillarHeight) continue;   // too low for a pillar
                result.Add(i);
            }
            return result;
        }

        // How piece i (between points i-1 and i) is built, for its colour on
        // the map and in the profile.
        private static ProfileStyle StyleOf(Plan plan, int i)
        {
            if (CurveMath.BendAngle(plan.Tangents[i - 1], plan.InTangent(i)) > MaxBend) return ProfileStyle.TooSharp;
            if (plan.Steep != null && i < plan.Steep.Length && plan.Steep[i]) return ProfileStyle.Steep;
            if (IsRaised(plan.Kinds[i - 1]) || IsRaised(plan.Kinds[i])) return ProfileStyle.Raised;
            if (IsLowered(plan.Kinds[i - 1]) || IsLowered(plan.Kinds[i])) return ProfileStyle.Lowered;
            return ProfileStyle.Ground;
        }

        // The curve seen from the side, for the panel: road height, ground and
        // water at evenly spaced points, pillars and crossed roads.
        private const int ProfileSamples = 160;

        private static PanelProfile MakeProfile(Division division, out string text)
        {
            text = "";
            var plan = division.Plan;
            int n = plan.Points.Count;
            if (n < 2) return null;
            var along = ChordAlong(plan);
            float length = along[n - 1];
            if (length < 1f) return null;

            var terrain = TerrainManager.instance;
            var profile = new PanelProfile
            {
                Length = length,
                Road = new float[ProfileSamples],
                Ground = new float[ProfileSamples],
                Water = new float[ProfileSamples],
                Style = new ProfileStyle[ProfileSamples],
                Positions = new Vector3[ProfileSamples],
            };
            float highest = 0f, deepest = 0f;
            int piece = 1;
            var bezier = RoadPreview.PieceBezier(plan, 1);
            for (int s = 0; s < ProfileSamples; s++)
            {
                float d = length * s / (ProfileSamples - 1);
                int before = piece;
                while (piece < n - 1 && along[piece] < d) piece++;
                if (piece != before) bezier = RoadPreview.PieceBezier(plan, piece);
                float span = along[piece] - along[piece - 1];
                float t = span > 0.01f ? Mathf.Clamp01((d - along[piece - 1]) / span) : 0f;
                Vector3 p = bezier.Position(t);
                profile.Road[s] = p.y;
                profile.Positions[s] = p;
                profile.Ground[s] = terrain.SampleRawHeightSmooth(p);
                profile.Water[s] = terrain.SampleRawHeightSmoothWithWater(p, false, 0f);
                profile.Style[s] = StyleOf(plan, piece);
                highest = Mathf.Max(highest, p.y - profile.Water[s]);
                deepest = Mathf.Max(deepest, profile.Ground[s] - p.y);
            }
            foreach (int i in PillarNodes(division)) profile.Pillars.Add(along[i]);
            // The nodes (dragged up and down in the side view) and the marked stretches.
            profile.NodeAlong = along;
            profile.NodeHeight = new float[n];
            profile.NodePinned = new bool[n];
            profile.NodeHeld = new bool[n];
            for (int i = 0; i < n; i++)
            {
                profile.NodeHeight[i] = plan.Points[i].y;
                profile.NodeHeld[i] = plan.Held != null && i < plan.Held.Length && plan.Held[i];
                foreach (var height in division.Heights)
                    if (CurveMath.FlatDistance(height.Key, plan.Points[i]) < PinSnap) profile.NodePinned[i] = true;
            }
            if (division.NodeAlong != null && division.NodeAlong.Length == n)
                foreach (var range in division.ForcedRanges)
                    profile.Stretches.Add(new PanelStretch
                    {
                        From = Mathf.Clamp(CurveMath.Remap(division.NodeAlong, along, range.From), 0f, length),
                        To = Mathf.Clamp(CurveMath.Remap(division.NodeAlong, along, range.To), 0f, length),
                        Kind = range.Kind,
                    });
            foreach (var crossing in division.Crossings)
                profile.Crossings.Add(new KeyValuePair<float, float>(Mathf.Clamp(crossing.Distance, 0f, length), crossing.Point.y));

            float steepest = 0f;
            for (int i = 1; i < n; i++)
                if (along[i] - along[i - 1] > 0.5f)
                    steepest = Mathf.Max(steepest, Mathf.Abs(plan.Points[i].y - plan.Points[i - 1].y) / (along[i] - along[i - 1]));
            var parts = new List<string>();
            if (highest >= 1f) parts.Add("up to " + highest.ToString("F0") + " m above the ground");
            if (deepest >= 1f) parts.Add("down to " + deepest.ToString("F0") + " m below it");
            parts.Add("steepest " + (steepest * 100f).ToString("0.#") + " %");
            if (profile.Pillars.Count > 0) parts.Add(profile.Pillars.Count + " pillar(s)");
            text = string.Join(", ", parts.ToArray());
            text = char.ToUpper(text[0]) + text.Substring(1);
            return profile;
        }

        // Each node's distance along the straight pieces between them: the side view's scale.
        private static float[] ChordAlong(Plan plan)
        {
            int n = plan.Points.Count;
            var along = new float[n];
            for (int i = 1; i < n; i++) along[i] = along[i - 1] + CurveMath.FlatDistance(plan.Points[i - 1], plan.Points[i]);
            return along;
        }

        private const float PinSnap = 6f;   // a dragged height belongs to the node this close to it (the planner's LiftSnap)

        // Starts dragging node i's height in the side view (false: its height
        // is set by an end or a crossing, or it is on the piece to the mouse).
        private bool GrabNode(int i)
        {
            var division = preview;
            if (division == null || division.Plan == null || division.ControlAt == null || division.NodeAlong == null) return false;
            var plan = division.Plan;
            if (i < 0 || i >= plan.Points.Count || i >= division.NodeAlong.Length) return false;
            if (plan.Held != null && i < plan.Held.Length && plan.Held[i]) return false;
            float place = CurveMath.PlaceOf(division.ControlAt, division.NodeAlong[i]);
            if (place > points.Count - 1 + 0.0001f) return false;
            draggedPin = PinNear(division, division.NodeAlong[i]);
            if (draggedPin == null)
            {
                draggedPin = new HeightPin { At = place, Height = plan.Points[i].y };
                heightPins.Add(draggedPin);
            }
            draggedGround = TerrainManager.instance.SampleRawHeightSmooth(plan.Points[i]);
            return true;
        }

        // The dragged height follows the mouse, in half metres.
        private void DragHeight(float height)
        {
            if (draggedPin == null) return;
            height = Mathf.Round(Mathf.Clamp(height, draggedGround + MinPinHeight, draggedGround + MaxLift) * 2f) / 2f;
            if (Mathf.Abs(height - draggedPin.Height) < 0.01f) return;
            draggedPin.Height = height;
            choicesVersion++;
        }

        // The mouse wheel over a dot in the side view: its height held, 'step' up or down.
        private void NudgeNode(int i, float step)
        {
            var division = preview;
            if (division == null || division.Plan == null || i < 0 || i >= division.Plan.Points.Count) return;
            float current = division.Plan.Points[i].y;
            if (!GrabNode(i)) return;
            DragHeight(draggedPin.Height + step);
            if (Mathf.Abs(draggedPin.Height - current) < 0.01f) DragHeight(current + step);
            ReleaseNode();
        }

        private void ReleaseNode()
        {
            if (draggedPin == null) return;
            Mod.Log("Side view: height held at " + draggedPin.Height.ToString("F1") + " m (" + FormatLift(draggedPin.Height - draggedGround) +
                    " from the ground)");
            draggedPin = null;
        }

        // Right-click on a held height: the planner decides it again.
        private void FreeNode(int i)
        {
            var division = preview;
            if (division == null || division.NodeAlong == null || i < 0 || i >= division.NodeAlong.Length) return;
            var pin = PinNear(division, division.NodeAlong[i]);
            if (pin == null) return;
            heightPins.Remove(pin);
            choicesVersion++;
            Mod.Log("Side view: height freed");
        }

        private HeightPin PinNear(Division division, float along)
        {
            foreach (var pin in heightPins)
            {
                float at;
                if (CurveMath.PlaceAlong(division.ControlAt, pin.At, out at) && Mathf.Abs(at - along) < PinSnap) return pin;
            }
            return null;
        }

        // A stretch selected in the side view (its metres) made Elevated, Tunnel
        // or left to the planner (None). Heights held in it are let go, the
        // crossings in it go back to the stretch's way of crossing, and water
        // in it is bridged (Elevated) or tunnelled under (Tunnel).
        private void MarkStretch(float from, float to, Forced kind)
        {
            var division = preview;
            if (division == null || division.Plan == null || division.ControlAt == null || division.NodeAlong == null) return;
            var chord = ChordAlong(division.Plan);
            if (chord.Length != division.NodeAlong.Length) return;
            float alongFrom = CurveMath.Remap(chord, division.NodeAlong, Mathf.Min(from, to));
            float alongTo = CurveMath.Remap(chord, division.NodeAlong, Mathf.Max(from, to));
            float last = points.Count - 1;
            float placeFrom = Mathf.Min(CurveMath.PlaceOf(division.ControlAt, alongFrom), last);
            float placeTo = Mathf.Min(CurveMath.PlaceOf(division.ControlAt, alongTo), last);
            if (placeTo - placeFrom < 0.0001f) return;

            var kept = new List<ForcedStretch>();
            foreach (var stretch in stretches)
            {
                if (stretch.To <= placeFrom || stretch.From >= placeTo)
                {
                    kept.Add(stretch);
                    continue;
                }
                if (stretch.From < placeFrom) kept.Add(new ForcedStretch { From = stretch.From, To = placeFrom, Kind = stretch.Kind });
                if (stretch.To > placeTo) kept.Add(new ForcedStretch { From = placeTo, To = stretch.To, Kind = stretch.Kind });
            }
            if (kind != Forced.None) kept.Add(new ForcedStretch { From = placeFrom, To = placeTo, Kind = kind });
            stretches.Clear();
            stretches.AddRange(kept);
            heightPins.RemoveAll(pin => pin.At >= placeFrom && pin.At <= placeTo);
            foreach (var crossing in division.Crossings)
                if (crossing.Distance >= alongFrom && crossing.Distance <= alongTo) crossingTypes.Remove(crossing.Key);
            if (kind != Forced.None)
                foreach (var run in division.WaterRuns)
                    if (run.To >= alongFrom && run.From <= alongTo) waterTunnel[run.Key] = kind == Forced.Tunnel;
            choicesVersion++;
            Mod.Log("Side view: " + alongFrom.ToString("N0") + " to " + alongTo.ToString("N0") + " m " +
                    (kind == Forced.Elevated ? "elevated" : kind == Forced.Tunnel ? "in a tunnel" : "left to the planner"));
        }

        // A stretch selected in the side view (its metres) on one even slope:
        // from its first node to its last at their heights now, every node
        // between held on the line. What was set there before (heights,
        // Elevated / Tunnel) goes first; a node whose height a crossing or an
        // end sets keeps it.
        private void StraightGrade(float from, float to)
        {
            var division = preview;
            if (division == null || division.Plan == null || division.ControlAt == null || division.NodeAlong == null) return;
            var plan = division.Plan;
            var chord = ChordAlong(plan);
            int n = plan.Points.Count;
            if (chord.Length != n || division.NodeAlong.Length != n) return;
            float lo = Mathf.Min(from, to) - 0.5f, hi = Mathf.Max(from, to) + 0.5f;
            int first = -1, last = -1;
            for (int i = 0; i < n; i++)
                if (chord[i] >= lo && chord[i] <= hi)
                {
                    if (first < 0) first = i;
                    last = i;
                }
            if (first < 0 || last - first < 2)
            {
                Mod.Log("Side view: straight grade needs at least one node between the stretch's ends");
                return;
            }
            float startHeight = plan.Points[first].y, endHeight = plan.Points[last].y;
            float span = Mathf.Max(chord[last] - chord[first], 0.01f);
            var heights = new float[n];
            for (int i = first; i <= last; i++) heights[i] = Mathf.Lerp(startHeight, endHeight, (chord[i] - chord[first]) / span);

            MarkStretch(chord[first], chord[last], Forced.None);
            float lastPlace = points.Count - 1;
            int held = 0, kept = 0;
            var terrain = TerrainManager.instance;
            for (int i = first; i <= last; i++)
            {
                if (plan.Held != null && i < plan.Held.Length && plan.Held[i])
                {
                    if (i != first && i != last) kept++;
                    continue;
                }
                float place = CurveMath.PlaceOf(division.ControlAt, division.NodeAlong[i]);
                if (place > lastPlace + 0.0001f) continue;
                float ground = terrain.SampleRawHeightSmooth(plan.Points[i]);
                float height = Mathf.Round(Mathf.Clamp(heights[i], ground + MinPinHeight, ground + MaxLift) * 2f) / 2f;
                var pin = PinNear(division, division.NodeAlong[i]);
                if (pin == null)
                {
                    pin = new HeightPin { At = place };
                    heightPins.Add(pin);
                }
                pin.Height = height;
                held++;
            }
            choicesVersion++;
            Mod.Log("Side view: straight grade " + chord[first].ToString("N0") + " to " + chord[last].ToString("N0") + " m, " +
                    ((endHeight - startHeight) / span * 100f).ToString("0.0") + " %, " + held + " height(s) held" +
                    (kept > 0 ? ", " + kept + " set by a crossing kept" : ""));
        }

        private void ResetShape()
        {
            heightPins.Clear();
            stretches.Clear();
            draggedPin = null;
            choicesVersion++;
            Mod.Log("Side view: heights and stretches cleared");
        }

        // Points removed from the end: what was set on the pieces past 'last' goes.
        private void ForgetShapeBeyond(float last)
        {
            heightPins.RemoveAll(pin => pin.At > last);
            stretches.RemoveAll(stretch => stretch.From >= last);
            foreach (var stretch in stretches) stretch.To = Mathf.Min(stretch.To, last);
            if (draggedPin != null && !heightPins.Contains(draggedPin)) draggedPin = null;
        }

        // --- upgrade along a curve: an existing road changed into the picked one ---

        private struct UpgradePoint
        {
            public ushort Segment;
            public float T;
            public bool OnNode;       // at a node: the upgrade starts (or ends) there
            public Vector3 Position;
        }

        private bool upgradeMode;
        // Drawing a slip ramp: its ends leave and join the roads there along
        // them (in their traffic's direction), turned RampAngle off them.
        private bool rampMode;
        // A median ramp road (0: off): a ramp in the road's middle rising to
        // deck level at 8 %, the road's outer lanes on the ground beside it (its
        // ground road) along the whole line. 1: six-lane road with a 2-lane ramp,
        // 2: six-lane with a 4-lane ramp, 3: four-lane with a 2-lane ramp.
        // 4, 5: a median cut (a metro or train track through an open cut in the
        // road's middle into a tunnel, or up out of one), the road beside it on
        // the ground with an open gap (see CutProfile, FinishCut).
        private int medianRamp;
        private static readonly string[] MedianRampNames =
            { null, "six-lane road + 2-lane median ramp", "six-lane road + 4-lane median ramp", "four-lane road + 2-lane median ramp",
              "metro median cut", "train median cut" };
        private int MedianRampLanes => medianRamp == 2 ? 4 : 2;
        private bool MedianCut => medianRamp >= 4;
        // A median cut's road: the one under the line where it starts (Divide),
        // else the six-lane road (built on open ground).
        private NetInfo cutRoad;
        private NetInfo MedianGroundRoad => medianRamp == 0 ? null
            : MedianCut ? cutRoad ?? PrefabCollection<NetInfo>.FindLoaded("Large Road")
            : PrefabCollection<NetInfo>.FindLoaded(medianRamp == 3 ? "Medium Road" : "Large Road");
        // The ground road beside the ramp (the road without its inner lanes), or
        // beside a median cut (its gap open and as wide as the track's cut).
        private NetInfo MedianGroundSplit
        {
            get
            {
                if (!MedianCut) return SplitRoads.For(MedianGroundRoad, MedianRampLanes / 2);
                float gap;
                return SplitRoads.CutRoadFor(MedianGroundRoad, TrackHalf(CutTrack), out gap);
            }
        }
        // The road the curve is drawn with: a median ramp's ramp road, a median
        // cut's track, else the one picked.
        private NetInfo DrawNetwork => MedianCut ? CutTrack ?? SelectedNetwork
            : medianRamp != 0 && SplitRoads.RampRoad(MedianRampLanes) != null ? SplitRoads.RampRoad(MedianRampLanes) : SelectedNetwork;

        // A median cut's track: the game's metro or train track (its ground
        // version, whose raised, slope and tunnel versions it is built with).
        private NetInfo CutTrack => medianRamp == 4 ? TrackFor(true) : medianRamp == 5 ? TrackFor(false) : null;
        private static NetInfo metroTrack, trainTrack;

        private static NetInfo TrackFor(bool metro)
        {
            var known = metro ? metroTrack : trainTrack;
            if (known != null) return known;
            NetInfo found = null;
            foreach (string name in metro ? new[] { "Metro Track Ground", "Metro Track" } : new[] { "Train Track" })
            {
                var info = PrefabCollection<NetInfo>.FindLoaded(name);
                if (IsCutTrack(info, metro)) { found = info; break; }
            }
            if (found == null)
            {
                int count = PrefabCollection<NetInfo>.LoadedCount();
                for (uint i = 0; i < count && found == null; i++)
                {
                    var info = PrefabCollection<NetInfo>.GetLoaded(i);
                    if (info == null || info.name == null || !IsCutTrack(info, metro)) continue;
                    string n = info.name;
                    if (n.Contains("Elevated") || n.Contains("Bridge") || n.Contains("Slope") || n.Contains("Tunnel") || n.Contains("Station")) continue;
                    found = info;
                }
            }
            if (found != null)
            {
                Mod.Log("Median cut: the " + (metro ? "metro" : "train") + " track is " + found.name + " (" + (2f * TrackHalf(found)).ToString("F1") +
                        " m wide with its raised, slope and tunnel versions)");
                if (metro) metroTrack = found; else trainTrack = found;
            }
            return found;
        }

        // A two-way track of that kind with raised, slope and tunnel versions.
        private static bool IsCutTrack(NetInfo info, bool metro)
        {
            if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.PublicTransport) return false;
            var sub = metro ? ItemClass.SubService.PublicTransportMetro : ItemClass.SubService.PublicTransportTrain;
            if (info.m_class.m_subService != sub) return false;
            return RoadPlanner.InfoFor(info, NodeKind.Elevated) != null && RoadPlanner.InfoFor(info, NodeKind.Slope) != null &&
                   RoadPlanner.InfoFor(info, NodeKind.Tunnel) != null;
        }

        // How far the track (any of its versions: the slope's walls) reaches
        // either side of its middle, with a little room.
        private const float CutRoom = 0.5f;

        private static float TrackHalf(NetInfo track)
        {
            if (track == null) return 0f;
            float half = track.m_halfWidth;
            foreach (var kind in new[] { NodeKind.Elevated, NodeKind.Slope, NodeKind.Tunnel })
            {
                var version = RoadPlanner.InfoFor(track, kind);
                if (version != null && kind != NodeKind.Tunnel) half = Mathf.Max(half, version.m_halfWidth);
            }
            return half + CutRoom;
        }
        private bool planningGround;   // planning a median ramp's ground road (not the ramp)
        private const float RampAngle = 12f;
        private const float MedianRampAngle = 25f;
        private readonly List<UpgradePoint> upgradePoints = new List<UpgradePoint>();
        private UpgradePlan upgradePlan;          // the preview

        // One of the three median ramp roads (1..3); picked again: off.
        private void PickMedianRamp(int choice)
        {
            medianRamp = medianRamp == choice ? 0 : choice;
            cutRoad = null;
            if (MedianCut && (CutTrack == null || MedianGroundSplit == null))
            {
                bool metro = medianRamp == 4;
                string why = CutTrack == null ? "the game's " + (metro ? "metro" : "train") + " track (with its elevated, slope and tunnel versions) wasn't found"
                    : "the " + (metro ? "metro" : "train") + " track and its open cut are " + (2f * TrackHalf(CutTrack)).ToString("F1") +
                      " m wide, wider than the widest gap RR makes in the six-lane road (" + (2f * SplitRoads.WidestCut(MedianGroundRoad)).ToString("F1") + " m)";
                Mod.Log("Median cut: " + MedianRampNames[medianRamp] + " isn't available: " + why);
                Mod.WarnNow("Median cut not available", char.ToUpper(why[0]) + why.Substring(1) + ".");
                medianRamp = 0;
            }
            else if (medianRamp != 0 && !MedianCut && (SplitRoads.RampRoad(MedianRampLanes) == null || MedianGroundSplit == null))
            {
                Mod.Log("Median ramp: " + MedianRampNames[medianRamp] + " isn't available (its ramp or ground road wasn't made when the city loaded)");
                Mod.WarnNow("Median ramp not available", "The " + MedianRampNames[medianRamp] + " needs road copies made when the city loads, " +
                                                         "and they weren't. The log says why (lines with 'Median ramp roads' or 'Median ramps').");
                medianRamp = 0;
            }
            if (medianRamp != 0)
            {
                // The ramp (a median cut's track) is a deck over the road (or the
                // ground) from its first point: drawn along the road, it follows it.
                rampMode = false;
                if (upgradeMode) ToggleUpgrade();
                deckMode = true;
                underMode = false;
            }
            choicesVersion++;
            Mod.Log(medianRamp != 0 ? (MedianCut ? "Median cut: " : "Median ramp: ") + MedianRampNames[medianRamp] : "Median ramp off");
        }

        // A median ramp's height along it (set by MedianProfile for the plan),
        // and what it does, for the log.
        private Func<float, float, float> medianProfile;
        private string medianSummary;

        // The median ramp's profile: up from where it starts (the ground road's
        // height, or the ground) at 8 % to its target, then level; or down from
        // the deck it starts on at 8 % to the road (or ground) it lands on. The
        // target going up: the deck it ends on, else the first deck level (the
        // highest RR's own deck is over the roads along and beyond the line:
        // MedianDeckLevel). Null if
        // fine, else why it can't be built.
        private string MedianProfile(List<Vector3> controls, List<Vector3> line, List<ushort> nodes, List<ushort> segments, int n, float length, out bool down)
        {
            medianProfile = null;
            medianSummary = null;
            var terrain = TerrainManager.instance;
            var net = NetManager.instance;
            Vector3 start = nodes[0] != 0 ? net.m_nodes.m_buffer[nodes[0]].m_position : controls[0];
            Vector3 end = nodes[n] != 0 ? net.m_nodes.m_buffer[nodes[n]].m_position : controls[n];
            float startGround = terrain.SampleRawHeightSmooth(start), endGround = terrain.SampleRawHeightSmooth(end);
            bool startOnRoad = nodes[0] != 0 || segments[0] != 0, endOnRoad = nodes[n] != 0 || segments[n] != 0;
            float grade = RoadPlanner.MedianRampGrade;
            down = startOnRoad && start.y - startGround >= DeckJoinHeight;
            if (!down)
            {
                float from = startOnRoad ? start.y : startGround;
                bool joinsDeck = nodes[n] != 0 && end.y - endGround >= DeckJoinHeight;
                string over = null;
                float target = joinsDeck ? end.y : from + MedianDeckLevel(line, nodes[n], out over);
                string what = joinsDeck ? "the deck it joins" : "the first deck level" + (over != null ? " (" + over + ")" : "");
                if (target - from < 1f) return "No deck level to climb to here (" + what + " is " + (target - from).ToString("F1") + " m up)";
                float climb = (target - from) / grade;
                if (length < climb - 1f)
                    return "The ramp needs " + climb.ToString("N0") + " m to climb at 8 % to " + what + " (" + (target - from).ToString("F1") +
                           " m up) and the line drawn is " + length.ToString("N0") + " m: draw it longer";
                medianProfile = (d, total) => Mathf.Min(from + grade * d, target);
                medianSummary = "up " + (target - from).ToString("F1") + " m to " + what + " in " + climb.ToString("N0") + " m at 8 %, then level for " +
                                Mathf.Max(0f, length - climb).ToString("N0") + " m";
                return null;
            }
            if (nodes[0] == 0) return "A median ramp coming down starts on a deck's end: click the end of the deck first";
            float top = start.y, to = endOnRoad ? end.y : endGround;
            if (top - to < 1f) return "The deck it starts on is no higher than where it lands";
            float fall = (top - to) / grade;
            if (length < fall - 1f)
                return "The ramp needs " + fall.ToString("N0") + " m to come down at 8 % from the deck (" + (top - to).ToString("F1") +
                       " m) and the line drawn is " + length.ToString("N0") + " m: draw it longer";
            medianProfile = (d, total) => Mathf.Min(top, to + grade * (total - d));
            medianSummary = "level at the deck for " + Mathf.Max(0f, length - fall).ToString("N0") + " m, then down " + (top - to).ToString("F1") +
                            " m in " + fall.ToString("N0") + " m at 8 %";
            return null;
        }

        // A median ramp's first deck level: the highest RR's deck is over any
        // road along the drawn line, at its end, or on a road going on from the
        // node it ends on (a deck going on from the ramp's top over a road with
        // lights or trees in its median is that high: the ramp climbs to it, so
        // the deck goes on level instead of climbing through the lights).
        // 'over': the road that sets it (null: the plain clearance).
        private const float DeckLevelStep = 8f;
        private const float DeckLevelBeyond = 30f;

        // The lowest a median ramp levels off: the lowest deck in RR that every
        // road's median lights and trees fit under (the deck over the Large Road
        // with Median, 14.6 m), so any deck going on from its top joins level and
        // nothing stands through it. Higher where the roads there need it.
        public const float MinMedianRampLevel = 14.6f;

        // 'line': the curve as drawn (sampled along its bends and the roads it
        // follows, not the straight chords between its points).
        private float MedianDeckLevel(List<Vector3> line, ushort endNode, out string over)
        {
            over = null;
            float best = MinMedianRampLevel;
            float half = Net != null ? Net.m_halfWidth : 4f;
            var samples = new List<Vector3>();
            float total = CurveMath.Length(line);
            for (float d = 0f; d < total; d += DeckLevelStep)
            {
                Vector3 p, tangent;
                CurveMath.PointAt(line, d, out p, out tangent);
                samples.Add(p);
            }
            if (line.Count > 0) samples.Add(line[line.Count - 1]);
            if (endNode != 0)
            {
                var net = NetManager.instance;
                for (int i = 0; i < 8; i++)
                {
                    ushort s = net.m_nodes.m_buffer[endNode].GetSegment(i);
                    if (s == 0) continue;
                    Vector3 a, b, c, d;
                    CrossingFinder.SegmentBezier(s, out a, out b, out c, out d);
                    bool start = net.m_segments.m_buffer[s].m_startNode == endNode;
                    float t = Mathf.Clamp01(DeckLevelBeyond / Mathf.Max(net.m_segments.m_buffer[s].m_averageLength, 1f));
                    samples.Add(CurveMath.Bezier(a, b, c, d, start ? t : 1f - t));
                }
            }
            var segments = NetManager.instance.m_segments.m_buffer;
            foreach (var p in samples)
            {
                var below = Decks.Under(p, half);
                if (below.Segment == 0) continue;
                float level = Decks.Floor(p, below) - below.Center.y;
                if (level > best + 0.05f)
                {
                    best = level;
                    var info = segments[below.Segment].Info;
                    over = "the deck level over " + (info != null ? info.name : "the road") + ", clear of what stands on it";
                }
            }
            return best;
        }

        // Drawing a median ramp down: it started on a raised deck.
        private bool MedianDown => medianRamp != 0 && !MedianCut && points.Count > 0 &&
                                   points[0].y - TerrainManager.instance.SampleRawHeightSmooth(points[0]) >= DeckJoinHeight;

        private void ToggleRamp()
        {
            rampMode = !rampMode;
            if (rampMode && upgradeMode) ToggleUpgrade();
            if (rampMode) medianRamp = 0;
            choicesVersion++;
            Mod.Log(rampMode ? "Ramp: start on the road it leaves, end on the road it joins" : "Ramp off");
        }

        // The direction a ramp leaves (or joins) the road at a node or a point
        // on a segment: along that road, the way its traffic goes (a two-way
        // road: the way the ramp heads), turned up to RampAngle towards the
        // ramp. False (with a note) where it can't: no road there, or a ramp
        // drawn against a one-way road's traffic.
        // A ramp heading along a two-way road (within MedianRampAngle) rises out
        // of its middle ('median'): it leaves along the road's centre line, not
        // turned off it; 'road' and 'roadT' are where on the road it starts.
        private static bool RampDirection(ushort node, ushort segment, float t, Vector3 heading, out Vector3 direction, ref string note,
            out bool median, out ushort road, out float roadT, bool allowMedian = true)
        {
            direction = Vector3.zero;
            median = false;
            road = 0;
            roadT = t;
            heading = CurveMath.FlatNormal(heading);
            if (heading == Vector3.zero) return false;
            var net = NetManager.instance;
            var segments = net.m_segments.m_buffer;
            Vector3 along;   // the road's direction there, from its start node to its end node
            // The road goes on both ahead of and behind the point, along the
            // ramp: the ramp can rise out of (or land in) its middle. At a road's
            // dead end (a deck's end) the ramp just continues it.
            bool through = true;
            if (node != 0)
            {
                // At a node: the road there that runs most nearly the ramp's way.
                float best = -2f, behind = 2f;
                along = Vector3.zero;
                for (int i = 0; i < 8; i++)
                {
                    ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (s == 0) continue;
                    bool atStart = segments[s].m_startNode == node;
                    Vector3 away = CurveMath.FlatNormal(atStart ? segments[s].m_startDirection : segments[s].m_endDirection);
                    float dot = Vector3.Dot(away, heading);
                    behind = Mathf.Min(behind, dot);
                    if (dot <= best) continue;
                    best = dot;
                    segment = s;
                    along = atStart ? away : -away;
                    roadT = atStart ? 0f : 1f;
                }
                float along25 = Mathf.Cos(MedianRampAngle * Mathf.Deg2Rad);
                through = best >= along25 && behind <= -along25;
            }
            else if (segment != 0)
            {
                Vector3 a, b, c, d;
                CrossingFinder.SegmentBezier(segment, out a, out b, out c, out d);
                along = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, t));
            }
            else return false;
            if (segment == 0 || along == Vector3.zero) return false;
            road = segment;

            Vector3 way;
            int traffic = TrafficWay(segment);
            if (traffic == 0) way = Vector3.Dot(along, heading) >= 0f ? along : -along;
            else
            {
                way = along * traffic;
                if (Vector3.Dot(way, heading) < 0f)
                {
                    var info = segments[segment].Info;
                    note = "The ramp runs against the one-way traffic on the " + (info != null ? info.name : "road") +
                           ": draw it the other way (from where the traffic comes)";
                    return false;
                }
            }
            if (allowMedian && traffic == 0 && through && Mathf.Abs(CurveMath.SignedAngle(way, heading)) <= MedianRampAngle)
            {
                median = true;
                direction = way;
                return true;
            }
            float turn = Mathf.Clamp(CurveMath.SignedAngle(way, heading), -RampAngle, RampAngle);
            direction = CurveMath.Rotate(way, turn);
            return true;
        }

        // A node on the ground with a road of 'road's kind (or its ground road)
        // at it, not a median ramp road.
        // Any road on the ground there (the one the player started on: the
        // Six-Lane Road, with a median or not, or any other), not a median
        // ramp or its ground road: its lanes feed the ramp and the ground road,
        // so no stretch of road is built there (it would lie on top of it).
        private static bool EndsOnGroundRoad(ushort node)
        {
            if (node == 0) return false;
            var net = NetManager.instance;
            if (OffGround(net.m_nodes.m_buffer[node].m_position)) return false;
            for (int i = 0; i < 8; i++)
            {
                ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                if (s == 0) continue;
                var info = net.m_segments.m_buffer[s].Info;
                if (info != null && !SplitRoads.IsRamp(info) && !SplitRoads.IsSplit(info)) return true;
            }
            return false;
        }

        // A two-way road with 'lanes' vehicle lanes, as many each way.
        private static bool EvenLanes(NetInfo info, int lanes)
        {
            return info != null && info.m_forwardVehicleLaneCount == info.m_backwardVehicleLaneCount &&
                   info.m_forwardVehicleLaneCount + info.m_backwardVehicleLaneCount == lanes;
        }

        // Which way a road's traffic runs along it: 1 from its start node to its
        // end node, -1 the other way, 0 both ways.
        private static int TrafficWay(ushort segment)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            var info = s.Info;
            if (info == null || info.m_hasForwardVehicleLanes == info.m_hasBackwardVehicleLanes) return 0;
            int way = info.m_hasForwardVehicleLanes ? 1 : -1;
            return (s.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None ? -way : way;
        }

        private void ToggleUpgrade()
        {
            upgradeMode = !upgradeMode;
            if (upgradeMode) rampMode = false;
            pickingBeside = false;
            upgradePoints.Clear();
            upgradePlan = null;
            choicesVersion++;
            Mod.Log(upgradeMode ? "Upgrade: click a road where it starts, then where it ends" : "Upgrade off: drawing again");
        }


        // The road under the mouse as an upgrade point: at a node (one of its
        // roads, of the picked road's kind if it has one) or on a road.
        private bool UpgradePointAt(out UpgradePoint point)
        {
            point = new UpgradePoint();
            if (!hoverValid) return false;
            var net = NetManager.instance;
            ushort node = hoverNode, segment = hoverSegment;
            if (node != 0)
            {
                ushort pick = 0;
                for (int i = 0; i < 8; i++)
                {
                    ushort road = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (road == 0) continue;
                    if (pick == 0 || (RoadPlanner.SameKind(net.m_segments.m_buffer[road].Info, SelectedNetwork) &&
                                      !RoadPlanner.SameKind(net.m_segments.m_buffer[pick].Info, SelectedNetwork))) pick = road;
                }
                if (pick == 0) return false;
                point = new UpgradePoint
                {
                    Segment = pick,
                    T = net.m_segments.m_buffer[pick].m_startNode == node ? 0f : 1f,
                    OnNode = true,
                    Position = net.m_nodes.m_buffer[node].m_position,
                };
                return true;
            }
            if (segment == 0) return false;
            point = new UpgradePoint { Segment = segment, T = hoverT, Position = hoverPosition };
            return true;
        }

        // Main thread, every frame while upgrading: the stretch from the first
        // point to the second (or to the mouse), worked out when either changes.
        private void UpdateUpgrade()
        {
            var hover = new UpgradePoint();
            bool withHover = upgradePoints.Count == 1 && mouseRayValid && UpgradePointAt(out hover);
            string key = "upgrade|" + choicesVersion + "|" + upgradePoints.Count + "|" +
                         (withHover ? hover.Segment + "," + Mathf.RoundToInt(hover.T * 20f) + "," + hover.OnNode : "") + "|" +
                         (SelectedNetwork != null ? SelectedNetwork.name : "");
            if (key != previewKey || ++framesSincePreview >= PreviewRefreshFrames)
            {
                previewKey = key;
                framesSincePreview = 0;
                preview = null;
                second = null;
                upgradePlan = null;
                if (upgradePoints.Count >= 2 || withHover)
                {
                    var a = upgradePoints[0];
                    var b = upgradePoints.Count >= 2 ? upgradePoints[1] : hover;
                    upgradePlan = RoadUpgrade.Make(SelectedNetwork, a.Segment, a.T, a.OnNode, b.Segment, b.T, b.OnNode);
                }
                ShowPanel();
            }
            HideButtons(0);
            foreach (var label in mapLabels) label.isVisible = false;
            UpdateCursorLabel(false);
        }

        private PanelModel UpgradeModel(PanelModel model)
        {
            string road = SelectedNetwork != null ? Title(SelectedNetwork) : "the picked road";
            model.Mode = "UPGRADE: click a road where the upgrade starts, then where it ends; the roads between become " + road +
                         " (same shape, same junctions). A point on the middle of a road cuts it there: only the part between the points changes. " +
                         "A cheaper road costs nothing and gives no refund. Check mark / Enter: upgrade. Right-click: take back a point. Upgrade button: back to drawing.";
            model.HasPoints = upgradePoints.Count > 0;
            var plan = upgradePlan;
            if (plan == null)
            {
                model.Summary = upgradePoints.Count == 0 ? "Click a road where the upgrade starts" : "Click (or point at) where it ends";
                return model;
            }
            model.Summary = plan.Segments.Count + " segment(s), " + plan.Length.ToString("N0") + " m become " + road +
                            (plan.Cost > 0 ? "   ·   cost " + (plan.Cost / 100).ToString("N0") : "");
            model.Details = plan.Unchanged > 0 ? plan.Unchanged + " segment(s) on the way already are" : "";
            model.Problems.AddRange(plan.Problems);
            model.Warnings.AddRange(plan.Warnings);
            model.CanBuild = upgradePoints.Count >= 2 && plan.Ok;
            return model;
        }

        private void BuildUpgrade()
        {
            if (SelectedNetwork == null || upgradePoints.Count < 2)
            {
                Mod.Log("Upgrade: click two points on a road first");
                return;
            }
            var a = upgradePoints[0];
            var b = upgradePoints[1];
            var plan = RoadUpgrade.Make(SelectedNetwork, a.Segment, a.T, a.OnNode, b.Segment, b.T, b.OnNode);
            if (!plan.Ok)
            {
                Mod.Log("Not upgraded: " + string.Join("; ", plan.Problems.ToArray()));
                return;
            }
            Mod.Log("Upgrading " + plan.Segments.Count + " segment(s), " + plan.Length.ToString("F0") + " m, to " + SelectedNetwork.name);
            NetworkBuilder.Upgrade(plan);
            upgradePoints.Clear();
            upgradePlan = null;
            choicesVersion++;
        }

        // The stretch to upgrade: its segments in the new road's colour (red if
        // it can't be done), and the points.
        private void DrawUpgrade(RenderManager.CameraInfo cameraInfo, OverlayEffect overlay)
        {
            var plan = upgradePlan;
            if (plan != null)
            {
                float width = (SelectedNetwork != null ? SelectedNetwork.m_halfWidth : 4f) * 2f;
                Color color = plan.Ok ? GoodColor : BadColor;
                for (int k = 0; k < plan.Segments.Count; k++)
                {
                    Vector3 a, b, c, d;
                    CrossingFinder.SegmentBezier(plan.Segments[k], out a, out b, out c, out d);
                    var bezier = new Bezier3 { a = a, b = b, c = c, d = d };
                    if (plan.From[k] > 0f || plan.To[k] < 1f) bezier = bezier.Cut(plan.From[k], plan.To[k]);
                    overlay.DrawBezier(cameraInfo, color, bezier, width, 100000f, -100000f, -1f, 1280f, false, true);
                }
            }
            foreach (var point in upgradePoints)
                overlay.DrawCircle(cameraInfo, SnapColor, point.Position, HandleSize, -1f, 1280f, false, true);
            UpgradePoint hover;
            if (mouseRayValid && UpgradePointAt(out hover))
                overlay.DrawCircle(cameraInfo, HoverColor, hover.Position, PointSize, -1f, 1280f, false, true);
        }

        // The parallel line as the tool is set now (null: Parallel is off).
        private ParallelSpec CurrentParallel()
        {
            if (!parallelMode || SelectedNetwork == null) return null;
            var info = besideNetwork ?? SelectedNetwork;
            return new ParallelSpec
            {
                Main = SelectedNetwork, Info = info, Gap = parallelGap, Left = parallelLeft, Reverse = parallelReverse,
                Setup = new LineSetup
                {
                    Info = info, Types = parallelTypes, Extra = parallelExtra, WaterTunnel = parallelWaterTunnel, WaterExtra = parallelWaterExtra,
                    WaterPlaces = parallelWaterPlaces, CrossingPlaces = parallelCrossingPlaces,
                    Pins = heightPins, Stretches = stretches,
                },
            };
        }

        // Why the parallel line can't be drawn (null: it can).
        private static string ParallelBlocked(List<bool> deckModes)
        {
            return deckModes.Contains(true) ? "a parallel line can't follow a deck yet: turn Parallel or Deck off" : null;
        }

        // A copy of everything the parallel line is drawn with, for planning it
        // again after the curve is built (the points and choices are cleared).
        // Its crossing choices are kept by where they are too: the curve's
        // junctions may cut the roads it crosses into new pieces.
        private ParallelSpec KeepParallel(Division parallel)
        {
            var spec = CurrentParallel();
            var setup = spec.Setup;
            var kept = new LineSetup
            {
                Info = setup.Info,
                Types = new Dictionary<int, CrossingType>(setup.Types),
                Extra = new Dictionary<int, float>(setup.Extra),
                WaterTunnel = new Dictionary<int, bool>(setup.WaterTunnel),
                WaterExtra = new Dictionary<int, float>(setup.WaterExtra),
                WaterPlaces = new List<KeyValuePair<int, Vector3>>(setup.WaterPlaces),
                CrossingPlaces = setup.CrossingPlaces != null ? new List<KeyValuePair<int, Vector3>>(setup.CrossingPlaces) : null,
                Pins = new List<HeightPin>(),
                Stretches = new List<ForcedStretch>(),
                ByPlace = new List<PlacedChoice>(),
            };
            foreach (var pin in setup.Pins) kept.Pins.Add(new HeightPin { At = pin.At, Height = pin.Height });
            foreach (var stretch in setup.Stretches) kept.Stretches.Add(new ForcedStretch { From = stretch.From, To = stretch.To, Kind = stretch.Kind });
            foreach (var crossing in parallel.Crossings)
                kept.ByPlace.Add(new PlacedChoice { At = crossing.Point, Type = crossing.Type, Extra = crossing.ExtraHeight });
            spec.Setup = kept;
            return spec;
        }

        // The curve was built (or not): plan the parallel line again against
        // the roads as they are now, and build it, undone together with the curve.
        // Main thread, every frame (HotkeyHandler), whether this tool is active or
        // not: a construction built in parts (a curve and its parallel line, a
        // median ramp road, a median cut) goes on to its next part as soon as the
        // one before is built, or is undone whole if a part fails, even after the
        // player has switched to another tool. A part that throws undoes it too.
        private bool buildingCut;   // planning a median cut's road now (whatever mode the tool is in)

        public void PumpPending()
        {
            var waiting = pending;
            if (waiting == null || waiting.State == 0) return;
            try
            {
                buildingCut = waiting.Cut;
                FinishParallel();
            }
            catch (Exception e)
            {
                Mod.LogError("Building the next part failed", e);
                if (pending == waiting) pending = null;
                if (waiting.Ground != null)
                {
                    Mod.WarnNow(waiting.Cut ? "Median cut not built" : "Median ramp road not built",
                                "Building its next part failed (the log says why). Nothing of it was left built.");
                    NetworkBuilder.UndoLast();
                }
            }
            finally
            {
                buildingCut = false;
            }
        }

        private void FinishParallel()
        {
            var waiting = pending;
            pending = null;
            if (waiting.Cut)
            {
                FinishCut(waiting);
                return;
            }
            if (waiting.Ground != null)
            {
                FinishMedian(waiting);
                return;
            }
            if (waiting.State != 1)
            {
                Mod.Log("The parallel line was not built: the curve wasn't");
                return;
            }
            var parallel = DivideParallel(waiting.Main, waiting.Spec);
            if (parallel == null || parallel.Plan == null || !parallel.Ok || !parallel.Plan.Ok)
            {
                string why = parallel == null || parallel.Plan == null ? "it can't be drawn there"
                    : !parallel.Ok ? "it bends too sharply" : string.Join("; ", parallel.Plan.Problems.ToArray());
                Mod.Log("The parallel line was not built (" + why + "). Ctrl+Z removes the curve");
                Mod.WarnNow("Parallel line not built", "The curve was built, but the line beside it wasn't: " + why +
                                                     ". Ctrl+Z removes the curve.");
                return;
            }
            Mod.Log("Building the parallel " + waiting.Spec.Info.name + ": " + parallel.Length.ToString("F0") + " m in " +
                    (parallel.Points.Count - 1) + " segment(s), " + parallel.Crossings.Count + " crossing(s)");
            NetworkBuilder.BuildCurve(waiting.Spec.Info, parallel.Plan, waiting.Group);
        }

        // A median ramp road is one construction, built part by part, each
        // only once the one before it has been (each tells FinishParallel):
        //   1. the ramp;
        //   2. where the ramp leaves (or lands in) the road, with one node shared by the full road, the ground
        //      road (its outer lanes) and the ramp (its inner lanes). Along an
        //      existing road, that road under the line becomes the ground road
        //      (cut where the line ends in it); off a road, LeadIn of the full
        //      road is built there; on a road's end, that road is the full road;
        //   3. the ground road along the rest of the line;
        //   4. that node set up (no traffic lights, no pedestrian
        //      crossing, the lanes joined inner to the ramp and outer to the
        //      ground road; with TM:PE).
        // If any part can't be planned or built, every part built before it is
        // undone (they share one undo group) and existing roads changed are put
        // back: nothing is left half built. One Ctrl+Z removes it all.
        private void FinishMedian(PendingParallel waiting)
        {
            var ramp = waiting.Main;
            bool down = ramp.Plan != null && ramp.Plan.MedianDown;
            if (waiting.State == 1 && !(waiting.Stage == MedianStage.Convert && waiting.Converted < 0)) waiting.AnyBuilt = true;
            if (waiting.State != 1 || (waiting.Stage == MedianStage.Convert && waiting.Converted < 0))
            {
                FailMedian(waiting, waiting.Stage == MedianStage.Ramp ? "the ramp couldn't be built" :
                                    waiting.Stage == MedianStage.Convert ? "the road under it couldn't be turned into its ground road" :
                                    waiting.Stage == MedianStage.LeadIn ? "the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + " couldn't be built" :
                                    waiting.Stage == MedianStage.Starts ? "the start pieces at its node couldn't be made: " + waiting.StartsWhy :
                                    "the ground road couldn't be built");
                return;
            }
            switch (waiting.Stage)
            {
                case MedianStage.Ramp:
                {
                    // Where the ramp was planned to leave (land): its first (last) node, at its height.
                    Vector3 at = down ? ramp.Plan.Points[ramp.Plan.Points.Count - 1] : ramp.Plan.Points[0];
                    waiting.Origin = RampNodeAt(at);
                    if (waiting.Origin == 0)
                    {
                        FailMedian(waiting, "the ramp's " + (down ? "last" : "first") + " node wasn't found (or more than one ramp end is there)");
                        return;
                    }
                    if (ramp.Plan.MedianAlong)
                    {
                        // The road under the line, from the ramp's node outward.
                        var line = new List<Vector3>(ramp.Polyline);
                        if (down) line.Reverse();
                        int lanes = waiting.Full != null ? waiting.Full.m_forwardVehicleLaneCount + waiting.Full.m_backwardVehicleLaneCount : 0;
                        NextMedian(waiting, MedianStage.Convert);
                        Mod.Log("Median ramp road: the road under it becomes " + waiting.Ground.name + " from node #" + waiting.Origin);
                        NetworkBuilder.ConvertAlong(line, waiting.Origin, waiting.Ground, lanes, waiting.Group, (stop, along, count) =>
                        {
                            waiting.Stop = stop;
                            waiting.StopAlong = along;
                            waiting.Converted = count;
                            waiting.State = 1;
                        });
                        return;
                    }
                    Mod.Log("Median ramp road: the ramp is built, " + (down ? "landing at" : "leaving from") + " node #" + waiting.Origin);
                    if (!EndsOnGroundRoad(waiting.Origin))
                    {
                        FailMedian(waiting, "no road on the ground at the ramp's node #" + waiting.Origin + " for its lanes");
                        return;
                    }
                    BuildMedianGround(waiting, waiting.Origin, 0f);
                    return;
                }
                case MedianStage.Convert:
                    if (waiting.Converted == 0)
                    {
                        FailMedian(waiting, "no stretch of the road under it could become its ground road");
                        return;
                    }
                    // The line goes on past the road: the ground road from where it left it.
                    if (waiting.StopAlong < ramp.Length - MinSpacing) BuildMedianGround(waiting, waiting.Stop, waiting.StopAlong);
                    else MakeMedianStarts(waiting);
                    return;
                case MedianStage.LeadIn:
                {
                    // The full road is built: the ramp starts (ends) on its end node.
                    ushort end = FullRoadEndAt(waiting.LeadAt, waiting.Full);
                    if (end == 0)
                    {
                        FailMedian(waiting, "the end of the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + " wasn't found");
                        return;
                    }
                    if (down) ramp.Plan.EndNode = end; else ramp.Plan.StartNode = end;
                    Mod.Log("Median ramp road, step 2: the " + LeadIn.ToString("F0") + " m of " + waiting.Full.name + " is built; its end node #" + end +
                            " is where the ramp " + (down ? "lands" : "leaves"));
                    Mod.Log("Median ramp road, step 3: " + waiting.RampLog + ", " + (down ? "ending" : "starting") + " on node #" + end);
                    NextMedian(waiting, MedianStage.Ramp);
                    NetworkBuilder.BuildCurve(waiting.RampInfo, ramp.Plan, waiting.Group, built => waiting.State = built ? 1 : 2);
                    return;
                }
                case MedianStage.Ground:
                    MakeMedianStarts(waiting);
                    return;
                case MedianStage.Starts:
                    FinishMedianOrigin(waiting);
                    return;
            }
        }

        // The ground road along the line from 'from' (a node, 'fromAlong' along
        // the line from the ramp's node) to its far end, planned now (the parts
        // built so far in place) and built.
        private void BuildMedianGround(PendingParallel waiting, ushort from, float fromAlong)
        {
            var ground = DivideGround(waiting.Main, waiting.Ground, from, fromAlong);
            string why = ground == null || ground.Plan == null ? "the road beside the ramp on the ground can't be drawn there"
                : !ground.Ok ? "the road beside the ramp bends too sharply"
                : !ground.Plan.Ok ? "the road beside the ramp: " + string.Join("; ", ground.Plan.Problems.ToArray())
                : null;
            if (why != null)
            {
                FailMedian(waiting, why);
                return;
            }
            Mod.Log("Median ramp road, next: building the ground road " + waiting.Ground.name + " beside the ramp from node #" + from + ": " + ground.Length.ToString("F0") + " m in " +
                    (ground.Points.Count - 1) + " segment(s), " + ground.Crossings.Count + " crossing(s) on the ground");
            NextMedian(waiting, MedianStage.Ground);
            NetworkBuilder.BuildCurve(waiting.Ground, ground.Plan, waiting.Group, built => waiting.State = built ? 1 : 2);
        }

        // The start pieces at the ramp's node: the full road's last stretch
        // and the ground road's first become start pieces that draw nothing at
        // that junction (NetworkBuilder.MedianTransition). Without them it would
        // be an ordinary junction (crosswalks, kerbs across the road): the whole
        // median ramp road is removed instead.
        private void MakeMedianStarts(PendingParallel waiting)
        {
            var full = SplitRoads.FullStartFor(waiting.Full);
            var ground = SplitRoads.StartFor(waiting.Ground);
            if (full == null || ground == null)
            {
                FailMedian(waiting, "its start pieces weren't made when the city loaded (the log's 'Median ramp roads' lines say why)");
                return;
            }
            Mod.Log("Median ramp road, next: the start pieces at node #" + waiting.Origin + " (" + full.name + ", " + ground.name + ")");
            NextMedian(waiting, MedianStage.Starts);
            NetworkBuilder.MedianTransition(waiting.Origin, full, ground, waiting.Group, (ok, why) =>
            {
                waiting.StartsWhy = why;
                waiting.State = ok ? 1 : 2;
            });
        }

        private void FinishMedianOrigin(PendingParallel waiting)
        {
            Mod.Log("Median ramp road, last: all parts built; setting up node #" + waiting.Origin + " where they meet (one Ctrl+Z removes it all)");
            NetworkBuilder.MedianOrigin(waiting.Origin);
        }

        // Waits (FinishParallel) for the next part.
        private void NextMedian(PendingParallel waiting, MedianStage stage)
        {
            waiting.Stage = stage;
            waiting.State = 0;
            pending = waiting;
        }

        // A part can't be built: every part built before it goes (one group).
        private void FailMedian(PendingParallel waiting, string why)
        {
            string what = waiting.Cut ? "Median cut" : "Median ramp road";
            Mod.Log(what + " not built (" + why + "): what was built of it is removed again");
            Mod.WarnNow(what + " not built", char.ToUpper(why[0]) + why.Substring(1) + ". Nothing of it was left built.");
            if (waiting.AnyBuilt) NetworkBuilder.UndoLast();
        }

        // The full road built before a median ramp's node on open ground: its
        // StartLength by the node becomes the start piece (no junction pieces),
        // and LeadInRest beyond it stays the plain road, so the free end has the
        // road's own end and junction pieces when another road joins it there.
        // (A lead-in only one start piece long left that end drawing nothing.)
        private const float LeadInRest = 16f;
        private const float LeadIn = SplitRoads.StartLength + LeadInRest;

        // The node of the full road just built at 'at': an end of a segment of
        // 'road' itself, NodeMatch across and NodeRise up or down from 'at' (a
        // road crossing above or below, at a stacked interchange, isn't it).
        // 0 if none, or if more than one node is there (no guessing).
        private const float NodeMatch = 2f;
        private const float NodeRise = 4f;

        private static ushort FullRoadEndAt(Vector3 at, NetInfo road)
        {
            return NodeAt(at, info => info == road);
        }

        // The node of a median ramp road's ramp (any of its pieces) at 'at', as
        // FullRoadEndAt.
        private static ushort RampNodeAt(Vector3 at)
        {
            return NodeAt(at, SplitRoads.IsRamp);
        }

        private static int SegmentsAt(ushort node)
        {
            int count = 0;
            for (int i = 0; i < 8; i++) if (NetManager.instance.m_nodes.m_buffer[node].GetSegment(i) != 0) count++;
            return count;
        }

        private static ushort NodeAt(Vector3 at, Func<NetInfo, bool> wanted)
        {
            var net = NetManager.instance;
            var found = new HashSet<ushort>();
            foreach (ushort s in RoadPlanner.NearbySegments(at, NodeMatch + 64f))
            {
                var seg = net.m_segments.m_buffer[s];
                if ((seg.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || !wanted(seg.Info)) continue;
                foreach (ushort end in new[] { seg.m_startNode, seg.m_endNode })
                {
                    Vector3 p = net.m_nodes.m_buffer[end].m_position;
                    if (CurveMath.FlatDistance(p, at) <= NodeMatch && Mathf.Abs(p.y - at.y) <= NodeRise) found.Add(end);
                }
            }
            if (found.Count != 1)
            {
                if (found.Count > 1) Mod.Log("  " + found.Count + " nodes match at " + at + ": none is taken");
                return 0;
            }
            foreach (ushort node in found) return node;
            return 0;
        }

        // LeadIn of the full road, straight from 'at' (where the ramp will leave
        // it, or land in it going down) away from the ramp ('away'), on the
        // ground; its far end is free or joins a road of its kind there. Built
        // before the ramp: its node at 'at' is new (the full road's).
        private Division DivideLeadIn(Vector3 at, Vector3 away, bool down, NetInfo road)
        {
            Vector3 dir = CurveMath.FlatNormal(away);
            if (dir == Vector3.zero || road == null) return null;
            Vector3 node = at;
            Vector3 far = node + dir * LeadIn;
            far.y = TerrainManager.instance.SampleRawHeightSmooth(far);
            ushort farNode, farSegment;
            float farT;
            EndOn(ref far, road, EndSnap, out farNode, out farSegment, out farT, true);
            var polyline = down ? new List<Vector3> { node, far } : new List<Vector3> { far, node };
            ushort startNode = down ? (ushort)0 : farNode, endNode = down ? farNode : (ushort)0;
            ushort startSegment = down ? (ushort)0 : farSegment, endSegment = down ? farSegment : (ushort)0;
            float startT = down ? 0f : farT, endT = down ? farT : 0f;
            var lifts = new List<float> { 0f, 0f };
            var setup = new LineSetup
            {
                Info = road, Types = new Dictionary<int, CrossingType>(), Extra = new Dictionary<int, float>(),
                WaterTunnel = new Dictionary<int, bool>(), WaterExtra = new Dictionary<int, float>(),
                WaterPlaces = new List<KeyValuePair<int, Vector3>>(), Pins = new List<HeightPin>(), Stretches = new List<ForcedStretch>(),
                ByPlace = new List<PlacedChoice>(),
            };
            line = setup;
            planningGround = true;
            try
            {
                var division = DivideAlong(polyline, new[] { 0f, CurveMath.Length(polyline) }, new List<float>(), Vector3.zero, Vector3.zero,
                    startNode, endNode, lifts, new List<bool>(), new List<bool>(), new HashSet<ushort>());
                division.Setup = setup;
                division.Lifts = lifts;
                if (division.Points.Count < 2) return division;
                PlanDivision(division, startNode, endNode, startSegment, startT, endSegment, endT, new List<KeyValuePair<Vector3, float>>());
                return division;
            }
            finally
            {
                planningGround = false;
            }
        }

        // The parallel line beside a divided curve: the curve's samples moved
        // sideways by both half widths and the gap, with the points' places and
        // heights and the side view's settings carried across (end to end for
        // a one-way pair, which runs the other way), then planned like any
        // curve. Each end joins a road of its kind it lands on, else it is free.
        private Division DivideParallel(Division main, ParallelSpec spec)
        {
            if (spec == null || main == null || main.Polyline == null || main.Polyline.Count < 2 || main.ControlAt == null ||
                main.Lifts == null || main.Length < 1f) return null;
            float offset = (spec.Main.m_halfWidth + spec.Gap + spec.Info.m_halfWidth) * (spec.Left ? -1f : 1f);
            var polyline = CurveMath.Offset(main.Polyline, offset);
            bool folds = CurveMath.Folds(main.Polyline, polyline);
            var mainAlong = CurveMath.Distances(main.Polyline);
            var along = CurveMath.Distances(polyline);
            int n = main.ControlAt.Length - 1;
            var controlAt = new float[n + 1];
            for (int i = 0; i <= n; i++) controlAt[i] = CurveMath.Remap(mainAlong, along, main.ControlAt[i]);
            var corners = new List<float>();
            if (main.Corners != null) foreach (float corner in main.Corners) corners.Add(CurveMath.Remap(mainAlong, along, corner));
            var lifts = new List<float>(main.Lifts);
            var pins = new List<HeightPin>();
            foreach (var pin in spec.Setup.Pins) pins.Add(new HeightPin { At = pin.At, Height = pin.Height });
            var forced = new List<ForcedStretch>();
            foreach (var stretch in spec.Setup.Stretches) forced.Add(new ForcedStretch { From = stretch.From, To = stretch.To, Kind = stretch.Kind });
            if (spec.Reverse)
            {
                polyline.Reverse();
                float total = along[along.Length - 1];
                var reversed = new float[n + 1];
                for (int i = 0; i <= n; i++) reversed[i] = total - controlAt[n - i];
                controlAt = reversed;
                for (int i = 0; i < corners.Count; i++) corners[i] = total - corners[i];
                corners.Reverse();
                lifts.Reverse();
                foreach (var pin in pins) pin.At = n - pin.At;
                foreach (var stretch in forced)
                {
                    float from = stretch.From;
                    stretch.From = n - stretch.To;
                    stretch.To = n - from;
                }
            }

            ushort startNode, endNode, startSegment, endSegment;
            float startT, endT;
            float reach = Mathf.Min(EndSnap, Mathf.Abs(offset) * 0.5f);
            Vector3 start = polyline[0], end = polyline[polyline.Count - 1];
            EndOn(ref start, spec.Info, reach, out startNode, out startSegment, out startT);
            EndOn(ref end, spec.Info, reach, out endNode, out endSegment, out endT);
            polyline[0] = start;
            polyline[polyline.Count - 1] = end;
            if (startNode != 0 || startSegment != 0) lifts[0] = 0f;
            if (endNode != 0 || endSegment != 0) lifts[n] = 0f;

            line = new LineSetup
            {
                Info = spec.Info, Types = spec.Setup.Types, Extra = spec.Setup.Extra, WaterTunnel = spec.Setup.WaterTunnel,
                WaterExtra = spec.Setup.WaterExtra, WaterPlaces = spec.Setup.WaterPlaces, CrossingPlaces = spec.Setup.CrossingPlaces,
                Pins = pins, Stretches = forced, ByPlace = spec.Setup.ByPlace,
            };
            var division = DivideAlong(polyline, controlAt, corners, Vector3.zero, Vector3.zero, startNode, endNode, lifts,
                new List<bool>(), new List<bool>(), new HashSet<ushort>());
            division.Setup = spec.Setup;   // choices made on it go to the parallel line's own
            division.Lifts = lifts;
            if (division.Points.Count < 2) return division;
            var raisedPoints = new List<KeyValuePair<Vector3, float>>();
            for (int i = 0; i <= n; i++)
            {
                if (Mathf.Abs(lifts[i]) <= 0.01f) continue;
                Vector3 point, ignore;
                CurveMath.PointAt(polyline, controlAt[i], out point, out ignore);
                raisedPoints.Add(new KeyValuePair<Vector3, float>(point, lifts[i]));
            }
            PlanDivision(division, startNode, endNode, startSegment, startT, endSegment, endT, raisedPoints);
            if (folds)
                division.Plan.Problems.Add("On the inside of a bend tighter than the gap the parallel line folds over itself: " +
                                           "widen the bend, narrow the gap or put it on the other side");
            return division;
        }

        // A median ramp road's ground road: its two carriageways (one road with
        // the gap in its middle) along the ramp's whole line on the ground,
        // through every road it crosses there (junctions: the ramp flies over
        // them), joined to a road at either end it lands on. 'origin': the node
        // it starts (going down: ends) at, 'originAlong' along the line from
        // where the ramp leaves (lands in) the road (past a stretch of road
        // that became the ground road).
        // 'until' (> 0): the ground road ends that far along the line (a median
        // cut's gap ends before its line does).
        private Division DivideGround(Division ramp, NetInfo split, ushort origin = 0, float originAlong = 0f, float until = -1f)
        {
            if (ramp == null || split == null || ramp.Polyline == null || ramp.Polyline.Count < 2 || ramp.Length < 1f) return null;
            bool down = ramp.Plan != null && ramp.Plan.MedianDown;
            float from = down ? 0f : originAlong;
            until = down ? ramp.Length - originAlong : until > 0f ? Mathf.Min(until, ramp.Length) : ramp.Length;
            var polyline = CurveMath.Slice(ramp.Polyline, from, until);
            if (CurveMath.Length(polyline) < 1f) return null;
            ushort startNode, endNode, startSegment, endSegment;
            float startT, endT;
            Vector3 start = polyline[0], end = polyline[polyline.Count - 1];
            EndOn(ref start, split, EndSnap, out startNode, out startSegment, out startT, true);
            EndOn(ref end, split, EndSnap, out endNode, out endSegment, out endT, true);
            if (origin != 0 && !down)
            {
                startNode = origin;
                startSegment = 0;
                start = NetManager.instance.m_nodes.m_buffer[origin].m_position;
            }
            if (origin != 0 && down)
            {
                endNode = origin;
                endSegment = 0;
                end = NetManager.instance.m_nodes.m_buffer[origin].m_position;
            }
            polyline[0] = start;
            polyline[polyline.Count - 1] = end;
            var lifts = new List<float> { 0f, 0f };
            var setup = new LineSetup
            {
                Info = split, Types = new Dictionary<int, CrossingType>(), Extra = new Dictionary<int, float>(),
                WaterTunnel = new Dictionary<int, bool>(), WaterExtra = new Dictionary<int, float>(),
                WaterPlaces = new List<KeyValuePair<int, Vector3>>(), Pins = new List<HeightPin>(), Stretches = new List<ForcedStretch>(),
                ByPlace = new List<PlacedChoice>(),
            };
            line = setup;
            planningGround = true;
            try
            {
                var division = DivideAlong(polyline, new[] { 0f, CurveMath.Length(polyline) }, new List<float>(), Vector3.zero, Vector3.zero,
                    startNode, endNode, lifts, new List<bool>(), new List<bool>(), new HashSet<ushort>());
                division.Setup = setup;
                division.Lifts = lifts;
                if (division.Points.Count < 2) return division;
                PlanDivision(division, startNode, endNode, startSegment, startT, endSegment, endT, new List<KeyValuePair<Vector3, float>>());
                if (division.Plan != null && (down ? startNode == 0 && startSegment == 0 : endNode == 0 && endSegment == 0))
                    division.Plan.Warnings.Add("The ground lanes beside the ramp end where you " + (down ? "start" : "stop") +
                                               " drawing (join them to a road later)");
                return division;
            }
            finally
            {
                planningGround = false;
            }
        }

        // A parallel end on a node, or on the middle of a road, of its own
        // kind joins it there ('at' moves onto it). 'skipRamps': not onto a
        // median ramp road (a ground road beside one never joins the ramp).
        private static void EndOn(ref Vector3 at, NetInfo info, float reach, out ushort node, out ushort segment, out float t, bool skipRamps = false)
        {
            node = segment = 0;
            t = 0f;
            var net = NetManager.instance;
            var near = new List<ushort>(RoadPlanner.NearbySegments(at, reach + 64f));
            float best = reach;
            foreach (ushort s in near)
            {
                var seg = net.m_segments.m_buffer[s];
                if ((seg.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || !RoadPlanner.SameKind(seg.Info, info) ||
                    (skipRamps && SplitRoads.IsRamp(seg.Info))) continue;
                foreach (ushort end in new[] { seg.m_startNode, seg.m_endNode })
                {
                    float d = CurveMath.FlatDistance(net.m_nodes.m_buffer[end].m_position, at);
                    if (d >= best || (skipRamps && OffGround(net.m_nodes.m_buffer[end].m_position))) continue;
                    best = d;
                    node = end;
                }
            }
            if (node != 0)
            {
                at = net.m_nodes.m_buffer[node].m_position;
                return;
            }
            foreach (ushort s in near)
            {
                var seg = net.m_segments.m_buffer[s];
                if ((seg.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || !RoadPlanner.SameKind(seg.Info, info) ||
                    (skipRamps && SplitRoads.IsRamp(seg.Info))) continue;
                Vector3 on;
                float onT = CrossingFinder.ClosestT(s, at, out on);
                if (CurveMath.FlatDistance(on, at) > seg.Info.m_halfWidth || (skipRamps && OffGround(on))) continue;
                if (onT * seg.m_averageLength < Crossing.NodeSnap || (1f - onT) * seg.m_averageLength < Crossing.NodeSnap) continue;
                segment = s;
                t = onT;
                at = on;
                return;
            }
        }

        // Raised off (or sunk into) the ground: a deck or a tunnel, not a road
        // on the ground for a median ramp's ground road to join.
        private static bool OffGround(Vector3 p)
        {
            return Mathf.Abs(p.y - TerrainManager.instance.SampleRawHeightSmooth(p)) > DeckJoinHeight;
        }

        private void ToggleParallel()
        {
            parallelMode = !parallelMode;
            // Off: a road picked in the menu is the curve's again, not one for beside it.
            if (!parallelMode) pickingBeside = false;
            choicesVersion++;
            Mod.Log("Parallel line " + (parallelMode ? "on" : "off"));
        }

        private void ChangeGap(float step)
        {
            parallelGap = Mathf.Clamp(parallelGap + step, 0f, MaxGap);
            choicesVersion++;
        }

        private void FlipSide()
        {
            parallelLeft = !parallelLeft;
            parallelTypes.Clear();
            parallelExtra.Clear();
            parallelWaterExtra.Clear();
            parallelWaterTunnel.Clear();
            parallelWaterPlaces.Clear();
            parallelCrossingPlaces.Clear();
            choicesVersion++;
        }

        // The road or track picked in the game's menu now goes beside the
        // curve (then pick the curve's own); pressed again: the same road as
        // the curve, the other way (a one-way pair).
        // What goes beside the curve: pressed, the next road or track picked in
        // the game's menu (the curve keeps its own); pressed again while
        // waiting, or with another road beside, back to the curve's own road.
        private void PickBeside()
        {
            if (besideNetwork != null || pickingBeside)
            {
                besideNetwork = null;
                pickingBeside = false;
                parallelReverse = true;
                Mod.Log("Parallel line: the same road as the curve");
            }
            else
            {
                pickingBeside = true;
                if (!parallelMode) parallelMode = true;
                Mod.Log("Parallel line: pick the road or track for it in the game's menu");
            }
            parallelTypes.Clear();
            parallelExtra.Clear();
            parallelWaterExtra.Clear();
            parallelWaterTunnel.Clear();
            parallelWaterPlaces.Clear();
            parallelCrossingPlaces.Clear();
            choicesVersion++;
        }

        // The other way (a one-way pair) or the same way (a double track).
        private void FlipDirection()
        {
            parallelReverse = !parallelReverse;
            parallelTypes.Clear();
            parallelExtra.Clear();
            parallelWaterExtra.Clear();
            parallelWaterTunnel.Clear();
            parallelWaterPlaces.Clear();
            parallelCrossingPlaces.Clear();
            choicesVersion++;
            Mod.Log("Parallel line runs " + (parallelReverse ? "the other way" : "the same way"));
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
            // One construction at a time: the parts of one built in parts are
            // started one after another (PumpPending), and a second would take
            // its place half built.
            if (pending != null)
            {
                Mod.Log("Not built: the last one is still being built, part by part");
                Mod.WarnNow("Still building", "The last road is still being built, part by part. Build this once it is done.");
                return;
            }
            if (upgradeMode)
            {
                BuildUpgrade();
                return;
            }
            if (DrawNetwork == null)
            {
                Mod.Log("Pick a road in the vanilla Roads menu first, then open the curve tool again");
                return;
            }
            if (points.Count < 2)
            {
                Mod.Log("Need at least 2 points to build");
                return;
            }

            var division = Divide(points, pointNodes, pointSegments, pointTs, pointLifts, pointStraight, pointDeck, pointUnder, pointRoad, pointRoadT);
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

            // The parallel line: checked now, built (planned again) once the curve is.
            ParallelSpec spec = null;
            if (parallelMode)
            {
                string why = ParallelBlocked(pointDeck);
                var parallel = why == null ? DivideParallel(division, CurrentParallel()) : null;
                if (why == null && (parallel == null || parallel.Plan == null)) why = "the parallel line can't be drawn here";
                else if (why == null && !parallel.Ok) why = "the parallel line bends too sharply";
                else if (why == null && !parallel.Plan.Ok) why = "parallel line: " + string.Join("; ", parallel.Plan.Problems.ToArray());
                if (why != null)
                {
                    Mod.Log("Not built: " + why);
                    return;
                }
                spec = KeepParallel(parallel);
            }

            // A median cut: its track first, then the road (see FinishCut).
            if (MedianCut)
            {
                StartCut(division);
                ClearPoints();
                return;
            }

            // A median ramp road's new ground road: checked now, built once the ramp is.
            NetInfo groundRoad = null;
            if (medianRamp != 0)
            {
                groundRoad = MedianGroundSplit;
                // Along a road its ground road is that road (checked as it is built).
                var ground = division.Plan.MedianAlong ? null : DivideGround(division, groundRoad);
                string why = division.Plan.MedianAlong ? null : ground == null || ground.Plan == null ? "its ground road can't be drawn here"
                    : !ground.Ok ? "its ground road bends too sharply"
                    : !ground.Plan.Ok ? "ground road: " + string.Join("; ", ground.Plan.Problems.ToArray()) : null;
                if (why != null)
                {
                    Mod.Log("Not built: " + why);
                    return;
                }
            }

            var drawn = DrawNetwork;
            if (medianRamp != 0)
                Mod.Log("Median ramp " + MedianRampNames[medianRamp] + ": " + (medianSummary ?? "no profile") + "; ground road " + MedianGroundSplit.name);
            string building = "Building " + drawn.name + ": " + division.Length.ToString("F0") + " m in " +
                              (division.Points.Count - 1) + " segment(s), " + division.Crossings.Count + " crossing(s)";
            // A median ramp road logs its ramp when the ramp's build starts (see below).
            if (groundRoad == null) Mod.Log(building);
            if (spec == null && groundRoad == null) NetworkBuilder.BuildCurve(drawn, division.Plan);
            else
            {
                var waiting = new PendingParallel { Main = division, Spec = spec, Ground = groundRoad, Full = MedianGroundRoad, Group = NetworkBuilder.NewGroup(),
                                                    RampInfo = drawn, Stage = MedianStage.Ramp };
                // A median ramp road on open ground: the stretch of full road its lanes
                // come from (or go on to) is built first, and the ramp and ground road
                // then join its end, just as on the end of a road already there (the
                // node is the full road's; one made by the ramp first was the ramp's).
                bool down = division.Plan.MedianDown;
                int joinAt = down ? division.Points.Count - 1 : 0;
                if (groundRoad != null && !division.Plan.MedianAlong &&
                    (down ? division.Plan.EndNode == 0 && division.Plan.EndSegment == 0 : division.Plan.StartNode == 0 && division.Plan.StartSegment == 0))
                {
                    Vector3 at = division.Plan.Points[joinAt];
                    Vector3 away = down ? division.Plan.InTangent(joinAt) : -division.Plan.Tangents[joinAt];
                    var lead = DivideLeadIn(at, away, down, waiting.Full);
                    string why = lead == null || lead.Plan == null ? "the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + " can't be drawn there"
                        : !lead.Ok || !lead.Plan.Ok ? "the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + ": " +
                                                      (lead.Plan.Ok ? "it bends too sharply" : string.Join("; ", lead.Plan.Problems.ToArray()))
                        : null;
                    if (why != null)
                    {
                        Mod.Log("Not built: " + why);
                        return;
                    }
                    Mod.Log("Median ramp road, step 1: building " + LeadIn.ToString("F0") + " m of " + waiting.Full.name +
                            (down ? " on from where the ramp lands" : " to where the ramp leaves") + " first");
                    waiting.Stage = MedianStage.LeadIn;
                    waiting.RampLog = building;
                    waiting.LeadAt = at;
                    NetworkBuilder.BuildCurve(waiting.Full, lead.Plan, waiting.Group, built => waiting.State = built ? 1 : 2);
                }
                else
                {
                    if (groundRoad != null) Mod.Log("Median ramp road, step 1: " + building + " (from " + (division.Plan.MedianAlong ? "the road it runs along" : "the road's end") + ")");
                    NetworkBuilder.BuildCurve(drawn, division.Plan, waiting.Group, built => waiting.State = built ? 1 : 2);
                }
                pending = waiting;
            }
            ClearPoints();
        }

        // Cuts the curve through 'controls' into buildable pieces. Where an end
        // is snapped to an existing road, the curve continues that road
        // smoothly; if that would bend too much it meets the road at an angle
        // instead (like a normal junction). Crossings with existing roads get a
        // node exactly on them, and RoadPlanner works out the heights.
        private Division Divide(List<Vector3> controls, List<ushort> nodes, List<ushort> segments, List<float> ts, List<float> lifts,
            List<bool> pointModes, List<bool> deckModes, List<bool> underModes, List<ushort> roads, List<float> roadTs)
        {
            line = MainSetup();
            if (controls.Count < 2) return new Division { Points = new List<Vector3>(), Tangents = new List<Vector3>(), InTangents = new List<Vector3>() };

            int n = controls.Count - 1;
            // An end on a road joins it at its height; any other point keeps its own.
            lifts = new List<float>(lifts);
            if (nodes[0] != 0 || segments[0] != 0) lifts[0] = 0f;
            if (nodes[n] != 0 || segments[n] != 0) lifts[n] = 0f;
            // Span i (point i to i+1) is straight if point i+1 was placed in straight mode.
            var straight = new List<bool>();
            for (int i = 1; i <= n; i++) straight.Add(i < pointModes.Count && pointModes[i]);
            // A ramp leaves and joins the roads at its ends along them. A median
            // ramp road starts (or lands) on a road's end, on open ground, or along
            // the middle of a road of its kind: that road is split there and the
            // stretch under the line becomes its ground road (see FinishMedian).
            string rampNote = null, rampProblem = null;
            NetInfo alongStart = null, alongEnd = null;   // the road it starts (ends) along the middle of
            Vector3 rampStart = Vector3.zero, rampEnd = Vector3.zero;
            // A median cut's track starts on a track's end: its road is the one under
            // the line where it starts (split there, see FinishCut), which its line
            // follows from there; an end on anything but its own track is free.
            if (MedianCut)
            {
                nodes = new List<ushort>(nodes);
                segments = new List<ushort>(segments);
                roads = new List<ushort>(roads);
                roadTs = new List<float>(roadTs);
                foreach (int k in new[] { 0, n })
                {
                    if (nodes[k] != 0 && !OnCutTrack(nodes[k], 0)) nodes[k] = 0;
                    if (segments[k] != 0 && !OnCutTrack(0, segments[k])) segments[k] = 0;
                }
                ushort startRoad;
                float startRoadT;
                cutRoad = CutRoadUnder(controls[0], controls[1] - controls[0], out startRoad, out startRoadT);
                roads[0] = startRoad;
                roadTs[0] = startRoadT;
                ushort underEnd;
                float underEndT;
                if (CutRoadUnder(controls[n], controls[n] - controls[n - 1], out underEnd, out underEndT) != null)
                {
                    roads[n] = underEnd;
                    roadTs[n] = underEndT;
                }
            }
            if (rampMode || (medianRamp != 0 && !MedianCut))
            {
                roads = new List<ushort>(roads);
                roadTs = new List<float>(roadTs);
                for (int e = 0; e < 2; e++)
                {
                    int k = e == 0 ? 0 : n;
                    Vector3 heading = e == 0 ? controls[1] - controls[0] : controls[n] - controls[n - 1];
                    Vector3 along;
                    bool median;
                    ushort road;
                    float roadT;
                    if (!RampDirection(nodes[k], segments[k], ts[k], heading, out along, ref rampNote, out median, out road, out roadT)) continue;
                    // A side ramp (Ramp mode) leaves a road at its side; a median ramp
                    // road isn't drawn out of a road's middle (see below).
                    if (median && medianRamp == 0)
                    {
                        median = false;
                        string ignore = null;
                        if (!RampDirection(nodes[k], segments[k], ts[k], heading, out along, ref ignore, out median, out road, out roadT, false)) continue;
                    }
                    if (e == 0) rampStart = along; else rampEnd = along;
                    if (!median) continue;
                    // Its deck follows that road (over it until the road becomes its ground road).
                    roads[k] = road;
                    roadTs[k] = roadT;
                    var alongRoad = NetManager.instance.m_segments.m_buffer[road].Info;
                    if (e == 0) alongStart = alongRoad; else alongEnd = alongRoad;
                }
                if (rampMode && ((nodes[0] == 0 && segments[0] == 0) || (nodes[n] == 0 && segments[n] == 0)))
                    rampNote = rampNote ?? "A ramp starts on the road it leaves and ends on the road it joins: snap both ends to roads";
            }
            // Deck pieces; one between two deck points on roads follows those roads.
            var deck = new List<bool>();
            var below = new List<bool>();      // of those, the ones under the road
            var paths = new List<List<Vector3>>();
            var under = new HashSet<ushort>();
            for (int i = 1; i <= n; i++)
            {
                // A median ramp road's ramp is a deck all along (over its own ground
                // road and the ground).
                bool onDeck = (i < deckModes.Count && deckModes[i]) || medianRamp != 0;
                deck.Add(onDeck);
                below.Add(onDeck && i < underModes.Count && underModes[i]);
                // Rounded where it turns at a junction (the roads meet at an angle
                // in the junction's middle).
                // Never so wide that the deck leaves the narrowest road it follows
                // (it would cut across the block's corner).
                var followed = new HashSet<ushort>();
                var path = onDeck && roads[i - 1] != 0 && roads[i] != 0
                    ? Decks.CachedRoadPath(roads[i - 1], roadTs[i - 1], roads[i], roadTs[i], followed) : null;
                under.UnionWith(followed);
                paths.Add(path != null
                    ? CurveMath.RoundCorners(path, RoadPlanner.IsRail(Net) ? RailDeckTurnRadius : DeckTurnRadius, 8f, InwardAllowed(followed))
                    : null);
            }
            // A straight end piece meets the road there at its own angle; a curved one may continue it.
            Vector3 startDir = straight[0] ? Vector3.zero : NetworkBuilder.ContinuationDirection(nodes[0], controls[1] - controls[0], true);
            Vector3 endDir = straight[n - 1] ? Vector3.zero : NetworkBuilder.ContinuationDirection(nodes[n], controls[n] - controls[n - 1], false);
            if (rampStart != Vector3.zero) startDir = rampStart;
            if (rampEnd != Vector3.zero) endDir = rampEnd;

            var division = DivideShaped(controls, startDir, endDir, nodes[0], nodes[n], lifts, straight, deck, below, paths, under);
            division.Continues = startDir != Vector3.zero || endDir != Vector3.zero;
            if (!division.Ok && division.Continues)
            {
                // Continuing the road bends too much: meet it at an angle instead.
                var plain = DivideShaped(controls, Vector3.zero, Vector3.zero, nodes[0], nodes[n], lifts, straight, deck, below, paths, under);
                if (plain.Ok)
                {
                    division = plain;
                    division.Continues = false;
                }
            }

            // Ends on the middle of a road.
            ushort startSegment = nodes[0] == 0 ? segments[0] : (ushort)0;
            ushort endSegment = nodes[n] == 0 ? segments[n] : (ushort)0;
            // Points the player raised or lowered, by where they are.
            var raisedPoints = new List<KeyValuePair<Vector3, float>>();
            for (int i = 0; i <= n; i++)
                if (Mathf.Abs(lifts[i]) > 0.01f) raisedPoints.Add(new KeyValuePair<Vector3, float>(controls[i], lifts[i]));
            division.Lifts = lifts;
            // A median ramp's own height profile (up to its target and level, or down).
            bool down = false;
            string profileProblem = MedianCut ? CutProfile(controls, division.Polyline, nodes, n, division.Length, out down)
                : medianRamp != 0 ? MedianProfile(controls, division.Polyline, nodes, segments, n, division.Length, out down) : null;
            if (MedianCut && profileProblem == null && CutStops(division, down))
            {
                // Divided again with a stop where the track reaches the ground and one
                // where it reaches the tunnel's depth, nothing between: the open cut
                // is one slope piece, planned as it is built.
                cutFrom = division.CutFrom;
                cutTo = division.CutTo;
                try
                {
                    var again = DivideShaped(controls, division.Continues ? startDir : Vector3.zero, division.Continues ? endDir : Vector3.zero,
                        nodes[0], nodes[n], lifts, straight, deck, below, paths, under);
                    if (again.Ok)
                    {
                        again.Continues = division.Continues;
                        again.GapFrom = division.GapFrom;
                        again.GapTo = division.GapTo;
                        again.CutFrom = division.CutFrom;
                        again.CutTo = division.CutTo;
                        division = again;
                    }
                }
                finally
                {
                    cutFrom = cutTo = -1f;
                }
            }
            if (MedianCut) TypeCutCrossings(division);
            if (medianRamp == 0) medianProfile = null;
            PlanDivision(division, nodes[0], nodes[n], startSegment, ts[0], endSegment, ts[n], raisedPoints);
            if (MedianCut)
            {
                if (division.Plan != null) CheckCut(division, profileProblem);
                return division;
            }
            if (division.Plan != null)
            {
                division.Plan.MedianDown = down;
                // Along a road: only where it leaves (or lands), and only a road
                // with the lanes it is for (its inner ones go up the ramp).
                var along = down ? alongEnd : alongStart;
                var wrongEnd = down ? alongStart : alongEnd;
                division.Plan.MedianAlong = medianRamp != 0 && along != null;
                int wanted = medianRamp == 3 ? 4 : 6;
                if (medianRamp != 0 && wrongEnd != null)
                    division.Plan.Problems.Insert(0, "The " + MedianRampNames[medianRamp] + " " + (down ? "comes down from" : "rises to") + " its deck at the " +
                                                     (down ? "start" : "end") + ": that end can't be on the middle of a road");
                else if (medianRamp != 0 && along != null && !EvenLanes(along, wanted))
                    division.Plan.Problems.Insert(0, "The road here is the " + Title(along) + " (" + (along.m_forwardVehicleLaneCount + along.m_backwardVehicleLaneCount) +
                                                     " lanes); the " + MedianRampNames[medianRamp] + " " + (down ? "lands in" : "rises out of") + " a two-way " + wanted +
                                                     "-lane road: pick the median ramp road for its lanes, or draw it elsewhere");
                if (profileProblem != null) division.Plan.Problems.Insert(0, profileProblem);
                else if (medianRamp != 0 && medianSummary != null) division.Plan.Warnings.Add("Median ramp: " + medianSummary);
            }
            if (rampNote != null && division.Plan != null) division.Plan.Warnings.Insert(0, rampNote);
            if (rampProblem != null && division.Plan != null) division.Plan.Problems.Insert(0, rampProblem);
            if (division.Plan != null)
            {
                // Not on a road of its kind: a short stretch of that road is built
                // to the node where the ramp leaves (or lands in) it (see FinishParallel).
                int joinAt = down ? n : 0;
                if (division.Plan.MedianAlong)
                    division.Plan.Warnings.Add("The " + Title(down ? alongEnd : alongStart) + " under it becomes its ground road from where it " + (down ? "lands" : "leaves") +
                                               " to where the line leaves the road (or ends)");
                // On a road but not on its end node (on its middle, or near an end
                // without snapping to it): it would be built through that road.
                Vector3 joinPoint = controls[joinAt];
                ushort onNode, onSegment;
                float onT;
                if (medianRamp != 0 && nodes[joinAt] == 0) EndOn(ref joinPoint, MedianGroundRoad, EndSnap, out onNode, out onSegment, out onT, true);
                else onNode = onSegment = 0;
                // Never at a junction: its other roads would be at the ramp's node.
                if (nodes[joinAt] != 0 && SegmentsAt(nodes[joinAt]) > 2)
                    division.Plan.Problems.Insert(0, (down ? "End" : "Start") + " the " + MedianRampNames[medianRamp] + " on a road's end or along its middle, " +
                                                     "not at a junction (its other roads would meet the ramp there)");
                if (!division.Plan.MedianAlong && (onNode != 0 || onSegment != 0))
                    division.Plan.Problems.Insert(0, (down ? "End" : "Start") + " the " + MedianRampNames[medianRamp] + " on the end node of the road " +
                                                     "(it snaps to it), or on open ground: here it would be built through the road");
                else if (medianRamp != 0 && !division.Plan.MedianAlong && !EndsOnGroundRoad(nodes[joinAt]))
                    division.Plan.Warnings.Add("A " + LeadIn.ToString("F0") + " m stretch of " + Title(MedianGroundRoad) + " is built " +
                                               (down ? "on from where it lands" : "behind where it starts") + ", for its lanes to " +
                                               (down ? "go on to" : "come from"));
            }
            return division;
        }

        // Heights, road versions and pillars for a divided curve (RoadPlanner),
        // its cost and the buildings in its way.
        private void PlanDivision(Division division, ushort startNode, ushort endNode, ushort startSegment, float startT,
            ushort endSegment, float endT, List<KeyValuePair<Vector3, float>> raisedPoints)
        {
            var joined = JoinedRoads(startNode, endNode, startSegment, endSegment);
            RoadPlanner.Ramp = rampMode;
            RoadPlanner.MedianRamp = medianRamp != 0 && !MedianCut && !planningGround;
            RoadPlanner.MedianCut = MedianCut && !planningGround;
            RoadPlanner.HoldStart = medianRamp != 0 && !MedianCut && !planningGround && startNode == 0 && startSegment == 0;
            RoadPlanner.Profile = medianRamp != 0 && !planningGround ? medianProfile : null;
            try
            {
                FindDecks(division, Net != null ? Net.m_halfWidth : 8f, joined);
                division.Plan = RoadPlanner.Make(Net, division.Points, division.Tangents,
                    division.Crossings, division.CrossingIndex, division.WaterRuns, startNode, endNode,
                    startSegment, startT, endSegment, endT, raisedPoints, division.InTangents, division.OnDeck, division.Below, division.UnderDeck,
                    division.Heights, division.Forced, NodeDistances(division));
                // Raised stretches get a node (and pillar) every RaisedSpacing, like the
                // vanilla tool's elevated roads; plan again with them.
                if (AddRaisedNodes(division))
                {
                    FindDecks(division, Net != null ? Net.m_halfWidth : 8f, joined);
                    division.Plan = RoadPlanner.Make(Net, division.Points, division.Tangents,
                        division.Crossings, division.CrossingIndex, division.WaterRuns, startNode, endNode,
                        startSegment, startT, endSegment, endT, raisedPoints, division.InTangents, division.OnDeck, division.Below, division.UnderDeck,
                        division.Heights, division.Forced, NodeDistances(division));
                }
            }
            finally
            {
                // Never left set for the next plan (a parallel line, the next curve).
                RoadPlanner.Ramp = false;
                RoadPlanner.MedianRamp = false;
                RoadPlanner.MedianCut = false;
                RoadPlanner.HoldStart = false;
                RoadPlanner.Profile = null;
            }
            if (MedianCut && !planningGround) OneSlope(division);
            DropNodesOverCrossings(division);
            division.NodeAlong = NodeDistances(division);
            division.Cost = NetworkBuilder.Cost(Net, division.Plan);
            int blocked = BuildingsInTheWay(division);
            if (blocked > 0)
                division.Plan.Warnings.Add("Runs through " + blocked + " building(s), ringed red on the map: bulldoze them first (they are not removed)");
        }

        // How far either side of a crossing our bridge's nodes go: past the other
        // road's edge and ours, measured along our road (longer at an angle).
        private float SideOffset(List<Vector3> polyline, Crossing crossing)
        {
            Vector3 point, ours;
            CurveMath.PointAt(polyline, crossing.Distance, out point, out ours);
            crossing.Skew = RoadPlanner.Skew(crossing, ours);
            return RoadPlanner.DeckHalf(crossing, Net, crossing.Type == CrossingType.Overpass);
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
            var nodeAlong = NodeDistances(division);
            var terrain = TerrainManager.instance;
            for (int i = plan.Points.Count - 1; i >= 1; i--)
            {
                // Raised pieces over land: both ends up (elevated, or the bridge
                // version over a lowered road), or a ramp with one end up that is
                // still high enough in the middle for a pillar. Bridges over water
                // keep their long, even spans (BridgeSpacing), with no extra pillar
                // in the river.
                bool upBefore = IsRaised(plan.Kinds[i - 1]), upAfter = IsRaised(plan.Kinds[i]);
                if (!upBefore && !upAfter) continue;
                if (InWater(division.WaterRuns, (nodeAlong[i - 1] + nodeAlong[i]) * 0.5f)) continue;
                if (!upBefore || !upAfter)
                {
                    Vector3 middle = (plan.Points[i - 1] + plan.Points[i]) * 0.5f;
                    if (middle.y - terrain.SampleRawHeightSmoothWithWater(middle, false, 0f) < MinPillarHeight) continue;
                }
                if (keep.Contains(i) || keep.Contains(i - 1)) continue;
                float length = CurveMath.FlatDistance(division.Points[i - 1], division.Points[i]);
                float spacing = RoadPlanner.IsRail(Net) ? RailRaisedSpacing : RaisedSpacing;
                int pieces = Mathf.CeilToInt(length / spacing - 0.15f);
                if (pieces < 2) continue;
                var bezier = PieceBezier(division, i);
                for (int k = pieces - 1; k >= 1; k--)
                {
                    float t = k / (float)pieces;
                    division.Points.Insert(i, CurveMath.Bezier(bezier.a, bezier.b, bezier.c, bezier.d, t));
                    Vector3 along = CurveMath.FlatNormal(CurveMath.BezierDerivative(bezier.a, bezier.b, bezier.c, bezier.d, t));
                    division.Tangents.Insert(i, along);
                    division.InTangents.Insert(i, along);
                }
                for (int c = 0; c < division.CrossingIndex.Count; c++)
                    if (division.CrossingIndex[c] >= i) division.CrossingIndex[c] += pieces - 1;
                added = true;
            }
            return added;
        }

        // The height a point placed now gets: none on an existing road (it
        // takes that road's height), else the current Page Up / Page Down height.
        private void ChangeLift(float step)
        {
            lift = Mathf.Clamp(Mathf.Round((lift + step) * 10f) / 10f, MinLift, MaxLift);
            choicesVersion++;
            Mod.Log("Next point's height: " + FormatLift(lift));
        }

        public static string FormatLift(float value)
        {
            if (Mathf.Abs(value) < 0.05f) return "ground level";
            return (value > 0f ? "+" : "") + value.ToString("0.#") + " m";
        }

        // --- on-screen buttons ---------------------------------------------------

        // Main thread. Icons next to each crossing, +/- next to each bridge and
        // water crossing. Buttons are reused; each frame says what they do.
        private void PlaceButtons()
        {
            int used = 0, labelled = 0;
            var camera = Camera.main;
            var view = UIView.GetAView();
            var placed = new List<Vector4>();   // x, y, width, height of each row so far
            foreach (var division in new[] { preview, second })
            {
                if (division == null || division.Plan == null || camera == null || view == null) continue;
                // Same numbers and order as the panel's list along the road (the
                // parallel line's with a P). Rows that would cover one already
                // placed (crossings close together) are stacked above it.
                string prefix = division == second ? "P" : "";
                var items = Along(division);
                for (int n = 0; n < items.Count; n++)
                {
                    var item = items[n];
                    Vector2 gui;
                    if (!ToGui(camera, view, item.Where, out gui)) continue;
                    float y = gui.y - IconSize - 24f;
                    if (item.Crossing != null)
                    {
                        var c = item.Crossing;
                        var allowed = new List<CrossingType>();
                        foreach (var type in AllTypes) if (c.Allows(type)) allowed.Add(type);
                        int count = allowed.Count + (c.Type == CrossingType.Bridge ? 2 : 0);
                        float x = gui.x - count * (IconSize + 2f) * 0.5f;
                        y = FreeRow(placed, x, y, count * (IconSize + 2f));
                        UseLabel(labelled++, prefix + (n + 1) + " · " + ItemHeight(division, item), new Vector2(x, y - 22f));
                        var d = division;
                        foreach (var type in allowed)
                        {
                            var t = type;
                            UseButton(used++, RoadPanel.IconFor(t), RoadPanel.Letter(t), c.Describe(t), new Vector2(x, y), c.Type == t,
                                () => SetType(d, c, t));
                            x += IconSize + 2f;
                        }
                        if (c.Type == CrossingType.Bridge)
                        {
                            System.Action<int> wheel = dir => AddExtra(d, c, dir * ClickStep());
                            UseButton(used++, Icons.Plus, "+", "Higher (+" + HeightStep + " m, Shift: 1 m; or the mouse wheel)", new Vector2(x, y), false, () => AddExtra(d, c, ClickStep()), wheel);
                            UseButton(used++, Icons.Minus, "-", "Lower (-" + HeightStep + " m, Shift: 1 m; or the mouse wheel)", new Vector2(x + IconSize + 2f, y), false, () => AddExtra(d, c, -ClickStep()), wheel);
                        }
                    }
                    else
                    {
                        var r = item.Water;
                        var d = division;
                        // Bridge or tunnel, and for a bridge its height.
                        float rowWidth = (r.Tunnel ? 2f : 4f) * (IconSize + 2f);
                        float left = gui.x - rowWidth * 0.5f;
                        y = FreeRow(placed, left, y, rowWidth);
                        UseLabel(labelled++, prefix + (n + 1) + " · " + ItemHeight(division, item), new Vector2(left, y - 22f));
                        UseButton(used++, RoadPanel.IconFor(CrossingType.Bridge), "B", "Bridge over the water", new Vector2(left, y),
                            !r.Tunnel, () => SetWaterTunnel(d, r, false));
                        UseButton(used++, RoadPanel.IconFor(CrossingType.Tunnel), "T", "Tunnel under the water (under its bed; the entrances on the shores)",
                            new Vector2(left + IconSize + 2f, y), r.Tunnel, () => SetWaterTunnel(d, r, true));
                        if (!r.Tunnel)
                        {
                            System.Action<int> wheel = dir => AddWaterExtra(d, r, dir * ClickStep());
                            UseButton(used++, Icons.Plus, "+", "Bridge higher over the water (+" + HeightStep + " m; or the mouse wheel)",
                                new Vector2(left + 2f * (IconSize + 2f), y), false, () => AddWaterExtra(d, r, ClickStep()), wheel);
                            UseButton(used++, Icons.Minus, "-", "Bridge lower over the water (-" + HeightStep + " m; or the mouse wheel)",
                                new Vector2(left + 3f * (IconSize + 2f), y), false, () => AddWaterExtra(d, r, -ClickStep()), wheel);
                        }
                    }
                }
            }
            HideButtons(used);
            for (int i = labelled; i < mapLabels.Count; i++) mapLabels[i].isVisible = false;
        }

        // The highest free spot at or above 'y' for a row of icons with its
        // label (22 px above it), and the row taken there.
        private static float FreeRow(List<Vector4> placed, float x, float y, float width)
        {
            const float label = 22f, gap = 4f;
            float height = IconSize + label;
            for (int guard = 0; guard < 20; guard++)
            {
                bool clear = true;
                foreach (var row in placed)
                    if (x < row.x + row.z + gap && row.x < x + width + gap && y - label < row.y + row.w - label + gap && row.y - label < y + IconSize + gap)
                    {
                        clear = false;
                        y = row.y - height - gap;
                        break;
                    }
                if (clear) break;
            }
            placed.Add(new Vector4(x, y, width, height));
            return y;
        }

        // Number and height of each item along the road, shown on the map.
        private readonly List<UILabel> mapLabels = new List<UILabel>();

        private void UseLabel(int index, string text, Vector2 position)
        {
            while (mapLabels.Count <= index)
            {
                var created = (UILabel)UIView.GetAView().AddUIComponent(typeof(UILabel));
                created.backgroundSprite = "GenericPanel";
                created.color = new Color32(20, 26, 34, 220);
                created.textColor = new Color32(255, 255, 255, 255);
                created.padding = new RectOffset(5, 5, 3, 2);
                created.textScale = 0.8f;
                created.autoSize = true;
                created.isInteractive = false;
                mapLabels.Add(created);
            }
            var label = mapLabels[index];
            if (label.text != text) label.text = text;
            label.relativePosition = new Vector3(position.x, position.y);
            label.isVisible = true;
        }

        private static bool ToGui(Camera camera, UIView view, Vector3 world, out Vector2 gui)
        {
            Vector3 screen = camera.WorldToScreenPoint(world);
            gui = Vector2.zero;
            if (screen.z <= 0f) return false;
            gui = view.ScreenPointToGUI(screen / view.inputScale);
            return true;
        }

        private void UseButton(int index, string icon, string letter, string tooltip, Vector2 position, bool selected, System.Action action,
            System.Action<int> wheel = null)
        {
            while (buttons.Count <= index)
            {
                int i = buttons.Count;
                var made = CreateButton(() => { if (i < buttonActions.Count && buttonActions[i] != null) buttonActions[i](); });
                RoadPanel.Wheel(() => { if (i < buttonWheels.Count && buttonWheels[i] != null) buttonWheels[i](1); },
                                () => { if (i < buttonWheels.Count && buttonWheels[i] != null) buttonWheels[i](-1); }, made);
                buttons.Add(made);
                buttonActions.Add(null);
                buttonWheels.Add(null);
            }
            var button = buttons[index];
            buttonActions[index] = action;
            buttonWheels[index] = wheel;
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

        // A crossing of 'division' (the curve, or the parallel line) chosen as 'type'.
        private void SetType(Division division, Crossing crossing, CrossingType type)
        {
            division.Setup.Types[crossing.Key] = type;
            // A ship route: the whole water crossing it is on goes over or under.
            var run = crossing.ShipPath ? RunAt(division.WaterRuns, crossing.Distance) : null;
            if (run != null) division.Setup.WaterTunnel[run.Key] = type == CrossingType.Tunnel;
            choicesVersion++;
            Mod.Log("Crossing with " + crossing.ExistingName + " (#" + crossing.Segment + "): " + crossing.Describe(type));
        }

        // Which time along our curve this crossing of its road counts as: the one
        // of that road remembered nearest it (within CrossingMatch, each once per
        // plan), so its choice follows it when the curve is edited, else a new
        // one; the place remembered is moved to where it is now.
        private static int PlaceOccurrence(List<KeyValuePair<int, Vector3>> places, Crossing crossing, HashSet<int> taken)
        {
            int nearest = -1;
            float best = CrossingMatch;
            for (int i = 0; i < places.Count; i++)
            {
                int key = places[i].Key;
                if ((key >> 4) != crossing.Segment || taken.Contains(key)) continue;
                float d = CurveMath.FlatDistance(places[i].Value, crossing.Point);
                if (d >= best) continue;
                best = d;
                nearest = i;
            }
            int occurrence;
            if (nearest >= 0)
            {
                occurrence = places[nearest].Key & 15;
                places[nearest] = new KeyValuePair<int, Vector3>(places[nearest].Key, crossing.Point);
            }
            else
            {
                occurrence = 0;
                while (occurrence < 15 && (taken.Contains((crossing.Segment << 4) | occurrence) || places.Exists(p => p.Key == ((crossing.Segment << 4) | occurrence))))
                    occurrence++;
                places.Add(new KeyValuePair<int, Vector3>((crossing.Segment << 4) | occurrence, crossing.Point));
            }
            taken.Add((crossing.Segment << 4) | occurrence);
            return occurrence;
        }

        private void AddExtra(Division division, Crossing crossing, float step)
        {
            float extra;
            var extras = division.Setup.Extra;
            extras.TryGetValue(crossing.Key, out extra);
            extras[crossing.Key] = Mathf.Clamp(extra + step, 0f, MaxExtraHeight);
            choicesVersion++;
        }

        // A water crossing of 'division' (the curve, or the parallel line): each keeps its own.
        private void SetWaterTunnel(Division division, WaterRun run, bool tunnel)
        {
            division.Setup.WaterTunnel[run.Key] = tunnel;
            choicesVersion++;
            Mod.Log("Water crossing at " + Format(run.Middle) + ": " + (tunnel ? "tunnel under it" : "bridge over it"));
        }

        private static WaterRun RunAt(List<WaterRun> runs, float at)
        {
            if (runs == null) return null;
            foreach (var run in runs) if (at >= run.From - 2f && at <= run.To + 2f) return run;
            return null;
        }

        private void AddWaterExtra(Division division, WaterRun run, float step)
        {
            float extra;
            var extras = division.Setup.WaterExtra;
            extras.TryGetValue(run.Key, out extra);
            extras[run.Key] = Mathf.Clamp(extra + step, -5f, MaxExtraHeight);
            choicesVersion++;
        }

        private readonly RoadPanel panel = new RoadPanel();

        private void ShowPanel()
        {
            if (!panel.Exists)
            {
                panel.Create();
                panel.Build = BuildCurrent;
                panel.Undo = NetworkBuilder.UndoLast;
                panel.Close = () =>
                {
                    ToolActivator.LeaveMode();
                    ToolsModifierControl.SetTool<DefaultTool>();
                };
                panel.GameTool = () => ToolActivator.SwitchToGameTool(SelectedNetwork);
                panel.SetStraight = SetStraight;
                panel.ChangeRadius = ChangeRadius;
                panel.ToggleSnap = ToggleSnapping;
                panel.ToggleDeck = ToggleDeck;
                panel.ToggleUnder = ToggleUnder;
                panel.Typed = SetTyped;
                panel.TypedSubmit = () => { if (!planning && points.Count > 0 && aimValid) AddAimPoint(); };
                panel.GrabNode = GrabNode;
                panel.DragHeight = DragHeight;
                panel.ReleaseNode = ReleaseNode;
                panel.NudgeNode = NudgeNode;
                panel.FreeNode = FreeNode;
                panel.MarkStretch = MarkStretch;
                panel.ResetShape = ResetShape;
                panel.StraightGrade = StraightGrade;
                panel.ToggleParallel = ToggleParallel;
                panel.ChangeGap = ChangeGap;
                panel.FlipSide = FlipSide;
                panel.PickBeside = PickBeside;
                panel.FlipDirection = FlipDirection;
                panel.ToggleUpgrade = ToggleUpgrade;
                panel.ToggleRamp = ToggleRamp;
                panel.PickMedianRamp = PickMedianRamp;
            }
            panel.Show(Model());
        }

        // Called by ToolActivator before the tool is destroyed.
        public void DestroyButtons()
        {
            panel.Destroy();
            foreach (var label in mapLabels) if (label != null) Destroy(label.gameObject);
            mapLabels.Clear();
            if (cursorLabel != null) Destroy(cursorLabel.gameObject);
            cursorLabel = null;
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
            NetSegment.CalculateMiddlePoints(bezier.a, division.Tangents[i - 1], bezier.d, -division.InTangents[i],
                false, false, out bezier.b, out bezier.c);
            return bezier;
        }

        // What the side panel shows for the current curve.
        private PanelModel Model()
        {
            var model = new PanelModel
            {
                Road = medianRamp != 0 ? char.ToUpper(MedianRampNames[medianRamp][0]) + MedianRampNames[medianRamp].Substring(1)
                     : SelectedNetwork != null ? Title(SelectedNetwork) : "No road picked: choose one in the game's Roads menu, then open RR",
                HasPoints = points.Count > 0,
                Straight = straightMode,
                Radius = cornerRadius,
                Snapping = snapping,
                Deck = deckMode && !underMode,
                Under = deckMode && underMode,
                Shaped = heightPins.Count > 0 || stretches.Count > 0,
                Parallel = parallelMode,
                Gap = parallelGap,
                ParallelLeft = parallelLeft,
                Beside = pickingBeside ? "pick its road or track in the game's menu"
                    : (besideNetwork != null ? Title(besideNetwork) : "the same road") + (parallelReverse ? ", other way" : ", same way"),
                BesideOther = besideNetwork != null || pickingBeside,
                ParallelReverse = parallelReverse,
                Upgrade = upgradeMode,
                Ramp = rampMode,
                MedianRamp = medianRamp != 0 ? MedianRampNames[medianRamp] : null,
                MedianChoice = medianRamp,
                Height = "Next point: " + FormatLift(lift) + "   (Page Up / Page Down, Shift: 1 m)",
                Mode = planning
                    ? "PLANNING: drag the points to adjust; Shift+click the curve: add a point; right-click a point: remove it; pick each crossing here or on the map. " +
                      "Check mark / Enter: build. Right-click elsewhere or Esc: back to drawing (Esc again: clear)."
                    : points.Count == 0 ? "DRAWING: click to place the first point. Tab: straight / curved pieces."
                    : "DRAWING: click to add points (Tab: straight / curved; type a length or turn above for an exact point). " +
                      "Double-click or Enter when done, to check and adjust the plan before building.",
            };
            if (upgradeMode) return UpgradeModel(model);
            var division = preview;
            if (division == null || division.Points.Count < 2 || division.Plan == null)
            {
                model.Summary = points.Count == 0 ? "Click on the map to place the first point" : "Place the next point";
                return model;
            }

            var plan = division.Plan;
            int raised = 0, lowered = 0;
            foreach (var kind in plan.Kinds)
            {
                if (IsRaised(kind)) raised++;
                if (IsLowered(kind)) lowered++;
            }
            model.Summary = division.Length.ToString("N0") + " m in " + (division.Points.Count - 1) + " segment(s)" +
                            (division.Cost > 0 ? "   ·   cost " + (division.Cost / 100).ToString("N0") : "");
            var details = new List<string>();
            if (raised > 0) details.Add("raised in places");
            if (plan.OnDeck != null)
            {
                int onDeck = 0, frames = 0, medians = 0;
                float ourHalf = SelectedNetwork != null ? SelectedNetwork.m_halfWidth : 8f;
                foreach (int i in PillarNodes(division))
                {
                    if (!plan.OnDeck[i] || plan.Decks[i].Segment == 0) continue;
                    var support = Decks.SupportFor(plan.Points[i], plan.Decks[i]);
                    if (support == Decks.Support.Column) medians++;
                    else if (support == Decks.Support.Portal) frames++;
                }
                int underRoad = 0;
                for (int i = 0; i < plan.OnDeck.Length; i++)
                {
                    if (!plan.OnDeck[i]) continue;
                    if (plan.UnderDeck != null && i < plan.UnderDeck.Length && plan.UnderDeck[i]) underRoad++;
                    else onDeck++;
                }
                if (underRoad > 0) details.Add("under the roads, one level down");
                if (onDeck > 0)
                    details.Add("deck over the roads below" + (medians > 0 ? ", " + medians + " column(s) on the median" : "") +
                                (frames > 0 ? ", " + frames + " frame(s) across roads" : ""));
            }
            if (lowered > 0) details.Add("lowered in places");
            if (division.Continues) details.Add("continues the road");
            model.Details = details.Count > 0 ? string.Join(", ", details.ToArray()) : "at ground level";
            string profileText;
            model.Profile = MakeProfile(division, out profileText);
            model.ProfileText = profileText;
            if (!division.Ok) model.Problems.Add("A bend is too sharp (over " + MaxBend + " degrees per segment): spread the points out");

            // Everything along the road, in order from the first point: crossings
            // and water bridges in one numbered list (the same numbers are on the
            // map); then the parallel line's, numbered with a P.
            AddItems(model, division, "");
            if (second != null && second.Plan != null)
            {
                AddItems(model, second, "P");
                model.Summary += "   ·   " + (medianRamp != 0 ? "ground road " : "parallel ") + second.Length.ToString("N0") + " m" +
                                 (second.Cost > 0 ? ", cost " + (second.Cost / 100).ToString("N0") : "");
            }
            model.Problems.AddRange(plan.Problems);
            model.Warnings.AddRange(plan.Warnings);
            model.CanBuild = DrawNetwork != null && division.Ok && plan.Ok;
            if (parallelMode)
            {
                string why = ParallelBlocked(pointDeck) ?? (deckMode && !planning && points.Count > 0 ? ParallelBlocked(new List<bool> { true }) : null);
                if (why == null && (second == null || second.Plan == null)) why = "the parallel line can't be drawn here";
                if (why != null) model.Problems.Add(char.ToUpper(why[0]) + why.Substring(1));
                else
                {
                    if (!second.Ok) model.Problems.Add("Parallel line: a bend is too sharp (over " + MaxBend + " degrees per segment)");
                    foreach (var problem in second.Plan.Problems) model.Problems.Add("Parallel line: " + problem);
                    foreach (var warning in second.Plan.Warnings) model.Warnings.Add("Parallel line: " + warning);
                }
                model.CanBuild &= why == null && second.Ok && second.Plan.Ok;
            }
            // A median ramp road's ground road: what stops it stops the build
            // (else the ramp would be built and taken away again).
            if (medianRamp != 0 && !plan.MedianAlong)
            {
                if (second == null || second.Plan == null) model.Problems.Add("The ground road beside the ramp can't be drawn here");
                else
                {
                    if (!second.Ok) model.Problems.Add("Ground road: a bend is too sharp (over " + MaxBend + " degrees per segment)");
                    foreach (var problem in second.Plan.Problems) model.Problems.Add("Ground road: " + problem);
                    foreach (var warning in second.Plan.Warnings) model.Warnings.Add("Ground road: " + warning);
                }
                model.CanBuild &= second != null && second.Plan != null && second.Ok && second.Plan.Ok;
            }
            return model;
        }

        // The panel's rows for everything along 'division', numbered from 'prefix'1.
        private void AddItems(PanelModel model, Division division, string prefix)
        {
            var items = Along(division);
            for (int n = 0; n < items.Count; n++)
            {
                var item = items[n];
                var row = new PanelCrossing
                {
                    Title = prefix + (n + 1) + "   at " + item.At.ToString("N0") + " m   " + ItemName(item),
                    Detail = ItemDetail(division, item),
                    Step = HeightStep,
                };
                if (item.Crossing != null)
                {
                    var crossing = item.Crossing;
                    row.Selected = crossing.Type;
                    row.HeightButtons = crossing.Type == CrossingType.Bridge;
                    foreach (var type in AllTypes) if (crossing.Allows(type)) row.Allowed.Add(type);
                    row.Pick = type => SetType(division, crossing, type);
                    row.Raise = step => AddExtra(division, crossing, step);
                }
                else
                {
                    var run = item.Water;
                    row.Allowed.Add(CrossingType.Bridge);
                    row.Allowed.Add(CrossingType.Tunnel);
                    row.Selected = run.Tunnel ? CrossingType.Tunnel : CrossingType.Bridge;
                    row.Pick = type => SetWaterTunnel(division, run, type == CrossingType.Tunnel);
                    row.HeightButtons = !run.Tunnel;
                    row.Raise = step => AddWaterExtra(division, run, step);
                }
                model.Items.Add(row);
            }
        }

        // The road's name as the game shows it.
        private static string Title(NetInfo info)
        {
            try
            {
                string title = info.GetUncheckedLocalizedTitle();
                if (!string.IsNullOrEmpty(title)) return title;
            }
            catch { }
            return info.name;
        }

        // A crossing or a water bridge, at its distance along the curve.
        private class AlongItem
        {
            public float At;
            public Crossing Crossing;
            public WaterRun Water;
            public Vector3 Where;
        }

        // Crossings and water bridges in order along the curve.
        private static List<AlongItem> Along(Division division)
        {
            var items = new List<AlongItem>();
            foreach (var crossing in division.Crossings)
                items.Add(new AlongItem { At = crossing.Distance, Crossing = crossing, Where = crossing.Point });
            foreach (var run in division.WaterRuns)
                items.Add(new AlongItem { At = run.From, Water = run, Where = run.Middle });
            items.Sort((x, y) => x.At.CompareTo(y.At));
            return items;
        }

        private static string ItemName(AlongItem item)
        {
            return item.Crossing != null ? item.Crossing.ExistingName
                : "water, " + (item.Water.To - item.Water.From).ToString("N0") + " m wide";
        }

        // How it is crossed and how high: the deck above the water or the crossed
        // road (its own surface, not the ground under it), or how deep below it.
        private static string ItemDetail(Division division, AlongItem item)
        {
            var plan = division.Plan;
            var terrain = TerrainManager.instance;
            if (item.Water != null)
            {
                float middle = (item.Water.From + item.Water.To) * 0.5f;
                if (item.Water.Tunnel)
                {
                    float cover = terrain.SampleRawHeightSmooth(item.Water.Middle) - HeightAt(plan, middle);
                    return "tunnel, " + cover.ToString("F0") + " m under the bed";
                }
                float deck = HeightAt(plan, middle) - terrain.SampleRawHeightSmoothWithWater(item.Water.Middle, false, 0f);
                return "bridge, deck " + deck.ToString("F0") + " m above the water";
            }
            var crossing = item.Crossing;
            float ours = HeightAt(plan, crossing.Distance);
            float gap = ours - crossing.Point.y;   // a ship route's point is on the water
            string where = gap >= 0f ? (crossing.ShipPath ? " above the water" : " above it")
                : crossing.ShipPath ? " below the water" : " below it";
            return crossing.Describe() + (Mathf.Abs(gap) >= 1f ? ", " + Mathf.Abs(gap).ToString("F0") + " m" + where : "");
        }

        // The short height shown on the map next to an item's number.
        private static string ItemHeight(Division division, AlongItem item)
        {
            var plan = division.Plan;
            float at = item.Water != null ? (item.Water.From + item.Water.To) * 0.5f : item.Crossing.Distance;
            float under = item.Water != null
                ? TerrainManager.instance.SampleRawHeightSmoothWithWater(item.Where, false, 0f)
                : item.Crossing.Point.y;
            float gap = HeightAt(plan, at) - under;
            return Mathf.Abs(gap) < 1f ? "level" : (gap > 0f ? "+" : "-") + Mathf.Abs(gap).ToString("F0") + " m";
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

        // --- planning: the drawn curve kept as a plan to adjust before building ---

        private bool planning;          // done drawing: the plan stays put, points can be dragged
        private int dragIndex = -1;     // the point being dragged (-1: none)
        private const float HandleSize = 12f;
        private const float GrabPixels = 18f;

        private void StartPlanning()
        {
            if (points.Count < 2)
            {
                Mod.Log("Place at least 2 points first");
                return;
            }
            planning = true;
            dragIndex = -1;
            choicesVersion++;
            Mod.Log("Planning: drag the points to adjust, pick each crossing; Enter or the check mark builds it, Esc scraps it");
        }

        // The point whose marker is under the mouse on screen (-1: none).
        // Planning: a point put into the curve where the mouse is on it (within
        // InsertPixels on the screen), between the points either side; the
        // heights pinned and stretches marked stay where they were along it.
        private const float InsertPixels = 24f;

        private void InsertPointUnderMouse()
        {
            var camera = Camera.main;
            var line = preview;
            if (camera == null || line == null || line.Polyline == null || line.ControlAt == null || points.Count < 2) return;
            Vector3 mouse = Input.mousePosition;
            float best = InsertPixels * InsertPixels, bestAlong = -1f, run = 0f;
            Vector3 bestPoint = Vector3.zero;
            for (int i = 0; i < line.Polyline.Count; i++)
            {
                if (i > 0) run += CurveMath.FlatDistance(line.Polyline[i - 1], line.Polyline[i]);
                Vector3 screen = camera.WorldToScreenPoint(line.Polyline[i]);
                if (screen.z <= 0f) continue;
                float dx = screen.x - mouse.x, dy = screen.y - mouse.y;
                if (dx * dx + dy * dy >= best) continue;
                best = dx * dx + dy * dy;
                bestAlong = run;
                bestPoint = line.Polyline[i];
            }
            if (bestAlong < 0f)
            {
                Mod.Log("Shift+click on the curve itself to put a point in there");
                return;
            }
            int span = 0;
            while (span + 1 < line.ControlAt.Length && line.ControlAt[span + 1] <= bestAlong) span++;
            if (span >= points.Count - 1) span = points.Count - 2;
            float a = line.ControlAt[span], b = line.ControlAt[Mathf.Min(span + 1, line.ControlAt.Length - 1)];
            float f = b - a > 0.01f ? Mathf.Clamp((bestAlong - a) / (b - a), 0.05f, 0.95f) : 0.5f;
            int index = span + 1;
            bestPoint.y = TerrainManager.instance.SampleRawHeightSmooth(bestPoint);
            points.Insert(index, bestPoint);
            pointNodes.Insert(index, 0);
            pointMarkers.Insert(index, bestPoint);
            pointSegments.Insert(index, 0);
            pointTs.Insert(index, 0f);
            pointLifts.Insert(index, pointLifts[span]);
            pointStraight.Insert(index, pointStraight[index]);
            pointDeck.Insert(index, pointDeck[index]);
            pointUnder.Insert(index, pointUnder[index]);
            pointRoad.Insert(index, 0);
            pointRoadT.Insert(index, 0f);
            // Pins and stretches are placed by point (a point's number and how far
            // on to the next): those past the new point move one on.
            Func<float, float> shift = at =>
                at >= span + 1 ? at + 1f
                : at <= span ? at
                : at - span <= f ? span + (at - span) / f
                : span + 1 + (at - span - f) / (1f - f);
            foreach (var pin in heightPins) pin.At = shift(pin.At);
            foreach (var stretch in stretches)
            {
                stretch.From = shift(stretch.From);
                stretch.To = shift(stretch.To);
            }
            choicesVersion++;
            Mod.Log("Point " + (index + 1) + " put in at " + Format(bestPoint) + " (" + points.Count + " points)");
        }

        // Planning: point 'index' taken out (two at least stay).
        private void RemovePointAt(int index)
        {
            if (index < 0 || index >= points.Count || points.Count <= 2) return;
            points.RemoveAt(index);
            pointNodes.RemoveAt(index);
            pointMarkers.RemoveAt(index);
            pointSegments.RemoveAt(index);
            pointTs.RemoveAt(index);
            pointLifts.RemoveAt(index);
            pointStraight.RemoveAt(index);
            pointDeck.RemoveAt(index);
            pointUnder.RemoveAt(index);
            pointRoad.RemoveAt(index);
            pointRoadT.RemoveAt(index);
            // Placed by point: past it, one back; on its two spans, onto the one span left.
            Func<float, float> shift = at =>
                at >= index + 1 ? at - 1f
                : at <= index - 1 ? at
                : Mathf.Max(index - 1, 0) + Mathf.Clamp01((at - (index - 1)) * 0.5f);
            foreach (var pin in heightPins) pin.At = shift(pin.At);
            foreach (var stretch in stretches)
            {
                stretch.From = shift(stretch.From);
                stretch.To = shift(stretch.To);
            }
            if (dragIndex >= points.Count) dragIndex = -1;
            choicesVersion++;
            Mod.Log("Point " + (index + 1) + " taken out (" + points.Count + " points left)");
        }

        private int PointUnderMouse()
        {
            var camera = Camera.main;
            if (camera == null) return -1;
            Vector3 mouse = Input.mousePosition;
            int best = -1;
            float bestDistance = GrabPixels * GrabPixels;
            for (int i = 0; i < pointMarkers.Count; i++)
            {
                Vector3 screen = camera.WorldToScreenPoint(pointMarkers[i]);
                if (screen.z <= 0f) continue;
                float dx = screen.x - mouse.x, dy = screen.y - mouse.y;
                if (dx * dx + dy * dy < bestDistance)
                {
                    bestDistance = dx * dx + dy * dy;
                    best = i;
                }
            }
            return best;
        }

        // The dragged point follows the mouse (snapping to roads like a new
        // point) until the button is let go.
        private void UpdateDrag()
        {
            if (!Input.GetMouseButton(0) || dragIndex >= points.Count)
            {
                if (dragIndex >= 0 && dragIndex < points.Count) Mod.Log("Point " + (dragIndex + 1) + " moved to " + Format(points[dragIndex]));
                dragIndex = -1;
                return;
            }
            if (!hoverValid || !mouseRayValid) return;
            Vector3 p = hoverPosition;
            if ((p - points[dragIndex]).sqrMagnitude < 0.25f && pointNodes[dragIndex] == hoverNode) return;
            points[dragIndex] = p;
            pointNodes[dragIndex] = hoverNode;
            pointMarkers[dragIndex] = hoverMarker;
            pointSegments[dragIndex] = hoverNode == 0 ? hoverSegment : (ushort)0;
            pointTs[dragIndex] = hoverT;
            ushort road;
            float roadT;
            if (pointDeck[dragIndex] && OwnDeckAt(hoverNode, hoverSegment, hoverT))
            {
                // Onto a deck of ours: joined to it, going on over the road under it.
                RoadUnderDeck(hoverNode, hoverSegment, hoverT, out road, out roadT);
                pointRoad[dragIndex] = road;
                pointRoadT[dragIndex] = roadT;
            }
            else if (pointDeck[dragIndex] && !((rampMode || medianRamp != 0) && dragIndex == 0) && RoadAt(hoverNode, hoverSegment, hoverT, out road, out roadT))
            {
                // A deck point stays over the road, not joined to it.
                pointNodes[dragIndex] = 0;
                pointSegments[dragIndex] = 0;
                pointRoad[dragIndex] = road;
                pointRoadT[dragIndex] = roadT;
            }
            else pointRoad[dragIndex] = 0;
            choicesVersion++;
        }

        private void ClearPoints()
        {
            planning = false;
            dragIndex = -1;
            points.Clear();
            pointNodes.Clear();
            pointMarkers.Clear();
            pointSegments.Clear();
            pointTs.Clear();
            pointLifts.Clear();
            pointStraight.Clear();
            pointDeck.Clear();
            pointUnder.Clear();
            pointRoad.Clear();
            pointRoadT.Clear();
            Decks.ForgetPaths();
            ClearTyped();
            crossingTypes.Clear();
            crossingExtra.Clear();
            parallelTypes.Clear();
            parallelExtra.Clear();
            parallelWaterExtra.Clear();
            parallelWaterTunnel.Clear();
            parallelWaterPlaces.Clear();
            parallelCrossingPlaces.Clear();
            second = null;
            waterExtra.Clear();
            waterTunnel.Clear();
            waterPlaces.Clear();
            crossingPlaces.Clear();
            heightPins.Clear();
            stretches.Clear();
            draggedPin = null;
            upgradePoints.Clear();
            upgradePlan = null;
            preview = null;
            choicesVersion++;
        }

        private static string Format(Vector3 v)
        {
            return "(" + v.x.ToString("F1") + ", " + v.y.ToString("F1") + ", " + v.z.ToString("F1") + ")";
        }
    }
}
