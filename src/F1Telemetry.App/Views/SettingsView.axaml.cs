using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using F1Telemetry.App.ViewModels;

namespace F1Telemetry.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // Tunnel so the capture sees the keys before focused buttons/inputs consume them.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel { IsCapturingHotkey: true } vm && vm.TryCapture(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }
}
