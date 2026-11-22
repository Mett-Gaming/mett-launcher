using System.Security.Cryptography;

namespace MettLauncher.Core.IO;

/// <summary>Fortschritt eines Downloads (Bytes empfangen / gesamt, falls bekannt).</summary>
public readonly record struct DownloadProgress(long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;
}

public static class HttpDownload
{
    /// <summary>Streamt <paramref name="url"/> nach <paramref name="target"/> und liefert den Hash (Hex, klein).</summary>
    public static async Task<string> ToFileAsync(HttpClient http, Uri url, string target, HashAlgorithmName hashAlgorithm,
        long? expectedSize = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Download fehlgeschlagen: HTTP {(int)response.StatusCode} ({url.Segments[^1]}).");

        long? total = response.Content.Headers.ContentLength ?? (expectedSize > 0 ? expectedSize : null);
        using var hash = IncrementalHash.CreateHash(hashAlgorithm);
        long received = 0, lastReported = -1;
        progress?.Report(new DownloadProgress(0, total));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (progress is not null && received - lastReported >= 512 * 1024)
                {
                    lastReported = received;
                    progress.Report(new DownloadProgress(received, total));
                }
            }
        }
        progress?.Report(new DownloadProgress(received, total ?? received));
        if (expectedSize > 0 && received != expectedSize)
            throw new IOException($"Download unvollständig ({received} von {expectedSize} Bytes).");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
