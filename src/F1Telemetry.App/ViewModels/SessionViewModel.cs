using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

public sealed partial class RecordingItemViewModel(RecordingInfo info) : ObservableObject
{
    public RecordingInfo Info { get; private set; } = info;

    public string Title => $"{Info.TrackName} · {SessionTypes.Name(Info.SessionType)}";
    public string Subtitle => $"#{Info.Id} · {(Info.Format == Protocol.GameFormat.F1_26 ? "F1 26" : "F1 25")} · {Info.StartTime.LocalDateTime:g}";
    public bool IsLive => Info.EndTime is null;
    public string Description => Info.Description;

    public void Update(RecordingInfo info)
    {
        Info = info;
        OnPropertyChanged(string.Empty);
    }
}

public sealed record LapRowViewModel(
    LapRecord Lap,
    int Index,
    Chip LapTime,
    Chip S1,
    Chip S2,
    Chip S3,
    string Delta,
    IBrush DeltaBrush,
    string Position)
{
    public string Label => $"L{Lap.LapNumber}";
    public string Compound => Lap.Compound;
    public string TyreText => Lap.LapType == LapType.Regular ? $"{Lap.Compound.ToUpperInvariant()} {Lap.ActualCompound}" : Lap.LapType == LapType.Pit ? "PIT" : Lap.LapType == LapType.SafetyCar ? "SC" : "VSC";
    public IBrush TyreBrush => Lap.LapType == LapType.Regular ? Palette.TextMid : Palette.Slower;
}

public sealed record WheelRate(string Label, string Rate, IBrush Background, IBrush Foreground, IBrush Border);

public sealed record LapBar(string Label, double Height, IBrush Fill);

public sealed record StintViewModel(StintAnalysis Stint, IReadOnlyList<LapBar> Bars)
{
    public string Header => $"Stint {Stint.StintNumber} · {Stint.Compound}";
    public string Laps => $"L{Stint.StartLap}–{Stint.EndLap} · {Stint.RegularLapCount} clean";
    public string Compound => Stint.Compound;
    public bool HasData => Stint.RegularLapCount > 0;
    public bool HasPace => Stint.RegularLapCount >= 2;

    public string Lifespan => HasData ? $"{Stint.TyreLifespanLaps:0.0}" : "—";
    public string LifespanNote => HasData ? $"{Stint.AverageWearCombined:0.00}% wear per lap combined, to the wear limit" : "Drive one clean flying lap to build projections";
    public IReadOnlyList<WheelRate> Wheels { get; } = Enumerable.Range(0, 4).Select(i =>
    {
        var limiting = Stint.LimitingTyre?.Name == WheelValues.Names[i];
        var (bg, fg, border) = limiting ? (Palette.Raised, Palette.Slower, Palette.Slower) : (Palette.Raised, Palette.TextHi, Palette.Raised);
        return new WheelRate($"{WheelValues.ShortNames[i]} · {Stint.EndWear[i]:0.0}%", $"+{Stint.AverageWearPerLap[i]:0.00}", bg, fg, border);
    }).ToList();
    public bool HasLimiting => Stint.LimitingTyre is not null;
    public string LimitingName => Stint.LimitingTyre is { } t ? $"LIMITING TYRE · {WheelValues.ShortNames[Array.IndexOf(WheelValues.Names, t.Name)]}" : "";
    public string LimitingLaps => Stint.LimitingTyre is { } t ? $"{Math.Max(0, Math.Floor(t.LapsRemaining)):0} LAPS" : "";

    public string Pace => HasPace ? $"{Stint.PaceDegradationPerLap:+0.000;−0.000}" : "—";
    public IBrush PaceBrush => !HasPace ? Palette.TextLo : Stint.PaceDegradationPerLap > 0 ? Palette.Slower : Palette.Faster;
    public string PaceNote => HasPace ? $"{Stint.TotalStintTimeLoss:+0.00;−0.00} s over {Stint.RegularLapCount} clean laps (linear fit)" : "Needs 2+ clean laps for the pace regression";
    public string Best => Stint.BestLapFormatted;
    public string Average => Stint.AverageLapFormatted;

    public string Fuel => HasData ? $"{Stint.AverageFuelPerLap:0.00}" : "—";
    public string FuelNote => HasData ? $"{Stint.TotalFuelUsed:0.0} kg used on clean laps this stint" : "-";
}

/// <summary>Laps list, race-strategy post-mortem and position history for one recording (live or stored).</summary>
public sealed partial class SessionViewModel(TelemetryRuntime runtime) : ObservableObject
{
    private CancellationTokenSource? _loadCts;

    [ObservableProperty] public partial RecordingInfo? Recording { get; set; }
    [ObservableProperty] public partial string Header { get; set; } = "Select a recording";
    [ObservableProperty] public partial LapRowViewModel? SelectedLap { get; set; }
    [ObservableProperty] public partial StintViewModel? SelectedStint { get; set; }
    [ObservableProperty] public partial ChartModel? PositionChart { get; set; }
    [ObservableProperty] public partial int TargetLaps { get; set; } = 10;
    [ObservableProperty] public partial string FuelForTarget { get; set; } = "—";

