using Avalonia.Controls;
using Avalonia.Platform.Storage;
using F1Telemetry.App.ViewModels;
using F1Telemetry.Core.Setups;

namespace F1Telemetry.App.Views;

public partial class SetupsView : UserControl
{
    private static readonly FilePickerFileType SetupFiles = new("F1 Telemetry setups") { Patterns = [$"*{SetupFile.Extension}"] };

    public SetupsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SetupsViewModel vm)
            {
                vm.PickExportFile = PickExportFileAsync;
                vm.PickImportFile = PickImportFileAsync;
            }
        };
    }

    private async Task<string?> PickExportFileAsync(string suggestedName)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export setups",
            SuggestedFileName = suggestedName,
            DefaultExtension = SetupFile.Extension,
            FileTypeChoices = [SetupFiles],
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickImportFileAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import setups",
            AllowMultiple = false,
            FileTypeFilter = [SetupFiles],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
