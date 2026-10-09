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

/// <summary>One lap in the energy table; <paramref name="InSession"/> when it is one of the laps the session analysis averages.</summary>
public sealed record EnergyLapRow(LapEnergy Energy, LapRecord? Lap, EnergyUnit Unit, bool InSession)
{
    public int LapNumber => Energy.LapNumber;
    public string Label => $"L{LapNumber}";
    public Chip Time => Lap is not { HasTime: true } lap ? Chip.Empty
        : lap.IsBestLap ? Chip.Filled(TimeFormat.Lap(lap.LapTimeMs), Palette.SessionBest)
        : Chip.Plain(TimeFormat.Lap(lap.LapTimeMs), Palette.TextHi);
    public string Start => EnergyText.Format(Energy.StartStore, Unit);
    public IBrush StartBrush => EnergyText.Band(Energy.StartStore);
    public string End => EnergyText.Format(Energy.EndStore, Unit);
    public IBrush EndBrush => EnergyText.Band(Energy.EndStore);
    public string Net => EnergyText.Signed(Energy.NetChange, Unit);
    public string Harvested => EnergyText.Format(Energy.Harvested, Unit);
    public string Deployed => EnergyText.Format(Energy.Deployed, Unit);
    public string LimitAt => Energy.LimitReachedAt is { } d ? EnergyText.Km(d) : "—";
    public IReadOnlyList<Chip> Flags { get; } = EnergyText.Flags(Energy);
    public double Opacity => InSession ? 1 : 0.45;
    public string Tip => InSession ? "One of the laps the session analysis is built on." : "Not a typical lap: left out of the session analysis and the race plan.";
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
    public static string SignedMj(double joules) => Signed(joules, EnergyUnit.Mj);

    /// <summary>An energy as a number in <paramref name="unit"/>: 2.40 (MJ) or 60 (% of the battery).</summary>
    public static string Format(double joules, EnergyUnit unit) => unit == EnergyUnit.Percent
        ? (joules / EnergyAnalyzer.Capacity * 100).ToString("0", CultureInfo.InvariantCulture)
        : Mj(joules);

    /// <summary><see cref="Format"/> with a sign: +0.42 / −0.42, or +11 / −11.</summary>
    public static string Signed(double joules, EnergyUnit unit) => (joules < -5_000 ? "−" : "+") + Format(Math.Abs(joules), unit);

    /// <summary><see cref="Format"/> with the unit: 2.40 MJ or 60%.</summary>
    public static string WithUnit(double joules, EnergyUnit unit) =>
        unit == EnergyUnit.Percent ? Format(joules, unit) + "%" : Format(joules, unit) + " MJ";

    public static string Unit(EnergyUnit unit) => unit == EnergyUnit.Percent ? "%" : "MJ";

    /// <summary>Multiplier from J to chart values in <paramref name="unit"/>.</summary>
    public static double Scale(EnergyUnit unit) => unit == EnergyUnit.Percent ? 100 / EnergyAnalyzer.Capacity : 1e-6;

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

    /// <summary>A kind of waste added up over the session: <paramref name="seconds"/> in all, on <paramref name="laps"/> laps.</summary>
    public static string Explain(EnergyWaste kind, double seconds, int laps)
    {
        var total = seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        var on = laps == 1 ? "1 lap" : $"{laps} laps";
        return kind switch
        {
            EnergyWaste.Flat => $"FLAT · {total} on {on} at full throttle with an empty battery, so no electric power.",
            EnergyWaste.Full => $"FULL · {total} on {on} braking with a full battery: that energy could not be stored.",
            EnergyWaste.Fade => $"FADE · {total} on {on} deploying above the speed where the mode's power drops (see the car model).",
            _ => $"CAP · {total} on {on} braking after the harvest limit: nothing more could be harvested on those laps.",
        };
    }
}

/// <summary>The energy tab's pages: the session as driven, the ERS plan built on it, and the car model behind the plan.</summary>
public enum EnergyPage
{
    Session,
    Plan,
    Model,
}

/// <summary>
/// Energy tab: what the battery did over the selected session. The map, the battery trace and the summary describe the
/// session as a whole: its typical lap (the laps without pit stops, safety cars or big mistakes, averaged), or the ERS
/// plan built on it. The table lists what the battery did on every lap.
/// </summary>
public sealed partial class EnergyViewModel : ObservableObject
{
    private readonly TelemetryRuntime runtime;
    private readonly SettingsService settings;
    private int _loadVersion;

