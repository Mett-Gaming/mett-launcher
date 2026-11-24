using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MettLauncher.Core.Achievements;
using MettLauncher.Core.Overlay;
using MettLauncher.Core.Presence;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public sealed class AchievementTests : IDisposable
{
    private const string Good = """
        { "schema": 1, "achievements": [
          { "id": "erste", "name": "Erste", "text": "Text", "icon": "erste.png" },
          { "id": "hundert", "name": "Hundert", "goal": 100, "hidden": true } ] }
        """;

    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private string Game(string json)
    {
        var dir = _tmp.Sub("spiel", "erfolge");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "erfolge.json"), json);
        return _tmp.Sub("spiel");
    }

    private string StoreFile => _tmp.Sub("profil", "achievements", "kasse0.json");
    private AchievementStore Store() => new(_tmp.Sub("profil"));

    private static async Task Until(Func<bool> ok)
    {
        var sw = Stopwatch.StartNew();
        while (!ok())
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "Zeitüberschreitung");
            await Task.Delay(5);
        }
    }

    // ---- Beschreibung ----

    [Fact]
    public void Definitions_are_read_and_icons_checked()
    {
        var dir = Game(Good);
        var list = AchievementDefinitions.Load(dir, out var problem);
        Assert.Null(problem);
        Assert.Equal(["erste", "hundert"], list.Select(a => a.Id));
        Assert.Equal((1, 100, true), (list[0].Goal, list[1].Goal, list[1].Hidden));
        Assert.Null(list[0].Icon); // Bild fehlt
        var png = Path.Combine(dir, "erfolge", "erste.png");
        File.WriteAllBytes(png, new byte[10]);
        Assert.Equal(png, AchievementDefinitions.Load(dir, out _)[0].Icon);
        File.WriteAllBytes(png, new byte[256 * 1024 + 1]);
        Assert.Null(AchievementDefinitions.Load(dir, out _)[0].Icon);
    }

    [Fact]
    public void Icons_outside_the_folder_are_ignored()
    {
        var dir = Game("""{"schema":1,"achievements":[{"id":"a","name":"A","icon":"../geheim.png"}]}""");
        File.WriteAllBytes(Path.Combine(dir, "geheim.png"), new byte[10]);
        Assert.Null(Assert.Single(AchievementDefinitions.Load(dir, out _)).Icon);
    }

    [Theory]
    [InlineData("kaputt")]
    [InlineData("null")]
    [InlineData("""{"schema":2,"achievements":[]}""")]
    [InlineData("""{"schema":1}""")]
    [InlineData("""{"schema":1,"achievements":[null]}""")]
    [InlineData("""{"schema":1,"achievements":[{"id":"Groß","name":"A"}]}""")]
    [InlineData("""{"schema":1,"achievements":[{"id":"a"}]}""")]
    [InlineData("""{"schema":1,"achievements":[{"id":"a","name":"A","goal":0}]}""")]
    [InlineData("""{"schema":1,"achievements":[{"id":"a","name":"A"},{"id":"a","name":"B"}]}""")]
    public void Broken_definitions_only_switch_themselves_off(string json)
    {
        Assert.Empty(AchievementDefinitions.Load(Game(json), out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Games_without_definitions_are_fine()
    {
        Assert.Empty(AchievementDefinitions.Load(_tmp.Path, out var problem));
        Assert.Null(problem);
    }

    // ---- Speichern ----

    [Fact]
    public void Progress_never_drops_and_unlocks_once()
    {
        var s = Store();
        Assert.Equal(40, s.Advance("kasse0", "x", 40, 100).After.Progress);
        Assert.Equal(40, s.Advance("kasse0", "x", 10, 100).After.Progress);
        var (before, now) = s.Advance("kasse0", "x", 500, 100);
        Assert.Null(before.UnlockedAt);
        Assert.NotNull(now.UnlockedAt);
        Assert.Equal(100, now.Progress);
        var again = s.Advance("kasse0", "x", 100, 100);
        Assert.Equal(again.Before, again.After);
        Assert.Equal(now, Store().All("kasse0")["x"]); // nach Neustart gleich
        // Höheres Ziel nach einem Update nimmt nichts weg
        Assert.Equal(now.UnlockId, Store().Advance("kasse0", "x", 150, 200).After.UnlockId);
    }

    [Fact]
    public void Broken_file_falls_back_to_the_backup_and_is_kept()
    {
        Store().Advance("kasse0", "a", 1, 1);
        Store().Advance("kasse0", "b", 1, 1);
        File.WriteAllText(StoreFile, "{ kaputt");
        var s = Store();
        Assert.Contains("a", s.All("kasse0"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StoreFile)!, "*kaputt*"));
        s.Advance("kasse0", "c", 1, 1);
        Assert.Contains("c", Store().All("kasse0"));
    }

    [Fact]
    public void Failed_write_changes_nothing()
    {
        var s = Store();
        s.Advance("kasse0", "a", 1, 1);
        using (File.Open(StoreFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) return;
            Assert.ThrowsAny<IOException>(() => s.Advance("kasse0", "b", 1, 1));
        }
        Assert.DoesNotContain("b", s.All("kasse0"));
        Assert.NotNull(s.Advance("kasse0", "b", 1, 1).After.UnlockedAt);
    }

    [Fact]
    public void Bad_game_ids_are_refused() =>
        Assert.Throws<ArgumentException>(() => Store().All("../../weg"));

    [Fact]
    public async Task Service_fires_once_even_when_reported_in_parallel()
    {
        var dir = Game(Good);
        var service = new AchievementService(Store(), _ => dir);
        var seen = new List<AchievementChange>();
        service.Changed += c => { lock (seen) seen.Add(c); };
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.Report("kasse0", "erste", null))));
        Assert.True(Assert.Single(seen).Unlocked);
        Assert.True(service.Report("kasse0", "gibtsnicht", null));
        service.Report("kasse0", "hundert", 20);
        Assert.Equal(0, seen[^1].Milestone);
        service.Report("kasse0", "hundert", 60);
        Assert.Equal(50, seen[^1].Milestone);
        service.Report("kasse0", "hundert", 100);
        Assert.True(seen[^1].Unlocked);
        Assert.Equal(0, seen[^1].Milestone);
        Assert.Equal(4, seen.Count);
    }

    // ---- Verbindung zum Spiel ----

    [Fact]
    public async Task Reports_are_confirmed_after_saving()
    {
        using var server = new PresenceServer();
        var save = false;
        var seen = new List<(string, string, int?)>();
        server.Achievement = (g, id, v) => { seen.Add((g, id, v)); return save; };
        var status = 0;
        server.Changed += (_, _) => status++;
        server.Start();
        var env = server.Register("kasse0");
        using var client = new TcpClient();
        await client.ConnectAsync(IPEndPoint.Parse(env[PresenceServer.AddressVariable]));
        var w = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var r = new StreamReader(client.GetStream());
        await w.WriteLineAsync($$"""{"token":"{{env[PresenceServer.TokenVariable]}}","details":"x"}""");
        await w.WriteLineAsync("""{"unlock":"a","n":1}""");
        await Until(() => seen.Count == 1);
        save = true;
        await w.WriteLineAsync("""{"unlock":"a","n":1}""");
        Assert.Equal("""{"ack":1}""", await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        server.Unregister("kasse0"); // Spiel beendet, Meldung noch unterwegs
        await w.WriteLineAsync("""{"progress":"b","value":7,"n":2}""");
        Assert.Equal("""{"ack":2}""", await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(("kasse0", "b", 7), seen[^1]);
        await w.WriteLineAsync("""{"unlock":"../x","n":3}""");
        Assert.Equal("""{"ack":3}""", await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, seen.Count);
        Assert.Equal(1, status); // Erfolge ändern den Discord-Status nicht
    }

    [Fact]
    public async Task Floods_are_limited_without_losing_the_connection()
    {
        using var server = new PresenceServer();
        var count = 0;
        server.Achievement = (_, _, _) => { Interlocked.Increment(ref count); return true; };
        server.Start();
        var env = server.Register("kasse0");
        using var client = new TcpClient();
        await client.ConnectAsync(IPEndPoint.Parse(env[PresenceServer.AddressVariable]));
        var w = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var done = false;
        server.Changed += (_, p) => done |= p?.Details == "ende";
        await w.WriteLineAsync($$"""{"token":"{{env[PresenceServer.TokenVariable]}}"}""");
        await w.WriteAsync(string.Concat(Enumerable.Repeat("{\"unlock\":\"a\"}\n", 1000)));
        await w.WriteLineAsync("""{"details":"ende"}""");
        await Until(() => done);
        Assert.Equal(300, count);
    }

    // ---- Verbindung zur Schicht ----

    [Fact]
    public async Task Layer_receives_packets_and_reports_frames()
    {
        using var server = new OverlayServer();
        OverlayStats? stats = null;
        var states = new List<bool>();
        server.Stats += (_, s) => stats = s;
        server.Connected += (_, on) => { lock (states) states.Add(on); };
        server.Start();
        var env = server.Register("kasse0");
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPEndPoint.Parse(env[OverlayServer.AddressVariable]));
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(
                $$"""{"token":"{{env[OverlayServer.TokenVariable]}}"}""" + "\n{\"fps\":59.5,\"w\":1920,\"h\":1080}\nMüll\n[]\n"));
            await Until(() => stats is not null);
            Assert.Equal(new OverlayStats(59.5, 1920, 1080), stats);
            await server.SendImageAsync("kasse0", 7, 2, 1, [1, 2, 3, 4, 5, 6, 7, 8]);
            await server.SendQuadsAsync("kasse0", [new OverlayQuad(7, OverlayAnchor.BottomRight, 24, 24, 400, 96) { Hold = 5 }]);
            var got = new byte[5 + 20 + 5 + OverlayQuad.Bytes];
            await stream.ReadExactlyAsync(got).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(20, BinaryPrimitives.ReadInt32LittleEndian(got));
            Assert.Equal(1, got[4]);
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], got[17..25]);
            Assert.Equal((OverlayQuad.Bytes, 2), (BinaryPrimitives.ReadInt32LittleEndian(got.AsSpan(25)), got[29]));
            Assert.Equal((7, 3), (BinaryPrimitives.ReadInt32LittleEndian(got.AsSpan(30)), got[34]));
            Assert.Equal(5f, BinaryPrimitives.ReadSingleLittleEndian(got.AsSpan(30 + 5 + 6 * 4)));
        }
        await Until(() => { lock (states) return states.Count == 2; });
        Assert.Equal([true, false], states);
        Assert.False(server.IsConnected("kasse0"));
        await server.ClearAsync("kasse0"); // ohne Verbindung: nichts
    }

    [Fact]
    public async Task Layer_needs_the_right_token()
    {
        using var server = new OverlayServer();
        server.Start();
        var env = server.Register("kasse0");
        using var client = new TcpClient();
        await client.ConnectAsync(IPEndPoint.Parse(env[OverlayServer.AddressVariable]));
        await client.GetStream().WriteAsync("{\"token\":\"falsch\"}\n"u8.ToArray());
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(server.IsConnected("kasse0"));
    }
}
