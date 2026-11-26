using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

/// <summary>What one kind of lap burns at the track, as a card.</summary>
public sealed record FuelBurnCardViewModel(string Title, string Subtitle, FuelBurn? Burn, string Tip, string UsedFor)
{
    public string Value => Burn is null ? "—" : Inv($"{Burn.KgPerLap:0.00}");

    public string Detail => Burn is null
        ? "Not driven yet"
        : $"{Count(Burn.Laps, "lap")} · {Count(Burn.Runs, "run")} · {Count(Burn.Sessions, "session")}";

    public string Range => Burn is null ? "" : Inv($"{Burn.Low:0.00}–{Burn.High:0.00} kg");

    public bool FromOtherLengths => Burn?.FromOtherLengths == true;

    public bool HasBurn => Burn is not null;

    public bool IsUsed => UsedFor.Length > 0;

    public IBrush ValueBrush => Burn is null ? Palette.TextLo : Palette.TextHi;

    internal static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    internal static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A run at the track and what it was read as.</summary>
public sealed record FuelRunRowViewModel(FuelRun Run)
{
    public string Session => $"{SessionTypes.Name(Run.Session.Recording.SessionType)} · {Run.Session.Recording.StartTime.LocalDateTime.ToString("d MMM · HH:mm", CultureInfo.InvariantCulture)}";

    public string LapRange => Run.StartLap == Run.EndLap ? $"L{Run.StartLap}" : $"L{Run.StartLap}–{Run.EndLap}";

    public string Compound => Run.Compound;

    public Chip Kind => Run.Kind switch
    {
        FuelRunKind.Race => Chip.Filled("RACE", Palette.Info),
        FuelRunKind.RaceSim => Chip.Outlined("RACE SIM", Palette.Info),
        FuelRunKind.Qualifying => Chip.Filled("QUALIFYING", Palette.SessionBest),
        FuelRunKind.QualiSim => Chip.Outlined("QUALI SIM", Palette.SessionBest),
        _ => Chip.Outlined("OTHER", Palette.TextLo),
    };

    /// <summary>The run's laps by kind: "OUT · 3 PUSH · 1 COOL-DOWN · IN".</summary>
    public string Laps
    {
        get
        {
            int Of(FuelLapKind kind) => Run.Laps.Count(l => l.Kind == kind);
            var parts = new List<string>();
            if (Of(FuelLapKind.OutLap) > 0)
            {
                parts.Add("OUT");
            }

            parts.Add($"{Of(FuelLapKind.Push)} {(Run.Kind is FuelRunKind.Race or FuelRunKind.RaceSim ? "RACE PACE" : "PUSH")}");
            if (Of(FuelLapKind.CoolDown) is > 0 and var cool)
            {
                parts.Add($"{cool} COOL-DOWN");
            }

            if (Of(FuelLapKind.SafetyCar) is > 0 and var sc)
            {
                parts.Add($"{sc} SC");
            }

            if (Of(FuelLapKind.InLap) > 0)
            {
                parts.Add("IN");
            }

            return string.Join(" · ", parts);
        }
    }

    public string StartFuel => Run.StartFuelKg > 0 ? FuelBurnCardViewModel.Inv($"{Run.StartFuelKg:0.0}") : "—";

    public string Burn => Run.PushKgPerLap > 0 ? FuelBurnCardViewModel.Inv($"{Run.PushKgPerLap:0.00}") : "—";

    public string Why => Run.Why;

    public IBrush Foreground => Run.Kind == FuelRunKind.Other ? Palette.TextLo : Palette.TextHi;
}

/// <summary>One line of a fuel calculation: what, how much, and where it comes from.</summary>
public sealed record FuelLineViewModel(string Label, string Detail, string Kg, string Note, bool Total = false, bool Assumed = false)
{
    public IBrush KgBrush => Total ? Palette.TextHi : Palette.TextMid;

    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// The hub's fuel page: what each kind of lap burns at the track, read from every run of its sessions as a race, a race
/// simulation, a qualifying run or a qualifying simulation, and the race and qualifying fuel worked out from them.
/// </summary>
public sealed partial class FuelViewModel(TelemetryRuntime runtime) : ObservableObject
{
    private IReadOnlyList<RaceInput> _sessions = [];
    private (GameFormat Format, int TrackId)? _track;
    private FuelModel? _model;
    private double _fuelEffect = new Core.Engine.StrategyOptions().FuelEffectSecondsPerKg;
    private string _fuelEffectBasis = "";
    private bool _loading;
    private int _version;

