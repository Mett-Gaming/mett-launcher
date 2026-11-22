using System.Text.RegularExpressions;
using MettLauncher.Core.Catalog;

namespace MettLauncher.Core.GitHub;

/// <summary>Wählt aus einer Release-Liste die installierbaren Releases und das passende Asset.</summary>
public static class ReleaseSelector
{
    public static ReleaseAsset? SelectAsset(ReleaseInfo release, string assetPattern)
    {
        var regex = new Regex(assetPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return release.Assets.FirstOrDefault(a => regex.IsMatch(a.Name));
    }

    /// <summary>
    /// Alle installierbaren Releases (kein Draft, SemVer-Tag, passendes Asset), neueste zuerst.
    /// Prereleases nur, wenn <paramref name="includePrereleases"/> (oder das Spiel selbst) es erlaubt.
    /// </summary>
    public static IReadOnlyList<ResolvedRelease> Installable(IEnumerable<ReleaseInfo> releases, GameEntry game, bool includePrereleases = false)
    {
        var allowPre = includePrereleases || game.IncludePrereleases;
        var regex = new Regex(game.AssetPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return releases
            .Where(r => !r.Draft && r.Version is not null && (allowPre || (!r.Prerelease && !r.Version.IsPrerelease)))
            .Select(r => (r, asset: r.Assets.FirstOrDefault(a => regex.IsMatch(a.Name))))
            .Where(x => x.asset is not null)
            .Select(x => new ResolvedRelease(x.r, x.asset!, x.r.Version!))
            .OrderByDescending(x => x.Version)
            .ToList();
    }

    public static ResolvedRelease? Latest(IEnumerable<ReleaseInfo> releases, GameEntry game, bool includePrereleases = false) =>
        Installable(releases, game, includePrereleases).FirstOrDefault();

    public static ResolvedRelease? FindByTag(IEnumerable<ReleaseInfo> releases, GameEntry game, string tag)
    {
        // Explizit gewählte Versionen dürfen auch Prereleases sein.
        return Installable(releases, game, includePrereleases: true)
            .FirstOrDefault(r => string.Equals(r.Tag, tag, StringComparison.OrdinalIgnoreCase));
    }
}
