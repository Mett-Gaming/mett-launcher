using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Install;

/// <summary>
/// Baut die neue Version aus der vorhandenen und lädt nur Fehlendes aus dem Zip der Veröffentlichung nach (HTTP-Teilabruf).
/// Jede Datei wird gegen die Dateiliste geprüft. Klappt etwas nicht, lädt der Aufrufer das ganze Zip.
/// </summary>
internal sealed class DeltaInstaller(HttpClient http, long granularity)
{
    /// <summary>Ab diesem Anteil am Zip lohnt sich das Zusammensetzen nicht.</summary>
    private const double Worthwhile = 0.7;
    private const int MaxListing = 16 * 1024 * 1024, MaxRanges = 400;

    /// <returns><c>true</c> = <paramref name="content"/> enthält die vollständige, geprüfte neue Version.</returns>
    public async Task<bool> TryAsync(ResolvedRelease release, string installDir, string staging, string content,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(installDir) || FileListing.AssetFor(release) is not { Sha256: { } expected } asset || asset.Size > MaxListing)
                return false;
            var json = await http.GetByteArrayAsync(asset.DownloadUrl, ct).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(json)) != expected || FileListing.Parse(json, release.Asset) is not { } listing) return false;
            var plan = await Task.Run(() => DeltaPlanner.Plan(listing, installDir, ct), ct).ConfigureAwait(false);
            if (plan.Ranges.Count > MaxRanges || plan.DownloadSize > listing.ZipSize * Worthwhile) return false;

            var ranges = new List<(DeltaRange Range, string File)>();
            long done = 0;
            progress?.Report(new InstallProgress(InstallPhase.Downloading, 0, plan.DownloadSize) { FullBytes = listing.ZipSize });
            foreach (var r in plan.Ranges)
            {
                var file = Path.Combine(staging, $"teil-{ranges.Count}.bin");
                var before = done;
                if (!await FetchAsync(release.Asset, r, file, n => progress?.Report(new InstallProgress(InstallPhase.Downloading, before + n, plan.DownloadSize)
                    {
                        FullBytes = listing.ZipSize,
                    }), ct).ConfigureAwait(false)) return false;
                done += r.Length;
                ranges.Add((r, file));
            }
            progress?.Report(new InstallProgress(InstallPhase.Downloading, done, plan.DownloadSize) { FullBytes = listing.ZipSize });
            progress?.Report(new InstallProgress(InstallPhase.Verifying, 0, null));
            return await Task.Run(() => plan.Files.All(f => Build(f, ranges, content, ct)), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
    }

    private async Task<bool> FetchAsync(ReleaseAsset zip, DeltaRange range, string target, Action<long> report, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, zip.DownloadUrl);
        var last = range.Offset + range.Length - 1;
        request.Headers.Range = new RangeHeaderValue(range.Offset, last);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Nur genau das Gewünschte; ein ganzes Zip (200) kommt lieber über den normalen Weg.
        if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange is not { } got
            || got.From != range.Offset || got.To != last || (got.Length is { } total && total != zip.Size)) return false;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long received = 0, reported = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > range.Length) return false;
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            if (received - reported < granularity) continue;
            reported = received;
            report(received);
        }
        return received == range.Length;
    }

    /// <summary>Setzt eine Datei zusammen und prüft sie.</summary>
    private static bool Build(DeltaFile f, List<(DeltaRange Range, string File)> ranges, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FileSystemUtil.SafeCombine(content, f.File.Path) is not { } dest) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            foreach (var p in f.Pieces)
            {
                using var source = Open(p, ranges);
                using Stream input = f.File.Packed && p.LocalFile is null ? new DeflateStream(source, CompressionMode.Decompress) : source;
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    written += read;
                    if (written > f.File.Size) return false;
                    output.Write(buffer, 0, read);
                    hash.AppendData(buffer, 0, read);
                }
            }
        }
        return written == f.File.Size && Convert.ToHexStringLower(hash.GetHashAndReset()) == f.File.Sha256;
    }

    private static Stream Open(DeltaPiece p, List<(DeltaRange Range, string File)> ranges)
    {
        if (p.LocalFile is not null) return new Section(p.LocalFile, p.Source, p.Length);
        var (range, file) = ranges.First(r => p.Source >= r.Range.Offset && p.Source + p.Length <= r.Range.Offset + r.Range.Length);
        return new Section(file, p.Source - range.Offset, p.Length);
    }

    /// <summary>Ausschnitt einer Datei.</summary>
    private sealed class Section : Stream
    {
        private readonly FileStream _file;
        private long _left;

        public Section(string path, long offset, long length)
        {
            _file = File.OpenRead(path);
            _file.Seek(offset, SeekOrigin.Begin);
            _left = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _file.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
