using System.Diagnostics;
using System.Net.Sockets;
using F1Telemetry.Core;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Ingest;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Simulation;
using F1Telemetry.Storage;

// f1tel – developer tooling for the telemetry app. Run without arguments for help.
var cli = new Args(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return cli.Command switch
    {
        "record" => await Record(cli, cts.Token),
        "replay" => await Replay(cli, cts.Token),
        "simulate" => await Simulate(cli, cts.Token),
        "inspect" => await Inspect(cli, cts.Token),
        "seed" => await Seed(cli, cts.Token),
        "reimport" => await Reimport(cli, cts.Token),
        "list" => await List(cli, cts.Token),
        _ => Help(),
    };
}
catch (OperationCanceledException)
{
    return 130;
}

static int Help()
{
    Console.WriteLine("""
        f1tel <command> [options]

          record   --out <file.f1rec> [--port 20777]               Capture raw game UDP packets (Ctrl+C to stop)
          replay   <file.f1rec> [--host 127.0.0.1] [--port 20777] [--speed 1] [--loop]
                                                                   Re-send a capture over UDP with original timing
          simulate [--format 2025|2026] [--laps 6] [--pit 3 | --pits 3,6] [--track Monza] [--speed 1]
                   [--host 127.0.0.1] [--port 20777] [--out file.f1rec]
                                                                   Stream a simulated race over UDP (or write a capture)
          inspect  <file.f1rec>                                    Packet counts, formats, sessions and size errors
          seed     --db <file.duckdb> [--format 2025|2026] [--laps 8] [--pit 4 | --pits 3,6] [--track Monza] [--from <file.f1rec>]
                                                                   Process a simulation/capture offline into a database
          list     --db <file.duckdb>                               Recordings with their laps and setups
          reimport --db <file.duckdb> <capture.f1rec>... [--replace]
                                                                   Rebuild recordings from captures, e.g. after a schema
                                                                   upgrade. --replace deletes the recording a capture was
                                                                   made for (recording-<id>-…) once the new one is in.
                                                                   Close the app first: it locks the database.
        """);
    return 1;
}

