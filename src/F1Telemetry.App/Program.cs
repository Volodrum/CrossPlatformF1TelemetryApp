using Avalonia;
using F1Telemetry.App.Infrastructure;
using Velopack;

namespace F1Telemetry.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // First thing: an installer, update or uninstall run of the exe does its work here and exits.
        VelopackApp.Build().Run();

        var paths = new AppPaths();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(paths, e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(paths, e.Exception);
            e.SetObserved();
        };

        using var instance = SingleInstanceLock.TryAcquire(paths.LockFilePath);
        if (instance is null)
        {
            Console.Error.WriteLine("F1 Telemetry is already running.");
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static void WriteCrash(AppPaths paths, object error)
    {
        try
        {
            File.AppendAllText(Path.Combine(paths.LogsDirectory, "crash.log"), $"[{DateTime.Now:O}] {error}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