    // The loaded session: per-lap energy, the laps it is analysed on, and their average along the lap.
    private IReadOnlyList<LapEnergy> _energies = [];
    private IReadOnlyList<LapRecord> _records = [];
    private IReadOnlyList<TelemetrySample> _energySamples = [];
    private HashSet<int> _sessionLaps = [];
    private TypicalLap? _typical;

    // The racing line the map draws on: the session's fastest lap (or a typical one).
    private IReadOnlyList<TelemetrySample>? _line;

    public EnergyViewModel(TelemetryRuntime runtime, EnergyPlanViewModel plan, SettingsService settings)
    {
        this.runtime = runtime;
        this.settings = settings;
        Plan = plan;
        Unit = settings.Current.EnergyUnit;
        Plan.Unit = Unit;
        plan.PlanChanged += _ => ShowGraphics();
    }

    /// <summary>The ERS plan for the session.</summary>
    public EnergyPlanViewModel Plan { get; }

    [ObservableProperty] public partial RecordingInfo? Recording { get; set; }
    [ObservableProperty] public partial string Header { get; set; } = "Select a recording";
    [ObservableProperty] public partial bool HasData { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "Select a recording in the sidebar.";
    [ObservableProperty] public partial MapColoring Layer { get; set; } = MapColoring.DeployMode;
    [ObservableProperty] public partial EnergyUnit Unit { get; set; }

    [ObservableProperty] public partial EnergyPage Page { get; set; }

    /// <summary>The map shows the plan (when there is one) rather than the session as driven: on the plan page.</summary>
    [ObservableProperty] public partial bool ShowPlanOnMap { get; set; }
    [ObservableProperty] public partial TrackOutline? Outline { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TelemetrySample>? Samples { get; set; }
    [ObservableProperty] public partial ChartModel? Trace { get; set; }
    [ObservableProperty] public partial string MapTitle { get; set; } = "";
    [ObservableProperty] public partial string TrackName { get; set; } = "";
    [ObservableProperty] public partial string SessionTitle { get; set; } = "";
    [ObservableProperty] public partial string SessionNote { get; set; } = "";
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
    public bool IsMj => Unit == EnergyUnit.Mj;
    public bool IsPercent => Unit == EnergyUnit.Percent;
    public string UnitLabel => EnergyText.Unit(Unit);

    /// <summary>The table's footnote, in the current unit.</summary>
    public string TableNote => $"All values in {(IsPercent ? "% of the battery" : "MJ")}. Faded laps are left out of the session analysis "
        + "(pit and safety car laps, and laps more than 3% off the median). START and END are the battery at the line, in the ERS charge bands. "
        + "FLAT: full throttle with an empty battery. FULL: braking with a full battery, so that energy is lost. CAP: braking after the lap's harvest limit. "
        + "FADE: deploying above the speed where the mode's power drops.";

    partial void OnLayerChanged(MapColoring value)
    {
        OnPropertyChanged(nameof(IsModeLayer));
        OnPropertyChanged(nameof(IsBatteryLayer));
        FillLegend();
    }

    partial void OnUnitChanged(EnergyUnit value)
    {
        OnPropertyChanged(nameof(IsMj));
        OnPropertyChanged(nameof(IsPercent));
        OnPropertyChanged(nameof(UnitLabel));
        OnPropertyChanged(nameof(TableNote));
        Plan.Unit = value;
        if (settings.Current.EnergyUnit != value)
        {
            settings.Current.EnergyUnit = value;
            settings.SaveSoon();
        }

        if (HasData)
        {
            FillRows();
            FillSummary();
            FillLegend();
            ShowGraphics();
        }
    }

    public bool IsSessionPage => Page == EnergyPage.Session;
    public bool IsPlanPage => Page == EnergyPage.Plan;
    public bool IsModelPage => Page == EnergyPage.Model;

    partial void OnPageChanged(EnergyPage value)
    {
        OnPropertyChanged(nameof(IsSessionPage));
        OnPropertyChanged(nameof(IsPlanPage));
        OnPropertyChanged(nameof(IsModelPage));
        ShowPlanOnMap = value == EnergyPage.Plan;
    }

    partial void OnShowPlanOnMapChanged(bool value) => ShowGraphics();

    [RelayCommand]
    private void SetPage(EnergyPage page) => Page = page;

    [RelayCommand]
    private void SetLayer(MapColoring layer) => Layer = layer;

    [RelayCommand]
    private void SetUnit(EnergyUnit unit) => Unit = unit;

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
        Plan.Clear();
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

        // The laps the session is analysed on: its typical laps, or every lap when none is timed.
        var withEnergy = energies.Select(e => e.LapNumber).ToHashSet();
        var representative = SessionLaps.Representative(laps).Where(l => withEnergy.Contains(l.LapNumber)).ToList();
        var sessionLaps = representative.Count > 0 ? representative.Select(l => l.LapNumber).ToHashSet() : withEnergy;
        var fastest = SessionLaps.Fastest(laps.Where(l => withEnergy.Contains(l.LapNumber)));
        var line = await runtime.Store.GetLapSamplesAsync(recording.Id, fastest?.LapNumber ?? sessionLaps.Min());
        if (version != _loadVersion)
        {
            return;
        }

        (_energies, _records, _energySamples, _sessionLaps, _line) = (energies, laps, samples, sessionLaps, line);
        _typical = SessionLaps.Typical(samples, sessionLaps);
        HasData = true;
        FillRows();
        FillSummary();
        FillLegend();
        ShowGraphics();

        Plan.Clear(loading: true);
        var (inputs, unavailable) = await PlanInputsAsync(recording, model, representative, fastest, line);
        if (version == _loadVersion)
        {
            await Plan.SetSessionAsync(inputs, unavailable);
        }
    }

    /// <summary>
    /// The laps the plans are built on: race plans on the session's typical laps averaged, qualifying plans on its fastest
    /// lap. Null, with the reason, when the session can't be planned.
    /// </summary>
    private async Task<(PlanInputs? Inputs, string Unavailable)> PlanInputsAsync(RecordingInfo recording, ErsCarModel? model,
        IReadOnlyList<LapRecord> representative, LapRecord? fastest, IReadOnlyList<TelemetrySample> fastestSamples)
    {
        const string reimport = "Recordings made before the app stored engine power can get it from their raw capture: close the app and run "
            + "f1tel reimport --db <telemetry.duckdb> <capture.f1rec> --replace (see the README). New recordings have it.";
        if (model is not { HasPowerData: true })
        {
            return (null, "No plan: no lap at this track has ICE and MGU-K power data, so there is no car model to plan with. " + reimport);
        }

        if (model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0)
        {
            return (null, "No plan yet: the car model needs a straight-line fit and a deploy map first. Record a few more laps at this track.");
        }

        if (fastest is null)
        {
            return (null, "No plan: this session has no timed lap to build one on.");
        }

        var profiles = new List<(LapRecord Lap, LapProfile Profile)>();
        foreach (var lap in representative)
        {
            var samples = lap.LapNumber == fastest.LapNumber ? fastestSamples : await runtime.Store.GetLapSamplesAsync(recording.Id, lap.LapNumber);
            if (await Task.Run(() => LapProfile.From(samples, recording.Format, lap.LapTimeMs / 1000.0)) is { } profile)
            {
                profiles.Add((lap, profile));
            }
        }

        var qualifying = LapProfile.From(fastestSamples, recording.Format, fastest.LapTimeMs / 1000.0);
        var race = await Task.Run(() => LapProfile.Average([.. profiles.Select(p => p.Profile)]));
        if (race is null || qualifying is null)
        {
            return (null, "No plan: this session has no ICE and MGU-K power data. " + reimport);
        }

        var average = TimeFormat.Lap(profiles.Average(p => (double)p.Lap.LapTimeMs));
        var raceBasis = profiles.Count == 1
            ? $"Built on the session's only typical lap, L{profiles[0].Lap.LapNumber} · {average}."
            : $"Built on the session's typical lap: {profiles.Count} laps averaged ({average} on average).";
        var qualifyingBasis = $"Built on the session's fastest lap, L{fastest.LapNumber} · {TimeFormat.Lap(fastest.LapTimeMs)}.";
        return (new PlanInputs(model, race, raceBasis, qualifying, qualifyingBasis), "");
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
        (_energies, _records, _energySamples, _sessionLaps, _typical, _line) = ([], [], [], [], null, null);
        Samples = null;
        Trace = null;
        HasData = false;
        EmptyText = reason;
    }

    private void FillRows()
    {
        Laps.Clear();
        foreach (var energy in _energies)
        {
            Laps.Add(new EnergyLapRow(energy, _records.FirstOrDefault(l => l.LapNumber == energy.LapNumber), Unit, _sessionLaps.Contains(energy.LapNumber)));
        }
    }

    /// <summary>The session's laps added up: battery at the line, energy per lap, deploy modes and waste.</summary>
    private void FillSummary()
    {
        var laps = _energies.Where(e => _sessionLaps.Contains(e.LapNumber)).ToList();
        if (laps.Count == 0)
        {
            return;
        }

        var unit = Unit;
        SessionTitle = laps.Count == 1 ? "SESSION · 1 LAP" : $"SESSION · {laps.Count} LAPS";
        SessionNote = laps.Count == _energies.Count
            ? "Every lap of the session, averaged."
            : $"{laps.Count} of {_energies.Count} laps, averaged: pit and safety car laps, and laps more than 3% off the median, are left out.";

        var start = laps.Average(e => e.StartStore);
        var end = laps.Average(e => e.EndStore);
        (StartLevel, StartBrush, EndLevel, EndBrush) = (EnergyText.Format(start, unit), EnergyText.Band(start), EnergyText.Format(end, unit), EnergyText.Band(end));
        Harvested = EnergyText.Format(laps.Average(e => e.Harvested), unit);
        Deployed = EnergyText.Format(laps.Average(e => e.Deployed), unit);
        DeployNote = $"Session total: {EnergyText.WithUnit(laps.Sum(e => e.Harvested), unit)} harvested, {EnergyText.WithUnit(laps.Sum(e => e.Deployed), unit)} deployed. "
            + $"The battery moved {EnergyText.Signed(end - start, unit)}{(unit == EnergyUnit.Percent ? "%" : " MJ")} per lap on average.";

        var limited = laps.Where(e => e.LimitReachedAt is not null).ToList();
        HarvestNote = laps.All(e => e.HarvestLimit <= 0) ? "No harvest limit reported (F1 25 format)."
            : limited.Count == 0 ? $"Limit {EnergyText.WithUnit(laps.Average(e => e.HarvestLimit), unit)} per lap, never reached."
            : $"Limit {EnergyText.WithUnit(laps.Average(e => e.HarvestLimit), unit)} per lap, reached on {limited.Count} of {laps.Count} laps, "
              + $"at {EnergyText.Km(limited.Average(e => e.LimitReachedAt!.Value))} on average.";

        Modes.Clear();
        foreach (var group in laps.SelectMany(e => e.Modes).GroupBy(m => m.Mode).OrderBy(g => g.Key))
        {
            var metres = group.Sum(m => m.To - m.From) / laps.Count;
            Modes.Add(new ModeShare(DeployModes.Letter(group.Key), DeployModes.Name(group.Key).ToUpperInvariant(), EnergyText.Km(metres),
                Palette.DeployMode(group.Key), Palette.OnDeployMode(group.Key)));
        }

        Issues.Clear();
        foreach (var kind in laps.SelectMany(e => e.Issues).GroupBy(i => i.Kind).OrderBy(g => g.Key))
        {
            var on = laps.Count(e => e.Issues.Any(i => i.Kind == kind.Key));
            Issues.Add(EnergyText.Explain(kind.Key, kind.Sum(i => i.Seconds), on));
        }

        OnPropertyChanged(nameof(HasIssues));
    }

    /// <summary>The map and trace: the session as driven, or the plan when it is chosen and there is one.</summary>
    private void ShowGraphics()
    {
        if (!HasData || _line is not { } line || _typical is not { } typical)
        {
            return;
        }

        var plan = Plan.Result?.Plan;
        var onPlan = ShowPlanOnMap && plan is not null;
        Samples = onPlan
            ? Recolour(line, d => Math.Clamp((int)(d / LapProfile.Step), 0, plan!.Modes.Length - 1), i => plan!.Modes[i], i => plan!.Stores[i])
            : Recolour(line, typical.IndexAt, i => typical.Mode[i], i => typical.Level[i]);
        MapTitle = onPlan
            ? $"{(plan!.Kind == PlanKind.Race ? "RACE" : "QUALIFYING")} PLAN · {Plan.Predicted}"
            : $"YOUR SESSION · {(typical.LapCount == 1 ? "1 LAP" : $"{typical.LapCount} LAPS AVERAGED")}";
        Trace = BuildTrace(plan);
    }

    /// <summary>The racing line with a deploy mode and battery level at every point (by the index of its distance).</summary>
    private static List<TelemetrySample> Recolour(IReadOnlyList<TelemetrySample> line, Func<double, int> index, Func<int, int> mode, Func<int, double> level) =>
    [
        .. line.Where(s => s.LapDistance >= 0).Select(s =>
        {
            var i = index(s.LapDistance);
            return new TelemetrySample
            {
                LapNumber = s.LapNumber, SessionTime = s.SessionTime, LapDistance = s.LapDistance, WorldPosX = s.WorldPosX, WorldPosZ = s.WorldPosZ,
                ErsDeployMode = mode(i), ErsStoreEnergy = level(i),
            };
        }),
    ];

    /// <summary>
    /// Battery along the lap: every lap of the session faintly, their average, and the plan dashed when there is one; the
    /// session's deploy modes (and the plan's) under the trace.
    /// </summary>
    private ChartModel BuildTrace(LapPlan? plan)
    {
        var scale = EnergyText.Scale(Unit);
        var unit = EnergyText.Unit(Unit);
        var typical = _typical!;
        var series = new List<ChartSeries>();
        var first = true;
        foreach (var lap in _energySamples.Where(s => s.LapDistance >= 0 && _sessionLaps.Contains(s.LapNumber)).GroupBy(s => s.LapNumber))
        {
            series.Add(new ChartSeries(first ? "Each lap" : "", "#4A5363", [.. lap.Select(s => s.LapDistance)], [.. lap.Select(s => s.ErsStoreEnergy * scale)],
                LineWidth: 1, InLegend: first));
            first = false;
        }

        series.Add(new ChartSeries("Session average", "#F4F6F9", [.. Enumerable.Range(0, typical.Level.Length).Select(i => i * typical.Step)],
            [.. typical.Level.Select(j => j * scale)]));

        var limits = _energies.Where(e => _sessionLaps.Contains(e.LapNumber) && e.LimitReachedAt is not null).Select(e => e.LimitReachedAt!.Value).ToList();
        IReadOnlyList<ChartMarker> markers = limits.Count > 0 ? [new ChartMarker(limits.Average(), "#4DB5FF", "Harvest limit reached (average)")] : [];
        var driven = Row(typical.Runs());
        if (plan is null)
        {
            return new ChartModel($"Battery ({unit}) over the lap · every lap of the session and their average · deploy modes under the trace", series,
                Markers: markers, Timeline: driven.Count > 0 ? [new ChartTimelineRow("YOU", "#C3CAD5", driven, [])] : null);
        }

        series.Add(new ChartSeries($"{(plan.Kind == PlanKind.Race ? "Race" : "Qualifying")} plan", "#4DB5FF",
            [.. Enumerable.Range(0, plan.Stores.Length).Select(i => (i + 1) * LapProfile.Step)], [.. plan.Stores.Select(j => j * scale)], Line: ChartLine.Dashed));
        return new ChartModel($"Battery ({unit}) over the lap · the session and the plan · your deploy modes and the plan's under the trace", series,
            Markers: markers, Timeline: [new ChartTimelineRow("YOU", "#C3CAD5", driven, []), new ChartTimelineRow("PLAN", "#4DB5FF", Row(PlanRuns(plan)), [])]);

        static List<ChartTimelineSegment> Row(IEnumerable<ModeRun> runs) =>
        [
            .. runs.Select(m => new ChartTimelineSegment(m.From, m.To, DeployModes.Letter(m.Mode), "", "",
                m.Mode == DeployModes.None ? "#8D97A6" : Palette.DeployModeHex[Math.Clamp(m.Mode, 0, 3)],
                Palette.DeployModeHex[Math.Clamp(m.Mode, 0, 3)])),
        ];
    }

    /// <summary>The plan's segments merged into runs of one mode.</summary>
    private static IEnumerable<ModeRun> PlanRuns(LapPlan plan)
    {
        var start = 0;
        for (var i = 1; i <= plan.Modes.Length; i++)
        {
            if (i < plan.Modes.Length && plan.Modes[i] == plan.Modes[start])
            {
                continue;
            }

            yield return new ModeRun(plan.Modes[start], start * LapProfile.Step, i * LapProfile.Step);
            start = i;
        }
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
        else if (Unit == EnergyUnit.Percent)
        {
            Legend.Add(new LegendEntry("50% AND UP", Palette.Faster));
            Legend.Add(new LegendEntry("20–50%", Palette.Slower));
            Legend.Add(new LegendEntry("BELOW 20%", Palette.Danger));
        }
        else
        {
            Legend.Add(new LegendEntry("2.0 MJ AND UP", Palette.Faster));
            Legend.Add(new LegendEntry("0.8–2.0 MJ", Palette.Slower));
            Legend.Add(new LegendEntry("BELOW 0.8 MJ", Palette.Danger));
        }
    }
}
