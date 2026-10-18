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
        public string Summary = "";               // length, segments, cost
        public string Details = "";               // raised / lowered / continues ...
        public readonly List<PanelCrossing> Crossings = new List<PanelCrossing>();
        public readonly List<PanelWater> Water = new List<PanelWater>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool CanBuild;
        public bool HasPoints;
        public PanelProfile Profile;              // the curve seen from the side (null: nothing to show)
        public string ProfileText = "";           // highest / deepest / steepest
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
    }

    public class PanelCrossing
    {
        public string Title = "";                 // "1  Train Track"
        public string Detail = "";                // "Bridge, 11 m up"
        public readonly List<CrossingType> Allowed = new List<CrossingType>();
        public CrossingType Selected;
        public bool HeightButtons;                // bridge: +/- for the deck
        public float Step;                        // metres per +/- click
        public Action<CrossingType> Pick;
        public Action<float> Raise;
    }

    public class PanelWater
    {
        public string Title = "";
        public float Step;                        // metres per +/- click
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
        private Color32[] profilePixels;          // the drawn profile without the cursor line
        private UILabel profileReadout;
        private float cursorAlong = -1f;          // metres along the curve under the mouse on the side view (-1: none)
        private bool cursorPinned;                // clicked: stays when the mouse leaves
        private readonly List<UIComponent> dynamic = new List<UIComponent>();
        private readonly List<KeyValuePair<UILabel, Func<PanelModel, string>>> texts = new List<KeyValuePair<UILabel, Func<PanelModel, string>>>();
        private string structure;
        private PanelModel current;

        public Action Build, Undo, Clear, Close, GameTool;

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
            title.textScale = 1.1f;
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

            panel.isVisible = false;
        }

        public void Destroy()
        {
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            if (profileTexture != null) UnityEngine.Object.Destroy(profileTexture);
            panel = null;
            profileSprite = null;
            profileTexture = null;
            drawnProfile = null;
            profilePixels = null;
            profileReadout = null;
            cursorAlong = -1f;
            cursorPinned = false;
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
        }

        private static string Shape(PanelModel model)
        {
            var shape = new System.Text.StringBuilder();
            shape.Append(model.HasPoints).Append(model.CanBuild).Append(model.Profile != null).Append('|');
            shape.Append(Lines(model.ProfileText, Width - 2f * Pad, 0.75f)).Append(Lines(model.Height, Width - 2f * Pad, 0.75f));
            // A text that wraps to more or fewer lines moves everything under it.
            float wide = Width - 2f * Pad;
            shape.Append(Lines(model.Road, wide, 0.85f)).Append(Lines(model.Summary, wide, 0.85f)).Append(Lines(model.Details, wide, 0.75f));
            foreach (var crossing in model.Crossings) shape.Append(Lines(crossing.Title, wide, 0.8f)).Append(Lines(crossing.Detail, wide, 0.75f));
            foreach (var problem in model.Problems) shape.Append(Lines("• " + problem, wide, 0.75f));
            foreach (var warning in model.Warnings) shape.Append(Lines("• " + warning, wide, 0.75f));
            foreach (var crossing in model.Crossings)
            {
                foreach (var type in crossing.Allowed) shape.Append((int)type);
                shape.Append(':').Append((int)crossing.Selected).Append(crossing.HeightButtons).Append(';');
            }
            shape.Append('|').Append(model.Water.Count).Append('|').Append(model.Problems.Count).Append('|').Append(model.Warnings.Count);
            return shape.ToString();
        }

        private void Rebuild(PanelModel model)
        {
            foreach (var component in dynamic) if (component != null) UnityEngine.Object.Destroy(component.gameObject);
            dynamic.Clear();
            texts.Clear();
            float y = 46f;

            y = Header("ROAD", y);
            y = Text(m => m.Road, TextColor, 0.85f, y);
            y = Text(m => m.Height, DimColor, 0.75f, y);

            y = Header("CURVE", y);
            y = Text(m => m.Summary, TextColor, 0.85f, y);
            y = Text(m => m.Details, DimColor, 0.75f, y);

            profileSprite = null;
            profileReadout = null;
            drawnProfile = null;
            profilePixels = null;
            if (model.Profile != null)
            {
                y = Header("SIDE VIEW", y);
                profileSprite = (UITextureSprite)panel.AddUIComponent(typeof(UITextureSprite));
                profileSprite.size = new Vector2(ProfileWidth, ProfileHeight);
                profileSprite.relativePosition = new Vector3(Pad, y);
                profileSprite.tooltip = "Move the mouse along it to see the height there (marked on the map); click to keep the mark";
                profileSprite.eventMouseMove += (component, param) => SetCursor(MouseAlong(), false);
                profileSprite.eventMouseLeave += (component, param) => { if (!cursorPinned) SetCursor(-1f, false); };
                profileSprite.eventClick += (component, param) =>
                {
                    cursorPinned = !cursorPinned;
                    SetCursor(cursorPinned ? MouseAlong() : -1f, true);
                };
                dynamic.Add(profileSprite);
                y += ProfileHeight + 3f;
                float x = Pad;
                foreach (var style in new[] { ProfileStyle.Ground, ProfileStyle.Raised, ProfileStyle.Lowered, ProfileStyle.Steep })
                    x = Legend(StyleName(style), StyleColor(style), x, y);
                Legend("pillar", PillarLine, x, y);
                y += 16f;
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

            if (model.Crossings.Count > 0)
            {
                y = Header("CROSSINGS", y);
                for (int i = 0; i < model.Crossings.Count; i++)
                {
                    int index = i;
                    y = Text(m => index < m.Crossings.Count ? m.Crossings[index].Title : "", TextColor, 0.8f, y);
                    y = Text(m => index < m.Crossings.Count ? m.Crossings[index].Detail : "", DimColor, 0.75f, y - 2f);
                    float x = Pad;
                    var crossing = model.Crossings[i];
                    foreach (var type in crossing.Allowed)
                    {
                        var t = type;
                        IconButton(IconFor(t), Letter(t), Describe(t), new Vector2(x, y), crossing.Selected == t,
                            () => { var c = Row(index); if (c != null && c.Pick != null) c.Pick(t); });
                        x += IconSize + 3f;
                    }
                    if (crossing.HeightButtons)
                    {
                        x += 8f;
                        string step = crossing.Step.ToString("0.#");
                        IconButton(Icons.Plus, "+", "Deck higher (+" + step + " m)", new Vector2(x, y), false,
                            () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(c.Step); });
                        IconButton(Icons.Minus, "-", "Deck lower (-" + step + " m)", new Vector2(x + IconSize + 3f, y), false,
                            () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(-c.Step); });
                    }
                    y += IconSize + RowGap + 4f;
                }
            }

            if (model.Water.Count > 0)
            {
                y = Header("WATER", y);
                for (int i = 0; i < model.Water.Count; i++)
                {
                    int index = i;
                    float rowY = y;
                    var label = Label(m => index < m.Water.Count ? m.Water[index].Title : "", TextColor, 0.8f, new Vector2(Pad, rowY + 7f),
                        Width - 2f * Pad - 2f * (IconSize + 3f));
                    string step = model.Water[i].Step.ToString("0.#");
                    IconButton(Icons.Plus, "+", "Bridge higher over the water (+" + step + " m)", new Vector2(Width - Pad - 2f * IconSize - 3f, rowY), false,
                        () => { if (index < current.Water.Count) current.Water[index].Raise(current.Water[index].Step); });
                    IconButton(Icons.Minus, "-", "Bridge lower over the water (-" + step + " m)", new Vector2(Width - Pad - IconSize, rowY), false,
                        () => { if (index < current.Water.Count) current.Water[index].Raise(-current.Water[index].Step); });
                    y = Mathf.Max(rowY + IconSize, rowY + label.size.y) + RowGap;
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

            // Actions.
            y += 6f;
            float buttonWidth = (Width - 2f * Pad - 3f * 6f) / 4f;
            TextButton("Build", "Build the curve (Enter, or double-click)", new Vector2(Pad, y), buttonWidth, model.CanBuild, () => { if (Build != null) Build(); });
            TextButton("Undo", "Remove the last curve built (Ctrl+Z)", new Vector2(Pad + buttonWidth + 6f, y), buttonWidth, true, () => { if (Undo != null) Undo(); });
            TextButton("Clear", "Clear the points (Esc)", new Vector2(Pad + 2f * (buttonWidth + 6f), y), buttonWidth, model.HasPoints, () => { if (Clear != null) Clear(); });
            TextButton("Game tool", "Draw this road with the game's own road tool instead (RR stays off until you pick it again)",
                new Vector2(Pad + 3f * (buttonWidth + 6f), y), buttonWidth, true, () => { if (GameTool != null) GameTool(); });
            y += 36f;
            y = Text(m => "Click: add a point   Right-click: remove the last one\nPage Up / Down: height of the next point\nOn a road: join it   Crossings: pick above or here",
                DimColor, 0.7f, y);
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

        private PanelCrossing Row(int index) => current != null && index < current.Crossings.Count ? current.Crossings[index] : null;

        private float Header(string text, float y)
        {
            var label = (UILabel)panel.AddUIComponent(typeof(UILabel));
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
            var label = (UILabel)panel.AddUIComponent(typeof(UILabel));
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
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
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

        private void TextButton(string text, string tooltip, Vector2 at, float width, bool enabled, Action onClick)
        {
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
            button.size = new Vector2(width, 30f);
            button.relativePosition = new Vector3(at.x, at.y);
            button.text = text;
            button.textScale = 0.8f;
            button.tooltip = tooltip;
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.disabledBgSprite = "ButtonMenuDisabled";
            button.isEnabled = enabled;
            button.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0 && button.isEnabled) onClick(); };
            dynamic.Add(button);
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
            foreach (float at in profile.Pillars)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt(at / Mathf.Max(profile.Length, 1f) * (w - 1)), 0, w - 1);
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
                int cx = Mathf.Clamp(Mathf.RoundToInt(crossing.Key / Mathf.Max(profile.Length, 1f) * (w - 1)), 0, w - 1);
                int cy = Row(crossing.Value, low, high, h);
                for (int x = Mathf.Max(cx - 3, 0); x <= Mathf.Min(cx + 3, w - 1); x++)
                    for (int y = Mathf.Max(cy - 1, 0); y <= Mathf.Min(cy + 1, h - 1); y++) pixels[y * w + x] = CrossingMark;
            }

            profilePixels = pixels;
            if (cursorAlong > profile.Length) cursorAlong = profile.Length;
            ShowCursor();
            profileSprite.texture = profileTexture;
        }

        // --- the side view's cursor ------------------------------------------------

        // Metres along the curve under the mouse on the side view (-1 if it isn't over it).
        private float MouseAlong()
        {
            var profile = drawnProfile;
            if (profileSprite == null || profile == null) return -1f;
            var view = UIView.GetAView();
            Vector2 gui = view.ScreenPointToGUI(Input.mousePosition / view.inputScale);
            float x = gui.x - profileSprite.absolutePosition.x;
            if (x < 0f || x > ProfileWidth) return -1f;
            return Mathf.Clamp01(x / (ProfileWidth - 1)) * profile.Length;
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

        // The profile with a white line where the cursor is, and its readout under it.
        private void ShowCursor()
        {
            var profile = drawnProfile;
            if (profileTexture == null || profilePixels == null || profile == null) return;
            const int w = ProfileWidth, h = ProfileHeight;
            var pixels = profilePixels;
            if (cursorAlong >= 0f)
            {
                pixels = (Color32[])profilePixels.Clone();
                int x = Mathf.Clamp(Mathf.RoundToInt(cursorAlong / Mathf.Max(profile.Length, 1f) * (w - 1)), 0, w - 1);
                for (int y = 0; y < h; y++) pixels[y * w + x] = CrossingMark;
            }
            profileTexture.SetPixels32(pixels);
            profileTexture.Apply();
            if (profileSprite != null) profileSprite.Invalidate();
            if (profileReadout != null) profileReadout.text = Readout(profile, cursorAlong);
        }

        private string Readout(PanelProfile profile, float along)
        {
            if (along < 0f) return "Point at the side view to see the height there";
            int i = Sample(profile, along);
            float above = profile.Road[i] - Mathf.Max(profile.Ground[i], profile.Water[i]);
            string height = above >= 1f ? above.ToString("F0") + " m above the " + (profile.Water[i] > profile.Ground[i] + 0.3f ? "water" : "ground")
                : above <= -1f ? (-above).ToString("F0") + " m below the ground" : "on the ground";
            return along.ToString("F0") + " m along: " + height + ", " + StyleName(profile.Style[i]) + (cursorPinned ? "  (pinned)" : "");
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
