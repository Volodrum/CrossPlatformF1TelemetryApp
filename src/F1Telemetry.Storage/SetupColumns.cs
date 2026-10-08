using System.Data.Common;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Storage;

/// <summary>
/// The setup settings as columns, shared by two tables: <c>recording_setups</c>, one row per setup the player drove in a
/// recording (from the lap it arrived on), and <c>setups</c>, the library. DDL, inserts, reads and the "same setup"
/// match are generated from <see cref="Settings"/>, like <see cref="TelemetryColumns"/>.
/// </summary>
internal static class SetupColumns
{
    private sealed record Setting(string Name, string SqlType, Func<CarSetup, object> Get);

    private static readonly Setting[] Settings =
    [
        new("front_wing", "INTEGER", s => (int)s.FrontWing),
        new("rear_wing", "INTEGER", s => (int)s.RearWing),
        new("on_throttle", "INTEGER", s => (int)s.OnThrottle),
        new("off_throttle", "INTEGER", s => (int)s.OffThrottle),
        new("front_camber", "DOUBLE", s => (double)s.FrontCamber),
        new("rear_camber", "DOUBLE", s => (double)s.RearCamber),
        new("front_toe", "DOUBLE", s => (double)s.FrontToe),
        new("rear_toe", "DOUBLE", s => (double)s.RearToe),
        new("front_suspension", "INTEGER", s => (int)s.FrontSuspension),
        new("rear_suspension", "INTEGER", s => (int)s.RearSuspension),
        new("front_anti_roll_bar", "INTEGER", s => (int)s.FrontAntiRollBar),
        new("rear_anti_roll_bar", "INTEGER", s => (int)s.RearAntiRollBar),
        new("front_ride_height", "INTEGER", s => (int)s.FrontSuspensionHeight),
        new("rear_ride_height", "INTEGER", s => (int)s.RearSuspensionHeight),
        new("brake_pressure", "INTEGER", s => (int)s.BrakePressure),
        new("brake_bias", "INTEGER", s => (int)s.BrakeBias),
        new("engine_braking", "INTEGER", s => (int)s.EngineBraking),
        new("tyre_pressure_fl", "DOUBLE", s => (double)s.TyresPressure.FrontLeft),
        new("tyre_pressure_fr", "DOUBLE", s => (double)s.TyresPressure.FrontRight),
        new("tyre_pressure_rl", "DOUBLE", s => (double)s.TyresPressure.RearLeft),
        new("tyre_pressure_rr", "DOUBLE", s => (double)s.TyresPressure.RearRight),
        new("ballast", "INTEGER", s => (int)s.Ballast),
        new("fuel_load", "DOUBLE", s => (double)s.FuelLoad),
    ];

    /// <summary>Every setting column, comma separated, optionally qualified with a table alias.</summary>
    public static string List(string? alias = null) => string.Join(", ", Settings.Select(s => alias is null ? s.Name : $"{alias}.{s.Name}"));

    /// <summary>The settings that make two setups the same: all but fuel load, which follows the fuel in the tank.</summary>
    public static string SameSetup(string a, string b) =>
        string.Join(" AND ", Settings.Where(s => s.Name != "fuel_load").Select(s => $"{a}.{s.Name} = {b}.{s.Name}"));

    /// <summary><see cref="SameSetup"/> against the <see cref="SettingArgs"/> parameters.</summary>
    public static string SameAsParameters(string alias) =>
        string.Join(" AND ", Settings.Where(s => s.Name != "fuel_load").Select(s => $"{alias}.{s.Name} = ${s.Name}"));

    public static string GroupBySettings(string alias) =>
        string.Join(", ", Settings.Where(s => s.Name != "fuel_load").Select(s => $"{alias}.{s.Name}"));

    private static string Ddl => string.Join(",\n    ", Settings.Select(s => $"{s.Name} {s.SqlType}"));

    // ------------------------------------------------------------ recording_setups

    public static string CreateTableSql { get; } =
        $"CREATE TABLE IF NOT EXISTS recording_setups (\n    recording_id BIGINT,\n    lap_number INTEGER,\n    session_time DOUBLE,\n    {Ddl},\n"
        + "    weather INTEGER,\n    track_temp INTEGER,\n    air_temp INTEGER\n)";

