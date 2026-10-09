using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Ingest;

namespace F1Telemetry.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PickReplayFile = PickReplayFileAsync;
            }
        };
    }

    /// <summary>Esc leaves the settings page. Runs after SettingsView's tunnelling capture handler, which marks Esc handled while capturing.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && DataContext is MainWindowViewModel { IsSettingsOpen: true } vm)
        {
            vm.IsSettingsOpen = false;
            e.Handled = true;
        }
    }

    private async Task<string?> PickReplayFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open raw telemetry capture",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("F1 capture") { Patterns = [$"*{PacketFile.Extension}"] }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
