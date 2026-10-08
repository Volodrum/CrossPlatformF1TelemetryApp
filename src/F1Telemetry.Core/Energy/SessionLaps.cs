using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Energy;

/// <summary>Which laps of a session the energy analysis is built on.</summary>
public static class SessionLaps
{
    /// <summary>A lap slower than the session's median by more than this share is not a typical lap (traffic, a mistake, lap 1).</summary>
    public const double Spread = 0.03;

    /// <summary>
    /// The session's typical laps, the ones a race plan should hold for: timed, not a pit, safety car or virtual safety car
    /// lap, and within <see cref="Spread"/> of the median lap time. In lap order.
    /// </summary>
    public static IReadOnlyList<LapRecord> Representative(IEnumerable<LapRecord> laps)
    {
        var timed = laps.Where(l => l.HasTime && l.LapType == LapType.Regular).ToList();
        if (timed.Count == 0)
        {
            return [];
        }

        var times = timed.Select(l => (double)l.LapTimeMs).Order().ToList();
        var median = times.Count % 2 == 1 ? times[times.Count / 2] : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
        return [.. timed.Where(l => l.LapTimeMs <= median * (1 + Spread)).OrderBy(l => l.LapNumber)];
    }

    /// <summary>The session's fastest timed regular lap: the reference for a qualifying lap. Null when none is timed.</summary>
    public static LapRecord? Fastest(IEnumerable<LapRecord> laps) =>
        laps.Where(l => l.HasTime && l.LapType == LapType.Regular).OrderBy(l => l.LapTimeMs).FirstOrDefault();

    /// <summary>
    /// How <paramref name="lapNumbers"/> were driven, every <paramref name="step"/> m along the lap: the average battery
    /// level and the deploy mode most of them ran there. Needs only the battery samples, not engine power. Null when
    /// none of the laps has samples.
    /// </summary>
    public static TypicalLap? Typical(IReadOnlyList<TelemetrySample> samples, IReadOnlyCollection<int> lapNumbers, double step = LapProfile.Step)
    {
        var laps = samples.Where(s => s.LapDistance >= 0 && lapNumbers.Contains(s.LapNumber))
            .GroupBy(s => s.LapNumber)
            .Select(g => g.OrderBy(s => s.LapDistance).ToList())
            .Where(l => l.Count > 1)
            .ToList();
        if (laps.Count == 0)
        {
            return null;
        }

        var points = (int)Math.Floor(laps.Min(l => l[^1].LapDistance) / step) + 1;
        var level = new double[points];
        var mode = new int[points];
        var at = new int[laps.Count];
        for (var i = 0; i < points; i++)
        {
            var d = i * step;
            double sum = 0;
            var votes = new Dictionary<int, int>();
            for (var k = 0; k < laps.Count; k++)
            {
                var lap = laps[k];
                while (at[k] < lap.Count - 1 && lap[at[k]].LapDistance < d)
                {
                    at[k]++;
                }

                var sample = lap[at[k]];
                sum += sample.ErsStoreEnergy;
                votes[sample.ErsDeployMode] = votes.GetValueOrDefault(sample.ErsDeployMode) + 1;
            }

            level[i] = sum / laps.Count;
            mode[i] = votes.MaxBy(v => v.Value).Key;
        }

        return new TypicalLap(step, level, mode, laps.Count);
    }
}

/// <summary>
/// A session's laps averaged along the lap: battery <see cref="Level"/> and majority deploy <see cref="Mode"/> at every
/// <see cref="Step"/> m (index i is at i · Step m).
/// </summary>
public sealed record TypicalLap(double Step, double[] Level, int[] Mode, int LapCount)
{
    /// <summary>The index of the point at or just before <paramref name="distance"/> m.</summary>
    public int IndexAt(double distance) => Math.Clamp((int)(distance / Step), 0, Level.Length - 1);

    /// <summary>The lap as runs of one deploy mode.</summary>
    public IEnumerable<ModeRun> Runs()
    {
        var start = 0;
        for (var i = 1; i <= Mode.Length; i++)
        {
            if (i < Mode.Length && Mode[i] == Mode[start])
            {
                continue;
            }

            yield return new ModeRun(Mode[start], start * Step, i * Step);
            start = i;
        }
    }
}
