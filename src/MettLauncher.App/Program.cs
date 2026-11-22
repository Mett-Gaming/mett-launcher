using Avalonia;
using Velopack;

namespace MettLauncher.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Muss als Erstes laufen: Velopack-Hooks (Install/Update/Uninstall) beenden den Prozess ggf. sofort.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
