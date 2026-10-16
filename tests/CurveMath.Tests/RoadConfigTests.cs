using System.Collections.Generic;
using System.Linq;
using RealisticRoads;
using Xunit;

public class RoadConfigTests
{
    private const LaneDirection F = LaneDirection.Forward, B = LaneDirection.Backward;

    private static RoadConfig Road(params RoadPiece[] pieces) => new RoadConfig { Id = "test", Pieces = pieces.ToList() };
    private static RoadPiece Walk() => new RoadPiece(PieceKind.Sidewalk);
    private static RoadPiece Car(LaneDirection d) => new RoadPiece(PieceKind.Car, d);

    [Fact]
    public void Presets_AllCompose()
    {
        foreach (var preset in RoadConfig.Presets())
        {
            var cs = CrossSection.Compose(preset);
            Assert.True(cs.Ok, preset.Name + ": " + string.Join("; ", cs.Errors));
        }
    }

    [Fact]
    public void TwoLaneStreet_MatchesTheVanillaBasicRoad()
    {
        // 3 + 2 + 3 + 3 + 2 + 3 = 16 m, like the vanilla two-lane road.
        var cs = CrossSection.Compose(RoadConfig.Presets()[0]);
        Assert.Equal(8f, cs.HalfWidth, 3);
        Assert.Equal(3f, cs.PavementWidth, 3);
        Assert.Equal("Basic Road", cs.BaseName);
        Assert.Equal(1, cs.ForwardLanes);
        Assert.Equal(1, cs.BackwardLanes);
        var cars = cs.Lanes.Where(l => l.Kind == PieceKind.Car).ToList();
        Assert.Equal(-1.5f, cars[0].Position, 3);
        Assert.Equal(B, cars[0].Direction);
        Assert.Equal(1.5f, cars[1].Position, 3);
        Assert.Equal(F, cars[1].Direction);
        Assert.True(cars.All(c => c.Outer));
        var parking = cs.Lanes.Where(l => l.Kind == PieceKind.Parking).ToList();
        Assert.Equal(-4f, parking[0].Position, 3);
        Assert.Equal(4f, parking[1].Position, 3);
    }

    [Fact]
    public void SymmetricRoad_HasMirroredLanes()
    {
        var cs = CrossSection.Compose(RoadConfig.Presets()[2]);
        var lanes = cs.Lanes.OrderBy(l => l.Position).ToList();
        for (int i = 0; i < lanes.Count; i++)
        {
            var mirror = lanes[lanes.Count - 1 - i];
            Assert.Equal(-lanes[i].Position, mirror.Position, 3);
            Assert.Equal(lanes[i].Kind, mirror.Kind);
        }
        Assert.Equal("Large Road Decoration Trees", cs.BaseName);
        // The bus lanes are the outer lanes, by the sidewalk.
        Assert.True(cs.Lanes.Where(l => l.Outer).All(l => l.Kind == PieceKind.Bus));
        // A tree median gets a decoration lane in the middle.
        Assert.Contains(cs.Lanes, l => l.Kind == PieceKind.Median && System.Math.Abs(l.Position) < 0.01f);
    }

    [Fact]
    public void BaseRoad_FollowsLaneCountAndMedian()
    {
        Assert.Equal("Medium Road Decoration Grass", CrossSection.Compose(RoadConfig.Presets()[1]).BaseName);
        Assert.Equal("Oneway Road", CrossSection.Compose(RoadConfig.Presets()[3]).BaseName);
        Assert.Equal("Medium Road", CrossSection.Compose(Road(Walk(), Car(B), Car(B), Car(F), Car(F), Walk())).BaseName);
        Assert.Equal("Large Oneway", CrossSection.Compose(Road(Walk(), Car(F), Car(F), Car(F), Walk())).BaseName);
        Assert.Equal("Highway", CrossSection.Compose(Road(Car(F), Car(F), Car(F))).BaseName);
        var cs = CrossSection.Compose(RoadConfig.Presets()[1]);
        Assert.Equal(new List<string> { "Medium Road Decoration Grass", "Medium Road", "Basic Road" }, cs.BaseCandidates(true));
        Assert.Equal(new List<string> { "Medium Road Decoration Grass", "Medium Road" }, cs.BaseCandidates(false));
    }

