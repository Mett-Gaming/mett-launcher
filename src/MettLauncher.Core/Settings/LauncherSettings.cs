using System.Text.Json;
using MettLauncher.Core.IO;
using MettLauncher.Core.Overlay;

namespace MettLauncher.Core.Settings;

public sealed record LauncherSettings
{
    /// <summary>Bibliotheksordner; <c>null</c> = Standard <c>%LocalAppData%\MettLauncher\games</c>.</summary>
    public string? LibraryDir { get; init; }
    public bool AutoUpdate { get; init; } = true;
    /// <summary>Prerelease-Kanal für alle Spiele.</summary>
    public bool IncludePrereleases { get; init; }
    /// <summary>Alternative Katalog-Basis-URL (z. B. zum Testen); <c>null</c> = Standard.</summary>
    public string? CatalogBaseUrl { get; init; }
    /// <summary>Name, den Mitspieler sehen; Pflicht für Hosten/Beitreten (2–24 Zeichen).</summary>
    public string? DisplayName { get; init; }
    /// <summary>Linux: Proton schreibt ein ausführliches Log nach <c>logs\</c> (<c>PROTON_LOG=1</c>).</summary>
    public bool ProtonLog { get; init; }
    /// <summary>Zeigt in Discord, was gespielt wird.</summary>
    public bool DiscordPresence { get; init; } = true;
    /// <summary>Einblendungen und Overlay in Spielen.</summary>
    public bool Overlay { get; init; } = true;
    /// <summary>Ton bei einem neuen Erfolg.</summary>
    public bool OverlaySound { get; init; } = true;
    public OverlayAnchor OverlayCorner { get; init; } = OverlayAnchor.BottomRight;
    /// <summary>Leiste mit Bildrate, Uhr und Sitzungsdauer.</summary>
    public bool OverlayBar { get; init; }
    public OverlayAnchor OverlayBarCorner { get; init; } = OverlayAnchor.TopLeft;
    /// <summary>Tastenkürzel für das Overlay-Feld, z. B. <c>f9</c>; leer = keins.</summary>
    public string OverlayKey { get; init; } = "f9";
    /// <summary>Schließen während eines Spiels: im Hintergrund weiterlaufen? <c>null</c> = fragen.</summary>
    public bool? KeepInBackground { get; init; }
}

/// <summary>Lädt/speichert <c>settings.json</c>.</summary>
public sealed class SettingsStore
{
    private readonly LauncherPaths _paths;
    private readonly Lock _lock = new();
    private LauncherSettings _current;

    public SettingsStore(LauncherPaths paths)
    {
        _paths = paths;
        _current = Load();
    }

    public LauncherSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public event EventHandler<LauncherSettings>? Changed;

    public string LibraryDir => ResolveLibraryDir(Current);

    public string ResolveLibraryDir(LauncherSettings s) =>
        string.IsNullOrWhiteSpace(s.LibraryDir) ? _paths.DefaultGamesDir : Path.GetFullPath(Environment.ExpandEnvironmentVariables(s.LibraryDir));

    public void Save(LauncherSettings settings)
    {
        lock (_lock)
        {
            AtomicFile.WriteAllText(_paths.SettingsFile, JsonSerializer.Serialize(settings, Json.Options));
            _current = settings;
        }
        Changed?.Invoke(this, settings);
    }

    public void Update(Func<LauncherSettings, LauncherSettings> change)
    {
        LauncherSettings next;
        lock (_lock) next = change(_current);
        Save(next);
    }

    private LauncherSettings Load()
    {
        try
        {
            return File.Exists(_paths.SettingsFile)
                ? JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(_paths.SettingsFile), Json.Options) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }
}
