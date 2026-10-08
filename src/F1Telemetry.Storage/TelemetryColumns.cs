using System.Data.Common;
using DuckDB.NET.Data;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Storage;

/// <summary>
/// Single source of truth for the <c>telemetry</c> table: column name, SQL type, how to append a value
/// from a <see cref="TelemetrySample"/> and how to read it back. Schema DDL, the appender and the reader
/// are all generated from this list, so they cannot drift apart.
/// </summary>
internal sealed class TelemetryColumn
{
    private TelemetryColumn(string name, string sqlType, Action<IDuckDBAppenderRow, TelemetrySample> append, Action<TelemetrySample, DbDataReader, int> read)
    {
        Name = name;
        SqlType = sqlType;
        Append = append;
        Read = read;
    }

    public string Name { get; }
    public string SqlType { get; }
    public Action<IDuckDBAppenderRow, TelemetrySample> Append { get; }
    public Action<TelemetrySample, DbDataReader, int> Read { get; }

    public static TelemetryColumn Long(string name, Func<TelemetrySample, long> get, Action<TelemetrySample, long> set) =>
        new(name, "BIGINT", (row, s) => row.AppendValue(get(s)), (s, r, i) => set(s, r.IsDBNull(i) ? 0 : r.GetInt64(i)));

    public static TelemetryColumn Int(string name, Func<TelemetrySample, int> get, Action<TelemetrySample, int> set) =>
        new(name, "INTEGER", (row, s) => row.AppendValue(get(s)), (s, r, i) => set(s, r.IsDBNull(i) ? 0 : r.GetInt32(i)));

    public static TelemetryColumn Double(string name, Func<TelemetrySample, double> get, Action<TelemetrySample, double> set) =>
        new(name, "DOUBLE", (row, s) => row.AppendValue(get(s)), (s, r, i) => set(s, r.IsDBNull(i) ? 0 : r.GetDouble(i)));
}

