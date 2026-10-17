using System;
using System.Collections.Generic;

namespace RealisticRoads
{
    // R3 road builder, the pure part: a road is a list of pieces from left to
    // right; CrossSection.Compose turns it into widths, lane positions, checks
    // and the vanilla road to build it from. No game code here, so it is
    // unit-tested like CurveMath (see docs/R3_ROAD_BUILDER.md).

    public enum PieceKind { Sidewalk, Car, Bus, Bike, Parking, Median }

    // Forward = the way the road is drawn. Right-hand traffic: backward lanes on
    // the left, forward on the right (the game mirrors it for left-hand traffic).
    public enum LaneDirection { Forward, Backward }

    public enum MedianStyle { Painted, Grass, Trees }

    public class RoadPiece
    {
        public PieceKind Kind;
        public float Width;
        public LaneDirection Direction;
        public MedianStyle Median;

        public RoadPiece() { }

        public RoadPiece(PieceKind kind, LaneDirection direction = LaneDirection.Forward, float width = 0f)
        {
            Kind = kind;
            Direction = direction;
            Width = width > 0f ? width : DefaultWidth(kind);
        }

        public RoadPiece Copy() => (RoadPiece)MemberwiseClone();

        public bool IsTraffic => Kind == PieceKind.Car || Kind == PieceKind.Bus;
        public bool HasDirection => IsTraffic || Kind == PieceKind.Bike;

        public static float DefaultWidth(PieceKind kind)
        {
            switch (kind)
            {
                case PieceKind.Sidewalk: return 3f;
                case PieceKind.Bike: return 2f;
                case PieceKind.Parking: return 2f;
                case PieceKind.Median: return 2f;
                default: return 3f;
            }
        }

        public static float MinWidth(PieceKind kind)
        {
            switch (kind)
            {
                case PieceKind.Sidewalk: return 2f;
                case PieceKind.Bike: return 1.5f;
                case PieceKind.Parking: return 2f;
                case PieceKind.Median: return 0.5f;
                default: return 2.5f;
            }
        }

        public static float MaxWidth(PieceKind kind)
        {
            switch (kind)
            {
                case PieceKind.Sidewalk: return 6f;
                case PieceKind.Bike: return 3f;
                case PieceKind.Parking: return 3f;
                case PieceKind.Median: return 12f;
                default: return 4f;
            }
        }

        public string Describe()
        {
            string what;
            switch (Kind)
            {
                case PieceKind.Car: what = "car lane"; break;
                case PieceKind.Bus: what = "bus lane"; break;
                case PieceKind.Bike: what = "bike lane"; break;
                case PieceKind.Parking: what = "parking"; break;
                case PieceKind.Median: what = Median.ToString().ToLowerInvariant() + " median"; break;
                default: what = "sidewalk"; break;
            }
            if (HasDirection) what += Direction == LaneDirection.Forward ? " (forward)" : " (backward)";
            return what + ", " + Width.ToString("0.0") + " m";
        }
    }

    public class RoadConfig
    {
        public int Version = 1;
        public string Id = "";
        public long CreatedTicks;   // when it was made (the panel lists newest first)
        public string Name = "";
        public float SpeedKmh = 50f;
        public List<RoadPiece> Pieces = new List<RoadPiece>();

        // The prefab name is what a saved city remembers, so it comes from the
        // id and never changes; the player sees Name.
        public string PrefabName => "RR " + Id;

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        // Same lanes, widths, directions, medians and speed (names may differ).
        public bool SameLayout(RoadConfig other)
        {
            if (other == null || other.Pieces.Count != Pieces.Count || Math.Abs(other.SpeedKmh - SpeedKmh) > 0.01f) return false;
            for (int i = 0; i < Pieces.Count; i++)
            {
                var a = Pieces[i];
                var b = other.Pieces[i];
                if (a.Kind != b.Kind || Math.Abs(a.Width - b.Width) > 0.01f) return false;
                if (a.HasDirection && a.Direction != b.Direction) return false;
                if (a.Kind == PieceKind.Median && a.Median != b.Median) return false;
            }
            return true;
        }

