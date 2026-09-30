using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

/// <summary>Race gap column: time between each car and the player, or each car's interval to the car ahead.</summary>
public enum TowerGapMode
{
    GapToMe,
    Interval,
}

public enum GapTone
{
    Normal,

    /// <summary>Car ahead within 1 s of the player (gap to me), or the player within DRS range (interval).</summary>
    Ahead,

    /// <summary>Car behind within 1 s of the player (0.1 s in qualifying).</summary>
    Threat,

    Muted,
    Pit,
    Out,
}

public sealed record TowerRow(
    int CarIndex,
    int Position,
    string Code,
    uint? TeamColour,
    bool IsPlayer,
    string Gap,
    GapTone Tone,
    VisualCompound Compound,
    int TyreAgeLaps,
    double? ErsPercent,
    string? Penalty,
    string? Warnings,
    bool CutoffBelow);

/// <param name="Title">Session label, e.g. "RACE · LAP" or "QUALIFYING · Q2".</param>
/// <param name="Counter">The big number next to it: current lap in races, time left otherwise.</param>
/// <param name="CounterTotal">"/ 30" after the lap in races, empty otherwise.</param>
/// <param name="IsRace">Race gaps (the footer legend differs from qualifying).</param>
public sealed record TowerBoard(string Title, string Counter, string CounterTotal, string Mode, bool IsRace, IReadOnlyList<TowerRow> Rows)
{
    public static TowerBoard Empty { get; } = new("WAITING FOR DATA", "", "", "", true, []);
}

/// <summary>Builds the compact timing tower: the player plus the cars immediately ahead and behind.</summary>
public static class TimingTower
{
    public const int Ahead = 3;
    public const int Behind = 2;

    private const int CloseRaceMs = 1000;
    private const int CloseQualifyingMs = 100;

    public static TowerBoard Build(FieldSnapshot field, TowerGapMode mode)
    {
        var cars = field.Cars;
        var playerIndex = IndexOfPlayer(cars);
        if (playerIndex < 0)
        {
            return TowerBoard.Empty;
        }

        var me = cars[playerIndex];
        var isRace = SessionTypes.IsRace(field.SessionType);
        var cutoff = Cutoff(field.SessionType, cars.Count);
        var size = Ahead + 1 + Behind;
        var start = Math.Clamp(playerIndex - Ahead, 0, Math.Max(0, cars.Count - size));
        var rows = new List<TowerRow>(size);
        for (var i = start; i < Math.Min(cars.Count, start + size); i++)
        {
            var car = cars[i];
            var previous = i > 0 ? cars[i - 1] : null;
            var (gap, tone) = Status(car) ?? (isRace
                ? mode == TowerGapMode.Interval ? Interval(car, previous, me, field.TrackLength) : GapToMe(car, me, field.TrackLength)
                : BestLapGap(car, me));
            rows.Add(new TowerRow(
                car.CarIndex, car.Position, car.Code, car.TeamColour, car.IsPlayer, gap, tone, car.Compound, car.TyreAgeLaps,
                car.ErsPercent,
                car.PenaltySeconds > 0 ? $"+{car.PenaltySeconds}s" : null,
                car.PenaltySeconds == 0 && car.TrackLimitWarnings > 0 ? $"{car.TrackLimitWarnings}⚠" : null,
                cutoff == car.Position));
        }

        var (title, counter, total) = isRace
            ? ("RACE · LAP", Math.Max(1, me.CurrentLap).ToString(), $"/ {field.TotalLaps}")
            : (SessionTitle(field.SessionType), TimeFormat.Clock(field.SessionTimeLeftSeconds), "");
        var modeLabel = isRace ? mode == TowerGapMode.Interval ? "INTERVAL" : "GAP TO ME" : cutoff > 0 ? $"CUT-OFF P{cutoff}" : "BEST Δ";
        return new TowerBoard(title, counter, total, modeLabel, isRace, rows);
    }

    private static int IndexOfPlayer(IReadOnlyList<CarTiming> cars)
    {
        for (var i = 0; i < cars.Count; i++)
        {
            if (cars[i].IsPlayer)
            {
                return i;
            }
        }

        return -1;
    }

