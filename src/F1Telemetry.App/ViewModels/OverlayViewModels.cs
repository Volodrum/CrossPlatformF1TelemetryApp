using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.App.ViewModels;

public abstract class OverlayViewModelBase : ObservableObject
{
    /// <summary>Fills the overlay with representative values so it can be positioned without live data.</summary>
    public abstract void LoadPreview();

    /// <summary>Returns the overlay to its empty "no data yet" state (e.g. to drop preview values).</summary>
    public abstract void Reset();
}

public sealed record LapTimingRow(string Lap, Chip S1, Chip S2, Chip S3, Chip Time, string Delta, IBrush DeltaBrush, bool IsCurrent)
{
    public IBrush RowBackground => IsCurrent ? Palette.Raised : Palette.Transparent;
    public IBrush LapBrush => IsCurrent ? Palette.TextHi : Palette.TextMid;
}

/// <summary>
/// Timing board. Sector chips: purple fill = session best, green fill = faster than the previous lap,
/// amber outline = slower, "—" = not yet run.
/// </summary>
public sealed partial class LapTimingOverlayViewModel : OverlayViewModelBase
{
    [ObservableProperty] public partial string BestLap { get; set; } = "-";

    public ObservableCollection<LapTimingRow> Rows { get; } = [];

    public void Update(LapTimingSnapshot snapshot)
    {
        var best = snapshot.SessionBest;
        BestLap = best is null ? "-" : TimeFormat.Lap(best.LapTimeMs);

        Rows.Clear();
        var laps = snapshot.RecentLaps.Take(4).ToList();
        for (var i = 0; i < laps.Count; i++)
        {
            var lap = laps[i];
            var previous = i + 1 < snapshot.RecentLaps.Count ? snapshot.RecentLaps[i + 1] : null;
            var delta = best is not null && lap.HasTime ? (int)lap.LapTimeMs - (int)best.LapTimeMs : (int?)null;

            Rows.Add(new LapTimingRow(
                $"L{lap.LapNumber}",
                Sector(lap.Sector1Ms, previous?.Sector1Ms, lap.IsBestSector1),
                Sector(lap.Sector2Ms, previous?.Sector2Ms, lap.IsBestSector2),
                Sector(lap.Sector3Ms, previous?.Sector3Ms, lap.IsBestSector3),
                !lap.HasTime ? Chip.Empty : lap.IsBestLap ? Chip.Filled(TimeFormat.Lap(lap.LapTimeMs), Palette.SessionBest) : Chip.Plain(TimeFormat.Lap(lap.LapTimeMs), Palette.TextHi),
                delta switch { null => "—", 0 => "BEST", _ => TimeFormat.Delta(delta.Value) },
                delta switch { null => Palette.TextLo, 0 => Palette.SessionBest, > 0 => Palette.Slower, _ => Palette.Faster },
                i == 0));
        }
    }

    private static Chip Sector(uint ms, uint? previousMs, bool isSessionBest)
    {
        if (ms == 0)
        {
            return Chip.Empty;
        }

        var text = TimeFormat.Sector(ms);
        if (isSessionBest)
        {
            return Chip.Filled(text, Palette.SessionBest);
        }

        return previousMs is > 0 && ms < previousMs ? Chip.Filled(text, Palette.Faster) : Chip.Outlined(text, Palette.Slower);
    }

    public override void LoadPreview() => Update(new LapTimingSnapshot(
    [
        new LapRecord { LapNumber = 5, Sector1Ms = 26_341 },
        new LapRecord { LapNumber = 4, LapTimeMs = 83_412, Sector1Ms = 26_388, Sector2Ms = 28_104, Sector3Ms = 28_920, IsBestLap = true, IsBestSector3 = true },
        new LapRecord { LapNumber = 3, LapTimeMs = 83_823, Sector1Ms = 26_512, Sector2Ms = 28_260, Sector3Ms = 29_051 },
        new LapRecord { LapNumber = 2, LapTimeMs = 83_659, Sector1Ms = 26_301, Sector2Ms = 28_090, Sector3Ms = 29_268, IsBestSector1 = true, IsBestSector2 = true },
        new LapRecord { LapNumber = 1, LapTimeMs = 84_950, Sector1Ms = 27_020, Sector2Ms = 28_600, Sector3Ms = 29_330 },
    ], new LapRecord { LapNumber = 4, LapTimeMs = 83_412 }));

    public override void Reset()
    {
        BestLap = "-";
        Rows.Clear();
    }
}

/// <summary>Delta pop-up: one hero number (lap delta) with a ▼/▲ marker, plus the sector split as a chip.</summary>
public sealed partial class DeltaOverlayViewModel : OverlayViewModelBase
{
    private readonly DispatcherTimer _hideTimer;

