using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Setups;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App.ViewModels;

/// <summary>A setup in the library list.</summary>
public sealed partial class SetupRowViewModel(SetupEntry entry, int version, IReadOnlyList<SetupRun> runs, Action<SetupRowViewModel, bool> favouriteChanged) : ObservableObject
{
    public SetupEntry Entry { get; private set; } = entry;
    public int Version { get; } = version;
    public IReadOnlyList<SetupRun> Runs { get; } = runs;

    [ObservableProperty] public partial bool IsChecked { get; set; }
    [ObservableProperty] public partial bool Favourite { get; set; } = entry.Favourite;

    partial void OnFavouriteChanged(bool value)
    {
        OnPropertyChanged(nameof(StarBrush));
        OnPropertyChanged(nameof(FavouriteTip));
        favouriteChanged(this, value);
    }

    public IBrush StarBrush => Favourite ? Palette.Slower : Palette.LineStrong;
    public string FavouriteTip => Favourite ? "Favourite · click to unstar" : "Mark as favourite";

    [RelayCommand]
    private void ToggleFavourite() => Favourite = !Favourite;

    public string Name => SetupLibrary.DisplayName(Entry, Version);
    public string Subtitle => $"{Entry.TrackName} · {(Entry.Format == GameFormat.F1_26 ? "F1 26" : "F1 25")} · "
                              + (Runs.Count == 0 ? Entry.Source == SetupSource.Imported ? "imported" : "no runs" : Runs.Count == 1 ? "1 run" : $"{Runs.Count} runs");
    public string Wings => $"{Entry.Setup.FrontWing} / {Entry.Setup.RearWing}";

    public uint BestLapMs => Runs.Select(r => r.BestLapMs).Where(ms => ms > 0).DefaultIfEmpty().Min();
    public Chip BestLap => BestLapMs > 0 ? Chip.Plain(TimeFormat.Lap(BestLapMs), Palette.TextHi) : Chip.Empty;
    public int Laps => Runs.Sum(r => r.Laps);
    public string LastDriven => Runs.Count == 0 ? "—" : Runs.Max(r => r.RecordingStart).LocalDateTime.ToString("d MMM", CultureInfo.InvariantCulture);

    public IReadOnlyList<WeatherBucket> Weather =>
        [.. Runs.Where(r => r.Conditions is not null).Select(r => SetupLibrary.Bucket(r.Conditions!.Value.Weather)).Distinct().Order()];

    public IReadOnlyList<Chip> WeatherChips => Weather.Count == 0
        ? [Chip.Empty]
        : [.. Weather.Select(b => b == WeatherBucket.Dry ? Chip.Outlined("DRY", Palette.TextMid) : Chip.Filled(SetupLibrary.BucketName(b).ToUpperInvariant(), Palette.Info))];

    public string TrackTemperatures
    {
        get
        {
            var temps = Runs.Where(r => r.Conditions is not null).Select(r => (int)r.Conditions!.Value.TrackTemperature).ToList();
            return temps.Count == 0 ? "—" : temps.Min() == temps.Max() ? $"{temps[0]} °C" : $"{temps.Min()}–{temps.Max()} °C";
        }
    }

    public void Rename(string? name)
    {
        Entry = Entry with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
        OnPropertyChanged(nameof(Name));
    }

    public void SetNotes(string notes) => Entry = Entry with { Notes = notes };
}

public sealed record SettingRowViewModel(string Label, string Value, string Other, string Change, bool IsChanged)
{
    public IBrush ValueBrush => IsChanged ? Palette.TextHi : Palette.TextMid;
}

public sealed record SettingGroupViewModel(string Name, IReadOnlyList<SettingRowViewModel> Settings);

