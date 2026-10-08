using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

/// <summary>A lap-detail tab: a group of charts, or (<paramref name="IsTrajectory"/>) the full-size track map.</summary>
public sealed record ChartTabViewModel(string Name, IReadOnlyList<ChartModel> Charts, bool IsTrajectory = false);

public sealed record SectorCard(string Label, string Time, Chip Delta);

/// <summary>One entry of the lap pickers. <see cref="Lap"/> is null for the "no comparison" entry.</summary>
public sealed record LapOption(RecordingInfo? Recording, LapRecord? Lap, string Label)
{
    public static LapOption None { get; } = new(null, null, "NO COMPARISON");

    public static LapOption For(RecordingInfo recording, LapRecord lap)
    {
        var tags = new List<string>();
        if (lap.IsBestLap)
        {
            tags.Add("BEST");
        }

        if (!lap.IsValid)
        {
            tags.Add("INVALID");
        }

        if (lap.LapType != LapType.Regular)
        {
            tags.Add(lap.LapType switch { LapType.Pit => "PIT", LapType.SafetyCar => "SC", _ => "VSC" });
        }

        var time = lap.HasTime ? TimeFormat.Lap(lap.LapTimeMs) : "no time";
        return new LapOption(recording, lap, $"L{lap.LapNumber}  {time}" + (tags.Count > 0 ? "  · " + string.Join(" · ", tags) : ""));
    }

    public override string ToString() => Label;
}

public sealed record RecordingOption(RecordingInfo Recording, string Label)
{
    public override string ToString() => Label;
}