    /// <summary>v4: older databases get the conditions columns.</summary>
    public static string[] AddConditionsSql { get; } =
    [
        "ALTER TABLE recording_setups ADD COLUMN IF NOT EXISTS weather INTEGER",
        "ALTER TABLE recording_setups ADD COLUMN IF NOT EXISTS track_temp INTEGER",
        "ALTER TABLE recording_setups ADD COLUMN IF NOT EXISTS air_temp INTEGER",
    ];

    public static string InsertSql { get; } =
        $"INSERT INTO recording_setups (recording_id, lap_number, session_time, {List()}, weather, track_temp, air_temp) "
        + $"VALUES ($recording_id, $lap_number, $session_time, {string.Join(", ", Settings.Select(s => "$" + s.Name))}, $weather, $track_temp, $air_temp)";

    public static string SelectSql { get; } =
        $"SELECT lap_number, session_time, {List()}, weather, track_temp, air_temp FROM recording_setups WHERE recording_id = $id ORDER BY session_time";

    public static (string Name, object? Value)[] InsertArgs(long recordingId, SetupChange change) =>
    [
        ("recording_id", recordingId),
        ("lap_number", change.LapNumber),
        ("session_time", change.SessionTime),
        .. SettingArgs(change.Setup),
        ("weather", change.Conditions is { } c ? (int)c.Weather : null),
        ("track_temp", change.Conditions is { } t ? (int)t.TrackTemperature : null),
        ("air_temp", change.Conditions is { } a ? (int)a.AirTemperature : null),
    ];

    /// <summary>Reads a row of <see cref="SelectSql"/>.</summary>
    public static SetupChange ReadRow(DbDataReader r)
    {
        var setup = ReadSetup(r, 2);
        var conditions = ReadConditions(r, 2 + Settings.Length);
        return new SetupChange(r.GetInt32(0), r.GetDouble(1), setup, conditions);
    }

    /// <summary>Weather, track and air temperature from three columns starting at <paramref name="first"/>; null when unknown.</summary>
    public static SessionConditions? ReadConditions(DbDataReader r, int first) =>
        r.IsDBNull(first) ? null : new SessionConditions((byte)r.GetInt32(first), (sbyte)r.GetInt32(first + 1), (sbyte)r.GetInt32(first + 2));

    // ------------------------------------------------------------ setups (the library)

    public static string CreateLibrarySql { get; } =
        "CREATE TABLE IF NOT EXISTS setups (\n    id BIGINT PRIMARY KEY,\n    game_format INTEGER,\n    track_id INTEGER,\n    track_name VARCHAR,\n"
        + "    name VARCHAR,\n    notes VARCHAR,\n    favourite BOOLEAN,\n    hidden BOOLEAN,\n    source VARCHAR,\n    created_at TIMESTAMP,\n"
        + $"    {Ddl}\n)";

    public static (string Name, object? Value)[] SettingArgs(CarSetup setup) => [.. Settings.Select(s => (s.Name, (object?)s.Get(setup)))];

    public static string SettingParameters => string.Join(", ", Settings.Select(s => "$" + s.Name));

    /// <summary>A setup from the <see cref="Settings"/> columns starting at <paramref name="first"/>.</summary>
    public static CarSetup ReadSetup(DbDataReader r, int first)
    {
        var i = first;
        byte U8() => (byte)r.GetInt32(i++);
        float F32() => (float)(r.IsDBNull(i++) ? 0 : r.GetDouble(i - 1));

        var frontWing = U8();
        var rearWing = U8();
        var onThrottle = U8();
        var offThrottle = U8();
        var frontCamber = F32();
        var rearCamber = F32();
        var frontToe = F32();
        var rearToe = F32();
        var frontSuspension = U8();
        var rearSuspension = U8();
        var frontArb = U8();
        var rearArb = U8();
        var frontHeight = U8();
        var rearHeight = U8();
        var brakePressure = U8();
        var brakeBias = U8();
        var engineBraking = U8();
        float fl = F32(), fr = F32(), rl = F32(), rr = F32();
        return new CarSetup(frontWing, rearWing, onThrottle, offThrottle, frontCamber, rearCamber, frontToe, rearToe,
            frontSuspension, rearSuspension, frontArb, rearArb, frontHeight, rearHeight, brakePressure, brakeBias, engineBraking,
            new Tyres<float>(rl, rr, fl, fr), U8(), F32());
    }
}
