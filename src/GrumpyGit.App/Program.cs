using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GrumpyGit.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Installed before anything else runs. A GUI app that dies has no console to
        // print to, so without this a crash leaves the user with a vanished window and
        // nothing to report — the failure is invisible to exactly the person who could
        // describe it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        // Faulted tasks nobody awaited surface here rather than as a silent no-op.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteCrash("Startup", ex);
            throw;
        }
    }

    /// <summary>
    /// Appends a crash to %LOCALAPPDATA%\Grumpy\crash.log. Appends rather than replaces
    /// so a repeating fault leaves a history; swallows its own failures because a logger
    /// that throws during a crash destroys the very report it was meant to produce.
    /// </summary>
    private static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Grumpy");
            Directory.CreateDirectory(dir);

            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"""

                ───────────────────────────────────────────────
                {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  [{source}]
                {ex?.ToString() ?? "(no exception object)"}

                """);
        }
        catch
        {
            // Nothing useful left to do.
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
