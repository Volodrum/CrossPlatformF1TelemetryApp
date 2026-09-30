using F1Telemetry.Core;
using F1Telemetry.Core.Engine;
using F1Telemetry.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace F1Telemetry.Ingest;

/// <summary>
/// Owns the ingest loop: one packet source at a time → parser → <see cref="SessionEngine"/>, on a dedicated
/// background task so the engine is effectively single-threaded. Optionally tees raw datagrams to an
/// <c>.f1rec</c> capture.
/// </summary>
public sealed class TelemetryPipeline(SessionEngine engine, PacketStatistics statistics, ILogger<TelemetryPipeline>? log = null)
    : IAsyncDisposable
{
    private readonly ILogger _log = log ?? NullLogger<TelemetryPipeline>.Instance;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private PacketFileWriter? _capture;

    private volatile object? _mode;

    public IPacketSource? ActiveSource { get; private set; }

    /// <summary>
    /// Telemetry mode: only packets of this format are processed (null = accept both). Packets of the other
    /// format are counted in <see cref="PacketStatistics.RejectedMode"/> so the UI can suggest switching.
    /// </summary>
    public GameFormat? Mode
    {
        get => (GameFormat?)_mode;
        set => _mode = value;
    }

    public PacketStatistics Statistics => statistics;

    public bool IsCapturingRaw => _capture is not null;

    public event Action<IPacketSource?>? SourceChanged;
    public event Action<Exception>? Faulted;

    public async Task StartAsync(IPacketSource source)
    {
        await _switchGate.WaitAsync();
        try
        {
            await StopCoreAsync();
            var cts = new CancellationTokenSource();
            _cts = cts;
            ActiveSource = source;
            _loop = Task.Factory.StartNew(() => RunAsync(source, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            _log.LogInformation("Ingest started from {Source}", source.Name);
        }
        finally
        {
            _switchGate.Release();
        }

        SourceChanged?.Invoke(source);
    }

    public async Task StopAsync()
    {
        await _switchGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _switchGate.Release();
        }

        SourceChanged?.Invoke(null);
    }

    /// <summary>Starts teeing every received datagram into a raw capture file.</summary>
    public string StartRawCapture(string path)
    {
        var writer = PacketFileWriter.Create(path);
        Interlocked.Exchange(ref _capture, writer)?.Dispose();
        return writer.Path;
    }

    public void StopRawCapture() => Interlocked.Exchange(ref _capture, null)?.Dispose();

    private async Task StopCoreAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();
        try
        {
            if (_loop is not null)
            {
                await _loop;
            }
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
        ActiveSource = null;
    }

    private async Task RunAsync(IPacketSource source, CancellationToken ct)
    {
        var handlerErrors = 0;
        try
        {
            await foreach (var raw in source.ReadAllAsync(ct))
            {
                _capture?.Write(raw);

                var result = PacketParser.Parse(raw.Data);
                statistics.Record(result);
                if (!result.IsSuccess)
                {
                    if (result.Status == ParseStatus.SizeMismatch && statistics.RejectedSize % 500 == 1)
                    {
                        _log.LogWarning("Dropped {Id} packet: {Actual} bytes, expected {Expected} for format {Format}. Game patch or wrong UDP format?",
                            result.Header?.PacketId, raw.Data.Length, result.ExpectedSize, result.Header?.PacketFormat);
                    }

                    continue;
                }

                if (Mode is { } mode && result.Header!.Value.Format != mode)
                {
                    statistics.RecordModeMismatch(result.Header.Value.Format);
                    continue;
                }

                try
                {
                    engine.Process(result.Packet!);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (handlerErrors++ % 1000 == 0)
                    {
                        _log.LogError(ex, "Error processing {Id} packet", result.Header?.PacketId);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Packet source {Source} failed", source.Name);
            Faulted?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        StopRawCapture();
        _switchGate.Dispose();
    }
}
