using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Proton;

public enum ProtonSource { Managed, Steam }

/// <summary>Eine nutzbare Proton-Version (Ordner mit dem Skript <c>proton</c>).</summary>
public sealed record ProtonInstall(string Name, string Path, ProtonSource Source)
{
    public string Display => Source == ProtonSource.Steam ? $"{Name} (Steam)" : Name;
}

/// <summary>Ein herunterladbares GE-Proton-Release.</summary>
public sealed record GeProtonRelease(string Tag, ReleaseAsset Archive, ReleaseAsset? Checksum);

public sealed class ProtonException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Verwaltet GE-Proton unter <c>proton\</c> (Download von GitHub mit Prüfsumme, atomar entpackt) und findet
/// zusätzlich Proton-Versionen einer vorhandenen Steam-Installation.
/// </summary>
public sealed partial class ProtonManager
{
    public const string GeRepo = "GloriousEggroll/proton-ge-custom";

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly ReleaseService _releases;
    private readonly string _home;

    /// <param name="home">Home-Verzeichnis (Tests übergeben ein Temp-Verzeichnis).</param>
    public ProtonManager(HttpClient http, LauncherPaths paths, ReleaseService releases, string? home = null)
    {
        _http = http;
        _paths = paths;
        _releases = releases;
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Vom Launcher verwaltete Versionen, neueste zuerst.</summary>
    public IReadOnlyList<ProtonInstall> ListManaged()
    {
        if (!Directory.Exists(_paths.ProtonDir)) return [];
        return Directory.EnumerateDirectories(_paths.ProtonDir)
            .Where(d => !System.IO.Path.GetFileName(d).StartsWith('_') && File.Exists(System.IO.Path.Combine(d, "proton")))
            .Select(d => new ProtonInstall(System.IO.Path.GetFileName(d), d, ProtonSource.Managed))
            .OrderByDescending(p => GeSortKey(p.Name))
            .ToList();
    }

    /// <summary>Proton-Versionen aus Steam (nativ und Flatpak): Valve-Proton und eigene Kompatibilitätswerkzeuge.</summary>
    public IReadOnlyList<ProtonInstall> ListSteam()
    {
        var roots = new[]
        {
            System.IO.Path.Combine(_home, ".steam", "steam"),
            System.IO.Path.Combine(_home, ".local", "share", "Steam"),
            System.IO.Path.Combine(_home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
        };
        var found = new Dictionary<string, ProtonInstall>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var dir in SafeDirs(System.IO.Path.Combine(root, "compatibilitytools.d")).Concat(
                         SafeDirs(System.IO.Path.Combine(root, "steamapps", "common")).Where(d => System.IO.Path.GetFileName(d).StartsWith("Proton", StringComparison.Ordinal))))
            {
                if (!File.Exists(System.IO.Path.Combine(dir, "proton"))) continue;
                var real = ResolveLink(dir);
                found.TryAdd(real, new ProtonInstall(System.IO.Path.GetFileName(dir), dir, ProtonSource.Steam));
            }
        }
        return found.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<ProtonInstall> ListAll() => [.. ListManaged(), .. ListSteam()];

    /// <summary>Gewünschte Version (Name) oder, wenn leer/nicht vorhanden, die neueste verwaltete GE-Proton.</summary>
    public ProtonInstall? Resolve(string? preferred)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = ListAll().FirstOrDefault(p => string.Equals(p.Name, preferred, StringComparison.Ordinal));
            if (match is not null) return match;
        }
        return ListManaged().FirstOrDefault();
    }

    /// <summary>Neueste GE-Proton-Version auf GitHub (stabile Releases, x86_64).</summary>
    public async Task<GeProtonRelease?> GetLatestAsync(CancellationToken ct = default)
    {
        var list = await _releases.GetReleasesAsync(GeRepo, ct).ConfigureAwait(false);
        foreach (var r in list.Releases.Where(r => !r.Draft && !r.Prerelease).OrderByDescending(r => GeSortKey(r.TagName)))
        {
            var archive = r.Assets.FirstOrDefault(a => ArchiveRegex().IsMatch(a.Name));
            if (archive is null) continue;
            var sum = r.Assets.FirstOrDefault(a => a.Name == System.IO.Path.ChangeExtension(System.IO.Path.ChangeExtension(archive.Name, null), null) + ".sha512sum");
            return new GeProtonRelease(r.TagName, archive, sum);
        }
        return null;
    }

    /// <summary>Gibt es eine neuere GE-Proton als die neueste installierte?</summary>
    public async Task<GeProtonRelease?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(ct).ConfigureAwait(false);
        var newest = ListManaged().FirstOrDefault();
        return latest is not null && (newest is null || GeSortKey(latest.Tag).CompareTo(GeSortKey(newest.Name)) > 0) ? latest : null;
    }

    /// <summary>Sorgt dafür, dass mindestens eine verwaltete GE-Proton vorhanden ist (lädt sonst die neueste).</summary>
    public async Task<ProtonInstall> EnsureAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (ListManaged().FirstOrDefault() is { } existing) return existing;
        var latest = await GetLatestAsync(ct).ConfigureAwait(false)
                     ?? throw new ProtonException("Keine GE-Proton-Version auf GitHub gefunden.");
        return await InstallAsync(latest, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Lädt, prüft (sha256 aus der API oder .sha512sum) und entpackt eine GE-Proton-Version.</summary>
    public async Task<ProtonInstall> InstallAsync(GeProtonRelease release, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (!TagRegex().IsMatch(release.Tag)) throw new ProtonException($"Unerwarteter Versionsname '{release.Tag}'.");
        var target = System.IO.Path.Combine(_paths.ProtonDir, release.Tag);
        if (File.Exists(System.IO.Path.Combine(target, "proton")))
            return new ProtonInstall(release.Tag, target, ProtonSource.Managed);

        var staging = System.IO.Path.Combine(_paths.ProtonDir, "_staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archivePath = System.IO.Path.Combine(staging, release.Archive.Name);
            string? expected = null;
            HashAlgorithmName algorithm;
            if (release.Archive.Sha256 is { } sha256)
            {
                expected = sha256;
                algorithm = HashAlgorithmName.SHA256;
            }
            else
            {
                algorithm = HashAlgorithmName.SHA512;
                if (release.Checksum is not null)
                {
                    var text = await _http.GetStringAsync(release.Checksum.DownloadUrl, ct).ConfigureAwait(false);
                    expected = ParseChecksumFile(text, release.Archive.Name);
                }
            }
            if (expected is null) throw new ProtonException($"Für {release.Tag} gibt es keine Prüfsumme – Download abgelehnt.");

            var actual = await HttpDownload.ToFileAsync(_http, release.Archive.DownloadUrl, archivePath, algorithm,
                release.Archive.Size, progress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new ProtonException($"Prüfsumme von {release.Archive.Name} stimmt nicht – Download verworfen.");

            var content = System.IO.Path.Combine(staging, "content");
            await ExtractTarGzAsync(archivePath, content, ct).ConfigureAwait(false);
            // Archive enthalten einen Oberordner (GE-Proton11-12/) – den nehmen wir als Ziel.
            var root = File.Exists(System.IO.Path.Combine(content, "proton"))
                ? content
                : Directory.EnumerateDirectories(content).FirstOrDefault(d => File.Exists(System.IO.Path.Combine(d, "proton")))
                  ?? throw new ProtonException("Im Archiv fehlt das Skript 'proton'.");
            Directory.CreateDirectory(_paths.ProtonDir);
            Directory.Move(root, target);
            return new ProtonInstall(release.Tag, target, ProtonSource.Managed);
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(staging);
        }
    }

    /// <summary>Entfernt eine verwaltete Version (nicht Steam-Versionen).</summary>
    public void Remove(ProtonInstall install)
    {
        if (install.Source != ProtonSource.Managed) throw new ProtonException("Steam-Versionen werden von Steam verwaltet.");
        FileSystemUtil.TryDeleteDirectory(install.Path);
    }

    /// <summary>Format von GE: <c>&lt;hash&gt;  &lt;dateiname&gt;</c> (eine oder mehrere Zeilen).</summary>
    public static string? ParseChecksumFile(string text, string fileName)
    {
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !HexRegex().IsMatch(parts[0])) continue;
            if (parts.Length == 1 || parts[1].TrimStart('*').EndsWith(fileName, StringComparison.Ordinal))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    internal static async Task ExtractTarGzAsync(string archive, string target, CancellationToken ct)
    {
        await using var file = File.OpenRead(archive);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        await ExtractTarAsync(gz, target, ct).ConfigureAwait(false);
    }

    /// <summary>Entpackt ein tar-Archiv sicher (keine Pfade/Links außerhalb des Ziels).</summary>
    internal static async Task ExtractTarAsync(Stream tar, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        var full = System.IO.Path.GetFullPath(target);
        using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimStart('/').TrimEnd('/');
            if (name.Length == 0 || name == ".") continue;
            var dest = FileSystemUtil.SafeCombine(full, name)
                       ?? throw new ProtonException($"Unzulässiger Pfad im Archiv: '{entry.Name}'.");
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(dest);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    await entry.ExtractToFileAsync(dest, overwrite: true, ct).ConfigureAwait(false);
                    break;
                case TarEntryType.SymbolicLink when !OperatingSystem.IsWindows():
                    // Links nur innerhalb des Archivs zulassen.
                    var linkTarget = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dest)!, entry.LinkName));
                    if (!linkTarget.StartsWith(full, StringComparison.Ordinal)) break;
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    if (File.Exists(dest) || Directory.Exists(dest)) File.Delete(dest);
                    File.CreateSymbolicLink(dest, entry.LinkName);
                    break;
                case TarEntryType.HardLink:
                    // Hardlink = Kopie einer schon entpackten Datei im Archiv.
                    var source = FileSystemUtil.SafeCombine(full, entry.LinkName.TrimStart('.', '/'));
                    if (source is not null && File.Exists(source))
                    {
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                        File.Copy(source, dest, overwrite: true);
                    }
                    break;
                // Geräte, FIFOs o. Ä. kommen in Proton-Archiven nicht vor -> überspringen.
            }
        }
    }

    /// <summary>Sortierschlüssel für <c>GE-Proton11-12</c> → (11, 12); Unbekanntes landet hinten.</summary>
    public static (int Major, int Minor) GeSortKey(string name)
    {
        var m = TagRegex().Match(name);
        return m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (-1, -1);
    }

    private static IEnumerable<string> SafeDirs(string path)
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).ToList() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string ResolveLink(string dir)
    {
        try { return new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? System.IO.Path.GetFullPath(dir); }
        catch (IOException) { return System.IO.Path.GetFullPath(dir); }
    }

    [GeneratedRegex(@"^GE-Proton(\d+)-(\d+)$")] private static partial Regex TagRegex();
    [GeneratedRegex(@"^GE-Proton\d+-\d+-x86_64\.tar\.gz$|^GE-Proton\d+-\d+\.tar\.gz$")] private static partial Regex ArchiveRegex();
    [GeneratedRegex(@"^[0-9a-fA-F]{64,128}$")] private static partial Regex HexRegex();
}
