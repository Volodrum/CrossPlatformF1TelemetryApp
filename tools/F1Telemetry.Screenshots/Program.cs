using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using F1Telemetry.App;
using F1Telemetry.App.Controls;
using F1Telemetry.App.Services;
using F1Telemetry.App.ViewModels;
using F1Telemetry.App.Views;
using F1Telemetry.App.Views.Overlays;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Simulation;
using Microsoft.Extensions.DependencyInjection;

// Usage: f1telemetry-screenshots <output-folder> [--overlays]
// Renders offscreen only (no windows appear): seeds a simulated race into a temporary database, drives the real
// view models, and captures each main-window tab plus the HUD overlays in preview state.
// --overlays: only the HUD overlay boards (no seeding, a few seconds).
var overlaysOnly = args.Contains("--overlays");
var output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "screenshots");
Directory.CreateDirectory(output);
var dataDir = Path.Combine(Path.GetTempPath(), "f1tel-shots-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("F1TELEMETRY_DATA_DIR", dataDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .With(App.CyrillicFallback)
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

// --frames: the data guide's six animations side by side, as a numbered PNG sequence of one loop (12 frames a second),
// to be put together into a GIF or video (ffmpeg -framerate 12 -i frame-%03d.png …).
if (args.Contains("--frames"))
{
    var kinds = new (F1Telemetry.Core.Analytics.GuideKind Kind, string? Compound, string Title)[]
    {
        (F1Telemetry.Core.Analytics.GuideKind.LongRun, "Soft", "LONG RUN"), (F1Telemetry.Core.Analytics.GuideKind.TwoCompounds, null, "TWO COMPOUNDS, ONE SESSION"),
        (F1Telemetry.Core.Analytics.GuideKind.FuelLoads, null, "SAME TYRE, TWO FUEL LOADS"), (F1Telemetry.Core.Analytics.GuideKind.Cliff, null, "TYRE CLIFF"),
        (F1Telemetry.Core.Analytics.GuideKind.PitStop, null, "PIT STOP LOSS"), (F1Telemetry.Core.Analytics.GuideKind.SafetyCar, null, "STOP UNDER A SAFETY CAR"),
    };
    var pictures = new List<GuideIllustration>();
    var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(16) };
    foreach (var (kind, compound, title) in kinds)
    {
        var picture = new GuideIllustration { Kind = kind, Compound = compound, Phase = 0, Width = 360 };
        pictures.Add(picture);
        grid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101216")),
            BorderBrush = new SolidColorBrush(Color.Parse("#22262E")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(8),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = title, Foreground = new SolidColorBrush(Color.Parse("#C3CAD5")), FontWeight = FontWeight.Bold, FontSize = 13, LetterSpacing = 1.5 },
                    picture,
                },
            },
        });
    }

    var frameWindow = new Window { Width = 1240, Height = 470, Background = new SolidColorBrush(Color.Parse("#07080A")), Content = grid };
    frameWindow.Show();
    Pump(minMs: 300);
    const int frames = 54; // one 4.5 s loop at 12 frames a second
    for (var f = 0; f < frames; f++)
    {
        foreach (var picture in pictures)
        {
            picture.Phase = (double)f / frames;
        }

        Pump(minMs: 20);
        frameWindow.CaptureRenderedFrame()?.Save(Path.Combine(output, $"frame-{f:000}.png"));
    }

    Console.WriteLine($"saved {frames} frames to {output}");
    return;
}

var services = App.ConfigureServices();
var settings = services.GetRequiredService<SettingsService>();
settings.Load();

