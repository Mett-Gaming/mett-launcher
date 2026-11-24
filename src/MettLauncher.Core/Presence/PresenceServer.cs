using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MettLauncher.Core.Presence;

/// <summary>Was ein Spiel über sich meldet. Alles optional.</summary>
public sealed record GamePresence
{
    /// <summary>Was gerade passiert, z. B. „Im Markt“.</summary>
    public string? Details { get; init; }
    /// <summary>Zusatz, z. B. „Stufe: unruhig“.</summary>
    public string? State { get; init; }
    public int? PartySize { get; init; }
    public int? PartyMax { get; init; }

    /// <summary>Längste Texte, die Discord annimmt.</summary>
    public const int MaxText = 128;

    /// <summary>Liest eine Zeile des Spiels; kaputte Angaben fallen einzeln weg.</summary>
    internal static GamePresence From(JsonElement json)
    {
        int? size = null, max = null;
        if (json.TryGetProperty("party", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2
            && p[0].TryGetInt32OrNull() is { } s and > 0 && p[1].TryGetInt32OrNull() is { } m && m >= s && m <= 1000)
            (size, max) = (s, m);
        return new GamePresence
        {
            Details = Clean(json, "details"),
            State = Clean(json, "state"),
            PartySize = size,
            PartyMax = max,
        };
    }

    private static string? Clean(JsonElement json, string key)
    {
        if (!json.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var text = new string([.. v.GetString()!.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
        if (text.Length == 0) return null;
        if (text.Length <= MaxText) return text;
        var cut = char.IsHighSurrogate(text[MaxText - 1]) ? MaxText - 1 : MaxText;
        return text[..cut];
    }
}

internal static class JsonElementExtensions
{
    public static int? TryGetInt32OrNull(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) ? i : null;
}

/// <summary>
/// Nimmt Statusmeldungen gestarteter Spiele an (localhost, eine JSON-Zeile je Meldung). Jedes Spiel bekommt beim Start
/// Adresse und eigenes Token als Umgebungsvariablen; unter Proton reicht Wine beides durch.
/// </summary>
public sealed class PresenceServer : IDisposable
{
    public const string AddressVariable = "METT_PRESENCE_ADDR";
    public const string TokenVariable = "METT_PRESENCE_TOKEN";
    /// <summary>Längste Zeile (Byte); wer mehr schickt, wird getrennt.</summary>
    private const int MaxLine = 4096;

    private readonly ConcurrentDictionary<string, string> _tokens = new(); // Token -> Spiel
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    /// <summary>Spiel-Id und neuer Status (<c>null</c> = Verbindung weg). Auf einem Hintergrund-Thread.</summary>
    public event Action<string, GamePresence?>? Changed;

    /// <summary>
    /// Erfolg gemeldet: Spiel, Id, Fortschritt (<c>null</c> = freischalten). <c>true</c> = verarbeitet, das Spiel
    /// bekommt dann eine Bestätigung und meldet es nicht noch einmal.
    /// </summary>
    public Func<string, string, int?, bool>? Achievement { get; set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Startet den Empfang; klappt es nicht, bleibt der Dienst aus.</summary>
    public void Start()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            _ = Task.Run(() => AcceptAsync(listener));
        }
        catch (SocketException)
        {
            _listener = null;
        }
    }

    /// <summary>Umgebungsvariablen für einen Spielstart; frühere Tokens des Spiels verfallen.</summary>
    public IReadOnlyDictionary<string, string> Register(string gameId)
    {
        Unregister(gameId);
        if (_listener?.LocalEndpoint is not IPEndPoint ep) return new Dictionary<string, string>();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = gameId;
        return new Dictionary<string, string>
        {
            [AddressVariable] = $"127.0.0.1:{ep.Port}",
            [TokenVariable] = token,
        };
    }

    public void Unregister(string gameId)
    {
        foreach (var (token, game) in _tokens)
            if (game == gameId) _tokens.TryRemove(token, out _);
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (ex is not SocketException) return;
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        string? game = null, token = null;
        var budget = new ReportBudget();
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[MaxLine];
                var used = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(used), _stop.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    used += read;
                    int end;
                    while ((end = Array.IndexOf(buffer, (byte)'\n', 0, used)) >= 0)
                    {
                        var line = Encoding.UTF8.GetString(buffer, 0, end);
                        Buffer.BlockCopy(buffer, end + 1, buffer, 0, used - end - 1);
                        used -= end + 1;
                        if (!Handle(line, ref game, ref token, budget, out var ack)) return;
                        if (ack is { } n)
                            await stream.WriteAsync(Encoding.UTF8.GetBytes($"{{\"ack\":{n}}}\n"), _stop.Token).ConfigureAwait(false);
                    }
                    if (used == buffer.Length) return; // Zeile zu lang
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Spiel beendet oder Launcher schließt
            }
            finally
            {
                // Ein Neustart mit neuem Token darf von der alten Verbindung nicht gelöscht werden.
                if (game is not null && _tokens.ContainsKey(token!)) Changed?.Invoke(game, null);
            }
        }
    }

    /// <returns><c>false</c> = Verbindung trennen.</returns>
    private bool Handle(string line, ref string? game, ref string? token, ReportBudget budget, out long? ack)
    {
        ack = null;
        if (string.IsNullOrWhiteSpace(line)) return true;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var json = doc.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return false;
            if (game is null)
            {
                // Erste Zeile: Token vorzeigen (darf schon einen Status enthalten)
                token = json.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (token is null || !_tokens.TryGetValue(token, out game)) { game = null; return false; }
            }
            // Erfolge zählen auch noch, wenn das Spiel gerade beendet wurde.
            if (Text(json, "unlock") is { } unlock) return Report(game, json, unlock, null, budget, out ack);
            if (Text(json, "progress") is { } progress)
                return Report(game, json, progress, json.TryGetProperty("value", out var v) ? v.TryGetInt32OrNull() ?? 0 : 0, budget, out ack);
            if (!_tokens.ContainsKey(token!)) return false; // Spiel beendet
            Changed?.Invoke(game, GamePresence.From(json));
            return true;
        }
        catch (JsonException)
        {
            return game is not null; // kaputte Meldung überspringen
        }
    }

    private static string? Text(JsonElement json, string key) =>
        json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private bool Report(string game, JsonElement json, string id, int? value, ReportBudget budget, out long? ack)
    {
        ack = null;
        if (!budget.Take()) return true; // zu viele auf einmal: ohne Bestätigung, das Spiel meldet sie später noch einmal
        // Ungültige Ids werden bestätigt, sonst kämen sie immer wieder.
        var done = !Achievements.AchievementDefinitions.IsId(id) || (Achievement?.Invoke(game, id, value) ?? false);
        if (done && json.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt64(out var number))
            ack = number;
        return true;
    }

    /// <summary>Höchstens 300 Erfolgsmeldungen in zehn Sekunden je Verbindung.</summary>
    private sealed class ReportBudget
    {
        private long _start = Environment.TickCount64;
        private int _used;

        public bool Take()
        {
            if (Environment.TickCount64 - _start > 10_000) (_start, _used) = (Environment.TickCount64, 0);
            return ++_used <= 300;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        _listener = null;
    }
}
