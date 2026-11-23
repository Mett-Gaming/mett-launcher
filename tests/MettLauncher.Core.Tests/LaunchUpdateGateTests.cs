using System.Diagnostics;
using System.Net;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Launch;
using MettLauncher.Core.Settings;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class LaunchUpdateGateTests
{
    private const string Url = "https://api.github.com/repos/Mett-Gaming/kasse0/releases";

    private static readonly string Json = TestData.ReleasesJson(
        ("v0.19.0-beta.1", true, false, ["KASSE0-v0.19.0-beta.1.zip"]),
        ("v0.18.0", false, false, ["KASSE0-v0.18.0.zip"]),
        ("v0.17.0", false, false, ["KASSE0-v0.17.0.zip"]));

    private sealed class Fixture : IDisposable
    {
        private readonly TempDir _tmp = new();

        public Fixture(string? installed = "0.17.0", TimeSpan? timeout = null)
        {
            Http = new FakeHttpHandler().OnJson(Url, Json, etag: "\"x\"");
            Library = new LibraryStore(_tmp.Paths);
            Settings = new SettingsStore(_tmp.Paths);
            Releases = new ReleaseService(new HttpClient(Http), _tmp.Paths);
            Gate = new LaunchUpdateGate(Releases, Library, Settings) { CheckTimeout = timeout ?? TimeSpan.FromSeconds(30) };
            if (installed is not null)
                Library.UpdateGame("kasse0", g =>
                {
                    g.InstalledVersion = installed;
                    g.InstalledTag = "v" + installed;
                    g.InstallPath = _tmp.Sub("games", "kasse0");
                });
        }

        public FakeHttpHandler Http { get; }
        public LibraryStore Library { get; }
        public SettingsStore Settings { get; }
        public ReleaseService Releases { get; }
        public LaunchUpdateGate Gate { get; }

        public void Dispose() => _tmp.Dispose();
    }

    [Fact]
    public async Task Offers_newer_release_with_both_versions()
    {
        using var f = new Fixture();
        var check = await f.Gate.CheckAsync(TestData.Kasse0());

        Assert.Equal("v0.17.0", check.Offer!.InstalledLabel);
        Assert.Equal("v0.18.0", check.Offer.Latest.Tag);
        Assert.Equal(3, check.Releases!.Count);
        Assert.True(f.Http.Requests.Single().Headers.CacheControl!.NoCache);
    }

    [Fact]
    public async Task Always_asks_server_and_sees_release_published_since_last_check()
    {
        using var f = new Fixture("0.18.0");
        Assert.Null((await f.Gate.CheckAsync(TestData.Kasse0())).Offer);

        f.Http.OnJson(Url, TestData.ReleasesJson(("v0.20.0", false, false, ["KASSE0-v0.20.0.zip"])), etag: "\"y\"");
        Assert.Equal("v0.20.0", (await f.Gate.CheckAsync(TestData.Kasse0())).Offer!.Latest.Tag);
        Assert.Equal(2, f.Http.Requests.Count);
    }

    [Fact]
    public async Task Not_modified_still_offers_known_update()
    {
        using var f = new Fixture();
        await f.Releases.GetReleasesAsync("Mett-Gaming/kasse0");
        f.Http.On(Url, _ => new HttpResponseMessage(HttpStatusCode.NotModified));

        Assert.Equal("v0.18.0", (await f.Gate.CheckAsync(TestData.Kasse0())).Offer!.Latest.Tag);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "v0.19.0-beta.1")]
    public async Task Respects_prerelease_channel(bool includePrereleases, string? expected)
    {
        using var f = new Fixture("0.18.0");
        f.Settings.Update(s => s with { IncludePrereleases = includePrereleases });
        Assert.Equal(expected, (await f.Gate.CheckAsync(TestData.Kasse0())).Offer?.Latest.Tag);
    }

    [Fact]
    public async Task Pinned_or_missing_game_is_not_checked()
    {
        using var pinned = new Fixture();
        pinned.Library.UpdateGame("kasse0", g => g.PinnedTag = "v0.17.0");
        Assert.Null((await pinned.Gate.CheckAsync(TestData.Kasse0())).Offer);
        Assert.Empty(pinned.Http.Requests);

        using var missing = new Fixture(installed: null);
        Assert.Null((await missing.Gate.CheckAsync(TestData.Kasse0())).Offer);
        Assert.Empty(missing.Http.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Offline_starts_without_question(bool cached)
    {
        using var f = new Fixture();
        if (cached) await f.Releases.GetReleasesAsync("Mett-Gaming/kasse0");
        f.Http.Offline = true;
        Assert.True(await f.Gate.RunAsync(TestData.Kasse0(), _ => true, NeverAsk, NeverUpdate));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Rate_limit_or_server_error_starts_without_question(HttpStatusCode code)
    {
        using var f = new Fixture();
        f.Http.On(Url, _ =>
        {
            var r = new HttpResponseMessage(code);
            r.Headers.Add("X-RateLimit-Remaining", "0");
            return r;
        });
        Assert.True(await f.Gate.RunAsync(TestData.Kasse0(), _ => true, NeverAsk, NeverUpdate));
    }

    [Fact]
    public async Task Broken_json_starts_without_question()
    {
        using var f = new Fixture();
        f.Http.OnJson(Url, "<html>");
        Assert.True(await f.Gate.RunAsync(TestData.Kasse0(), _ => true, NeverAsk, NeverUpdate));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // Handler ignoriert den Abbruch
    public async Task Timeout_starts_without_question(bool honoursCancellation)
    {
        using var f = new Fixture(timeout: TimeSpan.FromMilliseconds(100));
        var never = new TaskCompletionSource<HttpResponseMessage>();
        f.Http.On(Url, (_, ct) => honoursCancellation ? never.Task.WaitAsync(ct) : never.Task);

        var sw = Stopwatch.StartNew();
        Assert.True(await f.Gate.RunAsync(TestData.Kasse0(), _ => true, NeverAsk, NeverUpdate));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Default_timeout_is_four_seconds()
    {
        using var f = new Fixture();
        Assert.Equal(TimeSpan.FromSeconds(4), new LaunchUpdateGate(f.Releases, f.Library, f.Settings).CheckTimeout);
    }

    [Theory]
    [InlineData(LaunchUpdateChoice.Cancel, false)]
    [InlineData(LaunchUpdateChoice.StartWithoutUpdate, true)]
    public async Task Choice_without_update(LaunchUpdateChoice choice, bool starts)
    {
        using var f = new Fixture();
        Assert.Equal(starts, await f.Gate.RunAsync(TestData.Kasse0(), _ => true, (_, _) => Task.FromResult(choice), NeverUpdate));
    }

    [Theory]
    [InlineData(LaunchUpdateOutcome.Installed, true)]
    [InlineData(LaunchUpdateOutcome.Cancelled, false)]
    public async Task Update_and_start(LaunchUpdateOutcome outcome, bool starts)
    {
        using var f = new Fixture();
        string? updated = null;
        var start = await f.Gate.RunAsync(TestData.Kasse0(), _ => true,
            (_, _) => Task.FromResult(LaunchUpdateChoice.UpdateAndStart),
            r => { updated = r.Tag; return Task.FromResult(new LaunchUpdateResult(outcome)); });

        Assert.Equal(starts, start);
        Assert.Equal("v0.18.0", updated);
    }

    [Theory]
    [InlineData(LaunchUpdateChoice.Cancel, false)]
    [InlineData(LaunchUpdateChoice.StartWithoutUpdate, true)]
    public async Task Failed_update_asks_again_with_error(LaunchUpdateChoice second, bool starts)
    {
        using var f = new Fixture();
        var errors = new List<string?>();
        var start = await f.Gate.RunAsync(TestData.Kasse0(), _ => true,
            (_, error) =>
            {
                errors.Add(error);
                return Task.FromResult(errors.Count == 1 ? LaunchUpdateChoice.UpdateAndStart : second);
            },
            _ => Task.FromResult(new LaunchUpdateResult(LaunchUpdateOutcome.Failed, "kaputt")));

        Assert.Equal(starts, start);
        Assert.Equal([null, "kaputt"], errors);
    }

    [Fact]
    public async Task Aborted_after_check_neither_asks_nor_starts()
    {
        using var f = new Fixture();
        Assert.False(await f.Gate.RunAsync(TestData.Kasse0(), _ => false, NeverAsk, NeverUpdate));
    }

    private static Task<LaunchUpdateChoice> NeverAsk(LaunchUpdateOffer offer, string? error) =>
        throw new InvalidOperationException("Es hätte nicht gefragt werden dürfen.");

    private static Task<LaunchUpdateResult> NeverUpdate(ResolvedRelease release) =>
        throw new InvalidOperationException("Es hätte nicht aktualisiert werden dürfen.");
}
