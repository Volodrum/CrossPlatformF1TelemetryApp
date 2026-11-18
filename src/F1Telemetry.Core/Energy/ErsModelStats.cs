using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Energy;

/// <summary>
/// What an <see cref="ErsCarModel"/> is learned from, kept as sums instead of samples: a few hundred kilobytes for a
/// session whose samples take far more. Stats of different sessions <see cref="Merge"/> into the stats of all of them, so
/// the track hub keeps a session's contribution to the car model after its recording is deleted.
/// <para><see cref="Build"/> gives what <see cref="ErsModelBuilder.Build"/> gives on the same samples, except that a
/// deploy band's output is the median of its outputs rounded to <see cref="PowerResolution"/> W.</para>
/// </summary>
public sealed class ErsModelStats
{
    /// <summary>Deploy-band outputs are counted in steps of this many watts.</summary>
    public const int PowerResolution = 100;

    public int Laps { get; set; }

    public int PowerLaps { get; set; }

    /// <summary>Furthest lap distance of any sample, m; null with no samples.</summary>
    public double? MaxDistance { get; set; }

    public List<DeployBandStats> Bands { get; set; } = [];

    public List<StraightLineStats> StraightLine { get; set; } = [];

    /// <summary>Per <see cref="ErsModelBuilder.DistanceBin"/> m bin: energy harvested and deployed, and the laps that crossed it.</summary>
    public double[] AlongHarvested { get; set; } = [];

    public double[] AlongDeployed { get; set; } = [];

    public int[] AlongLaps { get; set; } = [];

    public List<PositionOutputStats> Outputs { get; set; } = [];

    /// <summary>Harvest flat out (<see cref="PositionRateStats.Mode"/> ≥ 0, the game's clipping) and off it (Mode −1).</summary>
    public List<PositionRateStats> Rates { get; set; } = [];

    /// <summary>Over the position map: battery drawn flat out, J, and the MGU-K output times time behind it, J.</summary>
    public double Drawn { get; set; }

    public double Given { get; set; }

    public List<TractionCellStats> Traction { get; set; } = [];

    public bool IsEmpty => Laps == 0;

    /// <summary>The stats of <paramref name="samples"/>: one or more recordings at a track, by recording, then time.</summary>
    public static ErsModelStats From(GameFormat format, IReadOnlyList<ModelSample> samples)
    {
        var stats = new ErsModelStats
        {
            Laps = samples.Select(s => (s.RecordingId, s.LapNumber)).Distinct().Count(),
            PowerLaps = samples.Where(s => s.IcePower > 0).Select(s => (s.RecordingId, s.LapNumber)).Distinct().Count(),
            MaxDistance = samples.Count == 0 ? null : samples.Max(s => s.LapDistance),
        };

        var steps = ErsModelBuilder.Steps(samples);
        stats.AddBands(samples, steps);
        stats.AddStraightLine(format, samples);
        stats.AddAlongLap(samples);
        stats.AddPositions(samples, steps);
        stats.AddTraction(format, samples, steps);
        return stats;
    }

    /// <summary>The stats of every session in <paramref name="parts"/> together.</summary>
    public static ErsModelStats Merge(IEnumerable<ErsModelStats> parts)
    {
        var all = new ErsModelStats();
        foreach (var part in parts)
        {
            all.Add(part);
        }

        return all;
    }

