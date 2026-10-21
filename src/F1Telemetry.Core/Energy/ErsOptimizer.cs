using System.Collections.Concurrent;

namespace F1Telemetry.Core.Energy;

public enum PlanKind
{
    /// <summary>One fast lap: starts with a full battery, may end empty.</summary>
    Qualifying,

    /// <summary>
    /// A lap to repeat every lap of the race: deploys what it harvests, so it ends with the charge it started with and the
    /// next lap can run the same plan.
    /// </summary>
    Race,
}

/// <summary>A deploy zone of a plan: the flat-out stretch from <see cref="From"/> to <see cref="To"/> m.</summary>
/// <param name="Seconds">Simulated time from the zone's start to the next zone's, with the plan.</param>
/// <param name="DrivenSeconds">The same with the modes of the reference lap.</param>
/// <param name="Deployed">Energy the MGU-K deploys from the zone's start to the next zone's, with the plan, J.</param>
public sealed record ZonePlan(int Number, double From, double To, DeployOption Choice, double EntryStore, double ExitStore, double Seconds, double DrivenSeconds,
    double Deployed = 0)
{
    /// <summary>Seconds the plan gains here over the reference lap's modes (negative = loses, to save energy).</summary>
    public double Gain => DrivenSeconds - Seconds;
}

/// <summary>
/// A deploy plan for a lap: a <see cref="DeployOption"/> per zone, with the simulated lap time and the battery level
/// and mode of every segment.
/// </summary>
/// <param name="Deployed">Energy the MGU-K deploys over the lap, J.</param>
public sealed record LapPlan(PlanKind Kind, double StartStore, double EndStore, double Seconds, IReadOnlyList<ZonePlan> Zones, int[] Modes, double[] Stores,
    double Deployed = 0)
{
    /// <summary>Energy that goes into the battery over the lap, J (harvest that doesn't fit in a full battery is lost).</summary>
    public double Harvested => EndStore - StartStore + Deployed;
}

/// <summary>
/// The reference lap replayed through the simulator. <see cref="DrivenSeconds"/> uses the MGU-K output it really had:
/// the baseline every plan is measured against. <see cref="MapSeconds"/> uses its modes with the car model's output,
/// draw and harvest for them: how close that gets to the real lap is how far the plans, built on the model, can be
/// trusted.
/// </summary>
/// <param name="ActualSeconds">The real time over the simulated segments.</param>
/// <param name="BatteryError">RMS gap between the battery the map replay has and the one the lap really had, segment by segment, J.</param>
public sealed record ModelCheck(double DrivenSeconds, double MapSeconds, double ActualSeconds, double MapEndStore, double ActualEndStore, int[] Modes, double[] Stores,
    double BatteryError = 0)
{
    /// <summary>Plans are trusted when the deploy map replays the lap within this.</summary>
    public const double TrustedWithin = 0.3;

    public double Error => MapSeconds - ActualSeconds;
    public bool IsTrusted => Math.Abs(Error) <= TrustedWithin;
}

/// <summary>
/// A plan with its model check and, for races, the battle budget: the same lap ending 1 MJ lower (attack) or higher
/// (recover).
/// </summary>
public sealed record PlanResult(LapPlan Plan, ModelCheck Check, LapPlan? Attack, LapPlan? Recover)
{
    /// <summary>Seconds the plan gains over the reference lap as driven, both simulated (model errors cancel).</summary>
    public double Gain => Check.DrivenSeconds - Plan.Seconds;

    /// <summary>A lap of <paramref name="lapSeconds"/> less <see cref="Gain"/>.</summary>
    public double PredictedSeconds(double lapSeconds) => lapSeconds - Gain;

    /// <summary>Seconds one extra MJ buys on this lap.</summary>
    public double? AttackGain => Attack is null ? null : Plan.Seconds - Attack.Seconds;

    /// <summary>Seconds it costs to end the lap with one MJ more.</summary>
    public double? RecoverCost => Recover is null ? null : Recover.Seconds - Plan.Seconds;
}

