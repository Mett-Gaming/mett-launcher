using MettLauncher.Core;
using MettLauncher.Core.Achievements;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Install;
using MettLauncher.Core.Launch;
using MettLauncher.Core.News;
using MettLauncher.Core.Overlay;
using MettLauncher.Core.Presence;
using MettLauncher.Core.Proton;
using MettLauncher.Core.Settings;
using MettLauncher.Core.State;

namespace MettLauncher.App.Services;

/// <summary>Verdrahtet die Core-Dienste einmal für die ganze App.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices()
    {
        Paths = LauncherPaths.Default();
        // Bis v0.3.0 lagen die Daten im Velopack-Installordner -> einmalig umziehen, bevor etwas geladen wird.
        Migration = LegacyDataMigration.Run(Paths, LauncherPaths.LegacyRoot);
        Http = MettHttp.CreateClient();
        Settings = new SettingsStore(Paths);
        Library = new LibraryStore(Paths);
        Catalog = new CatalogService(Http, Paths,
            Settings.Current.CatalogBaseUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var cu) ? cu : null);
        Releases = new ReleaseService(Http, Paths);
        News = new NewsService(Http, Paths);
        Proton = new ProtonManager(Http, Paths, Releases);
        Umu = new UmuManager(Http, Paths, Releases);
        ProtonRunner = new ProtonRunner(Paths, Proton, Umu, Library, () => Settings.Current.ProtonLog);
        var native = new NativeRunner();
        // Unter Linux laufen Windows-Spiele über Proton; nur Spiele mit "linux": "native" starten direkt.
        Launcher = new GameLauncher(Library, runnerFor: game =>
            OperatingSystem.IsLinux() && !game.RunsNativeOnLinux ? ProtonRunner : native);
        LaunchGate = new LaunchUpdateGate(Releases, Library, Settings);
        Installer = new InstallService(Http, Library, Settings, id => Launcher.IsRunning(id));
        Images = new ImageCache(Http, Paths);
        LauncherUpdates = new LauncherUpdateService();
        PresenceServer = new PresenceServer();
        Achievements = new AchievementService(new AchievementStore(Paths.LocalProfileDir), id => Library.GetGame(id)?.InstallPath);
        Achievements.Problem += text => System.Diagnostics.Trace.WriteLine(text);
        PresenceServer.Achievement = Achievements.Report;
        OverlayServer = new OverlayServer();
        OverlayServer.Start();
        Layer = new OverlayLayer(Paths, AppContext.BaseDirectory);
        Overlay = new OverlayService(OverlayServer, PresenceServer, Layer, Achievements, Launcher, Settings);
        PresenceServer.Start();
        Discord = new DiscordPresence();
        Presence = new PresenceService(PresenceServer, Discord.Show, Launcher, Settings, Catalog.ResolveAssetUrl);
        Net = new NetService();
        Net.Start(Paths, Settings.Current.DisplayName);
    }

    public NetService Net { get; }
    public AchievementService Achievements { get; }
    public OverlayServer OverlayServer { get; }
    public OverlayLayer Layer { get; }
    public OverlayService Overlay { get; }
    public PresenceServer PresenceServer { get; }
    public DiscordPresence Discord { get; }
    public PresenceService Presence { get; }
    public ProtonManager Proton { get; }
    public UmuManager Umu { get; }
    public ProtonRunner ProtonRunner { get; }
    public static bool IsLinux => OperatingSystem.IsLinux();

    public LauncherPaths Paths { get; }
    public MigrationResult Migration { get; }
    public HttpClient Http { get; }
    public SettingsStore Settings { get; }
    public LibraryStore Library { get; }
    public CatalogService Catalog { get; }
    public ReleaseService Releases { get; }
    public NewsService News { get; }
    public GameLauncher Launcher { get; }
    public LaunchUpdateGate LaunchGate { get; }
    public InstallService Installer { get; }
    public ImageCache Images { get; }
    public LauncherUpdateService LauncherUpdates { get; }

    public void Dispose()
    {
        Net.Dispose();
        OverlayServer.Dispose();
        PresenceServer.Dispose();
        Discord.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        Http.Dispose();
    }
}
