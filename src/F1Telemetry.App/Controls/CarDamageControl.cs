using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.App.Controls;

/// <summary>
/// The player's car from above for the damage page, drawn as a 2022-rules F1 car (wings, nose, halo, sidepods, floor,
/// engine cover, diffuser). Each part is tinted by its damage band (see
/// <see cref="Palette.Damage"/>) and carries its percentage; tyres show wear beside the wheel with brake damage
/// underneath. Floor, diffuser and gearbox are tinted only: their numbers live in the row below the control.
/// Drawn in a 328 × 340 design space and scaled to fit.
/// </summary>
public sealed class CarDamageControl : Control
{
    public static readonly StyledProperty<CarDamage?> DamageProperty =
        AvaloniaProperty.Register<CarDamageControl, CarDamage?>(nameof(Damage));

    private const double DesignWidth = 328;
    private const double DesignHeight = 340;

    private static readonly Typeface DataFace = new(new FontFamily("avares://F1Telemetry/Assets/Fonts#JetBrains Mono"), FontStyle.Normal, FontWeight.ExtraBold);
    private static readonly Typeface LabelFace = new(new FontFamily("avares://F1Telemetry/Assets/Fonts#Chakra Petch"), FontStyle.Normal, FontWeight.Bold);
    private static readonly IPen StructurePen = new Pen(Palette.LineStrong, 2);
    private static readonly IPen SuspensionPen = new Pen(Palette.LineStrong, 3, lineCap: PenLineCap.Round);
    private static readonly Dictionary<IBrush, (IBrush Fill, IPen Stroke)> Tints = [];

    // A 2022-rules car from above, nose up, centred on x = 164. Right-side parts are the left ones mirrored.
    private static readonly Geometry FrontWingLeft = Geometry.Parse(
        "M162,22 C136,23 110,27 90,33 L84,35 L84,58 L91,58 C112,52 138,44 162,42 Z");
    private static readonly Geometry FrontWingRight = Mirror(FrontWingLeft);

    /// <summary>Nose and monocoque: a slim nose widening to the cockpit, then the survival cell back to the gearbox.</summary>
    private static readonly Geometry Chassis = Geometry.Parse(
        "M164,24 C171,24 173,32 173,44 L177,118 C179,126 182,132 184,140 L184,248 C184,256 176,262 164,262 C152,262 144,256 144,248 L144,140 C146,132 149,126 151,118 L155,44 C155,32 157,24 164,24 Z");

    /// <summary>The floor: a flat leading edge behind the front wheels, widest under the sidepods, pinched in front of the rear wheels.</summary>
    private static readonly Geometry Floor = Geometry.Parse(
        "M148,134 L110,134 C103,134 100,139 100,146 L98,226 C98,250 112,262 122,270 L124,290 L204,290 L206,270 C216,262 230,250 230,226 L228,146 C228,139 225,134 218,134 Z");

    /// <summary>A sidepod: the inlet at the front, bulging out and sweeping in towards the rear (the coke bottle).</summary>
    private static readonly Geometry SidepodLeft = Geometry.Parse(
        "M146,148 L118,148 C110,148 106,153 106,161 C106,180 111,198 122,214 C131,227 140,238 147,246 C149,248 150,247 150,244 L150,170 Z");
    private static readonly Geometry SidepodRight = Mirror(SidepodLeft);

    /// <summary>Engine cover: the airbox behind the driver's head, tapering back to the gearbox.</summary>
    private static readonly Geometry EngineCover = Geometry.Parse(
        "M148,178 C148,166 180,166 180,178 L173,256 C172,261 156,261 155,256 Z");

    private static readonly Geometry Diffuser = Geometry.Parse("M124,290 H204 L210,307 H118 Z");

    /// <summary>The rear wing's main plane, slightly spooned, between its endplates.</summary>
    private static readonly Geometry RearWing = Geometry.Parse(
        "M108,316 C142,312 186,312 220,316 L220,336 C186,332 142,332 108,336 Z");

