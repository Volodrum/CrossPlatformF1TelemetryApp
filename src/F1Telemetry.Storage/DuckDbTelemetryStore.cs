using System.Data.Common;
using System.Diagnostics;
using System.Threading.Channels;
using DuckDB.NET.Data;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace F1Telemetry.Storage;

/// <summary>
/// DuckDB-backed <see cref="ITelemetryStore"/>.
/// <para>Writes: every mutation is an op on an ordered channel consumed by one writer task that owns the
/// write connection. Telemetry samples are batched and flushed through a DuckDB appender every 250 ms (or
/// 1000 rows), instead of one flush per row.</para>
/// <para>Reads: a second connection to the same database, serialised by a semaphore, on the thread pool.</para>
/// </summary>
public sealed class DuckDbTelemetryStore(string databasePath, ILogger<DuckDbTelemetryStore>? log = null) : ITelemetryStore
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxBatch = 1000;

    private readonly ILogger _log = log ?? NullLogger<DuckDbTelemetryStore>.Instance;
    private readonly Channel<WriteOp> _queue = Channel.CreateUnbounded<WriteOp>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private DuckDBConnection? _writer;
    private DuckDBConnection? _reader;
    private Task? _writerLoop;
    private long _lastRecordingId;

    public string DatabasePath => databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _writer = OpenWithWalRecovery();
            Schema.Migrate(_writer);
            _reader = new DuckDBConnection($"Data Source={databasePath}"); // DuckDB.NET shares one database instance per path
            _reader.Open();
            _lastRecordingId = Convert.ToInt64(ScalarOn(_writer, "SELECT coalesce(max(id), 0) FROM recordings"));
        }, cancellationToken);

        _writerLoop = Task.Factory.StartNew(WriterLoopAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _log.LogInformation("DuckDB store ready at {Path} (schema v{Version})", databasePath, Schema.LatestVersion);
    }

    private DuckDBConnection OpenWithWalRecovery()
    {
        try
        {
            return Open();
        }
        catch (Exception ex) when (File.Exists(databasePath + ".wal"))
        {
            // A crash can leave a WAL that DuckDB refuses to replay; losing the last few seconds beats not starting.
            _log.LogWarning(ex, "Database open failed; discarding WAL and retrying");
            File.Delete(databasePath + ".wal");
            return Open();
        }

        DuckDBConnection Open()
        {
            var connection = new DuckDBConnection($"Data Source={databasePath}");
            connection.Open();
            return connection;
        }
    }

    // ---------------------------------------------------------------- writes (non-blocking)

    public long BeginRecording(NewRecording recording)
    {
        var id = Interlocked.Increment(ref _lastRecordingId);
        Enqueue(new BeginOp(id, recording, DateTime.UtcNow));
        return id;
    }

    public void EndRecording(long recordingId) => Enqueue(new EndOp(recordingId, DateTime.UtcNow));

    public void UpdateRecording(long recordingId, string? sessionUid = null, int? trackId = null, string? trackName = null, int? sessionType = null) =>
        Enqueue(new UpdateOp(recordingId, sessionUid, trackId, trackName, sessionType));

    public void AppendSample(TelemetrySample sample) => Enqueue(new SampleOp(sample));

    public void UpsertLap(long recordingId, LapRecord lap) => Enqueue(new LapOp(recordingId, lap));

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var op = new FlushOp(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        // After shutdown there is nothing left to flush: late UI callbacks must not crash the app.
        return _queue.Writer.TryWrite(op) ? op.Done.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
    }

    public Task DeleteRecordingAsync(long recordingId, CancellationToken cancellationToken = default)
    {
        var op = new DeleteOp(recordingId, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        Enqueue(op);
        return op.Done.Task.WaitAsync(cancellationToken);
    }

    private void Enqueue(WriteOp op)
    {
        if (!_queue.Writer.TryWrite(op))
        {
            throw new InvalidOperationException("Store is closed.");
        }
    }

    private async Task WriterLoopAsync()
    {
        var pending = new List<TelemetrySample>(MaxBatch);
        var sinceFlush = Stopwatch.StartNew();
        var reader = _queue.Reader;

        while (true)
        {
            bool more;
            using (var timeout = new CancellationTokenSource(FlushInterval))
            {
                try
                {
                    more = await reader.WaitToReadAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    more = true; // timer tick: fall through to the time-based flush
                }
            }

            while (reader.TryRead(out var op))
            {
                if (op is SampleOp sample)
                {
                    pending.Add(sample.Sample);
                    if (pending.Count >= MaxBatch)
                    {
                        FlushSamples(pending, sinceFlush);
                    }

                    continue;
                }

                FlushSamples(pending, sinceFlush);
                Execute(op);
            }

            if (pending.Count > 0 && sinceFlush.Elapsed >= FlushInterval)
            {
                FlushSamples(pending, sinceFlush);
            }

            if (!more)
            {
                FlushSamples(pending, sinceFlush);
                return;
            }
        }
    }

    private void FlushSamples(List<TelemetrySample> pending, Stopwatch sinceFlush)
    {
        sinceFlush.Restart();
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            using var appender = _writer!.CreateAppender("telemetry");
            foreach (var sample in pending)
            {
                var row = appender.CreateRow();
                foreach (var column in TelemetryColumns.All)
                {
                    column.Append(row, sample);
                }

                row.EndRow();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to append {Count} telemetry rows", pending.Count);
        }

        pending.Clear();
    }

    private void Execute(WriteOp op)
    {
        try
        {
            switch (op)
            {
                case BeginOp b:
                    NonQuery("""
                        INSERT INTO recordings (id, session_uid, description, track_id, track_name, session_type, game_format, start_time)
                        VALUES ($id, $uid, $description, $trackId, $trackName, $sessionType, $format, $start)
                        """,
                        ("id", b.Id), ("uid", b.Recording.SessionUid), ("description", b.Recording.Description),
                        ("trackId", b.Recording.TrackId), ("trackName", b.Recording.TrackName),
                        ("sessionType", b.Recording.SessionType), ("format", (int)b.Recording.Format), ("start", b.StartUtc));
                    break;

                case EndOp e:
                    NonQuery("UPDATE recordings SET end_time = $end WHERE id = $id", ("id", e.Id), ("end", e.EndUtc));
                    break;

                case UpdateOp u:
                    ExecuteUpdate(u);
                    break;

                case LapOp l:
                    UpsertLapRow(l.RecordingId, l.Lap);
                    break;

                case FlushOp f:
                    f.Done.TrySetResult();
                    break;

                case DeleteOp d:
                    using (var tx = _writer!.BeginTransaction())
                    {
                        NonQuery("DELETE FROM telemetry WHERE recording_id = $id", ("id", d.Id));
                        NonQuery("DELETE FROM laps WHERE recording_id = $id", ("id", d.Id));
                        NonQuery("DELETE FROM recordings WHERE id = $id", ("id", d.Id));
                        tx.Commit();
                    }

                    d.Done.TrySetResult();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Store write {Op} failed", op.GetType().Name);
            switch (op)
            {
                case DeleteOp d:
                    d.Done.TrySetException(ex);
                    break;
                case FlushOp f:
                    f.Done.TrySetException(ex);
                    break;
            }
        }
    }

    private void ExecuteUpdate(UpdateOp u)
    {
        var sets = new List<string>();
        var args = new List<(string, object?)> { ("id", u.Id) };
        if (u.SessionUid is not null)
        {
            sets.Add("session_uid = $uid");
            args.Add(("uid", u.SessionUid));
        }

        if (u.TrackId is not null)
        {
            sets.Add("track_id = $trackId");
            args.Add(("trackId", u.TrackId));
        }

        if (u.TrackName is not null)
        {
            sets.Add("track_name = $trackName");
            args.Add(("trackName", u.TrackName));
        }

        if (u.SessionType is not null)
        {
            sets.Add("session_type = $sessionType");
            args.Add(("sessionType", u.SessionType));
        }

        if (sets.Count > 0)
        {
            NonQuery($"UPDATE recordings SET {string.Join(", ", sets)} WHERE id = $id", [.. args]);
        }
    }

    private void UpsertLapRow(long recordingId, LapRecord lap) =>
        NonQuery("""
            INSERT INTO laps (recording_id, lap_number, lap_time_ms, s1_ms, s2_ms, s3_ms, is_valid, is_best_lap, is_best_s1,
                              is_best_s2, is_best_s3, compound, actual_compound, stint_index, car_position, lap_type, pit_lane_ms, pit_stop_ms)
            VALUES ($rid, $lap, $time, $s1, $s2, $s3, $valid, $best, $bs1, $bs2, $bs3, $compound, $actual, $stint, $pos, $type, $pitLane, $pitStop)
            ON CONFLICT (recording_id, lap_number) DO UPDATE SET
                lap_time_ms = EXCLUDED.lap_time_ms, s1_ms = EXCLUDED.s1_ms, s2_ms = EXCLUDED.s2_ms, s3_ms = EXCLUDED.s3_ms,
                is_valid = EXCLUDED.is_valid, is_best_lap = EXCLUDED.is_best_lap, is_best_s1 = EXCLUDED.is_best_s1,
                is_best_s2 = EXCLUDED.is_best_s2, is_best_s3 = EXCLUDED.is_best_s3, compound = EXCLUDED.compound,
                actual_compound = EXCLUDED.actual_compound, stint_index = EXCLUDED.stint_index,
                car_position = coalesce(EXCLUDED.car_position, laps.car_position), lap_type = EXCLUDED.lap_type,
                pit_lane_ms = EXCLUDED.pit_lane_ms, pit_stop_ms = EXCLUDED.pit_stop_ms
            """,
            ("rid", recordingId), ("lap", lap.LapNumber), ("time", (long)lap.LapTimeMs), ("s1", (long)lap.Sector1Ms),
            ("s2", (long)lap.Sector2Ms), ("s3", (long)lap.Sector3Ms), ("valid", lap.IsValid), ("best", lap.IsBestLap),
            ("bs1", lap.IsBestSector1), ("bs2", lap.IsBestSector2), ("bs3", lap.IsBestSector3), ("compound", lap.Compound),
            ("actual", lap.ActualCompound), ("stint", lap.StintIndex), ("pos", lap.CarPosition), ("type", lap.LapType.ToString()),
            ("pitLane", (long)lap.PitLaneTimeMs), ("pitStop", (long)lap.PitStopTimeMs));

    private void NonQuery(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _writer!.CreateCommand();
        cmd.CommandText = sql;
        AddParameters(cmd, args);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- reads

    public Task<IReadOnlyList<RecordingInfo>> GetRecordingsAsync(CancellationToken cancellationToken = default) =>
        QueryAsync("""
            SELECT id, session_uid, description, track_id, track_name, session_type, game_format, start_time, end_time
            FROM recordings ORDER BY start_time DESC
            """, ReadRecording, cancellationToken);

    public async Task<RecordingInfo?> GetRecordingAsync(long recordingId, CancellationToken cancellationToken = default) =>
        (await QueryAsync("""
            SELECT id, session_uid, description, track_id, track_name, session_type, game_format, start_time, end_time
            FROM recordings WHERE id = $id
            """, ReadRecording, cancellationToken, ("id", recordingId))).FirstOrDefault();

    public Task<IReadOnlyList<LapRecord>> GetLapsAsync(long recordingId, CancellationToken cancellationToken = default) =>
        QueryAsync("""
            SELECT t.lap_number, l.lap_time_ms, l.s1_ms, l.s2_ms, l.s3_ms, l.is_valid, l.is_best_lap, l.is_best_s1, l.is_best_s2,
                   l.is_best_s3, l.compound, l.actual_compound, l.stint_index, l.car_position, l.lap_type, l.pit_lane_ms, l.pit_stop_ms
            FROM (SELECT DISTINCT lap_number FROM telemetry WHERE recording_id = $id) t
            LEFT JOIN laps l ON l.recording_id = $id AND l.lap_number = t.lap_number
            ORDER BY t.lap_number
            """,
            r => new LapRecord
            {
                LapNumber = r.GetInt32(0),
                LapTimeMs = (uint)GetLong(r, 1),
                Sector1Ms = (uint)GetLong(r, 2),
                Sector2Ms = (uint)GetLong(r, 3),
                Sector3Ms = (uint)GetLong(r, 4),
                IsValid = r.IsDBNull(5) || r.GetBoolean(5),
                IsBestLap = !r.IsDBNull(6) && r.GetBoolean(6),
                IsBestSector1 = !r.IsDBNull(7) && r.GetBoolean(7),
                IsBestSector2 = !r.IsDBNull(8) && r.GetBoolean(8),
                IsBestSector3 = !r.IsDBNull(9) && r.GetBoolean(9),
                Compound = r.IsDBNull(10) ? "Unknown" : r.GetString(10),
                ActualCompound = r.IsDBNull(11) ? "Unknown" : r.GetString(11),
                StintIndex = r.IsDBNull(12) ? -1 : r.GetInt32(12),
                CarPosition = r.IsDBNull(13) ? null : r.GetInt32(13),
                LapType = !r.IsDBNull(14) && Enum.TryParse<LapType>(r.GetString(14), out var type) ? type : LapType.Regular,
                PitLaneTimeMs = (uint)GetLong(r, 15),
                PitStopTimeMs = (uint)GetLong(r, 16),
            },
            cancellationToken, ("id", recordingId));

    public Task<IReadOnlyList<TelemetrySample>> GetLapSamplesAsync(long recordingId, int lapNumber, CancellationToken cancellationToken = default) =>
        QueryAsync($"SELECT {TelemetryColumns.SelectList} FROM telemetry WHERE recording_id = $id AND lap_number = $lap ORDER BY session_time",
            TelemetryColumns.ReadRow, cancellationToken, ("id", recordingId), ("lap", lapNumber));

    public Task<IReadOnlyList<LapAggregate>> GetLapAggregatesAsync(long recordingId, CancellationToken cancellationToken = default) =>
        QueryAsync("""
            SELECT lap_number, min(fuel_in_tank), max(fuel_in_tank),
                   min(tyre_wear_fl), max(tyre_wear_fl), min(tyre_wear_fr), max(tyre_wear_fr),
                   min(tyre_wear_rl), max(tyre_wear_rl), min(tyre_wear_rr), max(tyre_wear_rr)
            FROM telemetry WHERE recording_id = $id GROUP BY lap_number ORDER BY lap_number
            """,
            r => new LapAggregate(r.GetInt32(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5),
                r.GetDouble(6), r.GetDouble(7), r.GetDouble(8), r.GetDouble(9), r.GetDouble(10)),
            cancellationToken, ("id", recordingId));

    public async Task<WheelValues> GetLatestWearAsync(long recordingId, CancellationToken cancellationToken = default) =>
        (await QueryAsync("""
            SELECT tyre_wear_fl, tyre_wear_fr, tyre_wear_rl, tyre_wear_rr FROM telemetry
            WHERE recording_id = $id ORDER BY session_time DESC LIMIT 1
            """,
            r => new WheelValues(r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)),
            cancellationToken, ("id", recordingId))).FirstOrDefault();

    public Task<IReadOnlyList<PositionPoint>> GetPositionHistoryAsync(long recordingId, int maxPoints = 500, CancellationToken cancellationToken = default) =>
        QueryAsync("""
            WITH p AS (
                SELECT session_time, position, lap_number,
                       row_number() OVER (ORDER BY session_time) - 1 AS rn,
                       count(*) OVER () AS total
                FROM telemetry WHERE recording_id = $id AND position > 0
            )
            SELECT session_time, position, lap_number FROM p
            WHERE rn % greatest(1, CAST(ceil(total / $max) AS BIGINT)) = 0
            ORDER BY session_time
            """,
            r => new PositionPoint(r.GetDouble(0), r.GetInt32(1), r.GetInt32(2)),
            cancellationToken, ("id", recordingId), ("max", (double)Math.Max(1, maxPoints)));

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<DbDataReader, T> map, CancellationToken ct, params (string Name, object? Value)[] args)
    {
        await _readGate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                using var cmd = _reader!.CreateCommand();
                cmd.CommandText = sql;
                AddParameters(cmd, args);
                using var reader = cmd.ExecuteReader();
                var rows = new List<T>();
                while (reader.Read())
                {
                    rows.Add(map(reader));
                }

                return (IReadOnlyList<T>)rows;
            }, ct);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private static RecordingInfo ReadRecording(DbDataReader r) => new(
        Id: r.GetInt64(0),
        SessionUid: r.IsDBNull(1) ? "" : r.GetString(1),
        Description: r.IsDBNull(2) ? "" : r.GetString(2),
        TrackId: r.IsDBNull(3) ? -1 : r.GetInt32(3),
        TrackName: r.IsDBNull(4) ? "Unknown" : r.GetString(4),
        SessionType: r.IsDBNull(5) ? 0 : r.GetInt32(5),
        Format: r.IsDBNull(6) ? GameFormat.F1_25 : (GameFormat)r.GetInt32(6),
        StartTime: new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc)),
        EndTime: r.IsDBNull(8) ? null : new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc)));

    private static long GetLong(DbDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetInt64(i);

    private static void AddParameters(DuckDBCommand cmd, (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
        {
            cmd.Parameters.Add(new DuckDBParameter(name, value ?? DBNull.Value));
        }
    }

    private static object? ScalarOn(DuckDBConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        if (_writerLoop is not null)
        {
            await _writerLoop;
        }

        _reader?.Dispose();
        if (_writer is not null)
        {
            try
            {
                ScalarOn(_writer, "CHECKPOINT");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Checkpoint on shutdown failed");
            }

            _writer.Dispose();
        }

        _readGate.Dispose();
    }

    private abstract record WriteOp;
    private sealed record BeginOp(long Id, NewRecording Recording, DateTime StartUtc) : WriteOp;
    private sealed record EndOp(long Id, DateTime EndUtc) : WriteOp;
    private sealed record UpdateOp(long Id, string? SessionUid, int? TrackId, string? TrackName, int? SessionType) : WriteOp;
    private sealed record SampleOp(TelemetrySample Sample) : WriteOp;
    private sealed record LapOp(long RecordingId, LapRecord Lap) : WriteOp;
    private sealed record FlushOp(TaskCompletionSource Done) : WriteOp;
    private sealed record DeleteOp(long Id, TaskCompletionSource Done) : WriteOp;
}
