using System.Globalization;
using F1Telemetry.Core.Engine;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Analytics;

public enum GuideState
{
    /// <summary>Nothing recorded: the plans use a default, or can't use the compound at all.</summary>
    Missing,

    /// <summary>Recorded, but thin: more of it steadies the figure.</summary>
    Partial,

    /// <summary>Measured at this track.</summary>
    Done,
}

/// <summary>What a guide item asks for: decides the picture that shows how to do it.</summary>
public enum GuideKind
{
    /// <summary>A run of clean laps in a row on one compound.</summary>
    LongRun,

    /// <summary>Two compounds in one session.</summary>
    TwoCompounds,

    /// <summary>The same compound at two fuel loads.</summary>
    FuelLoads,

    /// <summary>A set run past the cliff wear.</summary>
    Cliff,

    /// <summary>A race with a pit stop.</summary>
    PitStop,

    /// <summary>A stop under a safety car.</summary>
    SafetyCar,

    // The energy data guide (see EnergyGuideBuilder).

    /// <summary>Laps that record ICE and MGU-K power.</summary>
    PowerLaps,

    /// <summary>A run of laps in a row at race pace.</summary>
    RaceRun,

    /// <summary>One deploy mode flat out across the speeds of the lap.</summary>
    ModeSpeeds,

    /// <summary>Every deploy mode on every straight.</summary>
    ModeStraights,

    /// <summary>Braking with room in the battery and below the harvest limit.</summary>
    BrakingHarvest,

    /// <summary>Flat out on the straights in each aero (DRS / active aero) state.</summary>
    AeroStates,

    /// <summary>A push lap from a full battery.</summary>
    FullBattery,

    /// <summary>The model replaying a lap as driven.</summary>
    Replay,
}

/// <summary>The kind of session in which a figure is best measured.</summary>
public enum GuideSession
{
    Practice,
    Qualifying,
    Race,
}

/// <summary>
/// One thing the race plans rest on at a track.
/// </summary>
/// <param name="Title">What it is ("Soft long run", "Pit stop loss").</param>
/// <param name="Have">What is recorded now ("5 clean laps, longest run 3"; "default 22.0 s").</param>
/// <param name="Do">What to drive to measure it; empty when <see cref="State"/> is <see cref="GuideState.Done"/>.</param>
/// <param name="Why">What the plans do without it.</param>
/// <param name="Session">Where it is best measured.</param>
/// <param name="Optional">Can't be set up on purpose (a stop under a safety car): never the next thing to do, and not counted.</param>
/// <param name="Short">The run to do in a few words, for tight places ("Soft · 6-lap run").</param>
/// <param name="Kind">What it asks for, for its picture.</param>
/// <param name="Compound">The compound it is about, when it is about one.</param>
/// <param name="Progress">How far along it is, 0 to 1.</param>
/// <param name="Count">The same as a count ("4 / 6 LAPS"); empty when it is all or nothing.</param>
/// <param name="Mode">The ERS deploy mode it is about, when it is about one (energy guide).</param>
public sealed record GuideItem(string Title, GuideState State, string Have, string Do, string Why, GuideSession Session, bool Optional = false, string Short = "",
    GuideKind Kind = GuideKind.LongRun, string? Compound = null, double Progress = 0, string Count = "", int? Mode = null);

/// <summary>
/// What the race plans know at a track and what to drive next to know more: the checklist behind the strategy tab's
/// data guide. Items are in order of how much the plans depend on them.
/// </summary>
public sealed record DataGuide(IReadOnlyList<GuideItem> Items)
{
    /// <summary>Items that count towards the score (the optional ones don't).</summary>
    public int Total => Items.Count(i => !i.Optional);

    public int Done => Items.Count(i => !i.Optional && i.State == GuideState.Done);

    /// <summary>The most valuable thing still to measure, in any session; null when everything is.</summary>
    public GuideItem? Next => Items.FirstOrDefault(i => !i.Optional && i.State != GuideState.Done);

