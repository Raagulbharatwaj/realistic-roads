using System;
using System.Collections.Generic;
using System.Linq;
using RealisticRoads;
using UnityEngine;
using Xunit;

public class CurveMathTests
{
    private static Vector3 V(float x, float z, float y = 0f) => new Vector3(x, y, z);

    // Points on a circle of 'radius' around the origin, from 0 to 'degrees'.
    private static List<Vector3> Arc(float radius, float degrees, int points)
    {
        var list = new List<Vector3>();
        for (int i = 0; i < points; i++)
        {
            double a = degrees * Math.PI / 180.0 * i / (points - 1);
            list.Add(V(radius * (float)Math.Cos(a), radius * (float)Math.Sin(a)));
        }
        return list;
    }

    [Fact]
    public void StraightLine_IsStraightAndEvenlyDivided()
    {
        var line = CurveMath.SampleSpline(new[] { V(0, 0), V(100, 0) });
        Assert.Equal(V(0, 0), line.First());
        Assert.Equal(V(100, 0), line.Last());
        Assert.Equal(100f, CurveMath.Length(line), 2);
        Assert.All(line, p => Assert.Equal(0f, p.z, 3));

        CurveMath.Divide(line, 4, out var points, out var tangents);
        Assert.Equal(5, points.Count);
        for (int i = 0; i <= 4; i++) Assert.Equal(25f * i, points[i].x, 1);
        Assert.All(tangents, t => Assert.Equal(V(1, 0), t));
        Assert.Equal(0f, CurveMath.MaxBend(tangents), 2);
    }

    [Fact]
    public void Spline_PassesThroughEveryControlPoint()
    {
        var control = new[] { V(0, 0), V(50, 30), V(120, -10), V(200, 40) };
        var line = CurveMath.SampleSpline(control);
        for (int i = 0; i < control.Length; i++)
            Assert.Equal(control[i], line[i * CurveMath.SamplesPerSpan]);
    }

    [Fact]
    public void QuarterCircle_HasRightLengthAndEqualPieces()
    {
        var line = CurveMath.SampleSpline(Arc(100f, 90f, 5));
        float expected = (float)(Math.PI / 2 * 100);
        Assert.InRange(CurveMath.Length(line), expected * 0.98f, expected * 1.02f);

        CurveMath.Divide(line, 4, out var points, out var tangents);
        var pieces = Enumerable.Range(1, 4).Select(i => CurveMath.FlatDistance(points[i - 1], points[i])).ToList();
        Assert.True(pieces.Max() - pieces.Min() < 1f, "pieces differ: " + string.Join(", ", pieces));
        // 90 degrees over 4 pieces: about 22.5 degrees each.
        Assert.InRange(CurveMath.MaxBend(tangents), 18f, 27f);
        // Starts heading +z (up the circle), ends heading -x.
        Assert.True(CurveMath.BendAngle(tangents.First(), V(0, 1)) < 8f, "start " + tangents.First());
        Assert.True(CurveMath.BendAngle(tangents.Last(), V(-1, 0)) < 8f, "end " + tangents.Last());
    }

    [Fact]
    public void ChoosePieceCount_AddsPiecesUntilBendsFit()
    {
        var line = CurveMath.SampleSpline(Arc(100f, 90f, 5));   // ~157 m
        int count = CurveMath.ChoosePieceCount(line, spacing: 80f, minSpacing: 16f, maxBend: 30f, out bool ok);
        Assert.True(ok);
        Assert.True(count >= 3, "count " + count);
        CurveMath.Divide(line, count, out _, out var tangents);
        Assert.True(CurveMath.MaxBend(tangents) <= 30f);
    }

    [Fact]
    public void ChoosePieceCount_FlagsCurvesTooTightToBuild()
    {
        var line = CurveMath.SampleSpline(Arc(8f, 180f, 5));    // U-turn, radius 8 m
        CurveMath.ChoosePieceCount(line, spacing: 40f, minSpacing: 16f, maxBend: 30f, out bool ok);
        Assert.False(ok);
    }

    [Fact]
    public void RepeatedPoints_DoNotBreakTheCurve()
    {
        var line = CurveMath.SampleSpline(new[] { V(0, 0), V(0, 0), V(50, 0), V(50, 0), V(100, 20) });
        Assert.All(line, p => Assert.False(float.IsNaN(p.x) || float.IsNaN(p.z)));
        CurveMath.Divide(line, 3, out var points, out var tangents);
        Assert.All(tangents, t => Assert.False(float.IsNaN(t.x)));
        Assert.Equal(V(100, 20), points.Last());
    }