/// <summary>
/// Finds the fastest deploy plan for a lap by dynamic programming over its deploy zones, the battery level (in
/// <see cref="Quantum"/> J steps) and, where the lap has a harvest limit, what it has harvested so far (in
/// <see cref="HarvestQuantum"/> J steps), with a few drivable options per zone (<see cref="Options"/>). Tracking the
/// harvest lets a plan weigh clipping: what the game harvests flat out only counts until the lap reaches its limit, and
/// what reaches it early leaves the braking zones after it nothing to give.
/// </summary>
public static class ErsOptimizer
{
    public const double Quantum = 25_000;
    public const double HarvestQuantum = 100_000;
    public const double OneMj = 1_000_000;

    private const double Capacity = LapSimulator.Capacity;
    internal static readonly int States = (int)Math.Round(Capacity / Quantum) + 1;

    /// <summary>A stretch from a deploy zone's start (<see cref="From"/>) to the next one's, flat out until <see cref="DeployEnd"/>.</summary>
    internal sealed record Stage(int From, int DeployEnd, int To)
    {
        public bool HasZone => DeployEnd > From;
    }

    /// <summary>
    /// What a zone can be run in: each mode on its own; a mode until the speed it fades from, then another; a mode for
    /// the first third or two thirds of the zone, then None.
    /// </summary>
    public static IReadOnlyList<DeployOption> Options(DeployMap deploy)
    {
        var modes = new List<int> { DeployModes.None };
        modes.AddRange(deploy.Curves.Select(c => c.Mode).Where(m => m != DeployModes.None));
        var options = modes.Select(m => new DeployOption(m, m)).ToList();
        foreach (var curve in deploy.Curves.Where(c => c.Mode != DeployModes.None))
        {
            if (curve.FadeFromKmh is { } fade)
            {
                options.AddRange(modes.Where(m => m != curve.Mode).Select(m => new DeployOption(curve.Mode, m, UntilKmh: fade)));
            }

            options.Add(new DeployOption(curve.Mode, DeployModes.None, UntilFraction: 1 / 3.0));
            options.Add(new DeployOption(curve.Mode, DeployModes.None, UntilFraction: 2 / 3.0));
        }

        return options;
    }

    /// <summary>
    /// The fastest plan of <paramref name="kind"/> for the simulator's lap. Qualifying starts at
    /// <paramref name="qualifyingStart"/> J (a full battery after a charging out-lap) and may end empty. A race picks
    /// the starting level that gives the fastest lap ending at least as charged as it began.
    /// </summary>
    public static PlanResult Plan(LapSimulator simulator, PlanKind kind, double qualifyingStart = Capacity) =>
        new ErsPlanner(simulator).Plan(kind, qualifyingStart);

    internal static int State(double joules) => Math.Clamp((int)Math.Round(joules / Quantum), 0, States - 1);

    /// <summary>Harvest levels a lap is planned over: none to track (one level) when it has no harvest limit.</summary>
    internal static int HarvestStates(LapProfile lap) => lap.HarvestLimit > 0 ? (int)Math.Ceiling(lap.HarvestLimit / HarvestQuantum) + 1 : 1;

    internal static int HarvestState(double joules, int states) => states == 1 ? 0 : Math.Clamp((int)Math.Round(joules / HarvestQuantum), 0, states - 1);

    internal static List<Stage> Stages(LapProfile lap)
    {
        var segments = lap.Segments;
        var runs = new List<(int From, int To)>();
        for (var i = 0; i < segments.Count;)
        {
            if (!segments[i].FullThrottle)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < segments.Count && segments[i].FullThrottle)
            {
                i++;
            }

            runs.Add((start, i));
        }

        var stages = new List<Stage>();
        if (runs.Count == 0 || runs[0].From > 0)
        {
            stages.Add(new Stage(0, 0, runs.Count == 0 ? segments.Count : runs[0].From));
        }

        for (var r = 0; r < runs.Count; r++)
        {
            stages.Add(new Stage(runs[r].From, runs[r].To, r + 1 < runs.Count ? runs[r + 1].From : segments.Count));
        }

