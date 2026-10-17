using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

/// <summary>One compound of the track's library.</summary>
public sealed record CompoundRowViewModel(CompoundModel Model, double FastestBase)
{
    public string Compound => Model.Compound;
    public string Name => Model.Compound.ToUpperInvariant();
    public string Data => $"{Model.Laps} clean laps · {Model.Stints} {(Model.Stints == 1 ? "stint" : "stints")} · {Model.Sessions} {(Model.Sessions == 1 ? "session" : "sessions")}";
    public string Pace => Model.BaseLapSeconds - FastestBase is var gap && gap < 0.0005 ? "FASTEST" : $"+{gap:0.000}";
    public IBrush PaceBrush => Pace == "FASTEST" ? Palette.SessionBest : Palette.TextHi;
    public string Degradation => $"{Model.DegradationPerLap:+0.000}";
    public string Wear => Model.WearPerLap > 0 ? $"{Model.WearPerLap:0.0}" : "—";
    public string Life => Model.LifeLaps is { } life ? $"{Math.Floor(life):0}" : "—";

    /// <summary>Tyre age where the cliff starts, and what each lap past it adds.</summary>
    public string Cliff => Model.CliffAge is { } age ? string.Create(CultureInfo.InvariantCulture, $"L{Math.Floor(age):0} +{Model.CliffSecondsPerLap:0.00}") : "—";

    public IBrush CliffBrush => Model.CliffMeasured ? Palette.TextHi : Palette.TextMid;

    public string CliffTip => Model.CliffMeasured
        ? "Measured from the laps run past the cliff wear."
        : string.Create(CultureInfo.InvariantCulture, $"Assumed: no clean lap was run past {new StrategyOptions().TyreCliffWearPercent:0} % wear, so each lap past it adds the default {Model.CliffSecondsPerLap:0.00} s.");

    public string Note => string.Join(" ", new[]
    {
        Model.DegradationMeasured ? "" : "Degradation assumed: no stint had two clean laps to measure it.",
        Model.Linked ? "" : "Pace as recorded: never run in a session linked to this one, so the conditions aren't corrected.",
        Model.IsDry ? "" : "Wet compound: left out of the dry plans.",
    }.Where(n => n.Length > 0));

    public bool HasNote => Note.Length > 0;
}

/// <summary>A stint on a plan's bar, as wide as its share of the race.</summary>
public sealed record PlanSegmentViewModel(string Compound, double Width, string Laps, string Tip)
{
    public IBrush Brush => Palette.Compound(Compound);
}

public sealed record PlanStopViewModel(PlannedStop Stop)
{
    public string Pit => $"PIT L{Stop.InLap}";
    public string Window => Stop.WindowFirst == Stop.WindowLast ? "no window" : $"window L{Stop.WindowFirst}–{Stop.WindowLast}";
    public string Undercut => Stop.UndercutSeconds >= 0 ? $"undercut +{Stop.UndercutSeconds:0.00} s" : $"overcut +{-Stop.UndercutSeconds:0.00} s";
    public IBrush UndercutBrush => Stop.UndercutSeconds >= 0 ? Palette.Faster : Palette.Slower;

    /// <summary>Laps on which a safety car (SC) or virtual safety car (VSC) makes pitting under it pay.</summary>
    public string SafetyCar => string.Join(" · ", new[] { LapRange("SC", Stop.SafetyCar), LapRange("VSC", Stop.VirtualSafetyCar) }.Where(w => w.Length > 0)) is { Length: > 0 } text
        ? text
        : "no SC window";

    public string SafetyCarTip =>
        "If a safety car (SC) or virtual safety car (VSC) comes out on one of these laps, pit under it: the stop costs less, "
        + "and with the later stints re-planned for the laps left that beats staying out to the planned in-lap. "
        + (Stop.SafetyCar is { } sc ? string.Create(CultureInfo.InvariantCulture, $"Under an SC it gains up to {sc.GainSeconds:0.0} s.") : "");