static async Task<int> Record(Args cli, CancellationToken ct)
{
    var output = cli.Get("--out") ?? $"capture-{DateTime.Now:yyyyMMdd-HHmmss}{PacketFile.Extension}";
    await using var writer = PacketFileWriter.Create(output);
    var source = new UdpPacketSource(cli.GetInt("--port", UdpPacketSource.DefaultPort));
    Console.WriteLine($"Capturing {source.Name} → {writer.Path} (Ctrl+C to stop)");
    var last = Stopwatch.StartNew();
    try
    {
        await foreach (var packet in source.ReadAllAsync(ct))
        {
            writer.Write(packet);
            if (last.Elapsed > TimeSpan.FromSeconds(2))
            {
                last.Restart();
                Console.Write($"\r{writer.PacketCount:N0} packets");
            }
        }
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine($"\nSaved {writer.PacketCount:N0} packets.");
    return 0;
}

static async Task<int> Replay(Args cli, CancellationToken ct)
{
    var path = cli.Positional ?? throw new ArgumentException("replay needs a file");
    var source = new ReplayPacketSource(path, cli.GetDouble("--speed", 1), cli.Has("--loop"));
    return await Send(source, cli, ct);
}

static async Task<int> Simulate(Args cli, CancellationToken ct)
{
    var options = SimulationFromArgs(cli);
    if (cli.Get("--out") is { } output)
    {
        await using var writer = PacketFileWriter.Create(output);
        foreach (var datagram in new SessionSimulator(options).Generate(ct))
        {
            writer.Write(new RawPacket(datagram.Time, datagram.Data));
        }

        Console.WriteLine($"Wrote {writer.PacketCount:N0} packets ({options.Format}, {options.Laps} laps) to {writer.Path}");
        return 0;
    }

    return await Send(new SimulatorPacketSource(() => options, cli.GetDouble("--speed", 1), loop: cli.Has("--loop")), cli, ct);
}

static async Task<int> Send(IPacketSource source, Args cli, CancellationToken ct)
{
    var host = cli.Get("--host") ?? "127.0.0.1";
    var port = cli.GetInt("--port", UdpPacketSource.DefaultPort);
    using var udp = new UdpClient();
    udp.Connect(host, port);
    Console.WriteLine($"{source.Name} → udp://{host}:{port} (Ctrl+C to stop)");
    long sent = 0;
    var progress = Stopwatch.StartNew();
    try
    {
        await foreach (var packet in source.ReadAllAsync(ct))
        {
            await udp.SendAsync(packet.Data, ct);
            sent++;
            if (progress.Elapsed > TimeSpan.FromSeconds(2))
            {
                progress.Restart();
                Console.Write($"\r{sent:N0} packets, t={packet.Timestamp:mm\\:ss}");
            }
        }
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine($"\nSent {sent:N0} packets.");
    return 0;
}

static async Task<int> Inspect(Args cli, CancellationToken ct)
{
    var path = cli.Positional ?? throw new ArgumentException("inspect needs a file");
    await using var reader = PacketFileReader.Open(path);
    var stats = new PacketStatistics();
    var sessions = new HashSet<ulong>();
    var formats = new Dictionary<ushort, long>();
    TimeSpan last = default;
    await foreach (var packet in reader.ReadAllAsync(ct))
    {
        var result = PacketParser.Parse(packet.Data);
        stats.Record(result);
        if (result.Header is { } h)
        {
            sessions.Add(h.SessionUid);
            formats[h.PacketFormat] = formats.GetValueOrDefault(h.PacketFormat) + 1;
        }

        last = packet.Timestamp;
    }

    Console.WriteLine($"{Path.GetFileName(path)}: {stats.Total:N0} packets over {last:hh\\:mm\\:ss}, {sessions.Count} session(s)");
    foreach (var (format, count) in formats)
    {
        Console.WriteLine($"  format {format}: {count:N0}{(FormatLayout.TryGet(format, out _) ? "" : "  (unsupported)")}");
    }

    foreach (var (id, count) in stats.Snapshot().OrderBy(kv => kv.Key))
    {
        Console.WriteLine($"  {(int)id,2} {id,-20} {count,10:N0}");
    }

    if (stats.RejectedSize + stats.RejectedOther > 0)
    {
        Console.WriteLine($"  rejected: {stats.RejectedSize:N0} wrong size, {stats.RejectedOther:N0} other");
    }

    return 0;
}

static async Task<int> Seed(Args cli, CancellationToken ct)
{
    var dbPath = cli.Get("--db") ?? throw new ArgumentException("seed needs --db <file>");
    await using var store = new DuckDbTelemetryStore(dbPath);
    await store.InitializeAsync(ct);
    IAsyncEnumerable<RawPacket> packets = cli.Get("--from") is { } capture
        ? new ReplayPacketSource(capture, speed: 0).ReadAllAsync(ct)
        : new SimulatorPacketSource(() => SimulationFromArgs(cli), speed: 0, loop: false).ReadAllAsync(ct);

    var sw = Stopwatch.StartNew();
    var (count, _) = await Ingest(store, packets, "Seeded by f1tel", ct);
    var recordings = await store.GetRecordingsAsync(ct);
    Console.WriteLine($"Processed {count:N0} packets in {sw.Elapsed.TotalSeconds:0.0}s → {dbPath} ({recordings.Count} recording(s))");
    return 0;
}

static async Task<int> Reimport(Args cli, CancellationToken ct)
{
    var dbPath = cli.Get("--db") ?? throw new ArgumentException("reimport needs --db <file>");
    var captures = cli.All.Where(a => a.EndsWith(PacketFile.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
    if (captures.Count == 0)
    {
        throw new ArgumentException($"reimport needs at least one {PacketFile.Extension} file");
    }

    await using var store = new DuckDbTelemetryStore(dbPath);
    await store.InitializeAsync(ct);
    foreach (var capture in captures)
    {
        // The app names captures after the recording they were made with: recording-17-20261006-162009.f1rec.
        var name = Path.GetFileNameWithoutExtension(capture);
        var parts = name.Split('-');
        var original = parts is ["recording", var id, ..] && long.TryParse(id, out var originalId)
            ? await store.GetRecordingAsync(originalId, ct)
            : null;

        if (original is null && parts is ["recording", var missing, ..])
        {
            Console.WriteLine($"{Path.GetFileName(capture)}: recording {missing} is not in the database, importing as a new one");
        }

        var sw = Stopwatch.StartNew();
        var (count, created) = await Ingest(store, new ReplayPacketSource(capture, speed: 0).ReadAllAsync(ct), original?.Description ?? $"Re-imported from {name}", ct);
        Console.WriteLine($"{Path.GetFileName(capture)}: {count:N0} packets in {sw.Elapsed.TotalSeconds:0.0}s → recording(s) {string.Join(", ", created)}");
        if (original is not null && created.Count > 0)
        {
            await KeepOriginalTimes(store, original, created, ct);
        }

        if (cli.Has("--replace") && original is not null && created.Count > 0)
        {
            await store.DeleteRecordingAsync(original.Id, ct);
            Console.WriteLine($"  replaced recording {original.Id}");
        }
    }

    return 0;
}

static async Task<int> List(Args cli, CancellationToken ct)
{
    var dbPath = cli.Get("--db") ?? throw new ArgumentException("list needs --db <file>");
    await using var store = new DuckDbTelemetryStore(dbPath);
    await store.InitializeAsync(ct);
    foreach (var r in (await store.GetRecordingsAsync(ct)).OrderBy(r => r.Id))
    {
        var laps = await store.GetLapsAsync(r.Id, ct);
        var setups = await store.GetSetupsAsync(r.Id, ct);
        Console.WriteLine($"{r.Id,4}  {r.StartTime.ToLocalTime():yyyy-MM-dd HH:mm}  {r.TrackName,-12} {SessionTypes.Name(r.SessionType),-20} "
                          + $"{(int)r.Format}  {laps.Count,3} laps  {setups.Count,2} setup(s)  {r.Description}");
    }

    return 0;
}

// A capture replays in seconds, so its recordings would be dated now: move them back to when the original was driven.
static async Task KeepOriginalTimes(ITelemetryStore store, RecordingInfo original, IReadOnlyList<long> created, CancellationToken ct)
{
    var rebuilt = new List<RecordingInfo>();
    foreach (var id in created)
    {
        if (await store.GetRecordingAsync(id, ct) is { } recording)
        {
            rebuilt.Add(recording);
        }
    }

    if (rebuilt.Count == 0)
    {
        return;
    }

    var shift = original.StartTime - rebuilt[0].StartTime;
    foreach (var recording in rebuilt)
    {
        store.UpdateRecording(recording.Id, startTime: recording.StartTime + shift, endTime: recording.EndTime + shift);
    }

    await store.FlushAsync(ct);
}

// Runs packets through the engine and records everything from the first packet that names the track. Returns the
// number of packets processed and the recordings created (more than one when the capture spans several sessions).
static async Task<(long Packets, List<long> Recordings)> Ingest(ITelemetryStore store, IAsyncEnumerable<RawPacket> packets, string description, CancellationToken ct)
{
    var engine = new SessionEngine();
    using var recorder = new RecordingCoordinator(engine, store);
    var created = new List<long>();
    recorder.StateChanged += state =>
    {
        if (state is { Change: RecordingChange.Started or RecordingChange.AutoSplit, RecordingId: { } id })
        {
            created.Add(id);
        }
    };

    long count = 0;
    await foreach (var packet in packets)
    {
        if (PacketParser.Parse(packet.Data) is { IsSuccess: true, Packet: { } parsed })
        {
            engine.Process(parsed);
            count++;
            if (!recorder.IsRecording && engine.Session is { TrackId: >= 0 })
            {
                recorder.Start(description);
            }
        }
    }

    recorder.Stop();
    await store.FlushAsync(ct);
    return (count, created);
}

static SimulationOptions SimulationFromArgs(Args cli)
{
    var trackName = cli.Get("--track") ?? "Monza";
    var track = Tracks.All.FirstOrDefault(t => string.Equals(t.Name, trackName, StringComparison.OrdinalIgnoreCase) || string.Equals(t.MapFile, trackName, StringComparison.OrdinalIgnoreCase))
                ?? Tracks.Get(11);
    var library = new TrackLibrary(Path.Combine(AppContext.BaseDirectory, "track_maps"), Path.Combine(AppContext.BaseDirectory, "tracks"));
    return new SimulationOptions
    {
        Format = cli.GetInt("--format", 2025) == 2026 ? GameFormat.F1_26 : GameFormat.F1_25,
        Track = library.Get(track.MapFile),
        TrackId = track.Id,
        Laps = cli.GetInt("--laps", 6),
        PitOnLap = cli.GetInt("--pit", 3),
        PitLaps = cli.Get("--pits")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray(),
        SessionUid = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
        Seed = Random.Shared.Next(),
    };
}

internal sealed class Args(string[] args)
{
    public string Command => args.Length > 0 ? args[0] : "";
    public IReadOnlyList<string> All => args;
    public string? Positional => args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    public bool Has(string name) => args.Contains(name);

    public string? Get(string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public int GetInt(string name, int fallback) => int.TryParse(Get(name), out var v) ? v : fallback;

    public double GetDouble(string name, double fallback) =>
        double.TryParse(Get(name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
