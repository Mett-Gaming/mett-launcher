using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.GitHub;

public enum ReleaseSource
{
    /// <summary>Frisch von der API (200).</summary>
    Network,
    /// <summary>API meldete 304 – Cache ist aktuell (zählt nicht gegen das Rate-Limit).</summary>
    NotModified,
    /// <summary>API nicht erreichbar oder Rate-Limit – Cache-Stand verwendet.</summary>
    Offline,
}

public sealed record ReleaseListResult(
    IReadOnlyList<ReleaseInfo> Releases,
    ReleaseSource Source,
    DateTimeOffset FetchedAt,
    Exception? NetworkError = null);

public sealed class ReleasesUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GitHubRateLimitException(DateTimeOffset? resetAt)
    : HttpRequestException($"GitHub-API-Limit erreicht{(resetAt is { } r ? $", wieder frei ab {r.ToLocalTime():HH:mm}" : "")}.")
{
    public DateTimeOffset? ResetAt { get; } = resetAt;
}

/// <summary>
/// Liest <c>GET /repos/{repo}/releases</c> ohne Token. Antworten werden samt ETag unter
/// <c>cache\releases\</c> abgelegt; Folgeabrufe schicken <c>If-None-Match</c>.
/// </summary>
public sealed partial class ReleaseService
{
    public static readonly Uri DefaultApiBase = new("https://api.github.com/");

    private readonly HttpClient _http;
    private readonly LauncherPaths _paths;
    private readonly Uri _apiBase;
    private readonly TimeProvider _time;

    public ReleaseService(HttpClient http, LauncherPaths paths, Uri? apiBase = null, TimeProvider? time = null)
    {
        _http = http;
        _paths = paths;
        _apiBase = apiBase ?? DefaultApiBase;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public static bool IsValidRepo(string? repo) => repo is not null && RepoRegex().IsMatch(repo);

    /// <param name="revalidate">Auch Zwischenspeicher unterwegs (Proxy/CDN) sollen beim Server nachfragen (<c>Cache-Control: no-cache</c>).</param>
    public async Task<ReleaseListResult> GetReleasesAsync(string repo, CancellationToken ct = default, bool revalidate = false)
    {
        if (!IsValidRepo(repo)) throw new ArgumentException($"Ungültiges Repo '{repo}', erwartet owner/name.", nameof(repo));

        var cacheFile = CacheFileFor(repo);
        var cache = ReadCache(cacheFile);
        Exception error;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_apiBase, $"repos/{repo}/releases?per_page=100"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (revalidate) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (cache?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var parsed))
                request.Headers.IfNoneMatch.Add(parsed);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && cache is not null)
            {
                var refreshed = cache with { FetchedAt = _time.GetUtcNow() };
                WriteCache(cacheFile, refreshed);
                return new ReleaseListResult(ParseReleases(cache.Json), ReleaseSource.NotModified, refreshed.FetchedAt);
            }

            if (IsRateLimited(response))
                throw new GitHubRateLimitException(ReadReset(response));

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var releases = ParseReleases(json);
            var entry = new CacheEntry(response.Headers.ETag?.ToString(), _time.GetUtcNow(), json);
            WriteCache(cacheFile, entry);
            return new ReleaseListResult(releases, ReleaseSource.Network, entry.FetchedAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            error = ex;
        }

        if (cache is not null)
        {
            try
            {
                return new ReleaseListResult(ParseReleases(cache.Json), ReleaseSource.Offline, cache.FetchedAt, error);
            }
            catch (JsonException) { /* kaputter Cache -> wie kein Cache */ }
        }
        throw new ReleasesUnavailableException($"Releases von {repo} konnten nicht geladen werden.", error);
    }

    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        var raw = JsonSerializer.Deserialize<List<ApiRelease>>(json, Json.GitHub) ?? [];
        var result = new List<ReleaseInfo>(raw.Count);
        foreach (var r in raw)
        {
            if (string.IsNullOrEmpty(r.TagName)) continue;
            SemVersion.TryParse(r.TagName, out var version);
            result.Add(new ReleaseInfo
            {
                TagName = r.TagName,
                Name = r.Name,
                Version = version,
                Prerelease = r.Prerelease,
                Draft = r.Draft,
                PublishedAt = r.PublishedAt,
                Body = r.Body,
                HtmlUrl = r.HtmlUrl,
                Assets = (r.Assets ?? [])
                    .Where(a => !string.IsNullOrEmpty(a.Name) && Uri.TryCreate(a.BrowserDownloadUrl, UriKind.Absolute, out _))
                    .Select(a => new ReleaseAsset
                    {
                        Name = a.Name!,
                        Size = a.Size,
                        DownloadUrl = new Uri(a.BrowserDownloadUrl!),
                        Digest = a.Digest,
                    })
                    .ToList(),
            });
        }
        return result;
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests ||
        (response.StatusCode == HttpStatusCode.Forbidden &&
         response.Headers.TryGetValues("X-RateLimit-Remaining", out var v) && v.FirstOrDefault() == "0");

    private static DateTimeOffset? ReadReset(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Reset", out var v) && long.TryParse(v.FirstOrDefault(), out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

    internal string CacheFileFor(string repo) =>
        Path.Combine(_paths.ReleaseCacheDir, repo.Replace('/', '_').ToLowerInvariant() + ".json");

    private static CacheEntry? ReadCache(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file), Json.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache(string file, CacheEntry entry) =>
        AtomicFile.WriteAllText(file, JsonSerializer.Serialize(entry, Json.Options));

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoRegex();

    internal sealed record CacheEntry(string? ETag, DateTimeOffset FetchedAt, string Json);

    private sealed class ApiRelease
    {
        public string? TagName { get; set; }
        public string? Name { get; set; }
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public string? Body { get; set; }
        public string? HtmlUrl { get; set; }
        public List<ApiAsset>? Assets { get; set; }
    }

    private sealed class ApiAsset
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? BrowserDownloadUrl { get; set; }
        public string? Digest { get; set; }
    }
}
