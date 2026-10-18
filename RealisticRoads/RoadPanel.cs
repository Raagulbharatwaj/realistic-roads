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
        public string Summary = "";               // length, segments, cost
        public string Details = "";               // raised / lowered / continues ...
        public readonly List<PanelCrossing> Crossings = new List<PanelCrossing>();
        public readonly List<PanelWater> Water = new List<PanelWater>();
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool CanBuild;
        public bool HasPoints;
    }

    public class PanelCrossing
    {
        public string Title = "";                 // "1  Train Track"
        public string Detail = "";                // "Bridge, 11 m up"
        public readonly List<CrossingType> Allowed = new List<CrossingType>();
        public CrossingType Selected;
        public bool HeightButtons;                // bridge: +/- for the deck
        public Action<CrossingType> Pick;
        public Action<float> Raise;
    }

    public class PanelWater
    {
        public string Title = "";
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

        private UIPanel panel;
        private readonly List<UIComponent> dynamic = new List<UIComponent>();
        private readonly List<KeyValuePair<UILabel, Func<PanelModel, string>>> texts = new List<KeyValuePair<UILabel, Func<PanelModel, string>>>();
        private string structure;
        private PanelModel current;

        public Action Build, Undo, Clear, Close;

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
            if (Icons.Atlas != null)
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
            panel = null;
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
                return;
            }
            foreach (var pair in texts)
            {
                string text = pair.Value(model);
                if (pair.Key.text != text) pair.Key.text = text;
            }
        }

        private static string Shape(PanelModel model)
        {
            var shape = new System.Text.StringBuilder();
            shape.Append(model.HasPoints).Append(model.CanBuild).Append('|');
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

            y = Header("CURVE", y);
            y = Text(m => m.Summary, TextColor, 0.85f, y);
            y = Text(m => m.Details, DimColor, 0.75f, y);

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
                        IconButton(Icons.Plus, "+", "Deck higher (+5 m)", new Vector2(x, y), false,
                            () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(5f); });
                        IconButton(Icons.Minus, "-", "Deck lower (-5 m)", new Vector2(x + IconSize + 3f, y), false,
                            () => { var c = Row(index); if (c != null && c.Raise != null) c.Raise(-5f); });
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
                    IconButton(Icons.Plus, "+", "Bridge higher over the water (+5 m)", new Vector2(Width - Pad - 2f * IconSize - 3f, rowY), false,
                        () => { if (index < current.Water.Count) current.Water[index].Raise(5f); });
                    IconButton(Icons.Minus, "-", "Bridge lower over the water (-5 m)", new Vector2(Width - Pad - IconSize, rowY), false,
                        () => { if (index < current.Water.Count) current.Water[index].Raise(-5f); });
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
            float buttonWidth = (Width - 2f * Pad - 2f * 6f) / 3f;
            TextButton("Build", "Build the curve (Enter, or double-click)", new Vector2(Pad, y), buttonWidth, model.CanBuild, () => { if (Build != null) Build(); });
            TextButton("Undo", "Remove the last curve built (Ctrl+Z)", new Vector2(Pad + buttonWidth + 6f, y), buttonWidth, true, () => { if (Undo != null) Undo(); });
            TextButton("Clear", "Clear the points (Esc)", new Vector2(Pad + 2f * (buttonWidth + 6f), y), buttonWidth, model.HasPoints, () => { if (Clear != null) Clear(); });
            y += 36f;
            y = Text(m => "Click: add a point   Right-click: remove the last one\nOn a road: join it   Crossings: pick above or here",
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
            var atlas = Icons.Atlas;
            if (atlas != null)
            {
                button.atlas = atlas;
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
            button.textScale = 0.9f;
            button.tooltip = tooltip;
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.disabledBgSprite = "ButtonMenuDisabled";
            button.isEnabled = enabled;
            button.eventClick += (component, param) => { if ((param.buttons & UIMouseButton.Left) != 0 && button.isEnabled) onClick(); };
            dynamic.Add(button);
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
