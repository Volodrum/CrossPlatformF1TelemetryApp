using F1Telemetry.Core.Tracks;

namespace F1Telemetry.Tests;

/// <summary>The track map's centre line as a stand-in racing line, for laps kept without their own.</summary>
public sealed class TrackOutlineTests
{
    /// <summary>A 100 × 50 m rectangle, 10 m wide, run from (0, 0) along +X: 300 m round.</summary>
    private static TrackOutline Rectangle()
    {
        var centre = new List<TrackPoint>();
        for (var d = 0; d < 300; d += 2)
        {
            centre.Add(d switch
            {
                < 100 => new TrackPoint(d, 0),
                < 150 => new TrackPoint(100, d - 100),
                < 250 => new TrackPoint(250 - d, 50),
                _ => new TrackPoint(0, 300 - d),
            });
        }

        return new TrackOutline("Test", [.. centre.Select(p => p with { Z = p.Z - 5 })], [.. centre.Select(p => p with { Z = p.Z + 5 })]);
    }

    [Fact]
    public void Points_on_the_centre_line_follow_lap_distance_from_the_start_line()
    {
        var (x, z) = Rectangle().AlongCenterline(25, 13)!.Value;

        Assert.Equal(13, x.Length);
        Assert.Equal((0, 0), (x[0], z[0]));
        Assert.Equal((75, 0), (x[3], z[3]));
        Assert.Equal((100, 25), (x[5], z[5]));
        Assert.Equal((50, 50), (x[8], z[8]));
        Assert.Equal((0, 0), (Math.Round(x[12], 6), Math.Round(z[12], 6))); // 300 m: back at the line, closing the loop
    }

    [Fact]
    public void Too_few_points_give_no_line() =>
        Assert.Null(new TrackOutline("Dot", [new TrackPoint(0, 0), new TrackPoint(1, 0)], [new TrackPoint(0, 1), new TrackPoint(1, 1)]).AlongCenterline(25, 10));
}
