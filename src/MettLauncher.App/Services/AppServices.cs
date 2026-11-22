using MettLauncher.Core;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Install;
using MettLauncher.Core.Launch;
using MettLauncher.Core.News;
using MettLauncher.Core.Settings;
using MettLauncher.Core.State;

namespace MettLauncher.App.Services;

/// <summary>Verdrahtet die Core-Dienste einmal für die ganze App.</summary>
public sealed class AppServices : IDisposable
{
    public AppServices()
    {
        Paths = LauncherPaths.Default();
        Http = MettHttp.CreateClient();
        Settings = new SettingsStore(Paths);
        Library = new LibraryStore(Paths);
        Catalog = new CatalogService(Http, Paths,
            Settings.Current.CatalogBaseUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var cu) ? cu : null);
        Releases = new ReleaseService(Http, Paths);
        News = new NewsService(Http, Paths);
        Launcher = new GameLauncher(Library);
        Installer = new InstallService(Http, Library, Settings, id => Launcher.IsRunning(id));
        Images = new ImageCache(Http, Paths);
        LauncherUpdates = new LauncherUpdateService();
    }

    public LauncherPaths Paths { get; }
    public HttpClient Http { get; }
    public SettingsStore Settings { get; }
    public LibraryStore Library { get; }
    public CatalogService Catalog { get; }
    public ReleaseService Releases { get; }
    public NewsService News { get; }
    public GameLauncher Launcher { get; }
    public InstallService Installer { get; }
    public ImageCache Images { get; }
    public LauncherUpdateService LauncherUpdates { get; }

    public void Dispose() => Http.Dispose();
}
