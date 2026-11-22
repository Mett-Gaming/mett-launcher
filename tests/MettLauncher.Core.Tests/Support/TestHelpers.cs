using System.IO.Compression;
using System.Security.Cryptography;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;

namespace MettLauncher.Core.Tests.Support;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mett-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public LauncherPaths Paths => new(System.IO.Path.Combine(Path, "appdata"));
    public string Sub(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>TimeProvider mit manuell gesteuerter Uhr.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

public static class TestData
{
    public static GameEntry Kasse0(Action<GameEntryBuilder>? configure = null)
    {
        var b = new GameEntryBuilder();
        configure?.Invoke(b);
        return new GameEntry
        {
            Id = "kasse0",
            Name = "KASSE 0",
            Repo = "Mett-Gaming/kasse0",
            AssetPattern = @"^KASSE0-v.*\.zip$",
            StripTopFolder = b.StripTopFolder,
            Exe = "KASSE0.exe",
            Args = b.Args,
            Preserve = b.Preserve,
            IncludePrereleases = b.IncludePrereleases,
        };
    }

    public sealed class GameEntryBuilder
    {
        public bool StripTopFolder { get; set; } = true;
        public string[] Args { get; set; } = [];
        public string[] Preserve { get; set; } = [];
        public bool IncludePrereleases { get; set; }
    }

    /// <summary>Baut ein Zip im Speicher; Schlüssel = Pfad im Archiv, Wert = Inhalt.</summary>
    public static byte[] Zip(IDictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                var entry = zip.CreateEntry(path);
                if (path.EndsWith('/')) continue;
                using var w = new StreamWriter(entry.Open());
                w.Write(text);
            }
        }
        return ms.ToArray();
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static ResolvedRelease Release(string tag, byte[] zip, string? digest = "auto", string? assetName = null)
    {
        var version = SemVersion.Parse(tag);
        var asset = new ReleaseAsset
        {
            Name = assetName ?? $"KASSE0-{tag}.zip",
            Size = zip.Length,
            DownloadUrl = new Uri($"https://github.com/Mett-Gaming/kasse0/releases/download/{tag}/KASSE0-{tag}.zip"),
            Digest = digest == "auto" ? "sha256:" + Sha256(zip) : digest,
        };
        var release = new ReleaseInfo { TagName = tag, Version = version, Assets = [asset] };
        return new ResolvedRelease(release, asset, version);
    }

    public static string ReleasesJson(params (string tag, bool prerelease, bool draft, string[] assets)[] releases)
    {
        var items = releases.Select(r => new
        {
            tag_name = r.tag,
            name = $"KASSE 0 {r.tag}",
            draft = r.draft,
            prerelease = r.prerelease,
            published_at = "2026-11-01T12:00:00Z",
            body = $"Notes {r.tag}",
            html_url = $"https://github.com/Mett-Gaming/kasse0/releases/tag/{r.tag}",
            assets = r.assets.Select(a => new
            {
                name = a,
                size = 1234,
                browser_download_url = $"https://github.com/Mett-Gaming/kasse0/releases/download/{r.tag}/{a}",
                digest = "sha256:" + new string('a', 64),
            }),
        });
        return System.Text.Json.JsonSerializer.Serialize(items);
    }
}
