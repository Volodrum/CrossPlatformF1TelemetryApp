namespace F1Telemetry.Core.Models;

/// <summary>
/// One recorded row: the player's car state at a single car-telemetry tick, enriched with the latest
/// status, damage, lap and motion data. This is the unit persisted at ~60 Hz and plotted on charts.
/// </summary>
public sealed class TelemetrySample
{
    public long RecordingId { get; set; }
    public int LapNumber { get; set; }
    public double SessionTime { get; set; }
    public double LapDistance { get; set; }
    public int Position { get; set; }

    // Driver inputs & dynamics
    public int Speed { get; set; }
    public double Throttle { get; set; }
    public double Steer { get; set; }
    public double Brake { get; set; }
    public int Clutch { get; set; }
    public int Gear { get; set; }
    public int Rpm { get; set; }
    public int Drs { get; set; }

    // Energy
    public double FuelInTank { get; set; }
    public double FuelRemainingLaps { get; set; }
    public double ErsStoreEnergy { get; set; }
    public int ErsDeployMode { get; set; }
    public double ErsHarvestedMguk { get; set; }
    public double ErsHarvestedMguh { get; set; }
    public double ErsDeployed { get; set; }

    /// <summary>Combustion engine output, W.</summary>
    public double EnginePowerIce { get; set; }

    /// <summary>MGU-K output, W: what the battery delivers in the current deploy mode.</summary>
    public double EnginePowerMguk { get; set; }

    /// <summary>Most energy the MGU-K may harvest in one lap, J. 2026 format only (0 in 2025).</summary>
    public double ErsHarvestLimit { get; set; }

    // Temperatures & pressures
    public int BrakesTempFl { get; set; }
    public int BrakesTempFr { get; set; }
    public int BrakesTempRl { get; set; }
    public int BrakesTempRr { get; set; }
    public int TyresSurfaceTempFl { get; set; }
    public int TyresSurfaceTempFr { get; set; }
    public int TyresSurfaceTempRl { get; set; }
    public int TyresSurfaceTempRr { get; set; }
    public int TyresInnerTempFl { get; set; }
    public int TyresInnerTempFr { get; set; }
    public int TyresInnerTempRl { get; set; }
    public int TyresInnerTempRr { get; set; }
    public int EngineTemp { get; set; }
    public double TyresPressureFl { get; set; }
    public double TyresPressureFr { get; set; }
    public double TyresPressureRl { get; set; }
    public double TyresPressureRr { get; set; }

    // Tyres
    public int ActualCompound { get; set; }
    public int VisualCompound { get; set; }
    public int TyresAgeLaps { get; set; }
    public double TyreWearFl { get; set; }
    public double TyreWearFr { get; set; }
    public double TyreWearRl { get; set; }
    public double TyreWearRr { get; set; }
    public int TyreDamageFl { get; set; }
    public int TyreDamageFr { get; set; }
    public int TyreDamageRl { get; set; }
    public int TyreDamageRr { get; set; }
    public int TyreBlistersFl { get; set; }
    public int TyreBlistersFr { get; set; }
    public int TyreBlistersRl { get; set; }
    public int TyreBlistersRr { get; set; }
    public int BrakesDamageFl { get; set; }
    public int BrakesDamageFr { get; set; }
    public int BrakesDamageRl { get; set; }
    public int BrakesDamageRr { get; set; }

    // Car damage & power unit wear
    public int FrontLeftWingDamage { get; set; }
    public int FrontRightWingDamage { get; set; }
    public int RearWingDamage { get; set; }
    public int FloorDamage { get; set; }
    public int DiffuserDamage { get; set; }
    public int SidepodDamage { get; set; }
    public int GearBoxDamage { get; set; }
    public int EngineDamage { get; set; }
    public int EngineMguhWear { get; set; }
    public int EngineEsWear { get; set; }
    public int EngineCeWear { get; set; }
    public int EngineIceWear { get; set; }
    public int EngineMgukWear { get; set; }
    public int EngineTcWear { get; set; }

    // Motion
    public double WorldPosX { get; set; }
    public double WorldPosY { get; set; }
    public double WorldPosZ { get; set; }
    public double GForceLat { get; set; }
    public double GForceLon { get; set; }
    public double GForceVert { get; set; }

    // 2026 Season Pack
    public int ActiveAeroMode { get; set; }
    public int OvertakeActive { get; set; }
    public int ActiveAeroAvailable { get; set; }
    public int OvertakeAvailable { get; set; }

    public double AverageTyreWear => (TyreWearFl + TyreWearFr + TyreWearRl + TyreWearRr) / 4.0;
}
