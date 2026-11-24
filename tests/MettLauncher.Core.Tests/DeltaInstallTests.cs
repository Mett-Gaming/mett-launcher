using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Install;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public sealed class DeltaInstallTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly Server _server = new();
    private readonly LibraryStore _library;
    private readonly InstallService _service;

    public DeltaInstallTests()
    {
        _library = new LibraryStore(_tmp.Paths);
        _service = new InstallService(new HttpClient(_server), _library, () => _tmp.Sub("games")) { ProgressGranularityBytes = 1 };
    }

    public void Dispose() => _tmp.Dispose();

    private string GameDir => _tmp.Sub("games", "kasse0");

    /// <summary>Webserver mit (abschaltbarem) Teilabruf.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public bool Ranges = true, WrongBytes, Break;
        public long Sent;
        public int FullZips;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!Files.TryGetValue(request.RequestUri!.AbsoluteUri, out var data))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (Ranges && request.Headers.Range?.Ranges.Single() is { From: { } from, To: { } to })
            {
                if (Break) throw new HttpRequestException("Leitung weg");
                var part = data[(int)from..(int)(to + 1)];
                if (WrongBytes) part[0] ^= 1;
                Sent += part.Length;
                var content = new ByteArrayContent(part);
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, data.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
            }
            Sent += data.Length;
            if (request.RequestUri.AbsolutePath.EndsWith(".zip")) FullZips++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        }
    }

    // ---- Nachbau von mettpack ----

    private static byte[] Pack(params (string Name, byte[] Data)[] files)
    {
        var sorted = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        var offset = 48 + sorted.Sum(f => 2 + f.Name.Length + 57);
        using var index = new MemoryStream();
        Span<byte> n = stackalloc byte[26];
        foreach (var (name, data) in sorted)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(n, (ushort)name.Length);
            index.Write(n[..2]);
            index.Write(Encoding.ASCII.GetBytes(name));
            BinaryPrimitives.WriteInt64LittleEndian(n, offset);
            BinaryPrimitives.WriteInt64LittleEndian(n[8..], data.Length);
            BinaryPrimitives.WriteInt64LittleEndian(n[16..], data.Length);
            n[24] = 0;
            index.Write(n[..25]);
            index.Write(SHA256.HashData(data));
            offset += data.Length;
        }
        var head = new byte[16];
        "MPK1"u8.CopyTo(head);
        BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), sorted.Count);
        BinaryPrimitives.WriteInt64LittleEndian(head.AsSpan(8), index.Length);
        return [.. head, .. SHA256.HashData(index.ToArray()), .. index.ToArray(), .. sorted.SelectMany(f => f.Data)];
    }

    private static List<object[]> Parts(byte[] pack, int files)
    {
        var start = 48 + (int)BinaryPrimitives.ReadInt64LittleEndian(pack.AsSpan(8));
        var list = new List<object[]> { new object[] { 0L, (long)start, TestData.Sha256(pack[..start]) } };
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, pack);
        list.AddRange(DeltaPlanner.ReadIndex(tmp).Select(p => new object[] { p.Offset, p.Size, p.Sha }));
        File.Delete(tmp);
        Assert.Equal(files + 1, list.Count);
        return list;
    }

    private ResolvedRelease Publish(string tag, Dictionary<string, byte[]> files, bool listing = true, Func<string, string>? tamper = null)
    {
        var all = RealZip(files);
        var rows = new List<object>();
        using (var zip = new ZipArchive(new MemoryStream(all)))
            foreach (var e in zip.Entries)
            {
                var data = files[e.FullName["KASSE0/".Length..]];
                var packed = !e.FullName.EndsWith(".mpak");
                rows.Add(new
                {
                    path = e.FullName["KASSE0/".Length..], size = data.Length, sha256 = TestData.Sha256(data), offset = DataOffset(all, e.FullName),
                    stored = e.CompressedLength, packed, parts = packed ? [] : Parts(data, BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4))),
                });
            }
        var r = TestData.Release(tag, all);
        _server.Files[r.Asset.DownloadUrl.AbsoluteUri] = all;
        if (!listing) return r;
        var json = System.Text.Json.JsonSerializer.Serialize(new { schema = 1, zip = r.Asset.Name, zip_size = all.Length, top = "KASSE0", files = rows });
        json = tamper?.Invoke(json) ?? json;
        var bytes = Encoding.UTF8.GetBytes(json);
        var url = new Uri(r.Asset.DownloadUrl.AbsoluteUri.Replace(".zip", ".dateien.json"));
        _server.Files[url.AbsoluteUri] = bytes;
        var asset = new ReleaseAsset { Name = FileListing.NameFor(r.Asset.Name), Size = bytes.Length, DownloadUrl = url, Digest = "sha256:" + TestData.Sha256(bytes) };
        return r with { Release = r.Release with { Assets = [r.Asset, asset] } };
    }

    /// <summary>Ort des Inhalts hinter dem Dateikopf.</summary>
    private static int DataOffset(byte[] zip, string name)
    {
        var n = Encoding.UTF8.GetBytes(name);
        var at = zip.AsSpan().IndexOf(n) - 30;
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(at)));
        return at + 30 + n.Length + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 28));
    }

    private static byte[] RealZip(Dictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, data) in files)
            {
                using var s = zip.CreateEntry("KASSE0/" + path, path.EndsWith(".mpak") ? CompressionLevel.NoCompression : CompressionLevel.Optimal).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    private static byte[] Noise(int seed, int length)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static Dictionary<string, byte[]> Version(int exe, int model) => new()
    {
        ["KASSE0.exe"] = Noise(exe, 20_000),
        ["LIESMICH.txt"] = "bleibt gleich"u8.ToArray(),
        ["leer.txt"] = [],
        ["daten/modelle.mpak"] = Pack(("a", Noise(1, 400_000)), ("b", Noise(model, 30_000)), ("c", Noise(3, 400_000))),
    };

    private sealed class Reporter(Action<InstallProgress> add) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => add(value);
    }

    private async Task<long> Update(ResolvedRelease r, List<InstallProgress>? reports = null)
    {
        _server.Sent = 0;
        _server.FullZips = 0;
        await _service.InstallAsync(TestData.Kasse0(), r, new Reporter(p => reports?.Add(p)));
        return _server.Sent;
    }

    private void AssertInstalled(Dictionary<string, byte[]> files)
    {
        var found = Directory.GetFiles(GameDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(GameDir, f).Replace('\\', '/'));
        Assert.Equal(files.Keys.Order(), found.Order());
        foreach (var (path, data) in files) Assert.Equal(data, File.ReadAllBytes(Path.Combine(GameDir, path)));
    }

    [Fact]
    public async Task Only_changed_pieces_are_loaded()
    {
        await Update(Publish("v1.0.0", Version(1, 2)));
        Assert.Equal(1, _server.FullZips); // erste Installation
        var v2 = Version(9, 8);
        var release = Publish("v1.1.0", v2);
        var reports = new List<InstallProgress>();
        var sent = await Update(release, reports);
        Assert.Equal(0, _server.FullZips);
        Assert.True(sent < 150_000, $"{sent} Byte"); // exe, Verzeichnis, ein Modell, Dateiliste
        AssertInstalled(v2);
        Assert.Equal("v1.1.0", _library.GetGame("kasse0")!.InstalledTag);
        Assert.Contains(reports, p => p.FullBytes == release.Asset.Size && p.Fraction == 1.0);
        // Rückwärts geht es genauso
        sent = await Update(Publish("v1.0.0", Version(1, 2)));
        Assert.True(sent < 150_000);
        AssertInstalled(Version(1, 2));
    }

    [Fact]
    public async Task Verifying_repairs_only_what_is_broken()
    {
        var r = Publish("v1.0.0", Version(1, 2));
        await Update(r);
        var pack = Path.Combine(GameDir, "daten", "modelle.mpak");
        var bytes = File.ReadAllBytes(pack);
        bytes[^420_000] ^= 1; // mittleres, kleines Modell beschädigt
        File.WriteAllBytes(pack, bytes);
        File.Delete(Path.Combine(GameDir, "LIESMICH.txt"));
        File.WriteAllText(Path.Combine(GameDir, "fremd.txt"), "x");
        Assert.InRange(await Update(r), 30_000, 100_000);
        Assert.Equal(0, _server.FullZips);
        AssertInstalled(Version(1, 2));
        File.Delete(Path.Combine(GameDir, "LIESMICH.txt"));
        Assert.True(await Update(r) < 5_000);
        AssertInstalled(Version(1, 2));
    }

    [Theory]
    [InlineData("ohne Teilabruf")]
    [InlineData("falsche Bytes")]
    [InlineData("Abbruch")]
    [InlineData("Liste verändert")]
    [InlineData("keine Liste")]
    public async Task Any_trouble_falls_back_to_the_whole_zip(string trouble)
    {
        await Update(Publish("v1.0.0", Version(1, 2)));
        var v2 = Version(9, 8);
        var r = Publish("v1.1.0", v2, listing: trouble != "keine Liste");
        _server.Ranges = trouble != "ohne Teilabruf";
        _server.WrongBytes = trouble == "falsche Bytes";
        _server.Break = trouble == "Abbruch";
        if (trouble == "Liste verändert") _server.Files.First(f => f.Key.EndsWith("v1.1.0.dateien.json")).Value[5] ^= 1;
        await Update(r);
        Assert.True(_server.FullZips >= 1);
        AssertInstalled(v2);
        Assert.False(Directory.Exists(_tmp.Sub("games", "_staging")));
    }

    [Theory]
    [InlineData("\"path\":\"LIESMICH.txt\"", "\"path\":\"../weg.txt\"")]
    [InlineData("\"path\":\"LIESMICH.txt\"", "\"path\":\"kasse0.EXE\"")]
    [InlineData("\"schema\":1", "\"schema\":2")]
    [InlineData("\"offset\":", "\"offset\":9")]
    [InlineData("\"zip_size\":", "\"zip_size\":1")]
    public async Task Bad_listings_are_ignored(string from, string to)
    {
        await Update(Publish("v1.0.0", Version(1, 2)));
        var v2 = Version(9, 8);
        var changed = false;
        var r = Publish("v1.1.0", v2, tamper: json => { changed = json.Contains(from); return json.Replace(from, to); });
        Assert.True(changed);
        await Update(r);
        Assert.Equal(1, _server.FullZips);
        AssertInstalled(v2);
        Assert.False(File.Exists(_tmp.Sub("games", "weg.txt")));
    }

    [Fact]
    public async Task Big_changes_take_the_whole_zip_and_cancelling_keeps_the_old_version()
    {
        await Update(Publish("v1.0.0", Version(1, 2)));
        var v2 = new Dictionary<string, byte[]> { ["KASSE0.exe"] = Noise(50, 900_000) };
        await Update(Publish("v2.0.0", v2));
        Assert.Equal(1, _server.FullZips);
        AssertInstalled(v2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.InstallAsync(TestData.Kasse0(), Publish("v3.0.0", Version(4, 5)), null, cts.Token));
        AssertInstalled(v2);
    }

    [Fact]
    public async Task Preserved_files_survive()
    {
        var game = TestData.Kasse0(b => b.Preserve = ["bilder"]);
        await _service.InstallAsync(game, Publish("v1.0.0", Version(1, 2)));
        Directory.CreateDirectory(Path.Combine(GameDir, "bilder"));
        File.WriteAllText(Path.Combine(GameDir, "bilder", "a.txt"), "meins");
        _server.FullZips = 0;
        await _service.InstallAsync(game, Publish("v1.1.0", Version(9, 8)));
        Assert.Equal(0, _server.FullZips);
        Assert.Equal("meins", File.ReadAllText(Path.Combine(GameDir, "bilder", "a.txt")));
    }
}