    public ObservableCollection<RaceLengthOption> RaceLengthOptions { get; } = [];

    [ObservableProperty] public partial RaceLengthOption? SelectedRaceLength { get; set; }

    public bool HasRaceLengths => RaceLengthOptions.Count > 1;

    [ObservableProperty] public partial int RaceLaps { get; set; } = 20;

    /// <summary>Laps of fuel carried on top of what the race needs.</summary>
    [ObservableProperty] public partial double MarginLaps { get; set; } = 1;

    [ObservableProperty] public partial bool FormationLap { get; set; } = true;

    /// <summary>Push laps in a qualifying run.</summary>
    [ObservableProperty] public partial int PushLaps { get; set; } = 1;

    [ObservableProperty] public partial bool QualiInLap { get; set; } = true;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasModel { get; set; }
    [ObservableProperty] public partial string Empty { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Insight { get; set; } = "";

    [ObservableProperty] public partial bool HasRacePlan { get; set; }
    [ObservableProperty] public partial string RaceStart { get; set; } = "—";
    [ObservableProperty] public partial string RaceBasis { get; set; } = "";
    [ObservableProperty] public partial string RaceEmpty { get; set; } = "";

    [ObservableProperty] public partial bool HasQualiPlan { get; set; }
    [ObservableProperty] public partial string QualiTotal { get; set; } = "—";
    [ObservableProperty] public partial string QualiBasis { get; set; } = "";

    public ObservableCollection<FuelBurnCardViewModel> Cards { get; } = [];
    public ObservableCollection<FuelLineViewModel> RaceLines { get; } = [];
    public ObservableCollection<FuelLineViewModel> Lighter { get; } = [];
    public ObservableCollection<FuelLineViewModel> QualiLines { get; } = [];
    public ObservableCollection<FuelRunRowViewModel> Runs { get; } = [];

    public bool HasLighter => Lighter.Count > 0;
    public bool HasInsight => Insight.Length > 0;

    private static string Inv(FormattableString text) => FuelBurnCardViewModel.Inv(text);

    private static string Kg(double kg) => Inv($"{kg:0.0} kg");

    /// <summary>Reads every session at the track (saved and unsaved) and works the fuel out.</summary>
    public async Task LoadTrackAsync(GameFormat format, int trackId)
    {
        var version = ++_version;
        IsBusy = true;
        try
        {
            var sessions = await runtime.Analysis.LoadTrackAsync(format, trackId);
            if (version != _version)
            {
                return;
            }

            var newTrack = _track != (format, trackId);
            _track = (format, trackId);
            _sessions = sessions;

            // Each race length any session counts for; the one most sessions do picked first on a new track.
            var lengths = sessions.Select(s => s.RaceLength).Where(RaceLengths.IsKnown).ToList();
            var keep = newTrack ? null : SelectedRaceLength?.Length;
            _loading = true;
            RaceLengthOptions.Clear();
            RaceLengthOptions.Add(new RaceLengthOption(0, "ANY LENGTH"));
            foreach (var length in lengths.Distinct().Order())
            {
                RaceLengthOptions.Add(new RaceLengthOption(length, RaceLengths.Name(length).ToUpperInvariant()));
            }

            var common = lengths.GroupBy(l => l).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).Select(g => g.Key).FirstOrDefault();
            SelectedRaceLength = RaceLengthOptions.FirstOrDefault(o => o.Length == (keep ?? common)) ?? RaceLengthOptions[0];
            _loading = false;
            OnPropertyChanged(nameof(HasRaceLengths));
            await RebuildAsync(newTrack);
        }
        finally
        {
            _loading = false;
            if (version == _version)
            {
                IsBusy = false;
            }
        }
    }

    partial void OnSelectedRaceLengthChanged(RaceLengthOption? value)
    {
        if (!_loading && value is not null)
        {
            _ = RebuildAsync(true);
        }
    }

    partial void OnRaceLapsChanged(int value) => Recalculate();

    partial void OnMarginLapsChanged(double value) => Recalculate();

    partial void OnFormationLapChanged(bool value) => Recalculate();

    partial void OnPushLapsChanged(int value) => Recalculate();

