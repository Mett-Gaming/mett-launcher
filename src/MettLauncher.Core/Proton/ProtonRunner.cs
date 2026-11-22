using System.Diagnostics;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.Launch;
using MettLauncher.Core.State;

namespace MettLauncher.Core.Proton;

/// <summary>
/// Startet Windows-Spiele unter Linux über <c>umu-run</c> + Proton. Jedes Spiel bekommt einen eigenen Prefix
/// unter <c>prefixes\&lt;id&gt;</c>. umu-run läuft bis zum Spielende, daher funktioniert die Spielzeitmessung wie nativ.
/// </summary>
public sealed class ProtonRunner(LauncherPaths paths, ProtonManager proton, UmuManager umu, LibraryStore library, Func<bool>? protonLog = null)
    : IGameRunner
{
    public bool DetectsExternalInstances => false;

    public string PrefixFor(string gameId) => Path.Combine(paths.PrefixDir, gameId);

    public ProtonInstall? ProtonFor(GameEntry game) => proton.Resolve(library.GetGame(game.Id)?.ProtonVersion);

    public string Describe(GameEntry game) => ProtonFor(game) is { } p ? $"Proton ({p.Display})" : "Proton";

    public ProcessStartInfo CreateStartInfo(GameEntry game, string installDir, string exePath, IReadOnlyDictionary<string, string>? environment)
    {
        var run = umu.Resolve() ?? throw new RunnerNotReadyException("Proton ist noch nicht eingerichtet (umu-launcher fehlt).");
        var p = ProtonFor(game) ?? throw new RunnerNotReadyException("Proton ist noch nicht eingerichtet (keine Proton-Version).");
        var prefix = PrefixFor(game.Id);
        Directory.CreateDirectory(prefix);

        var psi = new ProcessStartInfo(run.Path)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(exePath);
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);

        psi.Environment["WINEPREFIX"] = prefix;
        psi.Environment["PROTONPATH"] = p.Path;
        psi.Environment["GAMEID"] = string.IsNullOrWhiteSpace(game.UmuId) ? "umu-default" : game.UmuId;
        psi.Environment["STORE"] = "none";
        if (protonLog?.Invoke() == true)
        {
            Directory.CreateDirectory(paths.LogDir);
            psi.Environment["PROTON_LOG"] = "1";
            psi.Environment["PROTON_LOG_DIR"] = paths.LogDir;
            psi.Environment["UMU_LOG"] = "1";
        }
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v; // METT_NET_* reicht Wine an das Spiel durch
        return psi;
    }

    public async Task StopAsync(GameEntry game, GameSession session)
    {
        // umu-run und alle Kinder (Container, Wine) beenden …
        await ProcessKiller.CloseOrKillAsync(session.ProcessId).ConfigureAwait(false);
        // … und übrig gebliebene Wine-Prozesse im Prefix (best effort).
        if (ProtonFor(game) is not { } p) return;
        var wineserver = Path.Combine(p.Path, "files", "bin", "wineserver");
        if (!File.Exists(wineserver)) return;
        foreach (var prefix in new[] { PrefixFor(game.Id), Path.Combine(PrefixFor(game.Id), "pfx") })
        {
            if (!Directory.Exists(prefix)) continue;
            try
            {
                var psi = new ProcessStartInfo(wineserver, "-k") { UseShellExecute = false };
                psi.Environment["WINEPREFIX"] = prefix;
                using var k = Process.Start(psi);
                if (k is not null) await k.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>Löscht den Prefix eines Spiels (z. B. wenn er kaputt ist). Spielstände im Prefix gehen dabei verloren.</summary>
    public void ResetPrefix(GameEntry game) => IO.FileSystemUtil.TryDeleteDirectory(PrefixFor(game.Id));
}