    [Fact]
    public void Heights_AreCarriedAlong()
    {
        var line = CurveMath.SampleSpline(new[] { V(0, 0, 10), V(100, 0, 30) });
        CurveMath.Divide(line, 2, out var points, out _);
        Assert.Equal(20f, points[1].y, 1);
    }

    [Fact]
    public void ShapeEnds_LeavesAndArrivesInTheGivenDirections()
    {
        // Points go north-east, but the road must leave heading east (continuing
        // a road) and arrive heading north (into another road).
        var control = new List<Vector3> { V(0, 0), V(150, 60), V(200, 200) };
        var line = CurveMath.SampleShaped(control, V(1, 0), V(0, 1));
        int count = CurveMath.ChoosePieceCount(line, 40f, 16f, 30f, out bool ok);
        CurveMath.Divide(line, count, out var points, out var tangents);

        Assert.True(ok, "max bend " + CurveMath.MaxBend(tangents));
        Assert.Equal(V(0, 0), points.First());
        Assert.Equal(V(200, 200), points.Last());
        Assert.True(CurveMath.BendAngle(tangents.First(), V(1, 0)) < 5f, "start " + tangents.First());
        Assert.True(CurveMath.BendAngle(tangents.Last(), V(0, 1)) < 5f, "end " + tangents.Last());
    }

    [Fact]
    public void ShapeEnds_WithNoDirections_ChangesNothing()
    {
        var control = new List<Vector3> { V(0, 0), V(100, 50) };
        Assert.Equal(control, CurveMath.ShapeEnds(control, Vector3.zero, Vector3.zero));
    }

    [Fact]
    public void ShapeEnds_TwoPointsBothDirections_StaysInOrder()
    {
        // A short straight continuation: guides must not cross each other.
        var shaped = CurveMath.ShapeEnds(new List<Vector3> { V(0, 0), V(100, 0) }, V(1, 0), V(1, 0));
        Assert.Equal(4, shaped.Count);
        for (int i = 1; i < shaped.Count; i++) Assert.True(shaped[i].x > shaped[i - 1].x);
        var line = CurveMath.SampleSpline(shaped);
        Assert.InRange(CurveMath.Length(line), 99.5f, 100.5f);
    }

    // --- polylines ---

    [Fact]
    public void SliceAndPointAt_FollowThePolyline()
    {
        var line = CurveMath.SampleSpline(new[] { V(0, 0), V(100, 0), V(200, 100) });
        float total = CurveMath.Length(line);
        CurveMath.PointAt(line, 50f, out var p, out var t);
        var head = CurveMath.Slice(line, 0f, 50f);
        Assert.InRange(CurveMath.Length(head), 49.5f, 50.5f);
        Assert.Equal(p, head.Last());
        Assert.True(CurveMath.BendAngle(t, V(1, 0)) < 15f, "tangent " + t);

        var slice = CurveMath.Slice(line, 50f, total - 20f);
        Assert.InRange(CurveMath.Length(slice), total - 70.5f, total - 69.5f);
        Assert.Equal(p, slice.First());
    }

    [Fact]
    public void Intersect_FindsCrossingsOnlyInsideBothPieces()
    {
        Assert.True(CurveMath.Intersect(V(0, 0), V(10, 0), V(5, -5), V(5, 5), out float ta, out float tc));
        Assert.Equal(0.5f, ta, 3);
        Assert.Equal(0.5f, tc, 3);
        Assert.False(CurveMath.Intersect(V(0, 0), V(10, 0), V(15, -5), V(15, 5), out _, out _));
        Assert.False(CurveMath.Intersect(V(0, 0), V(10, 0), V(0, 1), V(10, 1), out _, out _));
    }

    // --- heights ---

    private static float[] Spaced(int n, float step) => Enumerable.Range(0, n).Select(i => i * step).ToArray();
    private static float[] Fill(int n, float v) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void Heights_FollowGroundWithoutLimits()
    {
        var d = Spaced(6, 40f);
        var ground = new float[] { 0, 5, 12, 8, 3, 0 };   // hilly
        Assert.True(CurveMath.SolveHeights(d, ground, Fill(6, -1000), Fill(6, 1000), new bool[6], 0.06f, out var h));
        Assert.Equal(ground, h);
    }

    [Fact]
    public void Heights_FlyoverRampsUpAndDownAtTheGrade()
    {
        int n = 21;
        var d = Spaced(n, 40f);                              // 800 m flat road
        var min = Fill(n, -1000); min[10] = 10f;              // 10 m over something at the middle
        Assert.True(CurveMath.SolveHeights(d, Fill(n, 0), min, Fill(n, 1000), new bool[n], 0.06f, out var h));
        Assert.Equal(10f, h[10], 2);
        Assert.Equal(10f - 0.06f * 40f, h[9], 2);
        Assert.Equal(10f - 0.06f * 80f, h[12], 2);
        Assert.Equal(0f, h[0], 2);
        for (int i = 1; i < n; i++) Assert.True(Mathf.Abs(h[i] - h[i - 1]) <= 0.06f * 40f + 0.01f);
    }

