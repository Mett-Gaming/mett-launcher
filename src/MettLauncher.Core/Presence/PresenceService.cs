using System.Text.Json.Nodes;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.Launch;
using MettLauncher.Core.Settings;

namespace MettLauncher.Core.Presence;

/// <summary>
/// Verbindet Spielstarts, Statusmeldungen der Spiele und Einstellungen mit der Discord-Anzeige. Ohne Anwendungs-Id im
/// Katalog (<c>discordAppId</c>) oder bei ausgeschalteter Einstellung wird nichts angezeigt.
/// </summary>
public sealed class PresenceService
{
    private sealed record Running(GameEntry Game, GameSession Session);

    private readonly PresenceServer _server;
    private readonly Action<PresenceTarget?> _show;
    private readonly SettingsStore _settings;
    private readonly Func<string?, Uri?> _resolveAsset;
    private readonly DateTimeOffset _launcherStart;
    private readonly Lock _lock = new();
    private readonly List<Running> _running = [];
    private readonly Dictionary<string, GamePresence> _reports = new(StringComparer.OrdinalIgnoreCase);
    private GameCatalog _catalog = new();

    public PresenceService(PresenceServer server, Action<PresenceTarget?> show, GameLauncher launcher, SettingsStore settings,
        Func<string?, Uri?> resolveAsset, TimeProvider? time = null)
    {
        _server = server;
        _show = show;
        _settings = settings;
        _resolveAsset = resolveAsset;
        _launcherStart = (time ?? TimeProvider.System).GetUtcNow();
        server.Changed += OnReport;
        launcher.GameStarted += (_, s) => OnStarted(s);
        launcher.GameExited += (_, e) => OnExited(e.GameId);
        settings.Changed += (_, _) => Refresh();
    }

    /// <summary>Neuer Katalog (Anwendungs-Ids, Cover).</summary>
    public void UpdateCatalog(GameCatalog catalog)
    {
        lock (_lock) _catalog = catalog;
        Refresh();
    }

    /// <summary>Umgebung für einen Spielstart: <paramref name="other"/> plus Adresse und Token für Statusmeldungen.</summary>
    public IReadOnlyDictionary<string, string> Environment(string gameId, IReadOnlyDictionary<string, string>? other)
    {
        var env = other is null ? [] : new Dictionary<string, string>(other);
        lock (_lock) _reports.Remove(gameId);
        foreach (var (k, v) in _server.Register(gameId)) env[k] = v;
        return env;
    }

    private void OnStarted(GameSession session)
    {
        lock (_lock)
        {
            if (_catalog.Find(session.GameId) is { } game) _running.Add(new Running(game, session));
        }
        Refresh();
    }

    private void OnExited(string gameId)
    {
        _server.Unregister(gameId);
        lock (_lock)
        {
            _running.RemoveAll(r => string.Equals(r.Game.Id, gameId, StringComparison.OrdinalIgnoreCase));
            _reports.Remove(gameId);
        }
        Refresh();
    }

    private void OnReport(string gameId, GamePresence? presence)
    {
        lock (_lock)
        {
            if (presence is null) _reports.Remove(gameId);
            else _reports[gameId] = presence;
        }
        Refresh();
    }

    private void Refresh()
    {
        // Unter der Sperre anzeigen, damit sich zwei Meldungen nicht überholen.
        lock (_lock) _show(Current());
    }

    private PresenceTarget? Current()
    {
        if (!_settings.Current.DiscordPresence) return null;
        // Laufen mehrere Spiele, zählt das zuletzt gestartete.
        if (_running.Count > 0)
        {
            var (started, session) = _running[^1];
            var game = _catalog.Find(started.Id) ?? started;
            var own = Clean(game.DiscordAppId);
            if ((own ?? Clean(_catalog.DiscordAppId)) is not { } app) return null;
            var activity = PresenceActivity.ForGame(game.Name, game.Tagline, _resolveAsset(game.Cover), session.StartedAt,
                _reports.GetValueOrDefault(game.Id), own is not null);
            return new PresenceTarget(app, session.ProcessId, activity);
        }
        return Clean(_catalog.DiscordAppId) is { } launcher
            ? new PresenceTarget(launcher, System.Environment.ProcessId, PresenceActivity.ForLauncher(_launcherStart))
            : null;
    }

    /// <summary>Anwendungs-Ids sind reine Zahlen; alles andere gilt als nicht gesetzt.</summary>
    private static string? Clean(string? id) => id is { Length: >= 17 and <= 20 } && id.All(char.IsAsciiDigit) ? id : null;
}
