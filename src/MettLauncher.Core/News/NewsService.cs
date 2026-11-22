using System.Text.Json;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.News;

/// <summary>Inhalt von <c>news.json</c> im Repo <c>Mett-Gaming/news</c>.</summary>
public sealed record NewsFeed
{
    public int Schema { get; init; }
    public IReadOnlyList<NewsItem> Items { get; init; } = [];
}

public sealed record NewsItem
{
    public required string Id { get; init; }
    public required DateTimeOffset Date { get; init; }
    public required string Title { get; init; }
    /// <summary>Spiel-ID aus dem Katalog oder <c>null</c> für allgemeine Meldungen.</summary>
    public string? Game { get; init; }
    /// <summary>Art der Meldung, z. B. <c>patchnotes</c>, <c>news</c>, <c>event</c>.</summary>
    public string Kind { get; init; } = "news";
    public string? Excerpt { get; init; }
    /// <summary>Langtext als Markdown.</summary>
    public string? Body { get; init; }
    /// <summary>Bild relativ zum Repo-Wurzelverzeichnis (z. B. <c>images/x.png</c>).</summary>
    public string? Image { get; init; }
    public string? Url { get; init; }
}

public sealed record NewsResult(NewsFeed Feed, bool FromCache, DateTimeOffset FetchedAt, Exception? NetworkError = null);

/// <summary>
/// Lädt <c>news.json</c> über raw.githubusercontent.com (kein API-Limit), legt die letzte gültige
/// Fassung im Cache ab und liefert offline diesen Stand. Fehlt beides, gibt es einen leeren Feed –
/// News sind nie ein Grund, den Launcher zu blockieren.
/// </summary>
public sealed class NewsService
{
    public const int SupportedSchema = 1;
    public static readonly Uri DefaultBaseUrl = new("https://raw.githubusercontent.com/Mett-Gaming/news/main/");

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly TimeProvider _time;

    public NewsService(HttpClient http, LauncherPaths paths, Uri? baseUrl = null, TimeProvider? time = null)
    {
        _http = http;
        _paths = paths;
        _time = time ?? TimeProvider.System;
        var b = baseUrl ?? DefaultBaseUrl;
        BaseUrl = b.AbsoluteUri.EndsWith('/') ? b : new Uri(b.AbsoluteUri + "/");
    }

    public Uri BaseUrl { get; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public Uri? ResolveAssetUrl(string? relative) =>
        string.IsNullOrWhiteSpace(relative) ? null
        : Uri.TryCreate(relative, UriKind.Absolute, out var abs) && abs.Scheme == Uri.UriSchemeHttps ? abs
        : FileSystemUtil.IsSafeRelativePath(relative) ? new Uri(BaseUrl, relative.Replace('\\', '/'))
        : null;

    public async Task<NewsResult> LoadAsync(CancellationToken ct = default)
    {
        Exception error;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http.GetAsync(new Uri(BaseUrl, "news.json"), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var feed = Parse(json);
            var now = _time.GetUtcNow();
            AtomicFile.WriteAllText(_paths.NewsCacheFile, JsonSerializer.Serialize(new CacheFile(now, json), Json.Options));
            return new NewsResult(feed, FromCache: false, now);
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
        return cached is not null
            ? cached with { NetworkError = error }
            : new NewsResult(new NewsFeed { Schema = SupportedSchema }, FromCache: true, DateTimeOffset.MinValue, error);
    }

    public NewsResult? TryLoadCache()
    {
        try
        {
            if (!File.Exists(_paths.NewsCacheFile)) return null;
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_paths.NewsCacheFile), Json.Options);
            if (cache?.Json is null) return null;
            return new NewsResult(Parse(cache.Json), FromCache: true, cache.FetchedAt);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parst und sortiert (neueste zuerst). Doppelte IDs und leere Titel sind ungültig.</summary>
    public static NewsFeed Parse(string json)
    {
        var feed = JsonSerializer.Deserialize<NewsFeed>(json, Json.Options)
                   ?? throw new InvalidDataException("news.json ist leer.");
        if (feed.Schema != SupportedSchema)
            throw new InvalidDataException($"news.json hat Schema {feed.Schema}, unterstützt wird {SupportedSchema}.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in feed.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
                throw new InvalidDataException($"News-ID '{item.Id}' fehlt oder ist doppelt.");
            if (string.IsNullOrWhiteSpace(item.Title))
                throw new InvalidDataException($"News '{item.Id}' hat keinen Titel.");
        }
        return feed with { Items = feed.Items.OrderByDescending(i => i.Date).ToList() };
    }

    /// <summary>Meldungen für ein Spiel (für die Aktivitätsspalte der Spielseite).</summary>
    public static IEnumerable<NewsItem> ForGame(NewsFeed feed, string gameId) =>
        feed.Items.Where(i => string.Equals(i.Game, gameId, StringComparison.OrdinalIgnoreCase));

    private sealed record CacheFile(DateTimeOffset FetchedAt, string Json);
}
