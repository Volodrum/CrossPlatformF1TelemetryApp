using Avalonia.Controls;
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
