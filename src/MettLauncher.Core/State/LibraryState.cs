using System.Text.Json;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.State;

/// <summary>Inhalt von <c>library.json</c>.</summary>
public sealed class LibraryState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public Dictionary<string, GameState> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public GameState GetOrAdd(string gameId)
    {
        if (!Games.TryGetValue(gameId, out var s))
            Games[gameId] = s = new GameState();
        return s;
    }

    public GameState? Get(string gameId) => Games.GetValueOrDefault(gameId);

    public LibraryState Clone() =>
        JsonSerializer.Deserialize<LibraryState>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
}

public sealed class GameState
{
    /// <summary>Installierte Version als SemVer-String (ohne <c>v</c>).</summary>
    public string? InstalledVersion { get; set; }
    public string? InstalledTag { get; set; }
    public string? InstallPath { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    public string? AssetName { get; set; }
    public string? AssetSha256 { get; set; }

    /// <summary>Festgepinnter Tag – solange gesetzt, gibt es kein Auto-Update.</summary>
    public string? PinnedTag { get; set; }

    public long TotalPlaySeconds { get; set; }
    public DateTimeOffset? LastPlayed { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsInstalled => InstalledVersion is not null && InstallPath is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan TotalPlaytime => TimeSpan.FromSeconds(TotalPlaySeconds);

    public void ClearInstall()
    {
        InstalledVersion = null;
        InstalledTag = null;
        InstallPath = null;
        InstalledAt = null;
        AssetName = null;
        AssetSha256 = null;
    }
}

/// <summary>
/// Lädt und speichert <see cref="LibraryState"/> thread-sicher. Jede Änderung wird sofort atomar
/// (tmp + rename) geschrieben.
/// </summary>
public sealed class LibraryStore
{
    private readonly Lock _lock = new();
    private LibraryState _state;

    public LibraryStore(string filePath)
    {
        FilePath = filePath;
        _state = Load(filePath);
    }

    public LibraryStore(LauncherPaths paths) : this(paths.LibraryFile) { }

    public string FilePath { get; }

    /// <summary>Wird nach jedem gespeicherten Update ausgelöst (Argument: Kopie des neuen Stands).</summary>
    public event EventHandler<LibraryState>? Changed;

    /// <summary>Eine unabhängige Kopie des aktuellen Stands.</summary>
    public LibraryState Snapshot()
    {
        lock (_lock) return _state.Clone();
    }

    public GameState? GetGame(string gameId)
    {
        lock (_lock)
        {
            var g = _state.Get(gameId);
            return g is null ? null : JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(g, Json.Options), Json.Options);
        }
    }

    public void Update(Action<LibraryState> mutate)
    {
        LibraryState copy;
        lock (_lock)
        {
            var working = _state.Clone();
            mutate(working);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(working, Json.Options));
            _state = working;
            copy = working.Clone();
        }
        Changed?.Invoke(this, copy);
    }

    public void UpdateGame(string gameId, Action<GameState> mutate) => Update(s => mutate(s.GetOrAdd(gameId)));

    internal static LibraryState Load(string filePath)
    {
        if (!File.Exists(filePath)) return new LibraryState();
        try
        {
            var state = JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(filePath), Json.Options) ?? new LibraryState();
            state.Games = new Dictionary<string, GameState>(state.Games ?? [], StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (JsonException)
        {
            // Kaputte Datei nicht stillschweigend überschreiben, sondern beiseitelegen.
            try { File.Move(filePath, filePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true); } catch (IOException) { }
            return new LibraryState();
        }
    }
}
