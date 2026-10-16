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
}