    private static string LapRange(string name, SafetyCarWindow? window) => window switch
    {
        null => "",
        { FirstLap: var first, LastLap: var last } when first == last => $"{name} L{first}",
        { FirstLap: var first, LastLap: var last } => $"{name} L{first}–{last}",
    };
}

public sealed record PlanRowViewModel(int Rank, RacePlan Plan, IReadOnlyList<PlanSegmentViewModel> Segments, IBrush RankBrush)
{
    public string RankText => $"#{Rank}";
    public string StopsText => Plan.StopCount == 1 ? "1 STOP" : $"{Plan.StopCount} STOPS";
    public string Mix => string.Join(" → ", Plan.Stints.Select(s => $"{Palette.CompoundLetter(s.Compound)}{s.Laps}{(s.StartWear > 0 ? $" ({s.StartWear:0} %)" : "")}"));
    public string Time => RaceTime(Plan.RaceSeconds);
    public string Gap => Plan.GapSeconds < 0.05 ? "BEST" : $"+{Plan.GapSeconds:0.0} s";
    public IBrush GapBrush => Plan.GapSeconds < 0.05 ? Palette.SessionBest : Palette.Slower;
    public IReadOnlyList<PlanStopViewModel> Stops { get; } = Plan.Stops.Select(s => new PlanStopViewModel(s)).ToList();

    private static string RaceTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}");
    }
}

/// <summary>A choice of the set the race starts on.</summary>
/// <param name="Set">The set; null lets the plan pick a new one.</param>
/// <param name="ManualWear">The wear is typed in (<see cref="RacePlansViewModel.StartWear"/>), not read from the game.</param>
public sealed record StartChoice(string Label, PlannerSet? Set, bool ManualWear = false)
{
    public override string ToString() => Label;
}

/// <summary>
/// The strategy optimiser on the strategy tab: the compound library of the session's track (every recording there in
/// the same game format) and the 1-, 2- and 3-stop plans it gives for a race distance.
/// </summary>
public sealed partial class RacePlansViewModel(TelemetryRuntime runtime, SettingsService settings) : ObservableObject
{
    /// <summary>A live session's library is rebuilt at most this often (each build reads every recording at the track).</summary>
    private static readonly TimeSpan LiveRebuild = TimeSpan.FromMinutes(1);

    private const double BarWidth = 420;
    private static readonly IBrush[] PlanBrushes = [Palette.RaceA, Palette.RaceB, Palette.SessionBest];
    private static readonly string[] PlanHex = [Palette.RaceAHex, Palette.RaceBHex, "#C77DFF"];

    private CompoundLibrary? _library;
    private TyreSetsChange? _gameSets;
    private bool _gameSetsFromRace;
    private (long Id, bool Live)? _loadedFor;
    private DateTime _loadedAt;
    private int _version;

    [ObservableProperty] public partial string Context { get; set; } = "RACE PLANS";
    [ObservableProperty] public partial int RaceLaps { get; set; } = 20;
    [ObservableProperty] public partial string Basis { get; set; } = "";
    [ObservableProperty] public partial string Problem { get; set; } = "";
    [ObservableProperty] public partial bool HasLibrary { get; set; }
    [ObservableProperty] public partial bool HasPlans { get; set; }
    [ObservableProperty] public partial ChartModel? PlanChart { get; set; }

    public ObservableCollection<CompoundRowViewModel> Compounds { get; } = [];
    public ObservableCollection<PlanRowViewModel> Plans { get; } = [];

    /// <summary>What the race can start on: the plan's pick, the sets the game listed, or a used set of each compound.</summary>
    public ObservableCollection<StartChoice> StartChoices { get; } = [];

    [ObservableProperty] public partial StartChoice? SelectedStart { get; set; }

    /// <summary>Wear of a used start set typed in, %.</summary>
    [ObservableProperty] public partial int StartWear { get; set; } = 20;

    public bool StartWearVisible => SelectedStart?.ManualWear == true;

    partial void OnSelectedStartChanged(StartChoice? value)
    {
        OnPropertyChanged(nameof(StartWearVisible));
        Replan();
    }

