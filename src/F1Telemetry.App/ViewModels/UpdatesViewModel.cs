using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Services;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace F1Telemetry.App.ViewModels;

/// <summary>
/// Auto-update through Velopack: an installed build looks for a newer release on GitHub (at startup when the setting is
/// on, or on request), downloads it in the background, and applies it when the user restarts. A copy run from an
/// unzipped folder or from source can't update itself and says so.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/Volodrum/CrossPlatformF1TelemetryApp";

    private readonly SettingsService _settings;
    private readonly TelemetryRuntime _runtime;
    private readonly ILogger _log;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    public UpdatesViewModel(SettingsService settings, TelemetryRuntime runtime, ILogger<UpdatesViewModel> log)
    {
        _settings = settings;
        _runtime = runtime;
        _log = log;
        try
        {
            _manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
        }
        catch (Exception ex)
        {
            // No Velopack runtime next to the exe (the Avalonia previewer, tests): behave as a portable copy.
            _log.LogDebug(ex, "Update manager unavailable");
        }

        IsInstalled = _manager?.IsInstalled == true;
        CurrentVersion = _manager?.CurrentVersion?.ToString()
                         ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                         ?? "dev";
        CheckOnStartup = settings.Current.CheckForUpdates;
        Status = IsInstalled
            ? "Not checked yet."
            : "This copy runs from a folder, so it can't update itself. Install it with the setup from the Releases page to get updates.";
    }

    public bool IsInstalled { get; }
    public string CurrentVersion { get; }

    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsUpdateReady { get; set; }
    [ObservableProperty] public partial string ReadyVersion { get; set; } = "";
    [ObservableProperty] public partial bool CheckOnStartup { get; set; }

    public bool CanCheck => IsInstalled && !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    partial void OnCheckOnStartupChanged(bool value)
    {
        _settings.Current.CheckForUpdates = value;
        _settings.Save();
    }

    /// <summary>At startup: checks and downloads when the build is installed and the setting is on.</summary>
    public Task CheckOnStartupAsync() => IsInstalled && CheckOnStartup ? CheckAsync() : Task.CompletedTask;

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (_manager is null || !IsInstalled || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Status = "Checking GitHub for a new release…";
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                Status = $"Up to date (checked {DateTime.Now:t}).";
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            Status = $"Downloading {version}…";
            await _manager.DownloadUpdatesAsync(update, progress => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = $"Downloading {version}… {progress}%"));
            _ready = update;
            ReadyVersion = version;
            IsUpdateReady = true;
            Status = $"Version {version} is ready. Restart to update (it is also applied the next time the app starts).";
            _log.LogInformation("Update {Version} downloaded", version);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
            Status = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Hands the downloaded release to Velopack's updater, which waits for this process to exit, applies it and starts
    /// the new version; then closes the app the normal way, so the recording and the database are flushed first.
    /// </summary>
    [RelayCommand]
    private void RestartToUpdate()
    {
        if (_manager is null || _ready is null)
        {
            return;
        }

        if (_runtime.Recorder.IsRecording)
        {
            Status = "Stop the recording before restarting to update.";
            return;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: false, restart: true);
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not start the update");
            Status = $"Could not start the update: {ex.Message}";
        }
    }
}
