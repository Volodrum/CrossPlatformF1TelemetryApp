using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.ViewModels;

/// <summary>
/// ERS PLAN overlay, kept to three things: the deploy mode the plan wants now (ringed amber when the car runs another),
/// the mode it switches to next, and how long until that switch (seconds at the current speed, metres when slow, or the
/// speed for a speed switch). The app never changes the mode: the driver does, in the game.
/// </summary>
public sealed partial class ErsPlanOverlayViewModel : OverlayViewModelBase
{
    // Below this the time to the switch means little (pit lane, spins): count metres instead.
    private const int CountSecondsFromKmh = 40;

    [ObservableProperty] public partial bool HasPlan { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "Waiting for a session.";
    [ObservableProperty] public partial string NowLetter { get; set; } = "—";
    [ObservableProperty] public partial IBrush NowFill { get; set; } = Palette.Raised;
    [ObservableProperty] public partial IBrush NowForeground { get; set; } = Palette.TextMid;
    [ObservableProperty] public partial IBrush NowBorder { get; set; } = Palette.Transparent;
    [ObservableProperty] public partial string NextLetter { get; set; } = "—";
    [ObservableProperty] public partial IBrush NextFill { get; set; } = Palette.Raised;
    [ObservableProperty] public partial IBrush NextForeground { get; set; } = Palette.TextMid;
    [ObservableProperty] public partial string Countdown { get; set; } = "";
    [ObservableProperty] public partial string CountdownUnit { get; set; } = "";
    [ObservableProperty] public partial string PlanTag { get; set; } = "RACE";
    [ObservableProperty] public partial IBrush PlanTagBrush { get; set; } = Palette.TextLo;

    public void Update(ErsPlanService service, TelemetrySample? sample)
    {
        (PlanTag, PlanTagBrush) = Tag(service.Kind, service.Stance);
        if (service.Plan is not { } plan || sample is null)
        {
            HasPlan = false;
            Status = service.Plan is null ? service.Status : "Waiting for the car.";
            return;
        }

        var advice = ErsCoach.Advise(plan, sample.LapDistance, sample.ErsStoreEnergy, sample.ErsDeployMode, sample.Speed);
        Show(advice, sample.Speed);
    }

    private void Show(CoachAdvice advice, double kmh)
    {
        HasPlan = true;
        if (advice.NowMode is { } now)
        {
            (NowLetter, NowFill, NowForeground) = (DeployModes.Letter(now), Palette.DeployMode(now), Palette.OnDeployMode(now));
            NowBorder = advice.OnPlan ? Palette.Transparent : Palette.Slower;
        }
        else
        {
            (NowLetter, NowFill, NowForeground, NowBorder) = ("—", Palette.Raised, Palette.TextMid, Palette.Transparent);
        }

        if (advice.NextMode is { } next)
        {
            (NextLetter, NextFill, NextForeground) = (DeployModes.Letter(next), Palette.DeployMode(next), Palette.OnDeployMode(next));
            (Countdown, CountdownUnit) = CountdownText(advice, kmh);
        }
        else
        {
            (NextLetter, NextFill, NextForeground, Countdown, CountdownUnit) = ("—", Palette.Raised, Palette.TextMid, "", "");
        }
    }

    private static (string Value, string Unit) CountdownText(CoachAdvice advice, double kmh)
    {
        if (advice.NextIn is not { } metres)
        {
            // A speed switch: no distance to count, so show the speed it happens at ("270 KM/H").
            var parts = advice.NextAt.Split(' ', 2);
            return (parts[0], parts.Length > 1 ? parts[1] : "");
        }

        if (kmh < CountSecondsFromKmh)
        {
            return ($"{Math.Max(0, Math.Round(metres / 10) * 10):0}", "M");
        }

        var seconds = metres / (kmh / 3.6);
        return (seconds.ToString(seconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture), "S");
    }

    /// <summary>The corner tag naming the plan the hotkey picked: quiet for a normal race, coloured otherwise.</summary>
    private static (string Text, IBrush Brush) Tag(PlanKind kind, RaceStance stance) => (kind, stance) switch
    {
        (PlanKind.Qualifying, _) => ("QUALI", Palette.SessionBest),
        (_, RaceStance.Attack) => ("ATTACK", Palette.Orange),
        (_, RaceStance.Recover) => ("RECOVER", Palette.Faster),
        _ => ("RACE", Palette.TextLo),
    };

    public override void LoadPreview()
    {
        (PlanTag, PlanTagBrush) = Tag(PlanKind.Race, RaceStance.Attack);
        var zone = new ZonePlan(2, 400, 2125, new DeployOption(DeployModes.Overtake, DeployModes.None, UntilFraction: 0.5), 2_400_000, 1_300_000, 0, 0);
        Show(new CoachAdvice(zone, DeployModes.Overtake, false, DeployModes.None, "380 M", 380, 1_950_000, 230_000), 290);
    }

    public override void Reset()
    {
        HasPlan = false;
        Status = "Waiting for a session.";
    }
}