// --lang uk: the pages in that language; every text shown without a translation is listed in loc-missing.txt.
if (args.SkipWhile(a => a != "--lang").Skip(1).FirstOrDefault() is { } language)
{
    F1Telemetry.Core.Localization.Loc.Use(language);
    settings.Current.Language = language;
    var missing = new SortedSet<string>(StringComparer.Ordinal);
    F1Telemetry.Core.Localization.Loc.Missing += text =>
    {
        lock (missing)
        {
            missing.Add(text.Replace("\n", "\\n"));
        }
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
    {
        lock (missing)
        {
            File.WriteAllLines(Path.Combine(output, "loc-missing.txt"), missing);
        }
    };
}
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

if (!overlaysOnly)
{
    CaptureMainWindow();
}

void CaptureMainWindow()
{
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
if (window.GetVisualDescendants().OfType<StrategyView>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } strategyScroll)
{
    // The position chart sits at the bottom of the strategy tab.
    strategyScroll.Offset = strategyScroll.Offset.WithY(10_000);
    Capture(window, "05-position");
    strategyScroll.Offset = default;
}

// Settings is a page opened from the toolbar; the controls table is at its top.
vm.IsSettingsOpen = true;
Capture(window, "12-controller");
vm.IsSettingsOpen = false;

// Overlays have their own page in the rail.
vm.SelectedTab = MainTab.Overlays;
Capture(window, "06-overlays");
if (window.GetVisualDescendants().OfType<OverlaysView>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } overlaysScroll)
{
    // The overlays further down carry the segmented options (sector box GAP TO, timing tower RACE GAP).
    overlaysScroll.Offset = overlaysScroll.Offset.WithY(10_000);
    Capture(window, "06b-overlay-options");
    overlaysScroll.Offset = default;
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
    compareScroll.Offset = compareScroll.Offset.WithY(640);
    Capture(window, "10-compare-charts");
    compareScroll.Offset = compareScroll.Offset.WithY(10_000); // the stints under the pace chart
    Capture(window, "10b-compare-stints");
}

// Lap vs lap: the one-stop race's best lap against the best lap of another recording at the track.
vm.SelectedTab = MainTab.CompareLaps;
Pump(vm.ActivateLapCompareAsync(), 1500);
Capture(window, "17-compare-laps");

vm.SelectedRecording = vm.Recordings.First(r => r.Description == "Two stops");
Pump(vm.Session.RefreshAsync(), 300);
vm.SelectedTab = MainTab.Strategy;
Capture(window, "11-strategy-two-stops");

// Race plans and the data guide: the strategy tab's other pages, built from all three Monza recordings.
Pump(minMs: 1500);
vm.Session.StrategyPage = StrategyPage.Plans;
Capture(window, "18-race-plans");
if (window.GetVisualDescendants().OfType<StrategyView>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>()
        .FirstOrDefault(s => s.IsEffectivelyVisible) is { } plansScroll)
{
    // The plans' lap-time chart at the bottom: the three plans on one chart, then one chart each.
    plansScroll.Offset = plansScroll.Offset.WithY(10_000);
    Capture(window, "18b-race-plans-chart");
    vm.Session.Plans.SplitChart = true;
    Pump(minMs: 500);
    plansScroll.Offset = plansScroll.Offset.WithY(10_000);
    Capture(window, "18c-race-plans-split");
    vm.Session.Plans.SplitChart = false;
    plansScroll.Offset = default;
}

vm.Session.StrategyPage = StrategyPage.Guide;
Capture(window, "20-data-guide");
if (window.GetVisualDescendants().OfType<StrategyView>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>()
        .FirstOrDefault(s => s.IsEffectivelyVisible) is { } guideScroll)
{
    guideScroll.Offset = guideScroll.Offset.WithY(10_000); // the race group and the measured cards
    Capture(window, "20b-data-guide-cards");
    guideScroll.Offset = default;
}
vm.Session.StrategyPage = StrategyPage.Session;

// The game picker at the top of the rail.
if (window.FindControl<Button>("GameButton") is { Flyout: { } games } gameButton)
{
    games.ShowAt(gameButton);
    Capture(window, "21-game-picker");
    games.Hide();
}

// Energy: the one-stop race's battery map, lap summary and trace, with the two-stop race's typical laps added.
vm.SelectedRecording = vm.Recordings.First(r => r.Description == "One stop");
settings.Current.SetEnergyPool(GameFormat.F1_26, 11, vm.Recordings.Where(r => r.Description is "One stop" or "Two stops").Select(r => r.Info.Id));
vm.SelectedTab = MainTab.Energy;
Pump(vm.ActivateEnergyAsync(), 1500);
Capture(window, "13-energy");
Pump(minMs: 2000); // the plan is computed in the background
vm.Energy.Page = EnergyPage.Plan;
Capture(window, "15-energy-plan");
vm.Energy.Page = EnergyPage.Guide;
Capture(window, "30-energy-guide");
vm.Energy.Page = EnergyPage.Session;

