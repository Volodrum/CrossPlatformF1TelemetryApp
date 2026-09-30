using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using F1Telemetry.Simulation;

namespace F1Telemetry.Ingest;

/// <summary>A raw datagram with its arrival time relative to the start of the stream.</summary>
public readonly record struct RawPacket(TimeSpan Timestamp, byte[] Data);

public interface IPacketSource
{
    string Name { get; }

    IAsyncEnumerable<RawPacket> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>Live game telemetry on a UDP port (default 20777).</summary>
public sealed class UdpPacketSource(int port = UdpPacketSource.DefaultPort, IPAddress? bindAddress = null) : IPacketSource
{
    public const int DefaultPort = 20777;

    public string Name => $"UDP :{port}";

    public async IAsyncEnumerable<RawPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = new UdpClient(new IPEndPoint(bindAddress ?? IPAddress.Any, port));
        client.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        var clock = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue; // ICMP port-unreachable noise on Windows
            }

            yield return new RawPacket(clock.Elapsed, result.Buffer);
        }
    }
}

/// <summary>Replays a <c>.f1rec</c> capture, preserving original timing (scaled by <paramref name="speed"/>).</summary>
public sealed class ReplayPacketSource(string path, double speed = 1.0, bool loop = false) : IPacketSource
{
    public string Name => $"Replay {Path.GetFileName(path)} x{speed:0.##}";

    public async IAsyncEnumerable<RawPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        do
        {
            var pacer = new Pacer(speed);
            await using var reader = PacketFileReader.Open(path);
            await foreach (var packet in reader.ReadAllAsync(cancellationToken))
            {
                await pacer.WaitUntilAsync(packet.Timestamp, cancellationToken);
                yield return packet;
            }
        }
        while (loop && !cancellationToken.IsCancellationRequested);
    }
}

/// <summary>In-process simulated session (demo mode / development without the game).</summary>
public sealed class SimulatorPacketSource(Func<SimulationOptions> options, double speed = 1.0, bool loop = true) : IPacketSource
{
    public string Name => $"Simulator x{speed:0.##}";

    public async IAsyncEnumerable<RawPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        do
        {
            var pacer = new Pacer(speed);
            var simulator = new SessionSimulator(options());
            foreach (var datagram in simulator.Generate(cancellationToken))
            {
                await pacer.WaitUntilAsync(datagram.Time, cancellationToken);
                yield return new RawPacket(datagram.Time, datagram.Data);
            }
        }
        while (loop && !cancellationToken.IsCancellationRequested);
    }
}

/// <summary>Real-time pacing for recorded / generated streams. speed &lt;= 0 means "as fast as possible".</summary>
internal sealed class Pacer(double speed)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _origin;

    public async ValueTask WaitUntilAsync(TimeSpan timestamp, CancellationToken cancellationToken)
    {
        if (speed <= 0)
        {
            return;
        }

        _origin ??= timestamp;
        var target = (timestamp - _origin.Value) / speed;
        var wait = target - _clock.Elapsed;
        if (wait > TimeSpan.FromMilliseconds(2))
        {
            await Task.Delay(wait, cancellationToken);
        }
    }
}
