using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Models;

/// <summary>Identity and configuration of the session currently being received.</summary>
public sealed record SessionInfo(
    ulong SessionUid,
    GameFormat Format,
    int TrackId,
    string TrackName,
    int SessionType,
    string SessionTypeName,
    string FormulaName,
    int TotalLaps,
    byte PlayerCarIndex);

/// <summary>Weather and track status (drives the track-conditions overlay).</summary>
public sealed record TrackConditions(
    string TrackName,
    byte Weather,
    int TrackTemperature,
    int AirTemperature,
    int TotalLaps,
    byte SafetyCarStatus,
    IReadOnlyList<WeatherForecastSample> Forecast);

public enum DeltaColor
{
    Neutral,
    PersonalBest,
    SessionBest,
    Slower,
}

/// <summary>Sector/lap delta against the reference lap, emitted when a sector completes.</summary>
public sealed record DeltaUpdate(int SectorNumber, int LapDeltaMs, DeltaColor LapColor, int SectorDeltaMs, DeltaColor SectorColor);

/// <summary>Timing board for the lap-timing overlay (current lap first).</summary>
public sealed record LapTimingSnapshot(IReadOnlyList<LapRecord> RecentLaps, LapRecord? SessionBest);

public enum RadarSeverity
{
    Far,
    Near,
    Danger,
}

/// <param name="RelativeX">Metres to the player's right (negative = left).</param>
/// <param name="RelativeZ">Metres ahead of the player (negative = behind).</param>
public readonly record struct RadarBlip(int CarIndex, float RelativeX, float RelativeZ, float RelativeYawDegrees, float Distance, RadarSeverity Severity);

public sealed record RadarFrame(IReadOnlyList<RadarBlip> Cars, bool LeftDanger, bool RightDanger, float Range)
{
    public bool AnyCarClose => Cars.Count > 0;
}

/// <summary>Live strategy numbers for the player (drives the strategy part of the track overlay).</summary>
public sealed record StrategySnapshot(
    string Compound,
    int TyresAgeLaps,
    WheelValues TyreWear,
    string HighestWearTyre,
    double HighestWear,
    double? TyreLapsRemaining,
    double FuelInTank,
    double? FuelLapsRemaining,
    double GameFuelRemainingLaps,
    double? AverageFuelPerLap,
    PitStatus PitStatus,
    DriverStatus DriverStatus,
    bool CurrentLapClean,
    bool CurrentLapInvalidated);

/// <summary>Player pedal inputs at one car-telemetry tick (0–1 each).</summary>
public readonly record struct InputSample(double SessionTime, float Throttle, float Brake);
