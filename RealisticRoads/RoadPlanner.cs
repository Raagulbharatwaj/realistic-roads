using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RealisticRoads
{
    // What a node of the new road is built as.
    public enum NodeKind
    {
        Ground,
        Elevated,   // above land: elevated version, pillars
        Bridge,     // above water, or over a lowered road: bridge version
        Cut,        // below the ground in an open cutting: normal version, the game digs the terrain
        Slope,      // (not used for nodes any more: the one segment from a cutting into a tunnel is the slope version)
        Tunnel,     // deep underground: tunnel version
    }

    // A stretch the player marked in the side view: kept up on pillars or down
    // in a tunnel, whatever the ground does (None: the planner decides).
    public enum Forced : byte { None, Elevated, Tunnel }

    // A stretch of water the curve crosses, with the player's extra height.
    public class WaterRun
    {
        public float From, To;      // metres along the curve
        public float Extra;         // +/- from the player
        public bool Tunnel;         // crossed in a tunnel under the bed, not on a bridge
        public int Key;             // stable id for remembering Extra while the curve changes
        public Vector3 Middle;
    }

    // An existing road lowered into a cutting for an overpass.
    public class Lowering
    {
        public ushort Segment;
        public float[] SplitAt;            // positions along the segment for new nodes (0..1)
        public float[] SplitHeights;       // their lowered heights
        public List<ushort> Nodes = new List<ushort>();       // existing nodes to move down
        public List<float> NodeHeights = new List<float>();
    }

    // Everything needed to build a curve: node positions and heights, what
    // each node is, the crossings and what happens at them, and any problems.
    public class Plan
    {
        public List<Vector3> Points;          // y = planned height
        public List<Vector3> Tangents;        // direction leaving each node
        // Direction arriving at each node: the same as Tangents except at a
        // sharp corner (two straight pieces meeting at an angle). May be null.
        public List<Vector3> InTangents;
        public Vector3 InTangent(int i) => InTangents != null && i < InTangents.Count ? InTangents[i] : Tangents[i];
        public NodeKind[] Kinds;
        // A deck over roads: what is under each node (Segment 0: nothing, or
        // not on a deck), for its pillar or frame. Null if no deck.
        public DeckBelow[] Decks;
        public bool[] OnDeck;
        public bool[] UnderDeck;              // of those, the nodes of a deck under the road (a tunnel)
        public bool[] Middle;                 // suspension bridges: the node between two towers
        public bool[] Held;                   // height set by an end or a crossing (not draggable in the side view)
        public List<Crossing> Crossings = new List<Crossing>();
        public List<int> CrossingIndex = new List<int>();   // node index of each crossing
        public Dictionary<int, Lowering> Lowerings = new Dictionary<int, Lowering>();  // by crossing number
        public List<WaterRun> WaterRuns = new List<WaterRun>();
        public List<string> Problems = new List<string>();
        public List<string> Warnings = new List<string>();   // shown, but don't stop building
        public bool[] Steep;                  // piece i (between points i-1 and i) steeper than the road allows
        public ushort StartNode, EndNode;
        // An end placed in the middle of an existing road: that road is cut
        // there at build time and the new road joins it (a T-junction).
        public ushort StartSegment, EndSegment;
        public float StartT, EndT;
        public bool Ok => Problems.Count == 0;
        public bool GradeProblem;             // it doesn't fit at its grade (a steeper one may do)
        public float Grade;                   // the grade it was planned at
    }

    // Main thread: works out heights and road versions for a curve.
    public static class RoadPlanner
    {
        // Ramp grade for the road being planned: 4 % for roads, 3.5 % for rail
        // (trains can't climb road grades). Set by Make; one plan at a time per thread.
        public const float RoadGrade = 0.04f;
        public const float RailGrade = 0.035f;
        // Where a road doesn't fit at its usual grade it may go up to this
        // (any road, track or path, and a road or rail lowered for an
        // overpass), with a warning. Rail steeper than RailWarnGrade is warned.
        public const float SteepGrade = 0.045f;
        public const float RailWarnGrade = 0.02f;
        // A slip ramp (Ramp mode, roads only): cars take it slowly, so it may be
        // steeper, and shorter.
        public const float RampGrade = 0.065f;
        public const float RampSteepGrade = 0.07f;
        // Set by the tool (main thread) while it plans a ramp.
        [ThreadStatic] public static bool Ramp;
        [ThreadStatic] private static float maxGrade;
        [ThreadStatic] private static bool steepGrades;
        public static float MaxGrade => maxGrade > 0f ? maxGrade : RoadGrade;
        private const float MaxDeckHeight = 60f;      // higher than this above the ground: warned
        private const float EmbankmentHeight = 3f;    // up to this a ramp stays the ground version on an embankment
        public const float BridgeClearance = 10f;     // above water (before the player's +/-)
        public const float OverClearance = 9f;        // flyover deck above the road/rail below
        public const float ShipClearance = 20f;       // deck above a ship or ferry route (on the water)
        public const float UnderClearance = 7f;       // headroom under an elevated road we pass beneath
        public const float TunnelDepth = 12f;         // tunnel under the existing road
        public const float UnderwaterCover = 10f;     // a tunnel under water: at least this far under the bed
        public const float CutDepth = 8f;             // open cutting under a short bridge
        // Like the vanilla tool: shallower than this the road stays the normal
        // version in an open cutting; deeper it is a tunnel. The one segment
        // between the two is the slope version, which has the portal.
        public const float TunnelMinDepth = 8f;
        public const float ForcedHeight = OverClearance;   // an Elevated stretch: this high above the ground (or water)
        public const float ForcedDepth = TunnelDepth;      // a Tunnel stretch: this deep under it
        private const float Unlimited = 100000f;
        private const float RunTolerance = 3f;        // node distances are chords, water runs follow the curve
        private const float DefaultMaxSlope = 0.25f;  // roads without their own limit
        private const float RimReach = 400f;          // how far a gorge's other side may be
        private const float MaxEmbankment = 6f;       // plain road raised or lowered at most this much to ease a slope
        private const int ProbesPerPiece = 4;         // underground check points per lowered piece
        private static string lastLoweringFailure;
        private const float LiftSnap = 6f;            // a point's height applies to the node this close to it

        public static Plan Make(NetInfo info, List<Vector3> points, List<Vector3> tangents,
            List<Crossing> crossings, List<int> crossingIndex, List<WaterRun> waterRuns, ushort startNode, ushort endNode,
            ushort startSegment = 0, float startT = 0f, ushort endSegment = 0, float endT = 0f,
            List<KeyValuePair<Vector3, float>> lifts = null, List<Vector3> inTangents = null,
            bool[] onDeck = null, DeckBelow[] decks = null, bool[] underDeck = null,
            List<KeyValuePair<Vector3, float>> dragged = null, Forced[] forced = null)
        {
            // At the usual grades; if it only fails for want of room to ramp,
            // again at up to SteepGrade.
            steepGrades = false;
            var plan = MakeAt(info, points, tangents, crossings, crossingIndex, waterRuns, startNode, endNode, startSegment, startT,
                endSegment, endT, lifts, inTangents, onDeck, decks, underDeck, dragged, forced);
            if (!plan.Ok && plan.GradeProblem)
            {
                steepGrades = true;
                var steep = MakeAt(info, points, tangents, crossings, crossingIndex, waterRuns, startNode, endNode, startSegment, startT,
                    endSegment, endT, lifts, inTangents, onDeck, decks, underDeck, dragged, forced);
                steepGrades = false;
                if (steep.Ok)
                {
                    steep.Warnings.Insert(0, Ramp && !IsRail(info)
                        ? "No room for the ramp at " + Percent(RampGrade) + ": up to " + Percent(RampSteepGrade)
                        : "No room at the usual grades (roads " + Percent(RoadGrade) + ", rail " + Percent(RailGrade) +
                          ", rail lowered under it " + Percent(LoweredRailGrade) + "): ramps up to " + Percent(SteepGrade));
                    plan = steep;
                }
            }
            maxGrade = plan.Grade;
            // Rail steeper than RailWarnGrade anywhere: said, so it's a choice.
            if (IsRail(info) && plan.Points != null)
            {
                float steepest = 0f;
                for (int i = 1; i < plan.Points.Count; i++)
                {
                    float run = CurveMath.FlatDistance(plan.Points[i - 1], plan.Points[i]);
                    if (run > 1f) steepest = Mathf.Max(steepest, Mathf.Abs(plan.Points[i].y - plan.Points[i - 1].y) / run);
                }
                if (steepest > RailWarnGrade + 0.001f)
                    plan.Warnings.Add("Rail steeper than " + Percent(RailWarnGrade) + " in places (up to " + Percent(steepest) +
                                      "): heavy trains climb it slowly");
            }
            return plan;
        }

        private static string Percent(float grade)
        {
            return (grade * 100f).ToString("0.#") + " %";
        }

        private static Plan MakeAt(NetInfo info, List<Vector3> points, List<Vector3> tangents,
            List<Crossing> crossings, List<int> crossingIndex, List<WaterRun> waterRuns, ushort startNode, ushort endNode,
            ushort startSegment, float startT, ushort endSegment, float endT,
            List<KeyValuePair<Vector3, float>> lifts, List<Vector3> inTangents,
            bool[] onDeck, DeckBelow[] decks, bool[] underDeck,
            List<KeyValuePair<Vector3, float>> dragged, Forced[] forced)
        {
            var plan = new Plan
            {
                Points = new List<Vector3>(points),
                Tangents = new List<Vector3>(tangents),
                InTangents = inTangents != null ? new List<Vector3>(inTangents) : null,
                OnDeck = onDeck != null ? (bool[])onDeck.Clone() : null,
                UnderDeck = underDeck != null ? (bool[])underDeck.Clone() : null,
                Decks = decks != null ? (DeckBelow[])decks.Clone() : null,
                Crossings = crossings,
                CrossingIndex = crossingIndex,
                WaterRuns = waterRuns,
                StartNode = startNode,
                EndNode = endNode,
                StartSegment = startSegment,
                StartT = startT,
                EndSegment = endSegment,
                EndT = endT,
            };
            int n = points.Count;
            plan.Kinds = new NodeKind[n];
            plan.Middle = new bool[n];
            if (n < 2 || info == null) return plan;

            var terrain = TerrainManager.instance;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            var distance = new float[n];
            var ground = new float[n];
            var water = new bool[n];
            var minH = new float[n];
            var maxH = new float[n];
            var isFixed = new bool[n];
            var cutting = new bool[n];      // lowered for an underpass: never a tunnel
            var joins = new bool[n];        // a junction / level crossing at ground level: always the ground version
            var forceBridge = new bool[n];  // deck over a lowered road (overpass)
            var underWater = new bool[n];   // in a water crossing chosen as a tunnel

            bool ramp = Ramp && !IsRail(info);
            maxGrade = ramp ? (steepGrades ? RampSteepGrade : RampGrade) : steepGrades ? SteepGrade : GradeFor(info);
            plan.Grade = maxGrade;
            // Raised parts use the elevated version, else the bridge one (InfoFor).
            bool hasBridge = InfoFor(info, NodeKind.Bridge) != null && InfoFor(info, NodeKind.Bridge) != info;
            bool hasElevated = hasBridge;
            bool crossesWater = false;

            for (int i = 0; i < n; i++)
            {
                if (i > 0) distance[i] = distance[i - 1] + CurveMath.FlatDistance(points[i - 1], points[i]);
                ground[i] = terrain.SampleRawHeightSmooth(points[i]);
                float surface = terrain.SampleRawHeightSmoothWithWater(points[i], false, 0f);
                water[i] = surface - ground[i] > 0.5f;
                // A deck under the road never bridges water: it stays a tunnel (and
                // the underground check says if there is water in the way).
                bool tunnelDeck = underDeck != null && i < underDeck.Length && underDeck[i];
                // The nodes either side of a river narrower than the node spacing
                // stand on the banks, dry: the traced water run says they span water.
                var run = RunAt(waterRuns, distance[i], RunTolerance);
                // Under the water in a tunnel: at least UnderwaterCover under the
                // bed (the ground under the water), never a bridge's height.
                underWater[i] = run != null && run.Tunnel;
                minH[i] = water[i] && hasBridge && !tunnelDeck && !underWater[i]
                    ? surface + BridgeClearance + WaterExtra(waterRuns, distance[i]) : -Unlimited;
                maxH[i] = underWater[i] ? ground[i] - UnderwaterCover : Unlimited;
                if (run != null && !water[i])
                {
                    water[i] = true;
                    if (hasBridge && !tunnelDeck && !underWater[i])
                    {
                        float runSurface = terrain.SampleRawHeightSmoothWithWater(run.Middle, false, 0f);
                        minH[i] = Mathf.Max(minH[i], runSurface + BridgeClearance + run.Extra);
                    }
                }
            }

            // A deck stays clear of the road under it (or of the ground) all along.
            // Each node's floor is the highest of its own and its deck neighbours':
            // a node just off the middle of the deck below (or past its end) would
            // otherwise measure from the road under that, and the deck would dip and rise.
            // Applied below, once the held heights are known: near a held end (a
            // deck starting on the road it rises from) it ramps at the grade.
            var deckFloor = new float[n];
            var deckCeiling = new float[n];
            for (int i = 0; i < n; i++)
            {
                deckFloor[i] = float.MinValue;
                deckCeiling[i] = float.MaxValue;
            }
            if (onDeck != null && decks != null)
            {
                Func<int, bool> isUnder = i => underDeck != null && i < underDeck.Length && underDeck[i];
                Func<int, bool> isAbove = i => i >= 0 && i < n && i < onDeck.Length && onDeck[i] && !isUnder(i);
                var floor = new float[n];
                for (int i = 0; i < n && i < onDeck.Length && i < decks.Length; i++)
                    if (isAbove(i)) floor[i] = Decks.Floor(points[i], decks[i]);
                bool tooDeep = false;
                for (int i = 0; i < n && i < onDeck.Length && i < decks.Length; i++)
                {
                    if (isUnder(i))
                    {
                        // Under the road: at least one level (a tunnel's depth) below it;
                        // the heights stay as shallow as that and the grade allow.
                        deckCeiling[i] = Decks.Ceiling(points[i], decks[i]);
                        if (decks[i].Segment != 0 && !decks[i].OnGround) tooDeep = true;
                        continue;
                    }
                    if (!isAbove(i)) continue;
                    float level = floor[i];
                    if (isAbove(i - 1)) level = Mathf.Max(level, floor[i - 1]);
                    if (isAbove(i + 1)) level = Mathf.Max(level, floor[i + 1]);
                    deckFloor[i] = level;
                }
                if (tooDeep)
                    plan.Problems.Add("Under a road there is one level only: this road is already underground (or a deck goes under it). " +
                                      "Draw the tunnel under a road on the ground");
            }

            bool shaped = dragged != null && dragged.Count > 0;
            if (NetworkBuilder.NodeExists(startNode)) Fix(0, nodes[startNode].m_position.y, minH, maxH, isFixed);
            if (NetworkBuilder.NodeExists(endNode)) Fix(n - 1, nodes[endNode].m_position.y, minH, maxH, isFixed);
            // Ends on the middle of a road join it at its height.
            if (startSegment != 0) Fix(0, points[0].y, minH, maxH, isFixed);
            if (endSegment != 0) Fix(n - 1, points[n - 1].y, minH, maxH, isFixed);
            // The ends (and below, junctions and overpasses) are held where they
            // are: the side view shows them as not draggable.
            plan.Held = (bool[])isFixed.Clone();
            // Heights dragged in the side view: the node there is held exactly
            // at that height (before the points' own heights, so a drag wins).
            if (dragged != null)
                foreach (var height in dragged)
                {
                    int best = NodeNear(points, height.Key, isFixed);
                    if (best >= 0) Fix(best, height.Value, minH, maxH, isFixed);
                }
            // Heights the player gave points (Page Up / Page Down): the node there
            // is held that far above the ground (or the water); the road ramps
            // to it and away from it at the grade. Crossings below may still
            // override it (a junction stays at the other road's height).
            if (lifts != null)
                foreach (var lift in lifts)
                {
                    int best = NodeNear(points, lift.Key, isFixed);
                    if (best < 0) continue;
                    float level = water[best] ? terrain.SampleRawHeightSmoothWithWater(points[best], false, 0f) : ground[best];
                    Fix(best, level + lift.Value, minH, maxH, isFixed);
                }
            foreach (var crossing in crossings)
                if (crossing.Segment != 0 && (crossing.Segment == startSegment || crossing.Segment == endSegment))
                {
                    plan.Problems.Add("The curve starts or ends on the " + crossing.ExistingName +
                                      " and crosses it again: start from its end instead, or move the point");
                    break;
                }

            var overpasses = new List<int>();
            for (int c = 0; c < crossings.Count; c++)
            {
                var crossing = crossings[c];
                int k = crossingIndex[c];
                // Crossing at an angle, the other road is wider measured along ours.
                crossing.Skew = Skew(crossing, plan.Tangents[k]);
                float window = (crossing.ExistingHalfWidth + info.m_halfWidth + 2f) / crossing.Skew;
                switch (crossing.Type)
                {
                    case CrossingType.Junction:
                        if (crossing.ExistingAtGround)
                        {
                            Fix(k, crossing.Point.y, minH, maxH, isFixed);
                            joins[k] = true;
                            plan.Held[k] = true;
                        }
                        // Passing under an elevated road or over a sunken one at ground
                        // level: keep the headroom, so no embankment lifts us into the
                        // deck above (or a cutting digs us into the road below).
                        else if (crossing.ExistingElevation > 0f)
                            Limit(distance, k, window, crossing.Point.y - UnderClearance, false, maxH, false);
                        else
                            Limit(distance, k, window, crossing.Point.y + CutDepth, true, minH, false);
                        break;
                    case CrossingType.Bridge:
                        // Level at full clearance up to the deck's ends either side, and
                        // the nodes just past them (the node on the crossing itself is
                        // dropped later, so the side nodes carry the height and the deck
                        // between them never sags below it).
                        // Over rail on the ground the whole deck is higher, so the
                        // overhead wires pass under it.
                        // Over a ship or ferry route the deck leaves room for the ships.
                        Limit(distance, k, DeckHalf(crossing, info, false) + 1f,
                            crossing.Point.y + (crossing.ShipPath ? ShipClearance : OverClearance) + crossing.ExtraHeight +
                            (crossing.OverRail ? RailDeckLift : 0f), true, minH, true, true);
                        break;
                    case CrossingType.Overpass:
                        // We stay at ground level on a bridge deck; the existing road goes down.
                        Fix(k, crossing.Point.y, minH, maxH, isFixed);
                        plan.Held[k] = true;
                        // The deck spans the lowered road and its cutting's banks.
                        float deckHalf = DeckHalf(crossing, info, true) + 1f;
                        Mark(distance, k, deckHalf, forceBridge, false);
                        // The node on the crossing is dropped later; the deck between
                        // the side nodes must not sag below the height the lowering
                        // was planned for, or the headroom underneath shrinks.
                        for (int i = 0; i < n; i++)
                            if (i != k && Mathf.Abs(distance[i] - distance[k]) <= deckHalf) minH[i] = Mathf.Max(minH[i], crossing.Point.y);
                        overpasses.Add(c);
                        break;
                    case CrossingType.Underpass:
                        Limit(distance, k, window, crossing.Point.y - CutDepth, false, maxH, false);
                        Mark(distance, k, window * 4f, cutting, true);
                        break;
                    case CrossingType.Tunnel:
                        bool underElevated = crossing.ExistingElevation > UnderClearance;
                        // A tunnel must reach past both sides of the road above, so the
                        // nodes either side are kept down too (not only the window).
                        Limit(distance, k, window, crossing.Point.y - (underElevated ? UnderClearance : TunnelDepth), false, maxH, true);
                        break;
                }
            }

            // The deck's levels, as far as the grade reaches from the held heights.
            for (int i = 0; i < n; i++)
            {
                if (isFixed[i] || (deckFloor[i] == float.MinValue && deckCeiling[i] == float.MaxValue)) continue;
                float reachUp = Unlimited, reachDown = -Unlimited;
                for (int j = 0; j < n; j++)
                {
                    if (!isFixed[j]) continue;
                    float d = Mathf.Abs(distance[i] - distance[j]) * MaxGrade;
                    reachUp = Mathf.Min(reachUp, minH[j] + d);
                    reachDown = Mathf.Max(reachDown, maxH[j] - d);
                }
                if (deckFloor[i] != float.MinValue) minH[i] = Mathf.Max(minH[i], Mathf.Min(deckFloor[i], reachUp));
                if (deckCeiling[i] != float.MaxValue) maxH[i] = Mathf.Min(maxH[i], Mathf.Max(deckCeiling[i], reachDown));
            }

            // Stretches marked Elevated or Tunnel in the side view: held up at a
            // flyover's clearance (the bridge's over water) or down at a tunnel's
            // depth (under the bed in water). Near a node whose height is held
            // (an end on a road, a junction, a dragged height) the stretch ramps
            // to it at the grade, so a whole road marked Elevated still starts and
            // ends where it joins. Deck nodes keep their own levels.
            if (forced != null)
                for (int i = 0; i < n && i < forced.Length; i++)
                {
                    if (forced[i] == Forced.None || isFixed[i] || (onDeck != null && i < onDeck.Length && onDeck[i])) continue;
                    shaped = true;
                    float reachUp = Unlimited, reachDown = -Unlimited;
                    for (int j = 0; j < n; j++)
                    {
                        if (!isFixed[j]) continue;
                        float d = Mathf.Abs(distance[i] - distance[j]) * MaxGrade;
                        reachUp = Mathf.Min(reachUp, minH[j] + d);
                        reachDown = Mathf.Max(reachDown, maxH[j] - d);
                    }
                    if (forced[i] == Forced.Elevated)
                    {
                        float level = Mathf.Max(ground[i], terrain.SampleRawHeightSmoothWithWater(points[i], false, 0f));
                        minH[i] = Mathf.Max(minH[i], Mathf.Min(level + ForcedHeight, reachUp));
                    }
                    else maxH[i] = Mathf.Min(maxH[i], Mathf.Max(ground[i] - (water[i] ? UnderwaterCover : ForcedDepth), reachDown));
                }

            PlanLowerings(plan, info, overpasses);
            CheckLoweringsApart(plan);
            CheckRepeatedCrossings(plan);

            float[] heights;
            bool solved = CurveMath.SolveHeights(distance, ground, minH, maxH, isFixed, MaxGrade, out heights);
            // Dry gorges: a viaduct from rim to rim instead of a road down the cliff.
            float maxSlope = info.m_maxSlope > 0.01f ? info.m_maxSlope : DefaultMaxSlope;
            var valleys = (float[])minH.Clone();
            if (solved && CurveMath.LiftValleys(distance, heights, isFixed, maxSlope, MaxGrade, RimReach, valleys))
            {
                // Not above what a node must stay under (a road or deck over it):
                // that would throw out the viaduct for the whole road.
                for (int i = 0; i < n; i++) valleys[i] = Mathf.Max(minH[i], Mathf.Min(valleys[i], maxH[i]));
                float[] lifted;
                if (CurveMath.SolveHeights(distance, ground, valleys, maxH, isFixed, MaxGrade, out lifted))
                {
                    heights = lifted;
                    minH = valleys;
                }
            }
            // Level the bridges between their banks, then solve again with that.
            var levelled = (float[])minH.Clone();
            if (CurveMath.LevelSpans(distance, ground, heights, isFixed, levelled))
            {
                for (int i = 0; i < n; i++) levelled[i] = Mathf.Max(minH[i], Mathf.Min(levelled[i], maxH[i]));
                float[] again;
                bool solvedAgain = CurveMath.SolveHeights(distance, ground, levelled, maxH, isFixed, MaxGrade, out again);
                if (solvedAgain || !solved)
                {
                    heights = again;
                    solved = solvedAgain;
                }
            }
            if (!solved)
            {
                plan.GradeProblem = true;
                plan.Problems.Add(RampProblem(crossings, crossingIndex, distance, ground, minH, maxH) +
                                  (shaped ? ". Or ease what you set in the side view (right-click a held height to free it; Auto clears a stretch)" : ""));
            }

            // Plain ground: small embankments and cuttings keep the road within its
            // own slope limit; what is still steeper is built anyway and marked.
            var embanked = new bool[n];
            plan.Steep = new bool[n];
            if (solved)
            {
                float steepest = CurveMath.SmoothGrades(distance, ground, heights, isFixed, minH, maxH, maxSlope, MaxEmbankment, embanked);
                for (int i = 1; i < n; i++)
                {
                    float run = Mathf.Max(distance[i] - distance[i - 1], 0.01f);
                    bool plain = !isFixed[i] || !isFixed[i - 1];
                    plan.Steep[i] = plain && Mathf.Abs(heights[i] - heights[i - 1]) / run > maxSlope + 0.001f &&
                                    Mathf.Abs(heights[i] - ground[i]) <= MaxEmbankment + 0.5f &&
                                    Mathf.Abs(heights[i - 1] - ground[i - 1]) <= MaxEmbankment + 0.5f;
                }
                if (steepest > 0f)
                    plan.Warnings.Add("Steeper than this road allows in places (up to " + (steepest * 100f).ToString("F0") + "%, it allows " +
                                      (maxSlope * 100f).ToString("F0") + "%): marked purple, builds anyway");
            }

            for (int i = 0; i < n; i++)
            {
                var p = plan.Points[i];
                p.y = heights[i];
                plan.Points[i] = p;

                float elevation = heights[i] - ground[i];
                if (forceBridge[i] && elevation > -1f) plan.Kinds[i] = NodeKind.Bridge;
                else if (embanked[i] || joins[i]) plan.Kinds[i] = NodeKind.Ground;    // on an embankment / at a level crossing, normal road
                // A little above the ground (a ramp, a track on its ballast) stays
                // the ground version on an embankment; the elevated version only
                // where it must be raised (a bridge, water) or is really high.
                else if (elevation >= 1f && (water[i] || minH[i] > ground[i] + 0.5f || elevation >= EmbankmentHeight))
                    plan.Kinds[i] = water[i] && hasBridge ? NodeKind.Bridge : NodeKind.Elevated;
                else if (elevation >= 1f) plan.Kinds[i] = NodeKind.Ground;
                else if (elevation <= -1f)
                    plan.Kinds[i] = !cutting[i] && elevation <= -TunnelMinDepth ? NodeKind.Tunnel : NodeKind.Cut;
                else plan.Kinds[i] = NodeKind.Ground;
            }

            // A tunnel needs at least two tunnel nodes in a row; a lone deep node
            // would be two portals facing each other, so it stays a cutting.
            for (int i = 0; i < n; i++)
            {
                if (plan.Kinds[i] != NodeKind.Tunnel) continue;
                bool before = i > 0 && plan.Kinds[i - 1] == NodeKind.Tunnel;
                bool after = i < n - 1 && plan.Kinds[i + 1] == NodeKind.Tunnel;
                if (!before && !after) plan.Kinds[i] = NodeKind.Cut;
            }

            // (No suspension-bridge middle nodes: raised parts use elevated pieces.)

            for (int i = 0; i < n; i++) if (water[i]) crossesWater = true;
            if (crossesWater && !hasBridge)
                plan.Problems.Add(info.name + " has no bridge or elevated version, so it can't cross water");
            float highest = 0f;
            for (int i = 0; i < n; i++) highest = Mathf.Max(highest, heights[i] - ground[i]);
            if (highest > MaxDeckHeight)
                plan.Warnings.Add("Up to " + highest.ToString("F0") + " m above the ground: very tall pillars");

            CheckVariants(info, plan, hasElevated);
            CheckUnderground(info, plan, water, ground, underWater);
            CheckCrossings(plan);
            return plan;
        }

        // The road's version for a node kind, or null if it has none.
        private static float GradeFor(NetInfo info)
        {
            return info != null && info.m_class != null && info.m_class.m_service == ItemClass.Service.PublicTransport
                ? RailGrade : RoadGrade;
        }

        // Half the length of our deck over a crossed road or rail, measured along
        // our road: past its edge and ours with room for a pillar, and for an
        // overpass past the banks of the cutting it was lowered into.
        public const float PillarClearance = 4f;

        // Why the heights don't fit, with numbers: the crossing whose ramp
        // doesn't fit between it and an end of the curve, how much road it
        // needs either side and how much there is.
        private static string RampProblem(List<Crossing> crossings, List<int> crossingIndex, float[] distance, float[] ground,
            float[] minH, float[] maxH)
        {
            int n = distance.Length;
            string grade = (MaxGrade * 100f).ToString("0.#") + " %";
            for (int c = 0; c < crossings.Count; c++)
            {
                int k = crossingIndex[c];
                if (k < 0 || k >= n) continue;
                float rise = minH[k] > ground[k] ? minH[k] - ground[k] : maxH[k] < ground[k] ? ground[k] - maxH[k] : 0f;
                if (rise < 1f) continue;
                float need = rise / MaxGrade;
                float before = distance[k], after = distance[n - 1] - distance[k];
                if (before >= need && after >= need) continue;
                return "Not enough room for the " + crossings[c].Describe() + " (" + crossings[c].ExistingName + "): at " + grade +
                       " it needs about " + need.ToString("N0") + " m of road before and after it to go " + rise.ToString("F0") +
                       " m " + (minH[k] > ground[k] ? "up" : "down") + ", and has " + before.ToString("N0") + " m before and " +
                       after.ToString("N0") + " m after. Start or end the curve further away, or pick another crossing type";
            }
            return "Not enough room to ramp up and down at " + grade + " between the crossings: spread the points out, or space the crossings further apart";
        }

        // Rail we build gets long spans: its deck over a crossing is at least
        // this long either side, so no short pieces bunch up there.
        public const float RailDeckHalf = 45f;
        // Over a rail line on the ground our deck is this much higher than the
        // clearance (11 m over the rails), so the overhead wires pass under it.
        public const float RailDeckLift = 2f;

        public static float DeckHalf(Crossing crossing, NetInfo ours, bool overpass)
        {
            float half = SpanHalf(crossing, ours != null ? ours.m_halfWidth : 8f, overpass);
            return IsRail(ours) ? Mathf.Max(half, RailDeckHalf) : half;
        }

        // Train, metro, monorail and tram tracks: not ship, ferry, cable car or
        // other routes the game also files under public transport.
        public static bool IsRail(NetInfo info)
        {
            if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.PublicTransport) return false;
            var sub = info.m_class.m_subService;
            return sub == ItemClass.SubService.PublicTransportTrain || sub == ItemClass.SubService.PublicTransportMetro ||
                   sub == ItemClass.SubService.PublicTransportMonorail || sub == ItemClass.SubService.PublicTransportTram;
        }

        public static float SpanHalf(Crossing crossing, float ourHalfWidth, bool overpass)
        {
            float across = crossing.ExistingHalfWidth + ourHalfWidth + PillarClearance + (overpass ? CutDepth * 1.5f : 0f);
            return across / Mathf.Max(crossing.Skew, 0.35f);
        }

        // Removes node 'index' from a finished plan (CrossingIndex is the
        // caller's to fix).
        public static void RemoveNode(Plan plan, int index)
        {
            plan.Points.RemoveAt(index);
            plan.Tangents.RemoveAt(index);
            if (plan.InTangents != null && index < plan.InTangents.Count) plan.InTangents.RemoveAt(index);
            if (plan.Decks != null && index < plan.Decks.Length) plan.Decks = Without(plan.Decks, index);
            if (plan.OnDeck != null && index < plan.OnDeck.Length) plan.OnDeck = Without(plan.OnDeck, index);
            if (plan.UnderDeck != null && index < plan.UnderDeck.Length) plan.UnderDeck = Without(plan.UnderDeck, index);
            plan.Kinds = Without(plan.Kinds, index);
            if (plan.Steep != null)
            {
                // Piece index+1 now starts at index-1: steep if either half was.
                var steep = new bool[plan.Steep.Length - 1];
                for (int i = 0, j = 0; i < plan.Steep.Length; i++)
                {
                    if (i == index) continue;
                    steep[j] = plan.Steep[i] || (i == index + 1 && plan.Steep[index]);
                    j++;
                }
                plan.Steep = steep;
            }
            if (plan.Middle != null) plan.Middle = Without(plan.Middle, index);
            if (plan.Held != null && index < plan.Held.Length) plan.Held = Without(plan.Held, index);
        }

        private static T[] Without<T>(T[] array, int index)
        {
            var result = new T[array.Length - 1];
            for (int i = 0, j = 0; i < array.Length; i++) if (i != index) result[j++] = array[i];
            return result;
        }

        // Two networks of the same kind (roads and roads, train tracks and
        // train tracks...): they can join, and one can replace the other.
        public static bool SameKind(NetInfo a, NetInfo b)
        {
            return a != null && b != null && a.m_class != null && b.m_class != null &&
                   a.m_class.m_service == b.m_class.m_service && a.m_class.m_subService == b.m_class.m_subService;
        }

        public static NetInfo InfoFor(NetInfo info, NodeKind kind)
        {
            switch (kind)
            {
                // Raised parts always use the elevated version (concrete deck and
                // pillars): bridge versions bring towers, cables and trusses that
                // look wrong at most crossings. A fancy bridge can be drawn over it
                // afterwards with the vanilla tool.
                case NodeKind.Bridge: return Variant(info, "m_elevatedInfo") ?? Variant(info, "m_bridgeInfo");
                case NodeKind.Elevated: return Variant(info, "m_elevatedInfo") ?? Variant(info, "m_bridgeInfo");
                case NodeKind.Slope: return Variant(info, "m_slopeInfo");
                case NodeKind.Tunnel: return Variant(info, "m_tunnelInfo");
                default: return info;
            }
        }

        // The version for a segment between two node kinds.
        public static NetInfo SegmentInfo(NetInfo info, NodeKind a, NodeKind b)
        {
            if (a == NodeKind.Bridge || b == NodeKind.Bridge) return InfoFor(info, NodeKind.Bridge);
            if (a == NodeKind.Elevated || b == NodeKind.Elevated) return InfoFor(info, NodeKind.Elevated);
            if (a == NodeKind.Tunnel && b == NodeKind.Tunnel) return InfoFor(info, NodeKind.Tunnel);
            if (a == NodeKind.Tunnel || b == NodeKind.Tunnel || a == NodeKind.Slope || b == NodeKind.Slope)
                return InfoFor(info, NodeKind.Slope);
            return info;
        }

        // Suspension bridges (the vanilla six-lane road's, for one) are made
        // for a tower on every other node, with a plain "middle" node between
        // two towers where the cables hang lowest. The game's own tool builds
        // them that way; evenly spaced towers on every node look broken.
        public static bool IsDoubleLength(NetInfo info)
        {
            var bridge = Variant(info, "m_bridgeInfo");
            if (bridge == null || bridge.m_netAI == null) return false;
            var f = Field(bridge.m_netAI.GetType(), "m_doubleLength");
            return f != null && f.FieldType == typeof(bool) && (bool)f.GetValue(bridge.m_netAI);
        }

        // Every second node of each bridge run is a middle node; the run's
        // first and last nodes keep their towers.
        private static void MarkMiddles(Plan plan)
        {
            int n = plan.Kinds.Length;
            int i = 0;
            while (i < n)
            {
                if (plan.Kinds[i] != NodeKind.Bridge) { i++; continue; }
                int first = i;
                while (i < n && plan.Kinds[i] == NodeKind.Bridge) i++;
                for (int k = first + 1; k < i - 1; k += 2) plan.Middle[k] = true;
            }
        }

        // A road's other version (m_elevatedInfo, m_bridgeInfo, m_tunnelInfo,
        // m_slopeInfo on roads, tracks and paths), or null.
        public static NetInfo Variant(NetInfo info, string field)
        {
            if (info == null || info.m_netAI == null) return null;
            var f = Field(info.m_netAI.GetType(), field);
            return f != null ? f.GetValue(info.m_netAI) as NetInfo : null;
        }

        // Field lookups by AI type, cached: the preview plans every frame.
        // Locked because the builder asks from the simulation thread.
        private static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();

        private static FieldInfo Field(System.Type type, string name)
        {
            string key = type.FullName + "." + name;
            lock (fields)
            {
                FieldInfo field;
                if (!fields.TryGetValue(key, out field))
                {
                    field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                    fields[key] = field;
                }
                return field;
            }
        }

        private static float WaterExtra(List<WaterRun> runs, float at)
        {
            if (runs == null) return 0f;
            foreach (var run in runs)
                if (at >= run.From - 1f && at <= run.To + 1f) return run.Extra;
            return 0f;
        }

        // --- overpass: lowering the existing road -------------------------------

        // How deep and how steeply an existing road or rail is lowered under an
        // overpass. Rail gets a long, gentle dip, and its middle goes a little
        // deeper so the overhead wires clear our deck.
        public const float LoweredRailGrade = 0.02f;
        public const float WireDepth = 1.5f;
        private const float MinSplit = 8f;   // a cut closer than this to a node uses the node

        // A stretch of an existing road or rail: its nodes in order, with their
        // distance along it; Segments[i] runs from Nodes[i] to Nodes[i + 1].
        private class Line
        {
            public readonly List<ushort> Nodes = new List<ushort>();
            public readonly List<float> Along = new List<float>();
            public readonly List<ushort> Segments = new List<ushort>();
            public bool StartFixed = true, EndFixed = true;   // false at a dead end (free to lower)
        }

        private static Line LineThrough(ushort segment)
        {
            var s = NetManager.instance.m_segments.m_buffer[segment];
            var line = new Line();
            line.Nodes.Add(s.m_startNode);
            line.Nodes.Add(s.m_endNode);
            line.Along.Add(0f);
            line.Along.Add(Mathf.Max(s.m_averageLength, 1f));
            line.Segments.Add(segment);
            return line;
        }

        // Walks the line on past its start (or end) until 'until' is reached,
        // stopping at a junction (the node there stays put) or a dead end.
        private static void Extend(Line line, bool atStart, float until)
        {
            var net = NetManager.instance;
            for (int guard = 0; guard < 64; guard++)
            {
                int last = line.Nodes.Count - 1;
                if (atStart ? line.Along[0] <= until : line.Along[last] >= until) return;
                ushort node = atStart ? line.Nodes[0] : line.Nodes[last];
                ushort from = atStart ? line.Segments[0] : line.Segments[line.Segments.Count - 1];
                int count = NetworkBuilder.SegmentCount(node);
                if (count != 2)
                {
                    if (count == 1) { if (atStart) line.StartFixed = false; else line.EndFixed = false; }
                    return;
                }
                ushort next = 0;
                for (int i = 0; i < 8; i++)
                {
                    ushort segment = net.m_nodes.m_buffer[node].GetSegment(i);
                    if (segment != 0 && segment != from) next = segment;
                }
                if (next == 0 || line.Segments.Contains(next)) return;   // a ring: stop, the node stays
                var s = net.m_segments.m_buffer[next];
                ushort other = s.m_startNode == node ? s.m_endNode : s.m_startNode;
                float length = Mathf.Max(s.m_averageLength, 1f);
                if (atStart)
                {
                    line.Nodes.Insert(0, other);
                    line.Along.Insert(0, line.Along[0] - length);
                    line.Segments.Insert(0, next);
                }
                else
                {
                    line.Nodes.Add(other);
                    line.Along.Add(line.Along[last] + length);
                    line.Segments.Add(next);
                }
            }
        }

        // Where a crossing is along the line (NaN if its segment isn't on it).
        private static float AlongOf(Line line, Crossing crossing)
        {
            int i = line.Segments.IndexOf(crossing.Segment);
            if (i < 0) return float.NaN;
            var s = NetManager.instance.m_segments.m_buffer[crossing.Segment];
            float length = line.Along[i + 1] - line.Along[i];
            float t = s.m_startNode == line.Nodes[i] ? crossing.ExistingT : 1f - crossing.ExistingT;
            return line.Along[i] + t * length;
        }

        // Lowers the existing road or rail under every overpass. Overpasses on
        // the same line whose ramps would meet share one lowered stretch that
        // stays down between them, instead of rising and dipping again. Each
        // crossing gets one new node (none if an existing node is right there).
        private static void PlanLowerings(Plan plan, NetInfo info, List<int> overpasses)
        {
            var done = new HashSet<int>();
            foreach (int first in overpasses)
            {
                if (done.Contains(first)) continue;
                var lead = plan.Crossings[first];
                var existing = NetManager.instance.m_segments.m_buffer[lead.Segment].Info;
                bool rail = IsRail(existing);
                float grade = steepGrades ? SteepGrade : rail ? LoweredRailGrade : RoadGrade;
                float depth = CutDepth + (rail ? WireDepth : 0f);
                float ramp = depth / grade + 10f;

                // The group: overpasses on this line within two ramps of one another.
                var line = LineThrough(lead.Segment);
                var group = new List<int> { first };
                var at = new List<float> { AlongOf(line, lead) };
                for (bool grew = true; grew;)
                {
                    grew = false;
                    float lowest = at[0], highest = at[0];
                    foreach (float a in at) { lowest = Mathf.Min(lowest, a); highest = Mathf.Max(highest, a); }
                    Extend(line, true, lowest - 2f * ramp);
                    Extend(line, false, highest + 2f * ramp);
                    foreach (int other in overpasses)
                    {
                        if (group.Contains(other) || done.Contains(other)) continue;
                        float a = AlongOf(line, plan.Crossings[other]);
                        if (float.IsNaN(a)) continue;
                        foreach (float b in at)
                            if (Mathf.Abs(a - b) < 2f * ramp)
                            {
                                group.Add(other);
                                at.Add(a);
                                grew = true;
                                break;
                            }
                    }
                }
                foreach (int c in group) done.Add(c);

                string problem;
                if (!LowerGroup(plan, info, line, group, at, grade, depth, out problem))
                {
                    if (problem.StartsWith("Not enough room")) plan.GradeProblem = true;
                    plan.Problems.Add(problem.StartsWith("Not enough room")
                        ? problem + " (needs about " + ramp.ToString("F0") + " m of plain " + (rail ? "track" : "road") +
                          " each side at " + (grade * 100f).ToString("0.#") + " %)"
                        : problem);
                }
            }
        }

        private static bool LowerGroup(Plan plan, NetInfo info, Line line, List<int> group, List<float> at,
            float grade, float depth, out string problem)
        {
            problem = null;
            var net = NetManager.instance;
            string name = plan.Crossings[group[0]].ExistingName;

            // Points: the line's nodes, and a cut at each crossing (or the node
            // right there). Each crossing's low point, deck edges and the stretch
            // between crossings in the group are held down.
            var along = new List<float>(line.Along);
            var current = new List<float>();
            foreach (ushort node in line.Nodes) current.Add(net.m_nodes.m_buffer[node].m_position.y);
            var ids = new List<ushort>(line.Nodes);
            var splitOf = new int[group.Count];     // index in the points of each crossing's low point
            var splitT = new float[group.Count];    // where its segment is cut (<0: at a node)
            for (int g = 0; g < group.Count; g++)
            {
                var crossing = plan.Crossings[group[g]];
                float length = Mathf.Max(net.m_segments.m_buffer[crossing.Segment].m_averageLength, 1f);
                int nearest = 0;
                for (int i = 1; i < along.Count; i++) if (Mathf.Abs(along[i] - at[g]) < Mathf.Abs(along[nearest] - at[g])) nearest = i;
                // Never onto a pinned end of the line (a junction, or where the walk
                // stopped): that node stays put, so nothing would be lowered there.
                bool pinned = (nearest == 0 && line.StartFixed) || (nearest == along.Count - 1 && line.EndFixed);
                if (ids[nearest] != 0 && !pinned && Mathf.Abs(along[nearest] - at[g]) < MinSplit)
                {
                    splitOf[g] = nearest;
                    splitT[g] = -1f;
                    continue;
                }
                splitT[g] = Mathf.Clamp(crossing.ExistingT, MinSplit / length, 1f - MinSplit / length);
                // The cut where the segment will really be split: moved off the
                // crossing when it was too close to a node (the clamp above).
                float cutAt = at[g];
                var cutSegment = net.m_segments.m_buffer[crossing.Segment];
                int fromIndex = line.Nodes.IndexOf(cutSegment.m_startNode), toIndex = line.Nodes.IndexOf(cutSegment.m_endNode);
                if (fromIndex >= 0 && toIndex >= 0)
                    cutAt += (line.Along[toIndex] >= line.Along[fromIndex] ? 1f : -1f) * (splitT[g] - crossing.ExistingT) * length;
                int insert = 0;
                while (insert < along.Count && along[insert] < cutAt) insert++;
                along.Insert(insert, cutAt);
                current.Insert(insert, crossing.Point.y);
                ids.Insert(insert, 0);
                for (int h = 0; h < g; h++) if (splitOf[h] >= insert) splitOf[h]++;
                splitOf[g] = insert;
            }

            int m = along.Count;
            var minH = new float[m];
            var maxH = new float[m];
            var isFixed = new bool[m];
            for (int i = 0; i < m; i++)
            {
                minH[i] = -Unlimited;
                maxH[i] = Unlimited;
            }
            var order = new List<int>();
            for (int g = 0; g < group.Count; g++) order.Add(g);
            order.Sort((x, y) => at[x].CompareTo(at[y]));
            for (int o = 0; o < order.Count; o++)
            {
                int g = order[o];
                var crossing = plan.Crossings[group[g]];
                // Under the deck: the cutting's full depth; deeper still at the middle.
                float span = (info.m_halfWidth / Mathf.Max(crossing.Skew, 0.35f)) + 6f;
                for (int i = 0; i < m; i++)
                    if (Mathf.Abs(along[i] - at[g]) <= span) maxH[i] = Mathf.Min(maxH[i], crossing.Point.y - CutDepth);
                // The one cut is the low point; deep enough that the road, climbing
                // from it at the grade, still clears the deck's edge.
                maxH[splitOf[g]] = Mathf.Min(maxH[splitOf[g]], Mathf.Min(crossing.Point.y - depth, crossing.Point.y - CutDepth - grade * span));
                // Between this overpass and the next one: stay down, running evenly.
                if (o + 1 < order.Count)
                {
                    int h = order[o + 1];
                    float lowA = crossing.Point.y - CutDepth, lowB = plan.Crossings[group[h]].Point.y - CutDepth;
                    for (int i = 0; i < m; i++)
                        if (along[i] > at[g] && along[i] < at[h])
                            maxH[i] = Mathf.Min(maxH[i], Mathf.Lerp(lowA, lowB, (along[i] - at[g]) / Mathf.Max(at[h] - at[g], 0.1f)));
                }
            }
            if (line.StartFixed) { minH[0] = maxH[0] = current[0]; isFixed[0] = true; }
            if (line.EndFixed) { minH[m - 1] = maxH[m - 1] = current[m - 1]; isFixed[m - 1] = true; }

            float[] heights;
            if (!CurveMath.SolveHeights(along.ToArray(), current.ToArray(), minH, maxH, isFixed, grade, out heights))
            {
                problem = "Not enough room to lower the " + name +
                          " (a junction is too close, or it climbs or drops too steeply near the crossing; try a bridge, or cross somewhere flatter)";
                var detail = new System.Text.StringBuilder("Overpass: lowering the " + name + " failed, along / now / planned / max:");
                for (int i = 0; i < m; i++)
                    detail.Append("\n  " + (ids[i] != 0 ? "node #" + ids[i] : "cut") + "  " + along[i].ToString("F0") + " m  " +
                                  current[i].ToString("F1") + "  " + heights[i].ToString("F1") + (isFixed[i] ? "  fixed" : "") +
                                  (maxH[i] < Unlimited ? "  max " + maxH[i].ToString("F1") : ""));
                // The preview plans again and again: log each different failure once.
                string text = detail.ToString();
                if (text != lastLoweringFailure) Mod.Log(text);
                lastLoweringFailure = text;
                return false;
            }

            // Every crossing's low point must really be down (a pinned node can't be).
            for (int g = 0; g < group.Count; g++)
                if (heights[splitOf[g]] > plan.Crossings[group[g]].Point.y - CutDepth + 0.1f)
                {
                    problem = "Not enough room to lower the " + name + " (a junction is too close to the crossing)";
                    return false;
                }

            // The first overpass of the group moves the existing nodes; each one cuts its own segment.
            var terrain = TerrainManager.instance;
            var lead = new Lowering();
            for (int i = 0; i < m; i++)
            {
                if (ids[i] == 0 || heights[i] > current[i] - 0.5f) continue;
                Vector3 p = net.m_nodes.m_buffer[ids[i]].m_position;
                if (terrain.SampleRawHeightSmoothWithWater(p, false, 0f) - terrain.SampleRawHeightSmooth(p) > 0.5f)
                {
                    problem = "Can't lower the " + name + ": there is water";
                    return false;
                }
                lead.Nodes.Add(ids[i]);
                lead.NodeHeights.Add(heights[i]);
            }
            for (int g = 0; g < group.Count; g++)
            {
                var lowering = g == 0 ? lead : new Lowering();
                lowering.Segment = plan.Crossings[group[g]].Segment;
                lowering.SplitAt = splitT[g] < 0f ? new float[0] : new[] { splitT[g] };
                lowering.SplitHeights = splitT[g] < 0f ? new float[0] : new[] { heights[splitOf[g]] };
                plan.Lowerings[group[g]] = lowering;
            }
            return true;
        }


        // --- helpers -------------------------------------------------------------

        // Sine of the angle between the two roads at a crossing (1 = square),
        // not below 0.35 so a very shallow crossing doesn't blow up.
        public static float Skew(Crossing crossing, Vector3 ourDirection)
        {
            Vector3 a, b, c, d;
            CrossingFinder.SegmentBezier(crossing.Segment, out a, out b, out c, out d);
            Vector3 theirs = CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, crossing.ExistingT));
            Vector3 ours = CurveMath.FlatNormal(ourDirection);
            return Mathf.Max(Mathf.Abs(ours.x * theirs.z - ours.z * theirs.x), 0.35f);
        }

        // The free node within LiftSnap of 'at' (-1: none).
        private static int NodeNear(List<Vector3> points, Vector3 at, bool[] isFixed)
        {
            int best = -1;
            float bestDistance = LiftSnap;
            for (int i = 0; i < points.Count; i++)
            {
                if (isFixed[i]) continue;
                float d = CurveMath.FlatDistance(points[i], at);
                if (d < bestDistance)
                {
                    best = i;
                    bestDistance = d;
                }
            }
            return best;
        }

        private static void Fix(int i, float height, float[] minH, float[] maxH, bool[] isFixed)
        {
            minH[i] = maxH[i] = height;
            isFixed[i] = true;
        }

        // Applies a height limit to every node within 'window' metres of node k
        // (k sits exactly on the crossing). The limit gets a margin of the
        // ramp's slope over the window, so the road still clears where the
        // other road's edge is even while ramping away from k. 'neighbours'
        // also holds the nodes either side of k (tunnels must reach past the
        // road above); otherwise they are free to ramp.
        // Keeps the road above (isMin) or below 'height' over the footprint
        // 'window' either side of node k. A node d metres from k needs a margin
        // only for the footprint left beyond it: the road may slope away from it
        // at MaxGrade over the remaining (window - d). Nodes outside the window
        // (the neighbours of a tunnel) need none.
        // 'flat': the same height across the whole window (a bridge deck: level
        // at its clearance, so the planned deck is the one built whether or not
        // the node on the crossing is dropped). Otherwise the limit grows towards
        // k at the road's grade.
        private static void Limit(float[] distance, int k, float window, float height, bool isMin, float[] limits, bool neighbours,
            bool flat = false)
        {
            for (int i = 0; i < distance.Length; i++)
            {
                float d = Mathf.Abs(distance[i] - distance[k]);
                bool near = i == k || d <= window || (neighbours && Mathf.Abs(i - k) == 1);
                if (!near) continue;
                float margin = flat ? 0f : MaxGrade * Mathf.Max(0f, window - d);
                float limit = isMin ? height + margin : height - margin;
                limits[i] = isMin ? Mathf.Max(limits[i], limit) : Mathf.Min(limits[i], limit);
            }
        }

        private static void Mark(float[] distance, int k, float window, bool[] marks, bool neighbours)
        {
            for (int i = 0; i < distance.Length; i++)
                if (Mathf.Abs(distance[i] - distance[k]) <= window || (neighbours && Mathf.Abs(i - k) == 1)) marks[i] = true;
        }

        private static WaterRun RunAt(List<WaterRun> runs, float at, float tolerance)
        {
            if (runs == null) return null;
            foreach (var run in runs)
                if (at >= run.From - tolerance && at <= run.To + tolerance) return run;
            return null;
        }

        // A curve that crosses the same piece of road twice can make junctions,
        // bridges or tunnels there, but not lower it or bridge it twice.
        private static void CheckRepeatedCrossings(Plan plan)
        {
            for (int a = 0; a < plan.Crossings.Count; a++)
            for (int b = a + 1; b < plan.Crossings.Count; b++)
            {
                var first = plan.Crossings[a];
                var second = plan.Crossings[b];
                if (first.Segment != second.Segment) continue;
                if (ChangesExisting(first) || ChangesExisting(second))
                {
                    plan.Problems.Add("The curve crosses the same piece of the " + first.ExistingName +
                                      " twice: use junctions, bridges or tunnels there (not overpass/underpass)");
                    return;
                }
            }
        }

        private static bool ChangesExisting(Crossing crossing)
        {
            return crossing.Type == CrossingType.Overpass || crossing.Type == CrossingType.Underpass;
        }

        // Two overpasses lowering the same road (or overlapping stretches of it)
        // would each cut and move the same pieces.
        private static void CheckLoweringsApart(Plan plan)
        {
            // Node ids as they are, segment ids moved past the node range.
            var used = new Dictionary<int, int>();
            foreach (var pair in plan.Lowerings)
            {
                var touched = new HashSet<int>();
                foreach (ushort node in pair.Value.Nodes) touched.Add(node);
                touched.Add(0x10000 + pair.Value.Segment);
                foreach (int id in touched)
                {
                    int other;
                    if (used.TryGetValue(id, out other) && other != pair.Key)
                    {
                        plan.Problems.Add("Two overpasses would lower the same stretch of the " + plan.Crossings[pair.Key].ExistingName +
                                          ": make one of them a bridge");
                        return;
                    }
                    used[id] = pair.Key;
                }
            }
        }

        private static void CheckVariants(NetInfo info, Plan plan, bool hasElevated)
        {
            bool elevated = false, tunnel = false;
            foreach (var kind in plan.Kinds)
            {
                if (kind == NodeKind.Elevated || kind == NodeKind.Bridge) elevated = true;
                if (kind == NodeKind.Tunnel || kind == NodeKind.Slope) tunnel = true;
            }
            if (elevated && !hasElevated) plan.Problems.Add(info.name + " has no elevated/bridge version");
            if (tunnel && (Variant(info, "m_tunnelInfo") == null || Variant(info, "m_slopeInfo") == null))
                plan.Problems.Add(info.name + " has no tunnel version");
        }

        // Going down: no water, and nothing else underground in the way.
        private static void CheckUnderground(NetInfo info, Plan plan, bool[] water, float[] ground, bool[] underWater)
        {
            var samples = new Vector3[9];
            var segments = NetManager.instance.m_segments.m_buffer;
            var checkedSegments = new HashSet<ushort>();
            foreach (var crossing in plan.Crossings) checkedSegments.Add(crossing.Segment);
            // The roads a deck runs along (over or under) are no obstacle to it.
            if (plan.Decks != null) foreach (var below in plan.Decks) if (below.Segment != 0) checkedSegments.Add(below.Segment);

            for (int i = 0; i < plan.Points.Count; i++)
            {
                if (!IsBelow(plan.Kinds[i])) continue;
                // A tunnel under a water crossing is fine; an open cutting or its
                // portal in the water is not.
                if (water[i] && underWater[i] && plan.Kinds[i] == NodeKind.Tunnel) continue;
                if (water[i])
                {
                    plan.Problems.Add(underWater[i]
                        ? "The tunnel under the water starts too late: its entrance would be in the water. Start the curve further from the shore"
                        : "Can't go under here: there is water");
                    return;
                }
            }

            // Probe every lowered node and points along each lowered piece:
            // nodes are up to 80 m apart, too far to see a tunnel in between.
            var probes = new List<KeyValuePair<Vector3, float>>();   // point, ground there
            for (int i = 0; i < plan.Points.Count; i++)
            {
                if (IsBelow(plan.Kinds[i])) probes.Add(new KeyValuePair<Vector3, float>(plan.Points[i], ground[i]));
                if (i + 1 >= plan.Points.Count || !(IsBelow(plan.Kinds[i]) || IsBelow(plan.Kinds[i + 1]))) continue;
                for (int k = 1; k < ProbesPerPiece; k++)
                {
                    float t = k / (float)ProbesPerPiece;
                    probes.Add(new KeyValuePair<Vector3, float>(Vector3.Lerp(plan.Points[i], plan.Points[i + 1], t),
                        Mathf.Lerp(ground[i], ground[i + 1], t)));
                }
            }

            foreach (var probe in probes)
            {
                // Other networks near this point and at about the same depth.
                Vector3 p = probe.Key;
                foreach (var segment in NearbySegments(p, info.m_halfWidth + 40f))
                {
                    if (checkedSegments.Contains(segment)) continue;
                    ushort a = segments[segment].m_startNode, b = segments[segment].m_endNode;
                    if (a == plan.StartNode || b == plan.StartNode || a == plan.EndNode || b == plan.EndNode) continue;
                    CrossingFinder.SampleSegment(segment, samples);
                    float reach = info.m_halfWidth + segments[segment].Info.m_halfWidth + 2f;
                    foreach (var q in samples)
                    {
                        if (CurveMath.FlatDistance(p, q) < reach && Mathf.Abs(q.y - p.y) < UnderClearance &&
                            q.y < probe.Value - 1f)
                        {
                            plan.Problems.Add("Too close to " + segments[segment].Info.name + " underground (" +
                                              Mathf.Abs(q.y - p.y).ToString("F0") + " m apart)");
                            return;
                        }
                    }
                }
            }
        }

        private static bool IsBelow(NodeKind kind)
        {
            return kind == NodeKind.Cut || kind == NodeKind.Tunnel || kind == NodeKind.Slope;
        }

        // Passing over or under something without connecting needs headroom.
        private static void CheckCrossings(Plan plan)
        {
            for (int c = 0; c < plan.Crossings.Count; c++)
            {
                var crossing = plan.Crossings[c];
                bool changesIt = crossing.Connects || crossing.Type == CrossingType.Overpass || crossing.Type == CrossingType.Underpass;
                // The road our curve starts or ends in the middle of is cut there
                // first; a junction, overpass or underpass further along that same
                // piece of road would then have nothing left to change.
                if (changesIt && crossing.Segment != 0 && (crossing.Segment == plan.StartSegment || crossing.Segment == plan.EndSegment))
                    plan.Problems.Add("Crossing " + (c + 1) + " (" + crossing.ExistingName + ") is on the piece of road the curve " +
                                      (crossing.Segment == plan.StartSegment ? "starts" : "ends") +
                                      " on: pick a bridge or tunnel there, or start the curve on another road");
                if (changesIt) continue;
                float gap = Mathf.Abs(plan.Points[plan.CrossingIndex[c]].y - crossing.Point.y);
                if (gap < UnderClearance - 0.5f)
                    plan.Problems.Add("Only " + gap.ToString("F0") + " m from " + crossing.ExistingName +
                                      ": pick a bridge or tunnel for this crossing");
            }
        }

        internal static IEnumerable<ushort> NearbySegments(Vector3 p, float radius)
        {
            var net = NetManager.instance;
            int x0 = Cell(p.x - radius), x1 = Cell(p.x + radius), z0 = Cell(p.z - radius), z1 = Cell(p.z + radius);
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                ushort segment = net.m_segmentGrid[z * 270 + x];
                int guard = 0;
                while (segment != 0 && guard++ < 32768)
                {
                    if ((net.m_segments.m_buffer[segment].m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None)
                        yield return segment;
                    segment = net.m_segments.m_buffer[segment].m_nextGridSegment;
                }
            }
        }

        private static int Cell(float coordinate)
        {
            return Mathf.Clamp((int)(coordinate / 64f + 135f), 0, 269);
        }
    }
}
