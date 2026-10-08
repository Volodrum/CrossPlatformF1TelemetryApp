using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Formatting;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.ViewModels;

/// <summary>One deploy zone of the plan, next to what the reference lap ran there.</summary>
public sealed record ZoneRowViewModel(ZonePlan Zone, IReadOnlyList<ModeShare> Driven)
{
    public string Number => $"Z{Zone.Number}";
    public string Range => $"{(Zone.From / 1000).ToString("0.00", CultureInfo.InvariantCulture)}–{(Zone.To / 1000).ToString("0.00", CultureInfo.InvariantCulture)} km";

    public IReadOnlyList<ModeShare> Plan => Zone.Choice.First == Zone.Choice.Then
        ? [Share(Zone.Choice.First, "")]
        : [Share(Zone.Choice.First, ""), Share(Zone.Choice.Then, "")];

    public string Description => Zone.Choice.Describe();
    public string Battery => $"{EnergyText.Mj(Zone.EntryStore)} → {EnergyText.Mj(Zone.ExitStore)}";
    public string Gain => Math.Abs(Zone.Gain) < 0.0005 ? "0.000" : (Zone.Gain > 0 ? "−" : "+") + Math.Abs(Zone.Gain).ToString("0.000", CultureInfo.InvariantCulture);
    public IBrush GainBrush => Zone.Gain >= 0.0005 ? Palette.Faster : Zone.Gain <= -0.0005 ? Palette.Slower : Palette.TextMid;

    public static ModeShare Share(int mode, string text) =>
        new(DeployModes.Letter(mode), DeployModes.Name(mode).ToUpperInvariant(), text, Palette.DeployMode(mode), Palette.OnDeployMode(mode));
}

/// <summary>
/// The ERS lap plan for the lap chosen on the energy tab: qualifying (one fast lap from a full battery) or race (a lap
/// that ends with the charge it started with, keeping a reserve), simulated on the track's car model.
/// </summary>
public sealed partial class EnergyPlanViewModel(ILogger<EnergyPlanViewModel> log) : ObservableObject
{
    public static IReadOnlyList<double> Reserves { get; } = [0, 0.5, 1.0];

    private LapProfile? _lap;
    private ErsCarModel? _model;
    private double _lapSeconds;
    private int _version;