public sealed record XAxisOption(XAxisMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Full telemetry of one lap: grouped charts, map and sector deltas, compared against any other lap — from the
/// same session or another recording on the same track (defaults to the session-best lap).
/// </summary>
public sealed partial class LapDetailViewModel(TelemetryRuntime runtime) : ObservableObject
{
    private readonly Dictionary<(long Recording, int Lap), IReadOnlyList<TelemetrySample>> _cache = [];
    private RecordingInfo? _recording;
    private bool _suppress;
    private int _loadVersion;
    private LapOption? _reference;

    [ObservableProperty] public partial string Context { get; set; } = "LAP DETAIL";
    [ObservableProperty] public partial string Title { get; set; } = "Open a lap from the Laps tab";
    [ObservableProperty] public partial string LapTime { get; set; } = "";
    [ObservableProperty] public partial Chip? DeltaChip { get; set; }
    [ObservableProperty] public partial string Compound { get; set; } = "Unknown";
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial bool HasReference { get; set; }
    [ObservableProperty] public partial string ReferenceLegend { get; set; } = "";
    [ObservableProperty] public partial TrackOutline? Outline { get; set; }
    [ObservableProperty] public partial string TrackName { get; set; } = "";
    [ObservableProperty] public partial IReadOnlyList<TelemetrySample>? Samples { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TelemetrySample>? ReferenceSamples { get; set; }
    [ObservableProperty] public partial bool ColorByInputs { get; set; } = true;
    [ObservableProperty] public partial ChartTabViewModel? SelectedTab { get; set; }

    public bool ShowTrajectory => SelectedTab?.IsTrajectory == true;
    public bool ShowCharts => !ShowTrajectory;

    partial void OnSelectedTabChanged(ChartTabViewModel? value)
    {
        OnPropertyChanged(nameof(ShowTrajectory));
        OnPropertyChanged(nameof(ShowCharts));
    }

    [ObservableProperty] public partial LapOption? SelectedLap { get; set; }
    [ObservableProperty] public partial RecordingOption? SelectedCompareRecording { get; set; }
    [ObservableProperty] public partial LapOption? SelectedCompareLap { get; set; }
    [ObservableProperty] public partial XAxisOption SelectedXAxis { get; set; } = XAxisOptions[0];

    public static IReadOnlyList<XAxisOption> XAxisOptions { get; } =
    [
        new(XAxisMode.LapTime, "LAP TIME"),
        new(XAxisMode.Distance, "DISTANCE"),
    ];

    public ObservableCollection<ChartTabViewModel> Tabs { get; } = [];
    public ObservableCollection<SectorCard> Sectors { get; } = [];

    /// <summary>Laps of the opened recording (primary lap picker).</summary>
    public ObservableCollection<LapOption> LapOptions { get; } = [];

    /// <summary>Recordings on the same track that a comparison lap can come from.</summary>
    public ObservableCollection<RecordingOption> CompareRecordings { get; } = [];

    /// <summary>Laps of <see cref="SelectedCompareRecording"/>, preceded by "no comparison".</summary>
    public ObservableCollection<LapOption> CompareLaps { get; } = [];

    public bool HasLapOptions => LapOptions.Count > 0;

    /// <summary>Opens a lap from the Laps tab, compared with <paramref name="referenceLap"/> of the same recording.</summary>
    public async Task LoadAsync(RecordingInfo recording, LapRecord lap, LapRecord? referenceLap)
    {
        IsLoading = true;
        _suppress = true;
        try
        {
            if (_recording?.Id != recording.Id)
            {
                _cache.Clear();
            }

            _recording = recording;
            var laps = await runtime.Analysis.GetClassifiedLapsAsync(recording);
            if (!laps.Any(l => l.LapNumber == lap.LapNumber))
            {
                laps = [.. laps, lap];
            }

            Fill(LapOptions, laps.Select(l => LapOption.For(recording, l)));
            OnPropertyChanged(nameof(HasLapOptions));
            SelectedLap = LapOptions.First(o => o.Lap!.LapNumber == lap.LapNumber);

            var recordings = await runtime.Store.GetRecordingsAsync();
            Fill(CompareRecordings, recordings
                .Where(r => r.TrackId == recording.TrackId)
                .Select(r => new RecordingOption(r, r.Id == recording.Id
                    ? $"THIS SESSION · #{r.Id}"
                    : $"#{r.Id} · {SessionTypes.Name(r.SessionType)} · {r.StartTime.LocalDateTime:g}")));
            if (CompareRecordings.All(r => r.Recording.Id != recording.Id))
            {
                CompareRecordings.Insert(0, new RecordingOption(recording, $"THIS SESSION · #{recording.Id}"));
            }

            SelectedCompareRecording = CompareRecordings.First(r => r.Recording.Id == recording.Id);
            Fill(CompareLaps, [LapOption.None, .. LapOptions]);
            SelectedCompareLap = referenceLap is null
                ? LapOption.None
                : CompareLaps.FirstOrDefault(o => o.Lap?.LapNumber == referenceLap.LapNumber) ?? LapOption.None;
        }
        finally
        {
            _suppress = false;
        }

        await ReloadAsync();
    }

    partial void OnSelectedLapChanged(LapOption? value)
    {
        if (!_suppress)
        {
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedCompareLapChanged(LapOption? value)
    {
        if (!_suppress)
        {
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedXAxisChanged(XAxisOption value)
    {
        if (!_suppress && Samples is { } samples)
        {
            BuildCharts(samples, ReferenceSamples);
        }
    }

    partial void OnSelectedCompareRecordingChanged(RecordingOption? value)
    {
        if (!_suppress && value is not null)
        {
            _ = SwitchCompareRecordingAsync(value.Recording);
        }
    }

    /// <summary>Lists the laps of another recording and preselects its best lap.</summary>
    private async Task SwitchCompareRecordingAsync(RecordingInfo recording)
    {
        IReadOnlyList<LapOption> options;
        if (recording.Id == _recording?.Id)
        {
            options = LapOptions.ToList();
        }
        else
        {
            var laps = await runtime.Analysis.GetClassifiedLapsAsync(recording);
            options = laps.Select(l => LapOption.For(recording, l)).ToList();
        }

        if (SelectedCompareRecording?.Recording.Id != recording.Id)
        {
            return;
        }

        _suppress = true;
        try
        {
            Fill(CompareLaps, [LapOption.None, .. options]);
            var primary = SelectedLap?.Lap?.LapNumber;
            SelectedCompareLap = options.FirstOrDefault(o => o.Lap!.IsBestLap && (recording.Id != _recording?.Id || o.Lap.LapNumber != primary))
                                 ?? options.Where(o => o.Lap!.HasTime).MinBy(o => o.Lap!.LapTimeMs)
                                 ?? LapOption.None;
        }
        finally
        {
            _suppress = false;
        }

        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_recording is not { } recording || SelectedLap?.Lap is not { } lap)
        {
            return;
        }

        var reference = SelectedCompareLap is { Lap: not null, Recording: not null } r ? r : null;
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            var samples = await GetSamplesAsync(recording.Id, lap.LapNumber);
            var referenceSamples = reference is null ? null : await GetSamplesAsync(reference.Recording!.Id, reference.Lap!.LapNumber);
            if (version != _loadVersion)
            {
                return;
            }

            _reference = reference;
            Show(recording, lap, reference, samples, referenceSamples);
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    private async Task<IReadOnlyList<TelemetrySample>> GetSamplesAsync(long recordingId, int lapNumber)
    {
        // Laps of a live recording keep growing, so only finished recordings are cached.
        if (_cache.TryGetValue((recordingId, lapNumber), out var cached))
        {
            return cached;
        }

        var samples = await runtime.Store.GetLapSamplesAsync(recordingId, lapNumber);
        var live = recordingId == runtime.Recorder.ActiveRecordingId;
        if (!live)
        {
            if (_cache.Count > 16)
            {
                _cache.Clear();
            }

            _cache[(recordingId, lapNumber)] = samples;
        }

        return samples;
    }

    private string ReferenceLabel(LapOption reference) =>
        reference.Recording!.Id == _recording?.Id ? $"L{reference.Lap!.LapNumber}" : $"L{reference.Lap!.LapNumber} #{reference.Recording.Id}";

    private void Show(RecordingInfo recording, LapRecord lap, LapOption? reference, IReadOnlyList<TelemetrySample> samples, IReadOnlyList<TelemetrySample>? referenceSamples)
    {
        var referenceLap = reference?.Lap;
        var referenceLabel = reference is null ? "" : ReferenceLabel(reference);

        Context = ($"{recording.TrackName} · {SessionTypes.Name(recording.SessionType)}"
                   + (reference is null ? "" : $" · compared with {referenceLabel}" + (referenceLap!.IsBestLap ? " (best)" : ""))
                   + $" · {samples.Count:N0} samples").ToUpperInvariant();
        Title = $"Lap {lap.LapNumber}";
        LapTime = TimeFormat.Lap(lap.LapTimeMs);
        Compound = lap.Compound;
        TrackName = recording.TrackName.ToUpperInvariant();
        HasReference = referenceSamples is { Count: > 0 };
        ReferenceLegend = referenceLabel.ToUpperInvariant();

        if (referenceLap is { HasTime: true } && lap.HasTime)
        {
            var delta = (int)lap.LapTimeMs - (int)referenceLap.LapTimeMs;
            DeltaChip = delta <= 0
                ? Chip.Filled("▼ " + TimeFormat.Delta(delta).Replace('-', '−'), Palette.Faster)
                : Chip.Outlined("▲ " + TimeFormat.Delta(delta), Palette.Slower);
        }
        else
        {
            DeltaChip = lap.IsBestLap ? Chip.Filled("BEST", Palette.SessionBest) : null;
        }

        Sectors.Clear();
        (string Label, uint Ms, uint? Ref)[] sectors =
        [
            ("S1", lap.Sector1Ms, referenceLap?.Sector1Ms),
            ("S2", lap.Sector2Ms, referenceLap?.Sector2Ms),
            ("S3", lap.Sector3Ms, referenceLap?.Sector3Ms),
        ];
        foreach (var (label, ms, refMs) in sectors)
        {
            var chip = ms == 0 || refMs is not > 0
                ? Chip.Empty
                : (int)ms - (int)refMs.Value is var d && d <= 0
                    ? Chip.Filled("▼ " + TimeFormat.Delta(d).Replace('-', '−'), Palette.Faster)
                    : Chip.Outlined("▲ " + TimeFormat.Delta((int)ms - (int)refMs.Value), Palette.Slower);
            Sectors.Add(new SectorCard(label, TimeFormat.Sector(ms), chip));
        }

        Outline = runtime.Tracks.ForTrackId(recording.TrackId);
        Samples = samples;
        ReferenceSamples = referenceSamples;
        HasData = samples.Count > 0;
        BuildCharts(samples, referenceSamples);
    }

    private void BuildCharts(IReadOnlyList<TelemetrySample> samples, IReadOnlyList<TelemetrySample>? reference)
    {
        var mode = SelectedXAxis.Mode;
        var referenceLabel = _reference is null ? "" : ReferenceLabel(_reference);
        var selectedName = SelectedTab?.Name;
        Tabs.Clear();
        foreach (var (name, charts) in ChartCatalog.Tabs)
        {
            var models = charts.Select(c => c.Build(samples, reference, referenceLabel, mode)).ToList();
            if (name == "Dynamics" && ChartCatalog.BuildTimeDelta(samples, reference, referenceLabel, mode) is { } delta)
            {
                models.Insert(0, delta);
            }

            // Right under the battery trace of both laps.
            if (name == "Energy" && ChartCatalog.BuildBatteryDelta(samples, reference, referenceLabel, mode) is { } battery)
            {
                models.Insert(2, battery);
            }

            Tabs.Add(new ChartTabViewModel(name.ToUpperInvariant(), models));
        }

        Tabs.Add(new ChartTabViewModel("TRAJECTORY", [], IsTrajectory: true));
        SelectedTab = Tabs.FirstOrDefault(t => t.Name == selectedName) ?? Tabs.FirstOrDefault();
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