    /// <summary>The most valuable thing still to measure in this kind of session; null when there is none.</summary>
    public GuideItem? NextIn(GuideSession session) => Items.FirstOrDefault(i => !i.Optional && i.State != GuideState.Done && i.Session == session);

    /// <summary>The kind of session a game session type is, or null (time trial, unknown).</summary>
    public static GuideSession? SessionOf(int sessionType) =>
        SessionTypes.IsRace(sessionType) ? GuideSession.Race
        : SessionTypes.IsQualifying(sessionType) ? GuideSession.Qualifying
        : SessionTypes.IsPractice(sessionType) ? GuideSession.Practice
        : null;
}

/// <summary>
/// Builds a <see cref="DataGuide"/> from a track's <see cref="CompoundLibrary"/> and the sessions it came from: each
/// figure the planner uses, whether it was measured here or is a default, and the run that measures it.
/// </summary>
public static class DataGuideBuilder
{
    /// <summary>A compound's longest run of clean laps from which its pace and degradation are steady.</summary>
    public const int SteadyRunLaps = 6;

    private static readonly string[] Dry = ["Soft", "Medium", "Hard"];

    public static DataGuide Build(CompoundLibrary library, IReadOnlyList<RaceInput> sessions, StrategyOptions? options = null, PlannerOptions? planner = null)
    {
        options ??= new StrategyOptions();
        planner ??= new PlannerOptions();
        var minLaps = planner.MinCompoundLaps;
        var items = new List<GuideItem>();

        // Each dry compound: enough clean laps to be planned with, and one run long enough to show its degradation.
        var usable = 0;
        foreach (var compound in Dry)
        {
            var model = library[compound];
            var laps = model?.Laps ?? 0;
            var longest = LongestRun(sessions, compound);
            var name = compound.ToLowerInvariant();
            if (laps >= minLaps)
            {
                usable++;
            }

            var state = laps < minLaps ? GuideState.Missing
                : longest >= SteadyRunLaps && model!.DegradationMeasured ? GuideState.Done
                : GuideState.Partial;

            // A long run that measured nothing: its laps got faster as it went.
            var improving = state == GuideState.Partial && longest >= SteadyRunLaps;
            items.Add(new GuideItem(
                $"{compound} long run",
                state,
                laps == 0 ? "no clean laps here"
                : improving ? $"{laps} clean {Plural(laps, "lap")}, longest run {longest}, but the laps got faster through the run, so it shows no tyre wear"
                : $"{laps} clean {Plural(laps, "lap")}, longest run {longest}",
                state == GuideState.Done ? ""
                : improving ? $"Run the {name} again later in the session, once the track has rubbered in and you are up to speed: {SteadyRunLaps}+ laps at one steady pace."
                : $"Run {SteadyRunLaps}+ clean laps in a row on the {name}: one pace, no flashbacks, clear of traffic.",
                state == GuideState.Missing
                    ? $"With fewer than {minLaps} clean laps the {name} is left out of every plan."
                    : $"A run of {SteadyRunLaps}+ laps is what shows how fast the {name} loses time as it ages; shorter runs give a rough slope.",
                GuideSession.Practice,
                Short: laps < minLaps ? $"{compound} · {minLaps}+ clean laps" : $"{compound} · {SteadyRunLaps}-lap run",
                Kind: GuideKind.LongRun,
                Compound: compound,
                Progress: Math.Min(1.0, (double)longest / SteadyRunLaps),
                Count: $"{Math.Min(longest, SteadyRunLaps)} / {SteadyRunLaps} LAPS"));
        }

        // Compounds are compared through sessions that share one.
        var unlinked = library.Compounds.Where(c => c.IsDry && c.Laps >= minLaps && !c.Linked).Select(c => c.Compound.ToLowerInvariant()).ToList();
        if (usable >= 2)
        {
            items.Add(new GuideItem(
                "Compounds in the same conditions",
                unlinked.Count == 0 ? GuideState.Done : GuideState.Partial,
                unlinked.Count == 0 ? "every compound is tied to this session" : $"{string.Join(" and ", unlinked)} only run on another day",
                unlinked.Count == 0 ? "" : $"Run the {string.Join(" and the ", unlinked)} in a session that also has a compound from this one (two compounds in one practice).",
                "Track temperature and rubber change between sessions. Two compounds in one session let the day's conditions be taken out of the gap between them.",
                GuideSession.Practice,
                Short: "Two compounds · one session",
                Kind: GuideKind.TwoCompounds,
                Progress: unlinked.Count == 0 ? 1 : 0.5));
        }

        var measuredFuel = library.FuelEffect.Source == FuelEffectSource.Measured;
        var (fuelHave, fuelDo, fuelProgress) = measuredFuel
            ? (string.Create(CultureInfo.InvariantCulture, $"{library.FuelEffect.SecondsPerKg:0.000} s/kg, {library.FuelEffect.Basis}"), "", 1.0)
            : FuelRunsSoFar(sessions);
        items.Add(new GuideItem(
            "Fuel effect",
            measuredFuel ? GuideState.Done : fuelProgress > 0 ? GuideState.Partial : GuideState.Missing,
            fuelHave,
            fuelDo,
            "Within one stint the tyres wear as the fuel burns off, so the two can't be told apart. The same compound at two fuel loads separates them; until then a typical figure is used.",
            GuideSession.Practice,
            Short: "Same tyre · two fuel loads",
            Kind: GuideKind.FuelLoads,
            Progress: fuelProgress,
            Count: measuredFuel ? "" : $"{Math.Min(2, (int)Math.Round(fuelProgress * 2))} / 2 RUNS"));

        var dryModels = library.Compounds.Where(c => c.IsDry && c.Laps >= minLaps).ToList();
        if (dryModels.Count > 0)
        {
            var measured = dryModels.Where(c => c.CliffMeasured).Select(c => c.Compound.ToLowerInvariant()).ToList();
            var assumed = dryModels.Where(c => !c.CliffMeasured).Select(c => c.Compound.ToLowerInvariant()).ToList();
            items.Add(new GuideItem(
                "Tyre cliff",
                assumed.Count == 0 ? GuideState.Done : measured.Count > 0 ? GuideState.Partial : GuideState.Missing,
                measured.Count == 0 ? "assumed for every compound" : $"measured on the {string.Join(" and ", measured)}",
                assumed.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
                    $"Keep a set of the {string.Join(" or ", assumed)} on until its most worn tyre is past {options.TyreCliffWearPercent:0} % and run 3+ clean laps there."),
                string.Create(CultureInfo.InvariantCulture,
                    $"Past {options.TyreCliffWearPercent:0} % wear a set loses time faster. Until laps are run there each one is assumed to cost {options.DefaultTyreCliffSecondsPerLap:0.0} s more, which decides how far a plan stretches a stint."),
                GuideSession.Practice,
                Short: string.Create(CultureInfo.InvariantCulture, $"Run a set past {options.TyreCliffWearPercent:0} % wear"),
                Kind: GuideKind.Cliff,
                Progress: (double)measured.Count / dryModels.Count,
                Count: $"{measured.Count} / {dryModels.Count} COMPOUNDS"));
        }