    partial void OnStartWearChanged(int value) => Replan();

    /// <summary>The recording has the game's tyre sets (packet 12), so the plans can use them instead of the counts.</summary>
    [ObservableProperty] public partial bool HasGameSets { get; set; }

    /// <summary>Plan on the sets the game listed rather than the counts typed in; saved in the settings.</summary>
    [ObservableProperty] public partial bool SetsFromGame { get; set; } = settings.Current.RaceSetsFromGame;

    /// <summary>The sets the game listed, as the plans use them.</summary>
    [ObservableProperty] public partial string GameSets { get; set; } = "";

    public bool UsesGameSets => HasGameSets && SetsFromGame;

    public bool UsesCounts => !UsesGameSets;

    partial void OnHasGameSetsChanged(bool value) => OnSetsSourceChanged();

    partial void OnSetsFromGameChanged(bool value)
    {
        settings.Current.RaceSetsFromGame = value;
        settings.Save();
        OnSetsSourceChanged();
    }

    private void OnSetsSourceChanged()
    {
        OnPropertyChanged(nameof(UsesGameSets));
        OnPropertyChanged(nameof(UsesCounts));
        Replan();
    }

    partial void OnRaceLapsChanged(int value) => Replan();

    /// <summary>New sets of each dry compound the plans can use (a plan runs each set once); saved in the settings.</summary>
    [ObservableProperty] public partial int SoftSets { get; set; } = settings.Current.RaceSoftSets;

    [ObservableProperty] public partial int MediumSets { get; set; } = settings.Current.RaceMediumSets;
    [ObservableProperty] public partial int HardSets { get; set; } = settings.Current.RaceHardSets;

    partial void OnSoftSetsChanged(int value) => SaveSets();

    partial void OnMediumSetsChanged(int value) => SaveSets();

    partial void OnHardSetsChanged(int value) => SaveSets();

    private void SaveSets()
    {
        settings.Current.RaceSoftSets = SoftSets;
        settings.Current.RaceMediumSets = MediumSets;
        settings.Current.RaceHardSets = HardSets;
        settings.Save();
        Replan();
    }

    public void Clear()
    {
        _version++;
        _library = null;
        _gameSets = null;
        _loadedFor = null;
        Compounds.Clear();
        Plans.Clear();
        HasLibrary = HasPlans = HasGameSets = false;
        GameSets = "";
        StartChoices.Clear();
        PlanChart = null;
    }

    /// <summary>
    /// Builds the library for <paramref name="recording"/>'s track, unless it is already loaded (a live recording's is
    /// rebuilt once <see cref="LiveRebuild"/> has passed, so new laps count without reading the track on every refresh).
    /// </summary>
    /// <param name="sessionLaps">Laps of <paramref name="recording"/>: the race distance offered when no race was recorded at the track.</param>
    public async Task LoadAsync(RecordingInfo recording, int sessionLaps)
    {
        var live = recording.EndTime is null;
        if (_loadedFor == (recording.Id, live) && (!live || DateTime.UtcNow - _loadedAt < LiveRebuild))
        {
            return;
        }

        var firstLoad = _loadedFor?.Id != recording.Id;
        var version = ++_version;
        _loadedFor = (recording.Id, live);
        _loadedAt = DateTime.UtcNow;
        try
        {
            var library = await runtime.Analysis.BuildCompoundLibraryAsync(recording);
            var snapshots = await runtime.Store.GetTyreSetsAsync(recording.Id);
            if (version != _version)
            {
                return;
            }

            _library = library;
            _gameSets = RaceTyreSets.ForPlanning(snapshots, recording.SessionType);
            _gameSetsFromRace = SessionTypes.IsRace(recording.SessionType);
            LoadGameSets(firstLoad);
            Context = ($"RACE PLANS · {recording.TrackName} · {(recording.Format == GameFormat.F1_26 ? "F1 26" : "F1 25")} · "
                       + $"FROM {library.Sessions} {(library.Sessions == 1 ? "SESSION" : "SESSIONS")} AT THIS TRACK").ToUpperInvariant();
            var fastest = library.Compounds.Where(c => c.IsDry).Select(c => c.BaseLapSeconds).DefaultIfEmpty(0).Min();
            Compounds.Clear();
            foreach (var compound in library.Compounds)
            {
                Compounds.Add(new CompoundRowViewModel(compound, fastest));
            }

            HasLibrary = Compounds.Count > 0;
            Basis = string.Create(CultureInfo.InvariantCulture,
                $"Pit stop {library.PitLossSeconds:0.0} s ({(library.PitLossStops > 0 ? $"median of {library.PitLossStops} recorded {(library.PitLossStops == 1 ? "stop" : "stops")}" : "default: no race with a stop recorded here")}) · "
                + $"fuel {library.FuelPerLap:0.00} kg/lap at {library.FuelEffect.SecondsPerKg:0.000} s/kg ({library.FuelEffect.Basis}) · paces in this session's conditions");

            if (firstLoad)
            {
                // Suggest the longest race recorded here, else this session's length.
                RaceLaps = Math.Max(6, library.RaceLaps ?? sessionLaps);
            }

            Replan();
        }
        catch (Exception ex)
        {
            Problem = $"Could not build the compound library: {ex.Message}";
            HasPlans = false;
        }
    }

