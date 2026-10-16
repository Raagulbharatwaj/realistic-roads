using System;
using System.Collections.Generic;
using ColossalFramework.UI;
using UnityEngine;

namespace RealisticRoads
{
    // RB panel: put a road together from pieces, left to right, and create it.
    //   top row      the road to scale; click a piece to select it
    //   add          car / bus / bike / parking / median, after the selected piece
    //   selected     move < >, narrower / wider, flip direction, median style, delete
    //   presets      starting points
    //   library      your roads: click to load one into the editor, Delete removes it
    // Create always makes a new road type (a city's roads keep the type they were
    // built with). Main thread only.
    public static class RoadBuilderPanel
    {
        private const float PanelWidth = 600f;
        private const float PanelHeight = 404f;
        private const float WidthStep = 0.5f;
        private const int MaxLibraryButtons = 8;

        private static UIPanel panel;
        private static UITextField nameField;
        private static UILabel infoLabel;
        private static readonly List<UIComponent> pieceButtons = new List<UIComponent>();
        private static readonly List<UIComponent> libraryButtons = new List<UIComponent>();

        private static RoadConfig editing;
        private static int selected = -1;
        private static string loadedId;   // library road last loaded into the editor (for Delete)

        public static bool IsVisible => panel != null && panel.isVisible;

        public static void Create()
        {
            var view = UIView.GetAView();
            panel = (UIPanel)view.AddUIComponent(typeof(UIPanel));
            panel.name = "RealisticRoadsRoadBuilder";
            panel.backgroundSprite = "MenuPanel2";
            panel.size = new Vector2(PanelWidth, PanelHeight);
            panel.relativePosition = new Vector3(66f, 120f);
            panel.isVisible = false;

            Label("Road builder: pieces from left to right (in the direction you draw)", 12f, 10f, 0.9f);
            Label("Name", 12f, 40f, 0.8f);
            nameField = TextField(60f, 36f, 300f);
            Label("Speed", 380f, 40f, 0.8f);
            Button("-", 430f, 36f, 26f, () => ChangeSpeed(-10f), "Slower");
            Button("+", 460f, 36f, 26f, () => ChangeSpeed(10f), "Faster");

            // Row 3 (y 70-110) is the road preview, built in Refresh.

            float y = 124f;
            Label("Add", 12f, y + 4f, 0.8f);
            float x = 60f;
            x = Button("Car", x, y, 56f, () => Add(PieceKind.Car), "Add a car lane after the selected piece");
            x = Button("Bus", x, y, 56f, () => Add(PieceKind.Bus), "Add a bus lane");
            x = Button("Bike", x, y, 56f, () => Add(PieceKind.Bike), "Add a bike lane");
            x = Button("Parking", x, y, 76f, () => Add(PieceKind.Parking), "Add a parking lane (next to a sidewalk)");
            x = Button("Median", x, y, 70f, () => Add(PieceKind.Median), "Add a median between lanes");
            Button("Sidewalks", x, y, 90f, ToggleSidewalks, "Sidewalks on both sides, or none (one-way roads only)");

            y = 158f;
            Label("Selected", 12f, y + 4f, 0.8f);
            x = 76f;
            x = Button("<", x, y, 32f, () => Move(-1), "Move left");
            x = Button(">", x, y, 32f, () => Move(1), "Move right");
            x = Button("Narrower", x, y, 82f, () => Resize(-WidthStep), "0.5 m narrower (sidewalks change together)");
            x = Button("Wider", x, y, 64f, () => Resize(WidthStep), "0.5 m wider");
            x = Button("Flip", x, y, 50f, Flip, "Change the lane's direction");
            x = Button("Style", x, y, 56f, CycleMedian, "Median: painted, grass, trees");
            Button("Remove", x, y, 74f, Remove, "Remove the selected piece");

            y = 192f;
            Label("Presets", 12f, y + 4f, 0.8f);
            x = 76f;
            foreach (var preset in RoadConfig.Presets())
            {
                var p = preset;
                x = Button(p.Name, x, y, Mathf.Max(60f, p.Name.Length * 7.2f), () => Load(p.Copy(), null), "Start from " + p.Name);
            }

            infoLabel = Label("", 12f, 228f, 0.75f);
            infoLabel.autoSize = false;
            infoLabel.wordWrap = true;
            infoLabel.size = new Vector2(PanelWidth - 24f, 90f);

            Button("Create road", 12f, 320f, 120f, CreateRoad, "Save it to your library and make it buildable now");
            Button("Delete", 140f, 320f, 70f, DeleteLoaded, "Delete the library road you loaded (only if no road of it is built)");
            Button("Close", PanelWidth - 82f, 320f, 70f, Toggle, "Close (Ctrl+Shift+B)");
            Label("Library", 12f, 360f, 0.8f);

            Load(RoadConfig.Presets()[0], null);
        }

