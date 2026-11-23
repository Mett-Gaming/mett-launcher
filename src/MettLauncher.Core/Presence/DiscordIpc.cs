using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace MettLauncher.Core.Presence;

/// <summary>Öffnet die lokale Verbindung zum Discord-Programm (Tests setzen eine eigene ein).</summary>
public interface IDiscordConnector
{
    /// <returns><c>null</c> = Discord läuft nicht.</returns>
    Task<Stream?> ConnectAsync(CancellationToken ct);
}

/// <summary>Windows: Named Pipe, Linux: Unix-Socket (auch Flatpak und Snap).</summary>
public sealed class DiscordConnector : IDiscordConnector
{
    private const int Slots = 10;

    public async Task<Stream?> ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < Slots; i++)
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync(50, ct).ConfigureAwait(false);
                    return pipe;
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
                continue;
            }
            foreach (var path in UnixPaths(i))
            {
                if (!File.Exists(path)) continue;
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException)
                {
                    socket.Dispose();
                }
            }
        }
        return null;
    }

    private static IEnumerable<string> UnixPaths(int slot)
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Environment.GetEnvironmentVariable("TMPDIR") ?? "/tmp";
        yield return Path.Combine(dir, $"discord-ipc-{slot}");
        yield return Path.Combine(dir, "app", "com.discordapp.Discord", $"discord-ipc-{slot}");
        yield return Path.Combine(dir, "snap.discord", $"discord-ipc-{slot}");
    }
}

/// <summary>Eine Verbindung zu Discord: Anmeldung mit der Anwendungs-Id, dann Aktivität setzen.</summary>
public sealed class DiscordIpc : IAsyncDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private const int MaxPayload = 1 << 20;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private DiscordIpc(Stream stream) => _stream = stream;

    /// <summary>Wird abgeschlossen, wenn Discord die Verbindung beendet.</summary>
    public Task Closed => _closed.Task;

    /// <summary>Meldet sich an; wirft bei Ablehnung (z. B. unbekannte Anwendungs-Id) oder Zeitüberschreitung.</summary>
    public static async Task<DiscordIpc> OpenAsync(Stream stream, string appId, CancellationToken ct)
    {
        var ipc = new DiscordIpc(stream);
        try
        {
            await ipc.SendAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = appId }, ct).ConfigureAwait(false);
            _ = Task.Run(ipc.ReadLoopAsync, CancellationToken.None);
            await ipc._ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return ipc;
        }
        catch
        {
            await ipc.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <param name="activity"><c>null</c> = Anzeige entfernen.</param>
    public Task SetActivityAsync(int processId, JsonObject? activity, CancellationToken ct) => SendAsync(OpFrame, new JsonObject
    {
        ["cmd"] = "SET_ACTIVITY",
        ["args"] = new JsonObject { ["pid"] = processId, ["activity"] = activity?.DeepClone() },
        ["nonce"] = Guid.NewGuid().ToString(),
    }, ct);

    private async Task SendAsync(int op, JsonNode payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception error = new IOException("Discord hat die Verbindung beendet.");
        try
        {
            var head = new byte[8];
            while (true)
            {
                await _stream.ReadExactlyAsync(head).ConfigureAwait(false);
                var op = BinaryPrimitives.ReadInt32LittleEndian(head);
                var length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
                if (length is < 0 or > MaxPayload) break;
                var body = new byte[length];
                await _stream.ReadExactlyAsync(body).ConfigureAwait(false);
                if (op == OpClose) break;
                var json = JsonNode.Parse(body);
                if (op == OpPing) await SendAsync(OpPong, json ?? new JsonObject(), CancellationToken.None).ConfigureAwait(false);
                if (op == OpFrame && (string?)json?["evt"] == "READY") _ready.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            error = ex;
        }
        _ready.TrySetException(error);
        _ = _ready.Task.Exception; // als beobachtet markieren
        _closed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch (IOException) { }
        _closed.TrySetResult();
    }
}
