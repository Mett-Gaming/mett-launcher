using System.Text.Json;
using System.Text.RegularExpressions;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Catalog;

public sealed record CatalogResult(GameCatalog Catalog, bool FromCache, DateTimeOffset FetchedAt, Exception? NetworkError = null);

public sealed class CatalogUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Lädt <c>games.json</c> aus dem Repo <c>Mett-Gaming/launcher-catalog</c> über raw.githubusercontent.com,
/// speichert die letzte gültige Fassung auf Platte und fällt offline darauf zurück.
/// </summary>
public sealed partial class CatalogService
{
    public const int SupportedSchema = 1;
    public static readonly Uri DefaultBaseUrl = new("https://raw.githubusercontent.com/Mett-Gaming/launcher-catalog/main/");

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly TimeProvider _time;

    public CatalogService(HttpClient http, LauncherPaths paths, Uri? baseUrl = null, TimeProvider? time = null)
    {
        _http = http;
        _paths = paths;
        _time = time ?? TimeProvider.System;
        var b = baseUrl ?? DefaultBaseUrl;
        BaseUrl = b.AbsoluteUri.EndsWith('/') ? b : new Uri(b.AbsoluteUri + "/");
    }

    public Uri BaseUrl { get; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Absolute URL für Cover/Hero-Bilder aus dem Katalog.</summary>
    public Uri? ResolveAssetUrl(string? relative) =>
        string.IsNullOrWhiteSpace(relative) ? null : new Uri(BaseUrl, relative);

    public async Task<CatalogResult> LoadAsync(CancellationToken ct = default)
    {
        Exception error;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http.GetAsync(new Uri(BaseUrl, "games.json"), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var catalog = Parse(json);
            var now = _time.GetUtcNow();
            AtomicFile.WriteAllText(_paths.CatalogCacheFile, JsonSerializer.Serialize(new CacheFile(now, json), Json.Options));
            return new CatalogResult(catalog, FromCache: false, now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        {
            error = ex;
        }

        var cached = TryLoadCache();
        if (cached is not null)
            return cached with { NetworkError = error };
        throw new CatalogUnavailableException("Spielekatalog konnte nicht geladen werden und es gibt keinen Offline-Stand.", error);
    }

    /// <summary>Liest nur den Plattencache (z. B. für einen schnellen Start vor dem Netzabruf).</summary>
    public CatalogResult? TryLoadCache()
    {
        try
        {
            if (!File.Exists(_paths.CatalogCacheFile)) return null;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_paths.CatalogCacheFile), Json.Options);
            if (cache?.Json is null) return null;
            return new CatalogResult(Parse(cache.Json), FromCache: true, cache.FetchedAt);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static GameCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<GameCatalog>(json, Json.Options)
                      ?? throw new InvalidDataException("games.json ist leer.");
        if (catalog.Schema != SupportedSchema)
            throw new InvalidDataException($"games.json hat Schema {catalog.Schema}, unterstützt wird {SupportedSchema}.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in catalog.Games)
        {
            if (!IdRegex().IsMatch(g.Id)) throw new InvalidDataException($"Ungültige Spiel-ID '{g.Id}'.");
            if (!ids.Add(g.Id)) throw new InvalidDataException($"Spiel-ID '{g.Id}' ist doppelt.");
            if (!GitHub.ReleaseService.IsValidRepo(g.Repo)) throw new InvalidDataException($"Ungültiges Repo '{g.Repo}' bei '{g.Id}'.");
            try { _ = new Regex(g.AssetPattern); }
            catch (ArgumentException ex) { throw new InvalidDataException($"Ungültiges assetPattern bei '{g.Id}'.", ex); }
            if (!FileSystemUtil.IsSafeRelativePath(g.Exe))
                throw new InvalidDataException($"Ungültiger exe-Pfad bei '{g.Id}'.");
            foreach (var p in g.Preserve)
                if (!FileSystemUtil.IsSafeRelativePath(p))
                    throw new InvalidDataException($"Ungültiger preserve-Pfad '{p}' bei '{g.Id}'.");
        }
        return catalog;
    }

    // IDs dienen als Ordnernamen -> nur harmlose Zeichen.
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.IgnoreCase)]
    private static partial Regex IdRegex();

    private sealed record CacheFile(DateTimeOffset FetchedAt, string Json);
}
