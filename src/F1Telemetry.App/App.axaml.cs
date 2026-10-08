using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using F1Telemetry.App.Infrastructure;
using F1Telemetry.App.Services;
using F1Telemetry.App.ViewModels;
using F1Telemetry.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace F1Telemetry.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = ConfigureServices();
            var settings = _services.GetRequiredService<SettingsService>();
            settings.Load();

            var runtime = _services.GetRequiredService<TelemetryRuntime>();
            var overlays = _services.GetRequiredService<OverlayManager>();
            var hotkeys = _services.GetRequiredService<HotkeyService>();
            var gamepads = _services.GetRequiredService<GamepadService>();
            var viewModel = _services.GetRequiredService<MainWindowViewModel>();

            var window = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) => Shutdown(overlays);

            var options = StartupOptions.Parse(desktop.Args ?? []);
            window.Opened += async (_, _) =>
            {
                try
                {
                    await runtime.InitializeAsync();
                    overlays.Initialize();
                    hotkeys.Start();
                    gamepads.Start();
                    await viewModel.InitializeAsync();
                    await options.ApplyAsync(runtime, viewModel, desktop);
                }
                catch (Exception ex)
                {
                    _services.GetRequiredService<ILogger<App>>().LogCritical(ex, "Startup failed");
                    viewModel.StatusMessage = $"Startup failed: {ex.Message}";
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown(OverlayManager overlays)
    {
        overlays.Close();
        if (_services is null)
        {
            return;
        }

        // Stop ingest and flush the store before the process exits. Runs on the thread pool: blocking the UI
        // thread on async disposal would deadlock continuations that expect to resume on it.
        // Disposing the container disposes the runtime (IAsyncDisposable only, so it must be async).
        var log = _services.GetRequiredService<ILogger<App>>();
        var services = _services;
        if (!Task.Run(() => services.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(10)))
        {
            log.LogWarning("Shutdown timed out while flushing telemetry");
        }
    }

    internal static ServiceProvider ConfigureServices()
    {
        var paths = new AppPaths();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddDebug()
            .AddProvider(new FileLoggerProvider(paths.LogsDirectory)));

        services.AddSingleton(paths);
        services.AddSingleton<SettingsService>();
        services.AddSingleton<TelemetryRuntime>();
        services.AddSingleton<LiveDataHub>();
        services.AddSingleton<ErsPlanService>();
        services.AddSingleton<OverlayManager>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<GamepadService>();

        services.AddSingleton<LiveViewModel>();
        services.AddSingleton<SessionViewModel>();
        services.AddSingleton<LapDetailViewModel>();
        services.AddSingleton<CompareViewModel>();
        services.AddSingleton<EnergyPlanViewModel>();
        services.AddSingleton<EnergyViewModel>();
        services.AddSingleton<SetupsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
