using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Core.Models;

namespace F1Telemetry.App.Controls;

/// <summary>
/// One sector of the sector box: colour only, like the TV graphic. Purple = fastest of anyone, green = personal
/// best, yellow = slower, grey = invalid lap. The live sector fills with white as the car drives through it.
/// </summary>
public sealed class SectorBar : Control
{
    public static readonly StyledProperty<SectorMark> MarkProperty =
        AvaloniaProperty.Register<SectorBar, SectorMark>(nameof(Mark));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<SectorBar, double>(nameof(Progress));

    private const double Radius = 3;
    private static readonly IPen LiveOutline = new Pen(Palette.LineStrong, 2);
    private static readonly IBrush LiveFill = new SolidColorBrush(Color.Parse("#F4F6F9"), 0.55);

    static SectorBar()
    {
        AffectsRender<SectorBar>(MarkProperty, ProgressProperty);
        HeightProperty.OverrideDefaultValue<SectorBar>(12);
    }

    public SectorMark Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    /// <summary>0–1 progress through the sector while it is <see cref="SectorMark.Live"/>.</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        if (Mark != SectorMark.Live)
        {
            context.DrawRectangle(Fill(Mark), null, rect, Radius, Radius);
            return;
        }

        context.DrawRectangle(Palette.Selected, null, rect, Radius, Radius);
        var progress = Math.Clamp(Progress, 0, 1);
        if (progress > 0)
        {
            using (context.PushClip(new RoundedRect(rect, Radius)))
            {
                context.DrawRectangle(LiveFill, null, new Rect(0, 0, rect.Width * progress, rect.Height));
            }
        }

        context.DrawRectangle(null, LiveOutline, rect.Deflate(1), Radius, Radius);
    }

    private static IBrush Fill(SectorMark mark) => mark switch
    {
        SectorMark.SessionBest => Palette.SessionBest,
        SectorMark.PersonalBest => Palette.Faster,
        SectorMark.Slower => Palette.Slower,
        SectorMark.Invalid => Palette.LineStrong,
        _ => Palette.Raised,
    };
}
