using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Recording;
using F1Telemetry.Protocol;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.ViewModels;

public sealed record SourceOption(SourceKind Kind, string Label);

public enum MainTab
{
    Live,
    Laps,
    LapDetail,
    Strategy,
    Energy,
    Compare,
    Setups,
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly TelemetryRuntime _runtime;
    private readonly OverlayManager _overlays;
    private readonly ILogger _log;
    private readonly DispatcherTimer _liveRefresh;
    private bool _switchingSource;

    public MainWindowViewModel(
        TelemetryRuntime runtime,
        LiveDataHub hub,
        OverlayManager overlays,
        HotkeyService hotkeys,
        GamepadService gamepads,
        LiveViewModel live,
        SessionViewModel session,
        LapDetailViewModel lapDetail,
        CompareViewModel compare,
        EnergyViewModel energy,
        SetupsViewModel setups,
        SettingsViewModel settings,
        ErsPlanService ersPlan,
        ILogger<MainWindowViewModel> log)
    {
        _runtime = runtime;
        _overlays = overlays;
        _log = log;
        Live = live;
        Session = session;
        LapDetail = lapDetail;
        Compare = compare;
        Energy = energy;
        Setups = setups;
        Settings = settings;

        Sources =
        [
            new(SourceKind.Udp, "Game (UDP)"),
            new(SourceKind.Demo, "Demo (simulator)"),
            new(SourceKind.Replay, "Replay .f1rec file…"),
        ];
        _selectedSource = Sources.FirstOrDefault(s => s.Kind == runtime.ActiveSourceKind);

        session.LapOpened += async (recording, lap, reference) =>
        {
            SelectedTab = MainTab.LapDetail;
            await lapDetail.LoadAsync(recording, lap, reference);
        };

        hub.RecordingChanged += OnRecordingChanged;
        hub.LapCompleted += _ => ScheduleLiveRefresh();
        hotkeys.StartStopPressed += () => ToggleRecording();
        hotkeys.ToggleOverlaysPressed += () => overlays.GloballyVisible = !overlays.GloballyVisible;
        hotkeys.StrategyPagePressed += overlays.ToggleStrategyPage;
        gamepads.StartStopPressed += () => ToggleRecording();
        gamepads.ToggleOverlaysPressed += () => overlays.GloballyVisible = !overlays.GloballyVisible;
        gamepads.StrategyPagePressed += overlays.ToggleStrategyPage;
        hotkeys.ErsPlanPressed += ersPlan.NextMode;
        gamepads.ErsPlanPressed += ersPlan.NextMode;
        overlays.StateChanged += () => OnPropertyChanged(nameof(OverlaysEnabled));
        runtime.ModeChanged += _ => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(IsF125Mode));
            OnPropertyChanged(nameof(IsF126Mode));
        });
        runtime.Pipeline.SourceChanged += _ => Dispatcher.UIThread.Post(RestoreSourceSelection);

        // While recording, laps/strategy refresh shortly after each lap (session history lags the line by ~1 s) and every 5 s.
        _liveRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveRefresh.Tick += async (_, _) =>
        {
            try
            {
                await RefreshLiveSessionAsync();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Live session refresh failed");
            }
        };
    }

    public LiveViewModel Live { get; }
    public SessionViewModel Session { get; }
    public LapDetailViewModel LapDetail { get; }
    public CompareViewModel Compare { get; }
    public EnergyViewModel Energy { get; }
    public SetupsViewModel Setups { get; }
    public SettingsViewModel Settings { get; }
    public IReadOnlyList<SourceOption> Sources { get; }
    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = [];

    /// <summary>Set by the view: opens a file picker and returns the chosen .f1rec path.</summary>
    public Func<Task<string?>>? PickReplayFile { get; set; }

    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial RecordingItemViewModel? SelectedRecording { get; set; }
    [ObservableProperty] public partial MainTab SelectedTab { get; set; }

    /// <summary>Settings is a page of its own, opened from the toolbar, not one of the section tabs.</summary>
    [ObservableProperty] public partial bool IsSettingsOpen { get; set; }

    /// <summary>The recording waiting for the user to confirm its deletion, or null.</summary>
    [ObservableProperty] public partial RecordingItemViewModel? PendingDelete { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "";

    private SourceOption? _selectedSource;

    public SourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value) && value is not null && !_switchingSource)
            {
                _ = SwitchSourceAsync(value);
            }
        }
    }

    /// <summary>Telemetry mode "F1 25" (UDP format 2025). Read-only: the mode changes only via <see cref="SetModeCommand"/>.</summary>
    public bool IsF125Mode => _runtime.Mode == GameFormat.F1_25;

    /// <summary>Telemetry mode "F1 26" (2026 Season Pack, UDP format 2026).</summary>
    public bool IsF126Mode => _runtime.Mode == GameFormat.F1_26;

    /// <summary>
    /// Explicit click only. (A two-way bound RadioButton group flips the mode when a radio receives focus on
    /// window activation, which silently switched modes in testing.)
    /// </summary>
    [RelayCommand]
    private async Task SetModeAsync(GameFormat mode)
    {
        await _runtime.SetModeAsync(mode);
        StatusMessage = $"Telemetry mode: {(mode == GameFormat.F1_26 ? "F1 26" : "F1 25")}. Set the same UDP Format in the game (Settings → Telemetry).";
    }

    public string RecordButtonText => IsRecording ? "■  STOP RECORDING" : "●  START RECORDING";

    public bool OverlaysEnabled
    {
        get => _overlays.GloballyVisible;
        set => _overlays.GloballyVisible = value;
    }

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordButtonText));

    partial void OnSelectedRecordingChanged(RecordingItemViewModel? value)
    {
        PendingDelete = null;
        _ = Session.LoadAsync(value?.Info);
        if (SelectedTab == MainTab.Energy)
        {
            _ = ActivateEnergyAsync();
        }
    }

    public int SelectedTabIndex
    {
        get => (int)SelectedTab;
        set => SelectedTab = (MainTab)value;
    }

    partial void OnSelectedTabChanged(MainTab value)
    {
        IsSettingsOpen = false;
        OnPropertyChanged(nameof(SelectedTabIndex));

        if (value == MainTab.Compare)
        {
            _ = ActivateCompareAsync();
        }
        else if (value == MainTab.Energy)
        {
            _ = ActivateEnergyAsync();
        }
        else if (value == MainTab.Setups)
        {
            _ = Setups.LoadAsync();
        }
    }

    partial void OnIsSettingsOpenChanged(bool value)
    {
        // Overlay preview is opt-in on the settings page and never outlives it.
        if (!value)
        {
            Settings.PreviewOverlays = false;
        }
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    /// <summary>Shows the selected recording on the energy tab (loaded only while the tab is open).</summary>
    public async Task ActivateEnergyAsync()
    {
        try
        {
            await Energy.LoadAsync(SelectedRecording?.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Energy analysis failed");
            StatusMessage = $"Energy analysis failed: {ex.Message}";
        }
    }

    public async Task InitializeAsync() => await RefreshRecordingsAsync();

    /// <summary>Opens the compare tab on the sidebar's recordings, with the selected one as race A.</summary>
    public async Task ActivateCompareAsync()
    {
        try
        {
            await Compare.ActivateAsync([.. Recordings], SelectedRecording?.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Race comparison failed");
            StatusMessage = $"Race comparison failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleRecording() => _runtime.Recorder.Toggle();

    [RelayCommand]
    private async Task RefreshRecordingsAsync(long? select = null)
    {
        var recordings = await _runtime.Store.GetRecordingsAsync();
        var selectedId = select ?? SelectedRecording?.Info.Id;
        Recordings.Clear();
        foreach (var recording in recordings)
        {
            Recordings.Add(new RecordingItemViewModel(recording));
        }

        SelectedRecording = Recordings.FirstOrDefault(r => r.Info.Id == selectedId);
    }

    /// <summary>First step of a delete: asks for confirmation in the sidebar (the raw captures go with it).</summary>
    [RelayCommand]
    private void DeleteRecording(RecordingItemViewModel? item)
    {
        item ??= SelectedRecording;
        if (item is null)
        {
            return;
        }

        if (item.Info.Id == _runtime.Recorder.ActiveRecordingId)
        {
            StatusMessage = "Stop the recording before deleting it.";
            return;
        }

        PendingDelete = item;
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } item)
        {
            return;
        }

        PendingDelete = null;
        if (item.Info.Id == _runtime.Recorder.ActiveRecordingId)
        {
            StatusMessage = "Stop the recording before deleting it.";
            return;
        }

        var captures = await _runtime.DeleteRecordingAsync(item.Info);
        Recordings.Remove(item);
        var mb = (captures.Bytes / 1_000_000.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        StatusMessage = $"Deleted recording #{item.Info.Id}"
            + (captures.Deleted.Count switch { 0 => "", 1 => $" and its raw capture ({mb} MB)", var n => $" and its {n} raw captures ({mb} MB)" })
            + (captures.Failed.Count > 0 ? $". Could not delete {string.Join(", ", captures.Failed.Select(Path.GetFileName))}: the file is in use." : ".");
    }

    private async Task SwitchSourceAsync(SourceOption option)
    {
        try
        {
            if (option.Kind == SourceKind.Replay)
            {
                var path = PickReplayFile is null ? null : await PickReplayFile();
                if (path is null)
                {
                    RestoreSourceSelection();
                    return;
                }

                await _runtime.StartSourceAsync(SourceKind.Replay, path);
            }
            else
            {
                await _runtime.StartSourceAsync(option.Kind);
            }

            StatusMessage = $"Source: {_runtime.Pipeline.ActiveSource?.Name}";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start source {Source}", option.Kind);
            StatusMessage = $"Could not start {option.Label}: {ex.Message}";
            RestoreSourceSelection();
        }
    }

    private void RestoreSourceSelection()
    {
        _switchingSource = true;
        SelectedSource = Sources.FirstOrDefault(s => s.Kind == _runtime.ActiveSourceKind);
        _switchingSource = false;
    }

    private async void OnRecordingChanged(RecordingState state)
    {
        IsRecording = _runtime.Recorder.IsRecording;
        try
        {
            await _runtime.Store.FlushAsync();
            await RefreshRecordingsAsync(state.RecordingId);
        }
        catch (Exception ex)
        {
            // async void: an escaping exception would take the whole app down.
            _log.LogError(ex, "Failed to refresh after recording change");
        }

        if (IsRecording)
        {
            // No tab switch: a recording is often started by hotkey mid-drive, so the user stays where they are.
            _liveRefresh.Start();
            StatusMessage = state.Change == RecordingChange.AutoSplit ? $"New session detected – continued in recording #{state.RecordingId}" : $"Recording #{state.RecordingId}";
        }
        else
        {
            _liveRefresh.Stop();
            StatusMessage = $"Recording #{state.RecordingId} saved.";
        }
    }

    private void ScheduleLiveRefresh()
    {
        if (IsRecording)
        {
            DispatcherTimer.RunOnce(async () =>
            {
                try
                {
                    await RefreshLiveSessionAsync();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Live session refresh failed");
                }
            }, TimeSpan.FromSeconds(1.5));
        }
    }

    private async Task RefreshLiveSessionAsync()
    {
        if (!IsRecording || Session.Recording?.Id != _runtime.Recorder.ActiveRecordingId)
        {
            return;
        }

        await _runtime.Store.FlushAsync();
        await Session.RefreshAsync();
        if (Session.Recording is { } info)
        {
            SelectedRecording?.Update(info);
        }

        if (SelectedTab == MainTab.Energy)
        {
            await Energy.RefreshAsync();
        }
    }
}
