using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Detects sector completions between consecutive lap-history snapshots and produces the lap / sector
/// delta against the personal-best lap (purple = session best, green = faster, red = slower).
/// </summary>
public sealed class DeltaCalculator
{
    private LapRecord? _previousCurrent;

    public void Reset() => _previousCurrent = null;

    /// <param name="laps">Laps in ascending order; the last entry is the lap in progress.</param>
    public DeltaUpdate? Update(IReadOnlyList<LapRecord> laps)
    {
        if (laps.Count == 0)
        {
            return null;
        }

        var current = laps[^1];
        var previous = _previousCurrent;
        _previousCurrent = current;
        if (previous is null)
        {
            return null;
        }

        var completed = laps.Take(laps.Count - 1).ToList();
        var pb = completed.FirstOrDefault(l => l.IsBestLap && l.HasTime) ?? Fastest(completed);

        if (previous.LapNumber == current.LapNumber)
        {
            if (pb is null)
            {
                return null;
            }

            if (previous.Sector1Ms == 0 && current.Sector1Ms > 0)
            {
                var delta = (int)current.Sector1Ms - (int)pb.Sector1Ms;
                return new DeltaUpdate(1, delta, Colour(delta, false), delta, Colour(delta, current.IsBestSector1));
            }

            if (previous.Sector2Ms == 0 && current.Sector2Ms > 0)
            {
                var lapDelta = (int)(current.Sector1Ms + current.Sector2Ms) - (int)(pb.Sector1Ms + pb.Sector2Ms);
                var sectorDelta = (int)current.Sector2Ms - (int)pb.Sector2Ms;
                return new DeltaUpdate(2, lapDelta, Colour(lapDelta, false), sectorDelta, Colour(sectorDelta, current.IsBestSector2));
            }

            return null;
        }

        // Lap boundary: compare the lap that just finished with the best lap *before* it.
        if (completed.Count == 0 || completed[^1] is not { HasTime: true } finished)
        {
            return null;
        }

        var reference = finished.IsBestLap ? Fastest(completed.Take(completed.Count - 1)) : pb;
        if (reference is null)
        {
            return null;
        }

        var finishDelta = (int)finished.LapTimeMs - (int)reference.LapTimeMs;
        var s3Delta = (int)finished.Sector3Ms - (int)reference.Sector3Ms;
        return new DeltaUpdate(3, finishDelta, Colour(finishDelta, finished.IsBestLap), s3Delta, Colour(s3Delta, finished.IsBestSector3));
    }

    private static LapRecord? Fastest(IEnumerable<LapRecord> laps) =>
        laps.Where(l => l.HasTime).MinBy(l => l.LapTimeMs);

    private static DeltaColor Colour(int deltaMs, bool isSessionBest) =>
        isSessionBest ? DeltaColor.SessionBest : deltaMs < 0 ? DeltaColor.PersonalBest : DeltaColor.Slower;
}
