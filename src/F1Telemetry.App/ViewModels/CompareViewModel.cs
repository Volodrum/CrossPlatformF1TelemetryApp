using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

/// <summary>One row of the time breakdown: each race's figure and the difference, B minus A.</summary>
public sealed record BreakdownRow(string Label, string Note, string A, string B, string Delta, IBrush DeltaBrush, bool IsTotal = false)
{
    public FontWeight Weight => IsTotal ? FontWeight.Bold : FontWeight.Normal;
}

public sealed record CompareStintRow(string Compound, string Title, string Detail);

/// <summary>Race vs race: two recordings of the same track, the lap-by-lap gap between them and what it's made of.</summary>
public sealed partial class CompareViewModel(TelemetryRuntime runtime) : ObservableObject
{
    // Race identity on every chart of the tab: blue / orange (CVD-safe on the dark panel, and no tyre compound's colour).
    private const string ColorA = Palette.RaceAHex;
    private const string ColorB = Palette.RaceBHex;

    private CancellationTokenSource? _loadCts;
    private bool _updatingLists;

    public ObservableCollection<RecordingItemViewModel> RecordingsA { get; } = [];
    public ObservableCollection<RecordingItemViewModel> RecordingsB { get; } = [];
    public ObservableCollection<BreakdownRow> Breakdown { get; } = [];
    public ObservableCollection<CompareStintRow> StintsA { get; } = [];
    public ObservableCollection<CompareStintRow> StintsB { get; } = [];