    public ObservableCollection<LapRowViewModel> Laps { get; } = [];
    public ObservableCollection<StintViewModel> Stints { get; } = [];

    public IReadOnlyList<LapRecord> ClassifiedLaps { get; private set; } = [];

    /// <summary>Raised when the user picks a lap to open in the lap-detail view.</summary>
    public event Action<RecordingInfo, LapRecord, LapRecord?>? LapOpened;

    partial void OnSelectedLapChanged(LapRowViewModel? value)
    {
        if (value is not null && Recording is { } recording)
        {
            LapOpened?.Invoke(recording, value.Lap, ClassifiedLaps.FirstOrDefault(l => l.IsBestLap && l.LapNumber != value.Lap.LapNumber));
        }
    }

    partial void OnSelectedStintChanged(StintViewModel? value) => UpdateFuelCalculator();

    partial void OnTargetLapsChanged(int value) => UpdateFuelCalculator();

    private void UpdateFuelCalculator() =>
        FuelForTarget = SelectedStint is { HasData: true } s ? $"{s.Stint.FuelForLaps(TargetLaps):0.00}" : "—";

    public async Task LoadAsync(RecordingInfo? recording)
    {
        Recording = recording;
        if (recording is null)
        {
            Header = "Select a recording";
            Laps.Clear();
            Stints.Clear();
            PositionChart = null;
            return;
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (Recording is not { } recording)
        {
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        try
        {
            var fresh = await runtime.Store.GetRecordingAsync(recording.Id, cts.Token) ?? recording;
            var laps = await runtime.Analysis.GetClassifiedLapsAsync(fresh, cts.Token);
            var analysis = await runtime.Analysis.AnalyzeAsync(fresh, laps, cts.Token);
            var positions = await runtime.Store.GetPositionHistoryAsync(fresh.Id, 500, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Recording = fresh;
            ClassifiedLaps = laps;
            Header = $"{fresh.TrackName} · {SessionTypes.Name(fresh.SessionType)} · {laps.Count} laps";
            FillLaps(laps);
            FillStints(analysis);
            PositionChart = new ChartModel("Race position", [new ChartSeries("Position", "#C77DFF",
                positions.Select(p => p.SessionTime).ToArray(), positions.Select(p => (double)p.Position).ToArray())],
                XLabel: "Session time", InvertY: true, IntegerY: true, YPrefix: "P", XIsTime: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void FillLaps(IReadOnlyList<LapRecord> laps)
    {
        var bestMs = laps.Where(l => l.HasTime).Select(l => l.LapTimeMs).DefaultIfEmpty().Min();
        Laps.Clear();
        for (var i = 0; i < laps.Count; i++)
        {
            var lap = laps[i];
            var delta = lap.HasTime && bestMs > 0 ? (int)lap.LapTimeMs - (int)bestMs : (int?)null;
            var time = TimeFormat.Lap(lap.LapTimeMs);
            Laps.Add(new LapRowViewModel(
                lap, i + 1,
                !lap.HasTime ? Chip.Empty : lap.IsBestLap ? Chip.Filled(time, Palette.SessionBest) : lap.IsValid ? Chip.Plain(time, Palette.TextHi) : Chip.Outlined(time, Palette.Danger),
                SectorChip(lap.Sector1Ms, lap.IsBestSector1),
                SectorChip(lap.Sector2Ms, lap.IsBestSector2),
                SectorChip(lap.Sector3Ms, lap.IsBestSector3),
                delta switch { null => "—", 0 => "BEST", _ => TimeFormat.Delta(delta.Value) },
                delta switch { null => Palette.TextLo, 0 => Palette.SessionBest, _ => Palette.Slower },
                lap.CarPosition is { } p ? $"P{p}" : "—"));
        }
    }

    private static Chip SectorChip(uint ms, bool best) =>
        ms == 0 ? Chip.Empty : best ? Chip.Filled(TimeFormat.Sector(ms), Palette.SessionBest) : Chip.Plain(TimeFormat.Sector(ms), Palette.TextHi);

    private void FillStints(SessionAnalysis? analysis)
    {
        var selected = SelectedStint?.Stint.StintNumber;
        Stints.Clear();
        foreach (var stint in analysis?.Stints ?? [])
        {
            Stints.Add(new StintViewModel(stint, BuildBars(stint)));
        }

        SelectedStint = Stints.FirstOrDefault(s => s.Stint.StintNumber == selected) ?? Stints.LastOrDefault();
    }

    /// <summary>Lap-time bars for the pace card: clean laps of the stint, scaled between the fastest and slowest.</summary>
    private List<LapBar> BuildBars(StintAnalysis stint)
    {
        var laps = ClassifiedLaps.Where(l => l.LapNumber >= stint.StartLap && l.LapNumber <= stint.EndLap && l.LapType == LapType.Regular && l.HasTime).ToList();
        if (laps.Count == 0)
        {
            return [];
        }

        double min = laps.Min(l => l.LapTimeMs), max = laps.Max(l => l.LapTimeMs);
        var span = Math.Max(1, max - min);
        return laps.Select(l => new LapBar($"L{l.LapNumber}", 24 + (l.LapTimeMs - min) / span * 76, l.LapTimeMs == min ? Palette.SessionBest : Palette.LineStrong)).ToList();
    }
}
