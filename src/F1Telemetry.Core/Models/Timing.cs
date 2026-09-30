using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Models;

/// <summary>Live timing of one car, merged from the lap data, car status, participants and session history packets.</summary>
public sealed record CarTiming
{
    public required int CarIndex { get; init; }
    public int Position { get; init; }

    /// <summary>Participant name as sent by the game (surname for AI drivers).</summary>
    public string Name { get; init; } = "";

    /// <summary>Three-letter code for the tower, e.g. "VER".</summary>
    public string Code { get; init; } = "";

    /// <summary>Team colour as 0xRRGGBB, or null when unknown.</summary>
    public uint? TeamColour { get; init; }

    public bool IsPlayer { get; init; }
    public bool IsAi { get; init; } = true;

    /// <summary>Human player with telemetry set to "Restricted": status (ERS, fuel) and damage arrive zeroed.</summary>
    public bool TelemetryRestricted { get; init; }

    public uint DeltaToCarInFrontMs { get; init; }
    public uint DeltaToLeaderMs { get; init; }
    public float TotalDistance { get; init; }
    public int CurrentLap { get; init; }
    public PitStatus PitStatus { get; init; }
    public ResultStatus ResultStatus { get; init; }
    public DriverStatus DriverStatus { get; init; }
    public VisualCompound Compound { get; init; }
    public int TyreAgeLaps { get; init; }

    /// <summary>Battery charge 0–100 %, or null when the car's status isn't available.</summary>
    public double? ErsPercent { get; init; }

    /// <summary>Accumulated time penalties in seconds.</summary>
    public int PenaltySeconds { get; init; }

    public int TrackLimitWarnings { get; init; }

    /// <summary>Best lap this session (0 = none yet).</summary>
    public uint BestLapMs { get; init; }
}

/// <summary>All running cars in position order, plus the session context the tower needs.</summary>
public sealed record FieldSnapshot(
    IReadOnlyList<CarTiming> Cars,
    int PlayerCarIndex,
    int SessionType,
    int TotalLaps,
    int SessionTimeLeftSeconds,
    float TrackLength)
{
    public CarTiming? Player => Cars.FirstOrDefault(c => c.IsPlayer);
}

/// <summary>A reference lap for the sector box: its holder, lap time and the splits at the end of sectors 1 and 2.</summary>
public sealed record ReferenceLap(int CarIndex, string Code, uint LapMs, uint Sector1Ms, uint Sector2Ms)
{
    /// <summary>Time at the end of sector <paramref name="sector"/> (1 = S1, 2 = S1+S2, 3 = lap).</summary>
    public uint SplitAt(int sector) => sector switch
    {
        1 => Sector1Ms,
        2 => Sector1Ms + Sector2Ms,
        _ => LapMs,
    };
}

/// <summary>What a sector bar shows.</summary>
public enum SectorMark
{
    Pending,
    Live,
    SessionBest,
    PersonalBest,
    Slower,
    Invalid,
}

/// <summary>
/// The last finished sector: its own time and the cumulative gap at that split to the session-best and
/// personal-best laps (null when there is no such reference yet).
/// </summary>
public sealed record SectorSplit(int Sector, uint SectorMs, int? DeltaToSessionBestMs, int? DeltaToPersonalBestMs);

/// <summary>State of the qualifying-style sector box for the player's current (or just finished) lap.</summary>
public sealed record SectorBoxSnapshot
{
    public int Position { get; init; }
    public uint LapTimeMs { get; init; }

    /// <summary>A completed lap being held on screen (4 s after the line).</summary>
    public bool IsFinished { get; init; }

    public bool IsInvalid { get; init; }

    /// <summary>Out lap, in lap, garage or pit lane: sectors stay empty.</summary>
    public bool IsOutOrInLap { get; init; }

    public IReadOnlyList<SectorMark> Sectors { get; init; } = [SectorMark.Pending, SectorMark.Pending, SectorMark.Pending];

    /// <summary>0–1 progress through the live sector.</summary>
    public double LiveProgress { get; init; }

    /// <summary>The sector just finished, for 4 s after its split (then null until the next one).</summary>
    public SectorSplit? LastSplit { get; init; }
    public bool LapIsSessionBest { get; init; }
    public bool LapIsPersonalBest { get; init; }
    public ReferenceLap? SessionBest { get; init; }
    public ReferenceLap? PersonalBest { get; init; }
}

/// <summary>What the sector box's gap chips compare against.</summary>
public enum SectorReference
{
    SessionBest,
    PersonalBest,
}
