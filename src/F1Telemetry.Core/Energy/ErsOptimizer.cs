namespace F1Telemetry.Core.Energy;

public enum PlanKind
{
    /// <summary>One fast lap: starts with a full battery, may end empty.</summary>
    Qualifying,

    /// <summary>A lap to repeat: ends with at least the charge it started with.</summary>
    Race,
}

/// <summary>A deploy zone of a plan: the flat-out stretch from <see cref="From"/> to <see cref="To"/> m.</summary>
/// <param name="Seconds">Simulated time from the zone's start to the next zone's, with the plan.</param>
/// <param name="DrivenSeconds">The same with the modes of the reference lap.</param>
public sealed record ZonePlan(int Number, double From, double To, DeployOption Choice, double EntryStore, double ExitStore, double Seconds, double DrivenSeconds)
{
    /// <summary>Seconds the plan gains here over the reference lap's modes (negative = loses, to save energy).</summary>
    public double Gain => DrivenSeconds - Seconds;
}

/// <summary>
/// A deploy plan for a lap: a <see cref="DeployOption"/> per zone, with the simulated lap time and the battery level
/// and mode of every segment.
/// </summary>
public sealed record LapPlan(PlanKind Kind, double StartStore, double EndStore, double Seconds, IReadOnlyList<ZonePlan> Zones, int[] Modes, double[] Stores);

/// <summary>
/// The reference lap replayed through the simulator. <see cref="DrivenSeconds"/> uses the MGU-K output it really had:
/// the baseline every plan is measured against. <see cref="MapSeconds"/> uses its modes with the deploy map's output
/// for them: how close that gets to the real lap is how far the plans, built on the map, can be trusted.
/// </summary>
/// <param name="ActualSeconds">The real time over the simulated segments.</param>
public sealed record ModelCheck(double DrivenSeconds, double MapSeconds, double ActualSeconds, double MapEndStore, double ActualEndStore, int[] Modes, double[] Stores)
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
/// Finds the fastest deploy plan for a lap by dynamic programming over its deploy zones and the battery level
/// (in <see cref="Quantum"/> J steps), with a few drivable options per zone (<see cref="Options"/>).
/// </summary>
public static class ErsOptimizer
{
    public const double Quantum = 25_000;
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
        modes.AddRange(deploy.Curves.Select(c => c.Mode));
        var options = modes.Select(m => new DeployOption(m, m)).ToList();
        foreach (var curve in deploy.Curves)
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

    /// <summary>Time and exit level of every stage, option and entry level.</summary>
    internal static (double Seconds, int Exit)[,,] Precompute(LapSimulator simulator, List<Stage> stages, IReadOnlyList<DeployOption> options)
    {
        var table = new (double, int)[stages.Count, options.Count, States];
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
                    var result = simulator.Run(stage.From, stage.DeployEnd, stage.To, options[o], s * Quantum);
                    table[k, o, s] = (result.Seconds, State(result.ExitStore));
                }
            }
        });

        return table;
    }

    internal sealed record Policy(double[][] Value, int[][] Choice);

    /// <summary>Backwards over the stages: the fastest time to the line from each level, ending at <paramref name="minEnd"/> or above.</summary>
    internal static Policy Solve((double Seconds, int Exit)[,,] table, List<Stage> stages, int optionCount, int minEnd)
    {
        var value = new double[stages.Count + 1][];
        var choice = new int[stages.Count][];
        value[stages.Count] = [.. Enumerable.Range(0, States).Select(s => s >= minEnd ? 0 : double.PositiveInfinity)];
        for (var k = stages.Count - 1; k >= 0; k--)
        {
            value[k] = new double[States];
            choice[k] = new int[States];
            var count = stages[k].HasZone ? optionCount : 1;
            for (var s = 0; s < States; s++)
            {
                var best = double.PositiveInfinity;
                var pick = 0;
                for (var o = 0; o < count; o++)
                {
                    var (seconds, exit) = table[k, o, s];
                    var total = seconds + value[k + 1][exit];
                    if (total < best - 1e-9)
                    {
                        (best, pick) = (total, o);
                    }
                }

                value[k][s] = best;
                choice[k][s] = pick;
            }
        }

        return new Policy(value, choice);
    }

    /// <summary>Follows the policy from <paramref name="start"/>, re-simulating each stage from the exact level.</summary>
    internal static LapPlan Build(LapSimulator simulator, List<Stage> stages, IReadOnlyList<DeployOption> options, Policy policy, int start, PlanKind kind)
    {
        var count = simulator.Lap.Segments.Count;
        var modes = new int[count];
        var stores = new double[count];
        var zones = new List<ZonePlan>();
        var store = start * Quantum;
        var driven = simulator.Lap.StartStore;
        double seconds = 0;
        for (var k = 0; k < stages.Count; k++)
        {
            var stage = stages[k];
            var option = stage.HasZone ? options[policy.Choice[k][State(store)]] : new DeployOption(DeployModes.None, DeployModes.None);
            var entry = store;
            var result = simulator.Run(stage.From, stage.DeployEnd, stage.To, option, store, modes, stores);
            var reference = simulator.Run(stage.From, stage.DeployEnd, stage.To, DeployOption.AsDriven, driven);
            driven = reference.ExitStore;
            store = result.ExitStore;
            seconds += result.Seconds;
            if (stage.HasZone)
            {
                zones.Add(new ZonePlan(zones.Count + 1, stage.From * LapProfile.Step, stage.DeployEnd * LapProfile.Step, option, entry, store,
                    result.Seconds, reference.Seconds));
            }
        }

        return new LapPlan(kind, start * Quantum, store, seconds, zones, modes, stores);
    }

    /// <summary>The reference lap with its own modes, from its own starting level.</summary>
    public static ModelCheck Check(LapSimulator simulator) => Check(simulator, Stages(simulator.Lap));

    internal static ModelCheck Check(LapSimulator simulator, List<Stage> stages)
    {
        var count = simulator.Lap.Segments.Count;
        var modes = new int[count];
        var stores = new double[count];
        var (driven, _) = Replay(simulator, stages, DeployOption.AsDriven, modes, stores);
        var (byMap, mapEnd) = Replay(simulator, stages, DeployOption.AsDrivenByMap, null, null);
        return new ModelCheck(driven, byMap, simulator.Lap.CoveredSeconds, mapEnd, simulator.Lap.EndStore, modes, stores);
    }

    private static (double Seconds, double EndStore) Replay(LapSimulator simulator, List<Stage> stages, DeployOption option, int[]? modes, double[]? stores)
    {
        var store = simulator.Lap.StartStore;
        double seconds = 0;
        foreach (var stage in stages)
        {
            var result = simulator.Run(stage.From, stage.DeployEnd, stage.To, option, store, modes, stores);
            store = result.ExitStore;
            seconds += result.Seconds;
        }

        return (seconds, store);
    }
}

