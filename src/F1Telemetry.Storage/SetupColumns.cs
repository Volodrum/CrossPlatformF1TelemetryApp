using System.Data.Common;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Storage;

/// <summary>
/// The <c>recording_setups</c> table: one row per setup the player drove in a recording, from the lap it arrived on.
/// DDL, insert and read are generated from <see cref="Setting"/>s, like <see cref="TelemetryColumns"/>.
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

    private static readonly string[] Keys = ["recording_id", "lap_number", "session_time"];

    public static string CreateTableSql { get; } =
        "CREATE TABLE IF NOT EXISTS recording_setups (\n    recording_id BIGINT,\n    lap_number INTEGER,\n    session_time DOUBLE,\n    "
        + string.Join(",\n    ", Settings.Select(s => $"{s.Name} {s.SqlType}")) + "\n)";

    public static string InsertSql { get; } =
        $"INSERT INTO recording_setups ({string.Join(", ", Keys.Concat(Settings.Select(s => s.Name)))}) "
        + $"VALUES ({string.Join(", ", Keys.Concat(Settings.Select(s => s.Name)).Select(name => "$" + name))})";

    public static string SelectSql { get; } =
        $"SELECT lap_number, session_time, {string.Join(", ", Settings.Select(s => s.Name))} FROM recording_setups "
        + "WHERE recording_id = $id ORDER BY session_time";

    public static (string Name, object? Value)[] InsertArgs(long recordingId, SetupChange change) =>
    [
        ("recording_id", recordingId),
        ("lap_number", change.LapNumber),
        ("session_time", change.SessionTime),
        .. Settings.Select(s => (s.Name, (object?)s.Get(change.Setup))),
    ];

    /// <summary>Reads a row of <see cref="SelectSql"/>.</summary>
    public static SetupChange ReadRow(DbDataReader r)
    {
        var i = 2;
        byte U8() => (byte)r.GetInt32(i++);
        float F32() => (float)r.GetDouble(i++);

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
        var setup = new CarSetup(frontWing, rearWing, onThrottle, offThrottle, frontCamber, rearCamber, frontToe, rearToe,
            frontSuspension, rearSuspension, frontArb, rearArb, frontHeight, rearHeight, brakePressure, brakeBias, engineBraking,
            new Tyres<float>(rl, rr, fl, fr), U8(), F32());
        return new SetupChange(r.GetInt32(0), r.GetDouble(1), setup);
    }
}