    /// <summary>The halo round the cockpit, with its centre pillar running forward.</summary>
    private static readonly Geometry Halo = Geometry.Parse(
        "M164,124 L164,131 M151,152 C151,138 156,131 164,131 C172,131 177,138 177,152 L177,170 M151,152 L151,170");

    private static readonly IPen HaloPen = new Pen(Palette.TextLo, 3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IPen StrakePen = new Pen(Palette.LineStrong, 1.5);
    private static readonly IBrush Rim = new SolidColorBrush(Color.Parse("#07080A"), 0.55);

    /// <summary>Wishbones on the left: an A-arm and a track rod from each wheel to the car (mirrored for the right).</summary>
    private static readonly (Point From, Point To)[] SuspensionLeft =
    [
        (new(124, 88), new(156, 72)), (new(124, 88), new(155, 106)), (new(124, 100), new(155, 114)),
        (new(122, 280), new(155, 268)), (new(122, 280), new(155, 294)),
    ];

    static CarDamageControl()
    {
        AffectsRender<CarDamageControl>(DamageProperty);
        AffectsMeasure<CarDamageControl>(DamageProperty);
    }

    public CarDamage? Damage
    {
        get => GetValue(DamageProperty);
        set => SetValue(DamageProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? DesignWidth : availableSize.Width;
        return new Size(width, width * DesignHeight / DesignWidth);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var d = Damage ?? default;
        var scale = Math.Min(Bounds.Width / DesignWidth, Bounds.Height / DesignHeight);
        var offset = new Vector((Bounds.Width - DesignWidth * scale) / 2, (Bounds.Height - DesignHeight * scale) / 2);
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset));

        // Floor first: everything else sits on it.
        Tinted(context, Floor, Palette.Damage(d.FloorDamage));
        Tinted(context, Diffuser, Palette.Damage(d.DiffuserDamage));
        for (var x = 136.0; x <= 192; x += 14)
        {
            context.DrawLine(StrakePen, new Point(x, 292), new Point(x + (x - 164) * 0.12, 305));
        }

        foreach (var (from, to) in SuspensionLeft)
        {
            context.DrawLine(SuspensionPen, from, to);
            context.DrawLine(SuspensionPen, Mirror(from), Mirror(to));
        }

        // Front wing (left / right), then the nose and monocoque over its centre.
        Label(context, "FRONT WING", new Point(164, 10));
        Tinted(context, FrontWingLeft, Palette.Damage(d.FrontLeftWingDamage));
        Tinted(context, FrontWingRight, Palette.Damage(d.FrontRightWingDamage));
        Value(context, d.FrontLeftWingDamage, new Point(122, 39), 13);
        Value(context, d.FrontRightWingDamage, new Point(206, 39), 13);
        context.DrawGeometry(Palette.Raised, StructurePen, Chassis);

        // Sidepods (one value for both sides) and the engine cover.
        Tinted(context, SidepodLeft, Palette.Damage(d.SidepodDamage));
        Tinted(context, SidepodRight, Palette.Damage(d.SidepodDamage));
        foreach (var x in new[] { 127.0, 201.0 })
        {
            Label(context, "SP", new Point(x, 166));
            Value(context, d.SidepodDamage, new Point(x, 184), 12);
        }

        Tinted(context, EngineCover, Palette.Damage(d.EngineDamage));
        Label(context, "ENG", new Point(164, 202));
        Value(context, d.EngineDamage, new Point(164, 220), 12);

        // Cockpit and halo.
        context.DrawEllipse(Palette.OnColor, StructurePen, new Point(164, 150), 10, 16);
        context.DrawGeometry(null, HaloPen, Halo);

        Part(context, new Rect(155, 258, 18, 30), d.GearBoxDamage, 3);
        context.DrawLine(SuspensionPen, new Point(164, 288), new Point(164, 316)); // the rear wing's pylon

        // Rear wing: main plane between two endplates.
        Tinted(context, RearWing, Palette.Damage(d.RearWingDamage));
        Part(context, new Rect(101, 311, 7, 28), d.RearWingDamage, 2);
        Part(context, new Rect(220, 311, 7, 28), d.RearWingDamage, 2);
        Label(context, "REAR WING", new Point(142, 325));
        Value(context, d.RearWingDamage, new Point(198, 325), 13);

        // Tyres last, over the suspension: the fronts narrower than the rears.
        Tyre(context, "FL", new Rect(90, 62, 34, 58), d.TyresWear.FrontLeft, d.BrakesDamage.FrontLeft, new Point(82, 80), leftSide: true);
        Tyre(context, "FR", new Rect(204, 62, 34, 58), d.TyresWear.FrontRight, d.BrakesDamage.FrontRight, new Point(246, 80), leftSide: false);
        Tyre(context, "RL", new Rect(80, 252, 42, 62), d.TyresWear.RearLeft, d.BrakesDamage.RearLeft, new Point(72, 272), leftSide: true);
        Tyre(context, "RR", new Rect(206, 252, 42, 62), d.TyresWear.RearRight, d.BrakesDamage.RearRight, new Point(256, 272), leftSide: false);
    }

