using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using F1Telemetry.App;
using F1Telemetry.App.Services;
using F1Telemetry.App.ViewModels;
using F1Telemetry.App.Views;
using F1Telemetry.App.Views.Overlays;
using F1Telemetry.Protocol;
using F1Telemetry.Simulation;
using Microsoft.Extensions.DependencyInjection;

// Usage: f1telemetry-screenshots <output-folder>
// Renders offscreen only (no windows appear): seeds a simulated race into a temporary database, drives the real
// view models, and captures each main-window tab plus the HUD overlays in preview state.
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "screenshots");
Directory.CreateDirectory(output);
var dataDir = Path.Combine(Path.GetTempPath(), "f1tel-shots-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("F1TELEMETRY_DATA_DIR", dataDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

void Pump(Task? task = null, int minMs = 0)
{
    var until = DateTime.UtcNow.AddMilliseconds(minMs);
    while ((task is not null && !task.IsCompleted) || DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }

    task?.GetAwaiter().GetResult();
    Dispatcher.UIThread.RunJobs();
}

void Capture(Window window, string name)
{
    Pump(minMs: 300);
    var frame = window.CaptureRenderedFrame();
    var path = Path.Combine(output, name + ".png");
    frame?.Save(path);
    Console.WriteLine(frame is null ? $"FAILED {name}" : $"saved {path}");
}

var services = App.ConfigureServices();
var settings = services.GetRequiredService<SettingsService>();
settings.Load();
settings.Current.ListenOnStartup = false;
settings.Current.TelemetryMode = GameFormat.F1_26;
// Sample controller bindings, drawn as DualSense icons (no pad is opened here).
settings.Current.StartStopPadButton = "Touchpad+Start";
settings.Current.ToggleOverlaysPadButton = "Touchpad+Back";
settings.Current.StrategyPagePadButton = "Misc1+DPadRight";
services.GetRequiredService<GamepadService>().Family = F1Telemetry.Core.Input.PadFamily.DualSense;

var runtime = services.GetRequiredService<TelemetryRuntime>();
Pump(runtime.InitializeAsync());
var hub = services.GetRequiredService<LiveDataHub>();
var overlays = services.GetRequiredService<OverlayManager>();
var vm = services.GetRequiredService<MainWindowViewModel>();

void Seed(SimulationOptions options, string description, TimeSpan? stopAt = null)
{
    foreach (var datagram in new SessionSimulator(options).Generate())
    {
        if (datagram.Time > stopAt)
        {
            break;
        }

        if (PacketParser.Parse(datagram.Data) is { IsSuccess: true, Packet: { } packet })
        {
            runtime.Engine.Process(packet);
            if (!runtime.Recorder.IsRecording && runtime.Engine.Session is { TrackId: >= 0 })
            {
                runtime.Recorder.Start(description);
            }
        }
    }
}

// Two finished 12-lap races at Monza for the compare tab: one stop against two.
Seed(new SimulationOptions
{
    Format = GameFormat.F1_26, Track = runtime.Tracks.ForTrackId(11), TrackId = 11, Laps = 12, PitLaps = [6], TickRateHz = 30, Seed = 11, SessionUid = 1001,
}, "One stop");
runtime.Recorder.Stop();
Seed(new SimulationOptions
{
    Format = GameFormat.F1_26, Track = runtime.Tracks.ForTrackId(11), TrackId = 11, Laps = 12, PitLaps = [4, 8], TickRateHz = 30, Seed = 12, SessionUid = 1002,
}, "Two stops");
runtime.Recorder.Stop();

// Seed: 6 simulated laps at Monza (pit on lap 3), recorded; stop mid-lap 6 so the live view has a lap in progress.
Seed(new SimulationOptions
{
    Format = GameFormat.F1_26, Track = runtime.Tracks.ForTrackId(11), TrackId = 11, Laps = 6, PitOnLap = 3, TickRateHz = 30, Seed = 7, SessionUid = 1003,
}, "Screenshot seed", stopAt: TimeSpan.FromSeconds(5 * 86 + 40));

Pump(runtime.Store.FlushAsync(), 400);

var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
window.Show();
Pump(vm.InitializeAsync(), 600);

vm.SelectedTab = MainTab.Live;
Capture(window, "01-live");

vm.SelectedRecording = vm.Recordings.FirstOrDefault();
Pump(vm.Session.RefreshAsync(), 300);
vm.SelectedTab = MainTab.Laps;
Capture(window, "02-laps");

if (vm.Session.Laps.FirstOrDefault(l => l.Lap.LapNumber == 2) is { } lap)
{
    vm.Session.SelectedLap = lap;
    Pump(minMs: 1500);
}

vm.SelectedTab = MainTab.LapDetail;
Capture(window, "03-lap-detail");
vm.SelectedTab = MainTab.Strategy;
Capture(window, "04-strategy");
vm.SelectedTab = MainTab.Position;
Capture(window, "05-position");
vm.SelectedTab = MainTab.Settings;
Capture(window, "06-settings");
if (window.GetVisualDescendants().OfType<SettingsView>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } settingsScroll)
{
    settingsScroll.Offset = settingsScroll.Offset.WithY(400);
    Capture(window, "12-controller");
    settingsScroll.Offset = default;
}