    private void Add(ErsModelStats other)
    {
        Laps += other.Laps;
        PowerLaps += other.PowerLaps;
        MaxDistance = (MaxDistance, other.MaxDistance) switch
        {
            (null, var b) => b,
            (var a, null) => a,
            var (a, b) => Math.Max(a!.Value, b!.Value),
        };

        var bands = Bands.ToDictionary(b => (b.Mode, b.Band));
        foreach (var band in other.Bands)
        {
            if (!bands.TryGetValue((band.Mode, band.Band), out var mine))
            {
                mine = new DeployBandStats { Mode = band.Mode, Band = band.Band };
                bands[(band.Mode, band.Band)] = mine;
                Bands.Add(mine);
            }

            mine.Samples += band.Samples;
            mine.Steps += band.Steps;
            mine.StepJoules += band.StepJoules;
            mine.StepSeconds += band.StepSeconds;
            foreach (var (power, count) in band.Power)
            {
                mine.Power[power] = mine.Power.GetValueOrDefault(power) + count;
            }
        }

        var lines = StraightLine.ToDictionary(l => l.Aero);
        foreach (var line in other.StraightLine)
        {
            if (!lines.TryGetValue(line.Aero, out var mine))
            {
                mine = new StraightLineStats { Aero = line.Aero };
                lines[line.Aero] = mine;
                StraightLine.Add(mine);
            }

            mine.Add(line);
        }

        AlongHarvested = Sum(AlongHarvested, other.AlongHarvested);
        AlongDeployed = Sum(AlongDeployed, other.AlongDeployed);
        AlongLaps = Sum(AlongLaps, other.AlongLaps);

        var outputs = Outputs.ToDictionary(o => (o.Mode, o.Segment));
        foreach (var o in other.Outputs)
        {
            if (!outputs.TryGetValue((o.Mode, o.Segment), out var mine))
            {
                mine = new PositionOutputStats { Mode = o.Mode, Segment = o.Segment };
                outputs[(o.Mode, o.Segment)] = mine;
                Outputs.Add(mine);
            }

            (mine.Watts, mine.Kmh, mine.Samples, mine.DrawJoules, mine.DrawSeconds) =
                (mine.Watts + o.Watts, mine.Kmh + o.Kmh, mine.Samples + o.Samples, mine.DrawJoules + o.DrawJoules, mine.DrawSeconds + o.DrawSeconds);
        }

        var rates = Rates.ToDictionary(r => (r.Mode, r.Segment));
        foreach (var r in other.Rates)
        {
            if (!rates.TryGetValue((r.Mode, r.Segment), out var mine))
            {
                mine = new PositionRateStats { Mode = r.Mode, Segment = r.Segment };
                rates[(r.Mode, r.Segment)] = mine;
                Rates.Add(mine);
            }

            (mine.Joules, mine.Seconds, mine.Samples) = (mine.Joules + r.Joules, mine.Seconds + r.Seconds, mine.Samples + r.Samples);
        }

        Drawn += other.Drawn;
        Given += other.Given;

        var cells = Traction.ToDictionary(c => (c.Cell, c.HighLoad));
        foreach (var c in other.Traction)
        {
            if (!cells.TryGetValue((c.Cell, c.HighLoad), out var mine))
            {
                mine = new TractionCellStats { Cell = c.Cell, HighLoad = c.HighLoad };
                cells[(c.Cell, c.HighLoad)] = mine;
                Traction.Add(mine);
            }

            mine.Add(c);
        }
    }

    private static double[] Sum(double[] a, double[] b)
    {
        var sum = new double[Math.Max(a.Length, b.Length)];
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        }

