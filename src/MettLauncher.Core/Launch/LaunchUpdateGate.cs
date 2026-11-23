using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Settings;
using MettLauncher.Core.State;

namespace MettLauncher.Core.Launch;

/// <summary>Ein vor dem Start gefundenes Update.</summary>
public sealed record LaunchUpdateOffer(string InstalledLabel, ResolvedRelease Latest);

/// <param name="Offer">Das anzubietende Update; <c>null</c> = einfach starten.</param>
/// <param name="Releases">Frische Release-Liste, falls die Prüfung geklappt hat.</param>
public sealed record LaunchUpdateCheck(LaunchUpdateOffer? Offer, IReadOnlyList<ReleaseInfo>? Releases);

public enum LaunchUpdateChoice { Cancel, StartWithoutUpdate, UpdateAndStart }

public enum LaunchUpdateOutcome { Installed, Cancelled, Failed }

public sealed record LaunchUpdateResult(LaunchUpdateOutcome Outcome, string? Error = null);

/// <summary>
/// Sucht direkt vor dem Spielstart nach einer neueren Version. Offline, Zeitüberschreitung, Rate-Limit und
/// sonstige Fehler verhindern den Start nie. Festgepinnte Spiele werden nicht geprüft.
/// </summary>
public sealed class LaunchUpdateGate(ReleaseService releases, LibraryStore library, SettingsStore settings)
{
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(4);

    public async Task<LaunchUpdateCheck> CheckAsync(GameEntry game)
    {
        if (library.GetGame(game.Id) is not { IsInstalled: true, PinnedTag: null })
            return new LaunchUpdateCheck(null, null);

        using var timeout = new CancellationTokenSource(CheckTimeout);
        try
        {
            var token = timeout.Token;
            // Task.Run + WaitAsync: weder Cache-Zugriffe noch ein hängender Handler dürfen den Aufrufer aufhalten.
            var result = await Task.Run(() => releases.GetReleasesAsync(game.Repo, token, revalidate: true), token)
                .WaitAsync(token).ConfigureAwait(false);
            if (result.Source == ReleaseSource.Offline) return new LaunchUpdateCheck(null, null);

            // State erst jetzt lesen: während der Prüfung kann sich die Installation geändert haben.
            var state = library.GetGame(game.Id);
            var status = GameStatus.Compute(game, state, result.Releases, settings.Current.IncludePrereleases);
            var offer = status is { Kind: GameStatusKind.UpdateAvailable, Latest: { } latest }
                ? new LaunchUpdateOffer(state!.InstalledTag ?? state.InstalledVersion!, latest)
                : null;
            return new LaunchUpdateCheck(offer, result.Releases);
        }
        catch (Exception)
        {
            return new LaunchUpdateCheck(null, null);
        }
    }

    /// <summary>
    /// Ablauf Prüfung → Nachfrage → ggf. Update. Liefert <c>true</c>, wenn das Spiel gestartet werden soll.
    /// Schlägt das Update fehl, wird mit der Fehlermeldung erneut gefragt. Callbacks laufen im Kontext des Aufrufers.
    /// </summary>
    /// <param name="onChecked">Nach der Prüfung; <c>false</c> bricht den Start ab (z. B. weil inzwischen ein Update läuft).</param>
    public async Task<bool> RunAsync(GameEntry game, Func<LaunchUpdateCheck, bool> onChecked,
        Func<LaunchUpdateOffer, string?, Task<LaunchUpdateChoice>> ask,
        Func<ResolvedRelease, Task<LaunchUpdateResult>> update)
    {
        var check = await CheckAsync(game);
        if (!onChecked(check)) return false;
        if (check.Offer is not { } offer) return true;

        string? error = null;
        while (true)
        {
            switch (await ask(offer, error))
            {
                case LaunchUpdateChoice.StartWithoutUpdate: return true;
                case LaunchUpdateChoice.Cancel: return false;
            }
            var result = await update(offer.Latest);
            if (result.Outcome == LaunchUpdateOutcome.Installed) return true;
            if (result.Outcome == LaunchUpdateOutcome.Cancelled) return false;
            error = result.Error ?? "Unbekannter Fehler.";
        }
    }
}