    public DeltaOverlayViewModel()
    {
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            IsShowing = false;
        };
    }

    [ObservableProperty] public partial bool IsShowing { get; set; }

    /// <summary>Fades out instead of collapsing, so the overlay window keeps its size.</summary>
    public double Opacity => IsShowing ? 1 : 0;

    partial void OnIsShowingChanged(bool value) => OnPropertyChanged(nameof(Opacity));

    [ObservableProperty] public partial string LapDelta { get; set; } = "";
    [ObservableProperty] public partial IBrush LapBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial bool IsFaster { get; set; }
    [ObservableProperty] public partial bool IsSlower { get; set; }
    [ObservableProperty] public partial string SectorLabel { get; set; } = "";
    [ObservableProperty] public partial Chip SectorChip { get; set; } = Chip.Empty;

    public void Update(DeltaUpdate delta, bool sticky = false)
    {
        LapDelta = TimeFormat.Delta(delta.LapDeltaMs).Replace('-', '−');
        LapBrush = Palette.TextFor(delta.LapColor);
        IsFaster = delta.LapDeltaMs < 0 || delta.LapColor == DeltaColor.SessionBest;
        IsSlower = !IsFaster;
        SectorLabel = delta.SectorNumber == 3 ? "SECTOR 3 · LAP" : $"SECTOR {delta.SectorNumber}";
        SectorChip = Palette.For(delta.SectorColor, TimeFormat.Delta(delta.SectorDeltaMs).Replace('-', '−'));
        IsShowing = true;
        _hideTimer.Stop();
        if (!sticky)
        {
            _hideTimer.Start();
        }
    }

    public override void LoadPreview() => Update(new DeltaUpdate(1, -47, DeltaColor.PersonalBest, -47, DeltaColor.PersonalBest), sticky: true);

    /// <summary>Hidden until the first real sector split arrives.</summary>
    public override void Reset()
    {
        _hideTimer.Stop();
        IsShowing = false;
    }
}

public sealed partial class RadarOverlayViewModel : OverlayViewModelBase
{
    [ObservableProperty] public partial RadarFrame? Frame { get; set; }
    [ObservableProperty] public partial bool HasCars { get; set; }

    /// <summary>The radar fades out instead of collapsing, so the overlay window keeps its size.</summary>
    public double Opacity => HasCars ? 1 : 0;

    public bool Preview { get; set; }

    partial void OnHasCarsChanged(bool value) => OnPropertyChanged(nameof(Opacity));

    public void Update(RadarFrame frame)
    {
        Frame = frame;
        HasCars = Preview || frame.AnyCarClose;
    }

    public override void Reset()
    {
        Preview = false;
        Frame = null;
        HasCars = false;
    }

    public override void LoadPreview()
    {
        Preview = true;
        Update(new RadarFrame(
        [
            new RadarBlip(1, 2.7f, 0.6f, 0, 2.8f, RadarSeverity.Danger),
            new RadarBlip(2, -1.2f, -9.5f, 3, 9.6f, RadarSeverity.Near),
            new RadarBlip(3, 0.6f, 14f, -2, 14f, RadarSeverity.Far),
        ], false, true, 25.2f));
    }
}

public sealed record ForecastTile(string Time, string Rain, IBrush Background, IBrush Border, IBrush Foreground);

/// <summary>
/// Conditions and strategy overlay. Page 1 (strategy): weather/SC header, temperatures, forecast tiles, tyre life and
/// fuel range. Page 2 (damage): the car from above with damage per part. The hotkey flips pages; new damage shows
/// page 2 for a few seconds and then returns to the page that was chosen.
/// </summary>
public sealed partial class TrackConditionsOverlayViewModel : OverlayViewModelBase
{
    private readonly DispatcherTimer _popupTimer;
    private bool _damagePageChosen;

    public TrackConditionsOverlayViewModel()
    {
        _popupTimer = new DispatcherTimer();
        _popupTimer.Tick += (_, _) =>
        {
            _popupTimer.Stop();
            ShowDamage = _damagePageChosen;
        };
    }

    [ObservableProperty] public partial bool ShowDamage { get; set; }
    public bool ShowStrategy => !ShowDamage;
    public IBrush StrategyPager => ShowDamage ? Palette.LineStrong : Palette.Info;
    public IBrush DamagePager => ShowDamage ? Palette.Info : Palette.LineStrong;

