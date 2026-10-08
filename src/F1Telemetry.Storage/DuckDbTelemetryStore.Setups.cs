using System.Data.Common;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Setups;
using F1Telemetry.Protocol;

namespace F1Telemetry.Storage;

/// <summary>The setup library: <c>setups</c>, filled from <c>recording_setups</c> and from imported files.</summary>
public sealed partial class DuckDbTelemetryStore
{
    // A setup counts as driven when the car moved on it on a timed lap, not just sat in the garage while the player
    // went through saved setups.
    private const int DrivenSpeed = 30;

    private static readonly string SyncLibrarySql = $"""
        INSERT INTO setups (id, game_format, track_id, track_name, name, notes, favourite, hidden, source, created_at, {SetupColumns.List()})
        WITH changes AS (
            SELECT rs.*, lead(rs.session_time) OVER (PARTITION BY rs.recording_id ORDER BY rs.session_time) AS until
            FROM recording_setups rs
        ),
        driven AS (
            SELECT c.* FROM changes c
            WHERE EXISTS (SELECT 1 FROM telemetry t
                          WHERE t.recording_id = c.recording_id AND t.lap_number >= 1 AND t.speed > {DrivenSpeed}
                            AND t.session_time >= c.session_time AND (c.until IS NULL OR t.session_time < c.until))
        ),
        -- first_seen orders the versions: when the setup first went out, not just which recording it was in.
        fresh AS (
            SELECT r.game_format, r.track_id, any_value(r.track_name) AS track_name,
                   min(r.start_time + to_microseconds(CAST(d.session_time * 1000000 AS BIGINT))) AS first_seen,
                   {SetupColumns.GroupBySettings("d")}, any_value(d.fuel_load) AS fuel_load
            FROM driven d JOIN recordings r ON r.id = d.recording_id
            WHERE r.track_id >= 0
            GROUP BY r.game_format, r.track_id, {SetupColumns.GroupBySettings("d")}
        )
        SELECT (SELECT coalesce(max(id), 0) FROM setups) + row_number() OVER (ORDER BY f.first_seen, f.track_id),
               f.game_format, f.track_id, f.track_name, NULL, '', false, false, 'recorded', f.first_seen, {SetupColumns.List("f")}
        FROM fresh f
        WHERE NOT EXISTS (SELECT 1 FROM setups s WHERE s.game_format = f.game_format AND s.track_id = f.track_id AND {SetupColumns.SameSetup("s", "f")})
        """;

    private static readonly string LibrarySql = $"""
        SELECT id, game_format, track_id, track_name, name, notes, favourite, source, created_at, {SetupColumns.List()}
        FROM setups WHERE NOT hidden ORDER BY track_name, created_at, id
        """;

    // Each recorded setup runs from the lap it arrived on to the lap before the next change (or the recording's last lap).
    private static readonly string RunsSql = $"""
        WITH changes AS (
            SELECT rs.*,
                   lead(rs.lap_number) OVER w AS next_lap,
                   lead(rs.session_time) OVER w AS until
            FROM recording_setups rs
            WINDOW w AS (PARTITION BY rs.recording_id ORDER BY rs.session_time)
        ),
        runs AS (
            SELECT s.id AS setup_id, c.recording_id, r.start_time, r.session_type, c.session_time, c.until,
                   greatest(c.lap_number, 1) AS from_lap,
                   coalesce(c.next_lap - 1, (SELECT max(l.lap_number) FROM laps l WHERE l.recording_id = c.recording_id), c.lap_number) AS to_lap,
                   c.weather, c.track_temp, c.air_temp
            FROM changes c
            JOIN recordings r ON r.id = c.recording_id
            JOIN setups s ON s.game_format = r.game_format AND s.track_id = r.track_id AND {SetupColumns.SameSetup("s", "c")}
            WHERE NOT s.hidden
        ),
        measured AS (
            SELECT u.*,
                   (SELECT count(*) FROM laps l
                    WHERE l.recording_id = u.recording_id AND l.lap_number BETWEEN u.from_lap AND u.to_lap AND l.lap_time_ms > 0) AS lap_count,
                   (SELECT coalesce(min(l.lap_time_ms), 0) FROM laps l
                    WHERE l.recording_id = u.recording_id AND l.lap_number BETWEEN u.from_lap AND u.to_lap AND l.lap_time_ms > 0 AND l.is_valid) AS best_ms,
                   (SELECT coalesce(max(t.speed), 0) FROM telemetry t
                    WHERE t.recording_id = u.recording_id AND t.lap_number >= 1
                      AND t.session_time >= u.session_time AND (u.until IS NULL OR t.session_time < u.until)) AS top_speed
            FROM runs u
        )
        SELECT setup_id, recording_id, start_time, session_type, from_lap, to_lap, weather, track_temp, air_temp, lap_count, best_ms, top_speed
        FROM measured
        WHERE top_speed > {DrivenSpeed}
        ORDER BY start_time DESC, session_time
        """;