    private static Point Mirror(Point p) => new(DesignWidth - p.X, p.Y);

    private static Geometry Mirror(Geometry geometry)
    {
        var mirrored = geometry.Clone();
        mirrored.Transform = new MatrixTransform(new Matrix(-1, 0, 0, 1, DesignWidth, 0));
        return mirrored;
    }

    private static void Part(DrawingContext context, Rect rect, double percent, double radius)
    {
        var (fill, stroke) = Tint(Palette.Damage(percent));
        context.DrawRectangle(fill, stroke, rect, radius, radius);
    }

    private static void Tinted(DrawingContext context, Geometry geometry, IBrush colour)
    {
        var (fill, stroke) = Tint(colour);
        context.DrawGeometry(fill, stroke, geometry);
    }

    private static void Tyre(DrawingContext context, string name, Rect rect, float wear, byte brakes, Point textAnchor, bool leftSide)
    {
        var colour = Palette.TyreWear(wear);
        var (fill, stroke) = Tint(colour);
        context.DrawRectangle(fill, stroke, rect, 9, 9);
        // The wheel rim on the car's side of the tyre, so it reads as a wheel from above.
        var rim = leftSide ? new Rect(rect.Right - 9, rect.Y + 8, 5, rect.Height - 16) : new Rect(rect.X + 4, rect.Y + 8, 5, rect.Height - 16);
        context.DrawRectangle(Rim, null, rim, 2, 2);
        Text(context, name, LabelFace, 9.5, Palette.TextMid, rect.Center, align: 0);

        var align = leftSide ? 1 : -1;
        Text(context, Percent(wear), DataFace, 22, colour, textAnchor, align);
        Text(context, $"BRK {Percent(brakes)}", DataFace, 11, Palette.TextLo, textAnchor + new Point(0, 19), align);
    }

    private static void Value(DrawingContext context, double percent, Point centre, double size) =>
        Text(context, Percent(percent), DataFace, size, Palette.Damage(percent), centre, align: 0);

    private static void Label(DrawingContext context, string text, Point centre) =>
        Text(context, text, LabelFace, 9.5, Palette.TextLo, centre, align: 0);

    /// <param name="anchor">Vertical centre of the text; horizontally its centre (align 0), right edge (1) or left edge (-1).</param>
    private static void Text(DrawingContext context, string text, Typeface face, double size, IBrush brush, Point anchor, int align)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);
        var x = align switch
        {
            0 => anchor.X - formatted.Width / 2,
            1 => anchor.X - formatted.Width,
            _ => anchor.X,
        };
        context.DrawText(formatted, new Point(x, anchor.Y - formatted.Height / 2));
    }

    private static string Percent(double value) => $"{Math.Round(value).ToString(CultureInfo.InvariantCulture)}%";

    private static (IBrush Fill, IPen Stroke) Tint(IBrush colour)
    {
        if (!Tints.TryGetValue(colour, out var tint))
        {
            var c = ((ISolidColorBrush)colour).Color;
            Tints[colour] = tint = (new SolidColorBrush(c, 0.22), new Pen(colour, 2));
        }

        return tint;
    }
}