    [Fact]
    public void Heights_UnderpassRampsDown()
    {
        int n = 21;
        var d = Spaced(n, 40f);
        var max = Fill(n, 1000); max[10] = -8f;
        Assert.True(CurveMath.SolveHeights(d, Fill(n, 0), Fill(n, -1000), max, new bool[n], 0.06f, out var h));
        Assert.Equal(-8f, h[10], 2);
        Assert.Equal(-8f + 2.4f, h[9], 2);
        Assert.Equal(0f, h[0], 2);
    }

    [Fact]
    public void Heights_TooCloseToAFixedEnd_IsRejected()
    {
        var d = Spaced(3, 40f);
        var min = Fill(3, -1000); var max = Fill(3, 1000);
        min[0] = max[0] = 0f;                                 // snapped to an existing road at ground level
        min[1] = 10f;                                         // flyover 40 m away needs 25 % slope
        var isFixed = new[] { true, false, false };
        Assert.False(CurveMath.SolveHeights(d, Fill(3, 0), min, max, isFixed, 0.06f, out _));
    }

    [Fact]
    public void Heights_GorgeBridge_SpansLevelBetweenTheCliffTops()
    {
        // Cliff tops at 40 m, a stream at the bottom of a 270 m wide gorge.
        var d = new float[] { 0, 40, 80, 170, 260, 350, 390, 430 };
        var ground = new float[] { 40, 40, 40, 2, 0, 2, 40, 40 };
        var min = Fill(8, -1000);
        for (int i = 3; i <= 5; i++) min[i] = 12f;              // over the water: 10 m clearance
        var isFixed = new bool[8];
        Assert.True(CurveMath.SolveHeights(d, ground, min, Fill(8, 1000), isFixed, 0.06f, out var h));
        Assert.True(CurveMath.LevelSpans(d, ground, h, isFixed, min));
        Assert.True(CurveMath.SolveHeights(d, ground, min, Fill(8, 1000), isFixed, 0.06f, out h));
        for (int i = 2; i <= 6; i++) Assert.Equal(40f, h[i], 1); // a level deck, no dip to the stream
    }

    [Fact]
    public void Heights_BridgeMeetsAHighBank_InsteadOfDippingBelowIt()
    {
        // Low bank on the left, a 20 m bank on the right, river between.
        var d = Spaced(8, 60f);
        var ground = new float[] { 0, 0, -3, -3, -3, 20, 20, 20 };
        var min = Fill(8, -1000);
        for (int i = 2; i <= 4; i++) min[i] = 10f;
        var isFixed = new bool[8];
        Assert.True(CurveMath.SolveHeights(d, ground, min, Fill(8, 1000), isFixed, 0.06f, out var h));
        CurveMath.LevelSpans(d, ground, h, isFixed, min);
        Assert.True(CurveMath.SolveHeights(d, ground, min, Fill(8, 1000), isFixed, 0.06f, out h));
        Assert.True(h[4] >= 20f - 0.06f * 60f - 0.01f);           // climbs to the high bank, no sag
        for (int i = 1; i < 8; i++) Assert.True(h[i] >= ground[i] - 0.01f);
        for (int i = 1; i < 8; i++)
            if (h[i] > ground[i] + 0.5f || h[i - 1] > ground[i - 1] + 0.5f)
                Assert.True(Mathf.Abs(h[i] - h[i - 1]) <= 0.06f * 60f + 0.06f);
    }

    // --- code review fixes -------------------------------------------------

    [Fact]
    public void Heights_GroundJunctionOnAHill_StaysOnTheGround()
    {
        // Snapped to an existing junction at the top of a 10 % hill: a plain
        // ground road must follow the hill, not lift off it.
        var d = Spaced(6, 40f);
        var ground = d.Select(x => -0.1f * x).ToArray();
        var min = Fill(6, -1000); var max = Fill(6, 1000);
        min[0] = max[0] = 0f;
        var isFixed = new[] { true, false, false, false, false, false };
        Assert.True(CurveMath.SolveHeights(d, ground, min, max, isFixed, 0.06f, out var h));
        for (int i = 0; i < 6; i++) Assert.Equal(ground[i], h[i], 2);
    }

    [Fact]
    public void Spline_UnevenlySpacedStart_HasNoHook()
    {
        // 15 m then 300 m in a straight line: the even-spacing extrapolation put
        // the phantom point ahead of the start and the curve hooked backwards.
        var line = CurveMath.SampleSpline(new List<Vector3> { V(0, 0), V(15, 0), V(315, 0) });
        for (int i = 1; i < line.Count; i++) Assert.True(line[i].x >= line[i - 1].x - 0.01f);
        Assert.All(line, p => Assert.InRange(p.x, -0.01f, 315.01f));
    }

