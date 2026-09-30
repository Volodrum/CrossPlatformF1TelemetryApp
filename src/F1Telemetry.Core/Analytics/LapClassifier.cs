using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Analytics;

/// <summary>
/// Final (post-hoc) lap classification used by the dashboard and analytics. Single source of truth for
/// logic that previously lived in two places.
/// <list type="bullet">
/// <item>Out-laps with an unknown compound inherit it from the next known lap.</item>
/// <item>Race: laps flagged in-session plus in-laps detected from stint / compound transitions are PIT, and
/// an isolated pit entry also marks the following out-lap; every other lap is REGULAR.</item>
/// <item>Practice / qualifying / time trial: laps without a lap time are PIT (out/in-laps), others REGULAR.</item>
/// </list>
/// </summary>
public static class LapClassifier
{
    public static IReadOnlyList<LapRecord> Classify(IReadOnlyList<LapRecord> lapsAscending, int sessionType)
    {
        var laps = BackfillCompounds(lapsAscending);

        if (!SessionTypes.IsRace(sessionType))
        {
            return laps.Select(l => l with { LapType = l.HasTime ? LapType.Regular : LapType.Pit }).ToList();
        }

        var pitEntries = new HashSet<int>();
        for (var i = 0; i < laps.Count; i++)
        {
            if (laps[i].LapType == LapType.Pit)
            {
                pitEntries.Add(i);
            }
        }

        // Compound / stint transitions: the last lap of the previous stint is the in-lap.
        var lastKnown = -1;
        for (var i = 0; i < laps.Count; i++)
        {
            if (!IsKnown(laps[i].Compound))
            {
                continue;
            }

            if (lastKnown >= 0 && StintChanged(laps[lastKnown], laps[i]))
            {
                pitEntries.Add(lastKnown);
            }

            lastKnown = i;
        }

        var pitLaps = new HashSet<int>(pitEntries);
        foreach (var idx in pitEntries)
        {
            var isolated = !pitEntries.Contains(idx - 1);
            if (isolated && idx + 1 < laps.Count && laps[idx + 1].LapNumber == laps[idx].LapNumber + 1)
            {
                pitLaps.Add(idx + 1);
            }
        }

        return laps.Select((l, i) => l with { LapType = pitLaps.Contains(i) ? LapType.Pit : LapType.Regular }).ToList();
    }

    private static bool StintChanged(LapRecord previous, LapRecord current) =>
        previous.StintIndex >= 0 && current.StintIndex >= 0
            ? previous.StintIndex != current.StintIndex
            : previous.Compound != current.Compound;

    private static List<LapRecord> BackfillCompounds(IReadOnlyList<LapRecord> laps)
    {
        var result = laps.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            if (IsKnown(result[i].Compound))
            {
                continue;
            }

            for (var j = i + 1; j < result.Count; j++)
            {
                if (IsKnown(result[j].Compound))
                {
                    result[i] = result[i] with
                    {
                        Compound = result[j].Compound,
                        ActualCompound = result[j].ActualCompound,
                        StintIndex = result[i].StintIndex >= 0 ? result[i].StintIndex : result[j].StintIndex,
                    };
                    break;
                }
            }
        }

        return result;
    }

    internal static bool IsKnown(string compound) => compound is not ("Unknown" or "" or "Invalid");
}
