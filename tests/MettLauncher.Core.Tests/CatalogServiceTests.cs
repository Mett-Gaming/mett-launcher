using MettLauncher.Core.Catalog;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class CatalogServiceTests
{
    private const string Url = "https://raw.githubusercontent.com/Mett-Gaming/launcher-catalog/main/games.json";

    private const string ValidJson = """
        {
          "schema": 1,
          "games": [{
            "id": "kasse0",
            "name": "KASSE 0",
            "repo": "Mett-Gaming/kasse0",
            "tagline": "Liminaler Supermarkt-Horror",
            "cover": "covers/kasse0.png",
            "hero": "covers/kasse0-hero.png",
            "assetPattern": "^KASSE0-v.*\\.zip$",
            "stripTopFolder": true,
            "exe": "KASSE0.exe",
            "args": [],
            "preserve": [],
            "includePrereleases": false
          }]
        }
        """;

    [Fact]
    public async Task Loads_schema_v1_and_writes_cache()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, ValidJson);
        var svc = new CatalogService(new HttpClient(handler), tmp.Paths);

        var result = await svc.LoadAsync();

        Assert.False(result.FromCache);
        var game = Assert.Single(result.Catalog.Games);
        Assert.Equal("kasse0", game.Id);
        Assert.Equal("Mett-Gaming/kasse0", game.Repo);
        Assert.True(game.StripTopFolder);
        Assert.Equal("KASSE0.exe", game.Exe);
        Assert.True(File.Exists(tmp.Paths.CatalogCacheFile));
        Assert.Equal(new Uri("https://raw.githubusercontent.com/Mett-Gaming/launcher-catalog/main/covers/kasse0.png"),
            svc.ResolveAssetUrl(game.Cover));
    }

    [Fact]
    public async Task Falls_back_to_cache_when_offline()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, ValidJson);
        var svc = new CatalogService(new HttpClient(handler), tmp.Paths);
        await svc.LoadAsync();

        handler.Offline = true;
        var result = await svc.LoadAsync();

        Assert.True(result.FromCache);
        Assert.NotNull(result.NetworkError);
        Assert.Equal("kasse0", Assert.Single(result.Catalog.Games).Id);
    }

    [Fact]
    public async Task Throws_when_offline_without_cache()
    {
        using var tmp = new TempDir();
        var svc = new CatalogService(new HttpClient(new FakeHttpHandler { Offline = true }), tmp.Paths);
        await Assert.ThrowsAsync<CatalogUnavailableException>(() => svc.LoadAsync());
    }

    [Fact]
    public async Task Unsupported_schema_is_rejected_and_cache_is_kept()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, ValidJson);
        var svc = new CatalogService(new HttpClient(handler), tmp.Paths);
        await svc.LoadAsync();

        handler.OnJson(Url, """{ "schema": 2, "games": [] }""");
        var result = await svc.LoadAsync();

        Assert.True(result.FromCache);
        Assert.IsType<InvalidDataException>(result.NetworkError);
        Assert.Single(result.Catalog.Games);
    }

    [Fact]
    public async Task Http_error_falls_back_to_cache()
    {
        using var tmp = new TempDir();
        var handler = new FakeHttpHandler().OnJson(Url, ValidJson);
        var svc = new CatalogService(new HttpClient(handler), tmp.Paths);
        await svc.LoadAsync();

        handler.On(Url, _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var result = await svc.LoadAsync();
        Assert.True(result.FromCache);
    }

    [Theory]
    [InlineData("""{ "schema": 1, "games": [{ "id": "../evil", "name": "x", "repo": "a/b", "assetPattern": ".*", "exe": "x.exe" }] }""")]
    [InlineData("""{ "schema": 1, "games": [{ "id": "ok", "name": "x", "repo": "not a repo", "assetPattern": ".*", "exe": "x.exe" }] }""")]
    [InlineData("""{ "schema": 1, "games": [{ "id": "ok", "name": "x", "repo": "a/b", "assetPattern": "(", "exe": "x.exe" }] }""")]
    [InlineData("""{ "schema": 1, "games": [{ "id": "ok", "name": "x", "repo": "a/b", "assetPattern": ".*", "exe": "..\\x.exe" }] }""")]
    [InlineData("""{ "schema": 1, "games": [{ "id": "ok", "name": "x", "repo": "a/b", "assetPattern": ".*", "exe": "x.exe", "preserve": ["C:\\Windows"] }] }""")]
    [InlineData("""{ "schema": 1, "games": [{ "id": "a", "name": "x", "repo": "a/b", "assetPattern": ".*", "exe": "x.exe" }, { "id": "A", "name": "y", "repo": "a/c", "assetPattern": ".*", "exe": "y.exe" }] }""")]
    public void Parse_rejects_invalid_entries(string json)
    {
        Assert.Throws<InvalidDataException>(() => CatalogService.Parse(json));
    }

    [Fact]
    public void Parse_applies_defaults()
    {
        var c = CatalogService.Parse("""{ "schema": 1, "games": [{ "id": "g", "name": "G", "repo": "o/r", "assetPattern": ".*", "exe": "bin/g.exe" }] }""");
        var g = Assert.Single(c.Games);
        Assert.True(g.StripTopFolder);
        Assert.Empty(g.Args);
        Assert.Empty(g.Preserve);
        Assert.False(g.IncludePrereleases);
    }
}
