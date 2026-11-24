using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.IO;
using MettLauncher.Core.Settings;
using MettLauncher.Core.State;

namespace MettLauncher.Core.Install;

public enum InstallPhase { Downloading, Verifying, Extracting, Finalizing, Completed }

/// <param name="TotalBytes">Gesamtgröße, falls bekannt (sonst <c>null</c>).</param>
public sealed record InstallProgress(InstallPhase Phase, long BytesReceived, long? TotalBytes)
{
    /// <summary>Größe des ganzen Pakets, wenn nur Geändertes geladen wird.</summary>
    public long? FullBytes { get; init; }

    /// <summary>0..1 für den Download, sonst <c>null</c>.</summary>
    public double? Fraction => TotalBytes is > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

public sealed record InstallResult(string InstallPath, SemVersion Version, string Tag, string? Sha256, bool HashVerified);

public sealed class InstallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Download → sha256 → Entpacken in Staging → preserve übernehmen → atomarer Ordnertausch → State. Gibt es eine Dateiliste und
/// eine Installation, wird nur Geändertes geladen (<see cref="DeltaInstaller"/>).
/// Bei Fehler oder Abbruch bleibt eine vorhandene Installation unverändert.
/// </summary>
public sealed class InstallService
{
    private const string StagingDirName = "_staging";

    private readonly HttpClient _http;
    private readonly LibraryStore _library;
    private readonly Func<string> _libraryDir;
    private readonly Func<string, bool> _isGameRunning;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="libraryDir">Liefert den aktuellen Bibliotheksordner (z. B. <c>() =&gt; settings.LibraryDir</c>).</param>
    /// <param name="isGameRunning">Optional: verhindert Install/Deinstall während das Spiel läuft.</param>
    public InstallService(HttpClient http, LibraryStore library, Func<string> libraryDir,
        Func<string, bool>? isGameRunning = null, TimeProvider? time = null)
    {
        _http = http;
        _library = library;
        _libraryDir = libraryDir;
        _isGameRunning = isGameRunning ?? (_ => false);
        _time = time ?? TimeProvider.System;
    }

    public InstallService(HttpClient http, LibraryStore library, SettingsStore settings,
        Func<string, bool>? isGameRunning = null, TimeProvider? time = null)
        : this(http, library, () => settings.LibraryDir, isGameRunning, time) { }

    /// <summary>Wie oft (in Bytes) Download-Fortschritt höchstens gemeldet wird.</summary>
    public long ProgressGranularityBytes { get; init; } = 256 * 1024;

    public bool IsBusy(string gameId) => _busy.ContainsKey(gameId);

    /// <summary>Installordner eines Spiels: vorhandener Pfad aus dem State, sonst <c>{Bibliothek}\{id}</c>.</summary>
    public string GetInstallDir(GameEntry game) =>
        _library.GetGame(game.Id)?.InstallPath ?? Path.Combine(_libraryDir(), game.Id);

    public async Task<InstallResult> InstallAsync(GameEntry game, ResolvedRelease release,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        if (_isGameRunning(game.Id)) throw new InstallException($"{game.Name} läuft gerade und kann nicht aktualisiert werden.");
        if (!_busy.TryAdd(game.Id, 0)) throw new InstallException($"Für {game.Name} läuft bereits eine Installation.");
        try
        {
            return await InstallCoreAsync(game, release, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _busy.TryRemove(game.Id, out _);
        }
    }

    private async Task<InstallResult> InstallCoreAsync(GameEntry game, ResolvedRelease release,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var installDir = Path.GetFullPath(GetInstallDir(game));
        var parent = Path.GetDirectoryName(installDir) ?? throw new InstallException($"Ungültiger Installordner '{installDir}'.");
        // Staging liegt neben dem Ziel -> gleiches Laufwerk -> Directory.Move ist ein Rename.
        var staging = Path.Combine(parent, StagingDirName, $"{game.Id}-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(staging, "download.zip");
        var content = Path.Combine(staging, "content");
        Directory.CreateDirectory(staging);
        try
        {
            var expected = release.Asset.Sha256;
            var delta = game.StripTopFolder && await new DeltaInstaller(_http, ProgressGranularityBytes)
                .TryAsync(release, installDir, staging, content, progress, ct).ConfigureAwait(false);
            string? sha;
            if (delta)
            {
                sha = expected; // jede Datei einzeln geprüft
            }
            else
            {
                FileSystemUtil.TryDeleteDirectory(content); // Reste eines abgebrochenen Zusammensetzens
                sha = await DownloadAsync(release.Asset, zipPath, progress, ct).ConfigureAwait(false);

                progress?.Report(new InstallProgress(InstallPhase.Verifying, 0, null));
                if (expected is not null && !string.Equals(expected, sha, StringComparison.OrdinalIgnoreCase))
                    throw new InstallException($"Prüfsumme stimmt nicht (erwartet {expected}, erhalten {sha}). Download verworfen.");

                ct.ThrowIfCancellationRequested();
                progress?.Report(new InstallProgress(InstallPhase.Extracting, 0, null));
                await Task.Run(() => Extract(zipPath, content, game.StripTopFolder, ct), ct).ConfigureAwait(false);
            }

            var exe = FileSystemUtil.SafeCombine(content, game.Exe);
            if (exe is null || !File.Exists(exe))
                throw new InstallException($"Im Paket fehlt '{game.Exe}'.");

            ct.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress(InstallPhase.Finalizing, 0, null));
            CopyPreserved(game, installDir, content);
            SwapIn(content, installDir, staging);

            _library.UpdateGame(game.Id, s =>
            {
                s.InstalledVersion = release.Version.ToString();
                s.InstalledTag = release.Tag;
                s.InstallPath = installDir;
                s.InstalledAt = _time.GetUtcNow();
                s.AssetName = release.Asset.Name;
                s.AssetSha256 = sha;
            });

            progress?.Report(new InstallProgress(InstallPhase.Completed, 0, null));
            return new InstallResult(installDir, release.Version, release.Tag, sha, HashVerified: expected is not null);
        }
        finally
        {
            FileSystemUtil.TryDeleteDirectory(staging);
            TryDeleteIfEmpty(Path.Combine(parent, StagingDirName));
        }
    }

    /// <summary>Streamt das Asset auf Platte und berechnet dabei den sha256 (Hex, klein).</summary>
    private async Task<string> DownloadAsync(ReleaseAsset asset, string target, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InstallException($"Download von {asset.Name} fehlgeschlagen: HTTP {(int)response.StatusCode}.");

        long? total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0, lastReported = -1;
        progress?.Report(new InstallProgress(InstallPhase.Downloading, 0, total));

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (progress is not null && received - lastReported >= ProgressGranularityBytes)
                {
                    lastReported = received;
                    progress.Report(new InstallProgress(InstallPhase.Downloading, received, total));
                }
            }
            await file.FlushAsync(ct).ConfigureAwait(false);
        }
        progress?.Report(new InstallProgress(InstallPhase.Downloading, received, total ?? received));

