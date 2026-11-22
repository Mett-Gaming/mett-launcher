using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;

namespace MettLauncher.Core.State;

public enum GameStatusKind
{
    NotInstalled,
    UpToDate,
    UpdateAvailable,
    /// <summary>Installiert und festgepinnt; neuere Versionen werden ignoriert.</summary>
    Pinned,
}

public sealed record GameStatusInfo(GameStatusKind Kind, SemVersion? Installed, ResolvedRelease? Latest)
{
    public bool HasUpdateBadge => Kind == GameStatusKind.UpdateAvailable;
}

public static class GameStatus
{
    /// <summary>Leitet den Anzeigestatus (Update-Badge etc.) aus State und Release-Liste ab.</summary>
    public static GameStatusInfo Compute(GameEntry game, GameState? state, IEnumerable<ReleaseInfo> releases, bool includePrereleases = false)
    {
        var latest = ReleaseSelector.Latest(releases, game, includePrereleases);
        if (state is null || !state.IsInstalled)
            return new GameStatusInfo(GameStatusKind.NotInstalled, null, latest);

        SemVersion.TryParse(state.InstalledVersion, out var installed);
        if (state.PinnedTag is not null)
            return new GameStatusInfo(GameStatusKind.Pinned, installed, latest);
        if (latest is not null && (installed is null || latest.Version > installed))
            return new GameStatusInfo(GameStatusKind.UpdateAvailable, installed, latest);
        return new GameStatusInfo(GameStatusKind.UpToDate, installed, latest);
    }
}
