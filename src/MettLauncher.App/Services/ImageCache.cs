using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using MettLauncher.Core;

namespace MettLauncher.App.Services;

/// <summary>
/// Lädt Cover/Hero/News-Bilder einmal herunter und legt sie unter <c>cache\images</c> ab.
/// Offline wird die Platte genutzt; fehlt ein Bild, liefert die Methode <c>null</c> (UI zeigt dann den Platzhalter).
/// </summary>
public sealed class ImageCache(HttpClient http, LauncherPaths paths)
{
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new();

    public Task<Bitmap?> GetAsync(Uri? url) =>
        url is null ? Task.FromResult<Bitmap?>(null) : _inFlight.GetOrAdd(url.AbsoluteUri, _ => LoadAsync(url));

    private async Task<Bitmap?> LoadAsync(Uri url)
    {
        var file = Path.Combine(paths.ImageCacheDir, Hash(url.AbsoluteUri) + Path.GetExtension(url.AbsolutePath));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var bytes = await http.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false);
            Directory.CreateDirectory(paths.ImageCacheDir);
            var tmp = file + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            // offline oder Bild fehlt -> Plattenstand verwenden
        }

        try
        {
            if (!File.Exists(file)) return null;
            await using var stream = File.OpenRead(file);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24];
}
