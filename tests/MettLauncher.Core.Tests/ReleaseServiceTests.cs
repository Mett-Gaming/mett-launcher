using System.Net;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class ReleaseServiceTests
{
    private const string Url = "https://api.github.com/repos/Mett-Gaming/kasse0/releases";

    private static readonly string Json = TestData.ReleasesJson(
        ("v0.18.0", false, false, ["KASSE0-v0.18.0.zip"]),
        ("v0.17.0", false, false, ["KASSE0-v0.17.0.zip", "KASSE0-v0.17.0.zip.sha256"]));

    [Fact]
    public async Task First_call_stores_etag_second_call_sends_if_none_match_and_uses_cache_on_304()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, Json, etag: "W/\"abc\"");
        var svc = new ReleaseService(new HttpClient(handler), tmp.Paths);

        var first = await svc.GetReleasesAsync("Mett-Gaming/kasse0");
        Assert.Equal(ReleaseSource.Network, first.Source);
        Assert.Equal(2, first.Releases.Count);
        Assert.Empty(handler.Requests[0].Headers.IfNoneMatch);
        Assert.Contains("per_page=100", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("application/vnd.github+json", handler.Requests[0].Headers.Accept.ToString());
        Assert.True(File.Exists(Path.Combine(tmp.Paths.ReleaseCacheDir, "mett-gaming_kasse0.json")));

        handler.On(Url, req =>
        {
            Assert.Equal("W/\"abc\"", req.Headers.IfNoneMatch.Single().ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var second = await svc.GetReleasesAsync("Mett-Gaming/kasse0");

        Assert.Equal(ReleaseSource.NotModified, second.Source);
        Assert.Equal(2, second.Releases.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task New_etag_replaces_cache()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, Json, etag: "\"one\"");
        var svc = new ReleaseService(new HttpClient(handler), tmp.Paths);
        await svc.GetReleasesAsync("Mett-Gaming/kasse0");

        handler.OnJson(Url, TestData.ReleasesJson(("v0.19.0", false, false, ["KASSE0-v0.19.0.zip"])), etag: "\"two\"");
        var r = await svc.GetReleasesAsync("Mett-Gaming/kasse0");
        Assert.Equal("v0.19.0", Assert.Single(r.Releases).TagName);

        handler.On(Url, req =>
        {
            Assert.Equal("\"two\"", req.Headers.IfNoneMatch.Single().ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var cached = await svc.GetReleasesAsync("Mett-Gaming/kasse0");
        Assert.Equal("v0.19.0", Assert.Single(cached.Releases).TagName);
    }

    [Fact]
    public async Task Offline_uses_cache()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, Json, etag: "\"x\"");
        var svc = new ReleaseService(new HttpClient(handler), tmp.Paths);
        await svc.GetReleasesAsync("Mett-Gaming/kasse0");

        handler.Offline = true;
        var r = await svc.GetReleasesAsync("Mett-Gaming/kasse0");
        Assert.Equal(ReleaseSource.Offline, r.Source);
        Assert.IsType<HttpRequestException>(r.NetworkError);
        Assert.Equal(2, r.Releases.Count);
    }

    [Fact]
    public async Task Rate_limit_uses_cache_and_reports_reset()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, Json, etag: "\"x\"");
        var svc = new ReleaseService(new HttpClient(handler), tmp.Paths);
        await svc.GetReleasesAsync("Mett-Gaming/kasse0");

        handler.On(Url, _ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
            resp.Headers.Add("X-RateLimit-Remaining", "0");
            resp.Headers.Add("X-RateLimit-Reset", "1795000000");
            return resp;
        });
        var r = await svc.GetReleasesAsync("Mett-Gaming/kasse0");
        Assert.Equal(ReleaseSource.Offline, r.Source);
        var rl = Assert.IsType<GitHubRateLimitException>(r.NetworkError);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795000000), rl.ResetAt);
    }

    [Fact]
    public async Task Throws_without_cache_when_offline()
    {
        using var tmp = new TempDir();
        var svc = new ReleaseService(new HttpClient(new FakeHttpHandler { Offline = true }), tmp.Paths);
        await Assert.ThrowsAsync<ReleasesUnavailableException>(() => svc.GetReleasesAsync("Mett-Gaming/kasse0"));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("noslash")]
    [InlineData("a/b/c")]
    [InlineData("")]
    public async Task Rejects_invalid_repo(string repo)
    {
        using var tmp = new TempDir();
        var svc = new ReleaseService(new HttpClient(new FakeHttpHandler()), tmp.Paths);
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetReleasesAsync(repo));
    }

    [Fact]
    public void Parses_digest_version_and_metadata()
    {
        var json = """
            [{
              "tag_name": "v0.17.0", "name": "KASSE 0 v0.17.0", "draft": false, "prerelease": false,
              "published_at": "2026-11-20T10:00:00Z", "body": "## Neu", "html_url": "https://x",
              "assets": [{
                "name": "KASSE0-v0.17.0.zip", "size": 7521820,
                "browser_download_url": "https://github.com/Mett-Gaming/kasse0/releases/download/v0.17.0/KASSE0-v0.17.0.zip",
                "digest": "sha256:5F50D76AB0F7075D4F985B30BDAB58BA788A28D4DC40BCCABBF92126BB40B93A"
              }, {
                "name": "nodigest.txt", "size": 1, "browser_download_url": "https://x/nodigest.txt", "digest": null
              }]
            }, { "tag_name": "nightly", "assets": [] }]
            """;
        var releases = ReleaseService.ParseReleases(json);

        Assert.Equal(2, releases.Count);
        var r = releases[0];
        Assert.Equal(new SemVersion(0, 17, 0), r.Version);
        Assert.Equal("## Neu", r.Body);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:00:00Z"), r.PublishedAt);
        Assert.Equal("5f50d76ab0f7075d4f985b30bdab58ba788a28d4dc40bccabbf92126bb40b93a", r.Assets[0].Sha256);
        Assert.Equal(7521820, r.Assets[0].Size);
        Assert.Null(r.Assets[1].Sha256);
        Assert.Null(releases[1].Version);
    }
}