    [Fact]
    public void Extrapolate_EvenSpacing_MatchesTheParabola()
    {
        // Both spans 10 m: the general formula must give the even-spacing one.
        var p = CurveMath.Extrapolate(V(0, 0), V(10, 0), V(18, 6));
        var even = V(0, 0) * 3f - V(10, 0) * 3f + V(18, 6);
        Assert.Equal(even.x, p.x, 3);
        Assert.Equal(even.z, p.z, 3);
    }

    [Fact]
    public void DivideAdaptive_LongStraightWithOneTightCorner_KeepsTheStraightLong()
    {
        var polyline = new List<Vector3>();
        for (int i = 0; i <= 100; i++) polyline.Add(V(i * 10f, 0));          // 1 km straight
        for (int i = 1; i <= 30; i++)                                        // tight quarter turn, r = 60 m, tangent to it
        {
            double a = Math.PI / 2 * i / 30;
            polyline.Add(V(1000f + 60f * (float)Math.Sin(a), 60f - 60f * (float)Math.Cos(a)));
        }
        CurveMath.DivideAdaptive(polyline, 80f, 24f, 30f, out var points, out var tangents, out bool ok);
        Assert.True(ok);
        int onStraight = points.Count(p => p.x < 990f);
        Assert.InRange(onStraight, 12, 15);                                  // ~80 m pieces, not 24 m
        Assert.True(CurveMath.MaxBend(tangents) <= 30f);
        for (int i = 1; i < points.Count; i++)
            Assert.True(CurveMath.FlatDistance(points[i - 1], points[i]) >= 20f);
    }

    [Fact]
    public void Heights_DryGorge_GetsALevelViaduct()
    {
        // 40 m deep ravine with no water: the road used to plunge down the cliff.
        var d = Spaced(9, 40f);
        var ground = new float[] { 40, 40, 40, 5, 0, 5, 40, 40, 40 };
        var min = Fill(9, -1000); var max = Fill(9, 1000);
        var isFixed = new bool[9];
        Assert.True(CurveMath.SolveHeights(d, ground, min, max, isFixed, 0.06f, out var h));
        Assert.True(CurveMath.LiftValleys(d, h, isFixed, 0.25f, 0.06f, 400f, min));
        Assert.True(CurveMath.SolveHeights(d, ground, min, max, isFixed, 0.06f, out h));
        CurveMath.LevelSpans(d, ground, h, isFixed, min);
        Assert.True(CurveMath.SolveHeights(d, ground, min, max, isFixed, 0.06f, out h));
        for (int i = 2; i <= 6; i++) Assert.Equal(40f, h[i], 1);
    }

    [Fact]
    public void LiftValleys_SteadyHill_IsLeftAlone()
    {
        // One-way slope steeper than the limit is not a gorge: no viaduct
        // (the planner reports it as too steep instead).
        var d = Spaced(5, 40f);
        var ground = new float[] { 0, 15, 30, 45, 60 };
        var min = Fill(5, -1000);
        Assert.False(CurveMath.LiftValleys(d, ground, new bool[5], 0.25f, 0.06f, 400f, min));
    }

    [Fact]
    public void SmoothGrades_StepInTheGround_GetsAnEmbankmentAndACutting()
    {
        // A 20 m step between two nodes 40 m apart is 50 %; the road allows 25 %.
        // Raising the low node and lowering the high one by up to 6 m brings it down.
        var d = Spaced(6, 40f);
        var ground = new float[] { 0, 0, 0, 20, 20, 20 };
        var h = (float[])ground.Clone();
        var embanked = new bool[6];
        float steepest = CurveMath.SmoothGrades(d, ground, h, new bool[6], Fill(6, -1000), Fill(6, 1000), 0.25f, 6f, embanked);
        Assert.Equal(0f, steepest);
        for (int i = 1; i < 6; i++) Assert.True(Mathf.Abs(h[i] - h[i - 1]) <= 0.25f * 40f + 0.01f);
        Assert.True(embanked[2] && embanked[3]);
        Assert.False(embanked[0] || embanked[5]);
    }

    [Fact]
    public void SmoothGrades_CliffTooHigh_ReportsTheLeftoverGrade()
    {
        var d = Spaced(4, 40f);
        var ground = new float[] { 0, 0, 60, 60 };
        var h = (float[])ground.Clone();
        float steepest = CurveMath.SmoothGrades(d, ground, h, new bool[4], Fill(4, -1000), Fill(4, 1000), 0.25f, 6f, new bool[4]);
        Assert.Equal((60f - 12f) / 40f, steepest, 2);
    }

