using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.App.ViewModels;

/// <summary>
/// TV-style qualifying box: position, name and the reference lap on top; three colour-only sector bars; the lap
/// time below them. For 4 s after a split the sector's time and its gap to the reference (coloured text) replace the
/// running time; at the line the finished lap time is shown with its gap.
/// </summary>
public sealed partial class SectorBoxOverlayViewModel : OverlayViewModelBase
{
    [ObservableProperty] public partial string Position { get; set; } = "–";
    [ObservableProperty] public partial IBrush PositionBackground { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial IBrush TeamBrush { get; set; } = Palette.LineStrong;
    [ObservableProperty] public partial string ReferenceLabel { get; set; } = "";
    [ObservableProperty] public partial string ReferenceTime { get; set; } = "";
    [ObservableProperty] public partial SectorMark Sector1 { get; set; }
    [ObservableProperty] public partial SectorMark Sector2 { get; set; }
    [ObservableProperty] public partial SectorMark Sector3 { get; set; }
    [ObservableProperty] public partial double Progress1 { get; set; }
    [ObservableProperty] public partial double Progress2 { get; set; }
    [ObservableProperty] public partial double Progress3 { get; set; }
    [ObservableProperty] public partial string LapTime { get; set; } = "0:00.000";
    [ObservableProperty] public partial IBrush LapTimeBackground { get; set; } = Palette.Transparent;
    [ObservableProperty] public partial IBrush LapTimeForeground { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial bool IsInvalid { get; set; }
    [ObservableProperty] public partial bool ShowLapTime { get; set; } = true;
    [ObservableProperty] public partial bool ShowSplit { get; set; }
    [ObservableProperty] public partial string SplitLabel { get; set; } = "";
    [ObservableProperty] public partial string SplitTime { get; set; } = "";
    [ObservableProperty] public partial bool HasDelta { get; set; }
    [ObservableProperty] public partial string Delta { get; set; } = "";
    [ObservableProperty] public partial IBrush DeltaBrush { get; set; } = Palette.TextHi;

    /// <summary>What the sector gap compares against (settings).</summary>
    public SectorReference Reference { get; set; } = SectorReference.SessionBest;

    /// <param name="qualifying">Qualifying-type session: a lap that beats everyone is "provisional pole", otherwise "fastest lap".</param>
    public void Update(SectorBoxSnapshot box, uint? teamColour, bool qualifying)
    {
        var bySession = Reference == SectorReference.SessionBest;
        var reference = bySession ? box.SessionBest : box.PersonalBest;
        var newBest = box is { IsFinished: true, LapIsSessionBest: true };

        Position = box.Position > 0 ? $"P{box.Position}" : "–";
        PositionBackground = newBest ? Palette.SessionBest : Palette.TextHi;
        TeamBrush = Palette.Team(teamColour);
        (ReferenceLabel, ReferenceTime) = newBest
            ? (qualifying ? "PROVISIONAL POLE" : "FASTEST LAP", "")
            : reference is null
                ? ("", "")
                : (bySession ? $"P1 {reference.Code}" : "PB", TimeFormat.Lap(reference.LapMs));

        Sector1 = box.Sectors[0];
        Sector2 = box.Sectors[1];
        Sector3 = box.Sectors[2];
        Progress1 = Sector1 == SectorMark.Live ? box.LiveProgress : 0;
        Progress2 = Sector2 == SectorMark.Live ? box.LiveProgress : 0;
        Progress3 = Sector3 == SectorMark.Live ? box.LiveProgress : 0;

        LapTime = box.LapTimeMs > 0 ? TimeFormat.Lap(box.LapTimeMs) : "0:00.000";
        IsInvalid = box.IsInvalid;
        (LapTimeBackground, LapTimeForeground) = box switch
        {
            { IsInvalid: true } => (Palette.Transparent, Palette.TextLo),
            { IsFinished: false } => (Palette.Transparent, Palette.TextHi),
            { LapIsSessionBest: true } => (Palette.SessionBest, Palette.OnColor),
            { LapIsPersonalBest: true } => (Palette.Faster, Palette.OnColor),
            _ => (Palette.Slower, Palette.OnColor),
        };

        // A sector split (4 s) replaces the running time; the finished lap keeps its lap time next to the gap.
        var split = box.LastSplit;
        ShowSplit = split is not null && !box.IsFinished;
        ShowLapTime = !ShowSplit;
        SplitLabel = split is null ? "" : $"S{split.Sector}";
        SplitTime = split is null ? "" : TimeFormat.Sector(split.SectorMs);

        var delta = split is null ? null : bySession ? split.DeltaToSessionBestMs : split.DeltaToPersonalBestMs;
        if (box.IsInvalid)
        {
            (HasDelta, Delta, DeltaBrush) = (true, "INVALID", Palette.Danger);
        }
        else if (delta is { } ms)
        {
            var colour = split!.Sector == 3 && box.LapIsSessionBest ? Palette.SessionBest : ms < 0 ? Palette.Faster : Palette.Slower;
            (HasDelta, Delta, DeltaBrush) = (true, TimeFormat.Delta(ms).Replace('-', '−'), colour);
        }
        else
        {
            (HasDelta, Delta, DeltaBrush) = (false, "", Palette.TextHi);
        }
    }

    public override void LoadPreview() => Update(new SectorBoxSnapshot
    {
        Position = 3,
        LapTimeMs = 63_448,
        Sectors = [SectorMark.SessionBest, SectorMark.Slower, SectorMark.Live],
        LiveProgress = 0.55,
        LastSplit = new SectorSplit(2, 27_905, 50, -123),
        SessionBest = new ReferenceLap(4, "NOR", 72_310, 23_386, 27_771),
        PersonalBest = new ReferenceLap(0, "YOU", 72_604, 23_450, 27_880),
    }, 0xE8002D, qualifying: true);

    public override void Reset() => Update(new SectorBoxSnapshot(), null, qualifying: false);
}

/// <summary>One row of the timing tower. Kept alive between updates so the 20 Hz refresh only changes values.</summary>
public sealed partial class TowerRowViewModel : ObservableObject
{
    private static readonly IBrush RowBrush = new ImmutableSolidColorBrush(Color.Parse("#0D0F13"));

    [ObservableProperty] public partial string Position { get; set; } = "";
    [ObservableProperty] public partial IBrush PositionBrush { get; set; } = Palette.TextMid;
    [ObservableProperty] public partial IBrush TeamBrush { get; set; } = Palette.LineStrong;
    [ObservableProperty] public partial string Code { get; set; } = "";
    [ObservableProperty] public partial string Gap { get; set; } = "";
    [ObservableProperty] public partial IBrush GapBrush { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial bool IsGapChip { get; set; }
    [ObservableProperty] public partial bool IsGapText { get; set; } = true;
    [ObservableProperty] public partial string Compound { get; set; } = "Unknown";
    [ObservableProperty] public partial string TyreAge { get; set; } = "";
    [ObservableProperty] public partial bool HasErs { get; set; }
    [ObservableProperty] public partial string Ers { get; set; } = "—";
    [ObservableProperty] public partial IBrush ErsBrush { get; set; } = Palette.TextLo;
    [ObservableProperty] public partial double ErsFill { get; set; }
    [ObservableProperty] public partial bool HasPenalty { get; set; }
    [ObservableProperty] public partial string Penalty { get; set; } = "";
    [ObservableProperty] public partial bool HasWarnings { get; set; }
    [ObservableProperty] public partial string Warnings { get; set; } = "";
    [ObservableProperty] public partial IBrush RowBackground { get; set; } = RowBrush;
    [ObservableProperty] public partial IBrush RowBorder { get; set; } = Palette.Transparent;
    [ObservableProperty] public partial bool IsCutoff { get; set; }

    /// <summary>Width of the battery bar's fill (the bar is 22 px inside its 2 px border).</summary>
    public const double ErsBarWidth = 22;

    public void Apply(TowerRow row)
    {
        Position = row.Position.ToString();
        PositionBrush = row.IsPlayer ? Palette.TextHi : Palette.TextMid;
        TeamBrush = Palette.Team(row.TeamColour);
        Code = row.Code;
        Gap = row.Gap;
        IsGapChip = row.Tone is GapTone.Pit or GapTone.Out;
        IsGapText = !IsGapChip;
        GapBrush = row.Tone switch
        {
            GapTone.Ahead => Palette.Faster,
            GapTone.Threat => Palette.Slower,
            GapTone.Pit => Palette.Info,
            GapTone.Muted or GapTone.Out => Palette.TextLo,
            _ => Palette.TextHi,
        };
        Compound = row.Compound.ToString();
        TyreAge = row.Compound == VisualCompound.Unknown ? "" : row.TyreAgeLaps.ToString();
        HasErs = row.ErsPercent is not null;
        Ers = row.ErsPercent is { } ers ? $"{ers:0}%" : "—";
        ErsBrush = row.ErsPercent is { } e ? Palette.Ers(e) : Palette.TextLo;
        ErsFill = (row.ErsPercent ?? 0) / 100 * ErsBarWidth;
        HasPenalty = row.Penalty is not null;
        Penalty = row.Penalty ?? "";
        HasWarnings = row.Warnings is not null;
        Warnings = row.Warnings ?? "";
        RowBackground = row.IsPlayer ? Palette.Selected : RowBrush;
        RowBorder = row.IsPlayer ? Palette.LineStrong : Palette.Transparent;
        IsCutoff = row.CutoffBelow;
    }
}

/// <summary>Compact tower of the six cars around the player: gap, tyre and age, battery, penalties.</summary>
public sealed partial class TimingTowerOverlayViewModel : OverlayViewModelBase
{
    [ObservableProperty] public partial string Title { get; set; } = "WAITING FOR DATA";
    [ObservableProperty] public partial string Counter { get; set; } = "";
    [ObservableProperty] public partial string CounterTotal { get; set; } = "";
    [ObservableProperty] public partial string Mode { get; set; } = "";
    [ObservableProperty] public partial string GapHeader { get; set; } = "GAP";
    [ObservableProperty] public partial string LegendLeft { get; set; } = "";
    [ObservableProperty] public partial IBrush LegendLeftBrush { get; set; } = Palette.Faster;
    [ObservableProperty] public partial string LegendRight { get; set; } = "";
    [ObservableProperty] public partial IBrush LegendRightBrush { get; set; } = Palette.Slower;
    [ObservableProperty] public partial bool HasLegendLeft { get; set; }

    public ObservableCollection<TowerRowViewModel> Rows { get; } = [];

    /// <summary>Race gap column (settings).</summary>
    public TowerGapMode GapMode { get; set; } = TowerGapMode.GapToMe;

    public void Update(FieldSnapshot field) => Show(TimingTower.Build(field, GapMode));

    private void Show(TowerBoard board)
    {
        Title = board.Title;
        Counter = board.Counter;
        CounterTotal = board.CounterTotal;
        Mode = board.Mode;
        var cutoff = board.Rows.Any(r => r.CutoffBelow) || board.Mode.StartsWith("CUT-OFF", StringComparison.Ordinal);
        (GapHeader, LegendLeft, LegendLeftBrush, LegendRight) = board switch
        {
            { IsRace: false } => ("BEST Δ", cutoff ? "ELIMINATION LINE" : "", Palette.Danger, "<0.1 S BEHIND"),
            _ when GapMode == TowerGapMode.Interval => ("INT", "YOU IN DRS RANGE", Palette.Faster, "THREAT BEHIND"),
            _ => ("GAP", "AHEAD <1 S", Palette.Faster, "BEHIND <1 S"),
        };
        HasLegendLeft = LegendLeft.Length > 0;
        LegendRightBrush = Palette.Slower;

        while (Rows.Count > board.Rows.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        while (Rows.Count < board.Rows.Count)
        {
            Rows.Add(new TowerRowViewModel());
        }

        for (var i = 0; i < board.Rows.Count; i++)
        {
            Rows[i].Apply(board.Rows[i]);
        }
    }

    public override void LoadPreview()
    {
        CarTiming Car(int position, string code, uint team, long gapMs, string compound, int age, double? ers, int penalty = 0, int warnings = 0,
            bool player = false, PitStatus pit = PitStatus.None) => new()
        {
            CarIndex = position,
            Position = position,
            Code = code,
            TeamColour = team,
            IsPlayer = player,
            DeltaToLeaderMs = (uint)(20_000 + gapMs),
            TotalDistance = 50_000,
            CurrentLap = 14,
            PitStatus = pit,
            ResultStatus = ResultStatus.Active,
            Compound = Enum.Parse<VisualCompound>(compound),
            TyreAgeLaps = age,
            ErsPercent = ers,
            PenaltySeconds = penalty,
            TrackLimitWarnings = warnings,
        };

        Update(new FieldSnapshot(
        [
            Car(4, "PIA", 0xFF8000, -3412, "Medium", 14, 71),
            Car(5, "LEC", 0xE8002D, -1905, "Hard", 8, 38, penalty: 5),
            Car(6, "RUS", 0x27F4D2, -644, "Medium", 14, 12, warnings: 2),
            Car(7, "YOU", 0xE8002D, 0, "Medium", 14, 54, player: true),
            Car(8, "ALO", 0x229971, 812, "Soft", 3, 88),
            Car(9, "GAS", 0x0093CC, 2307, "Hard", 0, null, pit: PitStatus.Pitting),
        ], 7, 15, 30, 0, 5000));
    }

    public override void Reset()
    {
        Show(TowerBoard.Empty);
        GapHeader = "GAP";
        HasLegendLeft = false;
        LegendRight = "";
    }
}
