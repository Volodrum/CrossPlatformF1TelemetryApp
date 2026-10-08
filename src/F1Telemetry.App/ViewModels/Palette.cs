using Avalonia.Media;
using Avalonia.Media.Immutable;
using F1Telemetry.Core.Models;

namespace F1Telemetry.App.ViewModels;

/// <summary>Code-side copy of the design tokens in Theme/Tokens.axaml (status colours used by view models).</summary>
public static class Palette
{
    public static readonly IBrush Transparent = Brushes.Transparent;
    public static readonly IBrush Panel = Brush("#101216");
    public static readonly IBrush Raised = Brush("#181B21");
    public static readonly IBrush LineStrong = Brush("#3A404C");
    public static readonly IBrush TextHi = Brush("#F4F6F9");
    public static readonly IBrush TextMid = Brush("#C3CAD5");
    public static readonly IBrush TextLo = Brush("#8D97A6");
    public static readonly IBrush OnColor = Brush("#07080A");
    public static readonly IBrush Brand = Brush("#E10600");
    public static readonly IBrush SessionBest = Brush("#C77DFF");
    public static readonly IBrush Faster = Brush("#2EE88F");
    public static readonly IBrush Slower = Brush("#FFC23D");
    public static readonly IBrush Danger = Brush("#FF5A4F");
    public static readonly IBrush Info = Brush("#4DB5FF");
    public static readonly IBrush InfoTint = Brush("#0E2236");
    public static readonly IBrush Selected = Brush("#1E2129");
    public static readonly IBrush Orange = Brush("#FF8A3D");

    /// <summary>ERS deploy modes None, Medium, Hotlap, Overtake: one blue ramp, lighter = more power.</summary>
    public static readonly string[] DeployModeHex = ["#3A404C", "#1F6FB2", "#4DB5FF", "#B5E2FF"];
    private static readonly IBrush[] DeployModeBrushes = [.. DeployModeHex.Select(Brush)];

    public static IBrush DeployMode(int mode) => mode is >= 0 and < 4 ? DeployModeBrushes[mode] : LineStrong;

    /// <summary>Text on a <see cref="DeployMode"/> fill: light on the two dark steps, dark on the two light ones.</summary>
    public static IBrush OnDeployMode(int mode) => mode is 2 or 3 ? OnColor : TextHi;

    /// <summary>Race A / race B in the comparison tab (validated colour-blind safe on the dark panel).</summary>
    public const string RaceAHex = "#3593DA";
    public const string RaceBHex = "#E06A1F";
    public static readonly IBrush RaceA = Brush(RaceAHex);
    public static readonly IBrush RaceB = Brush(RaceBHex);

    private static readonly Dictionary<uint, IBrush> TeamBrushes = [];

    /// <summary>Car damage page bands for bodywork and gearbox/engine: &lt;10 green, 10–29 amber, 30–49 orange, 50+ red.</summary>
    public static IBrush Damage(double percent) => percent switch
    {
        >= 50 => Danger,
        >= 30 => Orange,
        >= 10 => Slower,
        _ => Faster,
    };

    /// <summary>Tyre wear on the damage page, same thresholds as <see cref="Wear"/>: &lt;35 green, 35–55 amber, &gt;55 red.</summary>
    public static IBrush TyreWear(double wear) => wear switch
    {
        > 55 => Danger,
        >= 35 => Slower,
        _ => Faster,
    };

    /// <summary>Battery charge: 50 %+ green, 20–49 % amber, under 20 % red.</summary>
    public static IBrush Ers(double percent) => percent switch
    {
        >= 50 => Faster,
        >= 20 => Slower,
        _ => Danger,
    };

    /// <summary>Team colour stripe (UI thread only; brushes are cached per colour).</summary>
    public static IBrush Team(uint? rgb)
    {
        if (rgb is not { } value)
        {
            return LineStrong;
        }

        if (!TeamBrushes.TryGetValue(value, out var brush))
        {
            TeamBrushes[value] = brush = new ImmutableSolidColorBrush(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value));
        }

        return brush;
    }

    public static IBrush Compound(string compound) => compound switch
    {
        "Soft" => Brush("#FF4545"),
        "Medium" => Brush("#FFD12E"),
        "Hard" => TextHi,
        "Inter" => Faster,
        "Wet" => Brush("#3D8BFF"),
        _ => TextLo,
    };

    public static string CompoundLetter(string compound) => compound switch
    {
        "Soft" => "S",
        "Medium" => "M",
        "Hard" => "H",
        "Inter" => "I",
        "Wet" => "W",
        _ => "?",
    };

    public static Chip For(DeltaColor color, string text) => color switch
    {
        DeltaColor.SessionBest => Chip.Filled(text, SessionBest),
        DeltaColor.PersonalBest => Chip.Filled(text, Faster),
        DeltaColor.Slower => Chip.Outlined(text, Slower),
        _ => Chip.Plain(text, TextHi),
    };

    public static IBrush TextFor(DeltaColor color) => color switch
    {
        DeltaColor.SessionBest => SessionBest,
        DeltaColor.PersonalBest => Faster,
        DeltaColor.Slower => Slower,
        _ => TextHi,
    };

    /// <summary>Tyre wear bands from the template: &lt;35% normal, 35–55% amber outline, &gt;55% red fill ("box").</summary>
    public static (IBrush Background, IBrush Foreground, IBrush Border) Wear(double wear) => wear switch
    {
        > 55 => (Danger, OnColor, Danger),
        >= 35 => (Raised, Slower, Slower),
        _ => (Raised, TextHi, Raised),
    };

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

/// <summary>
/// Status chip. Status is never colour alone: filled = session best / faster / warning, outlined = slower,
/// plain = neutral; deltas also carry a ▲/▼ sign.
/// </summary>
public sealed record Chip(string Text, IBrush Background, IBrush Foreground, IBrush Border)
{
    public static Chip Filled(string text, IBrush color) => new(text, color, Palette.OnColor, color);
    public static Chip Outlined(string text, IBrush color) => new(text, Palette.Transparent, color, color);
    public static Chip Plain(string text, IBrush color) => new(text, Palette.Transparent, color, Palette.Transparent);
    public static Chip Empty { get; } = Plain("—", Palette.TextLo);
}