    [Fact]
    public void Heights_WideFlatGorge_GetsOneViaductRimToRim()
    {
        // Steep walls and a 120 m wide flat floor: the floor nodes are not next to
        // a wall, but the deck must still span from rim to rim.
        var d = Spaced(10, 40f);
        var ground = new float[] { 40, 40, 40, 2, 0, 0, 0, 2, 40, 40 };
        var min = Fill(10, -1000); var max = Fill(10, 1000);
        var isFixed = new bool[10];
        Assert.True(CurveMath.SolveHeights(d, ground, min, max, isFixed, 0.06f, out var h));
        Assert.True(CurveMath.LiftValleys(d, h, isFixed, 0.25f, 0.06f, 400f, min));
        Assert.True(CurveMath.SolveHeights(d, ground, min, max, isFixed, 0.06f, out h));
        CurveMath.LevelSpans(d, ground, h, isFixed, min);
        Assert.True(CurveMath.SolveHeights(d, ground, min, max, isFixed, 0.06f, out h));
        for (int i = 2; i <= 8; i++) Assert.Equal(40f, h[i], 1);
    }

    [Fact]
    public void DivideAdaptive_TightBendAtTheEnd_LeavesNoStub()
    {
        // Random curves ending in a bend (radius 48 m and up, where a 30 m piece
        // fits the bend limit): almost never a piece shorter than minSpacing
        // (chords in bends are a little under the arc length, hence 27 m).
        var random = new System.Random(7);
        int stubs = 0, tested = 0;
        for (int run = 0; run < 3000; run++)
        {
            var poly = new List<Vector3>();
            float x = 0, z = 0, heading = 0;
            float straight = 80f + (float)random.NextDouble() * 300f;
            float radius = 48f + (float)random.NextDouble() * 80f;   // tighter: a 30 m piece alone bends past the limit
            float arc = (float)(random.NextDouble() * 2.0);
            for (float d = 0; d <= straight; d += 2f) poly.Add(new Vector3(d, 0, 0));
            x = straight;
            for (float a = 0; a <= arc; a += 2f / radius)
            {
                heading = a;
                poly.Add(new Vector3(x + radius * (float)Math.Sin(a), 0, z + radius * (1 - (float)Math.Cos(a))));
            }
            CurveMath.DivideAdaptive(poly, 120f, 30f, 40f, out var points, out var tangents, out bool ok);
            if (!ok) continue;
            tested++;
            for (int i = 1; i < points.Count; i++)
                if (CurveMath.FlatDistance(points[i - 1], points[i]) < 27f) stubs++;
        }
        Assert.True(tested > 1000);
        Assert.True(stubs <= tested / 500, stubs + " stubs in " + tested + " curves");
    }

    // Underpass bridge ends (rail numbers: keep 12, slide 80, piece 30, segment 160).
    private static CurveMath.BridgeEnd End(float toNode, float reach, bool movable, bool hasBeyond, float beyond, out float slide) =>
        CurveMath.ChooseBridgeEnd(toNode, reach, movable, hasBeyond, beyond, 12f, 80f, 30f, 160f, out slide);

    [Fact]
    public void BridgeEnd_NodeJustPastTheReach_BridgeRunsToIt()
    {
        Assert.Equal(CurveMath.BridgeEnd.Keep, End(40f, 32f, true, true, 90f, out _));
    }

    [Fact]
    public void BridgeEnd_NodeFurtherOut_IsSlidInInsteadOfANewNode()
    {
        Assert.Equal(CurveMath.BridgeEnd.SlideIn, End(70f, 32f, true, true, 90f, out float slide));
        Assert.Equal(38f, slide, 3);
    }

    [Fact]
    public void BridgeEnd_NodeThatCantMove_GetsACutButNoSliver()
    {
        Assert.Equal(CurveMath.BridgeEnd.Cut, End(90f, 32f, false, true, 90f, out _));
        // 20 m past the reach: a cut would leave a 20 m piece, so the bridge runs on.
        Assert.Equal(CurveMath.BridgeEnd.Keep, End(52f, 32f, false, true, 90f, out _));
    }

    [Fact]
    public void BridgeEnd_SlideWouldMakeTheNextSegmentTooLong_Cuts()
    {
        Assert.Equal(CurveMath.BridgeEnd.Cut, End(70f, 32f, true, true, 140f, out _));
    }

    [Fact]
    public void BridgeEnd_NodeInsideTheBridge_IsSlidOut()
    {
        Assert.Equal(CurveMath.BridgeEnd.SlideOut, End(10f, 32f, true, true, 90f, out float slide));
        Assert.Equal(22f, slide, 3);
    }