    private static (string, GapTone)? Status(CarTiming car) => car switch
    {
        { ResultStatus: ResultStatus.Disqualified } => ("DSQ", GapTone.Out),
        { ResultStatus: ResultStatus.DidNotFinish or ResultStatus.Retired or ResultStatus.NotClassified } => ("DNF", GapTone.Out),
        { PitStatus: not PitStatus.None } => ("PIT", GapTone.Pit),
        _ => null,
    };

    private static (string, GapTone) GapToMe(CarTiming car, CarTiming me, float trackLength)
    {
        if (car.IsPlayer)
        {
            return ("—", GapTone.Muted);
        }

        var ahead = car.Position < me.Position;
        var laps = Laps(me.TotalDistance - car.TotalDistance, trackLength); // > 0: the car is laps behind the player
        if ((ahead && laps < 0) || (!ahead && laps > 0))
        {
            return (ahead ? $"−{-laps}L" : $"+{laps}L", GapTone.Muted);
        }

        var gap = (long)car.DeltaToLeaderMs - me.DeltaToLeaderMs;
        var tone = ahead && gap > -CloseRaceMs ? GapTone.Ahead : !ahead && gap < CloseRaceMs ? GapTone.Threat : GapTone.Normal;
        return (Signed(gap), tone);
    }

    private static (string, GapTone) Interval(CarTiming car, CarTiming? previous, CarTiming me, float trackLength)
    {
        if (previous is null)
        {
            return ("LEADER", GapTone.Muted);
        }

        if (Laps(previous.TotalDistance - car.TotalDistance, trackLength) is var laps and > 0)
        {
            return ($"+{laps}L", GapTone.Muted);
        }

        var interval = car.DeltaToCarInFrontMs;
        var close = interval < CloseRaceMs;
        var tone = car.IsPlayer && close ? GapTone.Ahead : previous.IsPlayer && close ? GapTone.Threat : GapTone.Normal;
        return (TimeFormat.Delta(interval), tone);
    }

    private static (string, GapTone) BestLapGap(CarTiming car, CarTiming me)
    {
        if (car.IsPlayer || me.BestLapMs == 0)
        {
            return car.BestLapMs == 0 ? ("NO TIME", GapTone.Muted) : (TimeFormat.Lap(car.BestLapMs), car.IsPlayer ? GapTone.Muted : GapTone.Normal);
        }

        if (car.BestLapMs == 0)
        {
            return ("NO TIME", GapTone.Muted);
        }

        var gap = (long)car.BestLapMs - me.BestLapMs;
        var tone = car.Position > me.Position && gap < CloseQualifyingMs ? GapTone.Threat : GapTone.Normal;
        return (Signed(gap), tone);
    }

    /// <summary>Whole laps between two cars from their total distance (0 when the track length is unknown).</summary>
    private static int Laps(float distance, float trackLength) =>
        trackLength > 0 ? (int)Math.Truncate(distance / trackLength) : 0;

    private static string Signed(long ms) => TimeFormat.Delta(ms).Replace('-', '−');

    /// <summary>Last position that goes through: Q1/SQ1 drop the bottom 5 (6 on a 22-car grid), Q2/SQ2 keep the top 10.</summary>
    private static int Cutoff(int sessionType, int cars) => sessionType switch
    {
        5 or 10 when cars > 15 => cars - (cars > 20 ? 6 : 5),
        6 or 11 when cars > 10 => 10,
        _ => 0,
    };

    private static string SessionTitle(int sessionType) => sessionType switch
    {
        1 or 2 or 3 => $"PRACTICE {sessionType}",
        4 => "SHORT PRACTICE",
        5 or 6 or 7 => $"QUALIFYING · Q{sessionType - 4}",
        8 => "SHORT QUALIFYING",
        9 => "ONE-SHOT QUALIFYING",
        10 or 11 or 12 => $"SPRINT SHOOTOUT · SQ{sessionType - 9}",
        13 => "SHORT SHOOTOUT",
        14 => "ONE-SHOT SHOOTOUT",
        SessionTypes.TimeTrial => "TIME TRIAL",
        _ => SessionTypes.Name(sessionType).ToUpperInvariant(),
    };
}
