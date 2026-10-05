using F1Telemetry.Core.Models;

namespace F1Telemetry.App.ViewModels;

public enum ChartLine
{
    Solid,
    Dashed,
    None,
}

/// <param name="IsReference">Drawn as the grey dashed reference lap, whatever <paramref name="Color"/> says.</param>
/// <param name="MarkerSize">Dot per point, in pixels; 0 = none. Unset: dots only for one- and two-point series.</param>
/// <param name="OpenMarkers">Rings instead of filled dots, so two series stay apart without colour.</param>
/// <param name="InLegend">False keeps the series out of the legend (an unnamed companion of a series that has an entry).</param>
/// <param name="EndLabel">Text at the last point (a trend line's slope, say), above it, or below when <paramref name="EndLabelBelow"/>.</param>
public sealed record ChartSeries(
    string Name,
    string Color,
    double[] X,
    double[] Y,
    bool Step = false,
    bool IsReference = false,
    ChartLine Line = ChartLine.Solid,
    float LineWidth = 3,
    float? MarkerSize = null,
    bool OpenMarkers = false,
    bool InLegend = true,
    string EndLabel = "",
    bool EndLabelBelow = false);

/// <summary>A dotted vertical line at <paramref name="X"/> (a pit stop, say); only markers with a label get a legend entry.</summary>
public sealed record ChartMarker(double X, string Color, string Label = "");

/// <summary>
/// One row of a strategy timeline drawn under the plot on the same x axis: a badge (<paramref name="Label"/> on
/// <paramref name="Color"/>), its stints, and a PIT chip at each of <paramref name="Pits"/> with a dotted line
/// in <paramref name="Color"/> up through the plot.
/// </summary>
public sealed record ChartTimelineRow(string Label, string Color, IReadOnlyList<ChartTimelineSegment> Segments, IReadOnlyList<double> Pits);

/// <summary>
/// A stint on a timeline row, <paramref name="X0"/>–<paramref name="X1"/>: a ring with <paramref name="Badge"/> (a compound letter)
/// in <paramref name="Edge"/>, then <paramref name="Text"/> and, at the right end, <paramref name="Value"/>, as far as they fit.
/// </summary>
public sealed record ChartTimelineSegment(double X0, double X1, string Badge, string Text, string Value, string Edge);

/// <param name="XIsTime">X values are seconds; ticks are formatted as m:ss.</param>
/// <param name="ZeroLine">Draws a dashed line at y = 0 (delta charts).</param>
/// <param name="YIsTime">Y values are seconds; ticks are formatted as m:ss.t (lap times).</param>
public sealed record ChartModel(
    string Title,
    IReadOnlyList<ChartSeries> Series,
    string XLabel = LapAxis.DistanceLabel,
    bool InvertY = false,
    bool IntegerY = false,
    string YPrefix = "",
    bool XIsTime = false,
    bool ZeroLine = false,
    IReadOnlyList<ChartMarker>? Markers = null,
    bool IntegerX = false,
    IReadOnlyList<ChartTimelineRow>? Timeline = null,
    bool YIsTime = false);

public sealed record SeriesSpec(string Name, string Color, Func<TelemetrySample, double> Value, bool Step = false);

public enum XAxisMode
{
    LapTime,
    Distance,
}

/// <summary>X values for lap charts: elapsed lap time (seconds) or lap distance (metres).</summary>
public static class LapAxis
{
    public const string DistanceLabel = "Lap distance (m)";
    public const string TimeLabel = "Lap time";

    public static string Label(XAxisMode mode) => mode == XAxisMode.LapTime ? TimeLabel : DistanceLabel;

    public static double[] X(IReadOnlyList<TelemetrySample> lap, XAxisMode mode) =>
        mode == XAxisMode.LapTime ? LapTimes(lap) : lap.Select(s => s.LapDistance).ToArray();

    /// <summary>
    /// Seconds since the lap started, per sample. The first sample of a lap lands a few metres past the line, so
    /// the start is extrapolated back from that sample's distance and speed.
    /// </summary>
    public static double[] LapTimes(IReadOnlyList<TelemetrySample> lap)
    {
        if (lap.Count == 0)
        {
            return [];
        }

        var first = lap[0];
        var start = first.SessionTime;
        if (first.LapDistance is > 0 and < 300 && first.Speed > 10)
        {
            start -= first.LapDistance / (first.Speed / 3.6);
        }

        var times = new double[lap.Count];
        for (var i = 0; i < lap.Count; i++)
        {
            times[i] = lap[i].SessionTime - start;
        }

        return times;
    }

