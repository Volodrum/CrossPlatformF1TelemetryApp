using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Games;
using F1Telemetry.Core.Recording;
using F1Telemetry.Protocol;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.ViewModels;

public sealed record SourceOption(SourceKind Kind, string Label);

/// <summary>A game in the rail's game picker. A planned game is listed but can't be picked yet.</summary>
public sealed partial class GameOptionViewModel(GameInfo game, Action<GameInfo> select) : ObservableObject
{
    public GameInfo Game { get; } = game;
    public string Name => Game.Name;
    public string ShortName => Game.ShortName;
    public string Detail => Game.Detail;
    public bool IsSupported => Game.Supported;
    public string Status => Game.Supported ? Game.Connection.ToUpperInvariant() : "PLANNED";

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => select(Game);
}

/// <summary>A telemetry mode of the current game in the toolbar (for F1: the UDP format, F1 25 or F1 26).</summary>
public sealed partial class ModeOptionViewModel(GameMode mode, Func<GameFormat, Task> select) : ObservableObject
{
    public string Label => mode.Label;
    public GameFormat Format => mode.Format;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>
    /// Explicit click only. (A two-way bound RadioButton group flips the mode when a radio receives focus on
    /// window activation, which silently switched modes in testing.)
    /// </summary>
    [RelayCommand]
    private Task SelectAsync() => select(Format);
}

/// <summary>
/// Every place the main window can show. <see cref="MainSection"/> (the rail) and the sub-tabs are derived from it;
/// <see cref="LapDetail"/> is the laps sub-tab drilled into one lap, <see cref="Compare"/> compares races and
/// <see cref="CompareLaps"/> laps.
/// </summary>
public enum MainTab
{
    Live,
    Laps,
    LapDetail,
    Strategy,
    Energy,
    Compare,
    CompareLaps,
    Setups,
    Overlays,
}

/// <summary>The navigation rail: what the user is doing. Only <see cref="Session"/> follows the recordings sidebar.</summary>
public enum MainSection
{
    Live,
    Session,
    Compare,
    Setups,
    Overlays,
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly TelemetryRuntime _runtime;
    private readonly OverlayManager _overlays;
    private readonly ILogger _log;
    private readonly DispatcherTimer _liveRefresh;
    private bool _switchingSource;

    /// <summary>The session sub-tab to come back to from another section (the laps tab remembers an open lap).</summary>
    private MainTab _lastSessionTab = MainTab.Laps;

    /// <summary>The compare sub-tab to come back to.</summary>
    private MainTab _lastCompareTab = MainTab.Compare;

    /// <summary>Recording of the lap open in the lap detail.</summary>
    private long? _lapRecordingId;

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
        LapCompare = new LapDetailViewModel(runtime) { IsWorkspace = true };
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
        Games = [.. GameCatalog.All.Select(g => new GameOptionViewModel(g, SelectGame))];
        ApplyGame();

        session.LapOpened += async (recording, lap, reference) =>
        {
            _lapRecordingId = recording.Id;
            SelectedTab = MainTab.LapDetail;
            await lapDetail.LoadAsync(recording, lap, reference);
        };
        lapDetail.BackRequested += BackToLaps;

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
        runtime.ModeChanged += _ => Dispatcher.UIThread.Post(MarkSelectedMode);
        runtime.Pipeline.SourceChanged += _ => Dispatcher.UIThread.Post(RestoreSourceSelection);
        runtime.Store.WriteFailed += ex => Dispatcher.UIThread.Post(() =>
            StatusMessage = $"Telemetry could not be saved: {ex.Message} (details in the log)");

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

    /// <summary>Lap vs lap in the compare section: any lap of any recording against any lap on the same track.</summary>
    public LapDetailViewModel LapCompare { get; }
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

    /// <summary>Every game the app knows, for the rail's picker: the supported ones and the planned ones.</summary>
    public IReadOnlyList<GameOptionViewModel> Games { get; }

