using MettLauncher.Core.News;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class NewsServiceTests
{
    private const string Url = "https://raw.githubusercontent.com/Mett-Gaming/news/main/news.json";

    private const string ValidJson = """
        {
          "schema": 1,
          "items": [
            { "id": "launcher-da", "date": "2026-11-21T18:00:00+01:00", "title": "Der Mett Launcher ist da", "kind": "news" },
            { "id": "kasse0-v0.17.0", "date": "2026-11-22T17:25:00+01:00", "title": "Patchnotes v0.17.0", "game": "kasse0",
              "kind": "patchnotes", "excerpt": "Neue Gänge im Lager.", "image": "images/kasse0-v0.17.0.png" }
          ]
        }
        """;

    [Fact]
    public async Task Loads_sorts_newest_first_and_caches()
    {
        using var tmp = new TempDir();
        var svc = new NewsService(new HttpClient(new FakeHttpHandler().OnJson(Url, ValidJson)), tmp.Paths);

        var result = await svc.LoadAsync();

        Assert.False(result.FromCache);
        Assert.Equal(["kasse0-v0.17.0", "launcher-da"], result.Feed.Items.Select(i => i.Id));
        Assert.True(File.Exists(tmp.Paths.NewsCacheFile));
        Assert.Single(NewsService.ForGame(result.Feed, "KASSE0"));
    }

    [Fact]
    public async Task Offline_uses_cache_then_empty_feed()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, ValidJson);
        var svc = new NewsService(new HttpClient(handler), tmp.Paths);
        await svc.LoadAsync();

        handler.Offline = true;
        var cached = await svc.LoadAsync();
        Assert.True(cached.FromCache);
        Assert.NotNull(cached.NetworkError);
        Assert.Equal(2, cached.Feed.Items.Count);

        File.Delete(tmp.Paths.NewsCacheFile);
        var empty = await svc.LoadAsync();
        Assert.Empty(empty.Feed.Items);
        Assert.NotNull(empty.NetworkError);
    }

    [Theory]
    [InlineData("""{ "schema": 2, "items": [] }""")]
    [InlineData("""{ "schema": 1, "items": [ { "id": "a", "date": "2026-01-01T00:00:00Z", "title": "x" }, { "id": "a", "date": "2026-01-01T00:00:00Z", "title": "y" } ] }""")]
    [InlineData("""{ "schema": 1, "items": [ { "id": "a", "date": "2026-01-01T00:00:00Z", "title": " " } ] }""")]
    public void Rejects_invalid_feeds(string json) =>
        Assert.Throws<InvalidDataException>(() => NewsService.Parse(json));

    [Fact]
    public void Resolves_only_safe_image_paths()
    {
        using var tmp = new TempDir();
        var svc = new NewsService(new HttpClient(new FakeHttpHandler()), tmp.Paths);
        Assert.Equal(new Uri("https://raw.githubusercontent.com/Mett-Gaming/news/main/images/a.png"), svc.ResolveAssetUrl("images/a.png"));
        Assert.Null(svc.ResolveAssetUrl("../../secret"));
        Assert.Null(svc.ResolveAssetUrl("http://insecure.example/a.png"));
        Assert.Null(svc.ResolveAssetUrl(null));
    }
}