        return sum;
    }

    private static int[] Sum(int[] a, int[] b)
    {
        var sum = new int[Math.Max(a.Length, b.Length)];
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] = (i < a.Length ? a[i] : 0) + (i < b.Length ? b[i] : 0);
        }

        return sum;
    }

    // ---------------------------------------------------------------- accumulate

    private void AddBands(IReadOnlyList<ModelSample> samples, ErsModelBuilder.CounterStep?[] steps)
    {
        var bands = new Dictionary<(int, int), DeployBandStats>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s is not { IcePower: > 0, Throttle: >= 0.98f, Brake: <= 0.01f, ErsStore: > ErsModelBuilder.Charged, Override: false })
            {
                continue;
            }

            var key = ((int)s.Mode, (int)s.Speed / ErsModelBuilder.SpeedBand * ErsModelBuilder.SpeedBand);
            if (!bands.TryGetValue(key, out var band))
            {
                band = new DeployBandStats { Mode = key.Item1, Band = key.Item2 };
                bands[key] = band;
                Bands.Add(band);
            }

            band.Samples++;
            var power = (int)Math.Round(s.MgukPower / PowerResolution);
            band.Power[power] = band.Power.GetValueOrDefault(power) + 1;
            if (steps[i] is { Deployed: { } joules } step)
            {
                (band.Steps, band.StepJoules, band.StepSeconds) = (band.Steps + 1, band.StepJoules + joules, band.StepSeconds + step.Seconds);
            }
        }
    }

    private void AddStraightLine(GameFormat format, IReadOnlyList<ModelSample> samples)
    {
        var mass = ErsModelBuilder.CarMass(format);
        var lines = new Dictionary<int, StraightLineStats>();
        foreach (var s in samples)
        {
            if (!(s.IcePower > 0 && s.Throttle >= 0.98f && s.Brake <= 0.01f && s.Speed > 120 && Math.Abs(s.Steer) < 0.05f))
            {
                continue;
            }

            if (!lines.TryGetValue(s.Aero, out var line))
            {
                line = new StraightLineStats { Aero = s.Aero };
                lines[s.Aero] = line;
                StraightLine.Add(line);
            }

            var v = s.Speed / 3.6;
            var m = mass + s.Fuel;
            line.Add([(s.IcePower + s.MgukPower) / (m * v), v * v / m, 1], s.GForceLongitudinal * 9.81);
        }
    }

    private void AddAlongLap(IReadOnlyList<ModelSample> samples)
    {
        var lapLength = samples.Count == 0 ? 0 : samples.Max(s => s.LapDistance);
        if (lapLength <= 0)
        {
            return;
        }

        var bins = (int)(lapLength / ErsModelBuilder.DistanceBin) + 1;
        var harvested = new double[bins];
        var deployed = new double[bins];
        var laps = new int[bins];
        foreach (var lap in samples.GroupBy(s => (s.RecordingId, s.LapNumber)))
        {
            var list = lap.ToList();
            var covered = list.Max(s => s.LapDistance) - Math.Max(0, list.Min(s => s.LapDistance));
            if (covered < ErsModelBuilder.LapCoverage * lapLength)
            {
                continue;
            }

            var seen = new bool[bins];
            for (var i = 1; i < list.Count; i++)
            {
                if (list[i].LapDistance < 0)
                {
                    continue;
                }

                var bin = Math.Min(bins - 1, (int)(list[i].LapDistance / ErsModelBuilder.DistanceBin));
                seen[bin] = true;
                var harvest = list[i].Harvested - list[i - 1].Harvested;
                var deploy = list[i].Deployed - list[i - 1].Deployed;
                if (harvest is >= 0 and <= (float)ErsModelBuilder.MaxCounterStep)
                {
                    harvested[bin] += harvest;
                }

                if (deploy is >= 0 and <= (float)ErsModelBuilder.MaxCounterStep)
                {
                    deployed[bin] += deploy;
                }
            }

            for (var b = 0; b < bins; b++)
            {
                laps[b] += seen[b] ? 1 : 0;
            }
        }

        (AlongHarvested, AlongDeployed, AlongLaps) = (harvested, deployed, laps);
    }

    private void AddPositions(IReadOnlyList<ModelSample> samples, ErsModelBuilder.CounterStep?[] steps)
    {
        const int modes = DeployModes.Overtake + 1;
        var outputs = new Dictionary<(int, int), PositionOutputStats>();
        var rates = new Dictionary<(int, int), PositionRateStats>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.LapDistance < 0 || s.Override)
            {
                continue;
            }

            var segment = (int)(s.LapDistance / LapProfile.Step);
            var mode = Math.Min((int)s.Mode, modes - 1);
            var flat = s.Throttle >= 0.98f && s.Brake <= 0.01f;
            if (flat && s.IcePower > 0 && s.ErsStore > ErsModelBuilder.Charged)
            {
                if (!outputs.TryGetValue((mode, segment), out var cell))
                {
                    cell = new PositionOutputStats { Mode = mode, Segment = segment };
                    outputs[(mode, segment)] = cell;
                    Outputs.Add(cell);
                }

                (cell.Watts, cell.Kmh, cell.Samples) = (cell.Watts + s.MgukPower, cell.Kmh + s.Speed, cell.Samples + 1);
                if (steps[i] is { Deployed: { } joules } step)
                {
                    (cell.DrawJoules, cell.DrawSeconds) = (cell.DrawJoules + joules, cell.DrawSeconds + step.Seconds);
                    (Drawn, Given) = (Drawn + joules, Given + s.MgukPower * step.Seconds);
                }
            }

            var room = s.ErsStore < LapSimulator.Capacity - ErsModelBuilder.HarvestHeadroom
                       && (s.HarvestLimit <= 0 || s.Harvested < s.HarvestLimit - ErsModelBuilder.HarvestHeadroom);
            if (room && steps[i] is { Harvested: { } harvested } h)
            {
                var key = (flat ? mode : -1, segment);
                if (!rates.TryGetValue(key, out var rate))
                {
                    rate = new PositionRateStats { Mode = key.Item1, Segment = segment };
                    rates[key] = rate;
                    Rates.Add(rate);
                }

                (rate.Joules, rate.Seconds, rate.Samples) = (rate.Joules + harvested, rate.Seconds + h.Seconds, rate.Samples + 1);
            }
        }
    }

    private void AddTraction(GameFormat format, IReadOnlyList<ModelSample> samples, ErsModelBuilder.CounterStep?[] steps)
    {
        var mass = ErsModelBuilder.CarMass(format);
        var cells = new Dictionary<(int, bool), TractionCellStats>();
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s is not { IcePower: > 0, Throttle: >= 0.98f, Brake: <= 0.01f, Override: false, Speed: >= 60, LapDistance: >= 0 })
            {
                continue;
            }

            var clip = steps[i] is { Harvested: { } joules } step ? joules / step.Seconds : 0;
            var v = s.Speed / 3.6;
            var key = ((int)(s.LapDistance / 100) * 1000 + (int)(s.Speed / 10), Math.Abs(s.GForceLateral) >= ErsModelBuilder.TractionLoads[1]);
            if (!cells.TryGetValue(key, out var cell))
            {
                cell = new TractionCellStats { Cell = key.Item1, HighLoad = key.Item2 };
                cells[key] = cell;
                Traction.Add(cell);
            }

            cell.Add((s.MgukPower - clip) / ((mass + s.Fuel) * v), s.GForceLongitudinal * 9.81);
        }
    }

    // ---------------------------------------------------------------- build

    public ErsCarModel Build(GameFormat format, int trackId) =>
        new(format, trackId, Laps, PowerLaps, BuildDeployMap(), BuildStraightLine(), BuildAlongLap(), BuildPositionMap(), BuildTraction());

    private DeployMap BuildDeployMap()
    {
        var bands = Bands
            .Where(b => b.Samples >= ErsModelBuilder.MinBandSamples)
            .Select(b =>
            {
                double? draw = b.Steps >= ErsModelBuilder.MinBandSamples && b.StepSeconds > 0 ? b.StepJoules / b.StepSeconds : null;
                return (b.Mode, Point: new DeployPoint(b.Band, b.MedianPower(), b.Samples, draw));
            })
            .ToList();

        var measured = bands.Where(b => b.Point.DrawW is not null && b.Point.PowerW > ErsModelBuilder.MinPeakW).ToList();
        var perWatt = measured.Count == 0 ? 1 : measured.Sum(b => b.Point.DrawW!.Value * b.Point.Samples) / measured.Sum(b => b.Point.PowerW * b.Point.Samples);

        return new DeployMap([.. bands
            .GroupBy(b => b.Mode)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var points = g.Select(b => b.Point.DrawW is null ? b.Point with { DrawW = b.Point.PowerW * perWatt } : b.Point).OrderBy(p => p.SpeedKmh).ToList();
                return new DeployCurve(g.Key, points, g.Key == DeployModes.None ? null : ErsModelBuilder.FadeFrom(points));
            })
            .Where(c => c.PeakW >= ErsModelBuilder.MinPeakW)]);
    }

    private List<StraightLineFit> BuildStraightLine()
    {
        var fits = new List<StraightLineFit>();
        foreach (var line in StraightLine.OrderBy(l => l.Aero))
        {
            if (line.Samples < ErsModelBuilder.MinFitSamples || line.Fit() is not { } fit)
            {
                continue;
            }

            fits.Add(fit);
        }

        return fits;
    }

    private List<EnergyBin> BuildAlongLap() =>
    [
        .. Enumerable.Range(0, AlongLaps.Length)
            .Where(b => AlongLaps[b] > 0)
            .Select(b => new EnergyBin(b * ErsModelBuilder.DistanceBin, AlongHarvested[b] / AlongLaps[b], AlongDeployed[b] / AlongLaps[b], AlongLaps[b])),
    ];

    private PositionMap BuildPositionMap()
    {
        const int modes = DeployModes.Overtake + 1;
        if (MaxDistance is not { } length || length < 0)
        {
            return PositionMap.Empty;
        }

        var count = (int)(length / LapProfile.Step) + 1;
        var perWatt = Given > 0 ? Drawn / Given : 1;
        var deploy = new DeployCell?[modes][];
        var clipping = new HarvestRate?[modes][];
        for (var m = 0; m < modes; m++)
        {
            deploy[m] = new DeployCell?[count];
            clipping[m] = new HarvestRate?[count];
        }

        var lift = new HarvestRate?[count];
        foreach (var c in Outputs.Where(c => c.Samples > 0 && c.Segment < count))
        {
            var watts = c.Watts / c.Samples;
            deploy[c.Mode][c.Segment] = new DeployCell(watts, c.DrawSeconds >= 0.2 ? c.DrawJoules / c.DrawSeconds : watts * perWatt, c.Kmh / c.Samples, c.Samples);
        }

        foreach (var r in Rates.Where(r => r.Seconds > 0 && r.Segment < count))
        {
            var rate = new HarvestRate(r.Joules / r.Seconds, r.Samples);
            if (r.Mode < 0)
            {
                lift[r.Segment] = rate;
            }
            else
            {
                clipping[r.Mode][r.Segment] = rate;
            }
        }

        return new PositionMap(deploy, clipping, lift);
    }

    private TractionMap BuildTraction()
    {
        var reference = Slope(Traction.Where(c => c.Kmh >= TractionMap.ReferenceKmh));
        if (reference is not { Samples: >= ErsModelBuilder.MinTractionSamples, Value: > 0.1 } r)
        {
            return TractionMap.Unlimited;
        }

        var speeds = ErsModelBuilder.TractionSpeeds;
        var loads = ErsModelBuilder.TractionLoads;
        var bands = new List<TractionBand>();
        for (var b = 0; b + 1 < speeds.Length; b++)
        {
            var (from, to) = (speeds[b], speeds[b + 1]);
            var inBand = Traction.Where(c => c.Kmh >= from && c.Kmh < to).ToList();
            Add(inBand, from, to, null, null);
            for (var g = 0; g + 1 < loads.Length; g++)
            {
                var high = g > 0;
                Add(inBand.Where(c => c.HighLoad == high), from, to, loads[g], loads[g + 1]);
            }
        }

        return new TractionMap(bands);

        void Add(IEnumerable<TractionCellStats> band, int from, int to, double? low, double? high)
        {
            if (Slope(band) is { Samples: >= ErsModelBuilder.MinTractionSamples } s && s.Error <= ErsModelBuilder.MaxTractionError * r.Value)
            {
                bands.Add(new TractionBand(from, to, low, high, Math.Clamp(s.Value / r.Value, 0.05, 1), s.Samples));
            }
        }
    }

    /// <summary>
    /// As <c>ErsModelBuilder.Slope</c>: the least-squares slope of Y on X within each cell (both less the cell's mean),
    /// with its standard error, from the cells' sums.
    /// </summary>
    private static (double Value, double Error, int Samples)? Slope(IEnumerable<TractionCellStats> cells)
    {
        double sxy = 0, sxx = 0, syy = 0;
        var count = 0;
        foreach (var cell in cells.GroupBy(c => c.Cell))
        {
            var n = cell.Sum(c => c.N);
            if (n < 2)
            {
                continue;
            }

            var (sx, sy) = (cell.Sum(c => c.Sx), cell.Sum(c => c.Sy));
            sxx += cell.Sum(c => c.Sxx) - sx * sx / n;
            sxy += cell.Sum(c => c.Sxy) - sx * sy / n;
            syy += cell.Sum(c => c.Syy) - sy * sy / n;
            count += n;
        }

        if (count < 3 || sxx <= 0)
        {
            return null;
        }

        var slope = sxy / sxx;
        var residual = Math.Max(0, syy - 2 * slope * sxy + slope * slope * sxx);
        return (slope, Math.Sqrt(residual / (count - 2) / sxx), count);
    }
}

