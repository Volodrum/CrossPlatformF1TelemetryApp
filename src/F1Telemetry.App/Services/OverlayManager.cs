using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using F1Telemetry.App.Platform;
using F1Telemetry.App.ViewModels;
using F1Telemetry.App.Views.Overlays;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Recording;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.App.Services;

public enum OverlayPreset
{
    TopLeft,
    TopRight,
    LeftCenter,
    TopCenter,
    Center,
    RightCenter,
    BottomCenter,
}

/// <summary>
/// Owns the HUD windows and their view models. Visibility policy (same as the original app):
/// visible = previewing || (globally enabled &amp;&amp; (mode == Always || (mode == Session &amp;&amp; recording))).
/// </summary>
public sealed class OverlayManager
{
    // An overlay counts as "dropped" once it has stopped moving for this long.
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(400);

    private readonly SettingsService _settings;
    private readonly LiveDataHub _hub;
    private readonly RecordingCoordinator _recorder;
    private readonly Dictionary<OverlayKind, OverlayWindow> _windows = [];
    private readonly Dictionary<OverlayKind, DateTime> _lastMoved = [];
    private readonly Dictionary<OverlayKind, OverlayViewModelBase> _viewModels;
    private readonly DispatcherTimer _keepClearTimer;
    private bool _editMode;
    private bool _globallyVisible = true;