    partial void OnShowDamageChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStrategy));
        OnPropertyChanged(nameof(StrategyPager));
        OnPropertyChanged(nameof(DamagePager));
    }

    [ObservableProperty] public partial CarDamage? Damage { get; set; }
    [ObservableProperty] public partial string Floor { get; set; } = "–";
    [ObservableProperty] public partial IBrush FloorBrush { get; set; } = Palette.TextLo;
    [ObservableProperty] public partial string Diffuser { get; set; } = "–";
    [ObservableProperty] public partial IBrush DiffuserBrush { get; set; } = Palette.TextLo;
    [ObservableProperty] public partial string Gearbox { get; set; } = "–";
    [ObservableProperty] public partial IBrush GearboxBrush { get; set; } = Palette.TextLo;
    [ObservableProperty] public partial string Faults { get; set; } = "–";
    [ObservableProperty] public partial IBrush FaultsBrush { get; set; } = Palette.TextLo;

    /// <summary>Hotkey: switch pages. Ends a running damage pop-up.</summary>
    public void TogglePage()
    {
        _popupTimer.Stop();
        _damagePageChosen = !ShowDamage;
        ShowDamage = _damagePageChosen;
    }

    /// <summary>New damage: show the damage page for <paramref name="seconds"/>, restarting the timer on further damage.</summary>
    public void PopUpDamage(double seconds)
    {
        if (_damagePageChosen)
        {
            return;
        }

        ShowDamage = true;
        _popupTimer.Stop();
        _popupTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
        _popupTimer.Start();
    }

    public void Update(CarDamage d)
    {
        Damage = d;
        (Floor, FloorBrush) = ($"{d.FloorDamage}%", Palette.Damage(d.FloorDamage));
        (Diffuser, DiffuserBrush) = ($"{d.DiffuserDamage}%", Palette.Damage(d.DiffuserDamage));
        (Gearbox, GearboxBrush) = ($"{d.GearBoxDamage}%", Palette.Damage(d.GearBoxDamage));
        var faults = new[]
        {
            (d.EngineBlown, "ENGINE BLOWN"),
            (d.EngineSeized, "ENGINE SEIZED"),
            (d.DrsFault, "DRS FAULT"),
            (d.ErsFault, "ERS FAULT"),
        }.Where(f => f.Item1).Select(f => f.Item2).ToList();
        (Faults, FaultsBrush) = faults.Count == 0 ? ("DRS OK · ERS OK", Palette.Faster) : (string.Join(" · ", faults), Palette.Danger);
    }

    [ObservableProperty] public partial string Weather { get; set; } = "WAITING FOR DATA";
    [ObservableProperty] public partial string SafetyCar { get; set; } = "";
    [ObservableProperty] public partial bool HasSafetyCar { get; set; }
    [ObservableProperty] public partial string TrackTemp { get; set; } = "–";
    [ObservableProperty] public partial string AirTemp { get; set; } = "–";
    [ObservableProperty] public partial string Compound { get; set; } = "Unknown";
    [ObservableProperty] public partial string TyreLabel { get; set; } = "TYRE LIFE";
    [ObservableProperty] public partial string TyreLaps { get; set; } = "CALC…";
    [ObservableProperty] public partial IBrush TyreBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string FuelLabel { get; set; } = "FUEL";
    [ObservableProperty] public partial string FuelLaps { get; set; } = "CALC…";
    [ObservableProperty] public partial IBrush FuelBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial IBrush StatusBrush { get; set; } = Palette.TextLo;
    [ObservableProperty] public partial bool HasStatus { get; set; }

    public ObservableCollection<ForecastTile> Forecast { get; } = [];

    public void Update(TrackConditions c)
    {
        Weather = WeatherTypes.Name(c.Weather).ToUpperInvariant();
        TrackTemp = $"{c.TrackTemperature}°";
        AirTemp = $"{c.AirTemperature}°";
        SafetyCar = c.SafetyCarStatus switch
        {
            SafetyCarStatus.Full => "SAFETY CAR",
            SafetyCarStatus.Virtual => "VSC",
            SafetyCarStatus.FormationLap => "FORMATION",
            _ => "",
        };
        HasSafetyCar = SafetyCar.Length > 0;

        Forecast.Clear();
        foreach (var sample in c.Forecast.Where(f => f.TimeOffsetMinutes > 0).Take(3))
        {
            var rainy = sample.RainPercentage >= 30;
            Forecast.Add(new ForecastTile(
                $"+{sample.TimeOffsetMinutes} MIN",
                $"{sample.RainPercentage}%",
                rainy ? Palette.InfoTint : Palette.Raised,
                rainy ? Palette.Info : Palette.Raised,
                rainy ? Palette.Info : Palette.TextHi));
        }
    }

    public void Update(StrategySnapshot s)
    {
        Compound = s.Compound;
        TyreLabel = $"TYRE LIFE · {s.HighestWearTyre} {s.HighestWear:0}%";
        TyreLaps = s.TyreLapsRemaining is { } tyre ? $"{tyre:0.0}" : "CALC…";
        TyreBrush = s.TyreLapsRemaining switch { null => Palette.TextMid, < 3 => Palette.Danger, < 8 => Palette.Slower, _ => Palette.Faster };
        FuelLabel = $"FUEL · {s.FuelInTank:0.00} KG";
        FuelLaps = s.FuelLapsRemaining is { } fuel ? $"{fuel:0.0}" : $"{s.GameFuelRemainingLaps:+0.0;−0.0}";
        FuelBrush = s.FuelLapsRemaining switch { null => Palette.TextMid, < 1.5 => Palette.Danger, _ => Palette.Slower };
        (Status, StatusBrush) = s switch
        {
            { PitStatus: PitStatus.Pitting } => ("PITTING", Palette.Slower),
            { PitStatus: PitStatus.InPitArea } => ("PIT BOX", Palette.Slower),
            { DriverStatus: DriverStatus.InGarage } => ("GARAGE", Palette.TextMid),
            { DriverStatus: DriverStatus.OutLap } => ("OUT LAP", Palette.Info),
            { DriverStatus: DriverStatus.InLap } => ("IN LAP", Palette.TextMid),
            { CurrentLapInvalidated: true } => ("LAP INVALID", Palette.Danger),
            _ => ("", Palette.TextLo),
        };
        HasStatus = Status.Length > 0;
    }

    public override void LoadPreview()
    {
        Update(new TrackConditions("Monza", 1, 32, 24, 12, SafetyCarStatus.Virtual,
        [
            new WeatherForecastSample(15, 0, 1, 32, 0, 24, 0, 5),
            new WeatherForecastSample(15, 5, 1, 32, 0, 24, 0, 10),
            new WeatherForecastSample(15, 10, 2, 31, 1, 23, 1, 20),
            new WeatherForecastSample(15, 15, 3, 29, 1, 22, 1, 45),
        ]));
        Update(new StrategySnapshot("Medium", 9, new WheelValues(18, 38, 15, 16), "FR", 38, 14.2, 18.45, 9.6, 9.8, 1.9,
            PitStatus.None, DriverStatus.FlyingLap, true, false));
        Update(new CarDamage(new Tyres<float>(24, 33, 29, 38), default, new Tyres<byte>(2, 3, 4, 6), default,
            FrontLeftWingDamage: 54, FrontRightWingDamage: 0, RearWingDamage: 4, FloorDamage: 31, DiffuserDamage: 0, SidepodDamage: 22,
            DrsFault: false, ErsFault: false, GearBoxDamage: 14, EngineDamage: 3, EngineMguhWear: 18, EngineEsWear: 7, EngineCeWear: 5,
            EngineIceWear: 12, EngineMgukWear: 9, EngineTcWear: 10, EngineBlown: false, EngineSeized: false));
    }

    public override void Reset()
    {
        Weather = "WAITING FOR DATA";
        SafetyCar = "";
        HasSafetyCar = false;
        TrackTemp = "–";
        AirTemp = "–";
        Compound = "Unknown";
        TyreLabel = "TYRE LIFE";
        TyreLaps = "CALC…";
        TyreBrush = Palette.TextHi;
        FuelLabel = "FUEL";
        FuelLaps = "CALC…";
        FuelBrush = Palette.TextHi;
        Status = "";
        StatusBrush = Palette.TextLo;
        HasStatus = false;
        Forecast.Clear();
        _popupTimer.Stop();
        ShowDamage = _damagePageChosen;
        Damage = null;
        (Floor, Diffuser, Gearbox, Faults) = ("–", "–", "–", "–");
        (FloorBrush, DiffuserBrush, GearboxBrush, FaultsBrush) = (Palette.TextLo, Palette.TextLo, Palette.TextLo, Palette.TextLo);
    }
}

/// <summary>
/// Throttle/brake history. Deliberately thin: the control reads <see cref="History"/> itself every frame, so no
/// per-sample property changes (and no layout passes) happen here.
/// </summary>
public sealed partial class InputTraceOverlayViewModel(InputHistory history) : OverlayViewModelBase
{
    public InputHistory History { get; } = history;

    [ObservableProperty] public partial double WindowSeconds { get; set; } = 6;
    [ObservableProperty] public partial bool Preview { get; set; }

    public string Header => $"INPUTS · LAST {WindowSeconds:0} S";

    partial void OnWindowSecondsChanged(double value) => OnPropertyChanged(nameof(Header));

    public override void LoadPreview() => Preview = true;

    public override void Reset() => Preview = false;
}