/// <summary>Flat-out samples of one deploy mode in one <see cref="ErsModelBuilder.SpeedBand"/> km/h band.</summary>
public sealed class DeployBandStats
{
    public int Mode { get; set; }

    /// <summary>The band's lowest speed, km/h.</summary>
    public int Band { get; set; }

    public int Samples { get; set; }

    /// <summary>MGU-K outputs counted by output ÷ <see cref="ErsModelStats.PowerResolution"/>, rounded.</summary>
    public Dictionary<int, int> Power { get; set; } = [];

    /// <summary>Deploy-counter steps in the band: how many, what they paid (J) and over how long (s).</summary>
    public int Steps { get; set; }

    public double StepJoules { get; set; }

    public double StepSeconds { get; set; }

    /// <summary>The median output, W (the mean of the middle two for an even count).</summary>
    public double MedianPower()
    {
        var total = Power.Values.Sum();
        if (total == 0)
        {
            return 0;
        }

        double? At(int index)
        {
            var seen = 0;
            foreach (var (power, count) in Power.OrderBy(p => p.Key))
            {
                seen += count;
                if (index < seen)
                {
                    return power * (double)ErsModelStats.PowerResolution;
                }
            }

            return null;
        }

        return total % 2 == 1 ? At(total / 2)!.Value : (At(total / 2 - 1)!.Value + At(total / 2)!.Value) / 2;
    }
}

