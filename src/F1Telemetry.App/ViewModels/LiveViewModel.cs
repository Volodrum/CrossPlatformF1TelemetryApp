using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using F1Telemetry.App.Services;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
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
            Session = $"{s.TrackName} · {s.SessionTypeName} · {(s.Format == GameFormat.F1_26 ? "F1 26" : "F1 25")}";
            TotalLaps = s.TotalLaps > 0 ? $"/{s.TotalLaps}" : "";
            Outline = runtime.Tracks.ForTrackId(s.TrackId);
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
            if (sample.LapNumber != _currentLapNumber)
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
            ? $"The game is sending {(stats.LastMismatchedFormat == GameFormat.F1_26 ? "F1 26" : "F1 25")} telemetry but the app is in {(_runtime.Mode == GameFormat.F1_26 ? "F1 26" : "F1 25")} mode. Switch the mode in the toolbar."
            : "";

        PacketStats.Clear();
        foreach (var (id, count) in stats.Snapshot().OrderBy(kv => kv.Key))
        {
            PacketStats.Add(new PacketStatRow($"{(int)id} {id}", count));
        }
    }
}