        public static void Destroy()
        {
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null;
            nameField = null;
            infoLabel = null;
            pieceButtons.Clear();
            libraryButtons.Clear();
        }

        public static void Toggle()
        {
            if (panel == null) return;
            panel.isVisible = !panel.isVisible;
            if (panel.isVisible)
            {
                RefreshLibrary();
                Refresh();
            }
        }

        // ---- Editing ----

        private static void Load(RoadConfig config, string libraryId)
        {
            editing = config;
            loadedId = libraryId;
            selected = -1;
            if (nameField != null) nameField.text = config.Name;
            Refresh();
        }

        private static void Add(PieceKind kind)
        {
            var pieces = editing.Pieces;
            int index = selected >= 0 ? selected + 1 : pieces.Count;
            // Never outside the sidewalks.
            if (pieces.Count > 0 && pieces[pieces.Count - 1].Kind == PieceKind.Sidewalk) index = Mathf.Min(index, pieces.Count - 1);
            if (pieces.Count > 0 && pieces[0].Kind == PieceKind.Sidewalk) index = Mathf.Max(index, 1);

            // Lanes left of the middle go backward, right of it forward.
            float before = 0f, total = 0f;
            for (int i = 0; i < pieces.Count; i++)
            {
                if (i < index) before += pieces[i].Width;
                total += pieces[i].Width;
            }
            var direction = before >= total * 0.5f ? LaneDirection.Forward : LaneDirection.Backward;
            var piece = new RoadPiece(kind, direction);
            if (kind == PieceKind.Median) piece.Median = MedianStyle.Grass;
            pieces.Insert(index, piece);
            selected = index;
            Refresh();
        }

        private static void ToggleSidewalks()
        {
            var pieces = editing.Pieces;
            if (pieces.Count > 0 && pieces[0].Kind == PieceKind.Sidewalk)
                pieces.RemoveAll(p => p.Kind == PieceKind.Sidewalk);
            else
            {
                pieces.Insert(0, new RoadPiece(PieceKind.Sidewalk));
                pieces.Add(new RoadPiece(PieceKind.Sidewalk));
            }
            selected = -1;
            Refresh();
        }

        private static RoadPiece Selected => selected >= 0 && selected < editing.Pieces.Count ? editing.Pieces[selected] : null;

        private static void Move(int step)
        {
            var piece = Selected;
            if (piece == null || piece.Kind == PieceKind.Sidewalk) return;
            int to = selected + step;
            if (to < 0 || to >= editing.Pieces.Count || editing.Pieces[to].Kind == PieceKind.Sidewalk) return;
            editing.Pieces[selected] = editing.Pieces[to];
            editing.Pieces[to] = piece;
            selected = to;
            Refresh();
        }

        private static void Resize(float step)
        {
            var piece = Selected;
            if (piece == null) return;
            float width = Mathf.Clamp(piece.Width + step, RoadPiece.MinWidth(piece.Kind), RoadPiece.MaxWidth(piece.Kind));
            if (piece.Kind == PieceKind.Sidewalk)
                foreach (var p in editing.Pieces) { if (p.Kind == PieceKind.Sidewalk) p.Width = width; }
            else piece.Width = width;
            Refresh();
        }

        private static void Flip()
        {
            var piece = Selected;
            if (piece == null || !piece.HasDirection) return;
            piece.Direction = piece.Direction == LaneDirection.Forward ? LaneDirection.Backward : LaneDirection.Forward;
            Refresh();
        }

        private static void CycleMedian()
        {
            var piece = Selected;
            if (piece == null || piece.Kind != PieceKind.Median) return;
            piece.Median = (MedianStyle)(((int)piece.Median + 1) % 3);
            Refresh();
        }