        public RoadConfig Copy()
        {
            var copy = (RoadConfig)MemberwiseClone();
            copy.Pieces = new List<RoadPiece>();
            foreach (var piece in Pieces) copy.Pieces.Add(piece.Copy());
            return copy;
        }

        // Starting points for the panel.
        public static List<RoadConfig> Presets()
        {
            const LaneDirection f = LaneDirection.Forward, b = LaneDirection.Backward;
            var list = new List<RoadConfig>();
            list.Add(Make("2-lane street", 50f,
                new RoadPiece(PieceKind.Sidewalk), new RoadPiece(PieceKind.Parking, b), new RoadPiece(PieceKind.Car, b),
                new RoadPiece(PieceKind.Car, f), new RoadPiece(PieceKind.Parking, f), new RoadPiece(PieceKind.Sidewalk)));
            var grass = new RoadPiece(PieceKind.Median, f, 3f) { Median = MedianStyle.Grass };
            list.Add(Make("4-lane with median", 60f,
                new RoadPiece(PieceKind.Sidewalk), new RoadPiece(PieceKind.Car, b), new RoadPiece(PieceKind.Car, b), grass,
                new RoadPiece(PieceKind.Car, f), new RoadPiece(PieceKind.Car, f), new RoadPiece(PieceKind.Sidewalk)));
            var trees = new RoadPiece(PieceKind.Median, f, 3f) { Median = MedianStyle.Trees };
            list.Add(Make("6-lane avenue with bus lanes", 60f,
                new RoadPiece(PieceKind.Sidewalk), new RoadPiece(PieceKind.Bus, b), new RoadPiece(PieceKind.Car, b),
                new RoadPiece(PieceKind.Car, b), trees, new RoadPiece(PieceKind.Car, f), new RoadPiece(PieceKind.Car, f),
                new RoadPiece(PieceKind.Bus, f), new RoadPiece(PieceKind.Sidewalk)));
            list.Add(Make("One-way with bike lane", 50f,
                new RoadPiece(PieceKind.Sidewalk), new RoadPiece(PieceKind.Car, f), new RoadPiece(PieceKind.Car, f),
                new RoadPiece(PieceKind.Bike, f), new RoadPiece(PieceKind.Sidewalk)));
            return list;
        }

        private static RoadConfig Make(string name, float speed, params RoadPiece[] pieces)
        {
            return new RoadConfig { Name = name, SpeedKmh = speed, Pieces = new List<RoadPiece>(pieces) };
        }
    }

    // One lane of the composed road, across the road from its centre line
    // (negative = left).
    public class LaneSpec
    {
        public PieceKind Kind;
        public float Position;
        public float Width;
        public LaneDirection Direction;
        public bool Outer;          // outermost car/bus lane going this way (bus stops, turning props)
        public MedianStyle Median;
    }

    public class CrossSection
    {
        public const float MaxSpeedKmh = 130f;
        public const float MinSpeedKmh = 10f;
        public const int MaxLanesPerDirection = 6;

        public float HalfWidth;
        public float PavementWidth;            // each sidewalk (0 = none)
        public float InnerHalfWidth => HalfWidth - PavementWidth;
        public int ForwardLanes, BackwardLanes; // cars and buses
        public bool HasMedian;
        public MedianStyle MedianStyle;
        public string BaseName = "";
        public List<LaneSpec> Lanes = new List<LaneSpec>();
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();

        public bool Ok => Errors.Count == 0;
        public bool OneWay => ForwardLanes == 0 || BackwardLanes == 0;

