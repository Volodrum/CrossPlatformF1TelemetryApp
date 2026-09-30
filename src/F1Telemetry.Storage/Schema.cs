using DuckDB.NET.Data;

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
