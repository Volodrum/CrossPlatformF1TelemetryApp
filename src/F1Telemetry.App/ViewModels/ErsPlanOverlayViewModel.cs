using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

/// <summary>
/// ERS PLAN overlay: the deploy mode the plan wants now (the headline), the next switch and where, the battery against
/// the plan's level at this point, and in races the gap to the car ahead and whether overtake mode is available. The
/// app never changes the mode: the driver does, in the game.
/// </summary>
public sealed partial class ErsPlanOverlayViewModel : OverlayViewModelBase
{
    /// <summary>Width of the battery bar, px (the overlay is 300 wide with 16 px padding).</summary>
    public const double BarWidth = 264;

    // Within this of the plan's level counts as on plan.
    private const double OnLevel = 50_000;

    [ObservableProperty] public partial string Title { get; set; } = "ERS PLAN";
    [ObservableProperty] public partial bool HasPlan { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Waiting for a session.";
    [ObservableProperty] public partial string Zone { get; set; } = "—";
    [ObservableProperty] public partial string NowLetter { get; set; } = "—";
    [ObservableProperty] public partial IBrush NowFill { get; set; } = Palette.Raised;
    [ObservableProperty] public partial IBrush NowForeground { get; set; } = Palette.TextMid;
    [ObservableProperty] public partial IBrush NowBorder { get; set; } = Palette.Transparent;
    [ObservableProperty] public partial string NowCaption { get; set; } = "";
    [ObservableProperty] public partial IBrush NowCaptionBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string NextLetter { get; set; } = "—";
    [ObservableProperty] public partial IBrush NextFill { get; set; } = Palette.Raised;
    [ObservableProperty] public partial IBrush NextForeground { get; set; } = Palette.TextMid;
    [ObservableProperty] public partial string NextAt { get; set; } = "";
    [ObservableProperty] public partial string Battery { get; set; } = "—";
    [ObservableProperty] public partial IBrush BatteryBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string Delta { get; set; } = "";
    [ObservableProperty] public partial IBrush DeltaBrush { get; set; } = Palette.TextMid;
    [ObservableProperty] public partial double BarFill { get; set; }
    [ObservableProperty] public partial double TargetX { get; set; }
    [ObservableProperty] public partial string Fight { get; set; } = "";
    [ObservableProperty] public partial bool OvertakeReady { get; set; }

    public bool HasFight => Fight.Length > 0;

    partial void OnFightChanged(string value) => OnPropertyChanged(nameof(HasFight));

    public void Update(ErsPlanService service, TelemetrySample? sample, FieldSnapshot? field)
    {
        Title = service.Kind == PlanKind.Qualifying ? "ERS PLAN · QUALIFYING" : $"ERS PLAN · RACE · {service.Stance.ToString().ToUpperInvariant()}";
        if (service.Plan is not { } plan || sample is null)
        {
            HasPlan = false;
            Status = service.Plan is null ? service.Status : "Waiting for the car.";
            return;
        }

        var advice = ErsCoach.Advise(plan, sample.LapDistance, sample.ErsStoreEnergy, sample.ErsDeployMode, sample.Speed);
        Show(advice, sample.ErsStoreEnergy, sample.ErsDeployMode);

        // Fights: the gap to the car ahead in races, and whether the game offers overtake mode now.
        var player = field?.Player;
        Fight = player is { Position: > 1, DeltaToCarInFrontMs: > 0 and < 1000 } && SessionTypes.IsRace(field!.SessionType)
            ? $"CAR AHEAD {(player.DeltaToCarInFrontMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} S"
            : "";
        OvertakeReady = sample.OvertakeAvailable == 1;
    }

    private void Show(CoachAdvice advice, double store, int currentMode)
    {
        HasPlan = true;
        Zone = advice.Zone is { } zone ? $"Z{zone.Number}" : "—";
        if (advice.NowMode is { } now)
        {
            (NowLetter, NowFill, NowForeground) = (DeployModes.Letter(now), Palette.DeployMode(now), Palette.OnDeployMode(now));
            NowBorder = advice.OnPlan ? Palette.Transparent : Palette.Slower;
            NowCaption = advice.OnPlan ? DeployModes.Name(now).ToUpperInvariant() : $"SWITCH TO {DeployModes.Name(now).ToUpperInvariant()}";
            NowCaptionBrush = advice.OnPlan ? Palette.TextHi : Palette.Slower;
        }
        else
        {
            (NowLetter, NowFill, NowForeground, NowBorder) = ("—", Palette.Raised, Palette.TextMid, Palette.Transparent);
            NowCaption = "BETWEEN ZONES";
            NowCaptionBrush = Palette.TextMid;
        }

        if (advice.NextMode is { } next)
        {
            (NextLetter, NextFill, NextForeground) = (DeployModes.Letter(next), Palette.DeployMode(next), Palette.OnDeployMode(next));
            NextAt = (advice.NextAt.EndsWith("KM/H", StringComparison.Ordinal) ? "AT " : "IN ") + advice.NextAt;
        }
        else
        {
            (NextLetter, NextFill, NextForeground, NextAt) = ("—", Palette.Raised, Palette.TextMid, "");
        }

        Battery = EnergyText.Mj(store);
        BatteryBrush = EnergyText.Band(store);
        Delta = Math.Abs(advice.Delta) < OnLevel ? "ON PLAN"
            : advice.Delta > 0 ? $"▲ +{EnergyText.Mj(advice.Delta)} VS PLAN"
            : $"▼ −{EnergyText.Mj(-advice.Delta)} VS PLAN";
        DeltaBrush = Math.Abs(advice.Delta) < OnLevel ? Palette.TextMid : advice.Delta > 0 ? Palette.Faster : Palette.Slower;
        BarFill = Math.Clamp(store / EnergyAnalyzer.Capacity, 0, 1) * BarWidth;
        TargetX = Math.Clamp(advice.Target / EnergyAnalyzer.Capacity, 0, 1) * BarWidth;
    }

    public override void LoadPreview()
    {
        Title = "ERS PLAN · RACE · NORMAL";
        var zone = new ZonePlan(2, 400, 2125, new DeployOption(DeployModes.Overtake, DeployModes.None, UntilKmh: 270), 2_400_000, 1_300_000, 0, 0);
        Show(new CoachAdvice(zone, DeployModes.Overtake, false, DeployModes.None, "270 KM/H", 1_950_000, 230_000), 2_180_000, DeployModes.Medium);
        Fight = "CAR AHEAD 0.6 S";
        OvertakeReady = true;
    }

    public override void Reset()
    {
        HasPlan = false;
        Status = "Waiting for a session.";
        Fight = "";
        OvertakeReady = false;
    }
}