internal static class TelemetryColumns
{
    public static readonly TelemetryColumn[] All =
    [
        TelemetryColumn.Long("recording_id", s => s.RecordingId, (s, v) => s.RecordingId = v),
        TelemetryColumn.Int("lap_number", s => s.LapNumber, (s, v) => s.LapNumber = v),
        TelemetryColumn.Double("session_time", s => s.SessionTime, (s, v) => s.SessionTime = v),
        TelemetryColumn.Double("lap_distance", s => s.LapDistance, (s, v) => s.LapDistance = v),
        TelemetryColumn.Int("position", s => s.Position, (s, v) => s.Position = v),

        TelemetryColumn.Int("speed", s => s.Speed, (s, v) => s.Speed = v),
        TelemetryColumn.Double("throttle", s => s.Throttle, (s, v) => s.Throttle = v),
        TelemetryColumn.Double("steer", s => s.Steer, (s, v) => s.Steer = v),
        TelemetryColumn.Double("brake", s => s.Brake, (s, v) => s.Brake = v),
        TelemetryColumn.Int("clutch", s => s.Clutch, (s, v) => s.Clutch = v),
        TelemetryColumn.Int("gear", s => s.Gear, (s, v) => s.Gear = v),
        TelemetryColumn.Int("rpm", s => s.Rpm, (s, v) => s.Rpm = v),
        TelemetryColumn.Int("drs", s => s.Drs, (s, v) => s.Drs = v),

        TelemetryColumn.Double("fuel_in_tank", s => s.FuelInTank, (s, v) => s.FuelInTank = v),
        TelemetryColumn.Double("fuel_remaining_laps", s => s.FuelRemainingLaps, (s, v) => s.FuelRemainingLaps = v),
        TelemetryColumn.Double("ers_store_energy", s => s.ErsStoreEnergy, (s, v) => s.ErsStoreEnergy = v),
        TelemetryColumn.Int("ers_deploy_mode", s => s.ErsDeployMode, (s, v) => s.ErsDeployMode = v),
        TelemetryColumn.Double("ers_harvested_mguk", s => s.ErsHarvestedMguk, (s, v) => s.ErsHarvestedMguk = v),
        TelemetryColumn.Double("ers_harvested_mguh", s => s.ErsHarvestedMguh, (s, v) => s.ErsHarvestedMguh = v),
        TelemetryColumn.Double("ers_deployed", s => s.ErsDeployed, (s, v) => s.ErsDeployed = v),

        TelemetryColumn.Int("brakes_temp_fl", s => s.BrakesTempFl, (s, v) => s.BrakesTempFl = v),
        TelemetryColumn.Int("brakes_temp_fr", s => s.BrakesTempFr, (s, v) => s.BrakesTempFr = v),
        TelemetryColumn.Int("brakes_temp_rl", s => s.BrakesTempRl, (s, v) => s.BrakesTempRl = v),
        TelemetryColumn.Int("brakes_temp_rr", s => s.BrakesTempRr, (s, v) => s.BrakesTempRr = v),
        TelemetryColumn.Int("tyres_surface_temp_fl", s => s.TyresSurfaceTempFl, (s, v) => s.TyresSurfaceTempFl = v),
        TelemetryColumn.Int("tyres_surface_temp_fr", s => s.TyresSurfaceTempFr, (s, v) => s.TyresSurfaceTempFr = v),
        TelemetryColumn.Int("tyres_surface_temp_rl", s => s.TyresSurfaceTempRl, (s, v) => s.TyresSurfaceTempRl = v),
        TelemetryColumn.Int("tyres_surface_temp_rr", s => s.TyresSurfaceTempRr, (s, v) => s.TyresSurfaceTempRr = v),
        TelemetryColumn.Int("tyres_inner_temp_fl", s => s.TyresInnerTempFl, (s, v) => s.TyresInnerTempFl = v),
        TelemetryColumn.Int("tyres_inner_temp_fr", s => s.TyresInnerTempFr, (s, v) => s.TyresInnerTempFr = v),
        TelemetryColumn.Int("tyres_inner_temp_rl", s => s.TyresInnerTempRl, (s, v) => s.TyresInnerTempRl = v),
        TelemetryColumn.Int("tyres_inner_temp_rr", s => s.TyresInnerTempRr, (s, v) => s.TyresInnerTempRr = v),
        TelemetryColumn.Int("engine_temp", s => s.EngineTemp, (s, v) => s.EngineTemp = v),
        TelemetryColumn.Double("tyres_pressure_fl", s => s.TyresPressureFl, (s, v) => s.TyresPressureFl = v),
        TelemetryColumn.Double("tyres_pressure_fr", s => s.TyresPressureFr, (s, v) => s.TyresPressureFr = v),
        TelemetryColumn.Double("tyres_pressure_rl", s => s.TyresPressureRl, (s, v) => s.TyresPressureRl = v),
        TelemetryColumn.Double("tyres_pressure_rr", s => s.TyresPressureRr, (s, v) => s.TyresPressureRr = v),

        TelemetryColumn.Int("actual_compound", s => s.ActualCompound, (s, v) => s.ActualCompound = v),
        TelemetryColumn.Int("visual_compound", s => s.VisualCompound, (s, v) => s.VisualCompound = v),
        TelemetryColumn.Int("tyres_age_laps", s => s.TyresAgeLaps, (s, v) => s.TyresAgeLaps = v),
        TelemetryColumn.Double("tyre_wear_fl", s => s.TyreWearFl, (s, v) => s.TyreWearFl = v),
        TelemetryColumn.Double("tyre_wear_fr", s => s.TyreWearFr, (s, v) => s.TyreWearFr = v),
        TelemetryColumn.Double("tyre_wear_rl", s => s.TyreWearRl, (s, v) => s.TyreWearRl = v),
        TelemetryColumn.Double("tyre_wear_rr", s => s.TyreWearRr, (s, v) => s.TyreWearRr = v),
        TelemetryColumn.Int("tyre_damage_fl", s => s.TyreDamageFl, (s, v) => s.TyreDamageFl = v),
        TelemetryColumn.Int("tyre_damage_fr", s => s.TyreDamageFr, (s, v) => s.TyreDamageFr = v),
        TelemetryColumn.Int("tyre_damage_rl", s => s.TyreDamageRl, (s, v) => s.TyreDamageRl = v),
        TelemetryColumn.Int("tyre_damage_rr", s => s.TyreDamageRr, (s, v) => s.TyreDamageRr = v),
        TelemetryColumn.Int("tyre_blisters_fl", s => s.TyreBlistersFl, (s, v) => s.TyreBlistersFl = v),
        TelemetryColumn.Int("tyre_blisters_fr", s => s.TyreBlistersFr, (s, v) => s.TyreBlistersFr = v),
        TelemetryColumn.Int("tyre_blisters_rl", s => s.TyreBlistersRl, (s, v) => s.TyreBlistersRl = v),
        TelemetryColumn.Int("tyre_blisters_rr", s => s.TyreBlistersRr, (s, v) => s.TyreBlistersRr = v),
        TelemetryColumn.Int("brakes_damage_fl", s => s.BrakesDamageFl, (s, v) => s.BrakesDamageFl = v),
        TelemetryColumn.Int("brakes_damage_fr", s => s.BrakesDamageFr, (s, v) => s.BrakesDamageFr = v),
        TelemetryColumn.Int("brakes_damage_rl", s => s.BrakesDamageRl, (s, v) => s.BrakesDamageRl = v),
        TelemetryColumn.Int("brakes_damage_rr", s => s.BrakesDamageRr, (s, v) => s.BrakesDamageRr = v),

        TelemetryColumn.Int("front_left_wing_damage", s => s.FrontLeftWingDamage, (s, v) => s.FrontLeftWingDamage = v),
        TelemetryColumn.Int("front_right_wing_damage", s => s.FrontRightWingDamage, (s, v) => s.FrontRightWingDamage = v),
        TelemetryColumn.Int("rear_wing_damage", s => s.RearWingDamage, (s, v) => s.RearWingDamage = v),
        TelemetryColumn.Int("floor_damage", s => s.FloorDamage, (s, v) => s.FloorDamage = v),
        TelemetryColumn.Int("diffuser_damage", s => s.DiffuserDamage, (s, v) => s.DiffuserDamage = v),
        TelemetryColumn.Int("sidepod_damage", s => s.SidepodDamage, (s, v) => s.SidepodDamage = v),
        TelemetryColumn.Int("gear_box_damage", s => s.GearBoxDamage, (s, v) => s.GearBoxDamage = v),
        TelemetryColumn.Int("engine_damage", s => s.EngineDamage, (s, v) => s.EngineDamage = v),
        TelemetryColumn.Int("engine_mguh_wear", s => s.EngineMguhWear, (s, v) => s.EngineMguhWear = v),
        TelemetryColumn.Int("engine_es_wear", s => s.EngineEsWear, (s, v) => s.EngineEsWear = v),
        TelemetryColumn.Int("engine_ce_wear", s => s.EngineCeWear, (s, v) => s.EngineCeWear = v),
        TelemetryColumn.Int("engine_ice_wear", s => s.EngineIceWear, (s, v) => s.EngineIceWear = v),
        TelemetryColumn.Int("engine_mguk_wear", s => s.EngineMgukWear, (s, v) => s.EngineMgukWear = v),
        TelemetryColumn.Int("engine_tc_wear", s => s.EngineTcWear, (s, v) => s.EngineTcWear = v),

        TelemetryColumn.Double("world_pos_x", s => s.WorldPosX, (s, v) => s.WorldPosX = v),
        TelemetryColumn.Double("world_pos_y", s => s.WorldPosY, (s, v) => s.WorldPosY = v),
        TelemetryColumn.Double("world_pos_z", s => s.WorldPosZ, (s, v) => s.WorldPosZ = v),
        TelemetryColumn.Double("g_force_lat", s => s.GForceLat, (s, v) => s.GForceLat = v),
        TelemetryColumn.Double("g_force_lon", s => s.GForceLon, (s, v) => s.GForceLon = v),
        TelemetryColumn.Double("g_force_vert", s => s.GForceVert, (s, v) => s.GForceVert = v),

        TelemetryColumn.Int("active_aero_mode", s => s.ActiveAeroMode, (s, v) => s.ActiveAeroMode = v),
        TelemetryColumn.Int("overtake_active", s => s.OvertakeActive, (s, v) => s.OvertakeActive = v),

        // v3. Older databases get these by ALTER TABLE, which appends: keep them last, in this order (the appender
        // writes by position).
        TelemetryColumn.Double("engine_power_ice", s => s.EnginePowerIce, (s, v) => s.EnginePowerIce = v),
        TelemetryColumn.Double("engine_power_mguk", s => s.EnginePowerMguk, (s, v) => s.EnginePowerMguk = v),
        TelemetryColumn.Double("ers_harvest_limit", s => s.ErsHarvestLimit, (s, v) => s.ErsHarvestLimit = v),
        TelemetryColumn.Int("active_aero_available", s => s.ActiveAeroAvailable, (s, v) => s.ActiveAeroAvailable = v),
        TelemetryColumn.Int("overtake_available", s => s.OvertakeAvailable, (s, v) => s.OvertakeAvailable = v),
    ];

    public static string SelectList { get; } = string.Join(", ", All.Select(c => c.Name));

    /// <summary>Columns added after v1, with the migration that adds them to an existing table.</summary>
    public static IEnumerable<string> AddColumnsSql(params string[] names) =>
        names.Select(name => All.Single(c => c.Name == name)).Select(c => $"ALTER TABLE telemetry ADD COLUMN IF NOT EXISTS {c.Name} {c.SqlType}");

    public static string CreateTableSql { get; } =
        $"CREATE TABLE IF NOT EXISTS telemetry (\n    {string.Join(",\n    ", All.Select(c => $"{c.Name} {c.SqlType}"))}\n)";

    public static TelemetrySample ReadRow(DbDataReader reader)
    {
        var sample = new TelemetrySample();
        for (var i = 0; i < All.Length; i++)
        {
            All[i].Read(sample, reader, i);
        }

        return sample;
    }
}
