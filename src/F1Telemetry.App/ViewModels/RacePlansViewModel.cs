using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;

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

    public string Note => string.Join(" ", new[]
    {
        Model.DegradationMeasured ? "" : "Degradation assumed: no stint had two clean laps to measure it.",
        Model.Linked ? "" : "Pace as recorded: never run in a session linked to this one, so the conditions aren't corrected.",
        Model.IsDry ? "" : "Wet compound: left out of the dry plans.",
    }.Where(n => n.Length > 0));

    public bool HasNote => Note.Length > 0;
}

/// <summary>A stint on a plan's bar, as wide as its share of the race.</summary>
public sealed record PlanSegmentViewModel(string Compound, double Width, string Laps)
{
    public IBrush Brush => Palette.Compound(Compound);
}

public sealed record PlanStopViewModel(PlannedStop Stop)
{
    public string Pit => $"PIT L{Stop.InLap}";
    public string Window => Stop.WindowFirst == Stop.WindowLast ? "no window" : $"window L{Stop.WindowFirst}–{Stop.WindowLast}";
    public string Undercut => Stop.UndercutSeconds >= 0 ? $"undercut +{Stop.UndercutSeconds:0.00} s" : $"overcut +{-Stop.UndercutSeconds:0.00} s";
    public IBrush UndercutBrush => Stop.UndercutSeconds >= 0 ? Palette.Faster : Palette.Slower;
}

public sealed record PlanRowViewModel(int Rank, RacePlan Plan, IReadOnlyList<PlanSegmentViewModel> Segments, IBrush RankBrush)
{
    public string RankText => $"#{Rank}";
    public string StopsText => Plan.StopCount == 1 ? "1 STOP" : $"{Plan.StopCount} STOPS";
    public string Mix => string.Join(" → ", Plan.Stints.Select(s => $"{Palette.CompoundLetter(s.Compound)}{s.Laps}"));
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
        _loadedFor = null;
        Compounds.Clear();
        Plans.Clear();
        HasLibrary = HasPlans = false;
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
            if (version != _version)
            {
                return;
            }

            _library = library;
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

    private void Replan()
    {
        if (_library is not { } library)
        {
            return;
        }

        var result = StrategyPlanner.Plan(library, RaceLaps, new PlannerOptions
        {
            MaxSets = new Dictionary<string, int> { ["Soft"] = SoftSets, ["Medium"] = MediumSets, ["Hard"] = HardSets },
        });
        Problem = library.Compounds.Count == 0 ? "No clean laps recorded at this track yet." : result.Problem;
        Plans.Clear();
        for (var i = 0; i < result.Plans.Count; i++)
        {
            var plan = result.Plans[i];
            var segments = plan.Stints.Select(s => new PlanSegmentViewModel(s.Compound, Math.Max(24, BarWidth * s.Laps / result.RaceLaps - 4), $"{s.Laps}")).ToList();
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
