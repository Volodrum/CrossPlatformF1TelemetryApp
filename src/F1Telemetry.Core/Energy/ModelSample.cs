namespace F1Telemetry.Core.Energy;

/// <summary>
/// The part of a telemetry sample the ERS car model learns from, kept small: a model reads every sample recorded at a
/// track.
/// </summary>
/// <param name="Harvested">Per-lap MGU-K harvest counter, J.</param>
/// <param name="Deployed">Per-lap deploy counter, J.</param>
/// <param name="IcePower">Combustion engine output, W; 0 in recordings made before it was recorded.</param>
/// <param name="GForceLongitudinal">In g, positive when accelerating.</param>
/// <param name="Aero">Active aero mode (2026 format) or DRS open (2025 format).</param>
/// <param name="Override">The 2026 overtake override was on: near a car ahead, the MGU-K gives far more at high speed.</param>
/// <param name="GForceLateral">In g.</param>
/// <param name="HarvestLimit">The lap's MGU-K harvest limit, J; 0 when the format doesn't report one (2025).</param>
public readonly record struct ModelSample(
    long RecordingId,
    int LapNumber,
    double SessionTime,
    float LapDistance,
    float Speed,
    float Throttle,
    float Brake,
    float Steer,
    float ErsStore,
    byte Mode,
    float Harvested,
    float Deployed,
    float IcePower,
    float MgukPower,
    float GForceLongitudinal,
    float Fuel,
    byte Aero,
    bool Override = false,
    float GForceLateral = 0,
    float HarvestLimit = 0);
