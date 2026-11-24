using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MettLauncher.Core.Overlay;

public enum OverlayAnchor : byte { TopLeft, TopRight, BottomLeft, BottomRight, Center }

/// <summary>Ein Rechteck im Overlay. Längen gelten bei 1080 Zeilen; die Schicht rechnet um.</summary>
public sealed record OverlayQuad(uint Image, OverlayAnchor Anchor, float X, float Y, float W, float H)
{
    public const int Bytes = 45;
    public float Alpha { get; init; } = 1;
    /// <summary>Sekunden, wie die folgenden.</summary>
    public float FadeIn { get; init; }
    /// <summary>Negativ = bleibt stehen.</summary>
    public float Hold { get; init; } = -1;
    public float FadeOut { get; init; }
    public float SlideX { get; init; }
    public float SlideY { get; init; }

    internal void Write(Span<byte> at)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(at, Image);
        at[4] = (byte)Anchor;
        float[] values = [X, Y, W, H, Alpha, FadeIn, Hold, FadeOut, SlideX, SlideY];
        for (var k = 0; k < values.Length; k++) BinaryPrimitives.WriteSingleLittleEndian(at[(5 + k * 4)..], values[k]);
    }
}

/// <summary>Was die Schicht über das laufende Spiel meldet.</summary>
public sealed record OverlayStats(double Fps, int Width, int Height);

/// <summary>Verbindung zur Overlay-Schicht in gestarteten Spielen (localhost); Gegenstück: <c>native/mett-overlay</c>.</summary>
public sealed class OverlayServer : IDisposable
{
    public const string AddressVariable = "METT_OVERLAY_ADDR", TokenVariable = "METT_OVERLAY_TOKEN";
    private const byte KindImage = 1, KindQuads = 2, KindRemove = 3, KindClear = 4;
    private const int MaxLine = 1024;

    private sealed record Connection(NetworkStream Stream)
    {
        public SemaphoreSlim Turn { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, string> _tokens = new(); // Token -> Spiel
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    /// <summary>Schicht eines Spiels verbunden bzw. getrennt. Auf einem Hintergrund-Thread.</summary>
    public event Action<string, bool>? Connected;
    public event Action<string, OverlayStats>? Stats;

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

    public bool IsConnected(string gameId) => _connections.ContainsKey(gameId);

    /// <summary>Umgebungsvariablen für einen Spielstart; frühere Tokens des Spiels verfallen.</summary>
    public IReadOnlyDictionary<string, string> Register(string gameId)
    {
        Unregister(gameId);
        if (_listener?.LocalEndpoint is not IPEndPoint ep) return new Dictionary<string, string>();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = gameId;
        return new Dictionary<string, string> { [AddressVariable] = $"127.0.0.1:{ep.Port}", [TokenVariable] = token };
    }

    public void Unregister(string gameId)
    {
        foreach (var (token, game) in _tokens)
            if (game == gameId) _tokens.TryRemove(token, out _);
    }

    /// <param name="rgba">Mit Alpha vormultipliziert, vier Byte je Bildpunkt.</param>
    public Task SendImageAsync(string gameId, uint id, int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("falsche Datenmenge", nameof(rgba));
        var body = new byte[12 + rgba.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, id);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), height);
        rgba.CopyTo(body, 12);
        return SendAsync(gameId, KindImage, body);
    }

    /// <summary>Ersetzt alles Gezeigte.</summary>
    public Task SendQuadsAsync(string gameId, IReadOnlyList<OverlayQuad> quads)
    {
        var body = new byte[quads.Count * OverlayQuad.Bytes];
        for (var i = 0; i < quads.Count; i++) quads[i].Write(body.AsSpan(i * OverlayQuad.Bytes, OverlayQuad.Bytes));
        return SendAsync(gameId, KindQuads, body);
    }

    public Task RemoveImageAsync(string gameId, uint id)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, id);
        return SendAsync(gameId, KindRemove, body);
    }

    public Task ClearAsync(string gameId) => SendAsync(gameId, KindClear, []);

    private async Task SendAsync(string gameId, byte kind, byte[] body)
    {
        if (!_connections.TryGetValue(gameId, out var c)) return;
        var packet = new byte[5 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(packet, body.Length);
        packet[4] = kind;
        body.CopyTo(packet, 5);
        await c.Turn.WaitAsync().ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            await c.Stream.WriteAsync(packet, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            c.Stream.Dispose(); // halbe Pakete darf es nicht geben
        }
        finally
        {
            c.Turn.Release();
        }
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
        string? game = null;
        Connection? mine = null;
        using (client)
        {
            try
            {
                client.NoDelay = true;
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
                        if (game is null)
                        {
                            if (ReadToken(line) is not { } token || !_tokens.TryGetValue(token, out game)) return;
                            _connections[game] = mine = new Connection(stream);
                            Connected?.Invoke(game, true);
                        }
                        else if (ReadStats(line) is { } stats && Current(game, mine!)) Stats?.Invoke(game, stats);
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
                // Eine neuere Verbindung desselben Spiels bleibt.
                if (mine is not null && _connections.TryRemove(KeyValuePair.Create(game!, mine))) Connected?.Invoke(game!, false);
            }
        }
    }

    private bool Current(string game, Connection mine) =>
        _connections.TryGetValue(game, out var c) && ReferenceEquals(c, mine);

    private static string? ReadToken(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.GetProperty("token").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static OverlayStats? ReadStats(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            return new OverlayStats(r.GetProperty("fps").GetDouble(), r.GetProperty("w").GetInt32(), r.GetProperty("h").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        _listener = null;
    }
}
