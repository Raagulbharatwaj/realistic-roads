using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace RealisticRoads
{
    // What the side panel shows; filled by the curve tool each frame.
    public class PanelModel
    {
        public string Road = "";
        public string Height = "";                // the next point's height (Page Up / Page Down)
        public string Mode = "";                  // drawing or planning, and what to do next
        public string Summary = "";               // length, segments, cost
        public string Details = "";               // raised / lowered / continues ...
        // Crossings and water bridges, in order along the road (numbered as on the map).
        public readonly List<PanelCrossing> Items = new List<PanelCrossing>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool CanBuild;
        public bool HasPoints;
        public bool Straight;                     // the next piece is straight (else curved)
        public float Radius;                      // corners between straight pieces (0: sharp)
        public bool Snapping;                     // straight pieces snap their angle and length
        public bool Deck;                         // the next piece is a deck over the roads below
        public bool Under;                        // the next piece runs one level under the roads
        public PanelProfile Profile;              // the curve seen from the side (null: nothing to show)
        public string ProfileText = "";           // highest / deepest / steepest
        public bool Shaped;                       // heights dragged or stretches marked in the side view
        public bool Parallel;                     // a second road or track is built beside the curve
        public float Gap;                         // metres between their edges
        public bool ParallelLeft;                 // it is on the left of the curve as drawn
        public string Beside = "";                // what it is
        public bool BesideOther;                  // another road or track (not the same road the other way)
        public bool Upgrade;                      // changing an existing road instead of drawing one
        public bool Walls;                        // retaining walls along cuttings and lowered roads
    }

    // How a stretch of the curve is built: the colours of the preview and the profile.
    public enum ProfileStyle : byte { Ground, Raised, Lowered, Steep, TooSharp }

    // The curve seen from the side: the road's height and the ground (and
    // water) under it at evenly spaced points, with pillars and crossings.
    public class PanelProfile
    {
        public float Length;                      // metres along the curve
        public float[] Road, Ground, Water;       // heights at evenly spaced samples
        public ProfileStyle[] Style;              // per sample
        public Vector3[] Positions;               // where each sample is on the map
        public readonly List<float> Pillars = new List<float>();     // metres along the curve
        public readonly List<KeyValuePair<float, float>> Crossings = new List<KeyValuePair<float, float>>();   // metres along, crossed road's height
        public float[] NodeAlong, NodeHeight;     // the nodes: metres along, height
        public bool[] NodePinned;                 // its height was dragged
        public bool[] NodeHeld;                   // its height is set by an end or a crossing (not draggable)
        public readonly List<PanelStretch> Stretches = new List<PanelStretch>();
    }

    // A stretch marked Elevated or Tunnel in the side view (metres along).
    public class PanelStretch
    {
        public float From, To;
        public Forced Kind;
    }

    // One thing along the road: a crossing (its options as icons) or a water
    // bridge (no options, only higher / lower).
    public class PanelCrossing
    {
        public string Title = "";                 // "3   at 1,240 m   Train Track"
        public string Detail = "";                // "bridge over it, 11 m above it"
        public readonly List<CrossingType> Allowed = new List<CrossingType>();
        public CrossingType Selected;
        public bool HeightButtons;                // bridge: +/- for the deck
        public float Step;                        // metres per +/- click
        public Action<CrossingType> Pick;
        public Action<float> Raise;
    }

    // The curve tool's side panel: a movable window with the road, the curve's
    // summary, each crossing with its choices, water bridges, problems and the
    // Build / Undo / Clear buttons. Main thread only.
    public class RoadPanel
    {
        private const float Width = 340f;
        private const float Pad = 12f;
        private const float RowGap = 4f;
        private const float IconSize = 30f;
        private const string PositionKey = "RealisticRoadsPanel";

        private static readonly Color32 HeaderColor = new Color32(140, 190, 255, 255);
        private static readonly Color32 TextColor = new Color32(235, 235, 235, 255);
        private static readonly Color32 DimColor = new Color32(170, 175, 185, 255);
        private static readonly Color32 ProblemColor = new Color32(255, 110, 90, 255);
        private static readonly Color32 WarningColor = new Color32(255, 210, 90, 255);

        private const int ProfileWidth = (int)(Width - 2f * Pad);
        private const int ProfileHeight = 92;
        private const float MinProfileRange = 24f;   // metres shown at least, so a flat road isn't drawn as a mountain

        private static readonly Color32 ProfileBack = new Color32(24, 28, 34, 255);
        private static readonly Color32 EarthColor = new Color32(96, 112, 72, 255);
        private static readonly Color32 WaterColor = new Color32(64, 120, 200, 255);
        private static readonly Color32 PillarLine = new Color32(200, 200, 200, 255);
        private static readonly Color32 CrossingMark = new Color32(255, 255, 255, 255);

        private UIPanel panel;
        private UITextureSprite profileSprite;
        private Texture2D profileTexture;
        private PanelProfile drawnProfile;
        private Color32[] profilePixels;          // what the texture shows: profileBase with the selection and cursor
        private UILabel profileReadout;
        private float cursorAlong = -1f;          // metres along the curve under the mouse on the side view (-1: none)
        private bool cursorPinned;                // clicked: stays when the mouse leaves
        // Dragging a node's height: the scale stays as it was when it was grabbed.
        public Func<int, bool> GrabNode;
        public Action<float> DragHeight;
        public Action ReleaseNode;
        public Action<int> FreeNode;
        public Action<float, float, Forced> MarkStretch;
        public Action ResetShape;
        private int draggedNode = -1;
        private float drawLow, drawHigh;          // the heights at the bottom and top of the drawn side view
        private float dragLow, dragHigh;          // those when the node was grabbed
        private bool pressed;                     // left button down on the side view, not on a node
        private float pressX;
        private bool selecting;                   // that press moved: a stretch is being selected
        private float selectFrom = -1f, selectTo = -1f;   // the selected stretch, metres along (-1: none)
        private Color32[] profileBase;            // the drawn side view without the selection and cursor
        private UIButton elevatedButton, tunnelButton, autoButton, resetButton;
        private UILabel shapeHint;                // what the buttons act on
        private const float NodeGrab = 6f;        // pixels from a node's dot that grab it
        private const float MinStretch = 10f;     // metres: a shorter selection is a click
        private const float ShapeButtonSize = 26f;
        private static readonly Color32 NodeDot = new Color32(210, 215, 225, 255);
        private static readonly Color32 PinnedDot = new Color32(255, 255, 255, 255);
        private static readonly Color32 PinnedCore = new Color32(30, 30, 30, 255);
        private readonly List<UIComponent> dynamic = new List<UIComponent>();
        private readonly List<KeyValuePair<UILabel, Func<PanelModel, string>>> texts = new List<KeyValuePair<UILabel, Func<PanelModel, string>>>();
        private string structure;
        private PanelModel current;

        public Action Build, Undo, Close, GameTool;
        // The DRAW bar: straight or curved pieces, corner radius, snapping, and
        // a typed length / turn for the next point (submitted: place it).
        public Action<bool> SetStraight;
        public Action<float> ChangeRadius;
        public Action ToggleSnap;
        public Action ToggleDeck, ToggleUnder;
        // The parallel line: on / off, the gap, which side, and what it is.
        public Action ToggleParallel, FlipSide, PickBeside;
        public Action<float> ChangeGap;
        private UIButton parallelButton, sideButton, besideButton;
        private UILabel gapLabel, besideLabel;
        // Upgrade an existing road along a stretch; retaining walls on or off.
        public Action ToggleUpgrade, ToggleWalls;
        private UIButton upgradeButton, wallsButton;
        private UILabel toolsLabel;
        public Action<string, string> Typed;
        public Action TypedSubmit;
        private UIButton curveButton, straightButton, snapButton, deckButton, underButton;
        private UILabel radiusLabel;
        private UITextField lengthField, turnField;
        private const float BarTop = 46f;
        private const float ContentTop = BarTop + 134f;  // the rows below the DRAW bar
        private UIButton buildButton, undoButton;
        private const int PageSize = 5;             // items along the road shown at a time
        private int page;                           // which PageSize items are shown
        private UIComponent parent;                 // where the row helpers add their parts

        public bool Exists => panel != null;

        public void Create()
        {
            if (panel != null) return;
            panel = (UIPanel)UIView.GetAView().AddUIComponent(typeof(UIPanel));
            panel.name = "RealisticRoadsPanel";
            panel.backgroundSprite = "MenuPanel2";
            panel.size = new Vector2(Width, 200f);
            panel.relativePosition = new Vector3(70f, 120f);
            try
            {
                if (PlayerPrefs.HasKey(PositionKey + ".x"))
                    panel.relativePosition = new Vector3(PlayerPrefs.GetFloat(PositionKey + ".x"), PlayerPrefs.GetFloat(PositionKey + ".y"));
            }
            catch { }

            // Title bar: drag to move (remembered), X closes the tool.
            var drag = (UIDragHandle)panel.AddUIComponent(typeof(UIDragHandle));
            drag.target = panel;
            drag.size = new Vector2(Width - 40f, 40f);
            drag.relativePosition = Vector3.zero;
            drag.eventMouseUp += (component, param) => SavePosition();

            var icon = (UIButton)panel.AddUIComponent(typeof(UIButton));
            icon.size = new Vector2(28f, 28f);
            icon.relativePosition = new Vector3(10f, 6f);
            icon.isInteractive = false;
            if (Icons.Has(Icons.LogoSmall))
            {
                icon.atlas = Icons.Atlas;
                icon.normalFgSprite = Icons.LogoSmall;
            }

            var title = (UILabel)panel.AddUIComponent(typeof(UILabel));
            title.text = "Realistic Roads";
            title.textScale = 1.0f;
            title.relativePosition = new Vector3(44f, 11f);
            title.isInteractive = false;

            var close = (UIButton)panel.AddUIComponent(typeof(UIButton));
            close.size = new Vector2(32f, 32f);
            close.relativePosition = new Vector3(Width - 38f, 4f);
            close.normalBgSprite = "buttonclose";
            close.hoveredBgSprite = "buttonclosehover";
            close.pressedBgSprite = "buttonclosepressed";
            close.tooltip = "Leave the curve tool (Esc)";
            close.eventClick += (component, param) => { if (Close != null) Close(); };

            // The actions, as icons in the title bar: build is green when the
            // curve can be built, undo lit when there is something to undo.
            float x = Width - 40f - 3f * (ActionSize + 4f);
            ActionButton(Icons.GameTool, Icons.GameTool, Icons.Background, "Game", x,
                "Draw this road with the game's own road tool instead", () => { if (GameTool != null) GameTool(); });
            undoButton = ActionButton(Icons.Undo, Icons.UndoOff, Icons.Background, "Undo", x + ActionSize + 4f,
                "Undo the last road built (Ctrl+Z)", () => { if (Undo != null) Undo(); });
            buildButton = ActionButton(Icons.Build, Icons.BuildOff, Icons.BackgroundSelected, "Build", x + 2f * (ActionSize + 4f),
                "Build it (Enter, or double-click the last point)", () => { if (Build != null) Build(); });
            SetEnabled(buildButton, false);
            SetEnabled(undoButton, false);
            CreateDrawBar();

            panel.isVisible = false;
        }

        // --- the DRAW bar (made once; only its state changes) ----------------------

        private void CreateDrawBar()
        {
            float y = BarTop;
            curveButton = BarButton(Icons.Curve, "C", "Curved pieces: the road bends smoothly through the points (Tab)", Pad, y,
                () => { if (SetStraight != null) SetStraight(false); });
            straightButton = BarButton(Icons.Straight, "S", "Straight pieces, with rounded corners between them (Tab)\n" +
                "Snaps to 15° turns, along or across nearby roads, and 8 m lengths; hold Shift to draw freely", Pad + IconSize + 3f, y,
                () => { if (SetStraight != null) SetStraight(true); });
            snapButton = BarButton(Icons.Snap, "Sn", "Snapping for straight pieces (on: green)", Pad + 2f * (IconSize + 3f) + 8f, y,
                () => { if (ToggleSnap != null) ToggleSnap(); });
            deckButton = BarButton(Icons.Deck, "D", "Deck: the next pieces run over the roads below, a level up (double or triple " +
                "decker)\nClick on a road to put a deck point over its middle; between two such points the deck follows the road.\n" +
                "Pillars stand on the road's median if it has one, else a frame spans the road", Pad + 3f * (IconSize + 3f) + 8f, y,
                () => { if (ToggleDeck != null) ToggleDeck(); });
            underButton = BarButton(Icons.Under, "U", "Under: the next pieces run one level under the roads, as a tunnel (metro under a " +
                "street)\nClick on a road to put a point under its middle; between two such points the tunnel follows the road.\n" +
                "One level only: not under a road that is already underground", Pad + 4f * (IconSize + 3f) + 8f, y,
                () => { if (ToggleUnder != null) ToggleUnder(); });
            float x = Pad + 5f * (IconSize + 3f) + 11f;
            BarLabel("Corner", x, y + 8f, 38f);
            BarButton(Icons.Minus, "-", "Tighter corners between straight pieces (below " + CurveTool.MinCornerRadius.ToString("0") + " m: a sharp corner)", x + 38f, y,
                () => { if (ChangeRadius != null) ChangeRadius(-CurveTool.RadiusStep); });
            radiusLabel = BarLabel("", x + 38f + IconSize, y + 8f, 38f);
            radiusLabel.textAlignment = UIHorizontalAlignment.Center;
            BarButton(Icons.Plus, "+", "Wider corners between straight pieces", x + 76f + IconSize, y,
                () => { if (ChangeRadius != null) ChangeRadius(CurveTool.RadiusStep); });

            y += IconSize + 6f;
            BarLabel("Length", Pad, y + 4f, 48f);
            lengthField = BarField(Pad + 50f, y, 62f, "Length of the next piece in metres; Enter places the point");
            BarLabel("m", Pad + 116f, y + 4f, 16f);
            BarLabel("Turn", Pad + 140f, y + 4f, 36f);
            turnField = BarField(Pad + 176f, y, 52f,
                "Turn from the last piece in degrees: + right, - left (the first piece: bearing from north); Enter places the point");
            BarLabel("° (+ right)", Pad + 232f, y + 4f, 84f);

            // Parallel: a second road beside the curve (a one-way pair, or a
            // road and a track), the gap between them, its side, and what it is.
            y += 30f;
            parallelButton = BarButton(Icons.Parallel, "P", "Parallel: build a second road beside the curve, at the gap set here.\n" +
                "The same road runs the other way (a one-way pair); or pick a road or track for it with the button on the right.\n" +
                "Each is planned on its own (crossings, bridges, tunnels); one Build and one Ctrl+Z for both", Pad, y,
                () => { if (ToggleParallel != null) ToggleParallel(); });
            float gx = Pad + IconSize + 6f;
            BarLabel("Gap", gx, y + 8f, 28f);
            BarButton(Icons.Minus, "-", "Narrower gap between the two roads' edges", gx + 28f, y,
                () => { if (ChangeGap != null) ChangeGap(-CurveTool.GapStep); });
            gapLabel = BarLabel("", gx + 28f + IconSize, y + 8f, 36f);
            gapLabel.textAlignment = UIHorizontalAlignment.Center;
            BarButton(Icons.Plus, "+", "Wider gap between the two roads' edges (up to " + CurveTool.MaxGap.ToString("0") + " m)",
                gx + 64f + IconSize, y, () => { if (ChangeGap != null) ChangeGap(CurveTool.GapStep); });
            float sx = gx + 64f + 2f * IconSize + 6f;
            sideButton = BarButton(Icons.Side, "<>", "Put the parallel road on the other side of the curve", sx, y,
                () => { if (FlipSide != null) FlipSide(); });
            besideButton = BarButton(Icons.Beside, "2", "What goes beside: pick a road or track in the game's menu, press this, " +
                "then pick the curve's own road.\nPress again for the same road as the curve, running the other way", sx + IconSize + 3f, y,
                () => { if (PickBeside != null) PickBeside(); });
            besideLabel = BarLabel("", sx + 2f * IconSize + 6f, y + 2f, Width - Pad - (sx + 2f * IconSize + 6f));
            besideLabel.wordWrap = true;
            besideLabel.size = new Vector2(besideLabel.size.x, 28f);
            besideLabel.textScale = 0.65f;

            // Upgrade an existing road instead of drawing one; retaining walls.
            y += IconSize + 4f;
            upgradeButton = BarButton(Icons.Upgrade, "Up", "Upgrade along a curve: click a road where it starts, then where it ends;\n" +
                "every road between becomes the road picked in the game's menu, on the same nodes (shape and junctions kept),\n" +
                "each in its own version (elevated, tunnel...). One Ctrl+Z puts the old road back", Pad, y,
                () => { if (ToggleUpgrade != null) ToggleUpgrade(); });
            wallsButton = BarButton(Icons.Walls, "W", "Retaining walls along cuttings, tunnel ramps and the roads lowered for an " +
                "overpass, up to the ground beside them (on: green)", Pad + IconSize + 3f, y,
                () => { if (ToggleWalls != null) ToggleWalls(); });
            toolsLabel = BarLabel("", Pad + 2f * IconSize + 10f, y + 8f, Width - 2f * Pad - 2f * IconSize - 10f);
        }

        private UIButton BarButton(string icon, string letter, string tooltip, float x, float y, Action onClick)
        {
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
            button.size = new Vector2(IconSize, IconSize);
            button.relativePosition = new Vector3(x, y);
            button.tooltip = tooltip;
            if (Icons.Has(icon, Icons.Background, Icons.BackgroundHover, Icons.BackgroundSelected))
            {
                button.atlas = Icons.Atlas;
                button.normalFgSprite = button.hoveredFgSprite = button.pressedFgSprite = button.focusedFgSprite = icon;
                button.normalBgSprite = button.focusedBgSprite = Icons.Background;
                button.hoveredBgSprite = Icons.BackgroundHover;
                button.pressedBgSprite = Icons.BackgroundSelected;
            }
            else
            {
                button.normalBgSprite = button.focusedBgSprite = "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed";
                button.text = letter;
                button.textScale = 0.8f;
            }
            button.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0) onClick(); };
            return button;
        }

        private UILabel BarLabel(string text, float x, float y, float width)
        {
            var label = (UILabel)panel.AddUIComponent(typeof(UILabel));
            label.autoSize = false;
            label.size = new Vector2(width, 16f);
            label.textScale = 0.75f;
            label.textColor = DimColor;
            label.relativePosition = new Vector3(x, y);
            label.isInteractive = false;
            label.text = text;
            return label;
        }

        private UITextField BarField(float x, float y, float width, string tooltip)
        {
            var field = (UITextField)panel.AddUIComponent(typeof(UITextField));
            field.size = new Vector2(width, 22f);
            field.relativePosition = new Vector3(x, y);
            field.tooltip = tooltip;
            field.normalBgSprite = "TextFieldPanel";
            field.hoveredBgSprite = "TextFieldPanelHovered";
            field.focusedBgSprite = "TextFieldPanel";
            field.selectionSprite = "EmptySprite";
            field.builtinKeyNavigation = true;
            field.isInteractive = true;
            field.canFocus = true;
            field.readOnly = false;
            field.selectOnFocus = true;
            field.submitOnFocusLost = false;   // clicking the map afterwards must not place a second point
            field.maxLength = 7;
            field.textScale = 0.8f;
            field.textColor = TextColor;
            field.padding = new RectOffset(6, 6, 4, 0);
            field.horizontalAlignment = UIHorizontalAlignment.Left;
            field.eventTextChanged += (component, text) => SendTyped();
            field.eventTextSubmitted += (component, text) =>
            {
                SendTyped();
                if (TypedSubmit != null) TypedSubmit();
            };
            return field;
        }

        private void SendTyped()
        {
            if (Typed != null && lengthField != null && turnField != null) Typed(lengthField.text, turnField.text);
        }

        // After the typed point is placed: empty fields for the next one.
        public void ClearTyped()
        {
            if (lengthField != null && lengthField.text != "") lengthField.text = "";
            if (turnField != null && turnField.text != "") turnField.text = "";
        }

        private void UpdateDrawBar(PanelModel model)
        {
            Select(curveButton, !model.Straight);
            Select(straightButton, model.Straight);
            Select(snapButton, model.Snapping);
            Select(deckButton, model.Deck);
            Select(underButton, model.Under);
            Select(parallelButton, model.Parallel);
            Select(sideButton, model.Parallel && !model.ParallelLeft);
            Select(besideButton, model.Parallel && model.BesideOther);
            Select(upgradeButton, model.Upgrade);
            Select(wallsButton, model.Walls);
            if (toolsLabel != null)
            {
                string tools = (model.Upgrade ? "Upgrading a road" : "Drawing") + " · walls " + (model.Walls ? "on" : "off");
                if (toolsLabel.text != tools) toolsLabel.text = tools;
            }
            if (gapLabel != null)
            {
                string gap = model.Gap.ToString("0") + " m";
                if (gapLabel.text != gap) gapLabel.text = gap;
            }
            if (besideLabel != null)
            {
                string beside = model.Parallel ? (model.ParallelLeft ? "Left: " : "Right: ") + model.Beside : "Parallel off";
                if (besideLabel.text != beside) besideLabel.text = beside;
            }
            if (radiusLabel != null)
            {
                string text = model.Radius < 0.5f ? "sharp" : model.Radius.ToString("0") + " m";
                if (radiusLabel.text != text) radiusLabel.text = text;
            }
        }

        private static void Select(UIButton button, bool selected)
        {
            if (button == null) return;
            bool icons = button.atlas == Icons.Atlas && Icons.Atlas != null;
            string normal = icons ? (selected ? Icons.BackgroundSelected : Icons.Background) : (selected ? "ButtonMenuPressed" : "ButtonMenu");
            if (button.normalBgSprite != normal) button.normalBgSprite = button.focusedBgSprite = normal;
        }

        public void Destroy()
        {
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            if (profileTexture != null) UnityEngine.Object.Destroy(profileTexture);
            panel = null;
            curveButton = straightButton = snapButton = deckButton = underButton = null;
            parallelButton = sideButton = besideButton = null;
            gapLabel = besideLabel = null;
            upgradeButton = wallsButton = null;
            toolsLabel = null;
            radiusLabel = null;
            lengthField = turnField = null;
            profileSprite = null;
            profileTexture = null;
            drawnProfile = null;
            profilePixels = null;
            profileBase = null;
            profileReadout = null;
            cursorAlong = -1f;
            cursorPinned = false;
            draggedNode = -1;
            pressed = selecting = false;
            selectFrom = selectTo = -1f;
            elevatedButton = tunnelButton = autoButton = resetButton = null;
            shapeHint = null;
            dynamic.Clear();
            texts.Clear();
            structure = null;
        }

        public void Hide()
        {
            if (panel != null) panel.isVisible = false;
        }

        // Shows the model. Rows are only rebuilt when the shape changes (a
        // crossing added, a choice made); otherwise just the texts change.
        public void Show(PanelModel model)
        {
            if (panel == null) return;
            panel.isVisible = true;
            current = model;
            SetEnabled(buildButton, model.CanBuild);
            SetEnabled(undoButton, NetworkBuilder.UndoCount > 0);
            UpdateDrawBar(model);
            string shape = Shape(model);
            if (shape != structure)
            {
                structure = shape;
                Rebuild(model);
            }
            else
            {
                foreach (var pair in texts)
                {
                    string text = pair.Value(model);
                    if (pair.Key.text != text) pair.Key.text = text;
                }
            }
            DrawProfile(model.Profile);
            UpdateShapeButtons();
        }

        private string Shape(PanelModel model)
        {
            var shape = new System.Text.StringBuilder();
            shape.Append(model.HasPoints).Append(model.CanBuild).Append(model.Profile != null).Append('|').Append(page).Append('|');
            shape.Append(Lines(model.ProfileText, Width - 2f * Pad, 0.75f)).Append(Lines(model.Height, Width - 2f * Pad, 0.75f))
                .Append(Lines(model.Mode, Width - 2f * Pad, 0.75f));
            // A text that wraps to more or fewer lines moves everything under it.
            float wide = Width - 2f * Pad;
            shape.Append(Lines(model.Road, wide, 0.85f)).Append(Lines(model.Summary, wide, 0.85f)).Append(Lines(model.Details, wide, 0.75f));
            foreach (var item in model.Items) shape.Append(Lines(item.Title, wide, 0.8f)).Append(Lines(item.Detail, wide, 0.75f));
            foreach (var problem in model.Problems) shape.Append(Lines("• " + problem, wide, 0.75f));
            foreach (var warning in model.Warnings) shape.Append(Lines("• " + warning, wide, 0.75f));
            foreach (var crossing in model.Items)
            {
                foreach (var type in crossing.Allowed) shape.Append((int)type);
                shape.Append(':').Append((int)crossing.Selected).Append(crossing.HeightButtons).Append(';');
            }
            shape.Append('|').Append(model.Items.Count).Append('|').Append(model.Problems.Count).Append('|').Append(model.Warnings.Count);
            return shape.ToString();
        }

        private void Rebuild(PanelModel model)
        {
            foreach (var component in dynamic) if (component != null) UnityEngine.Object.Destroy(component.gameObject);
            dynamic.Clear();
            texts.Clear();
            parent = panel;
            float y = ContentTop;

            y = Header("ROAD", y);
            y = Text(m => m.Road, TextColor, 0.85f, y);
            y = Text(m => m.Height, DimColor, 0.75f, y);
            y = Text(m => m.Mode, WarningColor, 0.75f, y);

            y = Header("CURVE", y);
            y = Text(m => m.Summary, TextColor, 0.85f, y);
            y = Text(m => m.Details, DimColor, 0.75f, y);

            profileSprite = null;
            profileReadout = null;
            drawnProfile = null;
            profilePixels = null;
            profileBase = null;
            elevatedButton = tunnelButton = autoButton = resetButton = null;
            shapeHint = null;
            if (model.Profile != null)
            {
                y = Header("SIDE VIEW", y);
                profileSprite = (UITextureSprite)panel.AddUIComponent(typeof(UITextureSprite));
                profileSprite.size = new Vector2(ProfileWidth, ProfileHeight);
                profileSprite.relativePosition = new Vector3(Pad, y);
                profileSprite.tooltip = "Drag a dot up or down to set the road's height there (right-click it to let go). " +
                                        "Drag along the view to select a stretch for the buttons below. Click to keep the mark on the map";
                profileSprite.eventMouseMove += (component, param) =>
                {
                    if (draggedNode < 0 && !selecting) SetCursor(MouseAlong(), false);
                };
                profileSprite.eventMouseLeave += (component, param) => { if (!cursorPinned && draggedNode < 0) SetCursor(-1f, false); };
                profileSprite.eventMouseDown += (component, param) => PressProfile(param.buttons);
                dynamic.Add(profileSprite);
                y += ProfileHeight + 3f;
                float x = Pad;
                foreach (var style in new[] { ProfileStyle.Ground, ProfileStyle.Raised, ProfileStyle.Lowered, ProfileStyle.Steep })
                    x = Legend(StyleName(style), StyleColor(style), x, y);
                Legend("pillar", PillarLine, x, y);
                y += 16f;
                // The selected stretch: up on pillars, down in a tunnel, or left
                // to the planner; and everything set here cleared.
                elevatedButton = ShapeButton(Icons.Elevated, "E", "Elevated: on pillars, " + RoadPlanner.ForcedHeight.ToString("F0") +
                    " m up (ramping up to it). The selected stretch, or the whole road if none is selected", Pad, y, () => Mark(Forced.Elevated));
                tunnelButton = ShapeButton(Icons.Tunnel, "T", "Tunnel: underground, " + RoadPlanner.ForcedDepth.ToString("F0") +
                    " m down (ramping down to it). The selected stretch, or the whole road if none is selected", Pad + ShapeButtonSize + 4f, y,
                    () => Mark(Forced.Tunnel));
                autoButton = ShapeButton(Icons.Auto, "A", "Auto: the planner decides the heights again (and lets go of the ones held). " +
                    "The selected stretch, or the whole road if none is selected", Pad + 2f * (ShapeButtonSize + 4f), y, () => Mark(Forced.None));
                resetButton = ShapeButton(Icons.Reset, "R", "Clear every height and stretch set in the side view",
                    Width - Pad - ShapeButtonSize, y, () => { if (ResetShape != null) ResetShape(); });
                shapeHint = (UILabel)panel.AddUIComponent(typeof(UILabel));
                shapeHint.autoSize = false;
                shapeHint.size = new Vector2(Width - 2f * Pad - 4f * (ShapeButtonSize + 4f) - 4f, ShapeButtonSize);
                shapeHint.relativePosition = new Vector3(Pad + 3f * (ShapeButtonSize + 4f) + 2f, y);
                shapeHint.textScale = 0.7f;
                shapeHint.wordWrap = true;
                shapeHint.verticalAlignment = UIVerticalAlignment.Middle;
                shapeHint.textColor = DimColor;
                shapeHint.isInteractive = false;
                dynamic.Add(shapeHint);
                y += ShapeButtonSize + 4f;
                y = Text(m => m.ProfileText, DimColor, 0.75f, y);
                profileReadout = (UILabel)panel.AddUIComponent(typeof(UILabel));
                profileReadout.autoSize = false;
                profileReadout.size = new Vector2(Width - 2f * Pad, 16f);
                profileReadout.textScale = 0.75f;
                profileReadout.textColor = TextColor;
                profileReadout.relativePosition = new Vector3(Pad, y);
                profileReadout.isInteractive = false;
                profileReadout.text = "";
                dynamic.Add(profileReadout);
                y += 18f;
            }

            if (model.Items.Count > 0)
            {
                y = Header("ALONG THE ROAD", y);
                // A page of PageSize at a time, with arrows to go through them.
                int pages = (model.Items.Count + PageSize - 1) / PageSize;
                page = Mathf.Clamp(page, 0, pages - 1);
                int first = page * PageSize, last = Mathf.Min(first + PageSize, model.Items.Count);
                for (int i = first; i < last; i++)
                {
                    int index = i;
                    y = Text(m => index < m.Items.Count ? m.Items[index].Title : "", TextColor, 0.8f, y);
                    y = Text(m => index < m.Items.Count ? m.Items[index].Detail : "", DimColor, 0.75f, y - 2f);
                    float x = Pad;
                    var item = model.Items[i];
                    foreach (var type in item.Allowed)
                    {
                        var t = type;
                        IconButton(IconFor(t), Letter(t), Describe(t), new Vector2(x, y), item.Selected == t,
                            () => { var c = Row(index); if (c != null && c.Pick != null) c.Pick(t); });
                        x += IconSize + 3f;
                    }
                    if (item.HeightButtons)
                    {
                        if (item.Allowed.Count > 0) x += 8f;
                        string step = item.Step.ToString("0.#");
                        IconButton(Icons.Plus, "+", "Higher (+" + step + " m)", new Vector2(x, y), false,
                            () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(c.Step); });
                        IconButton(Icons.Minus, "-", "Lower (-" + step + " m)", new Vector2(x + IconSize + 3f, y), false,
                            () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(-c.Step); });
                    }
                    if (item.Allowed.Count > 0 || item.HeightButtons) y += IconSize;
                    y += RowGap + 6f;
                }
                if (pages > 1)
                {
                    IconButton(Icons.PageBack, "<", "Previous " + PageSize, new Vector2(Pad, y), false, () => TurnPage(-1));
                    IconButton(Icons.PageNext, ">", "Next " + PageSize, new Vector2(Width - Pad - IconSize, y), false, () => TurnPage(1));
                    int shownFirst = first + 1, shownLast = last, total = model.Items.Count;
                    var label = Label(m => shownFirst + "-" + shownLast + " of " + total, DimColor, 0.8f,
                        new Vector2(Pad + IconSize + 8f, y + 8f), Width - 2f * Pad - 2f * IconSize - 16f);
                    label.textAlignment = UIHorizontalAlignment.Center;
                    y += IconSize + RowGap;
                }
            }

            if (model.Problems.Count > 0)
            {
                y = Header("CAN'T BUILD YET", y);
                for (int i = 0; i < model.Problems.Count; i++)
                {
                    int index = i;
                    y = Text(m => index < m.Problems.Count ? "• " + m.Problems[index] : "", ProblemColor, 0.75f, y);
                }
            }
            if (model.Warnings.Count > 0)
            {
                y = Header("NOTES", y);
                for (int i = 0; i < model.Warnings.Count; i++)
                {
                    int index = i;
                    y = Text(m => index < m.Warnings.Count ? "• " + m.Warnings[index] : "", WarningColor, 0.75f, y);
                }
            }

            panel.size = new Vector2(Width, y + Pad);
        }

        // Heights are worked out here rather than read back from the label (the
        // game lays text out later): roughly how many lines the text wraps to.
        private const float LineHeight = 20f;
        private const float CharWidth = 8.5f;

        private static int Lines(string text, float width, float scale)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int perLine = Mathf.Max(8, Mathf.FloorToInt(width / (CharWidth * scale)));
            int lines = 0;
            foreach (var paragraph in text.Split('\n')) lines += Mathf.Max(1, Mathf.CeilToInt(paragraph.Length / (float)perLine));
            return lines;
        }

        private void TurnPage(int step)
        {
            page += step;
            structure = null;          // rebuilt with the new page
            if (current != null) Show(current);
        }

        private PanelCrossing Row(int index) => current != null && index < current.Items.Count ? current.Items[index] : null;

        private float Header(string text, float y)
        {
            var label = (UILabel)(parent ?? panel).AddUIComponent(typeof(UILabel));
            label.text = text;
            label.textScale = 0.75f;
            label.textColor = HeaderColor;
            label.relativePosition = new Vector3(Pad, y + 6f);
            label.isInteractive = false;
            dynamic.Add(label);
            return y + 6f + 16f;
        }

        private float Text(Func<PanelModel, string> text, Color32 color, float scale, float y)
        {
            var label = Label(text, color, scale, new Vector2(Pad, y), Width - 2f * Pad);
            return y + Mathf.Max(label.size.y, 4f) + RowGap;
        }

        private UILabel Label(Func<PanelModel, string> text, Color32 color, float scale, Vector2 at, float width)
        {
            var label = (UILabel)(parent ?? panel).AddUIComponent(typeof(UILabel));
            string value = current != null ? text(current) : "";
            label.autoSize = false;
            label.wordWrap = true;
            label.textScale = scale;
            label.textColor = color;
            label.size = new Vector2(width, Lines(value, width, scale) * LineHeight * scale);
            label.relativePosition = new Vector3(at.x, at.y);
            label.isInteractive = false;
            label.text = value;
            dynamic.Add(label);
            texts.Add(new KeyValuePair<UILabel, Func<PanelModel, string>>(label, text));
            return label;
        }

        private void IconButton(string icon, string letter, string tooltip, Vector2 at, bool selected, Action onClick)
        {
            var button = (UIButton)(parent ?? panel).AddUIComponent(typeof(UIButton));
            button.size = new Vector2(IconSize, IconSize);
            button.relativePosition = new Vector3(at.x, at.y);
            button.tooltip = tooltip;
            if (Icons.Has(icon, Icons.Background, Icons.BackgroundHover, Icons.BackgroundSelected))
            {
                button.atlas = Icons.Atlas;
                button.normalFgSprite = icon;
                button.hoveredFgSprite = icon;
                button.pressedFgSprite = icon;
                button.normalBgSprite = selected ? Icons.BackgroundSelected : Icons.Background;
                button.hoveredBgSprite = selected ? Icons.BackgroundSelected : Icons.BackgroundHover;
                button.pressedBgSprite = Icons.BackgroundSelected;
            }
            else
            {
                button.normalBgSprite = selected ? "ButtonMenuPressed" : "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.text = letter;
                button.textScale = 0.8f;
            }
            button.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0) onClick(); };
            dynamic.Add(button);
        }

        private const float ActionSize = 30f;

        private UIButton ActionButton(string icon, string iconOff, string background, string text, float x, string tooltip, Action onClick)
        {
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
            button.size = new Vector2(ActionSize, ActionSize);
            button.relativePosition = new Vector3(x, 5f);
            button.tooltip = tooltip;
            if (Icons.Has(icon, iconOff, background, Icons.BackgroundHover, Icons.BackgroundOff))
            {
                button.atlas = Icons.Atlas;
                button.normalFgSprite = button.hoveredFgSprite = button.pressedFgSprite = button.focusedFgSprite = icon;
                button.disabledFgSprite = iconOff;
                button.normalBgSprite = button.focusedBgSprite = background;
                button.hoveredBgSprite = Icons.BackgroundHover;
                button.pressedBgSprite = Icons.BackgroundSelected;
                button.disabledBgSprite = Icons.BackgroundOff;
            }
            else
            {
                button.text = text;
                button.textScale = 0.7f;
                button.normalBgSprite = button.focusedBgSprite = "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed";
                button.disabledBgSprite = "ButtonMenuDisabled";
            }
            button.eventClick += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Left) != 0 && button.isEnabled) onClick();
            };
            return button;
        }

        private static void SetEnabled(UIButton button, bool enabled)
        {
            if (button != null && button.isEnabled != enabled) button.isEnabled = enabled;
        }

        public static string StyleName(ProfileStyle style)
        {
            switch (style)
            {
                case ProfileStyle.Raised: return "raised";
                case ProfileStyle.Lowered: return "lowered";
                case ProfileStyle.Steep: return "steep";
                case ProfileStyle.TooSharp: return "too sharp";
                default: return "ground";
            }
        }

        // The same colours as the curve on the map.
        public static Color32 StyleColor(ProfileStyle style)
        {
            switch (style)
            {
                case ProfileStyle.Raised: return new Color32(70, 160, 255, 255);
                case ProfileStyle.Lowered: return new Color32(255, 160, 40, 255);
                case ProfileStyle.Steep: return new Color32(190, 80, 255, 255);
                case ProfileStyle.TooSharp: return new Color32(255, 60, 40, 255);
                default: return new Color32(70, 230, 100, 255);
            }
        }

        // One coloured word of the legend under the profile; returns the next x.
        private float Legend(string text, Color32 color, float x, float y)
        {
            var label = (UILabel)panel.AddUIComponent(typeof(UILabel));
            label.text = text;
            label.textScale = 0.7f;
            label.textColor = color;
            label.autoSize = true;
            label.relativePosition = new Vector3(x, y);
            label.isInteractive = false;
            dynamic.Add(label);
            return x + text.Length * 6.5f + 14f;
        }

        // Paints the side view: ground (and water) filled from the bottom,
        // pillars as thin lines up to the deck, the road as a thick line in its
        // colour, and a white mark at each road or track we cross, at its height.
        // Only when the profile changed. Row 0 of the texture is the bottom.
        private void DrawProfile(PanelProfile profile)
        {
            if (profileSprite == null || profile == null || profile == drawnProfile || profile.Road == null || profile.Road.Length < 2) return;
            drawnProfile = profile;
            const int w = ProfileWidth, h = ProfileHeight;
            if (profileTexture == null)
                profileTexture = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };

            // Vertical scale: everything shown, at least MinProfileRange metres.
            // While a height is dragged it stays as it was, so the dot stays under the mouse.
            float low = float.MaxValue, high = float.MinValue;
            for (int i = 0; i < profile.Road.Length; i++)
            {
                low = Mathf.Min(low, Mathf.Min(profile.Road[i], profile.Ground[i]));
                high = Mathf.Max(high, Mathf.Max(profile.Road[i], Mathf.Max(profile.Ground[i], profile.Water[i])));
            }
            foreach (var crossing in profile.Crossings)
            {
                low = Mathf.Min(low, crossing.Value);
                high = Mathf.Max(high, crossing.Value);
            }
            if (high - low < MinProfileRange)
            {
                float middle = (high + low) * 0.5f;
                low = middle - MinProfileRange * 0.5f;
                high = middle + MinProfileRange * 0.5f;
            }
            float margin = (high - low) * 0.08f;
            low -= margin;
            high += margin;
            if (draggedNode >= 0)
            {
                low = dragLow;
                high = dragHigh;
            }
            drawLow = low;
            drawHigh = high;

            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = ProfileBack;
            var roadRows = new int[w];
            var groundRows = new int[w];
            int samples = profile.Road.Length;
            for (int x = 0; x < w; x++)
            {
                float f = x * (samples - 1) / (float)(w - 1);
                int i0 = Mathf.Clamp(Mathf.FloorToInt(f), 0, samples - 1), i1 = Mathf.Min(i0 + 1, samples - 1);
                float t = f - i0;
                float ground = Mathf.Lerp(profile.Ground[i0], profile.Ground[i1], t);
                float water = Mathf.Lerp(profile.Water[i0], profile.Water[i1], t);
                groundRows[x] = Row(ground, low, high, h);
                roadRows[x] = Row(Mathf.Lerp(profile.Road[i0], profile.Road[i1], t), low, high, h);
                for (int y = 0; y <= groundRows[x]; y++) pixels[y * w + x] = EarthColor;
                if (water > ground + 0.3f)
                    for (int y = groundRows[x] + 1; y <= Row(water, low, high, h); y++) pixels[y * w + x] = WaterColor;
            }
            // Stretches marked Elevated or Tunnel: a band along the top in their colour.
            foreach (var stretch in profile.Stretches)
            {
                var color = StyleColor(stretch.Kind == Forced.Elevated ? ProfileStyle.Raised : ProfileStyle.Lowered);
                for (int x = Column(profile, stretch.From); x <= Column(profile, stretch.To); x++)
                    for (int y = h - 4; y < h; y++) pixels[y * w + x] = color;
            }
            foreach (float at in profile.Pillars)
            {
                int x = Column(profile, at);
                for (int y = groundRows[x]; y < roadRows[x]; y++) pixels[y * w + x] = PillarLine;
            }
            for (int x = 0; x < w; x++)
            {
                int i = Mathf.Clamp(Mathf.RoundToInt(x * (samples - 1) / (float)(w - 1)), 0, samples - 1);
                var color = StyleColor(profile.Style[i]);
                int previous = x > 0 ? roadRows[x - 1] : roadRows[x];
                int from = Mathf.Min(previous, roadRows[x]) - 1, to = Mathf.Max(previous, roadRows[x]) + 1;
                for (int y = Mathf.Max(from, 0); y <= Mathf.Min(to, h - 1); y++) pixels[y * w + x] = color;
            }
            foreach (var crossing in profile.Crossings)
            {
                int cx = Column(profile, crossing.Key);
                int cy = Row(crossing.Value, low, high, h);
                for (int x = Mathf.Max(cx - 3, 0); x <= Mathf.Min(cx + 3, w - 1); x++)
                    for (int y = Mathf.Max(cy - 1, 0); y <= Mathf.Min(cy + 1, h - 1); y++) pixels[y * w + x] = CrossingMark;
            }
            // The nodes that can be dragged: a dot; a dragged (held) one: a bigger white square.
            if (profile.NodeAlong != null)
                for (int i = 0; i < profile.NodeAlong.Length; i++)
                {
                    if (profile.NodeHeld[i]) continue;
                    int cx = Column(profile, profile.NodeAlong[i]), cy = Row(profile.NodeHeight[i], low, high, h);
                    int size = profile.NodePinned[i] || i == draggedNode ? 2 : 1;
                    for (int x = Mathf.Max(cx - size, 0); x <= Mathf.Min(cx + size, w - 1); x++)
                        for (int y = Mathf.Max(cy - size, 0); y <= Mathf.Min(cy + size, h - 1); y++)
                            pixels[y * w + x] = size > 1 && x == cx && y == cy ? PinnedCore : size > 1 ? PinnedDot : NodeDot;
                }

            profileBase = pixels;
            profilePixels = new Color32[pixels.Length];
            if (cursorAlong > profile.Length) cursorAlong = profile.Length;
            if (selectFrom > profile.Length || selectTo > profile.Length) ClearSelection();
            ShowCursor();
            profileSprite.texture = profileTexture;
        }

        private static int Column(PanelProfile profile, float along)
        {
            return Mathf.Clamp(Mathf.RoundToInt(along / Mathf.Max(profile.Length, 1f) * (ProfileWidth - 1)), 0, ProfileWidth - 1);
        }

        // --- the side view's cursor, dragged heights and selected stretch -----------

        // Metres along the curve under the mouse on the side view (-1 if it isn't over it).
        private float MouseAlong()
        {
            float x, row;
            if (!MouseOnProfile(out x, out row) || x < 0f || x > ProfileWidth) return -1f;
            return AlongAt(x);
        }

        private float AlongAt(float x)
        {
            var profile = drawnProfile;
            return profile == null ? -1f : Mathf.Clamp01(x / (ProfileWidth - 1)) * profile.Length;
        }

        // The mouse in the side view's pixels: x from the left, row from the
        // bottom (either may be outside it).
        private bool MouseOnProfile(out float x, out float row)
        {
            x = row = 0f;
            if (profileSprite == null || drawnProfile == null) return false;
            var view = UIView.GetAView();
            Vector2 gui = view.ScreenPointToGUI(Input.mousePosition / view.inputScale);
            x = gui.x - profileSprite.absolutePosition.x;
            row = ProfileHeight - 1 - (gui.y - profileSprite.absolutePosition.y);
            return true;
        }

        // The draggable node whose dot is under the mouse (-1: none).
        private int NodeUnderMouse(float x, float row)
        {
            var profile = drawnProfile;
            if (profile == null || profile.NodeAlong == null) return -1;
            int best = -1;
            float bestDistance = NodeGrab * NodeGrab;
            for (int i = 0; i < profile.NodeAlong.Length; i++)
            {
                if (profile.NodeHeld[i]) continue;
                float dx = Column(profile, profile.NodeAlong[i]) - x, dy = Row(profile.NodeHeight[i], drawLow, drawHigh, ProfileHeight) - row;
                if (dx * dx + dy * dy < bestDistance)
                {
                    bestDistance = dx * dx + dy * dy;
                    best = i;
                }
            }
            return best;
        }

        // A press on the side view: on a dot, drag its height (right-click: let
        // it go); anywhere else, select a stretch (or, without moving, a click
        // that keeps the mark on the map).
        private void PressProfile(UIMouseButton buttons)
        {
            float x, row;
            if (!MouseOnProfile(out x, out row)) return;
            int node = NodeUnderMouse(x, row);
            if ((buttons & UIMouseButton.Right) != 0)
            {
                if (node >= 0 && drawnProfile.NodePinned[node] && FreeNode != null) FreeNode(node);
                else ClearSelection();
                return;
            }
            if ((buttons & UIMouseButton.Left) == 0) return;
            if (node >= 0 && GrabNode != null && GrabNode(node))
            {
                draggedNode = node;
                dragLow = drawLow;
                dragHigh = drawHigh;
                cursorPinned = false;
                SetCursor(drawnProfile.NodeAlong[node], true);
                return;
            }
            pressed = true;
            selecting = false;
            pressX = x;
        }

        // Every frame (from the tool): follows the mouse while a height is
        // dragged or a stretch selected, and ends either when the button is let
        // go (also outside the side view).
        public void Poll()
        {
            if (profileSprite == null)
            {
                draggedNode = -1;
                pressed = selecting = false;
                return;
            }
            float x, row;
            bool mouse = MouseOnProfile(out x, out row);
            bool held = Input.GetMouseButton(0);
            if (draggedNode >= 0)
            {
                if (!held)
                {
                    draggedNode = -1;
                    if (ReleaseNode != null) ReleaseNode();
                    // The scale fits the side view again.
                    var profile = drawnProfile;
                    drawnProfile = null;
                    DrawProfile(profile);
                    return;
                }
                if (mouse && DragHeight != null) DragHeight(dragLow + row / (ProfileHeight - 1) * (dragHigh - dragLow));
                return;
            }
            if (!pressed) return;
            if (mouse && !selecting && Mathf.Abs(x - pressX) > 4f)
            {
                selecting = true;
                selectFrom = AlongAt(pressX);
            }
            if (mouse && selecting)
            {
                selectTo = AlongAt(x);
                ShowCursor();
                UpdateShapeButtons();
            }
            if (held) return;
            pressed = false;
            if (selecting)
            {
                selecting = false;
                if (Mathf.Abs(selectTo - selectFrom) < MinStretch) ClearSelection();
                else Mod.Log("Side view: selected " + Mathf.Min(selectFrom, selectTo).ToString("N0") + " to " +
                             Mathf.Max(selectFrom, selectTo).ToString("N0") + " m");
                UpdateShapeButtons();
                return;
            }
            // A click: the mark on the map stays (or goes).
            cursorPinned = !cursorPinned;
            SetCursor(cursorPinned ? MouseAlong() : -1f, true);
        }

        private bool HasSelection => selectFrom >= 0f && selectTo >= 0f && Mathf.Abs(selectTo - selectFrom) >= MinStretch;

        private void ClearSelection()
        {
            selectFrom = selectTo = -1f;
            selecting = false;
            ShowCursor();
            UpdateShapeButtons();
        }

        // The buttons act on the selected stretch, or on the whole road.
        private void Mark(Forced kind)
        {
            if (MarkStretch == null || drawnProfile == null) return;
            float from = 0f, to = drawnProfile.Length;
            if (HasSelection)
            {
                from = Mathf.Min(selectFrom, selectTo);
                to = Mathf.Max(selectFrom, selectTo);
            }
            ClearSelection();
            MarkStretch(from, to, kind);
        }

        private void UpdateShapeButtons()
        {
            bool profile = drawnProfile != null;
            SetEnabled(elevatedButton, profile);
            SetEnabled(tunnelButton, profile);
            SetEnabled(autoButton, profile);
            SetEnabled(resetButton, current != null && current.Shaped);
            if (shapeHint != null)
            {
                string hint = HasSelection
                    ? "Selected " + Mathf.Min(selectFrom, selectTo).ToString("N0") + "-" + Mathf.Max(selectFrom, selectTo).ToString("N0") + " m"
                    : "Whole road (drag along the view to pick a stretch)";
                if (shapeHint.text != hint) shapeHint.text = hint;
            }
        }

        private UIButton ShapeButton(string icon, string letter, string tooltip, float x, float y, Action onClick)
        {
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
            button.size = new Vector2(ShapeButtonSize, ShapeButtonSize);
            button.relativePosition = new Vector3(x, y);
            button.tooltip = tooltip;
            if (Icons.Has(icon, Icons.Background, Icons.BackgroundHover, Icons.BackgroundSelected, Icons.BackgroundOff))
            {
                button.atlas = Icons.Atlas;
                button.normalFgSprite = button.hoveredFgSprite = button.pressedFgSprite = button.focusedFgSprite = button.disabledFgSprite = icon;
                button.normalBgSprite = button.focusedBgSprite = Icons.Background;
                button.hoveredBgSprite = Icons.BackgroundHover;
                button.pressedBgSprite = Icons.BackgroundSelected;
                button.disabledBgSprite = Icons.BackgroundOff;
            }
            else
            {
                button.text = letter;
                button.textScale = 0.7f;
                button.normalBgSprite = button.focusedBgSprite = "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed";
                button.disabledBgSprite = "ButtonMenuDisabled";
            }
            button.eventClick += (component, param) =>
            {
                if ((param.buttons & UIMouseButton.Left) != 0 && button.isEnabled) onClick();
            };
            dynamic.Add(button);
            return button;
        }

        private void SetCursor(float along, bool force)
        {
            if (cursorPinned && !force) return;
            if (Mathf.Abs(along - cursorAlong) < 0.01f && !force) return;
            cursorAlong = along;
            ShowCursor();
        }

        // Where the side view's cursor is on the map (for the curve tool to mark it).
        public bool TryGetCursor(out Vector3 position)
        {
            position = Vector3.zero;
            var profile = drawnProfile;
            if (panel == null || !panel.isVisible || profile == null || profile.Positions == null || cursorAlong < 0f) return false;
            position = profile.Positions[Sample(profile, cursorAlong)];
            return true;
        }

        private static int Sample(PanelProfile profile, float along)
        {
            int last = profile.Road.Length - 1;
            return Mathf.Clamp(Mathf.RoundToInt(along / Mathf.Max(profile.Length, 1f) * last), 0, last);
        }

        // The side view with the selected stretch lightened and a white line
        // where the cursor is, and the readout under it.
        private void ShowCursor()
        {
            var profile = drawnProfile;
            if (profileTexture == null || profileBase == null || profilePixels == null || profile == null) return;
            const int w = ProfileWidth, h = ProfileHeight;
            var pixels = profilePixels;
            Array.Copy(profileBase, pixels, pixels.Length);
            if (selectFrom >= 0f && selectTo >= 0f)
            {
                int from = Column(profile, Mathf.Min(selectFrom, selectTo)), to = Column(profile, Mathf.Max(selectFrom, selectTo));
                for (int x = from; x <= to; x++)
                    for (int y = 0; y < h; y++)
                    {
                        var c = pixels[y * w + x];
                        pixels[y * w + x] = new Color32((byte)((c.r + 255) / 2), (byte)((c.g + 255) / 2), (byte)((c.b + 255) / 2), 255);
                    }
            }
            if (cursorAlong >= 0f)
            {
                int x = Column(profile, cursorAlong);
                for (int y = 0; y < h; y++) pixels[y * w + x] = CrossingMark;
            }
            profileTexture.SetPixels32(pixels);
            profileTexture.Apply();
            if (profileSprite != null) profileSprite.Invalidate();
            if (profileReadout != null) profileReadout.text = Readout(profile, cursorAlong);
        }

        private string Readout(PanelProfile profile, float along)
        {
            if (selectFrom >= 0f && selectTo >= 0f)
            {
                float from = Mathf.Min(selectFrom, selectTo), to = Mathf.Max(selectFrom, selectTo);
                return "Selected " + from.ToString("N0") + " to " + to.ToString("N0") + " m: Elevated, Tunnel or Auto below";
            }
            if (along < 0f) return "Point at the side view to see the height there; drag a dot to set it";
            int i = Sample(profile, along);
            float above = profile.Road[i] - Mathf.Max(profile.Ground[i], profile.Water[i]);
            string height = above >= 1f ? above.ToString("F0") + " m above the " + (profile.Water[i] > profile.Ground[i] + 0.3f ? "water" : "ground")
                : above <= -1f ? (-above).ToString("F0") + " m below the ground" : "on the ground";
            return along.ToString("F0") + " m along: " + height + ", " + StyleName(profile.Style[i]) +
                   (draggedNode >= 0 ? "  (dragging)" : cursorPinned ? "  (pinned)" : "");
        }

        private static int Row(float height, float low, float high, int rows)
        {
            return Mathf.Clamp(Mathf.RoundToInt((height - low) / Mathf.Max(high - low, 0.01f) * (rows - 1)), 0, rows - 1);
        }

        private void SavePosition()
        {
            if (panel == null) return;
            try
            {
                PlayerPrefs.SetFloat(PositionKey + ".x", panel.relativePosition.x);
                PlayerPrefs.SetFloat(PositionKey + ".y", panel.relativePosition.y);
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Mod.LogError("Saving the panel position failed", e);
            }
        }

        public static string IconFor(CrossingType type)
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

        public static string Letter(CrossingType type)
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

        private static string Describe(CrossingType type)
        {
            switch (type)
            {
                case CrossingType.Bridge: return "Bridge: we go over, the other road stays";
                case CrossingType.Overpass: return "Overpass: the other road is lowered, we stay at ground level";
                case CrossingType.Underpass: return "Underpass: the other road gets a short bridge, we dip under";
                case CrossingType.Tunnel: return "Tunnel: we go under in a tunnel";
                default: return "Junction (level crossing for road x rail)";
            }
        }
    }
}