        public static CrossSection Compose(RoadConfig config)
        {
            var cs = new CrossSection();
            var pieces = config != null && config.Pieces != null ? config.Pieces : new List<RoadPiece>();
            int n = pieces.Count;
            if (n == 0)
            {
                cs.Errors.Add("Add some lanes");
                return cs;
            }

            float total = 0f;
            for (int i = 0; i < n; i++)
            {
                var p = pieces[i];
                if (p.Width < RoadPiece.MinWidth(p.Kind) - 0.001f || p.Width > RoadPiece.MaxWidth(p.Kind) + 0.001f)
                    cs.Errors.Add("A " + p.Describe() + " is outside " + RoadPiece.MinWidth(p.Kind).ToString("0.0") + "-" +
                                  RoadPiece.MaxWidth(p.Kind).ToString("0.0") + " m");
                total += p.Width;
            }
            cs.HalfWidth = total * 0.5f;

            // Sidewalks: both outer pieces, same width, or none. The road mesh
            // comes from a vanilla road with equal sidewalks.
            int sidewalks = 0;
            for (int i = 0; i < n; i++)
            {
                if (pieces[i].Kind != PieceKind.Sidewalk) continue;
                sidewalks++;
                if (i != 0 && i != n - 1) cs.Errors.Add("Sidewalks go on the outside of the road");
            }
            if (sidewalks == 2 && n >= 2 && pieces[0].Kind == PieceKind.Sidewalk && pieces[n - 1].Kind == PieceKind.Sidewalk)
            {
                if (Math.Abs(pieces[0].Width - pieces[n - 1].Width) > 0.01f)
                    cs.Errors.Add("Both sidewalks need the same width (for now)");
                cs.PavementWidth = Math.Max(pieces[0].Width, pieces[n - 1].Width);
            }
            else if (sidewalks != 0)
            {
                cs.Errors.Add("Sidewalks on both sides or none");
            }

            // Lanes, left to right.
            float x = -cs.HalfWidth;
            bool forwardSeen = false;
            bool misordered = false;
            for (int i = 0; i < n; i++)
            {
                var p = pieces[i];
                float centre = x + p.Width * 0.5f;
                x += p.Width;

                if (p.Kind == PieceKind.Median)
                {
                    if (i == 0 || i == n - 1 || pieces[i - 1].Kind == PieceKind.Sidewalk || pieces[i + 1].Kind == PieceKind.Sidewalk)
                        cs.Errors.Add("A median goes between lanes");
                    // The raised median comes from the vanilla road's mesh, which has
                    // one, in the middle (until R3.3 draws our own).
                    if (cs.HasMedian) cs.Errors.Add("One median per road (for now)");
                    else if (p.Median != MedianStyle.Painted && Math.Abs(centre) > 0.25f)
                        cs.Errors.Add("A grass or tree median must be in the middle of the road (for now): " +
                                      "put the same width of lanes on both sides");
                    cs.HasMedian = true;
                    cs.MedianStyle = p.Median;
                    if (p.Median == MedianStyle.Trees)
                        cs.Lanes.Add(new LaneSpec { Kind = PieceKind.Median, Position = centre, Width = p.Width, Median = p.Median });
                    continue;
                }

                var direction = p.Direction;
                if (p.Kind == PieceKind.Parking || p.Kind == PieceKind.Sidewalk)
                    direction = centre >= 0f ? LaneDirection.Forward : LaneDirection.Backward;
                if (p.HasDirection)
                {
                    if (direction == LaneDirection.Forward) forwardSeen = true;
                    else if (forwardSeen) misordered = true;
                }
                if (p.IsTraffic)
                {
                    if (direction == LaneDirection.Forward) cs.ForwardLanes++;
                    else cs.BackwardLanes++;
                }
                cs.Lanes.Add(new LaneSpec { Kind = p.Kind, Position = centre, Width = p.Width, Direction = direction });
            }

            if (misordered)
                cs.Errors.Add("Lanes going the same way must be together: backward (<) on the left, forward (>) on the right");
            if (cs.ForwardLanes + cs.BackwardLanes == 0)
                cs.Errors.Add("Add at least one car or bus lane");
            if (cs.ForwardLanes > MaxLanesPerDirection || cs.BackwardLanes > MaxLanesPerDirection)
                cs.Errors.Add("At most " + MaxLanesPerDirection + " lanes each way");
            if (cs.OneWay && cs.HasMedian && cs.MedianStyle != MedianStyle.Painted)
                cs.Errors.Add("One-way roads can't have a grass or tree median (for now): the one-way roads it is built from have none");
            if (cs.PavementWidth <= 0f && !cs.OneWay)
                cs.Errors.Add("Two-way roads need sidewalks (for now)");

            MarkOuterLanes(cs);
            CheckNeighbours(cs, pieces);
            cs.BaseName = ChooseBase(cs);
            if (config != null && (config.SpeedKmh < MinSpeedKmh || config.SpeedKmh > MaxSpeedKmh))
                cs.Warnings.Add("Speed limit kept between " + MinSpeedKmh + " and " + MaxSpeedKmh + " km/h");
            return cs;
        }

