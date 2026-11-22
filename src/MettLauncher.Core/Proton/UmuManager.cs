using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Proton;

public enum UmuSource { System, Managed }

/// <summary>Ausführbares <c>umu-run</c> (startet Proton außerhalb von Steam im Steam-Runtime-Container).</summary>
public sealed record UmuInstall(string Path, UmuSource Source, string? Version);

/// <summary>
/// Findet ein installiertes <c>umu-run</c> (Paketmanager) oder lädt die zipapp von
/// <c>Open-Wine-Components/umu-launcher</c> nach <c>tools\umu</c>. Die zipapp braucht <c>python3</c>.
/// </summary>
public sealed partial class UmuManager
{
    public const string Repo = "Open-Wine-Components/umu-launcher";

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly ReleaseService _releases;
    private readonly Func<string, string?> _which;

    /// <param name="which">Sucht ein Programm im PATH (Tests ersetzen das).</param>
    public UmuManager(HttpClient http, LauncherPaths paths, ReleaseService releases, Func<string, string?>? which = null)
    {
        _http = http;
        _paths = paths;
        _releases = releases;
        _which = which ?? Which;
    }

    public string ManagedDir => System.IO.Path.Combine(_paths.ToolsDir, "umu");

    /// <summary>System-umu bevorzugt, sonst die vom Launcher geladene zipapp.</summary>
    public UmuInstall? Resolve()
    {
        if (_which("umu-run") is { } system) return new UmuInstall(system, UmuSource.System, null);
        var managed = FindUmuRun(ManagedDir);
        if (managed is null) return null;
        var versionFile = System.IO.Path.Combine(ManagedDir, "VERSION");
        return new UmuInstall(managed, UmuSource.Managed, File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null);
    }

    public bool HasPython => _which("python3") is not null;

    public async Task<UmuInstall> EnsureAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (Resolve() is { } existing) return existing;
        if (!HasPython)
            throw new ProtonException("Für Proton wird python3 benötigt (umu-launcher). Bitte python3 über die Paketverwaltung installieren.");

        var list = await _releases.GetReleasesAsync(Repo, ct).ConfigureAwait(false);
        var release = list.Releases.Where(r => !r.Draft && !r.Prerelease).OrderByDescending(r => r.Version).FirstOrDefault(r =>
                          r.Assets.Any(a => ZipappRegex().IsMatch(a.Name)))
                      ?? throw new ProtonException("Keine umu-launcher-Version auf GitHub gefunden.");
        var asset = release.Assets.First(a => ZipappRegex().IsMatch(a.Name));
        var expected = asset.Sha256 ?? throw new ProtonException("umu-launcher hat keine Prüfsumme – Download abgelehnt.");

        var staging = System.IO.Path.Combine(_paths.ToolsDir, "_staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var tarPath = System.IO.Path.Combine(staging, asset.Name);
            var actual = await HttpDownload.ToFileAsync(_http, asset.DownloadUrl, tarPath, HashAlgorithmName.SHA256, asset.Size, progress, ct)
                .ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new ProtonException("Prüfsumme von umu-launcher stimmt nicht – Download verworfen.");

            var content = System.IO.Path.Combine(staging, "content");
            await using (var tar = File.OpenRead(tarPath))
                await ProtonManager.ExtractTarAsync(tar, content, ct).ConfigureAwait(false);
            if (FindUmuRun(content) is null) throw new ProtonException("Im umu-Archiv fehlt 'umu-run'.");
            await File.WriteAllTextAsync(System.IO.Path.Combine(content, "VERSION"), release.TagName, ct).ConfigureAwait(false);

            FileSystemUtil.TryDeleteDirectory(ManagedDir);
            Directory.CreateDirectory(_paths.ToolsDir);
            Directory.Move(content, ManagedDir);
            var path = FindUmuRun(ManagedDir)!;
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            return new UmuInstall(path, UmuSource.Managed, release.TagName);
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(staging);
        }
    }

    private static string? FindUmuRun(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "umu-run", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>Wie <c>which</c>: erstes ausführbares Programm dieses Namens im PATH.</summary>
    public static string? Which(string program)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(dir, program);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"^umu-launcher-[\d.]+-zipapp\.tar$")] private static partial Regex ZipappRegex();
}
