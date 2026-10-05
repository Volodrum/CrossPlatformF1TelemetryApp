using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Infrastructure;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Input;
using F1Telemetry.Core.Models;

namespace F1Telemetry.App.ViewModels;

public sealed partial class OverlaySettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly OverlayManager _overlays;

    public OverlaySettingsViewModel(OverlayKind kind, string name, string description, SettingsService settings, OverlayManager overlays)
    {
        Kind = kind;
        Name = name;
        Description = description;
        _settings = settings;
        _overlays = overlays;
        Mode = settings.Current.Overlay(kind).Mode;
        Scale = Math.Clamp(settings.Current.Overlay(kind).Scale, MinScale, MaxScale);
        Opacity = Math.Clamp(settings.Current.Overlay(kind).Opacity, OverlaySettings.MinOpacity, 1);
        HistorySeconds = Math.Clamp(settings.Current.InputTraceSeconds, 1, F1Telemetry.Core.Engine.InputHistory.MaxWindowSeconds);
        Reference = settings.Current.SectorBoxReference;
        GapMode = settings.Current.TowerGap;
        DamagePopup = settings.Current.DamagePopup;
        DamagePopupSeconds = Math.Clamp(settings.Current.DamagePopupSeconds, 1, 60);
        overlays.StateChanged += UpdatePosition;
        UpdatePosition();
    }

    public OverlayKind Kind { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<OverlayVisibility> Modes { get; } = Enum.GetValues<OverlayVisibility>();
    public double MinScale => OverlaySettings.MinScale;
    public double MaxScale => OverlaySettings.MaxScale;

    [ObservableProperty] public partial OverlayVisibility Mode { get; set; }
    [ObservableProperty] public partial string Position { get; set; } = "";

    /// <summary>Overlay size multiplier (1 = design size), applied live while the settings page is open.</summary>
    [ObservableProperty] public partial double Scale { get; set; } = 1;

    public string ScaleText => $"{Scale * 100:0}%";

    partial void OnModeChanged(OverlayVisibility value)
    {
        _settings.Current.Overlay(Kind).Mode = value;
        _settings.Save();
        _overlays.Refresh();
    }

    partial void OnScaleChanged(double value)
    {
        _overlays.SetScale(Kind, value);
        OnPropertyChanged(nameof(ScaleText));
        _settings.SaveSoon();
    }

    public double MinOpacity => OverlaySettings.MinOpacity;

    /// <summary>Overlay content opacity (0.2–1), applied live.</summary>
    [ObservableProperty] public partial double Opacity { get; set; } = 1;

    public string OpacityText => $"{Opacity * 100:0}%";

    partial void OnOpacityChanged(double value)
    {
        _overlays.SetOpacity(Kind, value);
        OnPropertyChanged(nameof(OpacityText));
        _settings.SaveSoon();
    }

    [RelayCommand]
    private void ResetOpacity() => Opacity = 1;

    /// <summary>Only the input trace has a history length.</summary>
    public bool HasHistory => Kind == OverlayKind.Inputs;

    /// <summary>Seconds of pedal history shown by the input trace.</summary>
    [ObservableProperty] public partial double HistorySeconds { get; set; } = 6;

    partial void OnHistorySecondsChanged(double value)
    {
        if (HasHistory)
        {
            _overlays.SetInputTraceSeconds(value);
            _settings.SaveSoon();
        }
    }

    /// <summary>Only the sector box has a reference lap.</summary>
    public bool HasReference => Kind == OverlayKind.SectorBox;

    [ObservableProperty] public partial SectorReference Reference { get; set; }
    public bool IsSessionBestReference => Reference == SectorReference.SessionBest;
    public bool IsPersonalBestReference => Reference == SectorReference.PersonalBest;

    partial void OnReferenceChanged(SectorReference value)
    {
        OnPropertyChanged(nameof(IsSessionBestReference));
        OnPropertyChanged(nameof(IsPersonalBestReference));
        if (HasReference && _settings.Current.SectorBoxReference != value)
        {
            _settings.Current.SectorBoxReference = value;
            _settings.Save();
        }
    }

    [RelayCommand]
    private void SetReference(SectorReference reference) => Reference = reference;

    /// <summary>Only the timing tower has a gap mode.</summary>
    public bool HasGapMode => Kind == OverlayKind.TimingTower;

    [ObservableProperty] public partial TowerGapMode GapMode { get; set; }
    public bool IsGapToMe => GapMode == TowerGapMode.GapToMe;
    public bool IsInterval => GapMode == TowerGapMode.Interval;

    partial void OnGapModeChanged(TowerGapMode value)
    {
        OnPropertyChanged(nameof(IsGapToMe));
        OnPropertyChanged(nameof(IsInterval));
        if (HasGapMode && _settings.Current.TowerGap != value)
        {
            _settings.Current.TowerGap = value;
            _settings.Save();
        }
    }

    [RelayCommand]
    private void SetGapMode(TowerGapMode mode) => GapMode = mode;

    /// <summary>Only the conditions &amp; strategy overlay has the damage page.</summary>
    public bool HasDamagePage => Kind == OverlayKind.TrackConditions;

    /// <summary>Show the damage page automatically when the car takes new damage.</summary>
    [ObservableProperty] public partial bool DamagePopup { get; set; }

    /// <summary>Seconds the damage pop-up stays before returning to the strategy page.</summary>
    [ObservableProperty] public partial double DamagePopupSeconds { get; set; } = 8;

    partial void OnDamagePopupChanged(bool value)
    {
        if (HasDamagePage && _settings.Current.DamagePopup != value)
        {
            _settings.Current.DamagePopup = value;
            _settings.Save();
        }
    }

    partial void OnDamagePopupSecondsChanged(double value)
    {
        if (HasDamagePage)
        {
            _settings.Current.DamagePopupSeconds = Math.Clamp(value, 1, 60);
            _settings.SaveSoon();
        }
    }

    [RelayCommand]
    private void ResetScale() => Scale = 1;

    [RelayCommand]
    private void Preset(OverlayPreset preset)
    {
        _overlays.ApplyPreset(Kind, preset);
        _settings.Save();
    }

    private void UpdatePosition()
    {
        var p = _overlays.GetPosition(Kind);
        Position = $"x {p.X}, y {p.Y}";
    }
}

