using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Controls;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

/// <summary>A deploy-mode chip with the distance driven in that mode: <c>M</c> 2.15 km.</summary>
public sealed record ModeShare(string Letter, string Name, string Distance, IBrush Fill, IBrush Foreground);

/// <summary>An entry of the map legend: a sample of the ribbon (<paramref name="Thickness"/> px) and its label.</summary>
public sealed record LegendEntry(string Label, IBrush Swatch, double Thickness = 6);

/// <summary>A straight-line fit of the car model, as a tile.</summary>
public sealed record FitTile(string Title, string DragArea, string Efficiency, string Quality);

/// <summary>One lap in the energy table.</summary>
public sealed record EnergyLapRow(LapEnergy Energy, LapRecord? Lap)
{
    public int LapNumber => Energy.LapNumber;
    public string Label => $"L{LapNumber}";
    public Chip Time => Lap is not { HasTime: true } lap ? Chip.Empty
        : lap.IsBestLap ? Chip.Filled(TimeFormat.Lap(lap.LapTimeMs), Palette.SessionBest)
        : Chip.Plain(TimeFormat.Lap(lap.LapTimeMs), Palette.TextHi);
    public string Start => EnergyText.Mj(Energy.StartStore);
    public IBrush StartBrush => EnergyText.Band(Energy.StartStore);
    public string End => EnergyText.Mj(Energy.EndStore);
    public IBrush EndBrush => EnergyText.Band(Energy.EndStore);
    public string Net => EnergyText.SignedMj(Energy.NetChange);
    public string Harvested => EnergyText.Mj(Energy.Harvested);
    public string Deployed => EnergyText.Mj(Energy.Deployed);
    public string LimitAt => Energy.LimitReachedAt is { } d ? EnergyText.Km(d) : "—";
    public IReadOnlyList<Chip> Flags { get; } = EnergyText.Flags(Energy);
    public string Kind => Lap?.LapType switch
    {
        LapType.Pit => "PIT",
        LapType.SafetyCar => "SC",
        LapType.VirtualSafetyCar => "VSC",
        _ => "",
    };
}