    [Fact]
    public void BridgeEnd_NodeInsideButRoadBeyondTooShort_ContinuesIntoIt()
    {
        Assert.Equal(CurveMath.BridgeEnd.Continue, End(10f, 32f, true, true, 40f, out _));
        Assert.Equal(CurveMath.BridgeEnd.Continue, End(10f, 32f, false, true, 90f, out _));
        Assert.Equal(CurveMath.BridgeEnd.Keep, End(10f, 32f, true, false, 0f, out _));
    }

    [Fact]
    public void ShapedSampleIndex_LandsOnEachControlPoint()
    {
        var controls = new List<Vector3> { V(0, 0), V(120, 40), V(260, -30), V(400, 10) };
        foreach (var start in new[] { Vector3.zero, V(1, 0) })
        foreach (var end in new[] { Vector3.zero, V(0, 1) })
        {
            var polyline = CurveMath.SampleShaped(controls, start, end);
            for (int c = 0; c < controls.Count; c++)
            {
                var p = polyline[CurveMath.ShapedSampleIndex(controls.Count, c, start, end)];
                Assert.Equal(controls[c].x, p.x, 3);
                Assert.Equal(controls[c].z, p.z, 3);
            }
        }
    }

    [Fact]
    public void Mixed_RoundedCornerIsACircleOfTheRadius()
    {
        var controls = new List<Vector3> { V(0, 0), V(100, 0), V(100, 100) };
        var line = CurveMath.SampleMixed(controls, new[] { true, true }, 50f, Vector3.zero, Vector3.zero, out var at, out var sharp);
        Assert.Empty(sharp);
        Assert.Equal(V(0, 0), line.First());
        Assert.Equal(V(100, 100), line.Last());
        foreach (var p in line)
        {
            if (p.x < 50f - 0.01f) Assert.Equal(0f, p.z, 3);                   // the first straight
            else if (p.z > 50f + 0.01f) Assert.Equal(100f, p.x, 3);            // the second
            else Assert.Equal(50f, CurveMath.FlatDistance(p, V(50, 50)), 0);   // the rounding (within 0.5 m)
        }
        // The middle control maps to the middle of the rounding.
        Assert.True(CurveMath.FlatDistance(line[at[1]], V(85.36f, 14.64f)) < 3f);
    }

    [Fact]
    public void Mixed_ZeroRadiusLeavesASharpCorner()
    {
        var controls = new List<Vector3> { V(0, 0), V(100, 0), V(100, 60) };
        var line = CurveMath.SampleMixed(controls, new[] { true, true }, 0f, Vector3.zero, Vector3.zero, out var at, out var sharp);
        Assert.Single(sharp);
        Assert.Equal(100f, sharp[0], 2);
        Assert.Equal(V(100, 0), line[at[1]]);
        Assert.Equal(160f, CurveMath.Length(line), 2);
    }

    [Fact]
    public void Mixed_CurveLeavesAStraightAlongItsLine()
    {
        var controls = new List<Vector3> { V(0, 0), V(100, 0), V(200, 80) };
        var line = CurveMath.SampleMixed(controls, new[] { true, false }, 40f, Vector3.zero, Vector3.zero, out var at, out _);
        int corner = at[1];
        Assert.Equal(V(100, 0), line[corner]);
        var after = CurveMath.FlatNormal(line[corner + 1] - line[corner]);
        Assert.True(CurveMath.BendAngle(V(1, 0), after) < 2f);
        Assert.Equal(V(200, 80), line.Last());
    }

    [Fact]
    public void Mixed_AllCurvesIsTheShapedCurve()
    {
        var controls = new List<Vector3> { V(0, 0), V(100, 30), V(220, 0) };
        var mixed = CurveMath.SampleMixed(controls, new[] { false, false }, 40f, Vector3.zero, Vector3.zero, out var at, out _);
        var shaped = CurveMath.SampleShaped(controls, Vector3.zero, Vector3.zero);
        Assert.Equal(shaped.Count, mixed.Count);
        Assert.Equal(V(100, 30), mixed[at[1]]);
    }

    [Fact]
    public void Snap_GridRoadsAndLength()
    {
        var north = V(0, 1);
        var raw = CurveMath.Rotate(north, -20f);   // 20 degrees right of north
        var snapped = CurveMath.SnapDirection(raw, north, null, 15f, 5f, out var kind);
        Assert.Equal(CurveMath.SnapKind.Grid, kind);
        Assert.Equal(-15f, CurveMath.SignedAngle(north, snapped), 2);

        var road = CurveMath.Rotate(north, -23f);   // a road 3 degrees off: parallel to it wins
        snapped = CurveMath.SnapDirection(raw, north, new[] { road }, 15f, 5f, out kind);
        Assert.Equal(CurveMath.SnapKind.Parallel, kind);
        Assert.True(CurveMath.BendAngle(snapped, road) < 0.01f);

        var across = CurveMath.Rotate(north, 68f);  // perpendicular to it is 22 degrees right
        snapped = CurveMath.SnapDirection(raw, north, new[] { across }, 15f, 5f, out kind);
        Assert.Equal(CurveMath.SnapKind.Perpendicular, kind);

        Assert.Equal(104f, CurveMath.SnapLength(101f, 8f));
        Assert.Equal(8f, CurveMath.SnapLength(2f, 8f));
    }

