using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Analytics;

/// <summary>The sets race plans can use, read from the tyre sets the game sent during a recording (packet 12).</summary>
public static class RaceTyreSets
{
    /// <summary>
    /// The snapshot to plan a race from: in a race, the sets as it started (the last snapshot before any set wore further,
    /// so a set changed on the grid counts but the laps driven don't); in any other session, what was left at its end.
    /// </summary>
    public static TyreSetsChange? ForPlanning(IReadOnlyList<TyreSetsChange> snapshots, int sessionType)
    {
        if (snapshots.Count == 0)
        {
            return null;
        }

        if (!SessionTypes.IsRace(sessionType))
        {
            return snapshots[^1];
        }

        var start = snapshots[0];
        foreach (var snapshot in snapshots.Skip(1))
        {
            if (snapshot.Sets.Count != start.Sets.Count || snapshot.Sets.Zip(start.Sets).Any(p => p.First.Wear > p.Second.Wear))
            {
                break;
            }

            start = snapshot;
        }

        return start;
    }

    /// <summary>Every dry set that can still be used, new and used, the fitted one included.</summary>
    public static IReadOnlyList<PlannerSet> Available(TyreSetsChange sets) =>
        sets.Sets.Where(s => s.Available && Dry(s) is not null).Select(s => new PlannerSet(Dry(s)!, s.Wear)).ToList();

    /// <summary>The set on the car, when it is a dry one.</summary>
    public static PlannerSet? Fitted(TyreSetsChange sets) =>
        sets.Fitted is { } fitted && Dry(fitted) is { } compound ? new PlannerSet(compound, fitted.Wear) : null;

    private static string? Dry(TyreSet set) => Compounds.Visual(set.VisualCompound) switch
    {
        VisualCompound.Soft => "Soft",
        VisualCompound.Medium => "Medium",
        VisualCompound.Hard => "Hard",
        _ => null,
    };
}