    /// <summary>The game everything on screen is about.</summary>
    public GameInfo Game => _runtime.Game;

    public string GameShortName => Game.ShortName;
    public string GameTip => $"Game: {Game.Name}. Click to see the games.";

    /// <summary>The current game's telemetry modes (F1: F1 25 / F1 26), shown in the toolbar when there is a choice.</summary>
    public ObservableCollection<ModeOptionViewModel> Modes { get; } = [];

    public bool HasModes => Modes.Count > 1;

    // Pages that exist only for games whose telemetry carries what they are built on.
    public bool HasEnergy => Game.Has(GameFeatures.Ers);
    public bool HasSetups => Game.Has(GameFeatures.Setups);

    private void SelectGame(GameInfo game)
    {
        if (!_runtime.SetGame(game.Id))
        {
            StatusMessage = $"{game.Name} isn't supported yet: it is planned. The app stays on {Game.Name}.";
            return;
        }

        ApplyGame();
        StatusMessage = $"Game: {Game.Name} ({Game.Connection}).";
        _ = RefreshRecordingsAsync();
    }

    /// <summary>Brings the picker, the toolbar's modes and the game's pages in line with the current game.</summary>
    private void ApplyGame()
    {
        foreach (var option in Games)
        {
            option.IsSelected = option.Game.Id == Game.Id;
        }

        Modes.Clear();
        foreach (var mode in Game.Modes)
        {
            Modes.Add(new ModeOptionViewModel(mode, SetModeAsync));
        }

        MarkSelectedMode();

        // A page the new game doesn't have can't stay on screen.
        if (!HasEnergy && _lastSessionTab == MainTab.Energy)
        {
            _lastSessionTab = MainTab.Laps;
        }

        if ((!HasSetups && SelectedTab == MainTab.Setups) || (!HasEnergy && SelectedTab == MainTab.Energy))
        {
            SelectedTab = MainTab.Live;
        }

        OnPropertyChanged(nameof(Game));
        OnPropertyChanged(nameof(GameShortName));
        OnPropertyChanged(nameof(GameTip));
        OnPropertyChanged(nameof(HasModes));
        OnPropertyChanged(nameof(HasEnergy));
        OnPropertyChanged(nameof(HasSetups));
    }

    private void MarkSelectedMode()
    {
        foreach (var mode in Modes)
        {
            mode.IsSelected = mode.Format == _runtime.Mode;
        }
    }

    private async Task SetModeAsync(GameFormat mode)
    {
        await _runtime.SetModeAsync(mode);
        StatusMessage = $"Telemetry mode: {GameCatalog.FormatName(mode)}. Set the same UDP Format in the game (Settings → Telemetry).";
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

        // An open lap belongs to the recording it came from; another recording starts on its lap list.
        if (value?.Info.Id != _lapRecordingId)
        {
            _lapRecordingId = null;
            if (_lastSessionTab == MainTab.LapDetail)
            {
                _lastSessionTab = MainTab.Laps;
            }

            if (SelectedTab == MainTab.LapDetail)
            {
                SelectedTab = MainTab.Laps;
            }
        }

        if (SelectedTab == MainTab.Energy)
        {
            _ = ActivateEnergyAsync();
        }
    }

    public MainSection Section => SelectedTab switch
    {
        MainTab.Live => MainSection.Live,
        MainTab.Compare or MainTab.CompareLaps => MainSection.Compare,
        MainTab.Setups => MainSection.Setups,
        MainTab.Overlays => MainSection.Overlays,
        _ => MainSection.Session,
    };

    // What is on screen: one section, or the settings page in place of all of them. Also the rail's highlight.
    public bool IsLiveActive => !IsSettingsOpen && Section == MainSection.Live;
    public bool IsSessionActive => !IsSettingsOpen && Section == MainSection.Session;
    public bool IsCompareActive => !IsSettingsOpen && Section == MainSection.Compare;
    public bool IsSetupsActive => !IsSettingsOpen && Section == MainSection.Setups;
    public bool IsOverlaysActive => !IsSettingsOpen && Section == MainSection.Overlays;

