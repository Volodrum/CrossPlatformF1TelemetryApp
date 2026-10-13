using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Recording;

/// <summary>
/// Outside races the game reuses a lap number after a run that ends in the pits. A lap abandoned into the pit lane, the
/// in-lap, the garage and the next out-lap all keep the number of the lap that follows them: the car crosses the line in
/// the pit lane, and again at the end of the out-lap, without the number moving on, and only the stretch after the last
/// of those crossings is the lap the game times. The out-lap of a session's first run is the stretch before the line,
/// with a negative lap distance. Samples that belong to no lap are kept on lap 0.
/// </summary>
public static class LapRestarts
{
    /// <summary>The lap distance falling by more than this (m) between two samples of one lap is a line crossing.</summary>
    public const double LineCrossing = 500;

    /// <summary>Races count every line crossing as a lap, the pit lane's too, so there is nothing to correct.</summary>
    public static bool Applies(int sessionType) => !SessionTypes.IsRace(sessionType);

    /// <summary>
    /// <paramref name="next"/> crossed the line without the lap number moving on: the lap starts again there. Time must
    /// move forward: a flashback also takes the lap distance back.
    /// </summary>
    public static bool IsRestart(TelemetrySample previous, TelemetrySample next) =>
        next.LapNumber == previous.LapNumber && next.SessionTime > previous.SessionTime && previous.LapDistance - next.LapDistance > LineCrossing;
}
