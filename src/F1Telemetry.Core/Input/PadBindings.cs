namespace F1Telemetry.Core.Input;

/// <summary>Controller buttons by position (same order as SDL's gamepad buttons), so a binding works on any pad.</summary>
public enum PadButton
{
    South,
    East,
    West,
    North,
    Back,
    Guide,
    Start,
    LeftStick,
    RightStick,
    LeftShoulder,
    RightShoulder,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    Misc1,
    RightPaddle1,
    LeftPaddle1,
    RightPaddle2,
    LeftPaddle2,
    Touchpad,
}

/// <summary>Which button names to show.</summary>
public enum PadFamily
{
    Generic,
    Xbox,
    PlayStation,
    DualSense,
    Nintendo,
}

/// <summary>One button, or a combo: hold <paramref name="Modifier"/> and press <paramref name="Button"/>. Saved as "Touchpad+DPadRight".</summary>
public sealed record PadBinding(PadButton? Modifier, PadButton Button)
{
    public static PadBinding? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || !Enum.TryParse<PadButton>(parts[^1], ignoreCase: true, out var button))
        {
            return null;
        }

        if (parts.Length == 1)
        {
            return new PadBinding(null, button);
        }

        return Enum.TryParse<PadButton>(parts[0], ignoreCase: true, out var modifier) && modifier != button
            ? new PadBinding(modifier, button)
            : null;
    }

    public string Describe(PadFamily family) =>
        Modifier is { } modifier ? $"{Name(modifier, family)} + {Name(Button, family)}" : Name(Button, family);

    public override string ToString() => Modifier is { } modifier ? $"{modifier}+{Button}" : Button.ToString();

    public static string Name(PadButton button, PadFamily family)
    {
        var playStation = family is PadFamily.PlayStation or PadFamily.DualSense;
        return button switch
        {
            PadButton.South => family switch { PadFamily.Xbox => "A", PadFamily.Nintendo => "B", _ when playStation => "Cross", _ => "A / Cross" },
            PadButton.East => family switch { PadFamily.Xbox => "B", PadFamily.Nintendo => "A", _ when playStation => "Circle", _ => "B / Circle" },
            PadButton.West => family switch { PadFamily.Xbox => "X", PadFamily.Nintendo => "Y", _ when playStation => "Square", _ => "X / Square" },
            PadButton.North => family switch { PadFamily.Xbox => "Y", PadFamily.Nintendo => "X", _ when playStation => "Triangle", _ => "Y / Triangle" },
            PadButton.Back => family switch { PadFamily.Xbox => "View", PadFamily.Nintendo => "−", PadFamily.DualSense => "Create", PadFamily.PlayStation => "Share", _ => "Back" },
            PadButton.Guide => family switch { PadFamily.Xbox => "Xbox", PadFamily.Nintendo => "Home", _ when playStation => "PS", _ => "Guide" },
            PadButton.Start => family switch { PadFamily.Xbox => "Menu", PadFamily.Nintendo => "+", _ when playStation => "Options", _ => "Start" },
            PadButton.LeftStick => playStation ? "L3" : "Left stick",
            PadButton.RightStick => playStation ? "R3" : "Right stick",
            PadButton.LeftShoulder => family switch { PadFamily.Xbox => "LB", PadFamily.Nintendo => "L", _ when playStation => "L1", _ => "LB / L1" },
            PadButton.RightShoulder => family switch { PadFamily.Xbox => "RB", PadFamily.Nintendo => "R", _ when playStation => "R1", _ => "RB / R1" },
            PadButton.DPadUp => "D-pad Up",
            PadButton.DPadDown => "D-pad Down",
            PadButton.DPadLeft => "D-pad Left",
            PadButton.DPadRight => "D-pad Right",
            PadButton.Misc1 => family switch { PadFamily.Xbox => "Share", PadFamily.Nintendo => "Capture", PadFamily.DualSense => "Mic", _ => "Misc" },
            // DualSense Edge: the paddles are the back buttons, paddle 2 the Fn buttons. Xbox Elite: P1–P4.
            PadButton.RightPaddle1 => family switch { PadFamily.Xbox => "P1", _ when playStation => "Right back", _ => "Right paddle 1" },
            PadButton.LeftPaddle1 => family switch { PadFamily.Xbox => "P3", _ when playStation => "Left back", _ => "Left paddle 1" },
            PadButton.RightPaddle2 => family switch { PadFamily.Xbox => "P2", _ when playStation => "Right Fn", _ => "Right paddle 2" },
            PadButton.LeftPaddle2 => family switch { PadFamily.Xbox => "P4", _ when playStation => "Left Fn", _ => "Left paddle 2" },
            PadButton.Touchpad => "Touchpad",
            _ => button.ToString(),
        };
    }
}