    /// <summary>Compare sub-tabs: 0 races, 1 laps.</summary>
    public int CompareTabIndex
    {
        get => _lastCompareTab == MainTab.CompareLaps ? 1 : 0;
        set
        {
            if (Section == MainSection.Compare && value != CompareTabIndex)
            {
                SelectedTab = value == 1 ? MainTab.CompareLaps : MainTab.Compare;
            }
        }
    }

    /// <summary>The laps sub-tab shows one lap instead of the list.</summary>
    public bool IsLapOpen => SelectedTab == MainTab.LapDetail;

    /// <summary>Session sub-tabs: 0 laps (list or one lap), 1 strategy, 2 energy.</summary>
    public int SessionTabIndex
    {
        get => _lastSessionTab switch { MainTab.Strategy => 1, MainTab.Energy => 2, _ => 0 };
        set
        {
            // The hidden tab strip re-syncs while another section is shown; only a click inside the session navigates.
            if (Section != MainSection.Session || value == SessionTabIndex)
            {
                return;
            }

            SelectedTab = value switch
            {
                1 => MainTab.Strategy,
                2 => MainTab.Energy,
                _ => _lapRecordingId is not null && _lapRecordingId == SelectedRecording?.Info.Id ? MainTab.LapDetail : MainTab.Laps,
            };
        }
    }

    [RelayCommand]
    private void Navigate(MainSection section)
    {
        IsSettingsOpen = false;
        SelectedTab = section switch
        {
            MainSection.Live => MainTab.Live,
            MainSection.Compare => _lastCompareTab,
            MainSection.Setups => MainTab.Setups,
            MainSection.Overlays => MainTab.Overlays,
            _ => _lastSessionTab,
        };
    }

    /// <summary>From one lap back to the lap list.</summary>
    [RelayCommand]
    private void BackToLaps()
    {
        _lapRecordingId = null;
        Session.SelectedLap = null; // so the same lap can be opened again
        SelectedTab = MainTab.Laps;
    }

    partial void OnSelectedTabChanged(MainTab value)
    {
        IsSettingsOpen = false;
        if (Section == MainSection.Session)
        {
            _lastSessionTab = value;
        }
        else if (Section == MainSection.Compare)
        {
            _lastCompareTab = value;
        }

        // Overlay preview is opt-in on the overlays page and never outlives it.
        if (value != MainTab.Overlays)
        {
            Settings.PreviewOverlays = false;
        }

        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(SessionTabIndex));
        OnPropertyChanged(nameof(CompareTabIndex));
        OnPropertyChanged(nameof(IsLapOpen));
        RaiseActiveChanged();

        if (value == MainTab.Compare)
        {
            _ = ActivateCompareAsync();
        }
        else if (value == MainTab.CompareLaps)
        {
            _ = ActivateLapCompareAsync();
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
        // The settings page covers the overlays page, and the preview with it.
        if (value)
        {
            Settings.PreviewOverlays = false;
        }

        RaiseActiveChanged();
    }

    private void RaiseActiveChanged()
    {
        OnPropertyChanged(nameof(IsLiveActive));
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(IsCompareActive));
        OnPropertyChanged(nameof(IsSetupsActive));
        OnPropertyChanged(nameof(IsOverlaysActive));
    }

    /// <summary>Opens lap vs lap on the sidebar's recordings, starting from the selected one.</summary>
    public async Task ActivateLapCompareAsync()
    {
        try
        {
            await LapCompare.ActivateAsync([.. Recordings.Select(r => r.Info)], SelectedRecording?.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Lap comparison failed");
            StatusMessage = $"Lap comparison failed: {ex.Message}";
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
        // The sidebar lists the current game's recordings: a session, a comparison or a setup never mixes games.
        foreach (var recording in recordings.Where(r => GameCatalog.ForFormat(r.Format).Id == Game.Id))
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
