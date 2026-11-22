using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Launch;
using MettLauncher.Core.Proton;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class ProtonTests
{
    private const string GeApi = "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases";
    private const string UmuApi = "https://api.github.com/repos/Open-Wine-Components/umu-launcher/releases";

    private static byte[] Tar(IDictionary<string, string> files, bool gzip)
    {
        using var raw = new MemoryStream();
        using (var tar = new TarWriter(raw, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                };
                tar.WriteEntry(entry);
            }
        }
        if (!gzip) return raw.ToArray();
        using var outMs = new MemoryStream();
        using (var gz = new GZipStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(raw.ToArray());
        return outMs.ToArray();
    }

    private static string ReleaseJson(string repoUrl, params (string tag, bool pre, (string name, byte[] data, bool digest)[] assets)[] rels) =>
        JsonSerializer.Serialize(rels.Select(r => new
        {
            tag_name = r.tag,
            name = r.tag,
            draft = false,
            prerelease = r.pre,
            published_at = "2026-11-01T12:00:00Z",
            assets = r.assets.Select(a => new
            {
                name = a.name,
                size = a.data.Length,
                browser_download_url = $"{repoUrl}/releases/download/{r.tag}/{a.name}",
                digest = a.digest ? "sha256:" + TestData.Sha256(a.data) : null,
            }),
        }));

    private sealed class Env : IDisposable
    {
        public readonly TempDir Tmp = new();
        public readonly FakeHttpHandler Http = new();
        public string Home => Tmp.Sub("home");
        public ReleaseService Releases => new(new HttpClient(Http), Tmp.Paths);
        public ProtonManager Proton => new(new HttpClient(Http), Tmp.Paths, Releases, Home);
        public void Dispose() => Tmp.Dispose();

        public string FakeProton(string root, string name)
        {
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(dir, "files", "bin"));
            File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh");
            return dir;
        }
    }

    [Theory]
    [InlineData("abc123ff  GE-Proton11-12-x86_64.tar.gz\n", "abc123ff")]
    public void Checksum_file_requires_hex_of_real_length(string text, string _)
    {
        // zu kurz für sha512 -> wird ignoriert
        Assert.Null(ProtonManager.ParseChecksumFile(text, "GE-Proton11-12-x86_64.tar.gz"));
        var full = new string('A', 128);
        Assert.Equal(full.ToLowerInvariant(), ProtonManager.ParseChecksumFile($"{full}  GE-Proton11-12-x86_64.tar.gz\n", "GE-Proton11-12-x86_64.tar.gz"));
    }

    [Fact]
    public void Sorts_managed_versions_newest_first_and_resolves_preference()
    {
        using var env = new Env();
        env.FakeProton(env.Tmp.Paths.ProtonDir, "GE-Proton9-27");
        env.FakeProton(env.Tmp.Paths.ProtonDir, "GE-Proton11-2");
        env.FakeProton(env.Tmp.Paths.ProtonDir, "GE-Proton11-12");
        Directory.CreateDirectory(Path.Combine(env.Tmp.Paths.ProtonDir, "_staging", "x"));

        var managed = env.Proton.ListManaged();
        Assert.Equal(["GE-Proton11-12", "GE-Proton11-2", "GE-Proton9-27"], managed.Select(p => p.Name));
        Assert.Equal("GE-Proton9-27", env.Proton.Resolve("GE-Proton9-27")!.Name);
        Assert.Equal("GE-Proton11-12", env.Proton.Resolve("gibt-es-nicht")!.Name);
        Assert.Equal("GE-Proton11-12", env.Proton.Resolve(null)!.Name);
    }

    [Fact]
    public void Finds_steam_proton_versions()
    {
        using var env = new Env();
        env.FakeProton(Path.Combine(env.Home, ".steam", "steam", "compatibilitytools.d"), "GE-Proton10-4");
        env.FakeProton(Path.Combine(env.Home, ".local", "share", "Steam", "steamapps", "common"), "Proton 9.0");
        Directory.CreateDirectory(Path.Combine(env.Home, ".local", "share", "Steam", "steamapps", "common", "Half-Life"));

        var steam = env.Proton.ListSteam();
        Assert.Equal(["GE-Proton10-4", "Proton 9.0"], steam.Select(p => p.Name));
        Assert.All(steam, p => Assert.Equal(ProtonSource.Steam, p.Source));
        Assert.Equal("GE-Proton10-4", env.Proton.Resolve("GE-Proton10-4")!.Name);
    }

    [Fact]
    public async Task Installs_latest_stable_ge_proton_with_checksum()
    {
        using var env = new Env();
        var good = Tar(new Dictionary<string, string> { ["GE-Proton11-12/proton"] = "#!/bin/sh", ["GE-Proton11-12/files/bin/wineserver"] = "x" }, gzip: true);
        var old = Tar(new Dictionary<string, string> { ["GE-Proton11-11/proton"] = "#!/bin/sh" }, gzip: true);
        var repo = "https://github.com/GloriousEggroll/proton-ge-custom";
        env.Http.OnJson(GeApi, ReleaseJson(repo,
            ("GE-Proton11-13", true, [("GE-Proton11-13-x86_64.tar.gz", good, true)]),   // Vorabversion -> ignorieren
            ("GE-Proton11-12", false, [("GE-Proton11-12-x86_64.tar.gz", good, true), ("GE-Proton11-12-aarch64.tar.gz", old, true)]),
            ("GE-Proton11-11", false, [("GE-Proton11-11-x86_64.tar.gz", old, true)])));
        env.Http.OnBytes($"{repo}/releases/download/GE-Proton11-12/GE-Proton11-12-x86_64.tar.gz", good);

        var installed = await env.Proton.EnsureAsync();

        Assert.Equal("GE-Proton11-12", installed.Name);
        Assert.True(File.Exists(Path.Combine(installed.Path, "proton")));
        Assert.True(File.Exists(Path.Combine(installed.Path, "files", "bin", "wineserver")));
        Assert.False(Directory.EnumerateFileSystemEntries(Path.Combine(env.Tmp.Paths.ProtonDir, "_staging")).Any());
        Assert.Null(await env.Proton.CheckForUpdateAsync());
    }

    [Fact]
    public async Task Rejects_ge_proton_with_wrong_checksum_or_without_checksum()
    {
        using var env = new Env();
        var data = Tar(new Dictionary<string, string> { ["GE-Proton11-12/proton"] = "x" }, gzip: true);
        var repo = "https://github.com/GloriousEggroll/proton-ge-custom";
        var sum = Encoding.UTF8.GetBytes(new string('0', 128) + "  GE-Proton11-12-x86_64.tar.gz\n");
        env.Http.OnJson(GeApi, ReleaseJson(repo, ("GE-Proton11-12", false,
            [("GE-Proton11-12-x86_64.tar.gz", data, false), ("GE-Proton11-12-x86_64.sha512sum", sum, false)])));
        env.Http.OnBytes($"{repo}/releases/download/GE-Proton11-12/GE-Proton11-12-x86_64.tar.gz", data);
        env.Http.OnBytes($"{repo}/releases/download/GE-Proton11-12/GE-Proton11-12-x86_64.sha512sum", sum);

        var ex = await Assert.ThrowsAsync<ProtonException>(() => env.Proton.EnsureAsync());
        Assert.Contains("Prüfsumme", ex.Message);
        Assert.Empty(env.Proton.ListManaged());
    }

    [Fact]
    public async Task Installs_umu_zipapp_only_with_python()
    {
        using var env = new Env();
        var tar = Tar(new Dictionary<string, string> { ["umu/umu-run"] = "#!/usr/bin/env python3" }, gzip: false);
        var repo = "https://github.com/Open-Wine-Components/umu-launcher";
        env.Http.OnJson(UmuApi, ReleaseJson(repo, ("1.4.4", false, [("umu-launcher-1.4.4-zipapp.tar", tar, true)])));
        env.Http.OnBytes($"{repo}/releases/download/1.4.4/umu-launcher-1.4.4-zipapp.tar", tar);

        var noPython = new UmuManager(new HttpClient(env.Http), env.Tmp.Paths, env.Releases, _ => null);
        await Assert.ThrowsAsync<ProtonException>(() => noPython.EnsureAsync());

        var umu = new UmuManager(new HttpClient(env.Http), env.Tmp.Paths, env.Releases, p => p == "python3" ? "/usr/bin/python3" : null);
        var installed = await umu.EnsureAsync();
        Assert.Equal(UmuSource.Managed, installed.Source);
        Assert.Equal("1.4.4", installed.Version);
        Assert.EndsWith("umu-run", installed.Path);
        Assert.Equal(installed.Path, umu.Resolve()!.Path);

        var system = new UmuManager(new HttpClient(env.Http), env.Tmp.Paths, env.Releases, p => p == "umu-run" ? "/usr/bin/umu-run" : null);
        Assert.Equal(UmuSource.System, system.Resolve()!.Source);
    }

    [Fact]
    public void Proton_runner_builds_umu_command_with_prefix_and_multiplayer_env()
    {
        using var env = new Env();
        var protonDir = env.FakeProton(env.Tmp.Paths.ProtonDir, "GE-Proton11-12");
        var library = new LibraryStore(env.Tmp.Paths);
        var umu = new UmuManager(new HttpClient(env.Http), env.Tmp.Paths, env.Releases, p => p == "umu-run" ? "/usr/bin/umu-run" : null);
        var runner = new ProtonRunner(env.Tmp.Paths, env.Proton, umu, library, () => true);
        var game = TestData.Kasse0(b => b.Args = ["--windowed"]);

        var psi = runner.CreateStartInfo(game, "/games/kasse0", "/games/kasse0/KASSE0.exe",
            new Dictionary<string, string> { ["METT_NET_ADDR"] = "127.0.0.1:5000" });

        Assert.Equal("/usr/bin/umu-run", psi.FileName);
        Assert.Equal(["/games/kasse0/KASSE0.exe", "--windowed"], psi.ArgumentList);
        Assert.Equal("/games/kasse0", psi.WorkingDirectory);
        Assert.Equal(runner.PrefixFor("kasse0"), psi.Environment["WINEPREFIX"]);
        Assert.True(Directory.Exists(runner.PrefixFor("kasse0")));
        Assert.Equal(protonDir, psi.Environment["PROTONPATH"]);
        Assert.Equal("umu-default", psi.Environment["GAMEID"]);
        Assert.Equal("none", psi.Environment["STORE"]);
        Assert.Equal("1", psi.Environment["PROTON_LOG"]);
        Assert.Equal("127.0.0.1:5000", psi.Environment["METT_NET_ADDR"]);
        Assert.False(runner.DetectsExternalInstances);
    }

    [Fact]
    public void Proton_runner_uses_per_game_version_and_reports_missing_runtime()
    {
        using var env = new Env();
        var library = new LibraryStore(env.Tmp.Paths);
        var umu = new UmuManager(new HttpClient(env.Http), env.Tmp.Paths, env.Releases, _ => null);
        var runner = new ProtonRunner(env.Tmp.Paths, env.Proton, umu, library);
        var game = TestData.Kasse0();
        Assert.Throws<RunnerNotReadyException>(() => runner.CreateStartInfo(game, "/g", "/g/KASSE0.exe", null));

        env.FakeProton(env.Tmp.Paths.ProtonDir, "GE-Proton11-12");
        var steamDir = env.FakeProton(Path.Combine(env.Home, ".steam", "steam", "compatibilitytools.d"), "GE-Proton10-4");
        library.UpdateGame("kasse0", s => s.ProtonVersion = "GE-Proton10-4");
        var withUmu = new ProtonRunner(env.Tmp.Paths, env.Proton,
            new UmuManager(new HttpClient(env.Http), env.Tmp.Paths, env.Releases, p => p == "umu-run" ? "/usr/bin/umu-run" : null), library);
        var psi = withUmu.CreateStartInfo(game with { UmuId = "umu-12345" }, "/g", "/g/KASSE0.exe", null);
        Assert.Equal(steamDir, psi.Environment["PROTONPATH"]);
        Assert.Equal("umu-12345", psi.Environment["GAMEID"]);
        Assert.False(psi.Environment.ContainsKey("PROTON_LOG"));
    }

    [Fact]
    public void Catalog_reads_linux_fields_with_proton_default()
    {
        var catalog = CatalogService.Parse("""
            { "schema": 1, "games": [
              { "id": "a", "name": "A", "repo": "o/a", "assetPattern": "x", "exe": "a.exe" },
              { "id": "b", "name": "B", "repo": "o/b", "assetPattern": "x", "exe": "b", "linux": "native", "umuId": "umu-1" } ] }
            """);
        Assert.False(catalog.Games[0].RunsNativeOnLinux);
        Assert.Null(catalog.Games[0].UmuId);
        Assert.True(catalog.Games[1].RunsNativeOnLinux);
        Assert.Equal("umu-1", catalog.Games[1].UmuId);
    }
}
