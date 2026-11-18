using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Hub;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.ViewModels;

public enum HubPage
{
    Sessions,
    Strategy,
    Energy,
    Setups,
}

/// <summary>A track (in one game format) with sessions recorded or saved there.</summary>
public sealed record HubTrackOption(GameFormat Format, int TrackId, string Name)
{
    public string Label => $"{Name.ToUpperInvariant()} · {Core.Games.GameCatalog.FormatName(Format)}";

    public override string ToString() => Label;
}

/// <summary>A session at the hub's track: a recording, saved or not, or a saved session whose recording was deleted.</summary>
public sealed partial class HubSessionRowViewModel : ObservableObject
{
    private readonly Action<HubSessionRowViewModel> _lengthChanged;
    private bool _loading = true;

    public HubSessionRowViewModel(TrackSession session, int lapsNow, bool isLive, Action<HubSessionRowViewModel> lengthChanged)
    {
        Session = session;
        LapsNow = lapsNow;
        IsLive = isLive;
        _lengthChanged = lengthChanged;
        Length = LengthOptions.FirstOrDefault(o => o.Length == session.RaceLength) ?? LengthOptions[0];
        _loading = false;
    }

    /// <summary>Any length, then each length the game offers.</summary>
    public static IReadOnlyList<RaceLengthOption> LengthOptions { get; } =
        [new(0, "ANY LENGTH"), .. RaceLengths.All.Select(l => new RaceLengthOption(l, RaceLengths.Name(l).ToUpperInvariant()))];

    public TrackSession Session { get; }

    /// <summary>Timed laps the recording has now (the saved copy's when it has none).</summary>
    public int LapsNow { get; }

    /// <summary>Being recorded right now: it can be saved, not deleted.</summary>
    public bool IsLive { get; }

    [ObservableProperty] public partial bool IsChecked { get; set; }

    [ObservableProperty] public partial RaceLengthOption? Length { get; set; }

    partial void OnLengthChanged(RaceLengthOption? value)
    {
        if (!_loading && value is not null)
        {
            _lengthChanged(this);
        }
    }

    public int RaceLength => Length?.Length ?? 0;

    /// <summary>Saved, but the recording has laps since (it was saved while being recorded).</summary>
    public bool Changed => Session is { Saved: { } saved, HasRecording: true } && LapsNow != saved.Laps;

    public bool CanSave => Session.HasRecording;

    public bool CanDeleteRecording => Session is { IsSaved: true, HasRecording: true } && !Changed && !IsLive;

    public string Title => $"{SessionTypes.Name(Session.SessionType)} · {Session.StartTime.LocalDateTime.ToString("d MMM yyyy · HH:mm", CultureInfo.InvariantCulture)}";

    public string Detail
    {
        get
        {
            var best = Session.Saved?.BestLapMs is > 0 and var ms && !Changed ? $" · best {TimeFormat.Lap(ms)}" : "";
            var laps = LapsNow == 1 ? "1 lap" : $"{LapsNow} laps";
            var id = Session.Recording is { } r ? $" · recording #{r.Id}" : "";
            return laps + best + id;
        }
    }

    public Chip Status => Session switch
    {
        { IsSaved: true, HasRecording: false } => Chip.Outlined("HUB ONLY", Palette.Faster),
        { IsSaved: true } when Changed => Chip.Outlined("CHANGED SINCE SAVED", Palette.Slower),
        { IsSaved: true } => Chip.Filled("SAVED", Palette.Faster),
        _ when IsLive => Chip.Outlined("RECORDING", Palette.Info),
        _ => Chip.Outlined("NOT SAVED", Palette.Slower),
    };

    public string StatusTip => Session switch
    {
        { IsSaved: true, HasRecording: false } => "Kept in the hub only: the recording was deleted, and the plans use what was saved.",
        { IsSaved: true } when Changed => "The recording has laps the hub copy doesn't. Save it again to update the hub.",
        { IsSaved: true } => "In the hub: the plans use the saved copy. The recording can be deleted without losing anything the plans use.",
        _ => "Not in the hub: the plans use the recording while it exists. Save it to keep what it taught after deleting it.",
    };
}

/// <summary>One figure before and after a save, in the preview.</summary>
public sealed record HubCompareRow(string Group, string Label, string Before, string After)
{
    public bool IsChange => Before != After;

    public IBrush AfterBrush => IsChange ? Palette.TextHi : Palette.TextMid;
}