    public async Task<IReadOnlyList<SetupEntry>> GetSetupLibraryAsync(CancellationToken cancellationToken = default)
    {
        var sync = new SyncLibraryOp(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Enqueue(sync);
        await sync.Done.Task.WaitAsync(cancellationToken);
        return await QueryAsync(LibrarySql, ReadEntry, cancellationToken);
    }

    public Task<IReadOnlyList<SetupRun>> GetSetupRunsAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(RunsSql, r => new SetupRun(
            SetupId: r.GetInt64(0),
            RecordingId: r.GetInt64(1),
            RecordingStart: new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)),
            SessionType: r.IsDBNull(3) ? 0 : r.GetInt32(3),
            FromLap: r.GetInt32(4),
            ToLap: r.GetInt32(5),
            Conditions: SetupColumns.ReadConditions(r, 6),
            Laps: (int)r.GetInt64(9),
            BestLapMs: (uint)GetLong(r, 10),
            TopSpeed: r.GetInt32(11)), cancellationToken);

    public void UpdateSetup(long setupId, string? name = null, string? notes = null, bool? favourite = null, bool? removed = null) =>
        Enqueue(new UpdateSetupOp(setupId, name, notes, favourite, removed));

    public Task<SetupImportResult> ImportSetupsAsync(IReadOnlyList<NewSetup> setups, CancellationToken cancellationToken = default)
    {
        var op = new ImportSetupsOp(setups, new TaskCompletionSource<SetupImportResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        Enqueue(op);
        return op.Done.Task.WaitAsync(cancellationToken);
    }

    private void ExecuteUpdateSetup(UpdateSetupOp u)
    {
        var sets = new List<string>();
        var args = new List<(string, object?)> { ("id", u.Id) };
        if (u.Name is not null)
        {
            sets.Add("name = $name");
            args.Add(("name", string.IsNullOrWhiteSpace(u.Name) ? null : u.Name.Trim()));
        }

        if (u.Notes is not null)
        {
            sets.Add("notes = $notes");
            args.Add(("notes", u.Notes));
        }

        if (u.Favourite is not null)
        {
            sets.Add("favourite = $favourite");
            args.Add(("favourite", u.Favourite));
        }

        if (u.Removed is not null)
        {
            sets.Add("hidden = $hidden");
            args.Add(("hidden", u.Removed));
        }

        if (sets.Count > 0)
        {
            NonQuery($"UPDATE setups SET {string.Join(", ", sets)} WHERE id = $id", [.. args]);
        }
    }

    private SetupImportResult ExecuteImport(IReadOnlyList<NewSetup> setups)
    {
        int added = 0, already = 0;
        using var tx = _writer!.BeginTransaction();
        foreach (var setup in setups)
        {
            (string, object?)[] match = [("format", (int)setup.Format), ("track", setup.TrackId), .. SetupColumns.SettingArgs(setup.Setup)];
            var existing = QueryWriter(
                $"SELECT id, hidden FROM setups WHERE game_format = $format AND track_id = $track AND {SetupColumns.SameAsParameters("setups")} LIMIT 1",
                r => (Id: r.GetInt64(0), Hidden: r.GetBoolean(1)), match).FirstOrDefault();

            if (existing.Id > 0)
            {
                if (existing.Hidden)
                {
                    NonQuery("UPDATE setups SET hidden = false WHERE id = $id", ("id", existing.Id));
                    added++;
                }
                else
                {
                    already++;
                }

                continue;
            }

            NonQuery($"""
                INSERT INTO setups (id, game_format, track_id, track_name, name, notes, favourite, hidden, source, created_at, {SetupColumns.List()})
                SELECT coalesce(max(id), 0) + 1, $format, $track, $trackName, $name, $notes, false, false, 'imported', $created, {SetupColumns.SettingParameters}
                FROM setups
                """,
                [.. match, ("trackName", setup.TrackName), ("name", string.IsNullOrWhiteSpace(setup.Name) ? null : setup.Name.Trim()),
                    ("notes", setup.Notes), ("created", DateTime.UtcNow)]);
            added++;
        }

        tx.Commit();
        return new SetupImportResult(added, already);
    }

    private List<T> QueryWriter<T>(string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = _writer!.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, args);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static SetupEntry ReadEntry(DbDataReader r) => new(
        Id: r.GetInt64(0),
        Format: (GameFormat)r.GetInt32(1),
        TrackId: r.GetInt32(2),
        TrackName: r.IsDBNull(3) ? "Unknown" : r.GetString(3),
        Name: r.IsDBNull(4) ? null : r.GetString(4),
        Notes: r.IsDBNull(5) ? "" : r.GetString(5),
        Favourite: !r.IsDBNull(6) && r.GetBoolean(6),
        Source: !r.IsDBNull(7) && Enum.TryParse<SetupSource>(r.GetString(7), ignoreCase: true, out var source) ? source : SetupSource.Recorded,
        CreatedAt: new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc)),
        Setup: SetupColumns.ReadSetup(r, 9));

    private sealed record SyncLibraryOp(TaskCompletionSource Done) : WriteOp;
    private sealed record UpdateSetupOp(long Id, string? Name, string? Notes, bool? Favourite, bool? Removed) : WriteOp;
    private sealed record ImportSetupsOp(IReadOnlyList<NewSetup> Setups, TaskCompletionSource<SetupImportResult> Done) : WriteOp;
}
