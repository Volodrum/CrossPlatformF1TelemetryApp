using Avalonia;
using Avalonia.Controls;
using F1Telemetry.App.ViewModels;

namespace F1Telemetry.App.Views;

public partial class OverlaysView : UserControl
{
    public OverlaysView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.PreviewToggleArea = PreviewBarArea;
        }
    }

    /// <summary>Screen rectangle (physical pixels) of the preview bar, or null when it isn't on screen.</summary>
    private PixelRect? PreviewBarArea()
    {
        if (!PreviewBar.IsEffectivelyVisible || TopLevel.GetTopLevel(this) is not Window { WindowState: not WindowState.Minimized })
        {
            return null;
        }

        var topLeft = PreviewBar.PointToScreen(new Point(0, 0));
        var bottomRight = PreviewBar.PointToScreen(new Point(PreviewBar.Bounds.Width, PreviewBar.Bounds.Height));
        return new PixelRect(topLeft, bottomRight);
    }
}
