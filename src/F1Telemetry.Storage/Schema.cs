using DuckDB.NET.Data;
using F1Telemetry.Core.Recording;

namespace F1Telemetry.Storage;

/// <summary>Versioned schema migrations. Each step runs once, in order, inside a transaction.</summary>
internal static class Schema
{
    private static readonly string[][] Migrations =
    [
        // v1: initial schema
        [
            """
            CREATE TABLE IF NOT EXISTS recordings (
                id BIGINT PRIMARY KEY,
                session_uid VARCHAR,
                description VARCHAR,
                track_id INTEGER,
                track_name VARCHAR,
                session_type INTEGER,
                game_format INTEGER,
                start_time TIMESTAMP,
                end_time TIMESTAMP
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS laps (
                recording_id BIGINT,
                lap_number INTEGER,
                lap_time_ms BIGINT,
                s1_ms BIGINT,
                s2_ms BIGINT,
                s3_ms BIGINT,
                is_valid BOOLEAN,
                is_best_lap BOOLEAN,
                is_best_s1 BOOLEAN,
                is_best_s2 BOOLEAN,
                is_best_s3 BOOLEAN,
                compound VARCHAR,
                actual_compound VARCHAR,
                stint_index INTEGER,
                car_position INTEGER,
                lap_type VARCHAR,
                PRIMARY KEY (recording_id, lap_number)
            )
            """,
            TelemetryColumns.CreateTableSql,
        ],

        // v2: pit-lane visit timing per lap (pit-stop loss)
        [
            "ALTER TABLE laps ADD COLUMN IF NOT EXISTS pit_lane_ms BIGINT",
            "ALTER TABLE laps ADD COLUMN IF NOT EXISTS pit_stop_ms BIGINT",
        ],

        // v3: ERS power and 2026 availability flags per sample; the player's setups per recording
        [
            .. TelemetryColumns.AddColumnsSql("engine_power_ice", "engine_power_mguk", "ers_harvest_limit", "active_aero_available", "overtake_available"),
            SetupColumns.CreateTableSql,
        ],

        // v4: weather and temperatures with each recorded setup; the setup library
        [
            .. SetupColumns.AddConditionsSql,
            SetupColumns.CreateLibrarySql,
        ],

        // v5: outside races, driving that belongs to no lap moves to lap 0 (see LapRestarts): everything a lap number
        // carried before its last line crossing (an abandoned lap, the in-lap, the garage, the out-lap), and the first
        // out-lap before the line. Races (session types 15–17) are left as they are.
        [
            $"""
            UPDATE telemetry SET lap_number = 0
            FROM (
                SELECT c.recording_id, c.lap_number, max(c.session_time) AS restart
                FROM (
                    SELECT recording_id, lap_number, session_time, lap_distance,
                           lag(lap_distance) OVER (PARTITION BY recording_id, lap_number ORDER BY session_time) AS previous
                    FROM telemetry WHERE lap_number >= 1
                ) c
                JOIN recordings r ON r.id = c.recording_id
                WHERE r.session_type NOT BETWEEN 15 AND 17 AND c.previous - c.lap_distance > {LapRestarts.LineCrossing}
                GROUP BY c.recording_id, c.lap_number
            ) x
            WHERE telemetry.recording_id = x.recording_id AND telemetry.lap_number = x.lap_number AND telemetry.session_time < x.restart
            """,
            """
            UPDATE telemetry SET lap_number = 0
            WHERE lap_number >= 1 AND lap_distance < 0
              AND recording_id IN (SELECT id FROM recordings WHERE session_type NOT BETWEEN 15 AND 17)
            """,
        ],
    ];

    public static int LatestVersion => Migrations.Length;

    public static void Migrate(DuckDBConnection connection)
    {
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL)");
        var current = Convert.ToInt32(Scalar(connection, "SELECT coalesce(max(version), 0) FROM schema_info"));

        for (var version = current + 1; version <= Migrations.Length; version++)
        {
            using var tx = connection.BeginTransaction();
            foreach (var sql in Migrations[version - 1])
            {
                Execute(connection, sql);
            }

            Execute(connection, $"INSERT INTO schema_info VALUES ({version})");
            tx.Commit();
        }

        Execute(connection, "CHECKPOINT");
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