// Track hub: Monza's sessions, none saved yet; the preview of saving them; then saved, with the strategy and ERS plans
// the hub gives.
vm.SelectedTab = MainTab.Setups;
Pump(vm.Hub.LoadAsync(), 1500);
Capture(window, "24-hub-sessions");
vm.Hub.TickUnsavedCommand.Execute(null);
Pump(vm.Hub.SaveCommand.ExecuteAsync(null), 1500);
Capture(window, "25-hub-preview");
Pump(vm.Hub.ConfirmSaveCommand.ExecuteAsync(null), 1500);
Capture(window, "26-hub-saved");
vm.Hub.TickSavedCommand.Execute(null);
Pump(vm.Hub.DeleteRecordingsCommand.ExecuteAsync(null), 300); // the first click arms it
Pump(vm.Hub.DeleteRecordingsCommand.ExecuteAsync(null), 1500);
Capture(window, "29-hub-recordings-deleted");
vm.Hub.Page = HubPage.Strategy;
Pump(minMs: 2000);
Capture(window, "27-hub-strategy");
vm.Hub.Page = HubPage.Energy;
Pump(minMs: 4000); // the plan is computed in the background
Capture(window, "28-hub-energy");
if (window.GetVisualDescendants().OfType<HubView>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>()
        .FirstOrDefault(s => s.IsEffectivelyVisible) is { } hubScroll)
{
    hubScroll.Offset = hubScroll.Offset.WithY(10_000); // the energy data guide under the plan
    Capture(window, "28b-hub-energy-guide");
    hubScroll.Offset = default;
}

// The hub's data guide: both guides of the track, one score, and what is left to drive by session.
vm.Hub.Page = HubPage.Guide;
Pump(minMs: 4000);
Capture(window, "28c-hub-data-guide");

// Setups: the three Monza setups the races ran (a new front wing at each stop), the newest compared with the one before.
vm.Hub.Page = HubPage.Setups;
Pump(vm.Setups.LoadAsync(), 800);
foreach (var row in vm.Setups.Rows.Take(2))
{
    row.IsChecked = true;
}

vm.Setups.Rows[0].Favourite = true;
Capture(window, "14-setups");

vm.SelectedTab = MainTab.Laps;
}

// Overlays in preview state, on the worst-case bright background used in the design's overlay board.
overlays.LapTiming.LoadPreview();
overlays.Delta.LoadPreview();
overlays.Radar.LoadPreview();
overlays.TrackConditions.LoadPreview();
overlays.Inputs.LoadPreview();
var board = new Window
{
    Width = 1640,
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
                    new RadarOverlayView { DataContext = overlays.Radar },
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

// The damage page close up (2×), to check the car drawing.
var damageCloseUp = new TrackConditionsOverlayViewModel();
damageCloseUp.LoadPreview();
damageCloseUp.TogglePage();
var damageBoard = new Window
{
    Width = 800,
    Height = 1040,
    Background = new SolidColorBrush(Color.Parse("#8A929C")),
    Content = new LayoutTransformControl
    {
        LayoutTransform = new ScaleTransform(2, 2),
        Margin = new Thickness(24),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        Child = new TrackConditionsOverlayView { DataContext = damageCloseUp },
    },
};
damageBoard.Show();
Capture(damageBoard, "22-damage-page");

// The data guide's animations, each at four moments of its loop (left to right), so the motion can be checked here.
var film = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
foreach (var (kind, compound) in new[]
         {
             (F1Telemetry.Core.Analytics.GuideKind.LongRun, "Soft"), (F1Telemetry.Core.Analytics.GuideKind.TwoCompounds, (string?)null),
             (F1Telemetry.Core.Analytics.GuideKind.FuelLoads, null), (F1Telemetry.Core.Analytics.GuideKind.Cliff, null),
             (F1Telemetry.Core.Analytics.GuideKind.PitStop, null), (F1Telemetry.Core.Analytics.GuideKind.SafetyCar, null),
         })
{
    var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
    foreach (var phase in new[] { 0.15, 0.4, 0.62, 0.9 })
    {
        row.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101216")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new GuideIllustration { Kind = kind, Compound = compound, Phase = phase, Width = 240 },
        });
    }

    film.Children.Add(row);
}

var filmBoard = new Window { Width = 1080, Height = 780, Background = new SolidColorBrush(Color.Parse("#07080A")), Content = film };
filmBoard.Show();
Capture(filmBoard, "23-guide-animations");

