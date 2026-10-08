using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Formatting;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.ViewModels;

/// <summary>One deploy zone of the plan, next to what the session ran there.</summary>
public sealed record ZoneRowViewModel(ZonePlan Zone, IReadOnlyList<ModeShare> Driven, EnergyUnit Unit)
{
    public string Number => $"Z{Zone.Number}";
    public string Range => $"{(Zone.From / 1000).ToString("0.00", CultureInfo.InvariantCulture)}–{(Zone.To / 1000).ToString("0.00", CultureInfo.InvariantCulture)} km";

    public IReadOnlyList<ModeShare> Plan => Zone.Choice.First == Zone.Choice.Then
        ? [Share(Zone.Choice.First, "")]
        : [Share(Zone.Choice.First, ""), Share(Zone.Choice.Then, "")];

    public string Description => Zone.Choice.Describe();
    public string Battery => $"{EnergyText.Format(Zone.EntryStore, Unit)} → {EnergyText.Format(Zone.ExitStore, Unit)}";
    public string Gain => Math.Abs(Zone.Gain) < 0.0005 ? "0.000" : (Zone.Gain > 0 ? "−" : "+") + Math.Abs(Zone.Gain).ToString("0.000", CultureInfo.InvariantCulture);
    public IBrush GainBrush => Zone.Gain >= 0.0005 ? Palette.Faster : Zone.Gain <= -0.0005 ? Palette.Slower : Palette.TextMid;

    public static ModeShare Share(int mode, string text) =>
        new(DeployModes.Letter(mode), DeployModes.Name(mode).ToUpperInvariant(), text, Palette.DeployMode(mode), Palette.OnDeployMode(mode));
}

/// <summary>What a session gives the planner: the car model and the lap each kind of plan is built on.</summary>
/// <param name="Race">The session's typical lap (its laps averaged): a race plan must hold for every one of them.</param>
/// <param name="Qualifying">The session's fastest lap: one push lap.</param>
public sealed record PlanInputs(ErsCarModel Model, LapProfile Race, string RaceBasis, LapProfile Qualifying, string QualifyingBasis);

/// <summary>
/// The ERS lap plan for the session on the energy tab: qualifying (one fast lap from a full battery, built on the
/// session's fastest lap) or race (a lap that deploys what it harvests, so every lap of the race can run it, built on the
/// session's typical lap), simulated on the track's car model.
/// </summary>
public sealed partial class EnergyPlanViewModel(ILogger<EnergyPlanViewModel> log) : ObservableObject
{
    private PlanInputs? _inputs;

    // The lap and basis of the plan on show (the kind may already have changed while the next plan computes).
    private LapProfile? _shownLap;
    private string _shownBasis = "";
    private readonly Dictionary<PlanKind, PlanResult> _results = [];
    private int _version;

