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
        public readonly List<string> Fixes = new List<string>();      // buttons under the problems that clear one
        public readonly List<string> Warnings = new List<string>();
        public bool CanBuild;
        public int Cost;                          // what building it costs (the game's money units, a hundredth of the shown)
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
        public bool BesideOther;                  // another road or track (or one being picked)
        public bool ParallelReverse;              // it runs the other way
        public bool ParallelCentred;              // the line drawn is the middle of the pair
        public bool Upgrade;                      // changing an existing road instead of drawing one
        public bool Ramp;                         // drawing a slip ramp between two roads
        public string MedianRamp;                 // the median ramp road being drawn (null: none)
        public int MedianChoice;                  // which of the three (1..3; 0: none)
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
        // A +/- click's step: 1 m with Shift held, like Page Up / Page Down.
        private static float ShiftStep(float step)
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            return shift ? 1f : step;
        }

        private const float Width = 360f;
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
        public Action<int, float> NudgeNode;   // the mouse wheel over a dot: its height up or down by this
        public Action ReleaseNode;
        public Action<int> FreeNode;
        public Action<float, float, Forced> MarkStretch;
        public Action ResetShape;
        public Action<float, float> StraightGrade;
        private int draggedNode = -1;
        private float drawLow, drawHigh;          // the heights at the bottom and top of the drawn side view
        private float dragLow, dragHigh;          // those when the node was grabbed
        private bool pressed;                     // left button down on the side view, not on a node
        private float pressX;
        private bool selecting;                   // that press moved: a stretch is being selected
        private float selectFrom = -1f, selectTo = -1f;   // the selected stretch, metres along (-1: none)
        private Color32[] profileBase;            // the drawn side view without the selection and cursor
        // The two pictures' pixels, kept (not made anew each time the side view changes).
        private readonly Color32[] baseBuffer = new Color32[ProfileWidth * ProfileHeight];
        private readonly Color32[] shownBuffer = new Color32[ProfileWidth * ProfileHeight];
        private UIButton elevatedButton, tunnelButton, autoButton, gradeButton, resetButton;
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
        public Action ToggleParallel, FlipSide, PickBeside, FlipDirection, ToggleCentred;
        public Action<int> ApplyFix;
        public Action<float> ChangeGap;
        private UIButton parallelButton, sideButton, besideButton, directionButton, centredButton;
        private UILabel gapLabel;
        // Upgrade an existing road along a stretch; draw a slip ramp.
        public Action ToggleUpgrade, ToggleRamp;
        public Action<int> PickMedianRamp;
        private UIButton upgradeButton, rampButton;
        private readonly UIButton[] medianButtons = new UIButton[6];
        private UILabel toolsLabel;
        public Action<string, string> Typed;
        public Action TypedSubmit;
        private UIButton curveButton, straightButton, snapButton, deckButton, underButton;
        private UILabel radiusLabel;
        private UITextField lengthField, turnField;
        private const float BarTop = 46f;
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
            LoadLayout();
            CreateDrawBar();

            panel.isVisible = false;
        }

        // --- the DRAW bar (made once; only its state changes) ----------------------

        // The bar is in four tabs, by what is being built: Draw (curved or
        // straight pieces, exact lengths, a parallel road), Decks (over or under
        // the roads), Ramps (a slip ramp, the median ramp roads, the median
        // cuts) and Upgrade. Only the open tab's buttons show, so modes of
        // different tabs aren't mixed by mistake: leaving the Ramps or Upgrade
        // tab turns its mode off, and opening Upgrade turns it on. The tabs use
        // the game's own sub-tab look (as its build menus' road groups); each
        // tool is a game option button with its name under it.
        private static readonly string[] TabNames = { "Draw", "Decks", "Ramps", "Upgrade" };
        private const float TabHeight = 28f;
        private const float RowsTop = BarTop + TabHeight + 8f;
        private const float BarIcon = 36f;                         // the tools' buttons (the game's option buttons are this size)
        private const float SmallIcon = 30f;                       // -/+ beside a value
        private const float TileStep = 54f;                        // a tool and the gap to the next
        private const float TileHeight = BarIcon + 17f;            // with its name under it
        private int tab;
        private readonly UIButton[] tabButtons = new UIButton[4];
        private class BarItem
        {
            public UIComponent Component;
            public int Tab;
            public bool ParallelOnly;      // shown only while Parallel is on
            public bool ExactOnly;         // shown only while Exact is open
        }
        private readonly List<BarItem> barItems = new List<BarItem>();
        private readonly Dictionary<UIButton, UILabel> captions = new Dictionary<UIButton, UILabel>();
        // The Draw tab's rows, from RowsTop: tools; length and turn; Parallel and
        // its gap; the parallel road's side, road, way and centring (while on).
        private static readonly float[] DrawRows = { 0f, TileHeight + 6f, TileHeight + 40f, 2f * TileHeight + 44f };
        private bool parallelShown;                                // the Draw tab has its fourth row
        private UILabel tabHint;                                   // a line under the open tab's tools (not on Draw)
        private static readonly string[] TabHints =
        {
            null,
            "Click a road to put a point over (or under) its middle.",
            "Hover a button to see how it is drawn.",
            "Click where the upgrade starts, then where it ends.",
        };

        private void Put(UIComponent component, int tab, int row, bool parallelOnly = false, bool exactOnly = false)
        {
            if (component == null) return;
            barItems.Add(new BarItem { Component = component, Tab = tab, ParallelOnly = parallelOnly, ExactOnly = exactOnly });
        }

        // --- remembered between sessions: the open tab, Exact, folded sections ----

        private UIButton exactButton;
        private bool exactShown;
        private readonly HashSet<string> folded = new HashSet<string>();
        private const string TabKey = PositionKey + ".Tab", ExactKey = PositionKey + ".Exact", FoldKey = PositionKey + ".Folded";

        private void LoadLayout()
        {
            try
            {
                tab = Mathf.Clamp(PlayerPrefs.GetInt(TabKey, 0), 0, 2);   // Upgrade isn't opened by itself
                exactShown = PlayerPrefs.GetInt(ExactKey, 0) != 0;
                folded.Clear();
                foreach (var name in PlayerPrefs.GetString(FoldKey, "").Split(','))
                    if (name.Length > 0) folded.Add(name);
            }
            catch (Exception e)
            {
                Mod.LogError("Reading the panel's layout", e);
            }
        }

        private void SaveLayout()
        {
            try
            {
                PlayerPrefs.SetInt(TabKey, tab);
                PlayerPrefs.SetInt(ExactKey, exactShown ? 1 : 0);
                PlayerPrefs.SetString(FoldKey, string.Join(",", new List<string>(folded).ToArray()));
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Mod.LogError("Saving the panel's layout", e);
            }
        }

        private void ToggleExact()
        {
            exactShown = !exactShown;
            // Closed: what was typed goes, so it doesn't place the next point unseen.
            if (!exactShown)
            {
                ClearTyped();
                SendTyped();
            }
            SaveLayout();
            if (current != null) Show(current);
        }

        private void ToggleFold(string section)
        {
            if (!folded.Remove(section)) folded.Add(section);
            SaveLayout();
            structure = null;
            if (current != null) Show(current);
        }

        // A tool's button with its name under it, in a tab.
        private UIButton Tile(string icon, string letter, string caption, string tooltip, float x, float y, int tab, bool parallelOnly, Action onClick)
        {
            var button = BarButton(icon, letter, tooltip, x, y, onClick);
            Put(button, tab, 0, parallelOnly);
            if (caption != null)
            {
                var label = Caption(caption, x + BarIcon / 2f, y + BarIcon + 2f);
                Put(label, tab, 0, parallelOnly);
                captions[button] = label;
            }
            return button;
        }

        // A one-word name centred on 'centre' (wider than the button, so it never clips).
        private UILabel Caption(string text, float centre, float y)
        {
            const float width = TileStep + 12f;
            var label = (UILabel)panel.AddUIComponent(typeof(UILabel));
            label.autoSize = false;
            label.wordWrap = false;
            label.size = new Vector2(width, 15f);
            label.relativePosition = new Vector3(centre - width / 2f, y);
            label.textAlignment = UIHorizontalAlignment.Center;
            label.textScale = 0.7f;
            label.textColor = DimColor;
            label.isInteractive = false;
            label.text = text;
            return label;
        }

        // The height of the open tab's tools, and where the rows below start
        // (after the line saying what is being drawn).
        private float TabContentHeight => tab == 0 ? (parallelShown ? DrawRows[3] : DrawRows[2]) + TileHeight : TileHeight + 36f;
        private int toolsLines = 1;
        private float ContentTop => RowsTop + TabContentHeight + 4f + toolsLines * 16f + 6f;

        private void CreateDrawBar()
        {
            float tabWidth = (Width - 2f * Pad) / 4f;
            bool gameTabs = GameHas("SubBarButtonBase", "SubBarButtonBaseHovered", "SubBarButtonBasePressed", "SubBarButtonBaseFocused");
            for (int t = 0; t < 4; t++)
            {
                int picked = t;
                var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
                button.size = new Vector2(tabWidth, TabHeight);
                button.relativePosition = new Vector3(Pad + t * tabWidth, BarTop);
                button.text = TabNames[t];
                button.textScale = 0.85f;
                button.textColor = DimColor;
                button.hoveredTextColor = TextColor;
                button.focusedTextColor = DimColor;
                button.pressedTextColor = TextColor;
                if (gameTabs)
                {
                    button.atlas = GameAtlas;
                    button.normalBgSprite = button.focusedBgSprite = "SubBarButtonBase";
                    button.hoveredBgSprite = "SubBarButtonBaseHovered";
                    button.pressedBgSprite = "SubBarButtonBasePressed";
                }
                else
                {
                    button.normalBgSprite = button.focusedBgSprite = "ButtonMenu";
                    button.hoveredBgSprite = "ButtonMenuHovered";
                    button.pressedBgSprite = "ButtonMenuPressed";
                }
                button.tooltip = t == 0 ? "Curved or straight pieces, exact lengths, a parallel road"
                    : t == 1 ? "The next pieces over the roads below (a deck), or under them as a tunnel"
                    : t == 2 ? "A slip ramp, the median ramp roads and the metro and train median cuts"
                    : "Turn a stretch of road into the road picked in the game's menu";
                button.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0) PickTab(picked); };
                tabButtons[t] = button;
            }
            tabSelected = gameTabs ? "SubBarButtonBaseFocused" : "ButtonMenuPressed";
            tabNormal = gameTabs ? "SubBarButtonBase" : "ButtonMenu";

            // Draw, row 0: curved / straight, snapping, the corner radius.
            float y = RowsTop;
            curveButton = Tile(Icons.Curve, "C", "Curved", "Curved pieces: the road bends smoothly through the points (Tab)", Pad, y, 0, false,
                () => { if (SetStraight != null) SetStraight(false); });
            straightButton = Tile(Icons.Straight, "S", "Straight", "Straight pieces, with rounded corners between them (Tab)\n" +
                "Snaps to 15° turns, along or across nearby roads, and 8 m lengths; hold Shift to draw freely", Pad + TileStep, y, 0, false,
                () => { if (SetStraight != null) SetStraight(true); });
            snapButton = Tile(Icons.Snap, "Sn", "Snap", "Snapping for straight pieces (on: lit)", Pad + 2f * TileStep, y, 0, false,
                () => { if (ToggleSnap != null) ToggleSnap(); });
            float x = Pad + 3f * TileStep + 10f;
            float sy = y + (BarIcon - SmallIcon) / 2f;
            Action tighter = () => { if (ChangeRadius != null) ChangeRadius(-CurveTool.RadiusStep); };
            Action wider = () => { if (ChangeRadius != null) ChangeRadius(CurveTool.RadiusStep); };
            var radiusMinus = BarButton(Icons.Minus, "-", "Tighter corners between straight pieces (below " + CurveTool.MinCornerRadius.ToString("0") + " m: a sharp corner; or the mouse wheel)", x, sy, tighter, SmallIcon);
            radiusLabel = BarLabel("", x + SmallIcon, sy + 7f, 52f);
            radiusLabel.textAlignment = UIHorizontalAlignment.Center;
            radiusLabel.textColor = TextColor;
            radiusLabel.textScale = 0.8f;
            var radiusPlus = BarButton(Icons.Plus, "+", "Wider corners between straight pieces (or the mouse wheel)", x + SmallIcon + 52f, sy, wider, SmallIcon);
            Wheel(wider, tighter, radiusMinus, radiusLabel, radiusPlus);
            Put(radiusMinus, 0, 0);
            Put(radiusLabel, 0, 0);
            Put(radiusPlus, 0, 0);
            Put(Caption("Corner", x + SmallIcon + 26f, y + BarIcon + 2f), 0, 0);

            // Draw, row 1: an exact length and turn for the next point, behind
            // the Exact button (only needed for precise drawing).
            y = RowsTop + DrawRows[1];
            exactButton = (UIButton)panel.AddUIComponent(typeof(UIButton));
            exactButton.size = new Vector2(58f, 22f);
            exactButton.relativePosition = new Vector3(Pad, y);
            exactButton.text = "Exact";
            exactButton.textScale = 0.75f;
            exactButton.normalBgSprite = exactButton.focusedBgSprite = "ButtonMenu";
            exactButton.hoveredBgSprite = "ButtonMenuHovered";
            exactButton.pressedBgSprite = "ButtonMenuPressed";
            exactButton.tooltip = "An exact length and turn for the next point (typed; Enter places it)";
            exactButton.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0) ToggleExact(); };
            Put(exactButton, 0, 1);
            Put(BarLabel("Length", Pad + 66f, y + 4f, 46f), 0, 1, false, true);
            lengthField = BarField(Pad + 112f, y, 56f, "Length of the next piece in metres; Enter places the point");
            Put(lengthField, 0, 1, false, true);
            Put(BarLabel("m", Pad + 171f, y + 4f, 16f), 0, 1, false, true);
            Put(BarLabel("Turn", Pad + 192f, y + 4f, 36f), 0, 1, false, true);
            turnField = BarField(Pad + 228f, y, 50f,
                "Turn from the last piece in degrees: + right, - left (the first piece: bearing from north); Enter places the point");
            Put(turnField, 0, 1, false, true);
            Put(BarLabel("°", Pad + 281f, y + 4f, 16f), 0, 1, false, true);

            // Draw, row 2: Parallel, a second road beside the curve (a one-way
            // pair, or a road and a track), and its gap while it is on.
            y = RowsTop + DrawRows[2];
            parallelButton = Tile(Icons.Parallel, "P", "Parallel", "Parallel: build a second road beside the curve, at the gap set here.\n" +
                "The same road runs the other way (a one-way pair); or pick a road or track for it with the Road button.\n" +
                "Each is planned on its own (crossings, bridges, tunnels); one Build and one Ctrl+Z for both", Pad, y, 0, false,
                () => { if (ToggleParallel != null) ToggleParallel(); });
            float gx = Pad + TileStep + 10f;
            sy = y + (BarIcon - SmallIcon) / 2f;
            Action narrower = () => { if (ChangeGap != null) ChangeGap(-CurveTool.GapStep); };
            Action widerGap = () => { if (ChangeGap != null) ChangeGap(CurveTool.GapStep); };
            var gapMinus = BarButton(Icons.Minus, "-", "Narrower gap between the two roads' edges (or the mouse wheel)", gx, sy, narrower, SmallIcon);
            gapLabel = BarLabel("", gx + SmallIcon, sy + 7f, 52f);
            gapLabel.textAlignment = UIHorizontalAlignment.Center;
            gapLabel.textColor = TextColor;
            gapLabel.textScale = 0.8f;
            var gapPlus = BarButton(Icons.Plus, "+", "Wider gap between the two roads' edges (up to " + CurveTool.MaxGap.ToString("0") + " m; or the mouse wheel)",
                gx + SmallIcon + 52f, sy, widerGap, SmallIcon);
            Wheel(widerGap, narrower, gapMinus, gapLabel, gapPlus);
            Put(gapMinus, 0, 2, true);
            Put(gapLabel, 0, 2, true);
            Put(gapPlus, 0, 2, true);
            Put(Caption("Gap", gx + SmallIcon + 26f, y + BarIcon + 2f), 0, 2, true);

            // Draw, row 3 (Parallel on): its side, road, way and centring.
            y = RowsTop + DrawRows[3];
            sideButton = Tile(Icons.Side, "<>", "Side", "Put the parallel road on the other side of the curve", Pad, y, 0, true,
                () => { if (FlipSide != null) FlipSide(); });
            besideButton = Tile(Icons.Beside, "2", "Road", "Another road or track beside the curve: press this, then pick it in the game's menu " +
                "(the curve keeps its own road).\nPress again to go back to the curve's own road", Pad + TileStep, y, 0, true,
                () => { if (PickBeside != null) PickBeside(); });
            directionButton = Tile(Icons.Direction, "Dir", "Way", "Which way the parallel line runs: the other way (a one-way pair, lit) " +
                "or the same way (a double track, a road and its tram or rail line)", Pad + 2f * TileStep, y, 0, true,
                () => { if (FlipDirection != null) FlipDirection(); });
            centredButton = Tile(Icons.Centred, "|", "Centred", "Centred pair: the line drawn is the middle between the two roads (lit), " +
                "each road half the way out to its side; else the line drawn is the curve's own and the parallel road goes beside it",
                Pad + 3f * TileStep, y, 0, true, () => { if (ToggleCentred != null) ToggleCentred(); });

            // Decks.
            y = RowsTop;
            deckButton = Tile(Icons.Deck, "D", "Over", "Deck: the next pieces run over the roads below, a level up (double or triple " +
                "decker)\nClick on a road to put a deck point over its middle; between two such points the deck follows the road.\n" +
                "Pillars stand on the road's median if it has one, else a frame spans the road", Pad, y, 1, false,
                () => { if (ToggleDeck != null) ToggleDeck(); });
            underButton = Tile(Icons.Under, "U", "Under", "Under: the next pieces run one level under the roads, as a tunnel (metro under a " +
                "street)\nClick on a road to put a point under its middle; between two such points the tunnel follows the road.\n" +
                "One level only: not under a road that is already underground", Pad + TileStep, y, 1, false,
                () => { if (ToggleUnder != null) ToggleUnder(); });

            // Ramps: a slip ramp, the three median ramp roads, the two median cuts.
            rampButton = Tile(Icons.Ramp, "R", "Slip", "Slip ramp: start on the road it leaves, end on the road it joins (any levels).\n" +
                "It leaves and joins them along them, the way their traffic goes; the heights between are worked out\n" +
                "at the usual grades (up to 4.5 % where there is no room). Draw it the way its traffic goes", Pad, y, 2, false,
                () => { if (ToggleRamp != null) ToggleRamp(); });
            const string how = "\nUp: start on the ground (on the road, along it, or anywhere); the ramp rises out of the road's middle at 8 % " +
                               "to the first deck level (or the deck you end on) and stays level as far as you draw.\nDown: start on a deck's end; " +
                               "it comes down at 8 % into the road's middle. The road's outer lanes run beside it on the ground to the next junction, " +
                               "which the ramp flies over";
            string[] icons = { null, Icons.Median62, Icons.Median64, Icons.Median42 };
            string[] titles = { null, "Six-lane road + 2-lane median ramp", "Six-lane road + 4-lane median ramp", "Four-lane road + 2-lane median ramp" };
            string[] letters = { null, "6+2", "6+4", "4+2" };
            for (int choice = 1; choice <= 3; choice++)
            {
                int picked = choice;
                medianButtons[choice] = Tile(icons[choice], letters[choice], letters[choice], titles[choice] + how,
                    Pad + choice * TileStep, y, 2, false, () => { if (PickMedianRamp != null) PickMedianRamp(picked); });
            }
            const string cutHow = "\nDown: start on the end of a raised track; it comes down at its grade (metro 5 %, train 3.5 %) into an open cut " +
                                  "in the road's middle and on into a tunnel.\nUp: start on the end of a tunnel; it comes up through the cut to the deck level " +
                                  "(or the raised track you end on).\nDrawn along a six-lane or four-lane road, that road gets an open gap for it where the " +
                                  "track is low (its inner lanes end where the gap begins); on open ground, the six-lane road is built around it";
            string[] cutIcons = { Icons.CutMetro, Icons.CutTrain };
            string[] cutTitles = { "Metro median cut", "Train median cut" };
            string[] cutLetters = { "MC", "TC" };
            string[] cutCaptions = { "Metro", "Train" };
            for (int k = 0; k < 2; k++)
            {
                int picked = 4 + k;
                medianButtons[picked] = Tile(cutIcons[k], cutLetters[k], cutCaptions[k], cutTitles[k] + cutHow,
                    Pad + picked * TileStep, y, 2, false, () => { if (PickMedianRamp != null) PickMedianRamp(picked); });
            }

            // Upgrade.
            upgradeButton = Tile(Icons.Upgrade, "Up", "Upgrade", "Upgrade along a curve: click a road where it starts, then where it ends;\n" +
                "every road between becomes the road picked in the game's menu, on the same nodes (shape and junctions kept),\n" +
                "each in its own version (elevated, tunnel...). One Ctrl+Z puts the old road back", Pad, y, 3, false,
                () => { if (ToggleUpgrade != null) ToggleUpgrade(); });

            // The open tab's hint, under its tools (not on Draw).
            tabHint = BarLabel("", Pad, RowsTop + TileHeight + 4f, Width - 2f * Pad);
            tabHint.wordWrap = true;
            tabHint.autoHeight = true;
            tabHint.size = new Vector2(Width - 2f * Pad, 30f);
            tabHint.textScale = 0.75f;

            // What is being drawn (the parallel line), under the open tab's tools.
            toolsLabel = BarLabel("", Pad, RowsTop + TabContentHeight + 4f, Width - 2f * Pad);
            toolsLabel.textScale = 0.75f;
            toolsLabel.textColor = TextColor;
            toolsLabel.wordWrap = true;
            toolsLabel.autoHeight = true;
        }

        private string tabSelected = "ButtonMenuPressed", tabNormal = "ButtonMenu";

        // A tab picked: the ramp or upgrade mode of the tab left is turned off,
        // Upgrade's turned on.
        private void PickTab(int picked)
        {
            if (picked == tab) return;
            var model = current;
            if (model != null)
            {
                if (tab == 3 && model.Upgrade && ToggleUpgrade != null) ToggleUpgrade();
                if (tab == 2 && model.Ramp && ToggleRamp != null) ToggleRamp();
                if (tab == 2 && model.MedianChoice > 0 && PickMedianRamp != null) PickMedianRamp(model.MedianChoice);
                if (picked == 3 && !model.Upgrade && ToggleUpgrade != null) ToggleUpgrade();
            }
            tab = picked;
            SaveLayout();
            structure = null;   // the rows below move: laid out again
            if (current != null) Show(current);
        }

        // Only the open tab's tools (Parallel's own only while it is on); the
        // tab follows a mode turned on elsewhere (Upgrade, a ramp). The open
        // tab is drawn as the game draws a picked one, its name bright.
        private void LayOutBar(PanelModel model)
        {
            if (model.Upgrade) tab = 3;
            else if (model.Ramp || model.MedianChoice > 0) tab = 2;
            else if (tab == 3) tab = 0;
            if (parallelShown != model.Parallel)
            {
                parallelShown = model.Parallel;
                structure = null;   // the Draw tab's fourth row comes or goes
            }
            for (int t = 0; t < 4; t++)
            {
                var button = tabButtons[t];
                if (button == null) continue;
                string normal = t == tab ? tabSelected : tabNormal;
                if (button.normalBgSprite != normal) button.normalBgSprite = button.focusedBgSprite = normal;
                Color32 text = t == tab ? TextColor : DimColor;
                if (!button.textColor.Equals(text)) button.textColor = button.focusedTextColor = text;
            }
            foreach (var item in barItems)
            {
                bool show = item.Tab == tab && (!item.ParallelOnly || model.Parallel) && (!item.ExactOnly || exactShown);
                if (item.Component.isVisible != show) item.Component.isVisible = show;
            }
            if (exactButton != null)
            {
                string bg = exactShown ? "ButtonMenuPressed" : "ButtonMenu";
                if (exactButton.normalBgSprite != bg) exactButton.normalBgSprite = exactButton.focusedBgSprite = bg;
            }
            if (tabHint != null)
            {
                string hint = TabHints[tab] ?? "";
                if (tabHint.text != hint) tabHint.text = hint;
                bool show = hint.Length > 0;
                if (tabHint.isVisible != show) tabHint.isVisible = show;
            }
            if (toolsLabel != null)
            {
                var at = new Vector3(Pad, RowsTop + TabContentHeight + 4f);
                if (toolsLabel.relativePosition != at) toolsLabel.relativePosition = at;
            }
        }

        private UIButton BarButton(string icon, string letter, string tooltip, float x, float y, Action onClick, float size = BarIcon)
        {
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
            button.size = new Vector2(size, size);
            button.relativePosition = new Vector3(x, y);
            button.tooltip = tooltip;
            Dress(button, icon, letter, false);
            button.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0) onClick(); };
            return button;
        }

        // --- the buttons' look ------------------------------------------------------

        // The game's own option-button look (its road tool's Straight / Curved /
        // Upgrade buttons) when its atlas has it: the road tool's own icon where
        // it has one, else ours drawn on the game's button. Without it, our own
        // button look. Checked at run time, sprite by sprite.
        private static readonly Dictionary<string, string> GameIcons = new Dictionary<string, string>
        {
            { Icons.Curve, "RoadOptionCurved" }, { Icons.Straight, "RoadOptionStraight" }, { Icons.Upgrade, "RoadOptionUpgrade" },
        };
        private readonly Dictionary<UIButton, string> native = new Dictionary<UIButton, string>();   // the game's look: its icon (or null: ours)

        private static UITextureAtlas GameAtlas
        {
            get
            {
                var view = UIView.GetAView();
                return view != null ? view.defaultAtlas : null;
            }
        }

        private static bool GameHas(params string[] names)
        {
            try
            {
                var atlas = GameAtlas;
                if (atlas == null) return false;
                foreach (var name in names) if (atlas[name] == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool? nativeLook;
        private static bool NativeLook
        {
            get
            {
                if (nativeLook == null)
                {
                    nativeLook = GameHas("OptionBase", "OptionBaseHovered", "OptionBasePressed", "OptionBaseFocused");
                    Mod.Log("Panel buttons: " + (nativeLook.Value ? "the game's own look" : "RR's own look (the game's option buttons weren't found)"));
                }
                return nativeLook.Value;
            }
        }

        private void Dress(UIButton button, string icon, string letter, bool selected)
        {
            if (NativeLook)
            {
                button.atlas = GameAtlas;
                button.normalBgSprite = button.focusedBgSprite = selected ? "OptionBaseFocused" : "OptionBase";
                button.hoveredBgSprite = "OptionBaseHovered";
                button.pressedBgSprite = "OptionBasePressed";
                if (GameHas("OptionBaseDisabled")) button.disabledBgSprite = "OptionBaseDisabled";
                string game;
                if (icon != null && GameIcons.TryGetValue(icon, out game) && GameHas(game))
                {
                    button.normalFgSprite = button.focusedFgSprite = selected && GameHas(game + "Focused") ? game + "Focused" : game;
                    button.hoveredFgSprite = GameHas(game + "Hovered") ? game + "Hovered" : game;
                    button.pressedFgSprite = GameHas(game + "Pressed") ? game + "Pressed" : game;
                    button.disabledFgSprite = GameHas(game + "Disabled") ? game + "Disabled" : game;
                    native[button] = game;
                    return;
                }
                if (icon != null && Icons.Has(icon))
                {
                    var sprite = (UISprite)button.AddUIComponent(typeof(UISprite));
                    float inner = Mathf.Round(button.size.x * 0.82f);
                    sprite.atlas = Icons.Atlas;
                    sprite.spriteName = icon;
                    sprite.size = new Vector2(inner, inner);
                    sprite.relativePosition = new Vector3((button.size.x - inner) / 2f, (button.size.y - inner) / 2f);
                    sprite.isInteractive = false;
                    // The game's button turns white under the mouse: RR's light icon
                    // goes dark on it then, as the game's own icons do.
                    button.eventMouseEnter += (component, param) => { if (sprite != null) sprite.color = new Color32(55, 60, 70, 255); };
                    button.eventMouseLeave += (component, param) => { if (sprite != null) sprite.color = new Color32(255, 255, 255, 255); };
                }
                else
                {
                    button.text = letter;
                    button.textScale = 0.8f;
                }
                native[button] = null;
                return;
            }
            if (Icons.Has(icon, Icons.Background, Icons.BackgroundHover, Icons.BackgroundSelected, Icons.BackgroundOff))
            {
                button.atlas = Icons.Atlas;
                button.normalFgSprite = button.hoveredFgSprite = button.pressedFgSprite = button.focusedFgSprite = button.disabledFgSprite = icon;
                button.normalBgSprite = button.focusedBgSprite = selected ? Icons.BackgroundSelected : Icons.Background;
                button.hoveredBgSprite = Icons.BackgroundHover;
                button.pressedBgSprite = Icons.BackgroundSelected;
                button.disabledBgSprite = Icons.BackgroundOff;
            }
            else
            {
                button.normalBgSprite = button.focusedBgSprite = selected ? "ButtonMenuPressed" : "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed";
                button.disabledBgSprite = "ButtonMenuDisabled";
                button.text = letter;
                button.textScale = 0.8f;
            }
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
            LayOutBar(model);
            Select(curveButton, !model.Straight);
            Select(straightButton, model.Straight);
            Select(snapButton, model.Snapping);
            Select(deckButton, model.Deck);
            Select(underButton, model.Under);
            Select(parallelButton, model.Parallel);
            Select(sideButton, model.Parallel && !model.ParallelLeft);
            Select(besideButton, model.Parallel && model.BesideOther);
            Select(upgradeButton, model.Upgrade);
            Select(rampButton, model.Ramp);
            for (int choice = 1; choice < medianButtons.Length; choice++) Select(medianButtons[choice], model.MedianChoice == choice);
            if (toolsLabel != null)
            {
                // Only the parallel line (the road row below names the road, ramp or cut).
                string tools = model.Parallel && tab == 0
                    ? "Parallel on the " + (model.ParallelLeft ? "left" : "right") + (model.ParallelCentred ? ", centred" : "") + ": " + model.Beside
                    : "";
                if (toolsLabel.text != tools) toolsLabel.text = tools;
                int lines = tools.Length == 0 ? 0 : Mathf.Max(1, Lines(tools, Width - 2f * Pad, 0.75f));
                if (lines != toolsLines)
                {
                    toolsLines = lines;
                    structure = null;   // the rows below move
                }
            }
            if (gapLabel != null)
            {
                string gap = model.Gap.ToString("0") + " m";
                if (gapLabel.text != gap) gapLabel.text = gap;
            }
            Select(directionButton, model.Parallel && model.ParallelReverse);
            Select(centredButton, model.Parallel && model.ParallelCentred);
            if (radiusLabel != null)
            {
                string text = model.Radius < 0.5f ? "sharp" : model.Radius.ToString("0") + " m";
                if (radiusLabel.text != text) radiusLabel.text = text;
            }
        }

        private void Select(UIButton button, bool selected)
        {
            if (button == null) return;
            UILabel caption;
            if (captions.TryGetValue(button, out caption) && caption != null)
            {
                Color32 color = selected ? TextColor : DimColor;
                if (!caption.textColor.Equals(color)) caption.textColor = color;
            }
            string game;
            if (native.TryGetValue(button, out game))
            {
                string bg = selected ? "OptionBaseFocused" : "OptionBase";
                if (button.normalBgSprite != bg) button.normalBgSprite = button.focusedBgSprite = bg;
                if (game != null)
                {
                    string fg = selected && GameHas(game + "Focused") ? game + "Focused" : game;
                    if (button.normalFgSprite != fg) button.normalFgSprite = button.focusedFgSprite = fg;
                }
                return;
            }
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
            parallelButton = sideButton = besideButton = directionButton = centredButton = null;
            gapLabel = null;
            upgradeButton = rampButton = null;
            for (int choice = 0; choice < medianButtons.Length; choice++) medianButtons[choice] = null;
            toolsLabel = null;
            tabHint = null;
            barItems.Clear();
            captions.Clear();
            for (int t = 0; t < tabButtons.Length; t++) tabButtons[t] = null;
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
            elevatedButton = tunnelButton = autoButton = gradeButton = resetButton = null;
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
            UpdateBottomBuild(model);
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
            // A text the game laid out higher than its row: laid out again next time.
            if (CheckHeights()) structure = null;
        }

        private string Shape(PanelModel model)
        {
            var shape = new System.Text.StringBuilder();
            shape.Append(model.HasPoints).Append(model.CanBuild).Append(model.Profile != null).Append('|').Append(page).Append('|').Append(tab).Append('|')
                .Append(string.Join(",", new List<string>(folded).ToArray())).Append('|');
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
            foreach (var fix in model.Fixes) shape.Append('|').Append(fix);
            return shape.ToString();
        }

        private void Rebuild(PanelModel model)
        {
            foreach (var component in dynamic) if (component != null) UnityEngine.Object.Destroy(component.gameObject);
            dynamic.Clear();
            texts.Clear();
            allotted.Clear();
            var gone = new List<UIButton>();
            foreach (var button in native.Keys) if (button == null) gone.Add(button);
            foreach (var button in gone) native.Remove(button);
            parent = panel;
            float y = ContentTop;

            y = Header("ROAD", y, "road");
            if (!Folded("road"))
            {
                y = Text(m => m.Road, TextColor, 0.85f, y);
                y = Text(m => m.Height, DimColor, 0.75f, y);
            }
            y = Text(m => m.Mode, DimColor, 0.75f, y);

            y = Header("CURVE", y);
            y = Text(m => m.Summary, TextColor, 0.85f, y);
            y = Text(m => m.Details, DimColor, 0.75f, y);

            profileSprite = null;
            profileReadout = null;
            drawnProfile = null;
            profilePixels = null;
            profileBase = null;
            elevatedButton = tunnelButton = autoButton = gradeButton = resetButton = null;
            shapeHint = null;
            if (model.Profile != null)
            {
                y = Header("SIDE VIEW", y, "side");
            }
            if (model.Profile != null && !Folded("side"))
            {
                profileSprite = (UITextureSprite)panel.AddUIComponent(typeof(UITextureSprite));
                profileSprite.size = new Vector2(ProfileWidth, ProfileHeight);
                profileSprite.relativePosition = new Vector3(Pad, y);
                profileSprite.tooltip = "Drag a dot up or down to set the road's height there (or the mouse wheel over it; right-click it to let go). " +
                                        "Drag along the view to select a stretch for the buttons below (none: the whole road). Click to keep the mark on the map";
                profileSprite.eventMouseMove += (component, param) =>
                {
                    if (draggedNode < 0 && !selecting) SetCursor(MouseAlong(), false);
                };
                profileSprite.eventMouseLeave += (component, param) => { if (!cursorPinned && draggedNode < 0) SetCursor(-1f, false); };
                profileSprite.eventMouseDown += (component, param) => PressProfile(param.buttons);
                // The mouse wheel over a dot: its height up or down (Shift: 1 m).
                profileSprite.eventMouseWheel += (component, param) =>
                {
                    float wx, wrow;
                    if (draggedNode >= 0 || NudgeNode == null || !MouseOnProfile(out wx, out wrow)) return;
                    int node = NodeUnderMouse(wx, wrow);
                    if (node < 0) return;
                    float step = ShiftStep(CurveTool.LiftStep);
                    NudgeNode(node, param.wheelDelta > 0f ? step : -step);
                    param.Use();
                };
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
                gradeButton = ShapeButton(Icons.Grade, "/", "Straight grade: one even slope from the selected stretch's first node to its last " +
                    "(the whole road if none is selected); the heights between are held on it. Heights set by a crossing stay",
                    Pad + 3f * (ShapeButtonSize + 4f), y, Grade);
                resetButton = ShapeButton(Icons.Reset, "R", "Clear every height and stretch set in the side view",
                    Width - Pad - ShapeButtonSize, y, () => { if (ResetShape != null) ResetShape(); });
                shapeHint = (UILabel)panel.AddUIComponent(typeof(UILabel));
                shapeHint.autoSize = false;
                shapeHint.size = new Vector2(Width - 2f * Pad - 5f * (ShapeButtonSize + 4f) - 4f, ShapeButtonSize);
                shapeHint.relativePosition = new Vector3(Pad + 4f * (ShapeButtonSize + 4f) + 2f, y);
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
                y = Header("ALONG THE ROAD", y, "along");
            }
            if (model.Items.Count > 0 && !Folded("along"))
            {
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
                        Action higher = () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(ShiftStep(c.Step)); };
                        Action lower = () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(-ShiftStep(c.Step)); };
                        var plus = IconButton(Icons.Plus, "+", "Higher (+" + step + " m, Shift: 1 m; or the mouse wheel)", new Vector2(x, y), false, higher);
                        var minus = IconButton(Icons.Minus, "-", "Lower (-" + step + " m, Shift: 1 m; or the mouse wheel)", new Vector2(x + IconSize + 3f, y), false, lower);
                        Wheel(higher, lower, plus, minus);
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
                // One click that clears a problem: two to a row.
                float fixWidth = (Width - 2f * Pad - 6f) / 2f;
                for (int i = 0; i < model.Fixes.Count; i++)
                {
                    int index = i;
                    var button = (UIButton)(parent ?? panel).AddUIComponent(typeof(UIButton));
                    button.size = new Vector2(fixWidth, 22f);
                    button.relativePosition = new Vector3(Pad + (i % 2) * (fixWidth + 6f), y);
                    button.text = model.Fixes[i];
                    button.textScale = 0.7f;
                    button.tooltip = "Fix: " + model.Fixes[i];
                    button.normalBgSprite = button.focusedBgSprite = "ButtonMenu";
                    button.hoveredBgSprite = "ButtonMenuHovered";
                    button.pressedBgSprite = "ButtonMenuPressed";
                    button.eventClick += (component, param) =>
                    {
                        if ((param.buttons & UIMouseButton.Left) != 0 && ApplyFix != null) ApplyFix(index);
                    };
                    dynamic.Add(button);
                    if (i % 2 == 1 || i == model.Fixes.Count - 1) y += 22f + RowGap;
                }
            }
            if (model.Warnings.Count > 0)
            {
                y = Header("NOTES", y, "notes");
            }
            if (model.Warnings.Count > 0 && !Folded("notes"))
            {
                for (int i = 0; i < model.Warnings.Count; i++)
                {
                    int index = i;
                    y = Text(m => index < m.Warnings.Count ? "• " + m.Warnings[index] : "", WarningColor, 0.75f, y);
                }
            }

            // The build button: what it builds and costs, lit when it can be built.
            bottomBuild = null;
            if (model.HasPoints)
            {
                y += 6f;
                bottomBuild = (UIButton)(parent ?? panel).AddUIComponent(typeof(UIButton));
                bottomBuild.size = new Vector2(Width - 2f * Pad, 34f);
                bottomBuild.relativePosition = new Vector3(Pad, y);
                bottomBuild.textScale = 0.95f;
                bottomBuild.normalBgSprite = bottomBuild.focusedBgSprite = "ButtonMenu";
                bottomBuild.hoveredBgSprite = "ButtonMenuHovered";
                bottomBuild.pressedBgSprite = "ButtonMenuPressed";
                bottomBuild.disabledBgSprite = "ButtonMenuDisabled";
                bottomBuild.tooltip = "Build it (Enter)";
                bottomBuild.eventClick += (component, param) =>
                {
                    if ((param.buttons & UIMouseButton.Left) != 0 && bottomBuild != null && bottomBuild.isEnabled && Build != null) Build();
                };
                dynamic.Add(bottomBuild);
                UpdateBottomBuild(model);
                y += 34f + RowGap;
            }
            // The keys, for the open tab.
            y = Text(m => KeysFor(m), DimColor, 0.7f, y + 2f);

            panel.size = new Vector2(Width, y + Pad);
        }

        private UIButton bottomBuild;

        private void UpdateBottomBuild(PanelModel model)
        {
            if (bottomBuild == null) return;
            string what = model.Upgrade ? "Upgrade" : "Build";
            string text = what + (model.Cost > 0 ? "   ·   " + (model.Cost / 100).ToString("N0") : "");
            if (bottomBuild.text != text) bottomBuild.text = text;
            SetEnabled(bottomBuild, model.CanBuild);
        }

        private string KeysFor(PanelModel model)
        {
            if (model.Upgrade) return "Enter: upgrade  ·  right-click: take back a point  ·  Esc: leave";
            return "Enter: plan, then build  ·  Esc: back  ·  Page Up / Down: height" + (tab == 0 ? "  ·  Tab: curved / straight" : "");
        }

        // Heights are worked out here rather than read back from the label (the
        // game lays text out later): roughly how many lines the text wraps to.
        private const float LineHeight = 20f;

        // Each text's height as the game laid it out (by text, width and size),
        // and the height each label was given: a label the game made higher than
        // that would have run into the next row, so the panel is laid out again
        // with the found height (once: it is kept).
        private readonly Dictionary<string, float> measured = new Dictionary<string, float>();
        private readonly Dictionary<UILabel, float> allotted = new Dictionary<UILabel, float>();
        private static string MeasureKey(string text, float width, float scale) => scale.ToString("0.00") + "|" + width.ToString("0") + "|" + text;

        private bool CheckHeights()
        {
            bool again = false;
            foreach (var pair in allotted)
            {
                var label = pair.Key;
                if (label == null || !label.isVisible || string.IsNullOrEmpty(label.text)) continue;
                if (label.size.y <= pair.Value + 1f) continue;
                measured[MeasureKey(label.text, label.size.x, label.textScale)] = label.size.y;
                again = true;
            }
            return again;
        }
        private const float CharWidth = 9.5f;

        private static int Lines(string text, float width, float scale)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // On the wide side: the game's font at small sizes is wider than in
            // proportion (measured on its screen), and a line too few clips the text.
            int perLine = Mathf.Max(8, Mathf.FloorToInt(width / (CharWidth * scale + 0.8f)));
            int lines = 0;
            // Wrapped at spaces like the label does (a word that doesn't fit goes to
            // the next line; one longer than a line is broken).
            foreach (var paragraph in text.Split('\n'))
            {
                int used = 0;
                lines++;
                foreach (var word in paragraph.Split(' '))
                {
                    int length = word.Length;
                    if (used > 0 && used + 1 + length > perLine)
                    {
                        lines++;
                        used = 0;
                    }
                    if (used > 0) used++;
                    while (length > perLine)
                    {
                        lines++;
                        length -= perLine;
                    }
                    used += length;
                }
            }
            return lines;
        }

        private void TurnPage(int step)
        {
            page += step;
            structure = null;          // rebuilt with the new page
            if (current != null) Show(current);
        }

        private PanelCrossing Row(int index) => current != null && index < current.Items.Count ? current.Items[index] : null;

        private float Header(string text, float y, string fold = null)
        {
            var label = (UILabel)(parent ?? panel).AddUIComponent(typeof(UILabel));
            label.text = text;
            label.textScale = 0.75f;
            label.textColor = HeaderColor;
            label.relativePosition = new Vector3(Pad, y + 6f);
            label.isInteractive = false;
            dynamic.Add(label);
            if (fold != null)
            {
                // Hide / Show on the right: the section folds away (remembered).
                var toggle = (UIButton)(parent ?? panel).AddUIComponent(typeof(UIButton));
                toggle.size = new Vector2(54f, 18f);
                toggle.relativePosition = new Vector3(Width - Pad - 54f, y + 3f);
                toggle.text = folded.Contains(fold) ? "Show" : "Hide";
                toggle.textScale = 0.7f;
                toggle.textColor = DimColor;
                toggle.hoveredTextColor = TextColor;
                toggle.pressedTextColor = TextColor;
                toggle.focusedTextColor = DimColor;
                toggle.tooltip = (folded.Contains(fold) ? "Show " : "Hide ") + text.ToLower();
                toggle.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0) ToggleFold(fold); };
                dynamic.Add(toggle);
            }
            return y + 6f + 16f;
        }

        private bool Folded(string section) => folded.Contains(section);

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
            // As high as it was found to be (the game sizes it to its text:
            // autoHeight), else the estimate; Show checks it and lays out again.
            float height = Lines(value, width, scale) * LineHeight * scale, found;
            if (measured.TryGetValue(MeasureKey(value, width, scale), out found)) height = Mathf.Max(height, found);
            label.autoHeight = true;
            label.size = new Vector2(width, height);
            label.relativePosition = new Vector3(at.x, at.y);
            allotted[label] = height;
            label.isInteractive = false;
            label.text = value;
            dynamic.Add(label);
            texts.Add(new KeyValuePair<UILabel, Func<PanelModel, string>>(label, text));
            return label;
        }

        // The mouse wheel over any of 'over': up runs 'up', down runs 'down'.
        internal static void Wheel(Action up, Action down, params UIComponent[] over)
        {
            foreach (var component in over)
            {
                if (component == null) continue;
                component.eventMouseWheel += (c, p) =>
                {
                    if (p.wheelDelta > 0f) up();
                    else if (p.wheelDelta < 0f) down();
                    p.Use();
                };
            }
        }

        private UIButton IconButton(string icon, string letter, string tooltip, Vector2 at, bool selected, Action onClick)
        {
            var button = (UIButton)(parent ?? panel).AddUIComponent(typeof(UIButton));
            button.size = new Vector2(IconSize, IconSize);
            button.relativePosition = new Vector3(at.x, at.y);
            button.tooltip = tooltip;
            Dress(button, icon, letter, selected);
            button.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0) onClick(); };
            dynamic.Add(button);
            return button;
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
            return x + text.Length * 7.5f + 14f;
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

            var pixels = baseBuffer;
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
            profilePixels = shownBuffer;
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

        // The selected stretch (or the whole road) on one even slope.
        private void Grade()
        {
            if (StraightGrade == null || drawnProfile == null) return;
            float from = 0f, to = drawnProfile.Length;
            if (HasSelection)
            {
                from = Mathf.Min(selectFrom, selectTo);
                to = Mathf.Max(selectFrom, selectTo);
            }
            ClearSelection();
            StraightGrade(from, to);
        }

        private void UpdateShapeButtons()
        {
            bool profile = drawnProfile != null;
            SetEnabled(gradeButton, profile);
            SetEnabled(elevatedButton, profile);
            SetEnabled(tunnelButton, profile);
            SetEnabled(autoButton, profile);
            SetEnabled(resetButton, current != null && current.Shaped);
            if (shapeHint != null)
            {
                string hint = HasSelection
                    ? Mathf.Min(selectFrom, selectTo).ToString("N0") + "-" + Mathf.Max(selectFrom, selectTo).ToString("N0") + " m"
                    : "Whole road";
                if (shapeHint.text != hint) shapeHint.text = hint;
            }
        }

        private UIButton ShapeButton(string icon, string letter, string tooltip, float x, float y, Action onClick)
        {
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
            button.size = new Vector2(ShapeButtonSize, ShapeButtonSize);
            button.relativePosition = new Vector3(x, y);
            button.tooltip = tooltip;
            Dress(button, icon, letter, false);
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
