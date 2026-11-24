using System.Collections.Concurrent;

namespace MettLauncher.Core.Achievements;

/// <summary>Änderung an einem Erfolg.</summary>
public sealed record AchievementChange(string GameId, AchievementDefinition Definition, AchievementState Before, AchievementState Now)
{
    /// <summary>Genau einmal je Erfolg wahr.</summary>
    public bool Unlocked => Before.UnlockedAt is null && Now.UnlockedAt is not null;

    /// <summary>Überschrittene Viertel-Marke (25, 50, 75), sonst 0.</summary>
    public int Milestone
    {
        get
        {
            if (Now.UnlockedAt is not null || Definition.Goal < 4) return 0;
            int Quarter(int v) => (int)(v * 4L / Definition.Goal);
            return Quarter(Now.Progress) > Quarter(Before.Progress) ? Quarter(Now.Progress) * 25 : 0;
        }
    }
}

/// <summary>Nimmt Meldungen der Spiele an und prüft sie gegen die Beschreibung des installierten Spiels.</summary>
public sealed class AchievementService(AchievementStore store, Func<string, string?> gameDir)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AchievementDefinition>> _defs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _told = new();

    /// <summary>Auf einem Hintergrund-Thread.</summary>
    public event Action<AchievementChange>? Changed;

    /// <summary>Meldung fürs Protokoll.</summary>
    public event Action<string>? Problem;

    /// <summary>Beschreibung neu lesen (Spielstart, Installation).</summary>
    public IReadOnlyList<AchievementDefinition> Reload(string gameId)
    {
        string? problem = null;
        var list = gameDir(gameId) is { } dir ? AchievementDefinitions.Load(dir, out problem) : [];
        if (problem is not null) Tell($"Erfolge von {gameId}: {problem}");
        return _defs[gameId] = list;
    }

    public IReadOnlyList<AchievementDefinition> Definitions(string gameId) =>
        _defs.TryGetValue(gameId, out var d) ? d : Reload(gameId);

    /// <summary>Stand aller Erfolge; leer, wenn er gerade nicht lesbar ist.</summary>
    public IReadOnlyDictionary<string, AchievementState> States(string gameId)
    {
        try
        {
            return store.All(gameId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell($"Erfolge von {gameId} nicht lesbar: {ex.Message}");
            return new Dictionary<string, AchievementState>();
        }
    }

    /// <summary>Meldung eines Spiels; <paramref name="value"/> <c>null</c> = freischalten.</summary>
    /// <returns><c>false</c> = nicht gespeichert, das Spiel soll es noch einmal melden.</returns>
    public bool Report(string gameId, string id, int? value)
    {
        if (Definitions(gameId).FirstOrDefault(a => a.Id == id) is not { } def)
        {
            Tell($"{gameId} meldet den unbekannten Erfolg „{id}“");
            return true;
        }
        try
        {
            var (before, now) = store.Advance(gameId, id, value ?? def.Goal, def.Goal);
            if (before != now) Changed?.Invoke(new AchievementChange(gameId, def, before, now));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Tell($"Erfolg „{id}“ von {gameId} nicht gespeichert: {ex.Message}");
            return false;
        }
    }

    private void Tell(string text)
    {
        if (_told.Count < 100 && _told.TryAdd(text, true)) Problem?.Invoke(text);
    }
}