// Race vs race: the one-stop race as A, the two-stop race as B; the second shot scrolls down to the charts.
vm.SelectedRecording = vm.Recordings.First(r => r.Description == "One stop");
Pump(vm.Session.RefreshAsync(), 300);
vm.SelectedTab = MainTab.Compare;
Pump(vm.ActivateCompareAsync(), 800);
vm.Compare.SelectedB = vm.Compare.RecordingsB.First(r => r.Description == "Two stops");
Capture(window, "09-compare");
if (window.GetVisualDescendants().OfType<CompareView>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } compareScroll)
{
    compareScroll.Offset = compareScroll.Offset.WithY(860);
    Capture(window, "10-compare-charts");
}

vm.SelectedRecording = vm.Recordings.First(r => r.Description == "Two stops");
Pump(vm.Session.RefreshAsync(), 300);
vm.SelectedTab = MainTab.Strategy;
Capture(window, "11-strategy-two-stops");

// Energy: the one-stop race's battery map, lap summary and trace.
vm.SelectedRecording = vm.Recordings.First(r => r.Description == "One stop");
vm.SelectedTab = MainTab.Energy;
Pump(vm.ActivateEnergyAsync(), 1500);
Capture(window, "13-energy");

vm.SelectedTab = MainTab.Live;

// Overlays in preview state, on the worst-case bright background used in the design's overlay board.
overlays.LapTiming.LoadPreview();
overlays.Delta.LoadPreview();
overlays.Radar.LoadPreview();
overlays.TrackConditions.LoadPreview();
overlays.Inputs.LoadPreview();
var board = new Window
{
    Width = 1320,
    Height = 560,
    Background = new SolidColorBrush(Color.Parse("#8A929C")),
    Content = new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 24,
        Margin = new Thickness(24),
        Children =
        {
            new StackPanel
            {
                Spacing = 24,
                Children =
                {
                    new LapTimingOverlayView { DataContext = overlays.LapTiming },
                    new InputTraceOverlayView { DataContext = overlays.Inputs, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left },
                },
            },
            new StackPanel
            {
                Spacing = 24,
                Children =
                {
                    new DeltaOverlayView { DataContext = overlays.Delta },
                    new RadarOverlayView { DataContext = overlays.Radar, Width = 240, Height = 240 },
                },
            },
            new TrackConditionsOverlayView { DataContext = overlays.TrackConditions, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top },
        },
    },
};
board.Show();
Capture(board, "07-overlays");

// The overlays added for replacing the in-game HUD: tower, sector box, and the strategy overlay's damage page.
overlays.SectorBox.LoadPreview();
overlays.TimingTower.LoadPreview();
var damagePage = new TrackConditionsOverlayViewModel();
damagePage.LoadPreview();
damagePage.TogglePage();
var hudBoard = new Window
{
    Width = 1260,
    Height = 700,
    Background = new SolidColorBrush(Color.Parse("#8A929C")),
    Content = new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 24,
        Margin = new Thickness(24),
        Children =
        {
            new TimingTowerOverlayView { DataContext = overlays.TimingTower, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top },
            new SectorBoxOverlayView { DataContext = overlays.SectorBox, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top },
            new TrackConditionsOverlayView { DataContext = damagePage, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top },
        },
    },
};
hudBoard.Show();
Capture(hudBoard, "08-hud-overlays");

runtime.Recorder.Stop();
Pump(services.DisposeAsync().AsTask(), 200);
try
{
    Directory.Delete(dataDir, true);
}
catch (IOException)
{
}
