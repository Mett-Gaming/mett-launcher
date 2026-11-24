using Avalonia;
using Velopack;

namespace MettLauncher.App;

internal static class Program
{
    /// <summary>Der Launcher wurde noch einmal gestartet.</summary>
    public static event Action? ShowRequested;

    [STAThread]
    public static void Main(string[] args)
    {
        // Muss als Erstes laufen: Velopack-Hooks (Install/Update/Uninstall) beenden den Prozess ggf. sofort.
        VelopackApp.Build().Run();
        // Läuft schon einer (vielleicht versteckt im Infobereich), zeigt er sich und dieser hier endet.
        var first = true;
        using var show = OperatingSystem.IsWindows() ? new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MettLauncherShow", out first) : null;
        if (show is not null && !first)
        {
            show.Set();
            return;
        }
        if (show is not null) ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