    /// <summary>
    /// The game's sets for the plans and the start choices. On a recording's first load a race starts on the set fitted
    /// as it started; otherwise the choice made stays when it is still offered.
    /// </summary>
    private void LoadGameSets(bool firstLoad)
    {
        var sets = _gameSets is { } snapshot ? RaceTyreSets.Available(snapshot) : [];
        var fitted = _gameSets is { } fittedIn ? RaceTyreSets.Fitted(fittedIn) : null;
        GameSets = sets.Count == 0 ? "" : Describe(sets);

        var choices = new List<StartChoice> { new("PLAN PICKS A NEW SET", null) };
        if (fitted is not null)
        {
            choices.Add(new StartChoice($"FITTED: {Name(fitted)}", fitted));
        }

        choices.AddRange(sets.Where(s => !s.IsNew).Distinct().OrderBy(s => CompoundLibraryBuilder.CompoundOrder(s.Compound)).ThenBy(s => s.WearPercent)
            .Where(s => s != fitted).Select(s => new StartChoice($"USED {Name(s)}", s)));
        choices.AddRange(new[] { "Soft", "Medium", "Hard" }.Select(c => new StartChoice($"USED {c.ToUpperInvariant()} · WEAR…", new PlannerSet(c, 1), ManualWear: true)));

        var keep = firstLoad ? null : SelectedStart?.Label;
        StartChoices.Clear();
        foreach (var choice in choices)
        {
            StartChoices.Add(choice);
        }

        HasGameSets = sets.Count > 0;
        // The fitted set's label carries its wear, which moves while a session is live.
        SelectedStart = choices.FirstOrDefault(c => c.Label == keep)
                        ?? (fitted is not null && (keep?.StartsWith("FITTED", StringComparison.Ordinal) == true || (firstLoad && _gameSetsFromRace)) ? choices[1] : choices[0]);
    }

    private static string Name(PlannerSet set) =>
        set.IsNew ? $"NEW {set.Compound.ToUpperInvariant()}" : string.Create(CultureInfo.InvariantCulture, $"{set.Compound.ToUpperInvariant()} · {set.WearPercent:0} %");

    /// <summary>"New 6 S · 2 M · 2 H · used S 18 %, M 30 %".</summary>
    private static string Describe(IReadOnlyList<PlannerSet> sets)
    {
        var fresh = sets.Where(s => s.IsNew).GroupBy(s => s.Compound).OrderBy(g => CompoundLibraryBuilder.CompoundOrder(g.Key))
            .Select(g => $"{g.Count()} {Palette.CompoundLetter(g.Key)}");
        var used = sets.Where(s => !s.IsNew).OrderBy(s => CompoundLibraryBuilder.CompoundOrder(s.Compound)).ThenBy(s => s.WearPercent)
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{Palette.CompoundLetter(s.Compound)} {s.WearPercent:0} %")).ToList();
        return $"NEW {string.Join(" · ", fresh.DefaultIfEmpty("NONE"))}{(used.Count > 0 ? $" · USED {string.Join(", ", used)}" : "")}";
    }