    partial void OnQualiInLapChanged(bool value) => Recalculate();

    /// <summary>The fuel model for the length picked; <paramref name="suggestLaps"/> takes the race distance from its races.</summary>
    private async Task RebuildAsync(bool suggestLaps)
    {
        var sessions = _sessions;
        var length = SelectedRaceLength?.Length ?? 0;
        var (model, library) = await Task.Run(() =>
            (runtime.Analysis.BuildFuelModel(sessions, length), runtime.Analysis.BuildTrackKnowledge(sessions, null, length).Library));
        if (!ReferenceEquals(sessions, _sessions))
        {
            return;
        }

        _model = model;
        _fuelEffect = library.FuelEffect.SecondsPerKg;
        _fuelEffectBasis = library.FuelEffect.Basis;
        if (suggestLaps && library.RaceLaps is { } laps)
        {
            _loading = true;
            RaceLaps = Math.Max(3, laps);
            _loading = false;
        }

        HasModel = model.HasData;
        Empty = model.HasData ? "" : "No timed laps at this track yet. Record practice, qualifying or a race here and the fuel fills in.";
        FillCards(model);
        FillRuns(model);
        Recalculate();
    }

    private void FillCards(FuelModel model)
    {
        var raceSource = model.Race is not null ? "RACE FUEL" : "";
        var simSource = model.Race is null && model.RaceSim is not null ? "RACE FUEL" : "";
        var pushSource = model.Push is not null ? "QUALIFYING FUEL" : "";
        Cards.Clear();
        Cards.Add(new("RACE PACE", "RACES", model.Race,
            "Green-flag laps of the races: what the race fuel is planned with. Safety car laps and pit laps are left out.", raceSource));
        Cards.Add(new("RACE PACE", "PRACTICE RACE SIMS", model.RaceSim,
            "Laps at race pace of the practice runs read as race simulations (a long run, or a few laps on a heavy tank). They plan the race fuel until a race is recorded here.", simSource));
        Cards.Add(new("PUSH LAP", "QUALIFYING", model.Qualifying,
            "Push laps driven in qualifying. Pooled with the practice qualifying simulations for the qualifying fuel.", pushSource));
        Cards.Add(new("PUSH LAP", "PRACTICE QUALI SIMS", model.QualiSim,
            "Push laps of the practice runs read as qualifying simulations (a short run on a light tank, or as fast as the session's best). Pooled with qualifying for the qualifying fuel.", pushSource));
        Cards.Add(new("OUT-LAP", "QUALIFYING & PRACTICE", model.OutLap,
            "The lap out of the pits or the garage, from qualifying runs when there are some. The qualifying fuel's out-lap, and the race's formation lap.", ""));
        Cards.Add(new("IN-LAP", "QUALIFYING & PRACTICE", model.InLap,
            "The lap back into the pits after a run. Out- and in-laps with fuel changed in the garage on them are left out.", ""));
        Cards.Add(new("COOL-DOWN LAP", "QUALIFYING & PRACTICE", model.CoolDown,
            Inv($"A timed lap more than {(FuelModel.CoolDownRatio - 1) * 100:0} % slower than its run's best: a lap to cool the tyres or charge the battery between pushes."), ""));
        Cards.Add(new("SAFETY CAR LAP", "SC & VSC", model.SafetyCar,
            "A lap behind a safety car or virtual safety car: what one saves against a racing lap.", ""));
    }

    private void FillRuns(FuelModel model)
    {
        Runs.Clear();
        foreach (var run in model.Runs.OrderByDescending(r => r.Session.Recording.StartTime).ThenBy(r => r.StartLap))
        {
            Runs.Add(new FuelRunRowViewModel(run));
        }

        int Of(FuelRunKind kind) => model.Runs.Count(r => r.Kind == kind);
        Summary = model.HasData
            ? $"{FuelBurnCardViewModel.Count(model.Runs.Count, "run")} at this track: {Of(FuelRunKind.Race)} race, {Of(FuelRunKind.RaceSim)} race sim, "
              + $"{Of(FuelRunKind.Qualifying)} qualifying, {Of(FuelRunKind.QualiSim)} quali sim, {Of(FuelRunKind.Other)} other."
            : "";

        // How much more a push lap burns than a racing lap: the reason the two are kept apart.
        Insight = model is { Push: { } push, RaceBurn: { } race } && race.KgPerLap > 0
            ? Inv($"A push lap here burns {(push.KgPerLap / race.KgPerLap - 1) * 100:+0;-0} % against a lap at race pace ({push.KgPerLap:0.00} against {race.KgPerLap:0.00} kg).")
            : "";
        OnPropertyChanged(nameof(HasInsight));
    }