    [ObservableProperty] public partial PlanKind Kind { get; set; } = PlanKind.Race;
    [ObservableProperty] public partial EnergyUnit Unit { get; set; } = EnergyUnit.Mj;
    [ObservableProperty] public partial PlanResult? Result { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool CanPlan { get; set; }
    [ObservableProperty] public partial string Note { get; set; } = "";
    [ObservableProperty] public partial string Basis { get; set; } = "";
    [ObservableProperty] public partial string Predicted { get; set; } = "—";
    [ObservableProperty] public partial string Gain { get; set; } = "—";
    [ObservableProperty] public partial string Battery { get; set; } = "—";
    [ObservableProperty] public partial string BatteryNote { get; set; } = "";
    [ObservableProperty] public partial string Energy { get; set; } = "—";
    [ObservableProperty] public partial string EnergyNote { get; set; } = "";
    [ObservableProperty] public partial string Budget { get; set; } = "—";
    [ObservableProperty] public partial string BudgetNote { get; set; } = "";
    [ObservableProperty] public partial Chip? Confidence { get; set; }
    [ObservableProperty] public partial string ConfidenceNote { get; set; } = "";

    public ObservableCollection<ZoneRowViewModel> Zones { get; } = [];

    public bool HasPlan => Result is not null;
    public bool IsQualifying => Kind == PlanKind.Qualifying;
    public bool IsRace => Kind == PlanKind.Race;
    public string UnitLabel => EnergyText.Unit(Unit);

    /// <summary>The lap the current plan is built on.</summary>
    public LapProfile? Lap => _inputs is not { } inputs ? null : Kind == PlanKind.Race ? inputs.Race : inputs.Qualifying;

    /// <summary>Raised on the UI thread when <see cref="Result"/> changes (null when there is no plan).</summary>
    public event Action<PlanResult?>? PlanChanged;

    partial void OnKindChanged(PlanKind value)
    {
        OnPropertyChanged(nameof(IsQualifying));
        OnPropertyChanged(nameof(IsRace));
        _ = RecomputeAsync();
    }

    partial void OnUnitChanged(EnergyUnit value)
    {
        OnPropertyChanged(nameof(UnitLabel));
        Render(Result);
    }

    partial void OnResultChanged(PlanResult? value)
    {
        OnPropertyChanged(nameof(HasPlan));
        PlanChanged?.Invoke(value);
    }

    [RelayCommand]
    private void SetKind(PlanKind kind) => Kind = kind;

    /// <summary>Drops the plan; <paramref name="loading"/> shows it is being prepared (the session's laps load first).</summary>
    public void Clear(bool loading = false)
    {
        ++_version;
        _inputs = null;
        _results.Clear();
        CanPlan = false;
        IsBusy = loading;
        Show(null, "");
    }

    /// <summary>Plans the session from <paramref name="inputs"/>, or shows <paramref name="unavailable"/> when there are none.</summary>
    public Task SetSessionAsync(PlanInputs? inputs, string unavailable)
    {
        ++_version;
        _inputs = inputs;
        _results.Clear();
        CanPlan = inputs is not null;
        if (inputs is null)
        {
            IsBusy = false;
            Show(null, unavailable);
            return Task.CompletedTask;
        }

        return RecomputeAsync();
    }

    private async Task RecomputeAsync()
    {
        var version = ++_version;
        if (_inputs is not { } inputs || Lap is not { } lap)
        {
            return;
        }

        var kind = Kind;
        if (_results.TryGetValue(kind, out var cached))
        {
            IsBusy = false;
            Show(cached, "");
            return;
        }

        if (LapSimulator.Create(lap, inputs.Model) is not { } simulator)
        {
            IsBusy = false;
            Show(null, "The car model needs a straight-line fit and a deploy map first: a few more laps at this track.");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => ErsOptimizer.Plan(simulator, kind));
            if (version == _version)
            {
                _results[kind] = result;
                Show(result, "");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ERS plan failed");
            if (version == _version)
            {
                Show(null, $"Could not plan this session: {ex.Message}");
            }
        }
        finally
        {
            if (version == _version)
            {
                IsBusy = false;
            }
        }
    }

    private void Show(PlanResult? result, string note)
    {
        Note = note;
        (_shownLap, _shownBasis) = result is null || _inputs is not { } inputs ? (null, "")
            : result.Plan.Kind == PlanKind.Race ? (inputs.Race, inputs.RaceBasis) : (inputs.Qualifying, inputs.QualifyingBasis);
        Render(result); // before Result: PlanChanged listeners read the texts
        Result = result;
    }

    /// <summary>Fills the texts and zone rows from <paramref name="shown"/> in the current <see cref="Unit"/>.</summary>
    private void Render(PlanResult? shown)
    {
        Zones.Clear();
        if (shown is not { } result || _shownLap is not { } lap)
        {
            Basis = "";
            return;
        }

        var plan = result.Plan;
        var unit = Unit;
        string E(double joules) => EnergyText.WithUnit(joules, unit);

        Basis = _shownBasis;
        Predicted = TimeFormat.Lap(result.PredictedSeconds(lap.ActualSeconds) * 1000);
        Gain = (result.Gain >= 0 ? "−" : "+") + Math.Abs(result.Gain).ToString("0.000", CultureInfo.InvariantCulture) + " s";
        Battery = $"{EnergyText.Format(plan.StartStore, unit)} → {EnergyText.Format(plan.EndStore, unit)}";
        BatteryNote = plan.Kind == PlanKind.Qualifying
            ? "Charge the battery full on the out-lap; the lap may end empty."
            : $"Cross the line with {E(plan.StartStore)} every lap.";
        Energy = $"{EnergyText.Format(plan.Harvested, unit)} / {EnergyText.Format(plan.Deployed, unit)}";
        EnergyNote = plan.Kind == PlanKind.Qualifying
            ? $"Deploys {E(plan.Deployed)}: the full battery plus what the lap harvests."
            : $"Harvest {E(plan.Harvested)} and deploy {E(plan.Deployed)} on every lap: the same energy each lap, so every race lap runs this plan.";

        static string S(double seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture);
        var oneMj = E(ErsOptimizer.OneMj);
        (Budget, BudgetNote) = (result.AttackGain, result.RecoverCost, plan.Kind) switch
        {
            (_, _, PlanKind.Qualifying) => ("—", "Qualifying spends everything: there is nothing to save for."),
            ({ } attack, { } recover, _) => ($"{oneMj} ≈ {S(attack)} s", $"Spending {oneMj} more on one lap gains about {S(attack)} s; winning it back costs about {S(recover)} s."),
            ({ } attack, null, _) => ($"{oneMj} ≈ {S(attack)} s", $"Spending {oneMj} more on one lap gains about {S(attack)} s."),
            (null, { } recover, _) => ($"{oneMj} ≈ {S(recover)} s", $"The battery has no {oneMj} to spare at the line; winning {oneMj} back costs about {S(recover)} s."),
            _ => ("—", $"Neither spending nor winning back {oneMj} fits this lap."),
        };

        var check = result.Check;
        var error = Math.Abs(check.Error).ToString("0.00", CultureInfo.InvariantCulture);
        Confidence = check.IsTrusted ? Chip.Filled($"MODEL ±{error} s", Palette.Faster) : Chip.Outlined("LOW CONFIDENCE", Palette.Slower);
        ConfidenceNote = check.IsTrusted
            ? $"Your modes, with the deploy map's output, replay the lap within {error} s."
            : $"Your modes, with the deploy map's output, replay the lap {error} s {(check.Error > 0 ? "slow" : "fast")}: the map "
              + $"{(check.MapEndStore > check.ActualEndStore ? "credits your modes with less" : "credits your modes with more")} energy than they gave. "
              + "Treat the gains as a direction, and record more laps here.";

        foreach (var zone in plan.Zones)
        {
            var segments = lap.Segments.Where(s => s.From >= zone.From && s.From < zone.To && s.FullThrottle).ToList();
            var driven = segments.GroupBy(s => s.Mode).OrderBy(g => g.Key)
                .Select(g => ZoneRowViewModel.Share(g.Key, $"{g.Count() * 100 / Math.Max(1, segments.Count)}%"))
                .ToList();
            Zones.Add(new ZoneRowViewModel(zone, driven, unit));
        }
    }
}