        return stages;
    }

    /// <summary>One stage run with one option from one battery and harvest level: its time, and the levels it ends at.</summary>
    internal readonly record struct Cell(float Seconds, short Exit, short ExitHarvest);

    /// <summary>[stage, option, battery level · <see cref="HarvestLevels"/> + harvest level].</summary>
    internal sealed record Table(Cell[,,] Cells, int HarvestLevels);

    /// <summary>
    /// Time and exit levels of every stage, option, battery level and harvest level. A stage is simulated once from no
    /// harvest; from any level where it can't reach the limit it runs the same, so only the levels near the limit are
    /// simulated again.
    /// </summary>
    internal static Table Precompute(LapSimulator simulator, List<Stage> stages, IReadOnlyList<DeployOption> options)
    {
        var levels = HarvestStates(simulator.Lap);
        var limit = simulator.Lap.HarvestLimit;
        var cells = new Cell[stages.Count, options.Count, States * levels];
        Parallel.For(0, stages.Count, k =>
        {
            var stage = stages[k];
            for (var o = 0; o < options.Count; o++)
            {
                if (!stage.HasZone && o > 0)
                {
                    continue; // nothing to deploy: every option is the same
                }

                for (var s = 0; s < States; s++)
                {
                    var free = simulator.Run(stage.From, stage.DeployEnd, stage.To, options[o], s * Quantum);
                    for (var h = 0; h < levels; h++)
                    {
                        var harvested = h * HarvestQuantum;
                        var result = levels == 1 || harvested + free.ExitHarvested <= limit
                            ? free with { ExitHarvested = harvested + free.ExitHarvested }
                            : simulator.Run(stage.From, stage.DeployEnd, stage.To, options[o], s * Quantum, harvested: harvested);
                        cells[k, o, s * levels + h] = new Cell((float)result.Seconds, (short)State(result.ExitStore), (short)HarvestState(result.ExitHarvested, levels));
                    }
                }
            }
        });

        return new Table(cells, levels);
    }

    /// <param name="Value">[stage][battery level · harvest levels + harvest level]: fastest time to the line.</param>
    /// <param name="Choice">The option that gets it.</param>
    internal sealed record Policy(double[][] Value, byte[][] Choice, int HarvestLevels)
    {
        public double At(int stage, int store, int harvest) => Value[stage][store * HarvestLevels + harvest];

        public int ChoiceAt(int stage, int store, int harvest) => Choice[stage][store * HarvestLevels + harvest];
    }

    /// <summary>Backwards over the stages: the fastest time to the line from each level, ending at <paramref name="minEnd"/> or above.</summary>
    internal static Policy Solve(Table table, List<Stage> stages, int optionCount, int minEnd)
    {
        var levels = table.HarvestLevels;
        var count = States * levels;
        var value = new double[stages.Count + 1][];
        var choice = new byte[stages.Count][];
        value[stages.Count] = [.. Enumerable.Range(0, count).Select(i => i / levels >= minEnd ? 0 : double.PositiveInfinity)];
        for (var k = stages.Count - 1; k >= 0; k--)
        {
            value[k] = new double[count];
            choice[k] = new byte[count];
            var options = stages[k].HasZone ? optionCount : 1;
            var next = value[k + 1];
            for (var i = 0; i < count; i++)
            {
                var best = double.PositiveInfinity;
                var pick = 0;
                for (var o = 0; o < options; o++)
                {
                    var cell = table.Cells[k, o, i];
                    var total = cell.Seconds + next[cell.Exit * levels + cell.ExitHarvest];
                    if (total < best - 1e-9)
                    {
                        (best, pick) = (total, o);
                    }
                }

                value[k][i] = best;
                choice[k][i] = (byte)pick;
            }
        }

        return new Policy(value, choice, levels);
    }

    /// <summary>
    /// Follows the policy from stage <paramref name="from"/> with <paramref name="store"/> J in the battery and
    /// <paramref name="harvested"/> J harvested on the lap, re-simulating each stage from the exact levels. Stages
    /// before <paramref name="from"/> keep what <paramref name="before"/> planned for them.
    /// </summary>
    internal static LapPlan Build(LapSimulator simulator, List<Stage> stages, IReadOnlyList<DeployOption> options, Policy policy, double store, PlanKind kind,
        int from = 0, double harvested = 0, LapPlan? before = null)
    {
        var lap = simulator.Lap;
        var count = lap.Segments.Count;
        var modes = before?.Modes.ToArray() ?? new int[count];
        var stores = before?.Stores.ToArray() ?? new double[count];
        var cut = stages[from].From * LapProfile.Step;
        var zones = before?.Zones.Where(z => z.From < cut).ToList() ?? [];
        var later = before?.Zones.Where(z => z.From >= cut).ToList() ?? [];
        var seconds = before is null ? 0 : before.Seconds - later.Sum(z => z.Seconds);
        var deployed = before is null ? 0 : before.Deployed - later.Sum(z => z.Deployed);
        var start = before?.StartStore ?? store;
        var driven = lap.Level[stages[from].From];
        var drivenHarvested = lap.Harvested[stages[from].From];
        for (var k = from; k < stages.Count; k++)
        {
            var stage = stages[k];
            var option = stage.HasZone
                ? options[policy.ChoiceAt(k, State(store), HarvestState(harvested, policy.HarvestLevels))]
                : new DeployOption(DeployModes.None, DeployModes.None);
            var entry = store;
            var result = simulator.Run(stage.From, stage.DeployEnd, stage.To, option, store, modes, stores, harvested);
            var reference = simulator.Run(stage.From, stage.DeployEnd, stage.To, DeployOption.AsDriven, driven, harvested: drivenHarvested);
            (driven, drivenHarvested) = (reference.ExitStore, reference.ExitHarvested);
            (store, harvested) = (result.ExitStore, result.ExitHarvested);
            seconds += result.Seconds;
            deployed += result.Deployed;
            if (stage.HasZone)
            {
                zones.Add(new ZonePlan(zones.Count + 1, stage.From * LapProfile.Step, stage.DeployEnd * LapProfile.Step, option, entry, store,
                    result.Seconds, reference.Seconds, result.Deployed));
            }
        }

        return new LapPlan(kind, start, store, seconds, zones, modes, stores, deployed);
    }

    /// <summary>The reference lap with its own modes, from its own starting level.</summary>
    public static ModelCheck Check(LapSimulator simulator) => Check(simulator, Stages(simulator.Lap));

    internal static ModelCheck Check(LapSimulator simulator, List<Stage> stages)
    {
        var count = simulator.Lap.Segments.Count;
        var modes = new int[count];
        var stores = new double[count];
        var (driven, _) = Replay(simulator, stages, DeployOption.AsDriven, modes, stores);
        var mapStores = new double[count];
        var (byMap, mapEnd) = Replay(simulator, stages, DeployOption.AsDrivenByMap, null, mapStores);
        var level = simulator.Lap.Level;
        var error = count == 0 ? 0 : Math.Sqrt(Enumerable.Range(0, count).Average(i => (mapStores[i] - level[i + 1]) * (mapStores[i] - level[i + 1])));
        return new ModelCheck(driven, byMap, simulator.Lap.CoveredSeconds, mapEnd, simulator.Lap.EndStore, modes, stores, error);
    }

    private static (double Seconds, double EndStore) Replay(LapSimulator simulator, List<Stage> stages, DeployOption option, int[]? modes, double[]? stores)
    {
        var store = simulator.Lap.StartStore;
        var harvested = simulator.Lap.Harvested[0];
        double seconds = 0;
        foreach (var stage in stages)
        {
            var result = simulator.Run(stage.From, stage.DeployEnd, stage.To, option, store, modes, stores, harvested);
            (store, harvested) = (result.ExitStore, result.ExitHarvested);
            seconds += result.Seconds;
        }

        return (seconds, store);
    }
}