    public OverlayManager(SettingsService settings, LiveDataHub hub, TelemetryRuntime runtime)
    {
        _settings = settings;
        _hub = hub;
        _recorder = runtime.Recorder;
        Inputs = new InputTraceOverlayViewModel(hub.Inputs);
        _viewModels = new()
        {
            [OverlayKind.LapTiming] = LapTiming,
            [OverlayKind.Delta] = Delta,
            [OverlayKind.Radar] = Radar,
            [OverlayKind.TrackConditions] = TrackConditions,
            [OverlayKind.Inputs] = Inputs,
            [OverlayKind.SectorBox] = SectorBox,
            [OverlayKind.TimingTower] = TimingTower,
        };
        settings.Changed += _ => ApplySettings();
        // Frequent enough that the hole follows the main window and a dragged overlay without visible lag.
        _keepClearTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => KeepClear());
    }

    public LapTimingOverlayViewModel LapTiming { get; } = new();
    public DeltaOverlayViewModel Delta { get; } = new();
    public RadarOverlayViewModel Radar { get; } = new();
    public TrackConditionsOverlayViewModel TrackConditions { get; } = new();
    public InputTraceOverlayViewModel Inputs { get; }
    public SectorBoxOverlayViewModel SectorBox { get; } = new();
    public TimingTowerOverlayViewModel TimingTower { get; } = new();

    public static IReadOnlyDictionary<OverlayKind, Size> Sizes { get; } = new Dictionary<OverlayKind, Size>
    {
        [OverlayKind.LapTiming] = new(560, 292),
        [OverlayKind.Delta] = new(280, 172),
        [OverlayKind.Radar] = new(240, 240),
        [OverlayKind.TrackConditions] = new(360, 340),
        [OverlayKind.Inputs] = new(552, 220),
        [OverlayKind.SectorBox] = new(400, 116),
        [OverlayKind.TimingTower] = new(420, 290),
    };

    public event Action? StateChanged;

    /// <summary>
    /// Screen area (physical pixels) of the settings page's preview toggle, which must stay visible and clickable
    /// through previewed overlays: they are topmost, so one dragged onto it would otherwise hide it.
    /// </summary>
    public Func<PixelRect?>? KeepClearArea { get; set; }

    public bool GloballyVisible
    {
        get => _globallyVisible;
        set
        {
            _globallyVisible = value;
            Refresh();
            StateChanged?.Invoke();
        }
    }

    /// <summary>Preview mode (toggled on the settings page): every overlay shown with preview data and draggable.</summary>
    public bool EditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value)
            {
                return;
            }

            _editMode = value;
            Radar.Preview = value;
            foreach (var vm in _viewModels.Values)
            {
                if (value)
                {
                    vm.LoadPreview();
                }
                else
                {
                    // Drop the preview values so no mock data stays on screen until the next live update.
                    vm.Reset();
                }
            }

            if (!value)
            {
                ApplyLatest();
            }

            foreach (var window in _windows.Values)
            {
                window.EditMode = value;
            }

            if (value)
            {
                _keepClearTimer.Start();
            }
            else
            {
                _keepClearTimer.Stop();
                KeepClear(); // edit mode is off now: clears the holes
                _settings.Save();
            }

            Refresh();
            if (value)
            {
                // Shown windows get their size on the next layout pass; check once that has happened.
                Dispatcher.UIThread.Post(KeepClear, DispatcherPriority.Background);
            }
        }
    }

    public void Initialize()
    {
        Inputs.WindowSeconds = Math.Clamp(_settings.Current.InputTraceSeconds, 1, InputHistory.MaxWindowSeconds);

        Create(OverlayKind.LapTiming, new LapTimingOverlayView { DataContext = LapTiming });
        Create(OverlayKind.Delta, new DeltaOverlayView { DataContext = Delta });
        // The radar is a square that fills whatever it gets, so give it its design size (the window scales it).
        Create(OverlayKind.Radar, new RadarOverlayView { DataContext = Radar, Width = Sizes[OverlayKind.Radar].Width, Height = Sizes[OverlayKind.Radar].Height });
        Create(OverlayKind.TrackConditions, new TrackConditionsOverlayView { DataContext = TrackConditions });
        Create(OverlayKind.Inputs, new InputTraceOverlayView { DataContext = Inputs });
        Create(OverlayKind.SectorBox, new SectorBoxOverlayView { DataContext = SectorBox });
        Create(OverlayKind.TimingTower, new TimingTowerOverlayView { DataContext = TimingTower });
        ApplySettings();

        _hub.LapTimingUpdated += t => { if (!_editMode) LapTiming.Update(t); };
        _hub.DeltaUpdated += d => { if (!_editMode) Delta.Update(d); };
        _hub.ButtonsPressed += buttons =>
        {
            if ((buttons & EventPacket.UdpActionMask(_settings.Current.StrategyPageUdpAction)) != 0)
            {
                ToggleStrategyPage();
            }
        };
        _hub.DamageTaken += () =>
        {
            if (!_editMode && _settings.Current.DamagePopup)
            {
                TrackConditions.PopUpDamage(_settings.Current.DamagePopupSeconds);
            }
        };
        _hub.Tick += () =>
        {
            if (!_editMode)
            {
                ApplyLatest(includeTiming: false);
            }
        };
        _hub.SessionChanged += _ =>
        {
            if (!_editMode)
            {
                Delta.Reset();
                SectorBox.Reset();
                TimingTower.Reset();
            }
        };
        _hub.RecordingChanged += _ => Refresh();
        Refresh();
    }

    /// <summary>Pushes the latest live values (not the discrete delta pop-up) into the overlays.</summary>
    private void ApplyLatest(bool includeTiming = true)
    {
        if (includeTiming && _hub.LapTiming is { } timing)
        {
            LapTiming.Update(timing);
        }

        if (_hub.Radar is { } radar)
        {
            Radar.Update(radar);
        }

        if (_hub.Conditions is { } conditions)
        {
            TrackConditions.Update(conditions);
        }

        if (_hub.Strategy is { } strategy)
        {
            TrackConditions.Update(strategy);
        }

        if (_hub.Damage is { } damage)
        {
            TrackConditions.Update(damage);
        }

        var field = _hub.Field;
        if (field is not null)
        {
            TimingTower.Update(field);
        }

        if (_hub.SectorBox is { } box)
        {
            SectorBox.Update(box, field?.Player?.TeamColour, SessionTypes.IsQualifying(_hub.Session?.SessionType ?? 0));
        }
    }

    /// <summary>Hotkey: flips the conditions &amp; strategy overlay between its strategy and damage pages.</summary>
    public void ToggleStrategyPage() => TrackConditions.TogglePage();

    /// <summary>Pushes overlay options from the settings into the view models and redraws with them.</summary>
    private void ApplySettings()
    {
        var s = _settings.Current;
        SectorBox.Reference = s.SectorBoxReference;
        TimingTower.GapMode = s.TowerGap;
        if (_editMode)
        {
            SectorBox.LoadPreview();
            TimingTower.LoadPreview();
        }
        else
        {
            ApplyLatest(includeTiming: false);
        }
    }

    /// <summary>Resizes an overlay live; the value is saved when the preview ends.</summary>
    public void SetScale(OverlayKind kind, double scale)
    {
        scale = Math.Clamp(scale, OverlaySettings.MinScale, OverlaySettings.MaxScale);
        _settings.Current.Overlay(kind).Scale = scale;
        if (_windows.TryGetValue(kind, out var window))
        {
            window.Scale = scale;
        }
    }

    /// <summary>Changes an overlay's opacity live (0.2–1).</summary>
    public void SetOpacity(OverlayKind kind, double opacity)
    {
        opacity = Math.Clamp(opacity, OverlaySettings.MinOpacity, 1);
        _settings.Current.Overlay(kind).Opacity = opacity;
        if (_windows.TryGetValue(kind, out var window))
        {
            window.ContentOpacity = opacity;
        }
    }

    /// <summary>Seconds of pedal history on the input overlay, applied live.</summary>
    public void SetInputTraceSeconds(double seconds)
    {
        seconds = Math.Clamp(seconds, 1, InputHistory.MaxWindowSeconds);
        _settings.Current.InputTraceSeconds = seconds;
        Inputs.WindowSeconds = seconds;
    }

    public void Refresh()
    {
        foreach (var (kind, window) in _windows)
        {
            var mode = _settings.Current.Overlay(kind).Mode;
            var visible = _editMode || (_globallyVisible && (mode == OverlayVisibility.Always || (mode == OverlayVisibility.Session && _recorder.IsRecording)));
            if (visible && !window.IsVisible)
            {
                window.Show();
                window.EditMode = _editMode;
            }
            else if (!visible && window.IsVisible)
            {
                window.Hide();
            }
        }
    }

    public PixelPoint GetPosition(OverlayKind kind) => _windows.TryGetValue(kind, out var w) ? w.Position : default;

    public void ApplyPreset(OverlayKind kind, OverlayPreset preset)
    {
        if (!_windows.TryGetValue(kind, out var window))
        {
            return;
        }

        var position = PresetPosition(window, kind, preset);
        window.Position = position;
        Store(kind, position);
    }

    public void Close()
    {
        _keepClearTimer.Stop();

        // Also covers a debounced size/history change that hasn't been written yet.
        _settings.Save();

        foreach (var window in _windows.Values)
        {
            window.Close();
        }
    }

    /// <summary>
    /// Keeps the preview toggle (<see cref="KeepClearArea"/>) usable while overlays are previewed: every overlay gets
    /// a see-through, click-through hole where it covers the toggle. Where the OS can't pass clicks through part of
    /// a window (macOS), an overlay that settles on the toggle is moved just below it instead.
    /// </summary>
    private void KeepClear()
    {
        var area = _editMode ? KeepClearArea?.Invoke() : null;
        if (OverlayInterop.SupportsInputHoles)
        {
            foreach (var window in _windows.Values)
            {
                window.Hole = area;
            }

            return;
        }

        if (area is not { } keepArea)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var (kind, window) in _windows)
        {
            if (!window.IsVisible || (_lastMoved.TryGetValue(kind, out var moved) && now - moved < SettleTime))
            {
                continue;
            }

            var gap = (int)(12 * window.RenderScaling);
            var keep = new PixelRect(keepArea.X - gap, keepArea.Y - gap, keepArea.Width + 2 * gap, keepArea.Height + 2 * gap);
            var size = PixelSize.FromSize(window.ClientSize, window.RenderScaling);
            var rect = new PixelRect(window.Position, size);
            if (!rect.Intersects(keep))
            {
                continue;
            }

            var work = (window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
            var y = keep.Bottom + size.Height <= work.Bottom ? keep.Bottom : Math.Max(work.Y, keep.Y - size.Height);
            window.Position = new PixelPoint(rect.X, y);
            Store(kind, window.Position);
        }
    }

    private void Create(OverlayKind kind, Control view)
    {
        var saved = _settings.Current.Overlay(kind);
        saved.Scale = Math.Clamp(saved.Scale, OverlaySettings.MinScale, OverlaySettings.MaxScale);
        saved.Opacity = Math.Clamp(saved.Opacity, OverlaySettings.MinOpacity, 1);
        var window = new OverlayWindow(kind, view, saved.Scale, saved.Opacity);
        window.Position = saved is { X: { } x, Y: { } y } ? new PixelPoint(x, y) : PresetPosition(window, kind, DefaultPreset(kind));
        window.PositionChanged += (_, e) =>
        {
            if (_editMode)
            {
                _lastMoved[kind] = DateTime.UtcNow;
                Store(kind, e.Point);
            }
        };
        _windows[kind] = window;
    }

    private void Store(OverlayKind kind, PixelPoint position)
    {
        var overlay = _settings.Current.Overlay(kind);
        overlay.X = position.X;
        overlay.Y = position.Y;
        StateChanged?.Invoke();
    }

    private static OverlayPreset DefaultPreset(OverlayKind kind) => kind switch
    {
        OverlayKind.LapTiming => OverlayPreset.LeftCenter,
        OverlayKind.Delta => OverlayPreset.TopCenter,
        OverlayKind.Radar => OverlayPreset.Center,
        OverlayKind.Inputs => OverlayPreset.BottomCenter,
        OverlayKind.SectorBox => OverlayPreset.TopRight,
        OverlayKind.TimingTower => OverlayPreset.TopLeft,
        _ => OverlayPreset.RightCenter,
    };

    private static PixelPoint PresetPosition(OverlayWindow window, OverlayKind kind, OverlayPreset preset)
    {
        var screen = window.Screens.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scaling = screen?.Scaling ?? 1.0;
        var size = Sizes[kind];
        var w = (int)(size.Width * window.Scale * scaling);
        var h = (int)(size.Height * window.Scale * scaling);
        var margin = (int)(20 * scaling);
        return preset switch
        {
            OverlayPreset.TopLeft => new PixelPoint(area.X + margin, area.Y + margin),
            OverlayPreset.TopRight => new PixelPoint(area.Right - w - margin, area.Y + margin),
            OverlayPreset.LeftCenter => new PixelPoint(area.X + margin, area.Y + (area.Height - h) / 2),
            OverlayPreset.TopCenter => new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (int)(150 * scaling)),
            OverlayPreset.Center => new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (int)(380 * scaling)),
            OverlayPreset.BottomCenter => new PixelPoint(area.X + (area.Width - w) / 2, area.Bottom - h - (int)(60 * scaling)),
            _ => new PixelPoint(area.Right - w - margin, area.Y + (area.Height - h) / 2),
        };
    }
}
