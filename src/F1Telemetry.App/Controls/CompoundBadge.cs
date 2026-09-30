using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.App.ViewModels;

namespace F1Telemetry.App.Controls;

/// <summary>Tyre compound badge: coloured ring plus letter, so the compound still reads when colour fades at an angle.</summary>
public sealed class CompoundBadge : Control
{
    public static readonly StyledProperty<string?> CompoundProperty =
        AvaloniaProperty.Register<CompoundBadge, string?>(nameof(Compound));

    private static readonly Typeface LetterFace = new(new FontFamily("avares://F1Telemetry/Assets/Fonts#Chakra Petch"), FontStyle.Normal, FontWeight.Bold);
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#07080A"));

    static CompoundBadge()
    {
        AffectsRender<CompoundBadge>(CompoundProperty);
        WidthProperty.OverrideDefaultValue<CompoundBadge>(44);
        HeightProperty.OverrideDefaultValue<CompoundBadge>(44);
    }

    public string? Compound
    {
        get => GetValue(CompoundProperty);
        set => SetValue(CompoundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var compound = Compound ?? "Unknown";
        var color = Palette.Compound(compound);
        var ring = Math.Max(2.5, size * 0.11);
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size / 2 - ring / 2 - 1;
        context.DrawEllipse(Fill, new Pen(color, ring), centre, radius, radius);

        var text = new FormattedText(Palette.CompoundLetter(compound), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LetterFace, size * 0.43, color);
        context.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }
}