        if (asset.Size > 0 && received != asset.Size)
            throw new InstallException($"Download unvollständig ({received} von {asset.Size} Bytes).");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Entpackt <paramref name="zipPath"/> nach <paramref name="target"/>. Mit <paramref name="stripTopFolder"/> wird ein
    /// gemeinsamer oberster Ordner (z. B. <c>KASSE0/</c>) entfernt – sofern alle Einträge darin liegen.
    /// </summary>
    internal static void Extract(string zipPath, string target, bool stripTopFolder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries
            .Select(e => (entry: e, path: e.FullName.Replace('\\', '/').TrimStart('/')))
            .Where(x => x.path.Length > 0)
            .ToList();

        string? prefix = null;
        if (stripTopFolder && entries.Count > 0)
        {
            var first = entries[0].path;
            var slash = first.IndexOf('/');
            if (slash > 0)
            {
                var candidate = first[..(slash + 1)];
                if (entries.All(x => x.path.StartsWith(candidate, StringComparison.Ordinal)))
                    prefix = candidate;
            }
        }

        foreach (var (entry, rawPath) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = prefix is null ? rawPath : rawPath[prefix.Length..];
            if (path.Length == 0) continue;

            var dest = FileSystemUtil.SafeCombine(target, path)
                       ?? throw new InstallException($"Unzulässiger Pfad im Archiv: '{entry.FullName}'.");
            if (path.EndsWith('/'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    private static void CopyPreserved(GameEntry game, string installDir, string content)
    {
        if (game.Preserve.Count == 0 || !Directory.Exists(installDir)) return;
        foreach (var rel in game.Preserve)
        {
            var from = FileSystemUtil.SafeCombine(installDir, rel);
            var to = FileSystemUtil.SafeCombine(content, rel);
            if (from is null || to is null) throw new InstallException($"Ungültiger preserve-Pfad '{rel}'.");
            if (File.Exists(from))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: true);
            }
            else if (Directory.Exists(from))
            {
                FileSystemUtil.CopyDirectory(from, to);
            }
        }
    }

    /// <summary>Tauscht den fertigen Ordner per Rename ein; bei Fehler wird die alte Installation zurückgeholt.</summary>
    private static void SwapIn(string content, string installDir, string staging)
    {
        if (!Directory.Exists(installDir))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(installDir)!);
            Directory.Move(content, installDir);
            return;
        }

        var backup = Path.Combine(staging, "previous");
        try
        {
            Directory.Move(installDir, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException("Der Installordner ist gesperrt (läuft das Spiel noch?).", ex);
        }

        try
        {
            Directory.Move(content, installDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.Move(backup, installDir);
            throw new InstallException("Neue Version konnte nicht eingesetzt werden; alte Version wiederhergestellt.", ex);
        }
        // backup liegt im Staging und wird mit ihm gelöscht.
    }

    /// <summary>Löscht den Installordner und entfernt die Installation aus dem State (Spielzeit bleibt).</summary>
    public Task UninstallAsync(GameEntry game, CancellationToken ct = default) => UninstallAsync(game.Id, ct);

    public async Task UninstallAsync(string gameId, CancellationToken ct = default)
    {
        if (_isGameRunning(gameId)) throw new InstallException("Das Spiel läuft gerade.");
        if (!_busy.TryAdd(gameId, 0)) throw new InstallException("Für dieses Spiel läuft gerade eine Installation.");
        try
        {
            var state = _library.GetGame(gameId);
            if (state?.InstallPath is { } dir && Directory.Exists(dir))
            {
                // Erst umbenennen (schlägt sofort fehl, wenn gesperrt), dann in Ruhe löschen.
                var trash = dir.TrimEnd(Path.DirectorySeparatorChar) + $".deleting-{Guid.NewGuid():N}";
                try
                {
                    Directory.Move(dir, trash);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InstallException("Der Installordner ist gesperrt (läuft das Spiel noch?).", ex);
                }
                await Task.Run(() => FileSystemUtil.DeleteDirectory(trash), ct).ConfigureAwait(false);
            }
            _library.UpdateGame(gameId, s => { s.ClearInstall(); s.PinnedTag = null; });
        }
        finally
        {
            _busy.TryRemove(gameId, out _);
        }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
