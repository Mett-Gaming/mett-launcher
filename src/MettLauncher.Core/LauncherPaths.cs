using System.Text.Json;
using System.Text.Json.Nodes;
using MettLauncher.Core.IO;

namespace MettLauncher.Core;

/// <summary>
/// Alle Dateisystem-Orte des Launchers. Standard ist <c>%LocalAppData%\MettGaming\Launcher</c>
/// (Linux: <c>~/.local/share/MettGaming/Launcher</c>). Tests übergeben ein eigenes Wurzelverzeichnis.
/// </summary>
/// <remarks>
/// Bewusst <b>nicht</b> <c>%LocalAppData%\MettLauncher</c>: Dort installiert Velopack die App selbst und löscht den
/// Ordner bei Neuinstallation/Reparatur bzw. Deinstallation – Spiele und Einstellungen gingen sonst verloren.
/// </remarks>
public sealed record LauncherPaths(string Root)
{
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static LauncherPaths Default() => new(Path.Combine(LocalAppData, "MettGaming", "Launcher"));

    /// <summary>Datenordner bis v0.3.0 (gleichzeitig Velopack-Installordner).</summary>
    public static string LegacyRoot => Path.Combine(LocalAppData, "MettLauncher");

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
    public string NetDir => Path.Combine(Root, "net");
    /// <summary>Erfolge ohne Konto.</summary>
    public string LocalProfileDir => Path.Combine(Root, "profiles", "local");
    public string LibraryFile => Path.Combine(Root, "library.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string DefaultGamesDir => Path.Combine(Root, "games");
}

/// <summary>Ergebnis der Datenmigration aus dem alten Ordner.</summary>
public sealed record MigrationResult(bool Migrated, IReadOnlyList<string> Moved, IReadOnlyList<string> Failed);

/// <summary>
/// Verschiebt Launcher-Daten aus dem alten Ordner (bis v0.3.0: <c>%LocalAppData%\MettLauncher</c>) in den neuen.
/// Velopack-Dateien (<c>current</c>, <c>packages</c>, <c>Update.exe</c>, …) bleiben liegen. Absolute Pfade in
/// <c>library.json</c>/<c>settings.json</c>, die in den alten Ordner zeigen, werden umgeschrieben.
/// </summary>
public static class LegacyDataMigration
{
    /// <summary>Alles, was der Launcher selbst angelegt hat.</summary>
    public static readonly string[] Entries = ["cache", "games", "net", "proton", "prefixes", "tools", "logs", "library.json", "settings.json"];

    public static MigrationResult Run(LauncherPaths target, string legacyRoot)
    {
        var moved = new List<string>();
        var failed = new List<string>();
        var from = Path.GetFullPath(legacyRoot);
        var to = Path.GetFullPath(target.Root);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(from))
            return new MigrationResult(false, moved, failed);
        // Schon migriert oder neuer Ordner bereits benutzt -> nichts anfassen.
        if (File.Exists(target.LibraryFile) || File.Exists(target.SettingsFile))
            return new MigrationResult(false, moved, failed);
        if (!Entries.Any(e => File.Exists(Path.Combine(from, e)) || Directory.Exists(Path.Combine(from, e))))
            return new MigrationResult(false, moved, failed);

        Directory.CreateDirectory(to);
        foreach (var entry in Entries)
        {
            var src = Path.Combine(from, entry);
            var dst = Path.Combine(to, entry);
            try
            {
                if (Directory.Exists(src))
                {
                    if (Directory.Exists(dst)) FileSystemUtil.CopyDirectory(src, dst); else Directory.Move(src, dst);
                    if (Directory.Exists(src)) FileSystemUtil.TryDeleteDirectory(src);
                    moved.Add(entry);
                }
                else if (File.Exists(src))
                {
                    File.Move(src, dst, overwrite: true);
                    moved.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(entry);
            }
        }

        RewritePaths(target.LibraryFile, from, to);
        RewritePaths(target.SettingsFile, from, to);

        // Alter Ordner leer (keine Velopack-Installation darin)? Dann weg damit, damit das Setup nicht nachfragt.
        try
        {
            if (Directory.Exists(from) && !Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new MigrationResult(moved.Count > 0, moved, failed);
    }

    /// <summary>Ersetzt in allen String-Werten einer JSON-Datei den alten Wurzelpfad durch den neuen.</summary>
    private static void RewritePaths(string file, string from, string to)
    {
        if (!File.Exists(file)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node is null || !Rewrite(node, from, to)) return;
            AtomicFile.WriteAllText(file, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }
    }

    private static bool Rewrite(JsonNode node, string from, string to)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue v && v.TryGetValue<string>(out var s) && Replace(s, from, to) is { } r)
                    {
                        obj[key] = r;
                        changed = true;
                    }
                    else if (child is not null && Rewrite(child, from, to)) changed = true;
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                    if (child is not null && Rewrite(child, from, to)) changed = true;
                break;
        }
        return changed;
    }

    private static string? Replace(string value, string from, string to)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (value.Equals(from, cmp)) return to;
        foreach (var sep in new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar })
            if (value.StartsWith(from + sep, cmp)) return to + value[from.Length..];
        return null;
    }
}
