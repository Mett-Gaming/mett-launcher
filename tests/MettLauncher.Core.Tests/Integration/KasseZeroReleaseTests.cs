using MettLauncher.Core.GitHub;
using MettLauncher.Core.Install;
using MettLauncher.Core.State;
using MettLauncher.Core.Tests.Support;

namespace MettLauncher.Core.Tests.Integration;

/// <summary>
/// Läuft gegen die echte GitHub-API. Abschalten mit <c>METT_SKIP_INTEGRATION=1</c> (z. B. offline oder in CI).
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(string? requireEnv = null)
    {
        if (Environment.GetEnvironmentVariable("METT_SKIP_INTEGRATION") is "1" or "true")
            Skip = "METT_SKIP_INTEGRATION ist gesetzt.";
        else if (requireEnv is not null && Environment.GetEnvironmentVariable(requireEnv) is not ("1" or "true"))
            Skip = $"Nur mit {requireEnv}=1.";
    }
}

public class KasseZeroReleaseTests
{
    private const string V017Sha256 = "5f50d76ab0f7075d4f985b30bdab58ba788a28d4dc40bccabbf92126bb40b93a";

    [IntegrationFact]
    public async Task Resolves_real_kasse0_v0_17_0_release()
    {
        using var tmp = new TempDir();
        using var http = MettHttp.CreateClient();
        var svc = new ReleaseService(http, tmp.Paths);

        var result = await svc.GetReleasesAsync("Mett-Gaming/kasse0");
        Assert.NotEqual(ReleaseSource.Offline, result.Source);

        var resolved = ReleaseSelector.FindByTag(result.Releases, TestData.Kasse0(), "v0.17.0");
        Assert.NotNull(resolved);
        Assert.Equal(new SemVersion(0, 17, 0), resolved.Version);
        Assert.Equal("KASSE0-v0.17.0.zip", resolved.Asset.Name);
        Assert.Equal(V017Sha256, resolved.Asset.Sha256);
        Assert.True(resolved.Asset.Size > 1_000_000);
        Assert.Equal("https://github.com/Mett-Gaming/kasse0/releases/download/v0.17.0/KASSE0-v0.17.0.zip",
            resolved.Asset.DownloadUrl.AbsoluteUri);

        var latest = ReleaseSelector.Latest(result.Releases, TestData.Kasse0());
        Assert.True(latest!.Version >= resolved.Version);

        // Zweiter Abruf schickt den ETag mit; GitHub antwortet 304 (zählt nicht gegen das Limit).
        var again = await svc.GetReleasesAsync("Mett-Gaming/kasse0");
        Assert.Equal(ReleaseSource.NotModified, again.Source);
        Assert.Equal(result.Releases.Count, again.Releases.Count);
    }

    /// <summary>Lädt das echte Zip (~7 MB) und installiert es. Nur mit <c>METT_INTEGRATION_DOWNLOAD=1</c>.</summary>
    [IntegrationFact(requireEnv: "METT_INTEGRATION_DOWNLOAD")]
    public async Task Installs_real_kasse0_v0_17_0()
    {
        using var tmp = new TempDir();
        using var http = MettHttp.CreateClient();
        var releases = await new ReleaseService(http, tmp.Paths).GetReleasesAsync("Mett-Gaming/kasse0");
        var resolved = ReleaseSelector.FindByTag(releases.Releases, TestData.Kasse0(), "v0.17.0")!;
        var library = new LibraryStore(tmp.Paths);
        var install = new InstallService(http, library, () => tmp.Sub("games"));

        var result = await install.InstallAsync(TestData.Kasse0(), resolved);

        Assert.True(result.HashVerified);
        Assert.True(File.Exists(Path.Combine(tmp.Sub("games", "kasse0"), "KASSE0.exe")));
        Assert.Equal("0.17.0", library.GetGame("kasse0")!.InstalledVersion);
    }
}