        items.Add(new GuideItem(
            "Pit stop loss",
            library.PitLossStops > 0 ? GuideState.Done : GuideState.Missing,
            string.Create(CultureInfo.InvariantCulture, $"{library.PitLossSeconds:0.0} s, {(library.PitLossStops > 0 ? $"median of {library.PitLossStops} recorded {Plural(library.PitLossStops, "stop")}" : "default")}"),
            library.PitLossStops > 0 ? "" : "Record a race here with a pit stop under green (a short race is enough).",
            "What a stop costs decides between one stop and two. It is measured from a race's in-lap and out-lap against clean laps; until then a default is used.",
            GuideSession.Race,
            Short: "Race with a pit stop",
            Kind: GuideKind.PitStop,
            Progress: library.PitLossStops > 0 ? 1 : 0));

        var neutralised = library.SafetyCarPitLossStops + library.VirtualSafetyCarPitLossStops;
        items.Add(new GuideItem(
            "Stop under a safety car",
            neutralised > 0 ? GuideState.Done : GuideState.Missing,
            neutralised > 0 ? $"{neutralised} recorded {Plural(neutralised, "stop")}" : "shares of a green stop assumed",
            neutralised > 0 ? "" : "Nothing to set up: pit under a safety car or VSC when the plan's SC window says so, and the stop is measured.",
            "The laps on which a safety car makes an early stop pay depend on what the stop costs under it.",
            GuideSession.Race,
            Optional: true,
            Short: "Pit under a safety car",
            Kind: GuideKind.SafetyCar,
            Progress: neutralised > 0 ? 1 : 0));