    private void Recalculate()
    {
        if (_loading || _model is not { } model)
        {
            return;
        }

        RecalculateRace(model);
        RecalculateQualifying(model);
    }

    private void RecalculateRace(FuelModel model)
    {
        RaceLines.Clear();
        Lighter.Clear();
        var plan = model.PlanRace(RaceLaps, MarginLaps, FormationLap, _fuelEffect);
        HasRacePlan = plan is not null;
        if (plan is null)
        {
            RaceStart = "—";
            RaceBasis = "";
            RaceEmpty = "No race or race simulation recorded here yet. Drive a long run in practice (5 laps or more at race pace) and the race fuel fills in.";
            OnPropertyChanged(nameof(HasLighter));
            return;
        }

        RaceEmpty = "";
        RaceStart = Kg(plan.StartKg);
        var source = plan.FromRaceSims ? "practice race simulations" : "races";
        RaceBasis = Inv($"{plan.BurnKgPerLap:0.00} kg a lap at race pace, from the {source}")
                    + (plan.FromOtherLengths ? " at other race lengths: none was driven at the length picked." : ".");
        RaceLines.Add(new("RACE LAPS", Inv($"{plan.Laps} × {plan.BurnKgPerLap:0.00} kg"), Kg(plan.Laps * plan.BurnKgPerLap), ""));
        if (FormationLap)
        {
            RaceLines.Add(new("FORMATION LAP", plan.FormationMeasured ? "an out-lap's burn" : Inv($"{FuelModel.SlowLapShare * 100:0} % of a racing lap"),
                Kg(plan.FormationKg), "", Assumed: !plan.FormationMeasured));
        }

        RaceLines.Add(new("FUEL NEEDED", "to see the flag", Kg(plan.NeededKg), "", Total: true));
        if (plan.MarginKg > 0)
        {
            RaceLines.Add(new("MARGIN", Inv($"{plan.MarginLaps:0.#} × {plan.BurnKgPerLap:0.00} kg"), Kg(plan.MarginKg),
                Inv($"Carried the whole race: about {plan.MarginCostSeconds:0.0} s at {_fuelEffect:0.000} s/kg ({_fuelEffectBasis}).")));
        }

        foreach (var option in plan.Lighter)
        {
            Lighter.Add(new(Inv($"−{option.LapsLess:0} {(option.LapsLess == 1 ? "LAP" : "LAPS")}"),
                Inv($"save {option.SaveKgPerLap:0.00} kg a lap ({option.SavePercent:0.0} %)"),
                Kg(option.StartKg),
                Inv($"The lighter car gains about {option.GainSeconds:0.0} s, before what lifting and coasting costs.")));
        }

        OnPropertyChanged(nameof(HasLighter));
    }

    private void RecalculateQualifying(FuelModel model)
    {
        QualiLines.Clear();
        var plan = model.PlanQualifying(PushLaps, QualiInLap);
        HasQualiPlan = plan is not null;
        if (plan is null)
        {
            QualiTotal = "—";
            QualiBasis = "No push lap recorded here yet. Drive a qualifying simulation in practice (a short run on a light tank) or qualifying.";
            return;
        }

        QualiTotal = Kg(plan.TotalKg);
        QualiBasis = plan.PushFromRace
            ? "No push lap recorded here yet: push laps take the race-pace burn, which is likely too low. Drive a qualifying simulation to measure it."
            : "Out-lap, push laps with a cool-down lap between each, and the in-lap. ASSUMED parts were never driven here and take "
              + Inv($"{FuelModel.SlowLapShare * 100:0} % of a push lap.");
        foreach (var part in plan.Parts)
        {
            QualiLines.Add(new(part.Name.ToUpperInvariant(), Inv($"{part.Laps} × {part.KgPerLap:0.00} kg"), Kg(part.Kg), "", Assumed: !part.Measured));
        }

        QualiLines.Add(new("FUEL FOR THE RUN", "", Kg(plan.TotalKg), "", Total: true));
    }
}