/// <summary>
/// Turns button presses and releases into bound actions (index into <see cref="Bindings"/>), and captures new bindings.
/// A combo fires when its button goes down while the modifier is held. A single button that is also some combo's modifier
/// fires on release instead, and only if no other button was pressed while it was held, so the combo doesn't trigger both.
/// </summary>
public sealed class PadBindingMatcher
{
    private readonly HashSet<PadButton> _held = [];

    /// <summary>Buttons whose single-button action waits for the release; true once another button was pressed meanwhile.</summary>
    private readonly Dictionary<PadButton, bool> _deferred = [];

    private PadButton? _capturePending;

    /// <summary>Index = action. Null entries are unbound.</summary>
    public IReadOnlyList<PadBinding?> Bindings { get; set; } = [];

    /// <summary>While capturing, presses define the next binding instead of firing actions.</summary>
    public bool Capturing { get; private set; }

    public void BeginCapture()
    {
        Capturing = true;
        _capturePending = null;
    }

    public void CancelCapture()
    {
        Capturing = false;
        _capturePending = null;
    }

    /// <summary>Forgets held buttons (a controller was unplugged mid-press).</summary>
    public void Reset()
    {
        _held.Clear();
        _deferred.Clear();
        _capturePending = null;
    }

    /// <summary>A button went down. Fired actions are added to <paramref name="fired"/>; returns the captured binding, if any.</summary>
    public PadBinding? Down(PadButton button, ICollection<int> fired)
    {
        var otherHeld = _held.Where(b => b != button).ToList();
        _held.Add(button);
        foreach (var key in _deferred.Keys.Where(k => k != button).ToList())
        {
            _deferred[key] = true;
        }

        if (Capturing)
        {
            if (otherHeld.Count > 0)
            {
                var modifier = _capturePending is { } pending && otherHeld.Contains(pending) ? pending : otherHeld[0];
                CancelCapture();
                return new PadBinding(modifier, button);
            }

            _capturePending = button;
            return null;
        }

        var comboFired = false;
        for (var i = 0; i < Bindings.Count; i++)
        {
            if (Bindings[i] is { Modifier: { } modifier } binding && binding.Button == button && modifier != button && _held.Contains(modifier))
            {
                fired.Add(i);
                comboFired = true;
            }
        }

        if (comboFired || !HasSingle(button))
        {
            return null;
        }

        if (Bindings.Any(b => b?.Modifier == button))
        {
            _deferred[button] = false;
        }
        else
        {
            AddSingles(button, fired);
        }

        return null;
    }

    /// <summary>A button went up. Fired actions are added to <paramref name="fired"/>; returns the captured binding, if any.</summary>
    public PadBinding? Up(PadButton button, ICollection<int> fired)
    {
        _held.Remove(button);
        if (Capturing)
        {
            if (_capturePending == button)
            {
                CancelCapture();
                return new PadBinding(null, button);
            }

            return null;
        }

        if (_deferred.Remove(button, out var spoiled) && !spoiled)
        {
            AddSingles(button, fired);
        }

        return null;
    }

    private bool HasSingle(PadButton button) => Bindings.Any(b => b is { Modifier: null } && b.Button == button);

    private void AddSingles(PadButton button, ICollection<int> fired)
    {
        for (var i = 0; i < Bindings.Count; i++)
        {
            if (Bindings[i] is { Modifier: null } binding && binding.Button == button)
            {
                fired.Add(i);
            }
        }
    }
}