/// <summary>
/// Plans one lap as often as needed: what every zone option does from every battery and harvest level is simulated
/// once (well under a second), then each plan is a quick search. The live overlay re-plans at the line from the battery
/// it has, and again at every zone from the battery and harvest the car really has there (<see cref="Continue"/>).
/// </summary>
public sealed class ErsPlanner
{
    // A race plan's starting level is searched this many levels apart, then level by level around the best.
    private const int CoarseStep = 4;

    // Policies kept for re-planning during the lap, by the level they end at.
    private const int CachedPolicies = 8;

    private readonly List<ErsOptimizer.Stage> _stages;
    private readonly IReadOnlyList<DeployOption> _options;
    private readonly ErsOptimizer.Table _table;
    private readonly ConcurrentDictionary<int, ErsOptimizer.Policy> _policies = new();

    public ErsPlanner(LapSimulator simulator)
    {
        Simulator = simulator;
        _stages = ErsOptimizer.Stages(simulator.Lap);
        _options = ErsOptimizer.Options(simulator.Model.Deploy);
        _table = ErsOptimizer.Precompute(simulator, _stages, _options);
    }

    public LapSimulator Simulator { get; }

    /// <inheritdoc cref="ErsOptimizer.Plan"/>
    public PlanResult Plan(PlanKind kind, double qualifyingStart = LapSimulator.Capacity)
    {
        var check = ErsOptimizer.Check(Simulator, _stages);
        if (kind == PlanKind.Qualifying)
        {
            return new PlanResult(PlanFrom(kind, qualifyingStart, 0), check, null, null);
        }

        // Every starting level, each with the end held at or above it; keep the fastest. Coarse first, then around the best.
        var floor = ErsOptimizer.State(Simulator.Reserve);
        var best = floor;
        var bestTime = double.PositiveInfinity;
        ErsOptimizer.Policy? bestPolicy = null;
        void Try(int s)
        {
            var policy = Solve(s);
            if (policy.At(0, s, 0) < bestTime - 1e-9)
            {
                (best, bestTime, bestPolicy) = (s, policy.At(0, s, 0), policy);
            }
        }

        for (var s = floor; s < ErsOptimizer.States; s += CoarseStep)
        {
            Try(s);
        }

        var coarse = best;
        for (var s = Math.Max(floor, coarse - CoarseStep + 1); s < Math.Min(ErsOptimizer.States, coarse + CoarseStep); s++)
        {
            if (s != coarse)
            {
                Try(s);
            }
        }

        var race = Build(bestPolicy!, best * ErsOptimizer.Quantum, kind);
        var oneMj = (int)Math.Round(ErsOptimizer.OneMj / ErsOptimizer.Quantum);
        LapPlan? attack = null, recover = null;
        if (best - oneMj >= 0)
        {
            attack = Build(Solve(best - oneMj), best * ErsOptimizer.Quantum, kind);
        }

        if (best + oneMj < ErsOptimizer.States && Solve(best + oneMj) is var up && !double.IsInfinity(up.At(0, best, 0)))
        {
            recover = Build(up, best * ErsOptimizer.Quantum, kind);
        }

        return new PlanResult(race, check, attack, recover);
    }

