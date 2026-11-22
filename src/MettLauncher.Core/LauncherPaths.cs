namespace MettLauncher.Core;

/// <summary>
/// Alle Dateisystem-Orte des Launchers. Standard ist <c>%LocalAppData%\MettLauncher</c>;
/// Tests übergeben ein eigenes Wurzelverzeichnis.
/// </summary>
public sealed record LauncherPaths(string Root)
{
    public static LauncherPaths Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MettLauncher"));

    public string CacheDir => Path.Combine(Root, "cache");
    public string CatalogCacheFile => Path.Combine(CacheDir, "games.json");
    public string ReleaseCacheDir => Path.Combine(CacheDir, "releases");
    public string NewsCacheFile => Path.Combine(CacheDir, "news.json");
    public string ImageCacheDir => Path.Combine(CacheDir, "images");
    /// <summary>Vom Launcher verwaltete Proton-Versionen (Linux).</summary>
    public string ProtonDir => Path.Combine(Root, "proton");
    /// <summary>Wine-Prefixe pro Spiel (Linux).</summary>
    public string PrefixDir => Path.Combine(Root, "prefixes");
    /// <summary>Hilfsprogramme wie umu-launcher (Linux).</summary>
    public string ToolsDir => Path.Combine(Root, "tools");
    public string LogDir => Path.Combine(Root, "logs");
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DefaultGamesDir => Path.Combine(Root, "games");
}