        return new DataGuide(items);
    }

    /// <summary>
    /// What the sessions have towards the fuel effect when it isn't measured yet: the session and compound with the most
    /// runs of <see cref="PaceModel.FuelRunLaps"/>+ clean laps, what to drive next, and how far along it is (one run of
    /// the two is half).
    /// </summary>
    private static (string Have, string Do, double Progress) FuelRunsSoFar(IReadOnlyList<RaceInput> sessions)
    {
        var minimum = PaceModel.FuelRunLaps;
        var best = sessions
            .SelectMany(s => PaceModel.FuelRuns(s.Analysis.PaceLaps))
            .OrderByDescending(c => c.Runs.Count).ThenByDescending(c => c.Runs.Sum(r => r.Laps)).ThenByDescending(c => c.ShortRuns)
            .Select(c => ((string Compound, IReadOnlyList<(int Stint, int Laps, double StartKg)> Runs, int ShortRuns)?)c)
            .FirstOrDefault();
        var shortNote = best is { ShortRuns: > 0 } b
            ? $" ({b.ShortRuns} shorter {Plural(b.ShortRuns, "run")} on the {b.Compound.ToLowerInvariant()} left out: a run of 1–2 laps after an out-lap starts on a full battery)"
            : "";
        switch (best)
        {
            case null or { Runs.Count: 0 }:
                return ($"no run of {minimum}+ clean laps yet{shortNote}, typical F1 value used",
                    $"In one session run the same compound twice at different fuel loads, {minimum}+ clean laps each: a long run on race fuel, then the same compound again on much less fuel (or the other way round).",
                    0);

            case { Runs.Count: 1 } one:
                var run = one.Runs[0];
                var name = one.Compound.ToLowerInvariant();
                return (string.Create(CultureInfo.InvariantCulture, $"one run of {minimum}+ laps on the {name} ({run.Laps} laps from {run.StartKg:0} kg){shortNote}, typical F1 value used"),
                    string.Create(CultureInfo.InvariantCulture,
                        $"In the same session run the {name} again for {minimum}+ clean laps, starting on a fuel load well away from {run.StartKg:0} kg (10 kg or more lighter or heavier)."),
                    0.5);

            case { } many:
                var loads = string.Join(" and ", many.Runs.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.StartKg:0} kg")));
                return ($"{many.Runs.Count} runs on the {many.Compound.ToLowerInvariant()} (from {loads}), too close in fuel or too scattered to separate the fuel from the tyres, typical F1 value used",
                    $"Run the {many.Compound.ToLowerInvariant()} again for {minimum}+ steady laps, clear of traffic, starting at least 10 kg of fuel away from the others.",
                    0.75);
        }
    }

    /// <summary>The most clean laps a compound has in one stint at the track.</summary>
    private static int LongestRun(IReadOnlyList<RaceInput> sessions, string compound)
    {
        var longest = 0;
        foreach (var session in sessions)
        {
            foreach (var stint in session.Analysis.PaceLaps.Where(l => l.Compound == compound).GroupBy(l => l.StintNumber))
            {
                longest = Math.Max(longest, stint.Count());
            }
        }

        return longest;
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";
}
