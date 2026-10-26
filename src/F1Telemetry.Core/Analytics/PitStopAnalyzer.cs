using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Analytics;

/// <param name="LossSeconds">In-lap plus out-lap, minus what each would have been as a clean lap: the total time the stop cost.</param>
/// <param name="ReferenceLapSeconds">Mean of those two clean-lap references.</param>
/// <param name="PitLaneSeconds">Entry-to-exit time from the game's pit-lane timer (null in recordings made before it was stored).</param>
/// <param name="StationarySeconds">Time stopped in the box, from the same source.</param>
public sealed record PitStopAnalysis(
    int InLap,
    int OutLap,
    string FromCompound,
    string ToCompound,
    double LossSeconds,
    double ReferenceLapSeconds,
    double? PitLaneSeconds,
    double? StationarySeconds)
{
    /// <summary>The safety car out on the in-lap: a stop under one costs less, so it is measured and planned apart.</summary>
    public Neutralisation Neutralisation { get; init; }
}

/// <summary>
/// Measures what each pit stop cost. A stop spans two laps (in-lap and out-lap), and both are slower for reasons beyond
/// the pit lane itself (slowing for the entry, cold tyres), so the cost is the pair against clean laps. The clean-lap
/// time for each comes from its stint's pace fit where there is one: the in-lap on the oldest tyres, the out-lap on
/// fresh ones. Otherwise it is the median of the clean laps on that side of the stop. A set changed under a red flag cost
/// no stop and is left out.
/// <para>A lap of the stop run behind a (virtual) safety car is measured against the laps the car ran behind it without
/// stopping, those of the same safety-car period first, else any in the race: under a safety car the field is slow, so
/// against a racing lap the stop would look a minute long. A stop with no such lap to compare is left out.</para>
/// </summary>
public static class PitStopAnalyzer
{
    /// <summary>Clean laps used on each side of the stop when there is no fit.</summary>
    private const int ReferenceLaps = 3;

    /// <param name="predictCleanLap">Clean-lap time for a lap number from its stint's fit, or null when that stint has none.</param>
    public static IReadOnlyList<PitStopAnalysis> Analyze(IReadOnlyList<LapRecord> classifiedLaps, int sessionType, Func<int, double?>? predictCleanLap = null)
    {
        var isRace = SessionTypes.IsRace(sessionType);
        var stops = new List<PitStopAnalysis>();
        for (var i = 1; i < classifiedLaps.Count; i++)
        {
            var (inLap, outLap) = (classifiedLaps[i - 1], classifiedLaps[i]);
            if (!LapClassifier.StintChanged(inLap, outLap) || LapClassifier.UnderRedFlag(inLap, outLap) || outLap.LapNumber != inLap.LapNumber + 1 || !inLap.HasTime || !outLap.HasTime)
            {
                continue;
            }

            bool IsReference(LapRecord l) => l.LapType == LapType.Regular && l.HasTime && !(isRace && l.LapNumber == 1);
            var before = classifiedLaps.Take(i - 1).Where(IsReference).TakeLast(ReferenceLaps).Select(l => l.LapTimeMs / 1000.0).ToList();
            var after = classifiedLaps.Skip(i + 1).Where(IsReference).Take(ReferenceLaps).Select(l => l.LapTimeMs / 1000.0).ToList();
            var inReference = predictCleanLap?.Invoke(inLap.LapNumber) ?? (before.Count > 0 ? PaceModel.Median(before) : null);
            var outReference = predictCleanLap?.Invoke(outLap.LapNumber) ?? (after.Count > 0 ? PaceModel.Median(after) : null);
            (inReference, outReference) = (inReference ?? outReference, outReference ?? inReference);
            if (inLap.Neutralisation != Neutralisation.None)
            {
                inReference = NeutralisedReference(classifiedLaps, i - 1);
            }

            if (outLap.Neutralisation != Neutralisation.None)
            {
                outReference = NeutralisedReference(classifiedLaps, i);
            }

            if (inReference is not { } inClean || outReference is not { } outClean)
            {
                continue;
            }

            var lane = Math.Max(inLap.PitLaneTimeMs, outLap.PitLaneTimeMs);
            var stationary = Math.Max(inLap.PitStopTimeMs, outLap.PitStopTimeMs);
            stops.Add(new PitStopAnalysis(
                inLap.LapNumber,
                outLap.LapNumber,
                inLap.Compound,
                outLap.Compound,
                (inLap.LapTimeMs + outLap.LapTimeMs) / 1000.0 - inClean - outClean,
                (inClean + outClean) / 2,
                lane > 0 ? lane / 1000.0 : null,
                stationary > 0 ? stationary / 1000.0 : null)
            {
                Neutralisation = inLap.Neutralisation,
            });
        }

        return stops;
    }

    /// <summary>
    /// What lap <paramref name="index"/> would have taken behind its safety car without the stop: the median of the laps
    /// run behind the same kind of safety car without stopping, in the same period (the unbroken run of neutralised laps
    /// around it) when it has any, else anywhere in the race.
    /// </summary>
    private static double? NeutralisedReference(IReadOnlyList<LapRecord> laps, int index)
    {
        var kind = laps[index].Neutralisation;
        bool IsReference(LapRecord l) =>
            l.Neutralisation == kind && l.HasTime && l.LapType is not (LapType.Pit or LapType.RedFlag);

        int first = index, last = index;
        while (first > 0 && laps[first - 1].Neutralisation != Neutralisation.None && laps[first - 1].LapNumber == laps[first].LapNumber - 1)
        {
            first--;
        }

        while (last < laps.Count - 1 && laps[last + 1].Neutralisation != Neutralisation.None && laps[last + 1].LapNumber == laps[last].LapNumber + 1)
        {
            last++;
        }

        var period = laps.Skip(first).Take(last - first + 1).Where(IsReference).Select(l => l.LapTimeMs / 1000.0).ToList();
        var race = period.Count > 0 ? period : laps.Where(IsReference).Select(l => l.LapTimeMs / 1000.0).ToList();
        return race.Count > 0 ? PaceModel.Median(race) : null;
    }
}
