using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Games;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Tracks;
using F1Telemetry.Protocol;

namespace F1Telemetry.App.ViewModels;

public sealed record PacketStatRow(string Packet, long Count);

/// <summary>One wheel cell of the live tyre grid (wear band colours from the template).</summary>
public sealed record WheelCell(string Name, string Temperature, string Wear, IBrush Background, IBrush Foreground, IBrush Border);

/// <summary>Live dashboard: hero value tiles, inputs, tyres, fuel, current-lap trace + map, ingest diagnostics.</summary>
public sealed partial class LiveViewModel : ObservableObject
{
    private readonly TelemetryRuntime _runtime;
    private readonly LiveDataHub _hub;
    private readonly List<TelemetrySample> _currentLap = [];
    private int _currentLapNumber;
    private DateTime _lastChart = DateTime.MinValue;
    private DateTime _lastSampleAt = DateTime.MinValue;
    private long _lastTotal;
    private long _lastRejectedMode;
    private DateTime _lastStats = DateTime.UtcNow;
    private readonly DispatcherTimer _statsTimer;

    public LiveViewModel(TelemetryRuntime runtime, LiveDataHub hub, OverlayManager overlays)
    {
        _runtime = runtime;
        _hub = hub;
        Radar = overlays.Radar;
        Conditions = overlays.TrackConditions;
        hub.Tick += OnTick;
        hub.DeltaUpdated += OnDelta;

        // Independent of incoming samples: rejected packets (wrong mode / size) never reach the engine.
        _statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats(DateTime.UtcNow));
        _statsTimer.Start();
        hub.SessionChanged += s =>
        {
            Session = $"{s.TrackName} · {s.SessionTypeName} · {GameCatalog.FormatName(s.Format)}";
            _ = UpdateGuideAsync(s);
            TotalLaps = s.TotalLaps > 0 ? $"/{s.TotalLaps}" : "";
            Outline = runtime.Tracks.ForTrackId(s.TrackId);
        };

