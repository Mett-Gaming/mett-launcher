using System.Net;
using MettLauncher.Core.Install;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class InstallServiceTests
{
    private static byte[] KasseZip(string version) => TestData.Zip(new Dictionary<string, string>
    {
        ["KASSE0/"] = "",
        ["KASSE0/KASSE0.exe"] = $"exe {version}",
        ["KASSE0/LIESMICH.txt"] = $"readme {version}",
        ["KASSE0/data/level.bin"] = $"level {version}",
    });

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir Tmp = new();
        public readonly FakeHttpHandler Http = new();
        public readonly LibraryStore Library;
        public readonly InstallService Service;
        public bool Running;

        public Fixture()
        {
            Library = new LibraryStore(Tmp.Paths);
            Service = new InstallService(new HttpClient(Http), Library, () => LibraryDir, _ => Running)
            {
                ProgressGranularityBytes = 1,
            };
        }

        public string LibraryDir => Tmp.Sub("games");
        public string GameDir => Path.Combine(LibraryDir, "kasse0");

        public Core.GitHub.ResolvedRelease Serve(string tag, byte[] zip, string? digest = "auto")
        {
            var r = TestData.Release(tag, zip, digest);
            Http.OnBytes(r.Asset.DownloadUrl.AbsoluteUri, zip);
            return r;
        }

        public void Dispose() => Tmp.Dispose();
    }

    [Fact]
    public async Task Installs_strips_top_folder_verifies_hash_and_writes_state()
    {
        using var f = new Fixture();
        var release = f.Serve("v0.17.0", KasseZip("17"));
        var reports = new List<InstallProgress>();

        var result = await f.Service.InstallAsync(TestData.Kasse0(), release, new SyncProgress<InstallProgress>(reports.Add));

        Assert.True(result.HashVerified);
        Assert.Equal(f.GameDir, result.InstallPath);
        Assert.Equal("exe 17", File.ReadAllText(Path.Combine(f.GameDir, "KASSE0.exe")));
        Assert.True(File.Exists(Path.Combine(f.GameDir, "data", "level.bin")));
        Assert.False(Directory.Exists(Path.Combine(f.GameDir, "KASSE0")));
        Assert.False(Directory.Exists(Path.Combine(f.LibraryDir, "_staging")));

        var state = f.Library.GetGame("kasse0")!;
        Assert.Equal("0.17.0", state.InstalledVersion);
        Assert.Equal("v0.17.0", state.InstalledTag);
        Assert.Equal(f.GameDir, state.InstallPath);
        Assert.Equal(release.Asset.Sha256, state.AssetSha256);

        Assert.Contains(reports, p => p.Phase == InstallPhase.Downloading && p.Fraction == 1.0);
        Assert.Equal(InstallPhase.Completed, reports[^1].Phase);
        Assert.Equal(
            [InstallPhase.Downloading, InstallPhase.Verifying, InstallPhase.Extracting, InstallPhase.Finalizing, InstallPhase.Completed],
            reports.Select(r => r.Phase).Distinct());
    }

    [Fact]
    public async Task Without_strip_top_folder_keeps_structure()
    {
        using var f = new Fixture();
        var zip = TestData.Zip(new Dictionary<string, string> { ["KASSE0.exe"] = "x", ["sub/a.txt"] = "a" });
        var release = f.Serve("v1.0.0", zip);
        await f.Service.InstallAsync(TestData.Kasse0(b => b.StripTopFolder = false), release);
        Assert.True(File.Exists(Path.Combine(f.GameDir, "KASSE0.exe")));
        Assert.True(File.Exists(Path.Combine(f.GameDir, "sub", "a.txt")));
    }

    [Fact]
    public async Task Strip_top_folder_is_skipped_when_entries_have_no_common_folder()
    {
        using var f = new Fixture();
        var zip = TestData.Zip(new Dictionary<string, string> { ["KASSE0.exe"] = "x", ["data/a.txt"] = "a" });
        await f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v1.0.0", zip));
        Assert.True(File.Exists(Path.Combine(f.GameDir, "data", "a.txt")));
    }

    [Fact]
    public async Task Update_replaces_files_and_keeps_preserved_paths()
    {
        using var f = new Fixture();
        var game = TestData.Kasse0(b => b.Preserve = ["saves", "config.ini"]);
        await f.Service.InstallAsync(game, f.Serve("v0.17.0", KasseZip("17")));

        Directory.CreateDirectory(Path.Combine(f.GameDir, "saves", "slot1"));
        File.WriteAllText(Path.Combine(f.GameDir, "saves", "slot1", "save.dat"), "mein spielstand");
        File.WriteAllText(Path.Combine(f.GameDir, "config.ini"), "volume=3");
        File.WriteAllText(Path.Combine(f.GameDir, "junk.tmp"), "weg damit");

        await f.Service.InstallAsync(game, f.Serve("v0.18.0", KasseZip("18")));

        Assert.Equal("exe 18", File.ReadAllText(Path.Combine(f.GameDir, "KASSE0.exe")));
        Assert.Equal("mein spielstand", File.ReadAllText(Path.Combine(f.GameDir, "saves", "slot1", "save.dat")));
        Assert.Equal("volume=3", File.ReadAllText(Path.Combine(f.GameDir, "config.ini")));
        Assert.False(File.Exists(Path.Combine(f.GameDir, "junk.tmp")));
        Assert.Equal("0.18.0", f.Library.GetGame("kasse0")!.InstalledVersion);
        Assert.Single(Directory.GetDirectories(f.LibraryDir)); // nur kasse0, keine Reste
    }

    [Fact]
    public async Task Hash_mismatch_aborts_and_keeps_old_install()
    {
        using var f = new Fixture();
        await f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v0.17.0", KasseZip("17")));

        var bad = f.Serve("v0.18.0", KasseZip("18"), digest: "sha256:" + new string('0', 64));
        var ex = await Assert.ThrowsAsync<InstallException>(() => f.Service.InstallAsync(TestData.Kasse0(), bad));

        Assert.Contains("Prüfsumme", ex.Message);
        Assert.Equal("exe 17", File.ReadAllText(Path.Combine(f.GameDir, "KASSE0.exe")));
        Assert.Equal("0.17.0", f.Library.GetGame("kasse0")!.InstalledVersion);
        Assert.False(Directory.Exists(Path.Combine(f.LibraryDir, "_staging")));
    }

    [Fact]
    public async Task Missing_digest_installs_without_verification()
    {
        using var f = new Fixture();
        var result = await f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v0.17.0", KasseZip("17"), digest: null));
        Assert.False(result.HashVerified);
        Assert.NotNull(result.Sha256);
    }

    [Fact]
    public async Task Cancellation_during_download_keeps_old_install_and_cleans_staging()
    {
        using var f = new Fixture();
        await f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v0.17.0", KasseZip("17")));

        var zip = KasseZip("18");
        var release = TestData.Release("v0.18.0", zip);
        using var cts = new CancellationTokenSource();
        f.Http.On(release.Asset.DownloadUrl.AbsoluteUri, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SlowStream(zip, afterBytes: 10, onReached: cts.Cancel)),
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.InstallAsync(TestData.Kasse0(), release, ct: cts.Token));

        Assert.Equal("exe 17", File.ReadAllText(Path.Combine(f.GameDir, "KASSE0.exe")));
        Assert.False(Directory.Exists(Path.Combine(f.LibraryDir, "_staging")));
        Assert.False(f.Service.IsBusy("kasse0"));
    }

    [Fact]
    public async Task Http_error_throws_install_exception()
    {
        using var f = new Fixture();
        var release = TestData.Release("v0.17.0", KasseZip("17")); // nicht registriert -> 404
        await Assert.ThrowsAsync<InstallException>(() => f.Service.InstallAsync(TestData.Kasse0(), release));
        Assert.False(Directory.Exists(f.GameDir));
    }

    [Fact]
    public async Task Missing_exe_in_package_fails()
    {
        using var f = new Fixture();
        var zip = TestData.Zip(new Dictionary<string, string> { ["KASSE0/other.exe"] = "x" });
        var ex = await Assert.ThrowsAsync<InstallException>(() => f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v1.0.0", zip)));
        Assert.Contains("KASSE0.exe", ex.Message);
        Assert.False(Directory.Exists(f.GameDir));
    }

    [Fact]
    public async Task Zip_slip_entries_are_rejected()
    {
        using var f = new Fixture();
        var zip = TestData.Zip(new Dictionary<string, string> { ["KASSE0.exe"] = "x", ["../../evil.txt"] = "boom" });
        await Assert.ThrowsAsync<InstallException>(() => f.Service.InstallAsync(TestData.Kasse0(b => b.StripTopFolder = false), f.Serve("v1.0.0", zip)));
        Assert.False(File.Exists(f.Tmp.Sub("evil.txt")));
        Assert.False(Directory.Exists(f.GameDir));
    }

    [Fact]
    public async Task Refuses_install_while_game_is_running()
    {
        using var f = new Fixture { Running = true };
        await Assert.ThrowsAsync<InstallException>(() => f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v1.0.0", KasseZip("1"))));
    }

    [Fact]
    public async Task Refuses_parallel_install_of_same_game()
    {
        using var f = new Fixture();
        var zip = KasseZip("18");
        var release = TestData.Release("v0.18.0", zip);
        var gate = new TaskCompletionSource();
        f.Http.On(release.Asset.DownloadUrl.AbsoluteUri, async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        });

        var first = f.Service.InstallAsync(TestData.Kasse0(), release);
        Assert.True(f.Service.IsBusy("kasse0"));
        await Assert.ThrowsAsync<InstallException>(() => f.Service.InstallAsync(TestData.Kasse0(), release));
        gate.SetResult();
        await first;
    }

    [Fact]
    public async Task Uninstall_deletes_folder_and_clears_install_but_keeps_playtime()
    {
        using var f = new Fixture();
        await f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v0.17.0", KasseZip("17")));
        File.SetAttributes(Path.Combine(f.GameDir, "LIESMICH.txt"), FileAttributes.ReadOnly);
        f.Library.UpdateGame("kasse0", s => { s.TotalPlaySeconds = 3600; s.PinnedTag = "v0.17.0"; });

        await f.Service.UninstallAsync(TestData.Kasse0());

        Assert.False(Directory.Exists(f.GameDir));
        Assert.Empty(Directory.GetDirectories(f.LibraryDir));
        var s = f.Library.GetGame("kasse0")!;
        Assert.False(s.IsInstalled);
        Assert.Null(s.PinnedTag);
        Assert.Equal(3600, s.TotalPlaySeconds);
    }

    [Fact]
    public async Task Reinstall_uses_existing_install_path_from_state()
    {
        using var f = new Fixture();
        var custom = f.Tmp.Sub("elsewhere", "KASSE");
        f.Library.UpdateGame("kasse0", s => { s.InstallPath = custom; s.InstalledVersion = "0.1.0"; });
        Directory.CreateDirectory(custom);

        await f.Service.InstallAsync(TestData.Kasse0(), f.Serve("v0.17.0", KasseZip("17")));

        Assert.True(File.Exists(Path.Combine(custom, "KASSE0.exe")));
        Assert.False(Directory.Exists(f.GameDir));
    }

    /// <summary>IProgress ohne SynchronizationContext-Umweg (Progress&lt;T&gt; meldet asynchron).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Liefert Daten in kleinen Häppchen und ruft nach <c>afterBytes</c> einen Callback.</summary>
    private sealed class SlowStream(byte[] data, int afterBytes, Action onReached) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return ReadCore(buffer.Span);
        }
        private int ReadCore(Span<byte> buffer)
        {
            var n = Math.Min(Math.Min(5, buffer.Length), data.Length - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            if (_pos >= afterBytes) onReached();
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
