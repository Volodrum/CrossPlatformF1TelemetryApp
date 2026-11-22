using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using F1Telemetry.Core.Hub;
using F1Telemetry.Core.Setups;
using F1Telemetry.Protocol;

namespace F1Telemetry.Storage;

/// <summary>
/// The track hub: <c>hub_sessions</c>, one row per saved session, its strategy laps, energy sums and setup runs kept as
/// JSON (kilobytes, against the megabytes of its samples).
/// </summary>
public sealed partial class DuckDbTelemetryStore
{
    private static readonly JsonSerializerOptions HubJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private const string HubColumns = """
        id, game_format, track_id, track_name, session_uid, recording_id, session_type, race_length, start_time, saved_at, laps, best_lap_ms,
        strategy, energy, setup_runs, weekend_id
        """;

    public Task<IReadOnlyList<HubSession>> GetHubSessionsAsync(GameFormat? format = null, int? trackId = null, CancellationToken cancellationToken = default) =>
        format is { } f && trackId is { } t
            ? QueryAsync($"SELECT {HubColumns} FROM hub_sessions WHERE game_format = $format AND track_id = $track ORDER BY start_time, id",
                ReadHubSession, cancellationToken, ("format", (int)f), ("track", t))
            : QueryAsync($"SELECT {HubColumns} FROM hub_sessions ORDER BY track_name, start_time, id", ReadHubSession, cancellationToken);

    public async Task<IReadOnlyList<long>> SaveHubSessionsAsync(IReadOnlyList<HubSession> sessions, CancellationToken cancellationToken = default) =>
        (IReadOnlyList<long>)(await RunHubAsync(() => ExecuteSaveHub(sessions), cancellationToken))!;

    public Task RemoveHubSessionsAsync(IReadOnlyList<long> hubIds, CancellationToken cancellationToken = default) =>
        RunHubAsync(() =>
        {
            using var tx = _writer!.BeginTransaction();
            foreach (var id in hubIds)
            {
                NonQuery("DELETE FROM hub_sessions WHERE id = $id", ("id", id));
            }

            tx.Commit();
            return null;
        }, cancellationToken);

    public Task SetHubRaceLengthAsync(long hubId, int raceLength, CancellationToken cancellationToken = default) =>
        RunHubAsync(() =>
        {
            NonQuery("UPDATE hub_sessions SET race_length = $length WHERE id = $id", ("id", hubId), ("length", raceLength));
            return null;
        }, cancellationToken);

    public Task SetHubSessionTypeAsync(long hubId, int sessionType, CancellationToken cancellationToken = default) =>
        RunHubAsync(() =>
        {
            NonQuery("UPDATE hub_sessions SET session_type = $type WHERE id = $id", ("id", hubId), ("type", sessionType));
            return null;
        }, cancellationToken);

    private Task<object?> RunHubAsync(Func<object?> run, CancellationToken cancellationToken)
    {
        var op = new HubOp(run, new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously));
        Enqueue(op);
        return op.Done.Task.WaitAsync(cancellationToken);
    }

    private List<long> ExecuteSaveHub(IReadOnlyList<HubSession> sessions)
    {
        var ids = new List<long>();
        using var tx = _writer!.BeginTransaction();
        foreach (var s in sessions)
        {
            long id;
            using (var cmd = _writer.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT coalesce((SELECT min(id) FROM hub_sessions WHERE recording_id = $rid AND session_uid = $uid),
                                    (SELECT coalesce(max(id), 0) + 1 FROM hub_sessions))
                    """;
                AddParameters(cmd, [("rid", s.RecordingId), ("uid", s.SessionUid)]);
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }

            NonQuery("DELETE FROM hub_sessions WHERE recording_id = $rid AND session_uid = $uid", ("rid", s.RecordingId), ("uid", s.SessionUid));
            NonQuery($"""
                INSERT INTO hub_sessions ({HubColumns})
                VALUES ($id, $format, $track, $trackName, $uid, $rid, $type, $length, $start, $saved, $laps, $best, $strategy, $energy, $runs, $weekend)
                """,
                ("id", id), ("format", (int)s.Format), ("track", s.TrackId), ("trackName", s.TrackName), ("uid", s.SessionUid),
                ("rid", s.RecordingId), ("type", s.SessionType), ("length", s.RaceLength), ("start", s.StartTime.UtcDateTime),
                ("saved", s.SavedAt.UtcDateTime), ("laps", s.Laps), ("best", (long)s.BestLapMs),
                ("strategy", JsonSerializer.Serialize(s.Strategy, HubJson)), ("energy", JsonSerializer.Serialize(s.Energy, HubJson)),
                ("runs", JsonSerializer.Serialize(s.SetupRuns, HubJson)), ("weekend", s.WeekendId));
            ids.Add(id);
        }

        tx.Commit();
        return ids;
    }

    private static HubSession ReadHubSession(DbDataReader r) => new(
        Id: r.GetInt64(0),
        Format: (GameFormat)r.GetInt32(1),
        TrackId: r.GetInt32(2),
        TrackName: r.IsDBNull(3) ? "Unknown" : r.GetString(3),
        SessionUid: r.IsDBNull(4) ? "" : r.GetString(4),
        RecordingId: r.GetInt64(5),
        SessionType: r.IsDBNull(6) ? 0 : r.GetInt32(6),
        RaceLength: r.IsDBNull(7) ? 0 : r.GetInt32(7),
        StartTime: new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc)),
        SavedAt: new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(9), DateTimeKind.Utc)),
        Laps: r.IsDBNull(10) ? 0 : r.GetInt32(10),
        BestLapMs: (uint)GetLong(r, 11),
        Strategy: Json<HubStrategyData>(r, 12) ?? new HubStrategyData([], [], default),
        Energy: Json<HubEnergyData>(r, 13) ?? new HubEnergyData(null, null, null, 0, 0),
        SetupRuns: Json<List<SetupRun>>(r, 14) ?? [])
    {
        WeekendId = GetLong(r, 15),
    };

    private static T? Json<T>(DbDataReader r, int i) where T : class =>
        r.IsDBNull(i) ? null : JsonSerializer.Deserialize<T>(r.GetString(i), HubJson);

    /// <summary>Setup runs kept in the hub for sessions whose recordings are gone.</summary>
    private async Task<IReadOnlyList<SetupRun>> HubRunsWithoutRecordingAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync("""
            SELECT setup_runs FROM hub_sessions h
            WHERE NOT EXISTS (SELECT 1 FROM recordings r WHERE r.id = h.recording_id)
            """, r => Json<List<SetupRun>>(r, 0) ?? [], cancellationToken);
        return [.. rows.SelectMany(runs => runs)];
    }

    private sealed record HubOp(Func<object?> Run, TaskCompletionSource<object?> Done) : WriteOp;
}