    [ObservableProperty] public partial RecordingItemViewModel? SelectedA { get; set; }
    [ObservableProperty] public partial RecordingItemViewModel? SelectedB { get; set; }
    [ObservableProperty] public partial bool HasResult { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "Pick two recordings of the same track.";
    [ObservableProperty] public partial string Header { get; set; } = "Race vs race";
    [ObservableProperty] public partial string Verdict { get; set; } = "";
    [ObservableProperty] public partial IBrush VerdictBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string VerdictNote { get; set; } = "";
    [ObservableProperty] public partial string StrategyA { get; set; } = "";
    [ObservableProperty] public partial string StrategyB { get; set; } = "";
    [ObservableProperty] public partial ChartModel? GapChart { get; set; }
    [ObservableProperty] public partial ChartModel? PaceChart { get; set; }
    [ObservableProperty] public partial ChartModel? PositionChart { get; set; }

    /// <summary>Called when the tab opens: takes the sidebar's recordings and keeps (or picks) the two races.</summary>
    public async Task ActivateAsync(IReadOnlyList<RecordingItemViewModel> recordings, RecordingInfo? current)
    {
        var keepA = SelectedA?.Info.Id ?? current?.Id;
        _updatingLists = true;
        try
        {
            RecordingsA.Clear();
            foreach (var recording in recordings)
            {
                RecordingsA.Add(recording);
            }

            SelectedA = RecordingsA.FirstOrDefault(r => r.Info.Id == keepA)
                ?? RecordingsA.FirstOrDefault(r => SessionTypes.IsRace(r.Info.SessionType))
                ?? RecordingsA.FirstOrDefault();
            FillB();
        }
        finally
        {
            _updatingLists = false;
        }

        await RefreshAsync();
    }

    partial void OnSelectedAChanged(RecordingItemViewModel? value)
    {
        if (_updatingLists)
        {
            return;
        }

        _updatingLists = true;
        try
        {
            FillB();
        }
        finally
        {
            _updatingLists = false;
        }

        _ = RefreshAsync();
    }

    partial void OnSelectedBChanged(RecordingItemViewModel? value)
    {
        if (!_updatingLists)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Race B candidates: the other recordings of race A's track.</summary>
    private void FillB()
    {
        var keepB = SelectedB?.Info.Id;
        RecordingsB.Clear();
        if (SelectedA is { } a)
        {
            foreach (var recording in RecordingsA.Where(r => r.Info.Id != a.Info.Id && r.Info.TrackId == a.Info.TrackId))
            {
                RecordingsB.Add(recording);
            }
        }

        SelectedB = RecordingsB.FirstOrDefault(r => r.Info.Id == keepB)
            ?? RecordingsB.FirstOrDefault(r => SessionTypes.IsRace(r.Info.SessionType))
            ?? RecordingsB.FirstOrDefault();
    }

    private async Task RefreshAsync()
    {
        _loadCts?.Cancel();
        if (SelectedA?.Info is not { } a || SelectedB?.Info is not { } b)
        {
            HasResult = false;
            Message = SelectedA is null
                ? "No recordings yet. Record two races on the same track, one per strategy."
                : $"No other recording of {SelectedA.Info.TrackName} yet. Record a second race there to compare strategies.";
            return;
        }

        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var raceA = await runtime.Analysis.LoadRaceAsync(a, cts.Token);
            var raceB = await runtime.Analysis.LoadRaceAsync(b, cts.Token);
            var result = raceA is null || raceB is null ? null : RaceComparison.Compare(raceA, raceB, runtime.Analysis.DefaultFuelEffect);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (result is null)
            {
                HasResult = false;
                Message = "These two recordings have no timed laps in common.";
                return;
            }

            Show(raceA!, raceB!, result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Show(RaceInput a, RaceInput b, RaceComparisonResult result)
    {
        Header = $"{a.Recording.TrackName} · #{a.Recording.Id} vs #{b.Recording.Id}";
        var gap = result.TotalGap;
        Verdict = Math.Abs(gap) < 0.0005 ? "DEAD HEAT" : $"B {Math.Abs(gap):0.000} S {(gap < 0 ? "AHEAD" : "BEHIND")}";
        VerdictBrush = gap < 0 ? Palette.Faster : gap > 0 ? Palette.Slower : Palette.TextHi;
        VerdictNote = $"After {result.Laps.Count} laps both races completed. Fuel effect {result.FuelEffect.SecondsPerKg:0.000} s/kg, {result.FuelEffect.Basis}.";
        StrategyA = $"A · {Strategy(a)}";
        StrategyB = $"B · {Strategy(b)}";

        FillBreakdown(result);
        Fill(StintsA, result.StintsA, a);
        Fill(StintsB, result.StintsB, b);
        GapChart = BuildGapChart(result);
        PaceChart = BuildPaceChart(a, b, result);
        PositionChart = BuildPositionChart(a, b);
        HasResult = true;
    }

    /// <summary>"2 stops · Medium → Hard → Medium".</summary>
    private static string Strategy(RaceInput race)
    {
        var stops = race.Analysis.PitStops.Count;
        var compounds = string.Join(" → ", race.Analysis.Stints.Select(s => s.Compound));
        return $"{stops} stop{(stops == 1 ? "" : "s")} · {compounds}";
    }

    private void FillBreakdown(RaceComparisonResult r)
    {
        static string Seconds(double s) => $"{s:+0.0;−0.0;0.0} s";
        static string Lap(double totalSeconds, int laps) => laps > 0 ? $"{TimeFormat.Lap(totalSeconds / laps * 1000)} /lap" : "—";
        var laps = r.Laps.Count;

        Breakdown.Clear();
        Breakdown.Add(Row("Pit stops", "In-lap + out-lap against clean laps",
            $"{r.PitStopsA.Count} · {r.A.PitStops:0.0} s", $"{r.PitStopsB.Count} · {r.B.PitStops:0.0} s", r.B.PitStops - r.A.PitStops));
        Breakdown.Add(Row("Tyre wear", "Time lost to tyre age, from each stint's fit",
            Seconds(r.A.TyreWear), Seconds(r.B.TyreWear), r.B.TyreWear - r.A.TyreWear));
        Breakdown.Add(Row("Fuel load", "Time the fuel mass cost",
            Seconds(r.A.FuelLoad), Seconds(r.B.FuelLoad), r.B.FuelLoad - r.A.FuelLoad));
        Breakdown.Add(Row("Base pace", "Fresh tyres, empty tank: compound and driving",
            Lap(r.A.BasePace, laps), Lap(r.B.BasePace, laps), r.B.BasePace - r.A.BasePace));
        Breakdown.Add(Row("Start, traffic, mistakes", "What the model doesn't explain",
            Seconds(r.A.Other), Seconds(r.B.Other), r.B.Other - r.A.Other));
        Breakdown.Add(Row("Total", $"{laps} laps", TimeFormat.Lap(r.A.Total * 1000), TimeFormat.Lap(r.B.Total * 1000), r.TotalGap, isTotal: true));
    }

    private static BreakdownRow Row(string label, string note, string a, string b, double delta, bool isTotal = false) =>
        new(label, note, a, b, $"{delta:+0.000;−0.000;0.000} s", delta < -0.0005 ? Palette.Faster : delta > 0.0005 ? Palette.Slower : Palette.TextMid, isTotal);

    private static void Fill(ObservableCollection<CompareStintRow> rows, IReadOnlyList<StintPaceFit> fits, RaceInput race)
    {
        rows.Clear();
        foreach (var stint in race.Analysis.Stints)
        {
            var fit = fits.FirstOrDefault(f => f.StintNumber == stint.StintNumber);
            var detail = fit is { Laps: >= 2 }
                ? $"{fit.DegradationPerLap:+0.000;−0.000} s/lap wear · base {TimeFormat.Lap(fit.BaseLapSeconds * 1000)} · {fit.Laps} clean laps"
                : "Too few clean laps for a fit";
            rows.Add(new CompareStintRow(stint.Compound, $"Stint {stint.StintNumber} · L{stint.StartLap}–{stint.EndLap}", detail));
        }

        foreach (var stop in race.Analysis.PitStops)
        {
            var lane = stop.PitLaneSeconds is { } s ? $" · pit lane {s:0.0} s" : "";
            rows.Add(new CompareStintRow("", $"Stop L{stop.InLap}–{stop.OutLap}", $"{stop.LossSeconds:0.0} s lost{lane}"));
        }
    }

    /// <summary>A dotted line between each stop's in-lap and out-lap.</summary>
    private static List<ChartMarker> StopMarkers(IReadOnlyList<PitStopAnalysis> a, IReadOnlyList<PitStopAnalysis> b) =>
    [
        .. a.Select((s, i) => new ChartMarker(s.InLap + 0.5, ColorA, i == 0 ? "A pit stop" : "")),
        .. b.Select((s, i) => new ChartMarker(s.InLap + 0.5, ColorB, i == 0 ? "B pit stop" : "")),
    ];

    private static ChartModel BuildGapChart(RaceComparisonResult r) =>
        new("Gap B − A (s) · above 0 = B behind",
            [new ChartSeries("Gap", ColorB, r.Laps.Select(l => (double)l).ToArray(), r.CumulativeGap.ToArray())],
            XLabel: "Lap", ZeroLine: true, Markers: StopMarkers(r.PitStopsA, r.PitStopsB), IntegerX: true);

    /// <summary>
    /// Clean laps with the fuel effect taken out, coloured by race (A blue rings, B orange dots), with each stint's
    /// tyre-wear trend as a line (A dashed). Above the plot, a strategy strip per race: one bar per stint in its
    /// compound colour, spanning the stint's laps (in-lap and out-lap included), with its wear rate. The break
    /// between two bars is the pit stop, so the plot itself needs no stop markers.
    /// </summary>
    private static ChartModel BuildPaceChart(RaceInput a, RaceInput b, RaceComparisonResult r)
    {
        var k = r.FuelEffect.SecondsPerKg;
        var series = new List<ChartSeries>();
        var bands = new List<ChartBand>();

        void Race(RaceInput race, IReadOnlyList<StintPaceFit> fits, string name, string color, bool isA)
        {
            var paceLaps = race.Analysis.PaceLaps.ToLookup(l => l.StintNumber);
            var named = false;
            foreach (var stint in race.Analysis.Stints)
            {
                var laps = paceLaps[stint.StintNumber].OrderBy(l => l.LapNumber).ToList();
                var fit = fits.FirstOrDefault(f => f.StintNumber == stint.StintNumber && f.Laps >= 2);
                var hasTrend = fit is not null && laps.Count >= 2;
                if (laps.Count > 0)
                {
                    series.Add(new ChartSeries($"Race {name}", color,
                        laps.Select(l => (double)l.LapNumber).ToArray(), laps.Select(l => l.LapSeconds - k * l.FuelKg).ToArray(),
                        Line: ChartLine.None, MarkerSize: 8, OpenMarkers: isA, InLegend: false));
                }

                if (hasTrend)
                {
                    var (first, last) = (laps[0], laps[^1]);
                    series.Add(new ChartSeries($"Race {name}", color,
                        [first.LapNumber, last.LapNumber], [fit!.Predict(first.TyreAge, 0, k), fit.Predict(last.TyreAge, 0, k)],
                        Line: isA ? ChartLine.Dashed : ChartLine.Solid, LineWidth: 2.5f, MarkerSize: 0, InLegend: !named));
                    named = true;
                }

                // A small gap either side, so back-to-back stints read as two bars with the stop between them.
                var compound = stint.Compound;
                var wear = hasTrend ? $"  {fit!.DegradationPerLap:+0.00;−0.00} s/lap" : "";
                bands.Add(new ChartBand(stint.StartLap - 0.5 + 0.08, stint.EndLap + 0.5 - 0.08, isA ? 0 : 1,
                    $"{name} · {compound.ToUpperInvariant()}{wear}", $"{name} {Palette.CompoundLetter(compound)}",
                    Hex(Palette.Compound(compound)), color));
            }

            // No stint had a trend line: the dots carry the race's legend entry.
            if (!named && series.FindIndex(s => s.Color == color) is var i and >= 0)
            {
                series[i] = series[i] with { InLegend = true };
            }
        }

        Race(a, r.StintsA, "A", ColorA, isA: true);
        Race(b, r.StintsB, "B", ColorB, isA: false);

        return new ChartModel("Fuel-corrected lap time · clean laps · lines = tyre wear per stint", series,
            XLabel: "Lap", IntegerX: true, Bands: bands, YIsTime: true);
    }

    private static ChartModel BuildPositionChart(RaceInput a, RaceInput b)
    {
        ChartSeries Series(RaceInput race, string name, string color, ChartLine line)
        {
            var laps = race.Laps.Where(l => l.CarPosition is > 0).ToList();
            return new ChartSeries(name, color, laps.Select(l => (double)l.LapNumber).ToArray(), laps.Select(l => (double)l.CarPosition!.Value).ToArray(),
                Step: true, Line: line, LineWidth: 2.5f);
        }

        return new ChartModel("Position at the end of each lap", [Series(a, "Race A", ColorA, ChartLine.Dashed), Series(b, "Race B", ColorB, ChartLine.Solid)],
            XLabel: "Lap", InvertY: true, IntegerY: true, YPrefix: "P", IntegerX: true);
    }

    private static string Hex(IBrush brush) => brush is ISolidColorBrush solid ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : "#F4F6F9";
}
