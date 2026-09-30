using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.App.Controls;

/// <summary>
/// The player's car from above for the damage page. Each part is tinted by its damage band (see
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

    private static readonly Geometry Floor = Geometry.Parse(
        "M110,136 H218 Q228,136 228,148 V232 Q228,244 214,248 L204,252 V288 H124 V252 L114,248 Q100,244 100,232 V148 Q100,136 110,136 Z");
    private static readonly Geometry Chassis = Geometry.Parse("M156,44 H172 L176,124 L184,150 V258 H144 V150 L152,124 Z");
    private static readonly Geometry Diffuser = Geometry.Parse("M126,290 H202 L206,308 H122 Z");

    private static readonly (Point From, Point To)[] Suspension =
    [
        (new(124, 80), new(154, 96)), (new(124, 106), new(153, 114)),
        (new(204, 80), new(174, 96)), (new(204, 106), new(175, 114)),
        (new(122, 270), new(146, 266)), (new(122, 296), new(146, 292)),
        (new(206, 270), new(182, 266)), (new(206, 296), new(182, 292)),
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

        // Front wing, split left / right.
        Label(context, "FRONT WING", new Point(164, 14));
        Part(context, new Rect(84, 22, 78, 22), d.FrontLeftWingDamage, 4);
        Value(context, d.FrontLeftWingDamage, new Point(123, 33), 13);
        Part(context, new Rect(166, 22, 78, 22), d.FrontRightWingDamage, 4);
        Value(context, d.FrontRightWingDamage, new Point(205, 33), 13);

        foreach (var (from, to) in Suspension)
        {
            context.DrawLine(SuspensionPen, from, to);
        }

        // Floor under everything, then the neutral chassis with the cockpit opening.
        Tinted(context, Floor, Palette.Damage(d.FloorDamage));
        context.DrawGeometry(Palette.Raised, StructurePen, Chassis);
        context.DrawEllipse(Palette.OnColor, StructurePen, new Point(164, 150), 10, 17);

        // Sidepods (one value for both sides) and engine cover.
        foreach (var x in new[] { 106.0, 186.0 })
        {
            Part(context, new Rect(x, 160, 36, 84), d.SidepodDamage, 12);
            Label(context, "SP", new Point(x + 18, 174));
            Value(context, d.SidepodDamage, new Point(x + 18, 202), 12);
        }

        Part(context, new Rect(148, 176, 32, 80), d.EngineDamage, 9);
        Label(context, "ENG", new Point(164, 202));
        Value(context, d.EngineDamage, new Point(164, 220), 12);

        Part(context, new Rect(154, 260, 20, 26), d.GearBoxDamage, 4);
        Tinted(context, Diffuser, Palette.Damage(d.DiffuserDamage));

        Part(context, new Rect(104, 316, 120, 22), d.RearWingDamage, 4);
        Label(context, "REAR WING", new Point(146, 327));
        Value(context, d.RearWingDamage, new Point(200, 327), 13);

        Tyre(context, "FL", new Rect(90, 62, 34, 58), d.TyresWear.FrontLeft, d.BrakesDamage.FrontLeft, new Point(82, 80), leftSide: true);
        Tyre(context, "FR", new Rect(204, 62, 34, 58), d.TyresWear.FrontRight, d.BrakesDamage.FrontRight, new Point(246, 80), leftSide: false);
        Tyre(context, "RL", new Rect(82, 252, 40, 62), d.TyresWear.RearLeft, d.BrakesDamage.RearLeft, new Point(74, 272), leftSide: true);
        Tyre(context, "RR", new Rect(206, 252, 40, 62), d.TyresWear.RearRight, d.BrakesDamage.RearRight, new Point(254, 272), leftSide: false);
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
        context.DrawRectangle(fill, stroke, rect, 8, 8);
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