    [Theory]
    [InlineData(50f)]
    [InlineData(60f)]
    public void Mixed_RightAngleCornerFitsThePieceBendLimit(float radius)
    {
        // The tool's spacing (120 m, at least 30 m) and bend limit (40 degrees a piece).
        var controls = new List<Vector3> { V(0, 0), V(300, 0), V(300, 300) };
        var line = CurveMath.SampleMixed(controls, new[] { true, true }, radius, Vector3.zero, Vector3.zero, out _, out _);
        CurveMath.DivideAdaptive(line, 120f, 30f, 40f, out var points, out var tangents, out _);
        Assert.True(CurveMath.MaxBend(tangents) <= 40f, "bend " + CurveMath.MaxBend(tangents));
    }

    [Fact]
    public void Mixed_DeckFollowsTheGivenPath()
    {
        // Span 1 runs along a road's path (a bend), the others are drawn.
        var controls = new List<Vector3> { V(0, 0), V(100, 0), V(200, 100), V(300, 100) };
        var path = new List<Vector3>();
        for (int i = 0; i <= 20; i++)
        {
            double a = Math.PI / 2 * i / 20;
            path.Add(V(100 + 100 * (float)Math.Sin(a), 100 - 100 * (float)Math.Cos(a)));
        }
        var paths = new List<List<Vector3>> { null, path, null };
        var line = CurveMath.SampleMixed(controls, new[] { true, false, true }, 60f, Vector3.zero, Vector3.zero, out var at, out _, paths);
        Assert.Equal(V(100, 0), line[at[1]]);
        Assert.Equal(V(200, 100), line[at[2]]);
        for (int k = at[1]; k <= at[2]; k++) Assert.Equal(100f, CurveMath.FlatDistance(line[k], V(100, 100)), 1);
        Assert.Equal(V(300, 100), line.Last());
    }

    [Fact]
    public void Mixed_HairpinIsSharpNotNaN()
    {
        var controls = new List<Vector3> { V(0, 0), V(100, 0), V(0, 0.5f) };   // turns back almost 180 degrees
        var line = CurveMath.SampleMixed(controls, new[] { true, true }, 60f, Vector3.zero, Vector3.zero, out var at, out var sharp);
        Assert.Single(sharp);
        Assert.All(line, p => Assert.False(float.IsNaN(p.x) || float.IsNaN(p.z)));
        Assert.Equal(V(100, 0), line[at[1]]);
    }

    [Fact]
    public void Mixed_RoundingsThatMeetLeaveNoRepeatedPoint()
    {
        // A 40 m piece between two right angles: both roundings take half of it each.
        var controls = new List<Vector3> { V(0, 0), V(200, 0), V(200, 40), V(400, 40) };
        var line = CurveMath.SampleMixed(controls, new[] { true, true, true }, 60f, Vector3.zero, Vector3.zero, out _, out _);
        for (int i = 1; i < line.Count; i++) Assert.True(CurveMath.FlatDistance(line[i - 1], line[i]) > 0.01f, "repeated point at " + i);
        Assert.Equal(V(400, 40), line.Last());
    }

    [Fact]
    public void Places_RoundTripAlongTheCurve()
    {
        var controlAt = new[] { 0f, 100f, 100f, 250f };
        float along;
        Assert.True(CurveMath.PlaceAlong(controlAt, 0.5f, out along));
        Assert.Equal(50f, along, 3);
        Assert.True(CurveMath.PlaceAlong(controlAt, 2.5f, out along));
        Assert.Equal(175f, along, 3);
        Assert.True(CurveMath.PlaceAlong(controlAt, 3f, out along));
        Assert.Equal(250f, along, 3);
        Assert.False(CurveMath.PlaceAlong(controlAt, 3.5f, out along));
        Assert.Equal(2.5f, CurveMath.PlaceOf(controlAt, 175f), 3);
        Assert.Equal(0.25f, CurveMath.PlaceOf(controlAt, 25f), 3);
        Assert.Equal(3f, CurveMath.PlaceOf(controlAt, 300f), 3);
    }