/// <summary>
/// The normal equations of the straight-line fit a = η·P/(m v) + β·v²/m + γ for one aero mode: X'X, X'y, y'y, Σy.
/// </summary>
public sealed class StraightLineStats
{
    public int Aero { get; set; }

    public int Samples { get; set; }

    /// <summary>X'X, row by row (3 × 3).</summary>
    public double[] Xx { get; set; } = new double[9];

    public double[] Xy { get; set; } = new double[3];

    public double Yy { get; set; }

    public double Y { get; set; }

    internal void Add(double[] x, double y)
    {
        Samples++;
        for (var r = 0; r < 3; r++)
        {
            Xy[r] += x[r] * y;
            for (var c = 0; c < 3; c++)
            {
                Xx[r * 3 + c] += x[r] * x[c];
            }
        }

        (Yy, Y) = (Yy + y * y, Y + y);
    }

    internal void Add(StraightLineStats other)
    {
        Samples += other.Samples;
        for (var i = 0; i < 9; i++)
        {
            Xx[i] += other.Xx[i];
        }

        for (var i = 0; i < 3; i++)
        {
            Xy[i] += other.Xy[i];
        }

        (Yy, Y) = (Yy + other.Yy, Y + other.Y);
    }

    internal StraightLineFit? Fit()
    {
        var a = new double[3, 4];
        for (var r = 0; r < 3; r++)
        {
            a[r, 3] = Xy[r];
            for (var c = 0; c < 3; c++)
            {
                a[r, c] = Xx[r * 3 + c];
            }
        }

        if (ErsModelBuilder.Solve(a) is not { } c3)
        {
            return null;
        }

        // Σ(y − ŷ)² = y'y − 2c'X'y + c'X'Xc.
        var fitted = 0.0;
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                fitted += c3[r] * Xx[r * 3 + c] * c3[c];
            }
        }

        var residual = Math.Max(0, Yy - 2 * (c3[0] * Xy[0] + c3[1] * Xy[1] + c3[2] * Xy[2]) + fitted);
        var total = Yy - Y * Y / Samples;
        return new StraightLineFit(Aero, c3[0], -c3[1], -c3[2], total > 0 ? 1 - residual / total : 0, Math.Sqrt(residual / Samples), Samples);
    }
}