/// <summary>Number formats and chips shared by the energy table, summary and overlays.</summary>
public static class EnergyText
{
    public static string Mj(double joules) => (joules / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>+0.42 / −0.42, with a real minus sign.</summary>
    public static string SignedMj(double joules) =>
        (joules < -5_000 ? "−" : "+") + Math.Abs(joules / 1_000_000).ToString("0.00", CultureInfo.InvariantCulture);

    public static string Km(double metres) => (metres / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " km";

    /// <summary>Battery level in the ERS charge bands.</summary>
    public static IBrush Band(double joules) => Palette.Ers(joules / EnergyAnalyzer.Capacity * 100);

    public static IReadOnlyList<Chip> Flags(LapEnergy energy) =>
    [
        .. energy.Issues.Select(issue =>
        {
            var seconds = issue.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            return issue.Kind switch
            {
                EnergyWaste.Flat => Chip.Filled("FLAT " + seconds, Palette.Danger),
                EnergyWaste.Full => Chip.Outlined("FULL " + seconds, Palette.Slower),
                EnergyWaste.Fade => Chip.Outlined("FADE " + seconds, Palette.Slower),
                _ => Chip.Outlined("CAP " + seconds, Palette.Slower),
            };
        }),
    ];

    public static string Explain(EnergyIssue issue)
    {
        var seconds = issue.Seconds.ToString("0.0", CultureInfo.InvariantCulture);
        return issue.Kind switch
        {
            EnergyWaste.Flat => $"FLAT · {seconds} s at full throttle with an empty battery, so no electric power.",
            EnergyWaste.Full => $"FULL · {seconds} s braking with a full battery: that energy could not be stored.",
            EnergyWaste.Fade => $"FADE · {seconds} s deploying above the speed where the mode's power drops (see the car model).",
            _ => $"CAP · {seconds} s braking after the harvest limit: nothing more could be harvested this lap.",
        };
    }
}

/// <summary>
/// Energy tab: for every lap of the selected recording, what the battery did (start and end level, MJ harvested and
/// deployed, where the harvest limit was reached, wasted energy), and for the chosen lap a battery map on the track,
/// the battery trace along the lap with the deploy modes under it, and a summary.
/// </summary>
public sealed partial class EnergyViewModel(TelemetryRuntime runtime) : ObservableObject
{
    private int _loadVersion;
    private int _lapVersion;

    [ObservableProperty] public partial RecordingInfo? Recording { get; set; }
    [ObservableProperty] public partial string Header { get; set; } = "Select a recording";
    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "Select a recording in the sidebar.";
    [ObservableProperty] public partial EnergyLapRow? SelectedLap { get; set; }
    [ObservableProperty] public partial MapColoring Layer { get; set; } = MapColoring.DeployMode;
    [ObservableProperty] public partial TrackOutline? Outline { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TelemetrySample>? Samples { get; set; }
    [ObservableProperty] public partial ChartModel? Trace { get; set; }
    [ObservableProperty] public partial string LapTitle { get; set; } = "";
    [ObservableProperty] public partial string TrackName { get; set; } = "";
    [ObservableProperty] public partial string StartLevel { get; set; } = "—";
    [ObservableProperty] public partial IBrush StartBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string EndLevel { get; set; } = "—";
    [ObservableProperty] public partial IBrush EndBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string Harvested { get; set; } = "—";
    [ObservableProperty] public partial string HarvestNote { get; set; } = "";
    [ObservableProperty] public partial string Deployed { get; set; } = "—";
    [ObservableProperty] public partial string DeployNote { get; set; } = "";
    [ObservableProperty] public partial ErsCarModel? Model { get; set; }
    [ObservableProperty] public partial string ModelHeader { get; set; } = "";
    [ObservableProperty] public partial string ModelNote { get; set; } = "";
    [ObservableProperty] public partial ChartModel? DeployChart { get; set; }
    [ObservableProperty] public partial ChartModel? AlongLapChart { get; set; }

    public ObservableCollection<EnergyLapRow> Laps { get; } = [];
    public ObservableCollection<ModeShare> Modes { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];
    public ObservableCollection<LegendEntry> Legend { get; } = [];
    public ObservableCollection<FitTile> Fits { get; } = [];

    public bool HasDeployChart => DeployChart is not null;
    public bool HasAlongLapChart => AlongLapChart is not null;

    partial void OnDeployChartChanged(ChartModel? value) => OnPropertyChanged(nameof(HasDeployChart));
    partial void OnAlongLapChartChanged(ChartModel? value) => OnPropertyChanged(nameof(HasAlongLapChart));

    public bool IsModeLayer => Layer == MapColoring.DeployMode;
    public bool IsBatteryLayer => Layer == MapColoring.Battery;
    public bool HasIssues => Issues.Count > 0;

    partial void OnLayerChanged(MapColoring value)
    {
        OnPropertyChanged(nameof(IsModeLayer));
        OnPropertyChanged(nameof(IsBatteryLayer));
        FillLegend();
    }

    [RelayCommand]
    private void SetLayer(MapColoring layer) => Layer = layer;

    partial void OnSelectedLapChanged(EnergyLapRow? value) => _ = ShowLapAsync(value);

    /// <summary>Shows <paramref name="recording"/>, unless it is already shown and finished (a live one is reloaded).</summary>
    public async Task LoadAsync(RecordingInfo? recording)
    {
        if (recording is not null && recording.Id == Recording?.Id && recording.EndTime is not null && Laps.Count > 0)
        {
            return;
        }

        Recording = recording;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        if (Recording is not { } recording)
        {
            Clear("Select a recording in the sidebar.");
            Header = "Select a recording";
            return;
        }

        var laps = await runtime.Analysis.GetClassifiedLapsAsync(recording);
        var samples = await runtime.Store.GetEnergySamplesAsync(recording.Id);
        var model = recording.TrackId >= 0
            ? await Task.Run(async () => ErsModelBuilder.Build(recording.Format, recording.TrackId,
                await runtime.Store.GetModelSamplesAsync(recording.Format, recording.TrackId)))
            : null;
        var energies = EnergyAnalyzer.AnalyzeLaps(samples, model?.Deploy);
        if (version != _loadVersion)
        {
            return;
        }

        ShowModel(model, recording);

        Header = $"{recording.TrackName} · {SessionTypes.Name(recording.SessionType)} · {energies.Count} laps";
        TrackName = recording.TrackName.ToUpperInvariant();
        Outline = runtime.Tracks.ForTrackId(recording.TrackId);
        if (energies.Count == 0 || samples.All(s => s.ErsStoreEnergy == 0 && s.ErsDeployed == 0))
        {
            Clear("No battery data in this recording.");
            return;
        }

        var previous = SelectedLap?.LapNumber;
        Laps.Clear();
        foreach (var energy in energies)
        {
            Laps.Add(new EnergyLapRow(energy, laps.FirstOrDefault(l => l.LapNumber == energy.LapNumber)));
        }

        HasData = true;
        FillLegend();
        SelectedLap = Laps.FirstOrDefault(r => r.LapNumber == previous)
                      ?? Laps.FirstOrDefault(r => r.Lap?.IsBestLap == true)
                      ?? Laps.FirstOrDefault(r => r.Lap?.HasTime == true)
                      ?? Laps[0];
    }

    /// <summary>The car model learned from every lap at this track: deploy map, energy along the lap, straight-line fits.</summary>
    private void ShowModel(ErsCarModel? model, RecordingInfo recording)
    {
        Model = model;
        Fits.Clear();
        if (model is null || model.Laps == 0)
        {
            ModelHeader = "";
            ModelNote = "";
            DeployChart = null;
            AlongLapChart = null;
            return;
        }

        var format = model.Format == Protocol.GameFormat.F1_26 ? "F1 26" : "F1 25";
        ModelHeader = $"CAR MODEL · {recording.TrackName.ToUpperInvariant()} · {format} · LEARNED FROM {model.Laps} LAPS";
        ModelNote = model.HasPowerData
            ? "What each deploy mode delivers by speed, where the lap harvests and spends energy, and how the car accelerates on the straights. "
              + "The lap plans are built on this model, and it gets better with every lap you record here."
            : "The deploy map and straight-line fit need ICE and MGU-K power, recorded from this version on. Re-import older captures with f1tel reimport to add it.";

        DeployChart = model.Deploy.Curves.Count == 0 ? null : new ChartModel("MGU-K output by speed (kW) · full throttle, charge in the battery",
            [.. model.Deploy.Curves.Select(c => new ChartSeries($"{DeployModes.Letter(c.Mode)} · {DeployModes.Name(c.Mode)}", Palette.DeployModeHex[Math.Clamp(c.Mode, 0, 3)],
                [.. c.Points.Select(p => p.SpeedKmh + ErsModelBuilder.SpeedBand / 2.0)], [.. c.Points.Select(p => p.PowerW / 1000)], MarkerSize: 6))],
            XLabel: "Speed (km/h)",
            Markers: [.. model.Deploy.Curves.Where(c => c.FadeFromKmh is not null)
                .Select(c => new ChartMarker(c.FadeFromKmh!.Value, Palette.DeployModeHex[Math.Clamp(c.Mode, 0, 3)], $"{DeployModes.Name(c.Mode)} fades from {c.FadeFromKmh} km/h"))]);

        AlongLapChart = model.AlongLap.Count == 0 ? null : new ChartModel("Energy along the lap (kJ per 50 m) · average lap",
        [
            new ChartSeries("Harvested", "#2EE88F", [.. model.AlongLap.Select(b => b.From)], [.. model.AlongLap.Select(b => b.HarvestedJ / 1000)], Step: true),
            new ChartSeries("Deployed", "#4DB5FF", [.. model.AlongLap.Select(b => b.From)], [.. model.AlongLap.Select(b => b.DeployedJ / 1000)], Step: true),
        ]);

        var lowestDrag = model.StraightLine.Count > 1 ? model.StraightLine.MinBy(f => f.DragArea) : null;
        foreach (var fit in model.StraightLine)
        {
            var title = model.Format == Protocol.GameFormat.F1_26
                ? $"ACTIVE AERO {fit.Aero}" + (fit == lowestDrag ? " · LOW DRAG" : "")
                : fit.Aero == 0 ? "DRS CLOSED" : "DRS OPEN";
            Fits.Add(new FitTile(title,
                fit.DragArea.ToString("0.00", CultureInfo.InvariantCulture),
                (fit.Efficiency * 100).ToString("0", CultureInfo.InvariantCulture),
                $"R² {fit.RSquared.ToString("0.00", CultureInfo.InvariantCulture)} · ±{fit.RmsError.ToString("0.0", CultureInfo.InvariantCulture)} m/s² · {fit.Samples:N0} samples"));
        }
    }

    private void Clear(string reason)
    {
        Laps.Clear();
        Modes.Clear();
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        SelectedLap = null;
        Samples = null;
        Trace = null;
        HasData = false;
        EmptyText = reason;
    }

    private async Task ShowLapAsync(EnergyLapRow? row)
    {
        var version = ++_lapVersion;
        if (row is null || Recording is not { } recording)
        {
            return;
        }

        var samples = await runtime.Store.GetLapSamplesAsync(recording.Id, row.LapNumber);
        if (version != _lapVersion)
        {
            return;
        }

        var energy = row.Energy;
        LapTitle = $"Lap {row.LapNumber}" + (row.Lap is { HasTime: true } lap ? $" · {TimeFormat.Lap(lap.LapTimeMs)}" : "");
        StartLevel = EnergyText.Mj(energy.StartStore);
        StartBrush = EnergyText.Band(energy.StartStore);
        EndLevel = EnergyText.Mj(energy.EndStore);
        EndBrush = EnergyText.Band(energy.EndStore);
        Harvested = EnergyText.Mj(energy.Harvested);
        HarvestNote = energy.HarvestLimit <= 0 ? "No harvest limit reported (F1 25 format)."
            : energy.LimitReachedAt is { } at ? $"Limit {EnergyText.Mj(energy.HarvestLimit)} MJ, reached at {EnergyText.Km(at)}."
            : $"Limit {EnergyText.Mj(energy.HarvestLimit)} MJ, not reached.";
        Deployed = EnergyText.Mj(energy.Deployed);
        DeployNote = $"Battery {EnergyText.Mj(energy.MinStore)}–{EnergyText.Mj(energy.MaxStore)} MJ over the lap, {EnergyText.SignedMj(energy.NetChange)} MJ net.";

        Modes.Clear();
        foreach (var group in energy.Modes.GroupBy(m => m.Mode).OrderBy(g => g.Key))
        {
            var metres = group.Sum(m => m.To - m.From);
            Modes.Add(new ModeShare(DeployModes.Letter(group.Key), DeployModes.Name(group.Key).ToUpperInvariant(), EnergyText.Km(metres),
                Palette.DeployMode(group.Key), Palette.OnDeployMode(group.Key)));
        }

        Issues.Clear();
        foreach (var issue in energy.Issues)
        {
            Issues.Add(EnergyText.Explain(issue));
        }

        OnPropertyChanged(nameof(HasIssues));
        Samples = samples;
        Trace = BuildTrace(samples, energy);
    }

    /// <summary>Battery level along the lap, the deploy modes under it, and where the harvest limit was reached.</summary>
    private static ChartModel BuildTrace(IReadOnlyList<TelemetrySample> samples, LapEnergy energy)
    {
        var lap = samples.Where(s => s.LapDistance >= 0).ToList();
        var series = new ChartSeries("Battery", "#F4F6F9", [.. lap.Select(s => s.LapDistance)], [.. lap.Select(s => s.ErsStoreEnergy / 1_000_000)]);
        var segments = energy.Modes.Select(m => new ChartTimelineSegment(m.From, m.To, DeployModes.Letter(m.Mode), "", "",
            m.Mode == DeployModes.None ? "#8D97A6" : Palette.DeployModeHex[Math.Clamp(m.Mode, 0, 3)],
            Palette.DeployModeHex[Math.Clamp(m.Mode, 0, 3)])).ToList();
        IReadOnlyList<ChartMarker> markers = energy.LimitReachedAt is { } at ? [new ChartMarker(at, "#4DB5FF", "Harvest limit reached")] : [];
        return new ChartModel("Battery (MJ) · deploy mode under the trace", [series], Markers: markers,
            Timeline: segments.Count > 0 ? [new ChartTimelineRow("", "#C3CAD5", segments, [])] : null);
    }

    private void FillLegend()
    {
        Legend.Clear();
        if (Layer == MapColoring.DeployMode)
        {
            Legend.Add(new LegendEntry("N · NONE", Palette.TextLo, 2));
            foreach (var mode in new[] { DeployModes.Medium, DeployModes.Hotlap, DeployModes.Overtake })
            {
                Legend.Add(new LegendEntry($"{DeployModes.Letter(mode)} · {DeployModes.Name(mode).ToUpperInvariant()}", Palette.DeployMode(mode)));
            }
        }
        else
        {
            Legend.Add(new LegendEntry("2.0 MJ AND UP", Palette.Faster));
            Legend.Add(new LegendEntry("0.8–2.0 MJ", Palette.Slower));
            Legend.Add(new LegendEntry("BELOW 0.8 MJ", Palette.Danger));
        }
    }
}
