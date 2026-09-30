using System.Diagnostics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Tests;

public class InputHistoryTests
{
    private sealed class FakeClock
    {
        public double Now { get; set; } = 1000;
    }

    private static (InputHistory History, FakeClock Clock) Create()
    {
        var clock = new FakeClock();
        return (new InputHistory(() => clock.Now), clock);
    }

    private static (double[] T, float[] Throttle, float[] Brake) Buffers() =>
        (new double[InputHistory.BufferSize], new float[InputHistory.BufferSize], new float[InputHistory.BufferSize]);

    /// <summary>Feeds 60 Hz samples, each arriving with the given latency pattern.</summary>
    private static void Feed(InputHistory history, FakeClock clock, double fromSession, double seconds, Func<int, double>? latency = null)
    {
        var n = (int)(seconds * 60);
        for (var i = 0; i < n; i++)
        {
            var session = fromSession + i / 60.0;
            clock.Now = 1000 + session + 0.010 + (latency?.Invoke(i) ?? 0);
            history.Add(new InputSample(session, i % 2 == 0 ? 1f : 0.5f, 0f));
        }
    }

    [Fact]
    public void Window_returns_only_recent_samples_relative_to_now_plus_one_lead_in()
    {
        var (history, clock) = Create();
        Feed(history, clock, 0, 10);
        var (t, th, br) = Buffers();

        var count = history.CopyWindow(6, t, th, br);

        // 6 s at 60 Hz, plus the one sample just before the window so the line reaches the left edge.
        Assert.InRange(count, 360, 362);
        Assert.True(t[0] <= -6 + 1e-9);
        Assert.True(t[1] > -6 - 1e-9);
        Assert.InRange(t[count - 1], -0.001, 0);
        Assert.True(t.Take(count).Zip(t.Skip(1).Take(count - 1)).All(p => p.First < p.Second));
    }

    [Fact]
    public void Now_advances_with_local_clock_between_packets_for_smooth_scrolling()
    {
        var (history, clock) = Create();
        Feed(history, clock, 0, 2);
        var (t, th, br) = Buffers();
        history.CopyWindow(6, t, th, br);
        var count = history.CopyWindow(6, t, th, br);
        var newestBefore = t[count - 1];

        clock.Now += 0.008; // half a packet interval later, no new packet
        count = history.CopyWindow(6, t, th, br);

        Assert.Equal(newestBefore - 0.008, t[count - 1], 3);
    }

    [Fact]
    public void Jittery_late_packets_do_not_shift_the_timeline()
    {
        var (history, clock) = Create();
        // Every third packet is 12 ms late: the mapping should stick to the on-time (minimum latency) ones.
        Feed(history, clock, 0, 3, i => i % 3 == 0 ? 0.012 : 0);
        var (t, th, br) = Buffers();

        clock.Now = 1000 + (3 - 1 / 60.0) + 0.010; // on-time arrival of the last sample
        var count = history.CopyWindow(6, t, th, br);

        Assert.InRange(t[count - 1], -0.002, 0);
    }

    [Fact]
    public void Trace_freezes_shortly_after_packets_stop()
    {
        var (history, clock) = Create();
        Feed(history, clock, 0, 2);
        var (t, th, br) = Buffers();

        clock.Now += 5; // game paused
        var count = history.CopyWindow(6, t, th, br);

        Assert.InRange(t[count - 1], -0.1001, -0.099);
    }

    [Fact]
    public void Flashback_clears_the_history()
    {
        var (history, clock) = Create();
        Feed(history, clock, 100, 3);
        Feed(history, clock, 50, 1); // session time jumped back
        var (t, th, br) = Buffers();

        var count = history.CopyWindow(30, t, th, br);

        Assert.InRange(count, 59, 60);
    }

    [Fact]
    public void Wraps_around_the_ring_buffer_without_losing_order()
    {
        var (history, clock) = Create();
        Feed(history, clock, 0, 200); // 12 000 samples > capacity
        var (t, th, br) = Buffers();

        var count = history.CopyWindow(30, t, th, br);

        Assert.InRange(count, 1800, 1802);
        Assert.True(t.Take(count).Zip(t.Skip(1).Take(count - 1)).All(p => p.First < p.Second));
    }

    [Fact]
    public void Add_and_copy_are_fast_enough_for_every_frame()
    {
        var history = new InputHistory();
        var (t, th, br) = Buffers();
        for (var i = 0; i < 20_000; i++)
        {
            history.Add(new InputSample(i / 120.0, 1, 0));
        }

        var sw = Stopwatch.StartNew();
        for (var frame = 0; frame < 1000; frame++)
        {
            history.CopyWindow(30, t, th, br);
        }

        // A 30 s window at 120 Hz copied 1000 times; one frame's copy must be far below a 144 Hz frame (6.9 ms).
        Assert.True(sw.Elapsed.TotalMilliseconds / 1000 < 0.5, $"copy took {sw.Elapsed.TotalMilliseconds / 1000:0.000} ms");
    }
}