/// <summary>Flat-out output, speed and battery draw of one deploy mode on one <see cref="LapProfile.Step"/> m segment.</summary>
public sealed class PositionOutputStats
{
    public int Mode { get; set; }

    public int Segment { get; set; }

    public double Watts { get; set; }

    public double Kmh { get; set; }

    public int Samples { get; set; }

    public double DrawJoules { get; set; }

    public double DrawSeconds { get; set; }
}

/// <summary>Harvest on one segment: flat out in a mode (the game's clipping) or, with <see cref="Mode"/> −1, off full throttle.</summary>
public sealed class PositionRateStats
{
    public int Mode { get; set; }

    public int Segment { get; set; }

    public double Joules { get; set; }

    public double Seconds { get; set; }

    public int Samples { get; set; }
}

/// <summary>
/// Flat-out samples at one place (100 m) and speed (10 km/h), split by lateral load: the sums the traction slopes are
/// fitted from (X = output ÷ (m · v), Y = acceleration).
/// </summary>
public sealed class TractionCellStats
{
    /// <summary>Place × 1000 + speed band, as <c>ErsModelBuilder.FitTraction</c> keys them.</summary>
    public int Cell { get; set; }

    /// <summary>The lateral load was at or above the high-load band's start.</summary>
    public bool HighLoad { get; set; }

    public int N { get; set; }

    public double Sx { get; set; }

    public double Sy { get; set; }

    public double Sxx { get; set; }

    public double Sxy { get; set; }

    public double Syy { get; set; }

    /// <summary>The cell's speed band's lowest speed, km/h.</summary>
    public int Kmh => Cell % 1000 * 10;

    internal void Add(double x, double y)
    {
        N++;
        (Sx, Sy, Sxx, Sxy, Syy) = (Sx + x, Sy + y, Sxx + x * x, Sxy + x * y, Syy + y * y);
    }

    internal void Add(TractionCellStats other)
    {
        N += other.N;
        (Sx, Sy, Sxx, Sxy, Syy) = (Sx + other.Sx, Sy + other.Sy, Sxx + other.Sxx, Sxy + other.Sxy, Syy + other.Syy);
    }
}