    [ObservableProperty] public partial PlanKind Kind { get; set; } = PlanKind.Race;
    [ObservableProperty] public partial double ReserveMj { get; set; } = 0.5;
    [ObservableProperty] public partial PlanResult? Result { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string Note { get; set; } = "";
    [ObservableProperty] public partial string Predicted { get; set; } = "—";
    [ObservableProperty] public partial string Gain { get; set; } = "—";
    [ObservableProperty] public partial string Battery { get; set; } = "—";
    [ObservableProperty] public partial string BatteryNote { get; set; } = "";
    [ObservableProperty] public partial string Budget { get; set; } = "—";
    [ObservableProperty] public partial string BudgetNote { get; set; } = "";
    [ObservableProperty] public partial Chip? Confidence { get; set; }
    [ObservableProperty] public partial string ConfidenceNote { get; set; } = "";

    public ObservableCollection<ZoneRowViewModel> Zones { get; } = [];

    public bool HasPlan => Result is not null;
    public bool IsQualifying => Kind == PlanKind.Qualifying;
    public bool IsRace => Kind == PlanKind.Race;

    /// <summary>Raised on the UI thread when <see cref="Result"/> changes (null when there is no plan).</summary>
    public event Action<PlanResult?>? PlanChanged;

    partial void OnKindChanged(PlanKind value)
    {
        OnPropertyChanged(nameof(IsQualifying));
        OnPropertyChanged(nameof(IsRace));
        _ = RecomputeAsync();
    }

    partial void OnReserveMjChanged(double value) => _ = RecomputeAsync();

    partial void OnResultChanged(PlanResult? value)
    {
        OnPropertyChanged(nameof(HasPlan));
        PlanChanged?.Invoke(value);
    }

    [RelayCommand]
    private void SetKind(PlanKind kind) => Kind = kind;

    /// <summary>Drops the plan while a new lap loads.</summary>
    public void Clear()
    {
        ++_version;
        Show(null, "");
    }

    /// <summary>Plans <paramref name="lap"/> (null clears) on <paramref name="model"/>.</summary>
    public Task SetLapAsync(LapProfile? lap, ErsCarModel? model, double lapSeconds)
    {
        (_lap, _model, _lapSeconds) = (lap, model, lapSeconds);
        return RecomputeAsync();
    }

    private async Task RecomputeAsync()
    {
        var version = ++_version;
        if (_lap is not { } lap || _model is not { } model)
        {
            Show(null, _model is { HasPowerData: false }
                ? "Plans need ICE and MGU-K power, recorded from this version on. Re-import older captures with f1tel reimport."
                : "Pick a lap with power data to plan it.");
            return;
        }

        var simulator = LapSimulator.Create(lap, model, Kind == PlanKind.Race ? ReserveMj * ErsOptimizer.OneMj : 0);
        if (simulator is null)
        {
            Show(null, "The car model needs a straight-line fit and a deploy map first: a few more laps at this track.");
            return;
        }

        IsBusy = true;
        try
        {
            var kind = Kind;
            var result = await Task.Run(() => ErsOptimizer.Plan(simulator, kind));
            if (version == _version)
            {
                Show(result, "");
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ERS plan failed");
            if (version == _version)
            {
                Show(null, $"Could not plan this lap: {ex.Message}");
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
        Zones.Clear();
        Result = result;
        if (result is null)
        {
            return;
        }

        var plan = result.Plan;
        Predicted = TimeFormat.Lap(result.PredictedSeconds(_lapSeconds) * 1000);
        Gain = (result.Gain >= 0 ? "−" : "+") + Math.Abs(result.Gain).ToString("0.000", CultureInfo.InvariantCulture) + " s";
        Battery = $"{EnergyText.Mj(plan.StartStore)} → {EnergyText.Mj(plan.EndStore)}";
        BatteryNote = plan.Kind == PlanKind.Qualifying
            ? "Charge the battery full on the out-lap; the lap may end empty."
            : $"Cross the line with {EnergyText.Mj(plan.StartStore)} MJ every lap"
              + (ReserveMj > 0 ? $", never below the {ReserveMj.ToString("0.0", CultureInfo.InvariantCulture)} MJ reserve." : ".");

        static string S(double seconds) => seconds.ToString("0.00", CultureInfo.InvariantCulture);
        (Budget, BudgetNote) = (result.AttackGain, result.RecoverCost, plan.Kind) switch
        {
            (_, _, PlanKind.Qualifying) => ("—", "Qualifying spends everything: there is nothing to save for."),
            ({ } attack, { } recover, _) => ($"1 MJ ≈ {S(attack)} s", $"Spending 1 MJ more this lap gains about {S(attack)} s; winning it back costs about {S(recover)} s."),
            ({ } attack, null, _) => ($"1 MJ ≈ {S(attack)} s", $"Spending 1 MJ more this lap gains about {S(attack)} s."),
            (null, { } recover, _) => ($"1 MJ ≈ {S(recover)} s", $"The plan starts at the reserve, so there is no MJ to spare; winning 1 MJ back costs about {S(recover)} s."),
            _ => ("—", "Neither spending nor winning back 1 MJ fits this lap."),
        };

        var check = result.Check;
        var error = Math.Abs(check.Error).ToString("0.00", CultureInfo.InvariantCulture);
        Confidence = check.IsTrusted ? Chip.Filled($"MODEL ±{error} s", Palette.Faster) : Chip.Outlined("LOW CONFIDENCE", Palette.Slower);
        ConfidenceNote = check.IsTrusted
            ? $"Your modes, with the deploy map's output, replay this lap within {error} s."
            : $"Your modes, with the deploy map's output, replay this lap {error} s {(check.Error > 0 ? "slow" : "fast")}: the map "
              + $"{(check.MapEndStore > check.ActualEndStore ? "credits your modes with less" : "credits your modes with more")} energy than they gave. "
              + "Treat the gains as a direction, and record more laps here.";

        var lap = _lap!;
        foreach (var zone in plan.Zones)
        {
            var segments = lap.Segments.Where(s => s.From >= zone.From && s.From < zone.To && s.FullThrottle).ToList();
            var driven = segments.GroupBy(s => s.Mode).OrderBy(g => g.Key)
                .Select(g => ZoneRowViewModel.Share(g.Key, $"{g.Count() * 100 / Math.Max(1, segments.Count)}%"))
                .ToList();
            Zones.Add(new ZoneRowViewModel(zone, driven));
        }
    }
}
