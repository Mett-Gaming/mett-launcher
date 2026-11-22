using MettLauncher.Core.GitHub;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests;

public class SemVersionTests
{
    [Theory]
    [InlineData("v0.17.0", 0, 17, 0, null)]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("V10.0.1-beta.2+build.7", 10, 0, 1, "beta.2")]
    public void Parses(string text, int major, int minor, int patch, string? pre)
    {
        var v = SemVersion.Parse(text);
        Assert.Equal((major, minor, patch, pre), (v.Major, v.Minor, v.Patch, v.Prerelease));
    }

    [Theory]
    [InlineData("v1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("nightly")]
    [InlineData("v01.2.3")]
    [InlineData("")]
    public void Rejects_invalid(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Theory]
    [InlineData("v0.9.0", "v0.10.0")]
    [InlineData("v0.17.0", "v0.18.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.9", "2.0.0")]
    public void Orders_by_semver_precedence(string lower, string higher)
    {
        Assert.True(SemVersion.Parse(lower) < SemVersion.Parse(higher));
        Assert.True(SemVersion.Parse(higher) > SemVersion.Parse(lower));
    }

    [Fact]
    public void Build_metadata_is_ignored_for_equality() =>
        Assert.Equal(SemVersion.Parse("1.0.0+a"), SemVersion.Parse("v1.0.0+b"));
}

public class ReleaseSelectorTests
{
    private static IReadOnlyList<ReleaseInfo> Releases() => ReleaseService.ParseReleases(TestData.ReleasesJson(
        ("v0.19.0-beta.1", true, false, ["KASSE0-v0.19.0-beta.1.zip"]),
        ("v0.20.0", false, true, ["KASSE0-v0.20.0.zip"]),          // Draft
        ("v0.18.0", false, false, ["source.tar.gz"]),               // kein passendes Asset
        ("v0.17.0", false, false, ["notes.txt", "KASSE0-v0.17.0.zip"]),
        ("nightly", false, false, ["KASSE0-vnightly.zip"]),         // kein SemVer
        ("v0.9.0", false, false, ["KASSE0-v0.9.0.zip"])));

    [Fact]
    public void Selects_asset_by_pattern()
    {
        var r = Releases().Single(x => x.TagName == "v0.17.0");
        Assert.Equal("KASSE0-v0.17.0.zip", ReleaseSelector.SelectAsset(r, @"^KASSE0-v.*\.zip$")!.Name);
        Assert.Null(ReleaseSelector.SelectAsset(r, @"^Other-.*\.zip$"));
    }

    [Fact]
    public void Latest_skips_drafts_prereleases_missing_assets_and_non_semver()
    {
        var latest = ReleaseSelector.Latest(Releases(), TestData.Kasse0());
        Assert.Equal("v0.17.0", latest!.Tag);
        Assert.Equal("KASSE0-v0.17.0.zip", latest.Asset.Name);
    }

    [Fact]
    public void Installable_is_sorted_newest_first()
    {
        var tags = ReleaseSelector.Installable(Releases(), TestData.Kasse0()).Select(r => r.Tag);
        Assert.Equal(["v0.17.0", "v0.9.0"], tags);
    }

    [Fact]
    public void Prerelease_channel_includes_prereleases()
    {
        Assert.Equal("v0.19.0-beta.1", ReleaseSelector.Latest(Releases(), TestData.Kasse0(), includePrereleases: true)!.Tag);
        Assert.Equal("v0.19.0-beta.1", ReleaseSelector.Latest(Releases(), TestData.Kasse0(b => b.IncludePrereleases = true))!.Tag);
    }

    [Fact]
    public void FindByTag_allows_explicit_prerelease()
    {
        Assert.NotNull(ReleaseSelector.FindByTag(Releases(), TestData.Kasse0(), "v0.19.0-beta.1"));
        Assert.Null(ReleaseSelector.FindByTag(Releases(), TestData.Kasse0(), "v0.20.0"));
    }

    [Fact]
    public void Status_reports_update_pinned_and_up_to_date()
    {
        var game = TestData.Kasse0();
        var releases = Releases();

        Assert.Equal(GameStatusKind.NotInstalled, GameStatus.Compute(game, null, releases).Kind);

        var old = new GameState { InstalledVersion = "0.9.0", InstalledTag = "v0.9.0", InstallPath = "x" };
        var s = GameStatus.Compute(game, old, releases);
        Assert.Equal(GameStatusKind.UpdateAvailable, s.Kind);
        Assert.True(s.HasUpdateBadge);
        Assert.Equal("v0.17.0", s.Latest!.Tag);

        old.PinnedTag = "v0.9.0";
        Assert.Equal(GameStatusKind.Pinned, GameStatus.Compute(game, old, releases).Kind);

        var current = new GameState { InstalledVersion = "0.17.0", InstallPath = "x" };
        Assert.Equal(GameStatusKind.UpToDate, GameStatus.Compute(game, current, releases).Kind);
    }
}
