using System.Text.Json.Nodes;

namespace MettLauncher.Core.Presence;

/// <summary>Was gerade angezeigt werden soll.</summary>
/// <param name="AppId">Discord-Anwendung, unter deren Namen der Status erscheint.</param>
public sealed record PresenceTarget(string AppId, int ProcessId, JsonObject Activity)
{
    public bool SameAs(PresenceTarget? other) => other is not null && AppId == other.AppId && ProcessId == other.ProcessId
        && JsonNode.DeepEquals(Activity, other.Activity);
}

/// <summary>Baut die Discord-Aktivität aus Spiel und gemeldetem Status.</summary>
public static class PresenceActivity
{
    public const string LauncherUrl = "https://github.com/Mett-Gaming/mett-launcher/releases/latest";

    /// <param name="ownApp">Das Spiel hat eine eigene Discord-Anwendung: sein Name steht dann schon darüber.</param>
    public static JsonObject ForGame(string gameName, string? tagline, Uri? cover, DateTimeOffset startedAt,
        GamePresence? presence, bool ownApp)
    {
        var details = presence?.Details;
        var state = presence?.State;
        if (!ownApp)
        {
            // Oben steht „Mett Launcher“ -> der Spielname gehört in die erste Zeile.
            state = string.Join(" · ", new[] { details, state }.Where(s => s is not null)) is { Length: > 0 } s ? s : null;
            details = gameName;
        }
        var activity = new JsonObject
        {
            ["timestamps"] = new JsonObject { ["start"] = startedAt.ToUnixTimeSeconds() },
            ["status_display_type"] = ownApp ? 0 : 2, // in der Mitgliederliste: Anwendungsname bzw. Details (Spielname)
            ["buttons"] = new JsonArray(new JsonObject { ["label"] = "Mett Launcher", ["url"] = LauncherUrl }),
        };
        if (Text(details) is { } d) activity["details"] = d;
        if (Text(state) is { } st) activity["state"] = st;
        if (cover is { Scheme: "https" })
            activity["assets"] = new JsonObject { ["large_image"] = cover.AbsoluteUri, ["large_text"] = Text(tagline) ?? gameName };
        if (presence is { PartySize: { } size, PartyMax: { } max })
            activity["party"] = new JsonObject { ["id"] = $"mett-{gameName}", ["size"] = new JsonArray(size, max) };
        return activity;
    }

    public static JsonObject ForLauncher(DateTimeOffset startedAt) => new()
    {
        ["details"] = "Im Launcher",
        ["timestamps"] = new JsonObject { ["start"] = startedAt.ToUnixTimeSeconds() },
        ["buttons"] = new JsonArray(new JsonObject { ["label"] = "Mett Launcher", ["url"] = LauncherUrl }),
    };

    /// <summary>Discord verlangt 2 bis 128 Zeichen.</summary>
    private static string? Text(string? s) => s switch
    {
        null => null,
        { Length: < 2 } => null,
        { Length: > GamePresence.MaxText } => s[..(GamePresence.MaxText - 1)] + "…",
        _ => s,
    };
}

/// <summary>
/// Hält die Anzeige in Discord aktuell: verbindet sich, sobald Discord läuft, meldet sich bei anderer Anwendung neu an und
/// sendet höchstens alle paar Sekunden. Fehler bleiben unsichtbar – ohne Discord passiert einfach nichts.
/// </summary>
public sealed class DiscordPresence : IAsyncDisposable
{
    private readonly IDiscordConnector _connector;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Task _loop;
    private PresenceTarget? _target;

    public DiscordPresence(IDiscordConnector? connector = null)
    {
        _connector = connector ?? new DiscordConnector();
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Discord erlaubt 5 Änderungen in 20 Sekunden.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(4.5);
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <param name="target"><c>null</c> = nichts anzeigen.</param>
    public void Show(PresenceTarget? target)
    {
        if (target is null ? Volatile.Read(ref _target) is null : target.SameAs(Volatile.Read(ref _target))) return;
        Volatile.Write(ref _target, target);
        _wake.Release();
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        DiscordIpc? ipc = null;
        PresenceTarget? shown = null;
        Task<bool>? wake = null;
        try
        {
            while (true)
            {
                var target = Volatile.Read(ref _target);
                try
                {
                    if (ipc is not null && (ipc.Closed.IsCompleted || target?.AppId != shown?.AppId))
                    {
                        // Schließen entfernt auch die alte Anzeige.
                        await ipc.DisposeAsync().ConfigureAwait(false);
                        (ipc, shown) = (null, null);
                    }
                    if (target is not null && ipc is null)
                    {
                        var stream = await _connector.ConnectAsync(ct).ConfigureAwait(false);
                        if (stream is not null) ipc = await DiscordIpc.OpenAsync(stream, target.AppId, ct).ConfigureAwait(false);
                    }
                    if (target is not null && ipc is not null && !target.SameAs(shown))
                    {
                        await ipc.SetActivityAsync(target.ProcessId, target.Activity, ct).ConfigureAwait(false);
                        shown = target;
                        await Task.Delay(MinInterval, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    if (ipc is not null) await ipc.DisposeAsync().ConfigureAwait(false);
                    (ipc, shown) = (null, null);
                    await Task.Delay(MinInterval, ct).ConfigureAwait(false);
                }
                // Neues Ziel abwarten; ohne Verbindung regelmäßig neu versuchen.
                wake ??= _wake.WaitAsync(RetryInterval, ct);
                await Task.WhenAny(wake, ipc?.Closed ?? wake).Unwrap().ConfigureAwait(false);
                if (wake.IsCompleted) wake = null; // sonst weiter benutzen, damit kein Wecken verloren geht
            }
        }
        catch (OperationCanceledException)
        {
            // Launcher schließt
        }
        finally
        {
            if (ipc is not null) await ipc.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
    }
}