    private void Replan()
    {
        if (_library is not { } library)
        {
            return;
        }

        var start = SelectedStart switch
        {
            { ManualWear: true, Set: { } set } => set with { WearPercent = Math.Clamp(StartWear, 0, 100) },
            { Set: { } set } => set,
            _ => null,
        };
        var result = StrategyPlanner.Plan(library, RaceLaps, new PlannerOptions
        {
            MaxSets = new Dictionary<string, int> { ["Soft"] = SoftSets, ["Medium"] = MediumSets, ["Hard"] = HardSets },
            Sets = UsesGameSets && _gameSets is { } game ? RaceTyreSets.Available(game) : null,
            StartSet = start,
        });
        Problem = library.Compounds.Count == 0 ? "No clean laps recorded at this track yet." : result.Problem;
        Plans.Clear();
        for (var i = 0; i < result.Plans.Count; i++)
        {
            var plan = result.Plans[i];
            var segments = plan.Stints.Select(s => new PlanSegmentViewModel(s.Compound, Math.Max(24, BarWidth * s.Laps / result.RaceLaps - 4),
                s.StartWear > 0 ? $"{s.Laps} U" : $"{s.Laps}",
                string.Create(CultureInfo.InvariantCulture, $"{s.Compound} · laps {s.FirstLap}–{s.LastLap}{(s.StartWear > 0 ? $" · used set, {s.StartWear:0} % worn" : " · new set")}"))).ToList();
            Plans.Add(new PlanRowViewModel(i + 1, plan, segments, i < PlanBrushes.Length ? PlanBrushes[i] : Palette.TextMid));
        }

        HasPlans = Plans.Count > 0;
        PlanChart = HasPlans ? BuildChart(result) : null;
    }

    /// <summary>Predicted lap times of the three best plans, with their stints as timeline rows under the plot.</summary>
    private static ChartModel BuildChart(StrategyPlans result)
    {
        var top = result.Plans.Take(PlanHex.Length).ToList();
        var series = new List<ChartSeries>();
        var timeline = new List<ChartTimelineRow>();
        for (var i = 0; i < top.Count; i++)
        {
            var plan = top[i];
            var stints = plan.Stints;
            for (var s = 0; s < stints.Count; s++)
            {
                // One line per stint, so the stop shows as a step rather than a slope across the pit laps.
                var laps = Enumerable.Range(stints[s].FirstLap, stints[s].Laps).ToArray();
                series.Add(new ChartSeries($"#{i + 1} {string.Join("-", stints.Select(st => Palette.CompoundLetter(st.Compound)))}", PlanHex[i],
                    [.. laps.Select(l => (double)l)], [.. laps.Select(l => plan.LapSeconds[l - 1])],
                    Line: i == 0 ? ChartLine.Solid : ChartLine.Dashed, LineWidth: i == 0 ? 3 : 2, InLegend: s == 0));
            }

            var segments = stints.Select(st => new ChartTimelineSegment(st.FirstLap - 0.5 + 0.08, st.LastLap + 0.5 - 0.08,
                Palette.CompoundLetter(st.Compound), $"{st.Compound.ToUpperInvariant()} · {st.Laps} LAPS", "", Hex(Palette.Compound(st.Compound)))).ToList();
            timeline.Add(new ChartTimelineRow($"#{i + 1}", PlanHex[i], segments, [.. plan.Stops.Select(p => p.InLap + 0.5)]));
        }

        return new ChartModel("Predicted lap times · fuel load included, stops not", series, XLabel: "Lap", IntegerX: true, Timeline: timeline, YIsTime: true);
    }

    private static string Hex(IBrush brush) => brush is ISolidColorBrush solid ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}" : "#F4F6F9";
}
