using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.Launch;
using MettLauncher.Core.Presence;
using MettLauncher.Core.Settings;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class PresenceTests
{
    private const string LauncherApp = "111111111111111111", GameApp = "222222222222222222";

    private static async Task Until(Func<bool> ok)
    {
        var sw = Stopwatch.StartNew();
        while (!ok())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "Zeitüberschreitung");
            await Task.Delay(5);
        }
    }

    private static async Task<(TcpClient, StreamWriter)> Connect(IReadOnlyDictionary<string, string> env)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPEndPoint.Parse(env[PresenceServer.AddressVariable]));
        return (client, new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" });
    }

    // ---- Meldungen der Spiele ----

    [Fact]
    public async Task Game_reports_status_and_disconnect_clears_it()
    {
        using var server = new PresenceServer();
        server.Start();
        var seen = new List<(string, GamePresence?)>();
        server.Changed += (g, p) => { lock (seen) seen.Add((g, p)); };
        var env = server.Register("kasse0");
        var (client, w) = await Connect(env);
        await w.WriteLineAsync($$"""{"token":"{{env[PresenceServer.TokenVariable]}}","details":"Im Markt"}""");
        await w.WriteLineAsync("kaputt");
        await w.WriteLineAsync("""{"details":" Käse\u0007 ","state":"","party":[2,8],"unbekannt":1}""");
        await Until(() => { lock (seen) return seen.Count == 2; });
        client.Dispose();
        await Until(() => { lock (seen) return seen.Count == 3; });
        Assert.Equal(("kasse0", new GamePresence { Details = "Im Markt" }), seen[0]);
        Assert.Equal(new GamePresence { Details = "Käse", PartySize = 2, PartyMax = 8 }, seen[1].Item2);
        Assert.Null(seen[2].Item2);
    }

    [Theory]
    [InlineData("""{"token":"falsch","details":"x"}""")]
    [InlineData("""{"details":"ohne Token"}""")]
    [InlineData("[]")]
    [InlineData("Müll")]
    public async Task Strangers_are_disconnected(string line)
    {
        using var server = new PresenceServer();
        server.Start();
        var called = false;
        server.Changed += (_, _) => called = true;
        var (client, w) = await Connect(server.Register("kasse0"));
        using (client)
        {
            await w.WriteLineAsync(line);
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(called);
    }

    [Fact]
    public async Task Too_long_lines_and_old_tokens_are_rejected()
    {
        using var server = new PresenceServer();
        server.Start();
        var called = false;
        server.Changed += (_, _) => called = true;
        var old = server.Register("kasse0");
        server.Register("kasse0");
        var (a, wa) = await Connect(old);
        await wa.WriteLineAsync($$"""{"token":"{{old[PresenceServer.TokenVariable]}}"}""");
        Assert.Equal(0, await a.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var (b, wb) = await Connect(old);
        await wb.WriteAsync(new string('x', 5000));
        await Until(() => { try { return b.GetStream().Read(new byte[1]) == 0; } catch (IOException) { return true; } });
        Assert.False(called);
        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task Texts_are_cut()
    {
        using var server = new PresenceServer();
        server.Start();
        GamePresence? got = null;
        server.Changed += (_, p) => got ??= p;
        var env = server.Register("x");
        var (client, w) = await Connect(env);
        await w.WriteLineAsync($$"""{"token":"{{env[PresenceServer.TokenVariable]}}","details":"{{new string('a', 300)}}","party":[9,2]}""");
        await Until(() => got is not null);
        client.Dispose();
        Assert.Equal(128, got!.Details!.Length);
        Assert.Null(got.PartySize);
    }

    // ---- Aktivität ----

    [Fact]
    public void Activity_names_the_game_without_own_app()
    {
        var p = new GamePresence { Details = "Im Markt", State = "Stufe: unruhig", PartySize = 2, PartyMax = 8 };
        var start = DateTimeOffset.FromUnixTimeSeconds(1000);
        var a = PresenceActivity.ForGame("KASSE 0", "Horror", new Uri("https://x.y/c.png"), start, p, ownApp: false);
        Assert.Equal("KASSE 0", (string?)a["details"]);
        Assert.Equal("Im Markt · Stufe: unruhig", (string?)a["state"]);
        Assert.Equal(1000, (long)a["timestamps"]!["start"]!);
        Assert.Equal("[2,8]", a["party"]!["size"]!.ToJsonString());
        Assert.Equal("https://x.y/c.png", (string?)a["assets"]!["large_image"]);
        var own = PresenceActivity.ForGame("KASSE 0", null, null, start, p, ownApp: true);
        Assert.Equal("Im Markt", (string?)own["details"]);
        Assert.Equal("Stufe: unruhig", (string?)own["state"]);
        Assert.Null(own["assets"]);
        var bare = PresenceActivity.ForGame("KASSE 0", null, null, start, null, ownApp: true);
        Assert.False(bare.ContainsKey("details"));
    }

    // ---- Zusammenspiel ----

    private sealed class FakeProcess : IRunningProcess
    {
        public TaskCompletionSource Exit { get; } = new();
        public int Id => 4242;
        public int ExitCode => 0;
        public Task WaitForExitAsync(CancellationToken ct = default) => Exit.Task;
        public void Dispose() { }
    }

    private sealed class FakeStarter(FakeProcess process) : IProcessStarter
    {
        public IRunningProcess Start(ProcessStartInfo startInfo) => process;
        public bool IsRunning(string exePath) => false;
    }

    [Fact]
    public async Task Service_follows_launcher_game_reports_and_setting()
    {
        using var dir = new TempDir();
        var library = new LibraryStore(dir.Paths);
        var install = dir.Sub("spiel");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "KASSE0.exe"), "");
        library.UpdateGame("kasse0", s => { s.InstallPath = install; s.InstalledVersion = "1.0.0"; s.InstalledTag = "v1.0.0"; });
        var process = new FakeProcess();
        var launcher = new GameLauncher(library, new FakeStarter(process));
        var settings = new SettingsStore(dir.Paths);
        using var server = new PresenceServer();
        server.Start();
        PresenceTarget? shown = null;
        var service = new PresenceService(server, t => shown = t, launcher, settings, p => p is null ? null : new Uri("https://x.y/" + p));
        var game = TestData.Kasse0();
        service.UpdateCatalog(new GameCatalog { Games = [game] });
        Assert.Null(shown); // keine Anwendungs-Id
        service.UpdateCatalog(new GameCatalog { Games = [game], DiscordAppId = "quatsch" });
        Assert.Null(shown);
        service.UpdateCatalog(new GameCatalog { Games = [game], DiscordAppId = LauncherApp });
        Assert.Equal("Im Launcher", (string?)shown!.Activity["details"]);

        var env = service.Environment("kasse0", new Dictionary<string, string> { ["METT_NET_ADDR"] = "a" });
        Assert.Equal(3, env.Count);
        var session = launcher.Launch(game, env);
        Assert.Equal((LauncherApp, 4242, "KASSE 0"), (shown!.AppId, shown.ProcessId, (string?)shown.Activity["details"]));
        var (client, w) = await Connect(env);
        await w.WriteLineAsync($$"""{"token":"{{env[PresenceServer.TokenVariable]}}","state":"An der Kasse"}""");
        await Until(() => (string?)shown!.Activity["state"] == "An der Kasse");

        service.UpdateCatalog(new GameCatalog { Games = [game with { DiscordAppId = GameApp }], DiscordAppId = LauncherApp });
        Assert.Equal(GameApp, shown!.AppId);
        settings.Update(s => s with { DiscordPresence = false });
        Assert.Null(shown);
        settings.Update(s => s with { DiscordPresence = true });
        Assert.NotNull(shown);

        process.Exit.SetResult();
        await session.Exited;
        await Until(() => (string?)shown!.Activity["details"] == "Im Launcher");
        client.Dispose();
    }

    // ---- Discord-Protokoll ----

    /// <summary>Spielt Discord: nimmt die Anmeldung an und sammelt Aktivitäten.</summary>
    private sealed class FakeDiscord : IDiscordConnector
    {
        public List<string> Apps { get; } = [];
        public List<JsonNode> Commands { get; } = [];
        public bool Running { get; set; } = true;
        public Stream? Server { get; private set; }

        public Task<Stream?> ConnectAsync(CancellationToken ct)
        {
            if (!Running) return Task.FromResult<Stream?>(null);
            var (there, back) = (new Pipe(), new Pipe());
            Server = new Duplex(there.Reader.AsStream(), back.Writer.AsStream());
            _ = Task.Run(() => ServeAsync(Server), ct);
            return Task.FromResult<Stream?>(new Duplex(back.Reader.AsStream(), there.Writer.AsStream()));
        }

        private async Task ServeAsync(Stream s)
        {
            try
            {
                while (true)
                {
                    var head = new byte[8];
                    await s.ReadExactlyAsync(head);
                    var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4))];
                    await s.ReadExactlyAsync(body);
                    var json = JsonNode.Parse(body)!;
                    if (BinaryPrimitives.ReadInt32LittleEndian(head) == 0)
                    {
                        lock (Commands) Apps.Add((string)json["client_id"]!);
                        var ready = Encoding.UTF8.GetBytes("""{"cmd":"DISPATCH","evt":"READY"}""");
                        var frame = new byte[8 + ready.Length];
                        frame[0] = 1;
                        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), ready.Length);
                        ready.CopyTo(frame, 8);
                        await s.WriteAsync(frame);
                        await s.FlushAsync();
                    }
                    else lock (Commands) Commands.Add(json);
                }
            }
            catch (Exception) { }
        }

        public int Count { get { lock (Commands) return Commands.Count; } }
    }

    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => read.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => read.ReadAsync(b, ct);
        public override void Write(byte[] b, int o, int c) => write.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => write.WriteAsync(b, ct);
        public override long Seek(long o, SeekOrigin s) => 0;
        public override void SetLength(long v) { }
        protected override void Dispose(bool disposing) { write.Dispose(); read.Dispose(); }
    }

    private static PresenceTarget Target(string app, string details) => new(app, 7, new JsonObject { ["details"] = details });

    [Fact]
    public async Task Discord_gets_handshake_updates_and_new_login_per_app()
    {
        var discord = new FakeDiscord();
        await using var p = new DiscordPresence(discord)
        {
            MinInterval = TimeSpan.FromMilliseconds(10),
            RetryInterval = TimeSpan.FromMilliseconds(30),
        };
        p.Show(Target(LauncherApp, "eins"));
        await Until(() => discord.Count == 1);
        p.Show(Target(LauncherApp, "eins")); // gleich: nichts senden
        p.Show(Target(LauncherApp, "zwei"));
        await Until(() => discord.Count == 2);
        p.Show(Target(GameApp, "drei"));
        await Until(() => discord.Count == 3);
        Assert.Equal([LauncherApp, GameApp], discord.Apps);
        var c = discord.Commands[2];
        Assert.Equal("SET_ACTIVITY", (string?)c["cmd"]);
        Assert.Equal(7, (int)c["args"]!["pid"]!);
        Assert.Equal("drei", (string?)c["args"]!["activity"]!["details"]);
    }

    [Fact]
    public async Task Discord_may_start_later_or_restart()
    {
        var discord = new FakeDiscord { Running = false };
        await using var p = new DiscordPresence(discord)
        {
            MinInterval = TimeSpan.FromMilliseconds(10),
            RetryInterval = TimeSpan.FromMilliseconds(30),
        };
        p.Show(Target(LauncherApp, "eins"));
        await Task.Delay(100);
        Assert.Equal(0, discord.Count);
        discord.Running = true;
        await Until(() => discord.Count == 1);
        discord.Server!.Dispose();
        await Until(() => discord.Count == 2);
        p.Show(null);
        await Task.Delay(100);
        Assert.Equal(2, discord.Count);
    }
}