        private static void Remove()
        {
            var piece = Selected;
            if (piece == null) return;
            if (piece.Kind == PieceKind.Sidewalk) editing.Pieces.RemoveAll(p => p.Kind == PieceKind.Sidewalk);
            else editing.Pieces.RemoveAt(selected);
            selected = -1;
            Refresh();
        }

        private static void ChangeSpeed(float step)
        {
            editing.SpeedKmh = Mathf.Clamp(editing.SpeedKmh + step, CrossSection.MinSpeedKmh, CrossSection.MaxSpeedKmh);
            Refresh();
        }

        // ---- Create / delete ----

        private static void CreateRoad()
        {
            try
            {
                var config = editing.Copy();
                config.Name = nameField != null && !string.IsNullOrEmpty(nameField.text.Trim()) ? nameField.text.Trim() : "Custom road";
                config.Id = RoadConfig.NewId();
                var cs = CrossSection.Compose(config);
                if (!cs.Ok)
                {
                    SetInfo("Can't create it yet:\n! " + string.Join("\n! ", cs.Errors.ToArray()));
                    return;
                }
                RoadLibrary.Save(config);
                var info = RoadTypes.CreateNow(config);
                loadedId = config.Id;
                RefreshLibrary();
                if (info == null)
                {
                    SetInfo("Saved '" + config.Name + "' to your library, but it couldn't be made buildable now (see the log). " +
                            "It will be there after you load a city again.");
                    return;
                }
                var netTool = ToolsModifierControl.GetTool<NetTool>();
                if (netTool != null) netTool.m_prefab = info;
                SetInfo("Created '" + config.Name + "'. It is selected: press RR (Ctrl+Shift+C) to draw it. It is also in the " +
                        "roads menu next to the road it was made from, after you load a city again.");
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: creating the road failed", e);
                SetInfo("Creating the road failed, see the log.");
            }
        }

        private static void DeleteLoaded()
        {
            var config = loadedId != null ? RoadLibrary.Find(loadedId) : null;
            if (config == null)
            {
                SetInfo("Load a road from your library first (buttons at the bottom), then Delete removes it.");
                return;
            }
            int used = CountSegments(config);
            if (used > 0)
            {
                SetInfo("'" + config.Name + "' has " + used + " road segment(s) in this city; bulldoze them first.");
                return;
            }
            RoadLibrary.Delete(config);
            loadedId = null;
            RefreshLibrary();
            SetInfo("Deleted '" + config.Name + "' from your library (other saved cities that use it will miss it).");
        }

