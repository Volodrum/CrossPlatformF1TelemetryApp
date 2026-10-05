using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Core.Input;

namespace F1Telemetry.App.Controls;

/// <summary>
/// A controller binding drawn as button icons ("Touchpad + ✕" as pictures), in the style of the connected pad:
/// PlayStation symbols, Xbox / Nintendo letters, a D-pad with the pressed arm lit. The tooltip spells it out.
/// </summary>
public sealed class PadBindingView : Control
{
    public static readonly StyledProperty<string?> BindingTextProperty =
        AvaloniaProperty.Register<PadBindingView, string?>(nameof(BindingText));

    public static readonly StyledProperty<PadFamily> FamilyProperty =
        AvaloniaProperty.Register<PadBindingView, PadFamily>(nameof(Family));

    private const double Gap = 6;
    private static readonly Typeface Face = new(new FontFamily("avares://F1Telemetry/Assets/Fonts#Chakra Petch"), FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush Body = Palette.Raised;
    private static readonly IBrush Lit = Palette.TextHi;
    private static readonly IBrush Dim = Palette.LineStrong;
    private static readonly IBrush Ink = Palette.TextMid;

    static PadBindingView()
    {
        AffectsRender<PadBindingView>(BindingTextProperty, FamilyProperty);
        AffectsMeasure<PadBindingView>(BindingTextProperty, FamilyProperty);
        HeightProperty.OverrideDefaultValue<PadBindingView>(34);
    }

    public PadBindingView()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BindingTextProperty || e.Property == FamilyProperty)
            {
                ToolTip.SetTip(this, PadBinding.TryParse(BindingText)?.Describe(Family) ?? "Not bound");
            }
        };
    }

    /// <summary>A saved binding such as "Touchpad+DPadRight"; empty = unbound.</summary>
    public string? BindingText
    {
        get => GetValue(BindingTextProperty);
        set => SetValue(BindingTextProperty, value);
    }

    public PadFamily Family
    {
        get => GetValue(FamilyProperty);
        set => SetValue(FamilyProperty, value);
    }

    private IReadOnlyList<PadButton> Buttons() =>
        PadBinding.TryParse(BindingText) is { } b ? b.Modifier is { } m ? [m, b.Button] : [b.Button] : [];

    private double IconHeight => double.IsNaN(Height) ? 34 : Height;

    protected override Size MeasureOverride(Size availableSize)
    {
        var buttons = Buttons();
        var h = IconHeight;
        var width = buttons.Count == 0 ? h : buttons.Sum(b => WidthRatio(b) * h) + (buttons.Count - 1) * (h * 0.5 + 2 * Gap);
        return new Size(width, h);
    }

    /// <summary>Icon width as a multiple of its height.</summary>
    private static double WidthRatio(PadButton button) => button switch
    {
        PadButton.Touchpad => 1.6,
        PadButton.LeftShoulder or PadButton.RightShoulder => 1.5,
        PadButton.Back or PadButton.Start or PadButton.Misc1 => 1.3,
        PadButton.RightPaddle1 or PadButton.LeftPaddle1 or PadButton.RightPaddle2 or PadButton.LeftPaddle2 => 1.3,
        _ => 1,
    };

    public override void Render(DrawingContext context)
    {
        var h = IconHeight;
        var buttons = Buttons();
        if (buttons.Count == 0)
        {
            DrawText(context, "—", new Point(h / 2, h / 2), h * 0.6, Palette.TextLo);
            return;
        }

        var x = 0.0;
        for (var i = 0; i < buttons.Count; i++)
        {
            if (i > 0)
            {
                // The "+" between modifier and button.
                var plus = new Point(x + Gap + h * 0.25, h / 2);
                var pen = new Pen(Palette.TextLo, 2, lineCap: PenLineCap.Round);
                context.DrawLine(pen, plus - new Vector(h * 0.18, 0), plus + new Vector(h * 0.18, 0));
                context.DrawLine(pen, plus - new Vector(0, h * 0.18), plus + new Vector(0, h * 0.18));
                x += h * 0.5 + 2 * Gap;
            }

            var w = WidthRatio(buttons[i]) * h;
            DrawButton(context, buttons[i], new Rect(x, 0, w, h));
            x += w;
        }
    }

    private void DrawButton(DrawingContext g, PadButton button, Rect r)
    {
        var c = r.Center;
        var s = r.Height;
        var ring = new Pen(Dim, 1.5);
        var playStation = Family is PadFamily.PlayStation or PadFamily.DualSense;

        switch (button)
        {
            case PadButton.South or PadButton.East or PadButton.West or PadButton.North:
                g.DrawEllipse(Body, ring, c, s / 2 - 1, s / 2 - 1);
                DrawFace(g, button, c, s);
                break;

            case PadButton.DPadUp or PadButton.DPadDown or PadButton.DPadLeft or PadButton.DPadRight:
                DrawDPad(g, button, c, s);
                break;

            case PadButton.LeftShoulder or PadButton.RightShoulder:
            {
                // Bumper seen from the front: flat bottom, rounded top.
                var left = button == PadButton.LeftShoulder;
                var body = new Rect(r.X + 1, r.Y + s * 0.2, r.Width - 2, s * 0.6);
                g.DrawRectangle(Body, ring, body, s * 0.25, s * 0.25);
                var label = Family switch
                {
                    PadFamily.Xbox => left ? "LB" : "RB",
                    PadFamily.Nintendo => left ? "L" : "R",
                    _ => left ? "L1" : "R1",
                };
                DrawText(g, label, body.Center, s * 0.42, Lit);
                break;
            }

            case PadButton.LeftStick or PadButton.RightStick:
            {
                g.DrawEllipse(Body, ring, c, s / 2 - 1, s / 2 - 1);
                g.DrawEllipse(null, new Pen(Ink, 1.5), c, s * 0.3, s * 0.3);
                var left = button == PadButton.LeftStick;
                DrawText(g, playStation ? (left ? "L3" : "R3") : (left ? "LS" : "RS"), c, s * 0.3, Lit);
                break;
            }

            case PadButton.Touchpad:
            {
                var pad = new Rect(r.X + 1, r.Y + s * 0.12, r.Width - 2, s * 0.76);
                g.DrawRectangle(Body, ring, pad, s * 0.14, s * 0.14);
                // A fingertip pressing the pad.
                g.DrawEllipse(Lit, null, pad.Center, s * 0.12, s * 0.12);
                g.DrawEllipse(null, new Pen(Ink, 1.2), pad.Center, s * 0.22, s * 0.22);
                break;
            }

            case PadButton.Back or PadButton.Start:
                DrawSmallButton(g, button, r, s);
                break;

            case PadButton.Guide:
                g.DrawEllipse(Body, ring, c, s / 2 - 1, s / 2 - 1);
                DrawText(g, Family switch { PadFamily.Xbox => "XB", PadFamily.Nintendo => "⌂", _ when playStation => "PS", _ => "◎" }, c, s * 0.38, Lit);
                break;

            case PadButton.Misc1:
                DrawPill(g, r, s);
                if (Family == PadFamily.DualSense)
                {
                    DrawMic(g, c, s);
                }
                else
                {
                    DrawText(g, Family switch { PadFamily.Xbox => "SHR", PadFamily.Nintendo => "CAP", _ => "MSC" }, c, s * 0.34, Lit);
                }

                break;

            default:
            {
                // Back paddles: a tall rounded tab with its name.
                var paddle = new Rect(r.X + s * 0.12, r.Y + 1, r.Width - s * 0.24, s - 2);
                g.DrawRectangle(Body, ring, paddle, s * 0.18, s * 0.18);
                var label = (button, Family) switch
                {
                    (PadButton.RightPaddle1, PadFamily.Xbox) => "P1",
                    (PadButton.RightPaddle2, PadFamily.Xbox) => "P2",
                    (PadButton.LeftPaddle1, PadFamily.Xbox) => "P3",
                    (PadButton.LeftPaddle2, PadFamily.Xbox) => "P4",
                    (PadButton.LeftPaddle1, _) => "L",
                    (PadButton.RightPaddle1, _) => "R",
                    (PadButton.LeftPaddle2, _) => "LFn",
                    _ => "RFn",
                };
                DrawText(g, label, paddle.Center, s * 0.34, Lit);
                break;
            }
        }
    }

    /// <summary>Face button: PlayStation symbol in its colour, Xbox / Nintendo letter, or the button's position.</summary>
    private void DrawFace(DrawingContext g, PadButton button, Point c, double s)
    {
        switch (Family)
        {
            case PadFamily.PlayStation or PadFamily.DualSense:
            {
                var (color, size) = button switch
                {
                    PadButton.South => ("#7CB2E8", s * 0.2),
                    PadButton.East => ("#FF6B6B", s * 0.2),
                    PadButton.West => ("#E78BD8", s * 0.18),
                    _ => ("#3DDC97", s * 0.22),
                };
                var pen = new Pen(new SolidColorBrush(Color.Parse(color)), Math.Max(1.8, s * 0.075), lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
                switch (button)
                {
                    case PadButton.South:
                        g.DrawLine(pen, c + new Vector(-size, -size), c + new Vector(size, size));
                        g.DrawLine(pen, c + new Vector(-size, size), c + new Vector(size, -size));
                        break;
                    case PadButton.East:
                        g.DrawEllipse(null, pen, c, size, size);
                        break;
                    case PadButton.West:
                        g.DrawRectangle(null, pen, new Rect(c.X - size, c.Y - size, size * 2, size * 2));
                        break;
                    default:
                        var triangle = new PolylineGeometry(
                            [c + new Vector(0, -size * 1.05), c + new Vector(size * 1.05, size * 0.75), c + new Vector(-size * 1.05, size * 0.75)], true);
                        g.DrawGeometry(null, pen, triangle);
                        break;
                }

                break;
            }

            case PadFamily.Xbox or PadFamily.Nintendo:
            {
                var xbox = Family == PadFamily.Xbox;
                var (letter, color) = button switch
                {
                    PadButton.South => (xbox ? "A" : "B", xbox ? "#6CC24A" : "#F4F6F9"),
                    PadButton.East => (xbox ? "B" : "A", xbox ? "#E8413C" : "#F4F6F9"),
                    PadButton.West => (xbox ? "X" : "Y", xbox ? "#3A8FE8" : "#F4F6F9"),
                    _ => (xbox ? "Y" : "X", xbox ? "#F5C518" : "#F4F6F9"),
                };
                DrawText(g, letter, c, s * 0.55, new SolidColorBrush(Color.Parse(color)));
                break;
            }

            default:
            {
                // Unknown pad: the four face buttons as dots, the bound one lit.
                var d = s * 0.22;
                foreach (var (b, offset) in new[] { (PadButton.South, new Vector(0, d)), (PadButton.East, new Vector(d, 0)), (PadButton.West, new Vector(-d, 0)), (PadButton.North, new Vector(0, -d)) })
                {
                    g.DrawEllipse(b == button ? Lit : Dim, null, c + offset, s * 0.09, s * 0.09);
                }

                break;
            }
        }
    }

    /// <summary>D-pad cross with the bound direction lit.</summary>
    private static void DrawDPad(DrawingContext g, PadButton button, Point c, double s)
    {
        var arm = s * 0.3;
        var half = s * 0.15;
        var cross = new PolylineGeometry(
        [
            c + new Vector(-half, -arm - half), c + new Vector(half, -arm - half), c + new Vector(half, -half),
            c + new Vector(arm + half, -half), c + new Vector(arm + half, half), c + new Vector(half, half),
            c + new Vector(half, arm + half), c + new Vector(-half, arm + half), c + new Vector(-half, half),
            c + new Vector(-arm - half, half), c + new Vector(-arm - half, -half), c + new Vector(-half, -half),
        ], true);
        g.DrawGeometry(Body, new Pen(Dim, 1.5), cross);

        var (dx, dy) = button switch
        {
            PadButton.DPadUp => (0, -1),
            PadButton.DPadDown => (0, 1),
            PadButton.DPadLeft => (-1, 0),
            _ => (1, 0),
        };
        var tip = c + new Vector(dx * (arm + half - 1), dy * (arm + half - 1));
        var inner = c + new Vector(dx * half * 0.6, dy * half * 0.6);
        var rect = new Rect(new Point(Math.Min(tip.X, inner.X), Math.Min(tip.Y, inner.Y)), new Point(Math.Max(tip.X, inner.X), Math.Max(tip.Y, inner.Y)))
            .Inflate(new Thickness(dx == 0 ? half - 1.5 : 0, dy == 0 ? half - 1.5 : 0));
        g.DrawRectangle(Lit, null, rect, 1.5, 1.5);
    }

    /// <summary>Create / Share / View and Options / Menu: the small buttons either side of the touchpad.</summary>
    private void DrawSmallButton(DrawingContext g, PadButton button, Rect r, double s)
    {
        DrawPill(g, r, s);
        var c = r.Center;
        var pen = new Pen(Lit, 1.6, lineCap: PenLineCap.Round);
        if (Family == PadFamily.Nintendo)
        {
            DrawText(g, button == PadButton.Back ? "−" : "+", c, s * 0.5, Lit);
        }
        else if (button == PadButton.Start)
        {
            // Options / Menu: three lines.
            for (var i = -1; i <= 1; i++)
            {
                g.DrawLine(pen, c + new Vector(-s * 0.16, i * s * 0.11), c + new Vector(s * 0.16, i * s * 0.11));
            }
        }
        else if (Family == PadFamily.Xbox)
        {
            // View: two overlapping windows.
            g.DrawRectangle(null, pen, new Rect(c.X - s * 0.18, c.Y - s * 0.13, s * 0.24, s * 0.18), 1.5, 1.5);
            g.DrawRectangle(Body, pen, new Rect(c.X - s * 0.06, c.Y - s * 0.04, s * 0.24, s * 0.18), 1.5, 1.5);
        }
        else
        {
            // Create / Share: a short burst of three rays fanning upwards.
            var origin = c + new Vector(0, s * 0.16);
            foreach (var ray in new[] { new Vector(0, -1), new Vector(-0.7, -0.7), new Vector(0.7, -0.7) })
            {
                g.DrawLine(pen, origin + ray * s * 0.12, origin + ray * s * 0.3);
            }
        }
    }

    private static void DrawPill(DrawingContext g, Rect r, double s)
    {
        var pill = new Rect(r.X + 1, r.Y + s * 0.2, r.Width - 2, s * 0.6);
        g.DrawRectangle(Body, new Pen(Dim, 1.5), pill, s * 0.3, s * 0.3);
    }

    /// <summary>DualSense mute button: a microphone.</summary>
    private static void DrawMic(DrawingContext g, Point c, double s)
    {
        var pen = new Pen(Lit, 1.5, lineCap: PenLineCap.Round);
        g.DrawRectangle(Lit, null, new Rect(c.X - s * 0.06, c.Y - s * 0.2, s * 0.12, s * 0.22), s * 0.06, s * 0.06);
        // The cup under the capsule: a half circle opening upwards.
        var cupCentre = c + new Vector(0, -s * 0.04);
        var cup = new PolylineGeometry(
            Enumerable.Range(0, 9).Select(i => cupCentre + new Vector(Math.Cos(Math.PI * i / 8), Math.Sin(Math.PI * i / 8)) * s * 0.13).ToList(), false);
        g.DrawGeometry(null, pen, cup);
        g.DrawLine(pen, c + new Vector(0, s * 0.08), c + new Vector(0, s * 0.18));
    }

    private static void DrawText(DrawingContext g, string text, Point centre, double size, IBrush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);
        g.DrawText(formatted, new Point(centre.X - formatted.Width / 2, centre.Y - formatted.Height / 2));
    }
}