    [Fact]
    public void Remap_IsStraightBetweenMarks()
    {
        var chord = new[] { 0f, 90f, 180f };
        var curve = new[] { 0f, 100f, 200f };
        Assert.Equal(50f, CurveMath.Remap(chord, curve, 45f), 3);
        Assert.Equal(150f, CurveMath.Remap(chord, curve, 135f), 3);
        Assert.Equal(200f, CurveMath.Remap(chord, curve, 180f), 3);
        Assert.Equal(90f, CurveMath.Remap(curve, chord, 100f), 3);
        Assert.Equal(-5f, CurveMath.Remap(chord, curve, -5f), 3);
    }

    [Fact]
    public void Offset_StraightLine_IsParallel()
    {
        var line = new List<Vector3> { V(0, 0, 5), V(0, 50, 5), V(0, 100, 5) };   // heading north
        var right = CurveMath.Offset(line, 10f);
        foreach (var p in right) Assert.Equal(10f, p.x, 3);   // east of it
        Assert.Equal(5f, right[1].y, 3);
        var left = CurveMath.Offset(line, -10f);
        Assert.Equal(-10f, left[2].x, 3);
        Assert.False(CurveMath.Folds(line, right));
    }

    [Fact]
    public void Offset_Corner_KeepsBothSidesAtTheGap()
    {
        // North, then east: a right-angled corner at (0, 100).
        var line = new List<Vector3> { V(0, 0), V(0, 100), V(100, 100) };
        var outside = CurveMath.Offset(line, -10f);   // left of the road: outside the right turn
        Assert.Equal(-10f, outside[1].x, 3);
        Assert.Equal(110f, outside[1].z, 3);
        Assert.Equal(110f, outside[2].z, 3);
    }

    [Fact]
    public void Offset_InsideTightBend_Folds()
    {
        var arc = Arc(20f, 180f, 37);                   // a 20 m radius half circle, turning left
        var inside = CurveMath.Offset(arc, -30f);       // left: the inside, wider than the radius
        Assert.True(CurveMath.Folds(arc, inside));
        var outside = CurveMath.Offset(arc, 30f);
        Assert.False(CurveMath.Folds(arc, outside));
        var near = CurveMath.Offset(arc, -10f);
        Assert.False(CurveMath.Folds(arc, near));
    }

    [Fact]
    public void Distances_AddUpAlongTheLine()
    {
        var d = CurveMath.Distances(new[] { V(0, 0), V(3, 4), V(3, 10) });
        Assert.Equal(new[] { 0f, 5f, 11f }, d);
    }

    [Fact]
    public void RoundCorners_JunctionCorner_IsSmoothAndKeepsEnds()
    {
        // Along one road east, then along a cross street north (a right angle at the junction).
        var path = new List<Vector3>();
        for (int x = 0; x <= 200; x += 4) path.Add(V(x, 0));
        for (int z = 4; z <= 200; z += 4) path.Add(V(200, z));
        var round = CurveMath.RoundCorners(path, 50f);
        Assert.Equal(path[0], round[0]);
        Assert.Equal(path[path.Count - 1], round[round.Count - 1]);
        float sharpest = 0f;
        for (int i = 1; i < round.Count - 1; i++)
            sharpest = Math.Max(sharpest, CurveMath.BendAngle(round[i] - round[i - 1], round[i + 1] - round[i]));
        Assert.True(sharpest < 8f, "sharpest turn " + sharpest);
        // It cuts the corner: no point near the junction's middle.
        foreach (var p in round) Assert.True(CurveMath.FlatDistance(p, V(200, 0)) > 10f);
    }

    [Fact]
    public void RoundCorners_GentleCurve_StaysAsItIs()
    {
        var path = new List<Vector3>();
        for (int i = 0; i <= 40; i++)
        {
            float a = i * 0.02f;
            path.Add(V(200f * (float)Math.Sin(a), 200f * (1f - (float)Math.Cos(a))));
        }
        Assert.Equal(path, CurveMath.RoundCorners(path, 50f));
    }

    [Fact]
    public void RoundCorners_StaysWithinTheRoadInsideTheCorner()
    {
        var path = new List<Vector3>();
        for (int x = 0; x <= 200; x += 4) path.Add(V(x, 0));
        for (int z = 4; z <= 200; z += 4) path.Add(V(200, z));
        // A deck over a road 8 m wide each side: at most 7 m inside the junction's middle.
        var round = CurveMath.RoundCorners(path, 50f, 8f, 7f);
        float nearest = float.MaxValue;
        foreach (var p in round) nearest = Math.Min(nearest, CurveMath.FlatDistance(p, V(200, 0)));
        Assert.True(nearest <= 7.01f, "passes " + nearest + " m inside the corner");
        Assert.True(nearest > 2f, "still rounded: " + nearest);
    }
}
