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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is SettingsViewModel { IsCapturingHotkey: true } vm && vm.TryCapture(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }
}