/// <summary>
/// Plans one lap as often as needed: what every zone option does from every battery level is simulated once (about
/// 0.1 s), then each plan is a quick search. The live overlay re-plans at every line from the battery it has.
/// </summary>
public sealed class ErsPlanner
{
    private readonly List<ErsOptimizer.Stage> _stages;
    private readonly IReadOnlyList<DeployOption> _options;
    private readonly (double Seconds, int Exit)[,,] _table;

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

        // Every starting level, each with the end held at or above it; keep the fastest.
        var floor = ErsOptimizer.State(Simulator.Reserve);
        var best = floor;
        var bestTime = double.PositiveInfinity;
        ErsOptimizer.Policy? bestPolicy = null;
        for (var s = floor; s < ErsOptimizer.States; s++)
        {
            var policy = Solve(s);
            if (policy.Value[0][s] < bestTime - 1e-9)
            {
                (best, bestTime, bestPolicy) = (s, policy.Value[0][s], policy);
            }
        }

        var race = Build(bestPolicy!, best, kind);
        var oneMj = (int)Math.Round(ErsOptimizer.OneMj / ErsOptimizer.Quantum);
        LapPlan? attack = null, recover = null;
        if (best - oneMj >= 0)
        {
            attack = Build(Solve(best - oneMj), best, kind);
        }

        if (best + oneMj < ErsOptimizer.States && Solve(best + oneMj) is var up && !double.IsInfinity(up.Value[0][best]))
        {
            recover = Build(up, best, kind);
        }

        return new PlanResult(race, check, attack, recover);
    }

    /// <summary>
    /// The fastest lap from <paramref name="start"/> J that ends at or above <paramref name="minEnd"/> J, or as close
    /// below it as the lap allows (a battery too low to recover fully in one lap).
    /// </summary>
    public LapPlan PlanFrom(PlanKind kind, double start, double minEnd)
    {
        var from = ErsOptimizer.State(start);
        for (var end = ErsOptimizer.State(minEnd); end >= 0; end--)
        {
            var policy = Solve(end);
            if (!double.IsInfinity(policy.Value[0][from]))
            {
                return Build(policy, from, kind);
            }
        }

        return Build(Solve(0), from, kind);
    }

    private ErsOptimizer.Policy Solve(int minEnd) => ErsOptimizer.Solve(_table, _stages, _options.Count, minEnd);

    private LapPlan Build(ErsOptimizer.Policy policy, int start, PlanKind kind) => ErsOptimizer.Build(Simulator, _stages, _options, policy, start, kind);
}