    [Fact]
    public void ForwardLanesOnTheLeft_IsAnError()
    {
        var cs = CrossSection.Compose(Road(Walk(), Car(F), Car(B), Walk()));
        Assert.False(cs.Ok);
        Assert.Contains(cs.Errors, e => e.Contains("together"));
    }

    [Fact]
    public void BadLayouts_GiveClearErrors()
    {
        Assert.False(CrossSection.Compose(Road()).Ok);
        Assert.Contains(CrossSection.Compose(Road(Walk(), Car(B), Car(F))).Errors, e => e.Contains("both sides"));
        Assert.Contains(CrossSection.Compose(Road(Walk(), new RoadPiece(PieceKind.Sidewalk, F, 4f) , Car(F), Walk())).Errors,
            e => e.Contains("outside"));
        Assert.Contains(CrossSection.Compose(Road(Walk(), new RoadPiece(PieceKind.Median), Car(B), Car(F), Walk())).Errors,
            e => e.Contains("median"));
        Assert.Contains(CrossSection.Compose(Road(Walk(), new RoadPiece(PieceKind.Car, F, 9f), Walk())).Errors,
            e => e.Contains("outside 2.5"));
        Assert.Contains(CrossSection.Compose(Road(Car(B), Car(F))).Errors, e => e.Contains("need sidewalks"));
        Assert.Contains(CrossSection.Compose(Road(Walk(), new RoadPiece(PieceKind.Bike, F), Walk())).Errors,
            e => e.Contains("car or bus"));
    }

    [Fact]
    public void UnequalSidewalks_AreAnError()
    {
        var cs = CrossSection.Compose(Road(Walk(), Car(B), Car(F), new RoadPiece(PieceKind.Sidewalk, F, 4f)));
        Assert.Contains(cs.Errors, e => e.Contains("same width"));
    }

    [Fact]
    public void BusLaneAwayFromTheCurb_IsAWarningOnly()
    {
        var cs = CrossSection.Compose(Road(Walk(), Car(B), new RoadPiece(PieceKind.Bus, B), Car(F), Walk()));
        Assert.True(cs.Ok);
        Assert.Contains(cs.Warnings, w => w.Contains("Buses"));
    }

    [Fact]
    public void Copy_IsIndependent()
    {
        var a = RoadConfig.Presets()[0];
        var b = a.Copy();
        b.Pieces[1].Width = 3f;
        b.Pieces.RemoveAt(0);
        Assert.Equal(2f, a.Pieces[1].Width);
        Assert.Equal(6, a.Pieces.Count);
    }

    [Fact]
    public void StretchX_KeepsSidewalksAndStretchesAsphalt()
    {
        // Basic road (5 m asphalt + 3 m sidewalk each side) to 8 m asphalt + 3 m sidewalk.
        Assert.Equal(0f, CrossSection.StretchX(0f, 5f, 3f, 8f, 3f), 3);
        Assert.Equal(4f, CrossSection.StretchX(2.5f, 5f, 3f, 8f, 3f), 3);
        Assert.Equal(-8f, CrossSection.StretchX(-5f, 5f, 3f, 8f, 3f), 3);   // the curb
        Assert.Equal(9.5f, CrossSection.StretchX(6.5f, 5f, 3f, 8f, 3f), 3); // mid sidewalk keeps its offset
        Assert.Equal(11f, CrossSection.StretchX(8f, 5f, 3f, 8f, 3f), 3);    // the edge
        Assert.Equal(-13f, CrossSection.StretchX(-10f, 5f, 3f, 8f, 3f), 3); // a wall beyond the edge
        // Wider sidewalk: scaled, not shifted.
        Assert.Equal(10f, CrossSection.StretchX(6.5f, 5f, 3f, 8f, 4f), 3);
    }
}