// The energy guide's animations, the same way.
var energyFilm = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
foreach (var (kind, mode) in new[]
         {
             (F1Telemetry.Core.Analytics.GuideKind.PowerLaps, (int?)null), (F1Telemetry.Core.Analytics.GuideKind.RaceRun, null),
             (F1Telemetry.Core.Analytics.GuideKind.ModeSpeeds, 2), (F1Telemetry.Core.Analytics.GuideKind.ModeStraights, null),
             (F1Telemetry.Core.Analytics.GuideKind.BrakingHarvest, null), (F1Telemetry.Core.Analytics.GuideKind.AeroStates, null),
             (F1Telemetry.Core.Analytics.GuideKind.FullBattery, null), (F1Telemetry.Core.Analytics.GuideKind.Replay, null),
         })
{
    var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
    foreach (var phase in new[] { 0.15, 0.4, 0.62, 0.9 })
    {
        row.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#101216")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = new GuideIllustration { Kind = kind, Mode = mode, Phase = phase, Width = 240 },
        });
    }

    energyFilm.Children.Add(row);
}

var energyFilmBoard = new Window { Width = 1080, Height = 1040, Background = new SolidColorBrush(Color.Parse("#07080A")), Content = energyFilm };
energyFilmBoard.Show();
Capture(energyFilmBoard, "31-energy-guide-animations");

// The ERS PLAN overlay in preview: Overtake wanted while the car runs Medium, so the switch is called out.
overlays.ErsPlan.LoadPreview();
var ersBoard = new Window
{
    Width = 360,
    Height = 160,
    Background = new SolidColorBrush(Color.Parse("#8A929C")),
    Content = new ErsPlanOverlayView { DataContext = overlays.ErsPlan, Margin = new Thickness(24), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top },
};
ersBoard.Show();
Capture(ersBoard, "16-ers-plan-overlay");

// The radar where it sits while driving, over a road at its size on a 1080p screen. Left: a car closing on the left.
// Right: a car alongside on the right, with that edge of the screen darkened (the side shade).
Control Road(string title, RadarFrame frame)
{
    var radar = new RadarOverlayViewModel();
    radar.Update(frame);
    var shade = SideShadeCalculator.Compute(frame);
    return new Panel
    {
        Width = 760,
        Height = 428,
        ClipToBounds = true,
        Children =
        {
            new Border { Background = new SolidColorBrush(Color.Parse("#6FA0CF")) },
            new Border { Background = new SolidColorBrush(Color.Parse("#4F7A45")), Height = 250, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom },
            new Avalonia.Controls.Shapes.Polygon
            {
                Fill = new SolidColorBrush(Color.Parse("#4A4D55")),
                Points = [new Point(330, 178), new Point(430, 178), new Point(760, 428), new Point(0, 428)],
            },
            new Avalonia.Controls.Shapes.Polygon
            {
                Fill = new SolidColorBrush(Color.Parse("#E8E8E8")),
                Points = [new Point(378, 178), new Point(382, 178), new Point(392, 428), new Point(368, 428)],
            },
            new Border { Background = SideShadeWindow.Gradient(true), Opacity = shade.Left, Width = 760 * SideShadeWindow.WidthShare, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left },
            new Border { Background = SideShadeWindow.Gradient(false), Opacity = shade.Right, Width = 760 * SideShadeWindow.WidthShare, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right },
            new RadarOverlayView
            {
                DataContext = radar, Width = 200, Height = 200,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24),
            },
            new TextBlock
            {
                Text = title, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 15,
                Margin = new Thickness(14, 10), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            },
        },
    };
}

var radarBoard = new Window
{
    Width = 1568,
    Height = 476,
    Background = new SolidColorBrush(Color.Parse("#101216")),
    Content = new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Spacing = 24,
        Margin = new Thickness(24),
        Children =
        {
            Road("CAR CLOSING ON THE LEFT", new RadarFrame(
            [
                new RadarBlip(1, -2.8f, -6.5f, 2, 7.1f, RadarSeverity.Near),
                new RadarBlip(2, 0.5f, 15f, 0, 15f, RadarSeverity.Far),
                new RadarBlip(3, 0.3f, -18f, 0, 18f, RadarSeverity.Far),
            ], false, false, RadarCalculator.Range)),
            Road("CAR ALONGSIDE ON THE RIGHT", new RadarFrame(
            [
                new RadarBlip(1, 2.4f, 0.6f, 0, 2.5f, RadarSeverity.Danger),
                new RadarBlip(2, -1.4f, -10f, 3, 10.1f, RadarSeverity.Far),
            ], false, true, RadarCalculator.Range)),
        },
    },
};
radarBoard.Show();
Capture(radarBoard, "19-radar");

runtime.Recorder.Stop();
Pump(services.DisposeAsync().AsTask(), 200);
try
{
    Directory.Delete(dataDir, true);
}
catch (IOException)
{
}