public sealed record SetupRunViewModel(SetupRun Run)
{
    public string When => Run.RecordingStart.LocalDateTime.ToString("d MMM yyyy · HH:mm", CultureInfo.InvariantCulture);
    public string Session => SessionTypes.Name(Run.SessionType);
    public string Laps => Run.ToLap > Run.FromLap ? $"L{Run.FromLap}–{Run.ToLap}" : $"L{Run.FromLap}";
    public Chip Best => Run.BestLapMs > 0 ? Chip.Plain(TimeFormat.Lap(Run.BestLapMs), Palette.TextHi) : Chip.Empty;
    public string TopSpeed => Run.TopSpeed > 0 ? $"{Run.TopSpeed} km/h" : "—";
    public string Conditions => Run.Conditions is { } c
        ? $"{WeatherTypes.Name(c.Weather)} · track {c.TrackTemperature} °C · air {c.AirTemperature} °C"
        : "Conditions not recorded";
}

/// <summary>A setup to compare the selected one with; <see cref="Row"/> is null for "no comparison".</summary>
public sealed record SetupCompareOption(SetupRowViewModel? Row, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Setups tab: every setup driven in a recording (or imported), per track and format, with the conditions and results
/// of each run. Setups can be renamed, annotated, starred, compared setting by setting, removed, and the ticked ones
/// exported to a <c>.f1setups</c> file; files can be imported.
/// </summary>
public sealed partial class SetupsViewModel(TelemetryRuntime runtime, ILogger<SetupsViewModel> log) : ObservableObject
{
    public const string AllTracks = "ALL TRACKS";
    public static IReadOnlyList<string> WeatherFilters { get; } = ["ANY WEATHER", "DRY", "WET", "VERY WET"];

    private readonly List<SetupRowViewModel> _all = [];
    private bool _loading;

    [ObservableProperty] public partial string Header { get; set; } = "Setups";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string TrackFilter { get; set; } = AllTracks;
    [ObservableProperty] public partial string WeatherFilter { get; set; } = WeatherFilters[0];
    [ObservableProperty] public partial bool FavouritesOnly { get; set; }
    [ObservableProperty] public partial SetupRowViewModel? SelectedSetup { get; set; }
    [ObservableProperty] public partial SetupCompareOption? CompareWith { get; set; }
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string EditNotes { get; set; } = "";
    [ObservableProperty] public partial bool RemoveArmed { get; set; }
    [ObservableProperty] public partial int CheckedCount { get; set; }

    public ObservableCollection<string> Tracks { get; } = [AllTracks];
    public ObservableCollection<SetupRowViewModel> Rows { get; } = [];
    public ObservableCollection<SetupCompareOption> CompareOptions { get; } = [];
    public ObservableCollection<SettingGroupViewModel> Groups { get; } = [];
    public ObservableCollection<SetupRunViewModel> Runs { get; } = [];

    /// <summary>Set by the view: a save dialog for the export, given a suggested file name; null when cancelled.</summary>
    public Func<string, Task<string?>>? PickExportFile { get; set; }

    /// <summary>Set by the view: an open dialog for a setup file; null when cancelled.</summary>
    public Func<Task<string?>>? PickImportFile { get; set; }

    public bool HasSetups => _all.Count > 0;
    public bool HasSelection => SelectedSetup is not null;
    public bool IsComparing => CompareWith?.Row is not null;
    public bool HasRuns => Runs.Count > 0;
    public string RemoveText => RemoveArmed ? "CLICK AGAIN TO REMOVE" : "REMOVE";
    public string CheckedText => CheckedCount switch { 0 => "NONE TICKED", _ => $"{CheckedCount} TICKED" };

    partial void OnTrackFilterChanged(string value) => ApplyFilter();
    partial void OnWeatherFilterChanged(string value) => ApplyFilter();
    partial void OnFavouritesOnlyChanged(bool value) => ApplyFilter();
    partial void OnRemoveArmedChanged(bool value) => OnPropertyChanged(nameof(RemoveText));

    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(CheckedText));
        ExportCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedSetupChanged(SetupRowViewModel? value)
    {
        RemoveArmed = false;
        OnPropertyChanged(nameof(HasSelection));
        _loading = true;
        EditName = value?.Entry.Name ?? "";
        EditNotes = value?.Entry.Notes ?? "";
        _loading = false;

        CompareOptions.Clear();
        Runs.Clear();
        if (value is not null)
        {
            CompareOptions.Add(new SetupCompareOption(null, "NO COMPARISON"));
            foreach (var other in _all.Where(r => r != value && r.Entry.Format == value.Entry.Format && r.Entry.TrackId == value.Entry.TrackId))
            {
                CompareOptions.Add(new SetupCompareOption(other, other.Name));
            }

            foreach (var run in value.Runs)
            {
                Runs.Add(new SetupRunViewModel(run));
            }
        }

        OnPropertyChanged(nameof(HasRuns));
        var previous = value is null ? null : SetupLibrary.Previous(value.Entry, _all.Select(r => r.Entry));
        CompareWith = CompareOptions.FirstOrDefault(o => o.Row?.Entry.Id == previous?.Id && previous is not null) ?? CompareOptions.FirstOrDefault();
        BuildGroups();
    }

    partial void OnCompareWithChanged(SetupCompareOption? value)
    {
        OnPropertyChanged(nameof(IsComparing));
        BuildGroups();
    }

    partial void OnEditNameChanged(string value)
    {
        if (_loading || SelectedSetup is not { } row || value == (row.Entry.Name ?? ""))
        {
            return;
        }

        runtime.Store.UpdateSetup(row.Entry.Id, name: value);
        row.Rename(value);
    }

    partial void OnEditNotesChanged(string value)
    {
        if (_loading || SelectedSetup is not { } row || value == row.Entry.Notes)
        {
            return;
        }

        runtime.Store.UpdateSetup(row.Entry.Id, notes: value);
        row.SetNotes(value);
    }

    /// <summary>Reloads the library, adding setups driven since the last time, and keeps the selection.</summary>
    public async Task LoadAsync()
    {
        try
        {
            await runtime.Store.FlushAsync();
            var library = await runtime.Store.GetSetupLibraryAsync();
            var runs = (await runtime.Store.GetSetupRunsAsync()).ToLookup(r => r.SetupId);
            var versions = SetupLibrary.Versions(library);
            var selected = SelectedSetup?.Entry.Id;
            var ticked = _all.Where(r => r.IsChecked).Select(r => r.Entry.Id).ToHashSet();

            foreach (var row in _all)
            {
                row.PropertyChanged -= OnRowChanged;
            }

            _all.Clear();
            foreach (var entry in library)
            {
                var row = new SetupRowViewModel(entry, versions[entry.Id], [.. runs[entry.Id]], OnFavouriteChanged) { IsChecked = ticked.Contains(entry.Id) };
                row.PropertyChanged += OnRowChanged;
                _all.Add(row);
            }

            var filter = TrackFilter;
            Tracks.Clear();
            Tracks.Add(AllTracks);
            foreach (var track in _all.Select(r => r.Entry.TrackName).Distinct().Order())
            {
                Tracks.Add(track);
            }

            TrackFilter = Tracks.Contains(filter) ? filter : AllTracks;
            Header = _all.Count == 1 ? "1 setup" : $"{_all.Count} setups";
            OnPropertyChanged(nameof(HasSetups));
            CheckedCount = _all.Count(r => r.IsChecked);
            ApplyFilter();
            SelectedSetup = Rows.FirstOrDefault(r => r.Entry.Id == selected) ?? Rows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Loading the setup library failed");
            Status = $"Could not load the setups: {ex.Message}";
        }
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SetupRowViewModel.IsChecked))
        {
            CheckedCount = _all.Count(r => r.IsChecked);
        }
    }

    private void OnFavouriteChanged(SetupRowViewModel row, bool favourite)
    {
        runtime.Store.UpdateSetup(row.Entry.Id, favourite: favourite);
        if (FavouritesOnly && !favourite)
        {
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var selected = SelectedSetup;
        var bucket = WeatherFilters.ToList().IndexOf(WeatherFilter) - 1; // -1 = any
        Rows.Clear();
        foreach (var row in _all
                     .Where(r => TrackFilter == AllTracks || r.Entry.TrackName == TrackFilter)
                     .Where(r => bucket < 0 || r.Weather.Contains((WeatherBucket)bucket))
                     .Where(r => !FavouritesOnly || r.Favourite)
                     .OrderByDescending(r => r.Favourite)
                     .ThenBy(r => r.Entry.TrackName)
                     .ThenByDescending(r => r.Version))
        {
            Rows.Add(row);
        }

        SelectedSetup = selected is not null && Rows.Contains(selected) ? selected : Rows.FirstOrDefault();
    }

    private void BuildGroups()
    {
        Groups.Clear();
        if (SelectedSetup is not { } row)
        {
            return;
        }

        var other = CompareWith?.Row?.Entry.Setup;
        foreach (var group in SetupSettings.All.GroupBy(s => s.Group))
        {
            Groups.Add(new SettingGroupViewModel(group.Key.ToUpperInvariant(), [.. group.Select(setting =>
            {
                var value = setting.Format(row.Entry.Setup) + Unit(setting);
                if (other is not { } compare)
                {
                    return new SettingRowViewModel(setting.Label, value, "", "", false);
                }

                var changed = setting.Differs(row.Entry.Setup, compare);
                var delta = setting.Get(row.Entry.Setup) - setting.Get(compare);
                var change = !changed ? "" : (delta > 0 ? "▲ +" : "▼ −") + setting.Format(Math.Abs(delta));
                return new SettingRowViewModel(setting.Label, value, setting.Format(compare) + Unit(setting), change, changed);
            })]));
        }
    }

    private static string Unit(SetupSetting setting) => setting.Unit switch { "" => "", "°" => "°", var unit => " " + unit };

    [RelayCommand]
    private void ClearFilters()
    {
        TrackFilter = AllTracks;
        WeatherFilter = WeatherFilters[0];
        FavouritesOnly = false;
    }

    [RelayCommand]
    private void TickShown()
    {
        var all = Rows.All(r => r.IsChecked);
        foreach (var row in Rows)
        {
            row.IsChecked = !all;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var chosen = _all.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0 || PickExportFile is null)
        {
            return;
        }

        var suggested = chosen.Select(r => r.Entry.TrackName).Distinct().Count() == 1 ? $"{chosen[0].Entry.TrackName} setups" : "Setups";
        if (await PickExportFile(suggested + SetupFile.Extension) is not { } path)
        {
            return;
        }

        try
        {
            var json = SetupFile.Write(chosen.Select(r => (r.Entry, r.Name, (SetupFile.Stats?)new SetupFile.Stats(r.BestLapMs, r.Laps))), DateTimeOffset.Now);
            await File.WriteAllTextAsync(path, json);
            Status = $"Exported {Count(chosen.Count)} to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Setup export failed");
            Status = $"Could not export: {ex.Message}";
        }
    }

    private bool CanExport() => CheckedCount > 0;

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (PickImportFile is null || await PickImportFile() is not { } path)
        {
            return;
        }

        try
        {
            var setups = SetupFile.Read(await File.ReadAllTextAsync(path));
            var result = await runtime.Store.ImportSetupsAsync(setups);
            await LoadAsync();
            Status = (result.Added, result.AlreadyThere) switch
            {
                (0, 0) => $"{Path.GetFileName(path)} has no setups.",
                (0, var already) => $"Nothing new: the library already has {(already == 1 ? "this setup" : $"all {already} setups")}.",
                (var added, 0) => $"Added {Count(added)}.",
                var (added, already) => $"Added {Count(added)}; {already} {(already == 1 ? "was" : "were")} already in the library.",
            };
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Status = $"Could not import {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    /// <summary>Removes the selected setup. The first click arms the button; the second removes.</summary>
    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedSetup is not { } row)
        {
            return;
        }

        if (!RemoveArmed)
        {
            RemoveArmed = true;
            return;
        }

        runtime.Store.UpdateSetup(row.Entry.Id, removed: true);
        Status = $"Removed {row.Name}. Importing a file that has it brings it back.";
        await LoadAsync();
    }

    private static string Count(int setups) => setups == 1 ? "1 setup" : $"{setups} setups";
}
