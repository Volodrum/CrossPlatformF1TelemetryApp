using System.Diagnostics;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Fixed-size ring buffer of recent pedal inputs, written by the ingest thread on every car-telemetry packet and
/// read by the input overlay on every display frame. No allocations after construction.
/// <para>
/// Samples are placed on the game's session clock, so their spacing is exact whatever the network jitter. To
/// scroll smoothly between packets (60 Hz data on a 144 Hz display), the session clock is mapped to the local
/// monotonic clock using the smallest observed arrival latency; the reader asks for "now" on that mapped clock.
/// </para>
/// </summary>
public sealed class InputHistory
{
    /// <summary>Longest window the buffer is sized for (at up to 120 Hz, with headroom).</summary>
    public const double MaxWindowSeconds = 30;

    private const int Capacity = 8192;

    // Latency may creep up by this much per sample (clock drift); a later packet snaps it back down.
    private const double OffsetCreep = 0.0005;

    // Arrival offset jumps larger than this mean a pause/resume: re-anchor immediately.
    private const double OffsetResync = 0.25;

    // When packets stop (pause, menus) the trace freezes this long after the newest sample instead of scrolling empty.
    private const double MaxExtrapolation = 0.1;

    private readonly Func<double> _clock;
    private readonly Lock _gate = new();
    private readonly double[] _time = new double[Capacity];
    private readonly float[] _throttle = new float[Capacity];
    private readonly float[] _brake = new float[Capacity];
    private long _written;
    private double _offset;
    private double _lastLocal = double.NegativeInfinity;

    public InputHistory(Func<double>? clock = null) =>
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);

    /// <summary>Incremented on every write; lets the reader skip frames when nothing changed.</summary>
    public long Version => Volatile.Read(ref _written);

    /// <summary>Local clock (seconds) of the newest sample.</summary>
    public double LastArrival
    {
        get
        {
            lock (_gate)
            {
                return _lastLocal;
            }
        }
    }

    public double Now() => _clock();

    public void Add(InputSample sample)
    {
        var local = _clock();
        lock (_gate)
        {
            var offset = local - sample.SessionTime;
            if (_written > 0)
            {
                var previous = _time[(_written - 1) % Capacity];

                // Flashback, restart or a new session: the old trace no longer lines up.
                if (sample.SessionTime < previous || sample.SessionTime - previous > 5)
                {
                    _written = 0;
                }
            }

            _offset = _written == 0 || Math.Abs(offset - _offset) > OffsetResync
                ? offset
                : Math.Min(offset, _offset + OffsetCreep);

            var i = _written % Capacity;
            _time[i] = sample.SessionTime;
            _throttle[i] = sample.Throttle;
            _brake[i] = sample.Brake;
            _lastLocal = local;
            Volatile.Write(ref _written, _written + 1);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _written = 0;
            _lastLocal = double.NegativeInfinity;
        }
    }

    /// <summary>
    /// Copies the samples of the last <paramref name="windowSeconds"/> (oldest first, plus one just before the window
    /// so the line reaches the left edge) into the caller's buffers, which must hold <see cref="BufferSize"/> items.
    /// Times are returned relative to "now" (≤ 0, seconds).
    /// </summary>
    /// <returns>The number of samples copied.</returns>
    public int CopyWindow(double windowSeconds, double[] time, float[] throttle, float[] brake)
    {
        var localNow = _clock();
        lock (_gate)
        {
            if (_written == 0)
            {
                return 0;
            }

            var newest = _time[(_written - 1) % Capacity];
            var now = Math.Min(localNow - _offset, newest + MaxExtrapolation);
            now = Math.Max(now, newest);
            var from = now - windowSeconds;

            var available = (int)Math.Min(_written, Capacity);
            var start = _written - available;
            var first = _written - 1;
            while (first > start && _time[first % Capacity] > from)
            {
                first--;
            }

            var count = (int)(_written - first);
            for (var k = 0; k < count; k++)
            {
                var i = (first + k) % Capacity;
                time[k] = _time[i] - now;
                throttle[k] = _throttle[i];
                brake[k] = _brake[i];
            }

            return count;
        }
    }

    public static int BufferSize => Capacity;
}
