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
}
