using Avalonia.Input;
using AvKey = Avalonia.Input.Key;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SharpHook;
using SharpHook.Data;

namespace F1Telemetry.App.Services;

/// <summary>
/// System-wide hotkeys (work while the game has focus) via SharpHook/libuiohook on Windows, macOS and X11.
/// macOS requires the Accessibility permission; Wayland does not allow global hooks.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private SimpleGlobalHook? _hook;
    private Hotkey? _startStop;
    private Hotkey? _toggleOverlays;
    private Hotkey? _strategyPage;

    public HotkeyService(SettingsService settings, ILogger<HotkeyService> log)
    {
        _settings = settings;
        _log = log;
        settings.Changed += _ => Reload();
    }

    public event Action? StartStopPressed;
    public event Action? ToggleOverlaysPressed;
    public event Action? StrategyPagePressed;

    /// <summary>Suspends dispatch (while the settings page is capturing a new shortcut).</summary>
    public bool Suspended { get; set; }

    public void Start()
    {
        Reload();
        try
        {
            _hook = new SimpleGlobalHook();
            _hook.KeyPressed += OnKeyPressed;
            _ = _hook.RunAsync().ContinueWith(t => _log.LogWarning(t.Exception, "Global hotkey hook stopped"), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Global hotkeys unavailable on this platform/session");
        }
    }

    private void Reload()
    {
        _startStop = Hotkey.TryParse(_settings.Current.StartStopHotkey);
        _toggleOverlays = Hotkey.TryParse(_settings.Current.ToggleOverlaysHotkey);
        _strategyPage = Hotkey.TryParse(_settings.Current.StrategyPageHotkey);
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        if (Suspended)
        {
            return;
        }

        var key = e.Data.KeyCode;
        var mask = e.RawEvent.Mask;
        if (_startStop?.Matches(key, mask) == true)
        {
            Dispatcher.UIThread.Post(() => StartStopPressed?.Invoke());
        }
        else if (_toggleOverlays?.Matches(key, mask) == true)
        {
            Dispatcher.UIThread.Post(() => ToggleOverlaysPressed?.Invoke());
        }
        else if (_strategyPage?.Matches(key, mask) == true)
        {
            Dispatcher.UIThread.Post(() => StrategyPagePressed?.Invoke());
        }
    }

    public void Dispose() => _hook?.Dispose();
}

/// <summary>Shortcut such as "Ctrl+Alt+S", shared by the settings UI (Avalonia keys) and the global hook (SharpHook keys).</summary>
public sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, bool Meta, string Key)
{
    public static Hotkey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = parts[..^1].Select(p => p.ToLowerInvariant()).ToHashSet();
        return new Hotkey(
            mods.Contains("ctrl") || mods.Contains("commandorcontrol"),
            mods.Contains("alt"),
            mods.Contains("shift"),
            mods.Contains("meta") || mods.Contains("cmd") || mods.Contains("command"),
            parts[^1]);
    }

    /// <summary>Builds a shortcut from an Avalonia key event; null for bare modifier presses.</summary>
    public static Hotkey? FromAvalonia(AvKey key, KeyModifiers modifiers)
    {
        if (key is AvKey.LeftCtrl or AvKey.RightCtrl or AvKey.LeftAlt or AvKey.RightAlt or AvKey.LeftShift or AvKey.RightShift or AvKey.LWin or AvKey.RWin)
        {
            return null;
        }

        var name = key switch
        {
            >= AvKey.D0 and <= AvKey.D9 => ((int)(key - AvKey.D0)).ToString(),
            >= AvKey.NumPad0 and <= AvKey.NumPad9 => "NumPad" + (int)(key - AvKey.NumPad0),
            _ => key.ToString(),
        };
        return new Hotkey(modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Alt),
            modifiers.HasFlag(KeyModifiers.Shift), modifiers.HasFlag(KeyModifiers.Meta), name);
    }

    public bool Matches(KeyCode code, EventMask mask) =>
        ToKeyCode() == code
        && Ctrl == ((mask & EventMask.Ctrl) != 0)
        && Alt == ((mask & EventMask.Alt) != 0)
        && Shift == ((mask & EventMask.Shift) != 0)
        && Meta == ((mask & EventMask.Meta) != 0);

    private KeyCode? ToKeyCode()
    {
        var candidate = Key switch
        {
            "Space" => "VcSpace",
            "Return" or "Enter" => "VcEnter",
            _ when Key.StartsWith("NumPad", StringComparison.Ordinal) => "VcNumPad" + Key[6..],
            _ => "Vc" + Key,
        };
        return Enum.TryParse<KeyCode>(candidate, ignoreCase: true, out var code) ? code : null;
    }

    public override string ToString() =>
        string.Join("+", new[] { Ctrl ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Meta ? "Meta" : null, Key }.Where(p => p is not null));
}
