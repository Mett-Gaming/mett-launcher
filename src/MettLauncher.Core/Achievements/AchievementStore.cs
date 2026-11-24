using System.Text.Json;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Achievements;

/// <summary>Stand eines Erfolgs.</summary>
public sealed record AchievementState
{
    public int Progress { get; init; }
    public DateTimeOffset? UnlockedAt { get; init; }
    /// <summary>Feste Kennung dieser Freischaltung, damit ein Konto sie später nicht doppelt zählt.</summary>
    public Guid? UnlockId { get; init; }
}

/// <summary>
/// Erfolge eines Profils, je Spiel eine Datei. Einträge bleiben nach dem Deinstallieren und auch, wenn ein Spiel einen
/// Erfolg entfernt. Eine kaputte Datei wird aufgehoben und die Sicherung benutzt.
/// </summary>
public sealed class AchievementStore(string profileDir, TimeProvider? time = null)
{
    private sealed class Content
    {
        public int Schema { get; set; } = 1;
        public Dictionary<string, AchievementState> Achievements { get; set; } = [];
    }

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Content> _games = new(StringComparer.OrdinalIgnoreCase);

    private string PathOf(string gameId) =>
        FileSystemUtil.SafeCombine(Path.Combine(profileDir, "achievements"), gameId + ".json") is { } p
            ? p : throw new ArgumentException("ungültige Spiel-Id", nameof(gameId));

    public IReadOnlyDictionary<string, AchievementState> All(string gameId)
    {
        lock (_lock) return new Dictionary<string, AchievementState>(Get(gameId).Achievements);
    }

    /// <summary>Setzt den Fortschritt (sinkt nie) und schaltet beim Erreichen von <paramref name="goal"/> frei.</summary>
    /// <exception cref="IOException">Nicht gespeichert; der Stand bleibt wie vorher.</exception>
    public (AchievementState Before, AchievementState After) Advance(string gameId, string id, int value, int goal)
    {
        lock (_lock)
        {
            var content = Get(gameId);
            var known = content.Achievements.TryGetValue(id, out var old);
            var before = old ?? new AchievementState();
            var after = before with { Progress = Math.Max(before.Progress, Math.Clamp(value, 0, goal)) };
            if (after.Progress >= goal && before.UnlockedAt is null)
                after = after with { UnlockedAt = _time.GetUtcNow(), UnlockId = Guid.NewGuid() };
            if (after == before) return (before, before);
            content.Achievements[id] = after;
            try
            {
                Save(PathOf(gameId), content);
            }
            catch
            {
                if (known) content.Achievements[id] = before;
                else content.Achievements.Remove(id);
                throw;
            }
            return (before, after);
        }
    }

    private Content Get(string gameId)
    {
        if (_games.TryGetValue(gameId, out var known)) return known;
        var path = PathOf(gameId);
        var content = Read(path);
        if (content is null)
        {
            if (File.Exists(path)) File.Move(path, $"{path}.kaputt-{_time.GetUtcNow():yyyyMMddHHmmssfff}", true);
            content = Read(path + ".bak") ?? new Content();
        }
        return _games[gameId] = content;
    }

    /// <returns><c>null</c> = fehlt oder kaputt. Ein Lesefehler (gesperrt) wird geworfen, sonst ginge der Stand verloren.</returns>
    private static Content? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var c = JsonSerializer.Deserialize<Content>(File.ReadAllText(path), Json.Options);
            return c is { Schema: 1, Achievements: not null } && c.Achievements.Values.All(a => a is not null) ? c : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Save(string path, Content content)
    {
        if (Read(path) is not null) File.Copy(path, path + ".bak", true);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(content, Json.Options));
    }
}