        private static int CountSegments(RoadConfig config)
        {
            int count = 0;
            var segments = NetManager.instance.m_segments.m_buffer;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == 0) continue;
                var info = segments[i].Info;
                if (info != null && info.name != null && info.name.StartsWith(config.PrefabName)) count++;
            }
            return count;
        }

        // ---- Drawing ----

        private static void Refresh()
        {
            if (panel == null || editing == null) return;
            foreach (var c in pieceButtons) UnityEngine.Object.Destroy(c.gameObject);
            pieceButtons.Clear();

            float total = 0f;
            foreach (var p in editing.Pieces) total += p.Width;
            float scale = total > 0f ? Mathf.Min(14f, (PanelWidth - 24f) / total) : 14f;
            float x = 12f;
            for (int i = 0; i < editing.Pieces.Count; i++)
            {
                var piece = editing.Pieces[i];
                int index = i;
                float w = Mathf.Max(piece.Width * scale, 8f);
                var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
                button.text = Short(piece);
                button.textScale = 0.7f;
                button.tooltip = piece.Describe();
                button.size = new Vector2(w - 2f, 40f);
                button.relativePosition = new Vector3(x, 72f);
                bool isSelected = i == selected;
                button.normalBgSprite = isSelected ? "ButtonMenuPressed" : "ButtonMenu";
                button.hoveredBgSprite = "ButtonMenuHovered";
                button.pressedBgSprite = "ButtonMenuPressed";
                button.focusedBgSprite = isSelected ? "ButtonMenuPressed" : "ButtonMenu";
                button.eventClick += (component, param) =>
                {
                    selected = selected == index ? -1 : index;
                    Refresh();
                };
                pieceButtons.Add(button);
                x += w;
            }

            var cs = CrossSection.Compose(editing);
            string text = "Width " + (cs.HalfWidth * 2f).ToString("0.0") + " m   lanes " + cs.BackwardLanes + " <  " + cs.ForwardLanes +
                          " >   " + editing.SpeedKmh.ToString("0") + " km/h   built from: " + cs.BaseName +
                          " (its markings and props, stretched to fit)";
            if (Selected != null) text += "\nSelected: " + Selected.Describe();
            foreach (var e in cs.Errors) text += "\n! " + e;
            foreach (var w in cs.Warnings) text += "\n~ " + w;
            SetInfo(text);
        }

        private static void RefreshLibrary()
        {
            if (panel == null) return;
            foreach (var c in libraryButtons) UnityEngine.Object.Destroy(c.gameObject);
            libraryButtons.Clear();
            float x = 76f;
            int shown = 0;
            foreach (var config in RoadLibrary.All())
            {
                if (shown++ >= MaxLibraryButtons) break;
                var c = config;
                string label = c.Name.Length > 14 ? c.Name.Substring(0, 13) + "." : c.Name;
                float w = Mathf.Max(50f, label.Length * 7f);
                if (x + w > PanelWidth - 12f) break;
                var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
                Style(button, label, w, "Load '" + c.Name + "' into the editor");
                button.relativePosition = new Vector3(x, 356f);
                button.eventClick += (component, param) => Load(c.Copy(), c.Id);
                libraryButtons.Add(button);
                x += w + 4f;
            }
        }

        private static string Short(RoadPiece piece)
        {
            string arrow = piece.Direction == LaneDirection.Forward ? ">" : "<";
            switch (piece.Kind)
            {
                case PieceKind.Car: return arrow;
                case PieceKind.Bus: return "Bus" + arrow;
                case PieceKind.Bike: return "Bk" + arrow;
                case PieceKind.Parking: return "P";
                case PieceKind.Median: return piece.Median == MedianStyle.Trees ? "Tree" : piece.Median == MedianStyle.Grass ? "Grs" : "=";
                default: return "Walk";
            }
        }

        private static void SetInfo(string text)
        {
            if (infoLabel != null) infoLabel.text = text;
        }

        // ---- Widgets ----

        private static UILabel Label(string text, float x, float y, float scale)
        {
            var label = (UILabel)panel.AddUIComponent(typeof(UILabel));
            label.text = text;
            label.textScale = scale;
            label.autoSize = true;
            label.relativePosition = new Vector3(x, y);
            return label;
        }

        // Returns the x where the next button goes.
        private static float Button(string text, float x, float y, float width, Action onClick, string tooltip)
        {
            var button = (UIButton)panel.AddUIComponent(typeof(UIButton));
            Style(button, text, width, tooltip);
            button.relativePosition = new Vector3(x, y);
            button.eventClick += (component, param) =>
            {
                try { onClick(); }
                catch (Exception e) { Mod.LogError("Road builder: '" + text + "' failed", e); }
            };
            return x + width + 4f;
        }

        private static void Style(UIButton button, string text, float width, string tooltip)
        {
            button.text = text;
            button.textScale = 0.75f;
            button.tooltip = tooltip;
            button.size = new Vector2(width, 28f);
            button.normalBgSprite = "ButtonMenu";
            button.hoveredBgSprite = "ButtonMenuHovered";
            button.pressedBgSprite = "ButtonMenuPressed";
            button.focusedBgSprite = "ButtonMenu";
        }

        private static UITextField TextField(float x, float y, float width)
        {
            var field = (UITextField)panel.AddUIComponent(typeof(UITextField));
            field.size = new Vector2(width, 24f);
            field.relativePosition = new Vector3(x, y);
            field.normalBgSprite = "TextFieldPanel";
            field.hoveredBgSprite = "TextFieldPanelHovered";
            field.focusedBgSprite = "TextFieldPanel";
            field.selectionSprite = "EmptySprite";
            field.builtinKeyNavigation = true;
            field.isInteractive = true;
            field.readOnly = false;
            field.canFocus = true;
            field.textScale = 0.85f;
            field.padding = new RectOffset(6, 6, 4, 4);
            field.horizontalAlignment = UIHorizontalAlignment.Left;
            field.maxLength = 40;
            return field;
        }
    }
}