        // A recorded lap can tick an item of the guide off; the lap reaches the store a moment after the line.
        hub.LapCompleted += lap =>
        {
            if (runtime.Recorder.IsRecording && hub.Session is { } session)
            {
                DispatcherTimer.RunOnce(() => { _ = UpdateGuideAsync(session, force: true); }, TimeSpan.FromSeconds(2));
            }
        };
    }

    public RadarOverlayViewModel Radar { get; }
    public TrackConditionsOverlayViewModel Conditions { get; }
    public ObservableCollection<PacketStatRow> PacketStats { get; } = [];
    public ObservableCollection<WheelCell> Wheels { get; } = [];

    [ObservableProperty] public partial string Session { get; set; } = "Waiting for telemetry…";
    [ObservableProperty] public partial bool IsReceiving { get; set; }
    [ObservableProperty] public partial string Speed { get; set; } = "–";
    [ObservableProperty] public partial string Gear { get; set; } = "–";
    [ObservableProperty] public partial string Rpm { get; set; } = "";
    [ObservableProperty] public partial string Lap { get; set; } = "–";
    [ObservableProperty] public partial string TotalLaps { get; set; } = "";
    [ObservableProperty] public partial string Position { get; set; } = "–";
    [ObservableProperty] public partial double Throttle { get; set; }
    [ObservableProperty] public partial double Brake { get; set; }
    [ObservableProperty] public partial string ThrottleText { get; set; } = "0%";
    [ObservableProperty] public partial string BrakeText { get; set; } = "0%";
    [ObservableProperty] public partial string Delta { get; set; } = "–";
    [ObservableProperty] public partial IBrush DeltaBackground { get; set; } = Palette.Panel;
    [ObservableProperty] public partial IBrush DeltaForeground { get; set; } = Palette.TextHi;
    [ObservableProperty] public partial string Compound { get; set; } = "Unknown";
    [ObservableProperty] public partial string TyreHeader { get; set; } = "TYRE WEAR";
    [ObservableProperty] public partial string TyreLapsLeft { get; set; } = "";
    [ObservableProperty] public partial IBrush TyreLapsBrush { get; set; } = Palette.Faster;
    [ObservableProperty] public partial string Fuel { get; set; } = "–";
    [ObservableProperty] public partial string FuelRange { get; set; } = "–";
    [ObservableProperty] public partial IBrush FuelRangeBrush { get; set; } = Palette.Slower;
    [ObservableProperty] public partial string Ers { get; set; } = "–";
    [ObservableProperty] public partial string IngestStatus { get; set; } = "";
    [ObservableProperty] public partial string ModeWarning { get; set; } = "";
    [ObservableProperty] public partial bool HasModeWarning { get; set; }
    [ObservableProperty] public partial TrackOutline? Outline { get; set; }
    [ObservableProperty] public partial IReadOnlyList<TelemetrySample>? CurrentLapSamples { get; set; }
    [ObservableProperty] public partial ChartModel? CurrentLapChart { get; set; }

    /// <summary>
    /// What to drive in the session on now to feed the race plans at this track (the first open item of the data guide
    /// for this kind of session); empty when the session has no kind (time trial) or no track yet.
    /// </summary>
    [ObservableProperty] public partial string Guide { get; set; } = "";

    [ObservableProperty] public partial bool HasGuide { get; set; }

    private (GameFormat Format, int TrackId, int SessionType)? _guideFor;

    /// <summary>Works the guide out again for a session it hasn't seen, or when <paramref name="force"/> (a recorded lap may have ticked an item off).</summary>
    private async Task UpdateGuideAsync(SessionInfo session, bool force = false)
    {
        var key = (session.Format, session.TrackId, session.SessionType);
        if (!force && _guideFor == key)
        {
            return;
        }

        _guideFor = key;
        var text = "";
        if (session.TrackId >= 0 && DataGuide.SessionOf(session.SessionType) is { } kind)
        {
            try
            {
                await _runtime.Store.FlushAsync();
                var knowledge = await _runtime.Analysis.BuildTrackKnowledgeAsync(session.Format, session.TrackId, null);
                text = knowledge.Guide.NextIn(kind) switch
                {
                    { } next => $"FOR THE RACE PLANS · {next.Title.ToUpperInvariant()}: {next.Do}",
                    null when kind == GuideSession.Practice => "FOR THE RACE PLANS · Every figure they use is measured here. More clean laps still steady them.",
                    null when kind == GuideSession.Qualifying => "FOR THE RACE PLANS · Keep recording: the tyre sets (used, and how worn) carry into the plan's START ON.",
                    _ => "FOR THE RACE PLANS · Record to the flag: this race's stops and laps add to what is known here.",
                };
            }
            catch (Exception)
            {
                // The guide is a hint: the dashboard carries on without it.
            }
        }

        if (_guideFor == key)
        {
            Guide = text;
            HasGuide = text.Length > 0;
        }
    }

    private void OnDelta(DeltaUpdate delta)
    {
        var faster = delta.LapDeltaMs < 0 || delta.LapColor == DeltaColor.SessionBest;
        Delta = (faster ? "▼" : "▲") + TimeFormat.Delta(delta.LapDeltaMs).Replace('-', '−');
        DeltaBackground = delta.LapColor == DeltaColor.SessionBest ? Palette.SessionBest : faster ? Palette.Faster : Palette.Slower;
        DeltaForeground = Palette.OnColor;
    }

    private void OnTick()
    {
        foreach (var sample in _hub.DrainSamples())
        {
            // Outside races the game can cross the line without a new lap number (a run ending in the pits): start over there too.
            if (sample.LapNumber != _currentLapNumber
                || (_currentLap.Count > 0 && LapRestarts.IsRestart(_currentLap[^1], sample) && LapRestarts.Applies(_hub.Session?.SessionType ?? 0)))
            {
                _currentLap.Clear();
                _currentLapNumber = sample.LapNumber;
            }

            _currentLap.Add(sample);
            _lastSampleAt = DateTime.UtcNow;
        }

        if (_hub.LastSample is { } s)
        {
            Speed = s.Speed.ToString(CultureInfo.InvariantCulture);
            Gear = s.Gear switch { 0 => "N", -1 => "R", _ => s.Gear.ToString(CultureInfo.InvariantCulture) };
            Rpm = s.Rpm.ToString("N0", CultureInfo.InvariantCulture);
            Lap = s.LapNumber.ToString(CultureInfo.InvariantCulture);
            Position = $"P{s.Position}";
            Throttle = s.Throttle;
            Brake = s.Brake;
            ThrottleText = $"{s.Throttle * 100:0}%";
            BrakeText = $"{s.Brake * 100:0}%";
            Ers = $"{s.ErsStoreEnergy / 1_000_000:0.00}";
            UpdateWheels(s);
        }

        if (_hub.Strategy is { } strategy)
        {
            Compound = strategy.Compound;
            TyreHeader = $"TYRE WEAR · {strategy.TyresAgeLaps} LAPS OLD";
            TyreLapsLeft = strategy.TyreLapsRemaining is { } t ? $"{t:0.0} LAPS LEFT" : "CALCULATING";
            TyreLapsBrush = strategy.TyreLapsRemaining switch { null => Palette.TextMid, < 3 => Palette.Danger, < 8 => Palette.Slower, _ => Palette.Faster };
            Fuel = $"{strategy.FuelInTank:0.00}";
            FuelRange = strategy.FuelLapsRemaining is { } f ? $"{f:0.0}" : $"{strategy.GameFuelRemainingLaps:+0.0;−0.0}";
            FuelRangeBrush = strategy.FuelLapsRemaining is < 1.5 ? Palette.Danger : Palette.Slower;
        }

        var now = DateTime.UtcNow;
        if (now - _lastChart > TimeSpan.FromMilliseconds(500) && _currentLap.Count > 1)
        {
            _lastChart = now;
            var snapshot = _currentLap.ToArray();
            CurrentLapSamples = snapshot;
            var x = LapAxis.LapTimes(snapshot);
            CurrentLapChart = new ChartModel($"Lap {_currentLapNumber} · speed (km/h)",
            [
                new ChartSeries("Speed", "#F4F6F9", x, snapshot.Select(p => (double)p.Speed).ToArray()),
            ], LapAxis.TimeLabel, XIsTime: true);
        }
    }

    private void UpdateWheels(TelemetrySample s)
    {
        (string Name, int Temp, double Wear)[] wheels =
        [
            ("FL", s.TyresSurfaceTempFl, s.TyreWearFl),
            ("FR", s.TyresSurfaceTempFr, s.TyreWearFr),
            ("RL", s.TyresSurfaceTempRl, s.TyreWearRl),
            ("RR", s.TyresSurfaceTempRr, s.TyreWearRr),
        ];

        for (var i = 0; i < wheels.Length; i++)
        {
            var (bg, fg, border) = Palette.Wear(wheels[i].Wear);
            var cell = new WheelCell(wheels[i].Name, $"{wheels[i].Temp}°", $"{wheels[i].Wear:0}%", bg, fg, border);
            if (Wheels.Count <= i)
            {
                Wheels.Add(cell);
            }
            else if (Wheels[i] != cell)
            {
                Wheels[i] = cell;
            }
        }
    }

    public void UpdateStats(DateTime now)
    {
        var stats = _runtime.Statistics;
        var rate = (stats.Total - _lastTotal) / Math.Max(0.001, (now - _lastStats).TotalSeconds);
        _lastTotal = stats.Total;
        _lastStats = now;
        IsReceiving = now - _lastSampleAt < TimeSpan.FromSeconds(2);
        IngestStatus = $"{_runtime.Pipeline.ActiveSource?.Name ?? "No source"} · {rate:0} pkt/s · total {stats.Total:N0}"
                       + (stats.RejectedSize > 0 ? $" · {stats.RejectedSize:N0} wrong-size" : "")
                       + (stats.RejectedFormat > 0 ? $" · {stats.RejectedFormat:N0} unsupported format ({stats.LastUnsupportedFormat})" : "")
                       + (_runtime.Pipeline.IsCapturingRaw ? " · raw capture ON" : "");

        var mismatched = stats.RejectedMode - _lastRejectedMode;
        _lastRejectedMode = stats.RejectedMode;
        HasModeWarning = mismatched > 0;
        ModeWarning = mismatched > 0
            ? $"The game is sending {GameCatalog.FormatName(stats.LastMismatchedFormat ?? GameFormat.F1_25)} telemetry but the app is in {GameCatalog.FormatName(_runtime.Mode)} mode. Switch the mode in the toolbar."
            : "";

        PacketStats.Clear();
        foreach (var (id, count) in stats.Snapshot().OrderBy(kv => kv.Key))
        {
            PacketStats.Add(new PacketStatRow($"{(int)id} {id}", count));
        }
    }
}
