using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Models;

public enum LapType
{
    Regular,
    Pit,
    SafetyCar,
    VirtualSafetyCar,
}

/// <summary>Summary of one lap: timing from the session-history packet plus engine-derived context.</summary>
public sealed record LapRecord
{
    public required int LapNumber { get; init; }
    public uint LapTimeMs { get; init; }
    public uint Sector1Ms { get; init; }
    public uint Sector2Ms { get; init; }
    public uint Sector3Ms { get; init; }
    public bool IsValid { get; init; } = true;
    public bool IsBestLap { get; init; }
    public bool IsBestSector1 { get; init; }
    public bool IsBestSector2 { get; init; }
    public bool IsBestSector3 { get; init; }

    /// <summary>Visual compound name (Soft/Medium/Hard/Inter/Wet/Unknown).</summary>
    public string Compound { get; init; } = "Unknown";

    public string ActualCompound { get; init; } = "Unknown";

    /// <summary>0-based tyre stint index from the game's stint history, or -1 when unknown.</summary>
    public int StintIndex { get; init; } = -1;

    public int? CarPosition { get; init; }
    public LapType LapType { get; init; } = LapType.Regular;

    public bool HasTime => LapTimeMs > 0;
}

public sealed record RecordingInfo(
    long Id,
    string SessionUid,
    string Description,
    int TrackId,
    string TrackName,
    int SessionType,
    GameFormat Format,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime);

public sealed record NewRecording(
    string SessionUid,
    string Description,
    int TrackId,
    string TrackName,
    int SessionType,
    GameFormat Format);

/// <summary>Per-lap min/max of fuel and wear, used to derive consumption rates after the fact.</summary>
public sealed record LapAggregate(
    int LapNumber,
    double MinFuel,
    double MaxFuel,
    double MinWearFl,
    double MaxWearFl,
    double MinWearFr,
    double MaxWearFr,
    double MinWearRl,
    double MaxWearRl,
    double MinWearRr,
    double MaxWearRr);

public readonly record struct PositionPoint(double SessionTime, int Position, int LapNumber);

public readonly record struct WheelValues(double Fl, double Fr, double Rl, double Rr)
{
    public double Average => (Fl + Fr + Rl + Rr) / 4.0;

    public double this[int index] => index switch { 0 => Fl, 1 => Fr, 2 => Rl, 3 => Rr, _ => throw new ArgumentOutOfRangeException(nameof(index)) };

    public static readonly string[] Names = ["Front Left", "Front Right", "Rear Left", "Rear Right"];
    public static readonly string[] ShortNames = ["FL", "FR", "RL", "RR"];
}