    /// <summary>m:ss (h:mm:ss past an hour), with tenths only when the value has them.</summary>
    public static string FormatTime(double seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        var totalTenths = (long)Math.Round(Math.Abs(seconds) * 10);
        var tenths = totalTenths % 10 == 0 ? "" : $".{totalTenths % 10}";
        var totalSeconds = totalTenths / 10;
        var (h, m, s) = (totalSeconds / 3600, totalSeconds / 60 % 60, totalSeconds % 60);
        return h > 0 ? $"{sign}{h}:{m:00}:{s:00}{tenths}" : $"{sign}{m}:{s:00}{tenths}";
    }
}

/// <summary>A chart definition; turned into a <see cref="ChartModel"/> for a given lap (+ optional reference lap).</summary>
public sealed record ChartSpec(string Title, SeriesSpec[] Series, bool InvertY = false, bool CompareWithReference = true)
{
    public ChartModel Build(IReadOnlyList<TelemetrySample> lap, IReadOnlyList<TelemetrySample>? reference, string referenceLabel, XAxisMode mode)
    {
        var x = LapAxis.X(lap, mode);
        var series = new List<ChartSeries>();
        if (CompareWithReference && reference is { Count: > 0 })
        {
            var rx = LapAxis.X(reference, mode);
            series.Add(new ChartSeries($"{Series[0].Name} ({referenceLabel})", Series[0].Color, rx, reference.Select(Series[0].Value).ToArray(), Series[0].Step, IsReference: true));
        }

        series.AddRange(Series.Select(s => new ChartSeries(s.Name, s.Color, x, lap.Select(s.Value).ToArray(), s.Step)));
        return new ChartModel(Title, series, LapAxis.Label(mode), InvertY: InvertY, XIsTime: mode == XAxisMode.LapTime);
    }
}

/// <summary>Lap-detail chart layout, grouped into the same four tabs as the original dashboard.</summary>
public static class ChartCatalog
{
    public static readonly (string Tab, ChartSpec[] Charts)[] Tabs =
    [
        ("Dynamics",
        [
            new ChartSpec("Speed (km/h)", [new("Speed", "#F4F6F9", s => s.Speed)]),
            new ChartSpec("Inputs", [new("Throttle", "#2EE88F", s => s.Throttle, true), new("Brake", "#FF5A4F", s => s.Brake, true)]),
            new ChartSpec("Gear / RPM", [new("RPM", "#4DB5FF", s => s.Rpm)]),
            new ChartSpec("G-forces", [new("Lateral", "#FFC23D", s => s.GForceLat), new("Longitudinal", "#4DB5FF", s => s.GForceLon)]),
            new ChartSpec("Steering", [new("Steer", "#C77DFF", s => s.Steer)]),
            new ChartSpec("Race position", [new("Position", "#C77DFF", s => s.Position)], InvertY: true, CompareWithReference: false),
        ]),
        ("Tyres",
        [
            new ChartSpec("Tyre surface temperature (°C)", [new("FL", "#4DB5FF", s => s.TyresSurfaceTempFl), new("FR", "#FFC23D", s => s.TyresSurfaceTempFr), new("RL", "#2EE88F", s => s.TyresSurfaceTempRl), new("RR", "#C77DFF", s => s.TyresSurfaceTempRr)], CompareWithReference: false),
            new ChartSpec("Tyre inner temperature (°C)", [new("FL", "#4DB5FF", s => s.TyresInnerTempFl), new("FR", "#FFC23D", s => s.TyresInnerTempFr), new("RL", "#2EE88F", s => s.TyresInnerTempRl), new("RR", "#C77DFF", s => s.TyresInnerTempRr)], CompareWithReference: false),
            new ChartSpec("Tyre wear (%)", [new("FL", "#4DB5FF", s => s.TyreWearFl), new("FR", "#FFC23D", s => s.TyreWearFr), new("RL", "#2EE88F", s => s.TyreWearRl), new("RR", "#C77DFF", s => s.TyreWearRr)], CompareWithReference: false),
            new ChartSpec("Tyre pressure (psi)", [new("FL", "#4DB5FF", s => s.TyresPressureFl), new("FR", "#FFC23D", s => s.TyresPressureFr), new("RL", "#2EE88F", s => s.TyresPressureRl), new("RR", "#C77DFF", s => s.TyresPressureRr)], CompareWithReference: false),
            new ChartSpec("Brake temperature (°C)", [new("FL", "#4DB5FF", s => s.BrakesTempFl), new("FR", "#FFC23D", s => s.BrakesTempFr), new("RL", "#2EE88F", s => s.BrakesTempRl), new("RR", "#C77DFF", s => s.BrakesTempRr)], CompareWithReference: false),
        ]),
        ("Energy",
        [
            new ChartSpec("Fuel in tank (kg)", [new("Fuel", "#4DB5FF", s => s.FuelInTank)], CompareWithReference: false),
            new ChartSpec("ERS store (MJ)", [new("ERS store", "#FFC23D", s => s.ErsStoreEnergy / 1_000_000)]),
            new ChartSpec("ERS this lap (MJ)", [new("Deployed", "#FFC23D", s => s.ErsDeployed / 1_000_000), new("Harvested MGU-K", "#2EE88F", s => s.ErsHarvestedMguk / 1_000_000)], CompareWithReference: false),
            new ChartSpec("2026: overtake / active aero", [new("Overtake active", "#FF5A4F", s => s.OvertakeActive, true), new("Active aero mode", "#4DB5FF", s => s.ActiveAeroMode, true)], CompareWithReference: false),
        ]),
        ("Health",
        [
            new ChartSpec("Aero damage (%)", [new("FL wing", "#FF5A4F", s => s.FrontLeftWingDamage), new("FR wing", "#FFC23D", s => s.FrontRightWingDamage), new("Rear wing", "#F4F6F9", s => s.RearWingDamage), new("Floor", "#C77DFF", s => s.FloorDamage), new("Sidepod", "#4DB5FF", s => s.SidepodDamage)], CompareWithReference: false),
            new ChartSpec("Power unit wear (%)", [new("ICE", "#FF5A4F", s => s.EngineIceWear), new("MGU-K", "#FFC23D", s => s.EngineMgukWear), new("MGU-H", "#F4F6F9", s => s.EngineMguhWear), new("ES", "#2EE88F", s => s.EngineEsWear), new("Gearbox", "#4DB5FF", s => s.GearBoxDamage)], CompareWithReference: false),
            new ChartSpec("Engine temperature (°C)", [new("Engine", "#FFC23D", s => s.EngineTemp)], CompareWithReference: false),
        ]),
    ];