    /// <summary>
    /// The fastest lap from <paramref name="start"/> J that ends at or above <paramref name="minEnd"/> J, or as close
    /// below it as the lap allows (a battery too low to recover fully in one lap).
    /// </summary>
    public LapPlan PlanFrom(PlanKind kind, double start, double minEnd) => From(0, start, 0, minEnd, kind, null);

    /// <summary>
    /// Re-plans the rest of <paramref name="plan"/> from zone <paramref name="zone"/> (1 = the first) with the battery
    /// and harvest the car really has there, aiming for <paramref name="minEnd"/> J at the line as the plan did. The
    /// zones before keep what the plan said. Null when the lap has no such zone.
    /// </summary>
    public LapPlan? Continue(LapPlan plan, int zone, double store, double harvested, double minEnd)
    {
        var stage = -1;
        for (int k = 0, seen = 0; k < _stages.Count; k++)
        {
            if (_stages[k].HasZone && ++seen == zone)
            {
                stage = k;
                break;
            }
        }

        return stage < 0 ? null : From(stage, store, harvested, minEnd, plan.Kind, plan);
    }

    private LapPlan From(int stage, double store, double harvested, double minEnd, PlanKind kind, LapPlan? before)
    {
        var from = ErsOptimizer.State(store);
        var harvest = ErsOptimizer.HarvestState(harvested, _table.HarvestLevels);
        for (var end = ErsOptimizer.State(minEnd); end >= 0; end--)
        {
            var policy = Cached(end);
            if (!double.IsInfinity(policy.At(stage, from, harvest)))
            {
                return Build(policy, store, kind, stage, harvested, before);
            }
        }

        return Build(Cached(0), store, kind, stage, harvested, before);
    }

    private ErsOptimizer.Policy Cached(int minEnd)
    {
        if (_policies.TryGetValue(minEnd, out var policy))
        {
            return policy;
        }

        if (_policies.Count >= CachedPolicies)
        {
            _policies.Clear();
        }

        return _policies.GetOrAdd(minEnd, Solve);
    }

    private ErsOptimizer.Policy Solve(int minEnd) => ErsOptimizer.Solve(_table, _stages, _options.Count, minEnd);

    private LapPlan Build(ErsOptimizer.Policy policy, double store, PlanKind kind, int from = 0, double harvested = 0, LapPlan? before = null) =>
        ErsOptimizer.Build(Simulator, _stages, _options, policy, store, kind, from, harvested, before);
}