/// <summary>Hotkeys, overlay layout, ingest options and data locations.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private const string PadPrefix = "pad:";

    private readonly HotkeyService _hotkeys;
    private readonly GamepadService _gamepads;
    private readonly AppPaths _paths;
    private readonly OverlayManager _overlays;
    private string? _capturingFor;

    public SettingsViewModel(SettingsService settings, HotkeyService hotkeys, GamepadService gamepads, OverlayManager overlays, AppPaths paths)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _gamepads = gamepads;
        gamepads.Captured += OnPadCaptured;
        gamepads.ControllersChanged += RefreshPadTexts;
        _paths = paths;
        _overlays = overlays;
        var s = settings.Current;
        UdpPort = s.UdpPort;
        ListenOnStartup = s.ListenOnStartup;
        TyreWearLimit = s.TyreWearLimitPercent;
        CaptureRawPackets = s.CaptureRawPackets;
        StartStopHotkey = s.StartStopHotkey;
        ToggleOverlaysHotkey = s.ToggleOverlaysHotkey;
        StrategyPageHotkey = s.StrategyPageHotkey;
        StrategyPageUdpAction = Math.Clamp(s.StrategyPageUdpAction, 0, F1Telemetry.Protocol.Packets.EventPacket.UdpActionCount);
        RefreshPadTexts();

        Overlays =
        [
            new(OverlayKind.LapTiming, "Lap timing", "Recent laps with sector times, colours and delta to best.", settings, overlays),
            new(OverlayKind.Delta, "Lap / sector delta", "Pops up at each sector split with the delta to your best lap.", settings, overlays),
            new(OverlayKind.Radar, "Proximity radar", "Nearby cars and side-by-side warnings; hidden when nobody is close.", settings, overlays),
            new(OverlayKind.TrackConditions, "Conditions & strategy", "Weather forecast, safety car, tyre life and fuel range. Second page (hotkey): car damage from above.", settings, overlays),
            new(OverlayKind.Inputs, "Input trace", "Throttle and brake history on a 0–100 % scale, updated every frame.", settings, overlays),
            new(OverlayKind.SectorBox, "Sector box", "TV-style qualifying box: sector colours, lap time, and each sector's time and gap to the reference lap.", settings, overlays),
            new(OverlayKind.TimingTower, "Timing tower", "The six cars around you: gap, tyre and age, battery charge, penalties and warnings.", settings, overlays),
        ];
    }

    public ObservableCollection<OverlaySettingsViewModel> Overlays { get; }

    /// <summary>Shows every overlay with preview data so it can be dragged and resized. Off until switched on.</summary>
    [ObservableProperty] public partial bool PreviewOverlays { get; set; }

    partial void OnPreviewOverlaysChanged(bool value) => _overlays.EditMode = value;

    /// <summary>Set by the view: the screen area of the preview toggle, which previewed overlays must keep clear of.</summary>
    public Func<PixelRect?>? PreviewToggleArea
    {
        get => _overlays.KeepClearArea;
        set => _overlays.KeepClearArea = value;
    }
    public string DataDirectory => _paths.DataDirectory;
    public string DatabasePath => _paths.DatabasePath;

    [ObservableProperty] public partial int UdpPort { get; set; }
    [ObservableProperty] public partial bool ListenOnStartup { get; set; }
    [ObservableProperty] public partial double TyreWearLimit { get; set; }
    [ObservableProperty] public partial bool CaptureRawPackets { get; set; }
    [ObservableProperty] public partial string StartStopHotkey { get; set; }
    [ObservableProperty] public partial string ToggleOverlaysHotkey { get; set; }
    [ObservableProperty] public partial string StrategyPageHotkey { get; set; }

    /// <summary>"Off", then the game's bindable "UDP Action 1" … "UDP Action 12" (index = action number).</summary>
    public IReadOnlyList<string> WheelButtons { get; } =
        ["Off", .. Enumerable.Range(1, F1Telemetry.Protocol.Packets.EventPacket.UdpActionCount).Select(i => $"UDP Action {i}")];

    /// <summary>Wheel / pad button (UDP Action number, 0 = off) that switches the strategy / damage page. Saved at once.</summary>
    [ObservableProperty] public partial int StrategyPageUdpAction { get; set; }

    partial void OnStrategyPageUdpActionChanged(int value)
    {
        if (value >= 0 && _settings.Current.StrategyPageUdpAction != value)
        {
            _settings.Current.StrategyPageUdpAction = value;
            _settings.Save();
        }
    }
    [ObservableProperty] public partial string CaptureHint { get; set; } = "";
    [ObservableProperty] public partial string PadCaptureHint { get; set; } = "";
    [ObservableProperty] public partial string SaveStatus { get; set; } = "";

    /// <summary>Controller bindings as button names of the connected pad, e.g. "Touchpad + D-pad Right".</summary>
    [ObservableProperty] public partial string StartStopPadText { get; set; } = "";
    [ObservableProperty] public partial string ToggleOverlaysPadText { get; set; } = "";
    [ObservableProperty] public partial string StrategyPagePadText { get; set; } = "";

    /// <summary>Which controllers are connected, or why none can be read.</summary>
    [ObservableProperty] public partial string ControllerStatus { get; set; } = "";

    public bool IsCapturingHotkey => _capturingFor is not null;

    private void RefreshPadTexts()
    {
        var s = _settings.Current;
        string Text(string binding) => PadBinding.TryParse(binding)?.Describe(_gamepads.Family) ?? "—";
        StartStopPadText = Text(s.StartStopPadButton);
        ToggleOverlaysPadText = Text(s.ToggleOverlaysPadButton);
        StrategyPagePadText = Text(s.StrategyPagePadButton);
        ControllerStatus = _gamepads switch
        {
            { Error: { } error } => $"Controller input unavailable: {error}",
            { Controllers.Count: > 0 } => "Connected: " + string.Join(", ", _gamepads.Controllers),
            _ => "No controller found. Connect it by USB or Bluetooth (it shows up here as soon as it is detected).",
        };
    }

    /// <summary>Waits for a controller press (one button, or hold one and press another) for the given action.</summary>
    [RelayCommand]
    private void BeginPadCapture(string target)
    {
        EndCapture();
        _capturingFor = PadPrefix + target;
        _gamepads.BeginCapture();
        PadCaptureHint = "Press a controller button, or hold one and press another for a combo (Esc to cancel)…";
    }

    [RelayCommand]
    private void ClearPadBinding(string target)
    {
        EndCapture();
        SetPadBinding(target, "");
    }

    private void OnPadCaptured(PadBinding binding)
    {
        if (_capturingFor?.StartsWith(PadPrefix, StringComparison.Ordinal) != true)
        {
            return;
        }

        var target = _capturingFor[PadPrefix.Length..];
        EndCapture();
        SetPadBinding(target, binding.ToString());
    }

    private void SetPadBinding(string target, string binding)
    {
        var s = _settings.Current;
        switch (target)
        {
            case "startStop":
                s.StartStopPadButton = binding;
                break;
            case "strategyPage":
                s.StrategyPagePadButton = binding;
                break;
            default:
                s.ToggleOverlaysPadButton = binding;
                break;
        }

        _settings.Save();
        RefreshPadTexts();
    }

    [RelayCommand]
    private void BeginCapture(string target)
    {
        EndCapture();
        _capturingFor = target;
        _hotkeys.Suspended = true;
        CaptureHint = "Press the new key combination (Esc to cancel)…";
    }

    /// <summary>Called by the view's KeyDown handler while capturing.</summary>
    public bool TryCapture(Avalonia.Input.Key key, KeyModifiers modifiers)
    {
        if (_capturingFor is null)
        {
            return false;
        }

        if (key == Key.Escape)
        {
            EndCapture();
            return true;
        }

        // Waiting for a controller button: the keyboard only cancels.
        if (_capturingFor.StartsWith(PadPrefix, StringComparison.Ordinal) || Hotkey.FromAvalonia(key, modifiers) is not { } hotkey)
        {
            return true;
        }

        switch (_capturingFor)
        {
            case "startStop":
                StartStopHotkey = hotkey.ToString();
                break;
            case "strategyPage":
                StrategyPageHotkey = hotkey.ToString();
                break;
            default:
                ToggleOverlaysHotkey = hotkey.ToString();
                break;
        }

        EndCapture();
        Save();
        return true;
    }

    private void EndCapture()
    {
        _capturingFor = null;
        _hotkeys.Suspended = false;
        _gamepads.CancelCapture();
        CaptureHint = "";
        PadCaptureHint = "";
    }

    [RelayCommand]
    private void Save()
    {
        var s = _settings.Current;
        var needsRestart = s.UdpPort != UdpPort || Math.Abs(s.TyreWearLimitPercent - TyreWearLimit) > 0.01;
        s.UdpPort = UdpPort;
        s.ListenOnStartup = ListenOnStartup;
        s.TyreWearLimitPercent = TyreWearLimit;
        s.CaptureRawPackets = CaptureRawPackets;
        s.StartStopHotkey = StartStopHotkey;
        s.ToggleOverlaysHotkey = ToggleOverlaysHotkey;
        s.StrategyPageHotkey = StrategyPageHotkey;
        _settings.Save();
        SaveStatus = needsRestart ? $"Saved {DateTime.Now:T}. Restart the source (UDP port) or the app (wear limit) to apply." : $"Saved {DateTime.Now:T}.";
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true });
        }
        catch (Exception)
        {
            SaveStatus = $"Data folder: {DataDirectory}";
        }
    }
}