    /// <summary>
    /// Running time gap to the reference lap: at each point of the lap, elapsed time minus the reference's elapsed
    /// time at the same lap distance (negative = ahead). Null when the laps don't overlap.
    /// </summary>
    public static ChartModel? BuildTimeDelta(IReadOnlyList<TelemetrySample> lap, IReadOnlyList<TelemetrySample>? reference, string referenceLabel, XAxisMode mode)
    {
        if (lap.Count < 2 || reference is not { Count: > 1 })
        {
            return null;
        }

        // Reference time as a function of distance; drop points that don't advance (standstill, resets).
        var refTimes = LapAxis.LapTimes(reference);
        var refDistance = new List<double>(reference.Count);
        var refTime = new List<double>(reference.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            var d = reference[i].LapDistance;
            if (d >= 0 && (refDistance.Count == 0 || d > refDistance[^1]))
            {
                refDistance.Add(d);
                refTime.Add(refTimes[i]);
            }
        }

        if (refDistance.Count < 2)
        {
            return null;
        }

        var lapTimes = LapAxis.LapTimes(lap);
        var xs = new List<double>(lap.Count);
        var ys = new List<double>(lap.Count);
        for (var i = 0; i < lap.Count; i++)
        {
            var d = lap[i].LapDistance;
            if (d < refDistance[0] || d > refDistance[^1])
            {
                continue;
            }

            var j = refDistance.BinarySearch(d);
            double tRef;
            if (j >= 0)
            {
                tRef = refTime[j];
            }
            else
            {
                var hi = ~j;
                var lo = hi - 1;
                var f = (d - refDistance[lo]) / (refDistance[hi] - refDistance[lo]);
                tRef = refTime[lo] + f * (refTime[hi] - refTime[lo]);
            }

            xs.Add(mode == XAxisMode.LapTime ? lapTimes[i] : d);
            ys.Add(lapTimes[i] - tRef);
        }

        if (xs.Count < 2)
        {
            return null;
        }

        return new ChartModel($"Time delta to {referenceLabel} (s) · below 0 = ahead",
            [new ChartSeries($"Δ {referenceLabel}", "#FFC23D", xs.ToArray(), ys.ToArray())],
            LapAxis.Label(mode), XIsTime: mode == XAxisMode.LapTime, ZeroLine: true);
    }
}
