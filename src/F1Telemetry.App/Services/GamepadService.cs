using Avalonia.Threading;
using F1Telemetry.Core.Input;
using Microsoft.Extensions.Logging;
using SDL;
using static SDL.SDL3;

namespace F1Telemetry.App.Services;

/// <summary>
/// Controller buttons as hotkeys, read straight from the pad with SDL3 (DualSense over USB or Bluetooth, Xbox, Switch, …)
/// on Windows, macOS and Linux. Works while the game has focus. SDL only reads: it never changes the pad's mode,
/// lights or rumble, so the game keeps full control of the controller.
/// </summary>
public sealed class GamepadService : IDisposable
{
    private const int StartStopAction = 0;
    private const int ToggleOverlaysAction = 1;
    private const int StrategyPageAction = 2;

    private readonly SettingsService _settings;
    private readonly ILogger _log;
    private readonly PadBindingMatcher _matcher = new();
    private readonly Lock _gate = new();
    private Thread? _thread;
    private volatile bool _stopping;

    public GamepadService(SettingsService settings, ILogger<GamepadService> log)
    {
        _settings = settings;
        _log = log;
        settings.Changed += _ => Reload();
    }

    public event Action? StartStopPressed;
    public event Action? ToggleOverlaysPressed;
    public event Action? StrategyPagePressed;

    /// <summary>A binding was captured (see <see cref="BeginCapture"/>). UI thread.</summary>
    public event Action<PadBinding>? Captured;

    /// <summary>A controller was connected or disconnected. UI thread.</summary>
    public event Action? ControllersChanged;

    /// <summary>Names of the connected controllers. UI thread.</summary>
    public IReadOnlyList<string> Controllers { get; private set; } = [];

    /// <summary>Button names to show: those of the controller connected or used last.</summary>
    public PadFamily Family { get; internal set; } = PadFamily.Generic;

    /// <summary>Set when controller support could not start (no SDL for this platform, no input access, …).</summary>
    public string? Error { get; private set; }

    public void Start()
    {
        Reload();
        _thread = new Thread(Run) { IsBackground = true, Name = "Gamepad input" };
        _thread.Start();
    }

    /// <summary>The next press (one button, or hold one and press another) becomes a binding instead of firing an action.</summary>
    public void BeginCapture()
    {
        lock (_gate)
        {
            _matcher.BeginCapture();
        }
    }

    public void CancelCapture()
    {
        lock (_gate)
        {
            _matcher.CancelCapture();
        }
    }

    private void Reload()
    {
        var s = _settings.Current;
        lock (_gate)
        {
            _matcher.Bindings =
            [
                PadBinding.TryParse(s.StartStopPadButton),
                PadBinding.TryParse(s.ToggleOverlaysPadButton),
                PadBinding.TryParse(s.StrategyPagePadButton),
            ];
        }
    }

    private unsafe void Run()
    {
        try
        {
            // Keep receiving buttons while the game, not this app, has focus.
            SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
            // Don't switch a DualSense / DualShock to enhanced reports or touch its lights: the game owns those.
            SDL_SetHint(SDL_HINT_JOYSTICK_ENHANCED_REPORTS, "0");
            SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS5_PLAYER_LED, "0");
            if (!SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD))
            {
                Fail(SDL_GetError() ?? "SDL_Init failed");
                return;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Fail(ex.Message);
            return;
        }

        var pads = new Dictionary<SDL_JoystickID, (nint Handle, string Name, PadFamily Family)>();
        try
        {
            SDL_Event e;
            while (!_stopping)
            {
                if (!SDL_WaitEventTimeout(&e, 100))
                {
                    continue;
                }

                do
                {
                    Handle(ref e, pads);
                }
                while (!_stopping && SDL_PollEvent(&e));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Gamepad input stopped");
        }
        finally
        {
            foreach (var pad in pads.Values)
            {
                SDL_CloseGamepad((SDL_Gamepad*)pad.Handle);
            }

            SDL_Quit();
        }
    }

    private unsafe void Handle(ref SDL_Event e, Dictionary<SDL_JoystickID, (nint Handle, string Name, PadFamily Family)> pads)
    {
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
            {
                var id = e.gdevice.which;
                var pad = SDL_OpenGamepad(id);
                if (pad is null)
                {
                    _log.LogWarning("Could not open controller: {Error}", SDL_GetError());
                    return;
                }

                var name = SDL_GetGamepadName(pad) ?? "Controller";
                var family = ToFamily(SDL_GetGamepadType(pad));
                pads[id] = ((nint)pad, name, family);
                _log.LogInformation("Controller connected: {Name} ({Family})", name, family);
                PublishControllers(pads, family);
                break;
            }

            case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
            {
                var id = e.gdevice.which;
                if (pads.Remove(id, out var pad))
                {
                    SDL_CloseGamepad((SDL_Gamepad*)pad.Handle);
                    _log.LogInformation("Controller disconnected: {Name}", pad.Name);
                    lock (_gate)
                    {
                        _matcher.Reset();
                    }

                    PublishControllers(pads, pads.Count > 0 ? pads.Values.Last().Family : null);
                }

                break;
            }

            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
            {
                // PadButton mirrors SDL's button order up to the touchpad; the rarer MISC2–6 are not bindable.
                if (e.gbutton.button > (byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD)
                {
                    return;
                }

                var button = (PadButton)e.gbutton.button;
                var fired = new List<int>();
                PadBinding? captured;
                lock (_gate)
                {
                    captured = e.gbutton.down ? _matcher.Down(button, fired) : _matcher.Up(button, fired);
                }

                var family = pads.TryGetValue(e.gbutton.which, out var pad) ? pad.Family : (PadFamily?)null;
                if (captured is not null || fired.Count > 0)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Family = family ?? Family;
                        if (captured is not null)
                        {
                            Captured?.Invoke(captured);
                        }

                        foreach (var action in fired)
                        {
                            Raise(action);
                        }
                    });
                }

                break;
            }
        }
    }

    private void Raise(int action)
    {
        switch (action)
        {
            case StartStopAction:
                StartStopPressed?.Invoke();
                break;
            case ToggleOverlaysAction:
                ToggleOverlaysPressed?.Invoke();
                break;
            case StrategyPageAction:
                StrategyPagePressed?.Invoke();
                break;
        }
    }

    private void PublishControllers(Dictionary<SDL_JoystickID, (nint Handle, string Name, PadFamily Family)> pads, PadFamily? family)
    {
        var names = pads.Values.Select(p => p.Name).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            Controllers = names;
            Family = family ?? Family;
            ControllersChanged?.Invoke();
        });
    }

    private void Fail(string error)
    {
        _log.LogWarning("Controller input unavailable: {Error}", error);
        Dispatcher.UIThread.Post(() =>
        {
            Error = error;
            ControllersChanged?.Invoke();
        });
    }

    private static PadFamily ToFamily(SDL_GamepadType type) => type switch
    {
        SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5 => PadFamily.DualSense,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_PS3 or SDL_GamepadType.SDL_GAMEPAD_TYPE_PS4 => PadFamily.PlayStation,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOX360 or SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOXONE => PadFamily.Xbox,
        SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_PRO
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_LEFT
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_RIGHT
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_PAIR => PadFamily.Nintendo,
        _ => PadFamily.Generic,
    };

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