        // The outermost car or bus lane each way is the one by the curb: bus
        // stops and turn arrows come from the vanilla road's outer lane.
        private static void MarkOuterLanes(CrossSection cs)
        {
            LaneSpec right = null, left = null;
            foreach (var lane in cs.Lanes)
            {
                if (lane.Kind != PieceKind.Car && lane.Kind != PieceKind.Bus) continue;
                if (lane.Direction == LaneDirection.Forward) { if (right == null || lane.Position > right.Position) right = lane; }
                else if (left == null || lane.Position < left.Position) left = lane;
            }
            if (right != null) right.Outer = true;
            if (left != null) left.Outer = true;
        }

        private static void CheckNeighbours(CrossSection cs, List<RoadPiece> pieces)
        {
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                bool bySidewalk = (i > 0 && pieces[i - 1].Kind == PieceKind.Sidewalk) ||
                                  (i < pieces.Count - 1 && pieces[i + 1].Kind == PieceKind.Sidewalk);
                if (p.Kind == PieceKind.Parking && !bySidewalk)
                    cs.Warnings.Add("Parking works best next to a sidewalk");
                if (p.Kind == PieceKind.Bus && !bySidewalk && cs.PavementWidth > 0f)
                {
                    bool byCurbLane = (i > 0 && (pieces[i - 1].Kind == PieceKind.Parking || pieces[i - 1].Kind == PieceKind.Bike)) ||
                                      (i < pieces.Count - 1 && (pieces[i + 1].Kind == PieceKind.Parking || pieces[i + 1].Kind == PieceKind.Bike));
                    if (!byCurbLane) cs.Warnings.Add("Buses only stop in a bus lane next to the sidewalk");
                }
            }
        }

        // The vanilla road the new one is cloned from: same lane count and
        // median where possible, so the borrowed markings are close.
        private static string ChooseBase(CrossSection cs)
        {
            int traffic = cs.ForwardLanes + cs.BackwardLanes;
            if (cs.PavementWidth <= 0f) return "Highway";
            if (cs.OneWay) return traffic <= 2 ? "Oneway Road" : "Large Oneway";
            bool raisedMedian = cs.HasMedian && cs.MedianStyle != MedianStyle.Painted;
            // The two-lane road's decorated versions put grass and trees on the
            // sidewalks, not in the middle: a median needs at least the four-lane road.
            string size = traffic <= 2 && !raisedMedian ? "Basic Road" : traffic <= 4 ? "Medium Road" : "Large Road";
            if (!raisedMedian) return size;
            return size + (cs.MedianStyle == MedianStyle.Trees ? " Decoration Trees" : " Decoration Grass");
        }

        // Names to try for the base road, best first.
        public List<string> BaseCandidates(bool includeLastResort)
        {
            var list = new List<string> { BaseName };
            int cut = BaseName.IndexOf(" Decoration", StringComparison.Ordinal);
            if (cut > 0) list.Add(BaseName.Substring(0, cut));
            if (includeLastResort)
            {
                string fallback = PavementWidth > 0f ? "Basic Road" : "Highway";
                if (!list.Contains(fallback)) list.Add(fallback);
            }
            return list;
        }

        // Moves a vertex of the base road's mesh across the road (x in metres
        // from the centre) so the asphalt stretches to the new width while the
        // sidewalks keep their shape and are only scaled to the new sidewalk
        // width. Anything outside the road edge (barriers, tunnel walls) moves out
        // with the edge.
        public static float StretchX(float x, float baseInner, float basePave, float newInner, float newPave)
        {
            float sign = x < 0f ? -1f : 1f;
            float a = Math.Abs(x);
            float baseHalf = baseInner + basePave, newHalf = newInner + newPave;
            float result;
            if (a <= baseInner)
                result = baseInner > 0.01f ? a * newInner / baseInner : a;
            else if (a <= baseHalf)
                result = newInner + (basePave > 0.01f ? (a - baseInner) * newPave / basePave : 0f);
            else
                result = a + (newHalf - baseHalf);
            return sign * result;
        }
    }
}
