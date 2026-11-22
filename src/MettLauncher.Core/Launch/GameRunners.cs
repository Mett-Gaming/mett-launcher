using System.Diagnostics;
using MettLauncher.Core.Catalog;

namespace MettLauncher.Core.Launch;

/// <summary>Wie ein installiertes Spiel gestartet und beendet wird (direkt oder über Proton).</summary>
public interface IGameRunner
{
    /// <summary>Kurzname für Anzeige/Logs, z. B. „Windows“ oder „Proton (GE-Proton11-12)“.</summary>
    string Describe(GameEntry game);

    /// <summary>Baut den Prozessstart. Darf <see cref="RunnerNotReadyException"/> werfen (z. B. Proton fehlt noch).</summary>
    ProcessStartInfo CreateStartInfo(GameEntry game, string installDir, string exePath, IReadOnlyDictionary<string, string>? environment);

    /// <summary>Kann der Launcher außerhalb gestartete Instanzen derselben exe erkennen? (nur nativ sinnvoll)</summary>
    bool DetectsExternalInstances { get; }

    /// <summary>Beendet eine laufende Sitzung (Fenster schließen, notfalls Prozessbaum beenden).</summary>
    Task StopAsync(GameEntry game, GameSession session);
}

/// <summary>Laufzeit ist noch nicht bereit (Proton/umu fehlt); die Meldung ist für den Spieler gedacht.</summary>
public sealed class RunnerNotReadyException(string message) : Exception(message);

/// <summary>Startet die exe direkt (Windows, oder native Linux-Builds).</summary>
public sealed class NativeRunner : IGameRunner
{
    public string Describe(GameEntry game) => OperatingSystem.IsWindows() ? "Windows" : "Nativ";

    public bool DetectsExternalInstances => true;

    public ProcessStartInfo CreateStartInfo(GameEntry game, string installDir, string exePath, IReadOnlyDictionary<string, string>? environment)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = installDir,
            UseShellExecute = false,
        };
        foreach (var arg in game.Args) psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (k, v) in environment) psi.Environment[k] = v;
        return psi;
    }

    public Task StopAsync(GameEntry game, GameSession session) => ProcessKiller.CloseOrKillAsync(session.ProcessId);
}

internal static class ProcessKiller
{
    /// <summary>Erst höflich (Hauptfenster schließen), nach 3 s den ganzen Prozessbaum beenden.</summary>
    public static async Task CloseOrKillAsync(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            if (p.CloseMainWindow())
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); return; }
                catch (OperationCanceledException) { }
            }
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // schon beendet
        }
    }
}
