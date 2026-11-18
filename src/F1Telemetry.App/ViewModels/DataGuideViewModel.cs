using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.Core.Analytics;

namespace F1Telemetry.App.ViewModels;

/// <summary>
/// A data guide on screen (see <see cref="Views.DataGuidePanel"/>): the score, the next run, and the cards by the kind
/// of session they are measured in. Used by the energy guide.
/// </summary>
public sealed partial class DataGuideViewModel : ObservableObject
{
    [ObservableProperty] public partial bool HasGuide { get; set; }
    [ObservableProperty] public partial string Score { get; set; } = "";
    [ObservableProperty] public partial double Progress { get; set; }

    /// <summary>The next run in one line, or what to say when everything is measured.</summary>
    [ObservableProperty] public partial string NextText { get; set; } = "";

    /// <summary>The most valuable thing still to measure; null when everything is.</summary>
    [ObservableProperty] public partial GuideRowViewModel? Next { get; set; }

    [ObservableProperty] public partial string Empty { get; set; } = "";

    public ObservableCollection<GuideRowViewModel> Practice { get; } = [];
    public ObservableCollection<GuideRowViewModel> Qualifying { get; } = [];
    public ObservableCollection<GuideRowViewModel> Race { get; } = [];

    public bool HasPractice => Practice.Count > 0;
    public bool HasQualifying => Qualifying.Count > 0;
    public bool HasRace => Race.Count > 0;

    /// <summary>No guide: <paramref name="empty"/> says why.</summary>
    public void Clear(string empty = "")
    {
        HasGuide = false;
        Empty = empty;
        Next = null;
        Score = NextText = "";
        Progress = 0;
        Practice.Clear();
        Qualifying.Clear();
        Race.Clear();
        Notify();
    }

    /// <param name="score">The headline, e.g. "4 OF 9 MEASURED AT MONZA".</param>
    /// <param name="allDone">What to say when nothing is left to measure.</param>
    public void Load(DataGuide guide, string score, string allDone)
    {
        Clear();
        var rows = guide.Items.Select(i => new GuideRowViewModel(i)).ToList();
        foreach (var row in rows)
        {
            (row.Item.Session switch { GuideSession.Race => Race, GuideSession.Qualifying => Qualifying, _ => Practice }).Add(row);
        }

        Score = score;
        Progress = guide.Total > 0 ? (double)guide.Done / guide.Total : 0;
        Next = rows.FirstOrDefault(r => r.Item == guide.Next);
        NextText = guide.Next is { } next ? $"Next: {next.Title.ToLowerInvariant()}. {next.Do}" : allDone;
        HasGuide = rows.Count > 0;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasPractice));
        OnPropertyChanged(nameof(HasQualifying));
        OnPropertyChanged(nameof(HasRace));
    }
}