/// <summary>
/// The track hub: per track, the sessions saved (what the strategy, energy and setup views learned from them, kept
/// after their recordings are deleted) and those not yet saved; the strategy and ERS plans the hub gives; and the setup
/// library. Sessions are saved only on request, after a preview of what changes.
/// </summary>
public sealed partial class HubViewModel : ObservableObject
{
    private readonly TelemetryRuntime _runtime;
    private readonly ILogger _log;
    private readonly List<TrackSession> _pending = [];
    private int _version;
    private bool _loadingTracks;

    public HubViewModel(TelemetryRuntime runtime, SettingsService settings, SetupsViewModel setups, ILogger<HubViewModel> log, ILogger<EnergyPlanViewModel> planLog)
    {
        _runtime = runtime;
        _log = log;
        Setups = setups;
        Plans = new RacePlansViewModel(runtime, settings);
        EnergyPlan = new EnergyPlanViewModel(planLog);
        runtime.Hub.Changed += (format, trackId) =>
        {
            if (SelectedTrack is { } track && track.Format == format && track.TrackId == trackId && !_saving)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadTrackAsync());
            }
        };
    }

    private bool _saving;

    public SetupsViewModel Setups { get; }

    /// <summary>The track's race plans from the hub and its unsaved recordings.</summary>
    public RacePlansViewModel Plans { get; }

    /// <summary>The track's ERS plan from the hub and its unsaved recordings.</summary>
    public EnergyPlanViewModel EnergyPlan { get; }

    /// <summary>Recordings were deleted here: the sidebar's list needs reading again.</summary>
    public event Action? RecordingsDeleted;

    [ObservableProperty] public partial HubPage Page { get; set; } = HubPage.Sessions;
    [ObservableProperty] public partial HubTrackOption? SelectedTrack { get; set; }
    [ObservableProperty] public partial string Header { get; set; } = "Track hub";
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial int CheckedCount { get; set; }
    [ObservableProperty] public partial bool RemoveArmed { get; set; }
    [ObservableProperty] public partial bool DeleteArmed { get; set; }

    /// <summary>A save is waiting for confirmation: <see cref="Preview"/> shows what it changes.</summary>
    [ObservableProperty] public partial bool IsPreviewing { get; set; }

    [ObservableProperty] public partial string PreviewTitle { get; set; } = "";
    [ObservableProperty] public partial string PreviewNote { get; set; } = "";
    [ObservableProperty] public partial string EnergyNote { get; set; } = "";

    public ObservableCollection<HubTrackOption> Tracks { get; } = [];
    public ObservableCollection<HubSessionRowViewModel> Sessions { get; } = [];
    public ObservableCollection<HubCompareRow> Preview { get; } = [];

    public bool HasTracks => Tracks.Count > 0;
    public bool HasSessions => Sessions.Count > 0;
    public bool IsSessionsPage => Page == HubPage.Sessions;
    public bool IsStrategyPage => Page == HubPage.Strategy;
    public bool IsEnergyPage => Page == HubPage.Energy;
    public bool IsSetupsPage => Page == HubPage.Setups;
    public bool ShowsTrack => Page != HubPage.Setups;

    public string SaveText => Sessions.Where(s => s.IsChecked && s.CanSave).ToList() is { Count: > 0 } save
        ? save.All(s => s.Session.IsSaved) ? $"UPDATE {save.Count} IN THE HUB…" : $"SAVE {save.Count} TO THE HUB…"
        : "SAVE TO THE HUB…";

    public string RemoveText => RemoveArmed ? $"CLICK AGAIN TO REMOVE {ToRemove.Count}" : "REMOVE FROM THE HUB";

    public string DeleteText => DeleteArmed
        ? $"CLICK AGAIN TO DELETE {ToDelete.Count} {(ToDelete.Count == 1 ? "RECORDING" : "RECORDINGS")}"
        : "DELETE SAVED RECORDINGS";

    public string RemoveTip => ToRemove.Count(r => !r.Session.HasRecording) is > 0 and var gone
        ? $"Take the ticked sessions out of the hub. {gone} of them {(gone == 1 ? "has" : "have")} no recording left: what the hub kept of {(gone == 1 ? "it" : "them")} is gone for good."
        : "Take the ticked sessions out of the hub. Their recordings stay and count as unsaved.";

    public string DeleteTip =>
        "Delete the recordings of the ticked sessions that are saved to the hub (samples and raw captures). The plans keep what the hub saved. "
        + "Sessions changed since they were saved, and the one being recorded, are left alone.";

    private List<HubSessionRowViewModel> ToSave => [.. Sessions.Where(s => s.IsChecked && s.CanSave)];

    private List<HubSessionRowViewModel> ToRemove => [.. Sessions.Where(s => s.IsChecked && s.Session.IsSaved)];

    private List<HubSessionRowViewModel> ToDelete => [.. Sessions.Where(s => s.IsChecked && s.CanDeleteRecording)];

    partial void OnPageChanged(HubPage value)
    {
        OnPropertyChanged(nameof(IsSessionsPage));
        OnPropertyChanged(nameof(IsStrategyPage));
        OnPropertyChanged(nameof(IsEnergyPage));
        OnPropertyChanged(nameof(IsSetupsPage));
        OnPropertyChanged(nameof(ShowsTrack));
        _ = ShowPageAsync();
    }

    partial void OnSelectedTrackChanged(HubTrackOption? value)
    {
        if (!_loadingTracks)
        {
            CancelPreview();
            _ = LoadTrackAsync();
        }
    }

    partial void OnRemoveArmedChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(RemoveTip));
    }

    partial void OnDeleteArmedChanged(bool value) => OnPropertyChanged(nameof(DeleteText));

    [RelayCommand]
    private void SetPage(HubPage page) => Page = page;

    /// <summary>Reads the tracks and shows the selected one (the newest track recorded at, on the first visit).</summary>
    public async Task LoadAsync()
    {
        try
        {
            var tracks = await _runtime.Hub.TracksAsync();
            var keep = SelectedTrack;
            _loadingTracks = true;
            Tracks.Clear();
            foreach (var (format, trackId, name) in tracks)
            {
                Tracks.Add(new HubTrackOption(format, trackId, name));
            }

            var newest = (await _runtime.Store.GetRecordingsAsync()).FirstOrDefault(r => r.TrackId >= 0);
            SelectedTrack = Tracks.FirstOrDefault(t => t == keep)
                            ?? Tracks.FirstOrDefault(t => newest is not null && t.Format == newest.Format && t.TrackId == newest.TrackId)
                            ?? Tracks.FirstOrDefault();
            _loadingTracks = false;
            OnPropertyChanged(nameof(HasTracks));
            await LoadTrackAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hub load failed");
            Status = $"Could not read the hub: {ex.Message}";
        }
        finally
        {
            _loadingTracks = false;
        }
    }

    /// <summary>Reads the selected track's sessions, then fills the page on show.</summary>
    private async Task LoadTrackAsync()
    {
        var version = ++_version;
        if (SelectedTrack is not { } track)
        {
            Sessions.Clear();
            Header = "Track hub";
            Summary = "Record a session to start: what it teaches the plans can be saved here.";
            OnPropertyChanged(nameof(HasSessions));
            return;
        }

        var ticked = Sessions.Where(s => s.IsChecked).Select(Key).ToHashSet();
        var sessions = await _runtime.Hub.SessionsAsync(track.Format, track.TrackId);
        var rows = new List<HubSessionRowViewModel>();
        foreach (var session in sessions.OrderByDescending(s => s.StartTime))
        {
            var laps = session.Recording is { } recording
                ? (await _runtime.Analysis.GetClassifiedLapsAsync(recording)).Count(l => l.HasTime)
                : session.Saved!.Laps;
            var row = new HubSessionRowViewModel(session, laps, session.Recording?.Id == _runtime.Recorder.ActiveRecordingId && session.Recording?.EndTime is null,
                OnRowLengthChanged) { IsChecked = ticked.Contains(Key(session)) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HubSessionRowViewModel.IsChecked))
                {
                    OnTicksChanged();
                }
            };
            rows.Add(row);
        }

        if (version != _version)
        {
            return;
        }

        Sessions.Clear();
        foreach (var row in rows)
        {
            Sessions.Add(row);
        }

        var saved = sessions.Count(s => s.IsSaved);
        var kept = sessions.Count(s => s.IsSaved && !s.HasRecording);
        var unsaved = sessions.Count(s => !s.IsSaved);
        Header = $"{track.Name} · {Core.Games.GameCatalog.FormatName(track.Format)}";
        Summary = $"{Count(saved, "session")} saved{(kept > 0 ? $" ({kept} without a recording)" : "")} · {Count(unsaved, "recording")} not saved. "
                  + "The plans use the saved sessions as saved, and every recording not saved as it is.";
        OnPropertyChanged(nameof(HasSessions));
        OnTicksChanged();
        await ShowPageAsync();
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    // A recording keeps its key when it is saved (its hub id is new), so its tick stays across a save.
    private static (long?, long?) Key(TrackSession session) => session.Recording is { } recording ? (recording.Id, null) : (null, session.Saved?.Id);

    private static (long?, long?) Key(HubSessionRowViewModel row) => Key(row.Session);

    private void OnTicksChanged()
    {
        CheckedCount = Sessions.Count(s => s.IsChecked);
        RemoveArmed = DeleteArmed = false;
        OnPropertyChanged(nameof(SaveText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(RemoveTip));
        SaveCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        DeleteRecordingsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A saved session's race length is changed in the hub at once; an unsaved one's is used when it is saved.</summary>
    private async void OnRowLengthChanged(HubSessionRowViewModel row)
    {
        if (row.Session.Saved is not { } saved)
        {
            return;
        }

        try
        {
            _saving = true;
            await _runtime.Hub.SetRaceLengthAsync(saved, row.RaceLength);
            Status = $"{row.Title} now counts for {RaceLengths.Name(row.RaceLength).ToLowerInvariant()} races.";
        }
        catch (Exception ex)
        {
            Status = $"Could not change the race length: {ex.Message}";
        }
        finally
        {
            _saving = false;
        }
    }

    private async Task ShowPageAsync()
    {
        if (SelectedTrack is not { } track)
        {
            return;
        }

        try
        {
            switch (Page)
            {
                case HubPage.Strategy:
                    await Plans.LoadTrackAsync(track.Format, track.TrackId, track.Name);
                    break;

                case HubPage.Energy:
                    EnergyPlan.Clear(loading: true);
                    var model = await _runtime.Hub.BuildErsModelAsync(track.Format, track.TrackId);
                    var (inputs, unavailable) = await _runtime.Hub.PlanInputsAsync(track.Format, track.TrackId, model);
                    EnergyNote = $"Car model from {Count(model.PowerLaps, "lap")} with engine power at this track (the hub's and every unsaved recording's).";
                    await EnergyPlan.SetSessionAsync(inputs, unavailable);
                    break;

                case HubPage.Setups:
                    await Setups.LoadAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hub page {Page} failed", Page);
            Status = $"Could not build the {Page.ToString().ToLowerInvariant()} page: {ex.Message}";
        }
    }

    [RelayCommand]
    private void TickUnsaved()
    {
        foreach (var row in Sessions)
        {
            row.IsChecked = row.CanSave && !row.IsLive && (!row.Session.IsSaved || row.Changed);
        }
    }

    [RelayCommand]
    private void TickSaved()
    {
        foreach (var row in Sessions)
        {
            row.IsChecked = row.CanDeleteRecording;
        }
    }

    private bool CanSave() => !IsBusy && ToSave.Count > 0;

    /// <summary>Builds the preview of saving the ticked sessions: the plans' figures before and after.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SelectedTrack is not { } track)
        {
            return;
        }

        var rows = ToSave;
        IsBusy = true;
        Status = "Working out what the save changes…";
        try
        {
            var all = await _runtime.Hub.SessionsAsync(track.Format, track.TrackId);
            var keys = rows.Select(Key).ToHashSet();
            var before = all.Where(s => s.IsSaved).ToList();

            // After: the hub as it is, with the ticked sessions as their recordings are now.
            var after = all.Where(s => s.IsSaved && !keys.Contains(Key(s))).ToList();
            var fresh = rows.Select(r => r.Session with { Saved = null, RaceLength = r.RaceLength }).ToList();
            after.AddRange(fresh);
            var length = MostCommonLength(after);

            Preview.Clear();
            foreach (var row in await CompareStrategyAsync(before, after, length))
            {
                Preview.Add(row);
            }

            foreach (var row in await CompareEnergyAsync(track, before, after))
            {
                Preview.Add(row);
            }

            _pending.Clear();
            _pending.AddRange(fresh);
            var updates = rows.Count(r => r.Session.IsSaved);
            PreviewTitle = (updates == rows.Count ? $"UPDATE {rows.Count} IN THE HUB" : $"SAVE {rows.Count} TO THE HUB")
                           + $" · {Count(before.Count, "SESSION").ToUpperInvariant()} SAVED NOW";
            PreviewNote = (length == 0
                              ? "Strategy figures pooled over every race length."
                              : $"Strategy figures for {RaceLengths.Name(length).ToLowerInvariant()} races, the length most sessions here count for.")
                          + " Nothing is saved until you confirm.";
            IsPreviewing = true;
            Status = "";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hub preview failed");
            Status = $"Could not work out the save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static int MostCommonLength(IEnumerable<TrackSession> sessions) =>
        sessions.Where(s => RaceLengths.IsKnown(s.RaceLength)).GroupBy(s => s.RaceLength)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).Select(g => g.Key).FirstOrDefault();

    private async Task<List<HubCompareRow>> CompareStrategyAsync(IReadOnlyList<TrackSession> before, IReadOnlyList<TrackSession> after, int length)
    {
        async Task<CompoundLibrary> Library(IReadOnlyList<TrackSession> sessions)
        {
            var races = new List<RaceInput>();
            foreach (var session in sessions)
            {
                if (await _runtime.Analysis.LoadRaceAsync(session) is { } race)
                {
                    races.Add(race);
                }
            }

            return _runtime.Analysis.BuildTrackKnowledge(races, null, length).Library;
        }

        var (a, b) = (await Library(before), await Library(after));
        var raceLaps = b.RaceLaps ?? a.RaceLaps ?? Plans.RaceLaps;
        var rows = new List<HubCompareRow>
        {
            new("STRATEGY", "SESSIONS", $"{before.Count}", $"{after.Count}"),
            new("STRATEGY", "CLEAN LAPS", $"{a.Compounds.Sum(c => c.Laps)}", $"{b.Compounds.Sum(c => c.Laps)}"),
        };

        foreach (var compound in a.Compounds.Concat(b.Compounds).Select(c => c.Compound).Distinct().OrderBy(CompoundLibraryBuilder.CompoundOrder))
        {
            var (x, y) = (a[compound], b[compound]);
            var name = compound.ToUpperInvariant();
            rows.Add(new("STRATEGY", $"{name} PACE", Pace(x), Pace(y)));
            rows.Add(new("STRATEGY", $"{name} DEGRADATION", Format(x, c => Inv($"{c.DegradationPerLap:+0.000} s/lap")), Format(y, c => Inv($"{c.DegradationPerLap:+0.000} s/lap"))));
            rows.Add(new("STRATEGY", $"{name} WEAR", Format(x, c => c.WearPerLap > 0 ? Inv($"{c.WearPerLap:0.0} %/lap") : "—"), Format(y, c => c.WearPerLap > 0 ? Inv($"{c.WearPerLap:0.0} %/lap") : "—")));
        }

        rows.Add(new("STRATEGY", "PIT STOP", Pit(a), Pit(b)));
        rows.Add(new("STRATEGY", "FUEL", a.FuelPerLap > 0 ? Inv($"{a.FuelPerLap:0.00} kg/lap") : "—", b.FuelPerLap > 0 ? Inv($"{b.FuelPerLap:0.00} kg/lap") : "—"));
        rows.Add(new("STRATEGY", $"BEST PLAN · {raceLaps} LAPS", BestPlan(a, raceLaps), BestPlan(b, raceLaps)));
        return rows;

        static string Pace(CompoundModel? c) => c is null ? "—" : TimeFormat.Lap(c.BaseLapSeconds * 1000);
        static string Pit(CompoundLibrary l) => l.Sessions == 0 ? "—" : Inv($"{l.PitLossSeconds:0.0} s{(l.PitLossStops == 0 ? " (default)" : "")}");
        static string Format(CompoundModel? c, Func<CompoundModel, string> text) => c is null ? "—" : text(c);
    }

    private static string BestPlan(CompoundLibrary library, int raceLaps)
    {
        if (library.Compounds.Count == 0)
        {
            return "—";
        }

        var plan = StrategyPlanner.Plan(library, raceLaps).Plans.FirstOrDefault();
        if (plan is null)
        {
            return "—";
        }

        var stints = string.Join(" · ", plan.Stints.Select(s => $"{Palette.CompoundLetter(s.Compound)} {s.Laps}"));
        var time = TimeSpan.FromSeconds(plan.RaceSeconds);
        return $"{stints} · {(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";
    }

    private async Task<List<HubCompareRow>> CompareEnergyAsync(HubTrackOption track, IReadOnlyList<TrackSession> before, IReadOnlyList<TrackSession> after)
    {
        var (a, b) = (await _runtime.Hub.BuildErsModelAsync(track.Format, track.TrackId, before), await _runtime.Hub.BuildErsModelAsync(track.Format, track.TrackId, after));
        var (x, y) = ((await _runtime.Hub.PlanInputsAsync(a, before)).Inputs, (await _runtime.Hub.PlanInputsAsync(b, after)).Inputs);
        return
        [
            new("ENERGY", "CAR MODEL · LAPS WITH POWER", $"{a.PowerLaps}", $"{b.PowerLaps}"),
            new("ENERGY", "DEPLOY MODES MAPPED", $"{a.Deploy.Curves.Count}", $"{b.Deploy.Curves.Count}"),
            new("ENERGY", "RACE PLAN · TYPICAL LAPS", x is null ? "—" : $"{x.RaceLaps}", y is null ? "—" : $"{y.RaceLaps}"),
            new("ENERGY", "QUALIFYING PLAN · LAP", x is null ? "—" : TimeFormat.Lap(x.Qualifying.ActualSeconds * 1000),
                y is null ? "—" : TimeFormat.Lap(y.Qualifying.ActualSeconds * 1000)),
        ];
    }

    [RelayCommand]
    private void CancelPreview()
    {
        IsPreviewing = false;
        _pending.Clear();
        Preview.Clear();
    }

    [RelayCommand]
    private async Task ConfirmSaveAsync()
    {
        if (_pending.Count == 0)
        {
            CancelPreview();
            return;
        }

        var sessions = _pending.ToList();
        CancelPreview();
        IsBusy = true;
        Status = "Saving to the hub…";
        try
        {
            _saving = true;
            var saved = await _runtime.Hub.SaveAsync(sessions);
            _saving = false;
            await LoadTrackAsync();
            var deletable = Sessions.Count(s => s.IsChecked && s.CanDeleteRecording);
            Status = $"Saved {Count(saved, "session")} to the hub."
                     + (deletable > 0 ? $" Their recordings can now be deleted without losing what the plans use: DELETE SAVED RECORDINGS." : "");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Hub save failed");
            Status = $"Could not save to the hub: {ex.Message}";
        }
        finally
        {
            _saving = false;
            IsBusy = false;
        }
    }

    private bool CanRemove() => !IsBusy && ToRemove.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        if (!RemoveArmed)
        {
            RemoveArmed = true;
            return;
        }

        var rows = ToRemove;
        RemoveArmed = false;
        try
        {
            _saving = true;
            await _runtime.Hub.RemoveAsync([.. rows.Select(r => r.Session.Saved!)]);
            _saving = false;
            await LoadTrackAsync();
            Status = $"Took {Count(rows.Count, "session")} out of the hub.";
        }
        catch (Exception ex)
        {
            Status = $"Could not remove from the hub: {ex.Message}";
        }
        finally
        {
            _saving = false;
        }
    }

    private bool CanDeleteRecordings() => !IsBusy && ToDelete.Count > 0;

    /// <summary>Deletes the recordings of the ticked saved sessions (asks for a second click first).</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRecordings))]
    private async Task DeleteRecordingsAsync()
    {
        if (!DeleteArmed)
        {
            DeleteArmed = true;
            return;
        }

        var rows = ToDelete;
        DeleteArmed = false;
        IsBusy = true;
        long bytes = 0;
        var failed = new List<string>();
        var deleted = 0;
        try
        {
            foreach (var row in rows)
            {
                if (row.Session.Recording is not { } recording || recording.Id == _runtime.Recorder.ActiveRecordingId)
                {
                    continue;
                }

                var captures = await _runtime.DeleteRecordingAsync(recording);
                bytes += captures.Bytes;
                failed.AddRange(captures.Failed.Select(Path.GetFileName).OfType<string>());
                deleted++;
            }

            RecordingsDeleted?.Invoke();
            await LoadTrackAsync();
            var mb = (bytes / 1_000_000.0).ToString("0", CultureInfo.InvariantCulture);
            Status = $"Deleted {Count(deleted, "recording")}{(bytes > 0 ? $" and {mb} MB of raw captures" : "")}. The hub keeps what the plans use."
                     + (failed.Count > 0 ? $" Could not delete {string.Join(", ", failed)}: in use." : "");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Deleting saved recordings failed");
            Status = $"Could not delete every recording: {ex.Message}";
            RecordingsDeleted?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        DeleteRecordingsCommand.NotifyCanExecuteChanged();
    }
}
