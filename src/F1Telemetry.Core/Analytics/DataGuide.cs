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
public sealed record GuideItem(string Title, GuideState State, string Have, string Do, string Why, GuideSession Session, bool Optional = false, string Short = "");

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
            items.Add(new GuideItem(
                $"{compound} long run",
                state,
                laps == 0 ? "no clean laps here" : $"{laps} clean {Plural(laps, "lap")}, longest run {longest}",
                state == GuideState.Done ? "" : $"Run {SteadyRunLaps}+ clean laps in a row on the {name}: one pace, no flashbacks, clear of traffic.",
                state == GuideState.Missing
                    ? $"With fewer than {minLaps} clean laps the {name} is left out of every plan."
                    : $"A run of {SteadyRunLaps}+ laps is what shows how fast the {name} loses time as it ages; shorter runs give a rough slope.",
                GuideSession.Practice,
                Short: laps < minLaps ? $"{compound} · {minLaps}+ clean laps" : $"{compound} · {SteadyRunLaps}-lap run"));
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
                Short: "Two compounds · one session"));
        }

        var measuredFuel = library.FuelEffect.Source == FuelEffectSource.Measured;
        items.Add(new GuideItem(
            "Fuel effect",
            measuredFuel ? GuideState.Done : GuideState.Missing,
            string.Create(CultureInfo.InvariantCulture, $"{library.FuelEffect.SecondsPerKg:0.000} s/kg, {(measuredFuel ? library.FuelEffect.Basis : "typical F1 value")}"),
            measuredFuel ? "" : "In one session run the same compound in two stints at different fuel loads, 3+ clean laps each (a long run on race fuel, then the same compound again on what is left).",
            "Within one stint the tyres wear as the fuel burns off, so the two can't be told apart. The same compound at two fuel loads separates them; until then a typical figure is used.",
            GuideSession.Practice,
            Short: "Same tyre · two fuel loads"));

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
                Short: string.Create(CultureInfo.InvariantCulture, $"Run a set past {options.TyreCliffWearPercent:0} % wear")));
        }

        items.Add(new GuideItem(
            "Pit stop loss",
            library.PitLossStops > 0 ? GuideState.Done : GuideState.Missing,
            string.Create(CultureInfo.InvariantCulture, $"{library.PitLossSeconds:0.0} s, {(library.PitLossStops > 0 ? $"median of {library.PitLossStops} recorded {Plural(library.PitLossStops, "stop")}" : "default")}"),
            library.PitLossStops > 0 ? "" : "Record a race here with a pit stop under green (a short race is enough).",
            "What a stop costs decides between one stop and two. It is measured from a race's in-lap and out-lap against clean laps; until then a default is used.",
            GuideSession.Race,
            Short: "Race with a pit stop"));

        var neutralised = library.SafetyCarPitLossStops + library.VirtualSafetyCarPitLossStops;
        items.Add(new GuideItem(
            "Stop under a safety car",
            neutralised > 0 ? GuideState.Done : GuideState.Missing,
            neutralised > 0 ? $"{neutralised} recorded {Plural(neutralised, "stop")}" : "shares of a green stop assumed",
            neutralised > 0 ? "" : "Nothing to set up: pit under a safety car or VSC when the plan's SC window says so, and the stop is measured.",
            "The laps on which a safety car makes an early stop pay depend on what the stop costs under it.",
            GuideSession.Race,
            Optional: true,
            Short: "Pit under a safety car"));

        return new DataGuide(items);
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
