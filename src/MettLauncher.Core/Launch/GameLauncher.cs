using System.Collections.Concurrent;
using System.Diagnostics;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.IO;
using MettLauncher.Core.State;

namespace MettLauncher.Core.Launch;

/// <summary>Abstraktion über <see cref="Process"/>, damit Tests ohne echte Prozesse auskommen.</summary>
public interface IProcessStarter
{
    IRunningProcess Start(ProcessStartInfo startInfo);

    /// <summary>Best effort: läuft bereits ein Prozess genau dieser exe (z. B. außerhalb des Launchers gestartet)?</summary>
    bool IsRunning(string exePath);
}

public interface IRunningProcess : IDisposable
{
    int Id { get; }
    Task WaitForExitAsync(CancellationToken ct = default);
    int ExitCode { get; }
}

public sealed class SystemProcessStarter : IProcessStarter
{
    public IRunningProcess Start(ProcessStartInfo startInfo) =>
        new SystemProcess(Process.Start(startInfo) ?? throw new InvalidOperationException($"'{startInfo.FileName}' konnte nicht gestartet werden."));

    public bool IsRunning(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
        {
            using (p)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, full, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Kein Zugriff (z. B. anderer Benutzer) oder schon beendet.
                }
            }
        }
        return false;
    }

    private sealed class SystemProcess(Process process) : IRunningProcess
    {
        public int Id => process.Id;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken ct = default) => process.WaitForExitAsync(ct);
        public void Dispose() => process.Dispose();
    }
}

public sealed record GameExitInfo(string GameId, TimeSpan Duration, int? ExitCode);

public sealed class GameAlreadyRunningException(string gameName) : Exception($"{gameName} läuft bereits.");

public sealed class GameNotInstalledException(string gameName) : Exception($"{gameName} ist nicht installiert.");

/// <summary>Eine laufende Spielsitzung.</summary>
public sealed class GameSession
{
    internal GameSession(string gameId, int processId, DateTimeOffset startedAt, Task<GameExitInfo> exited)
    {
        GameId = gameId;
        ProcessId = processId;
        StartedAt = startedAt;
        Exited = exited;
    }

    public string GameId { get; }
    public int ProcessId { get; }
    public DateTimeOffset StartedAt { get; }
    /// <summary>Wird abgeschlossen, wenn das Spiel beendet ist und die Spielzeit gespeichert wurde.</summary>
    public Task<GameExitInfo> Exited { get; }
}

/// <summary>
/// Startet Spiele (WorkingDirectory = Installordner), misst die Spielzeit und schreibt sie in den State.
/// Pro Spiel ist höchstens eine Sitzung aktiv.
/// </summary>
public sealed class GameLauncher
{
    private readonly LibraryStore _library;
    private readonly IProcessStarter _starter;
    private readonly TimeProvider _time;
    private readonly Func<GameEntry, IGameRunner> _runnerFor;
    private readonly ConcurrentDictionary<string, GameSession?> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="runnerFor">Wählt pro Spiel den Start-Weg (nativ oder Proton). Standard: immer nativ.</param>
    public GameLauncher(LibraryStore library, IProcessStarter? starter = null, TimeProvider? time = null,
        Func<GameEntry, IGameRunner>? runnerFor = null)
    {
        _library = library;
        _starter = starter ?? new SystemProcessStarter();
        _time = time ?? TimeProvider.System;
        var native = new NativeRunner();
        _runnerFor = runnerFor ?? (_ => native);
    }

    public IGameRunner RunnerFor(GameEntry game) => _runnerFor(game);

    /// <summary>Beendet ein laufendes Spiel über seinen Runner (unter Proton inkl. Wine-Prozessen).</summary>
    public async Task StopAsync(GameEntry game)
    {
        if (GetSession(game.Id) is { } session)
            await _runnerFor(game).StopAsync(game, session).ConfigureAwait(false);
    }

    public event EventHandler<GameSession>? GameStarted;
    public event EventHandler<GameExitInfo>? GameExited;

    public bool IsRunning(string gameId) => _running.ContainsKey(gameId);

    public GameSession? GetSession(string gameId) => _running.GetValueOrDefault(gameId);

    /// <param name="environment">Zusätzliche Umgebungsvariablen für das Spiel (z. B. <c>METT_NET_*</c> für den Mehrspieler).</param>
    public GameSession Launch(GameEntry game, IReadOnlyDictionary<string, string>? environment = null)
    {
        var state = _library.GetGame(game.Id);
        if (state is not { IsInstalled: true }) throw new GameNotInstalledException(game.Name);
        var installDir = state.InstallPath!;
        var exe = FileSystemUtil.SafeCombine(installDir, game.Exe);
        if (exe is null || !File.Exists(exe)) throw new GameNotInstalledException(game.Name);

        // Platzhalter reservieren -> zwei parallele Launch-Aufrufe können nicht beide starten.
        if (!_running.TryAdd(game.Id, null)) throw new GameAlreadyRunningException(game.Name);
        IRunningProcess process;
        DateTimeOffset startedAt;
        try
        {
            var runner = _runnerFor(game);
            if (runner.DetectsExternalInstances && _starter.IsRunning(exe)) throw new GameAlreadyRunningException(game.Name);
            var psi = runner.CreateStartInfo(game, installDir, exe, environment);
            startedAt = _time.GetUtcNow();
            process = _starter.Start(psi);
        }
        catch
        {
            _running.TryRemove(game.Id, out _);
            throw;
        }

        _library.UpdateGame(game.Id, s => s.LastPlayed = startedAt);
        var tcs = new TaskCompletionSource<GameExitInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new GameSession(game.Id, process.Id, startedAt, tcs.Task);
        _running[game.Id] = session;
        GameStarted?.Invoke(this, session);
        _ = WatchAsync(game.Id, process, startedAt, tcs);
        return session;
    }

    private async Task WatchAsync(string gameId, IRunningProcess process, DateTimeOffset startedAt, TaskCompletionSource<GameExitInfo> tcs)
    {
        int? exitCode = null;
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { exitCode = process.ExitCode; } catch (InvalidOperationException) { }
        }
        catch (Exception)
        {
            // Prozess verloren – Spielzeit trotzdem verbuchen.
        }
        finally
        {
            process.Dispose();
        }

        var end = _time.GetUtcNow();
        var duration = end - startedAt;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        try
        {
            _library.UpdateGame(gameId, s =>
            {
                s.TotalPlaySeconds += (long)Math.Round(duration.TotalSeconds);
                s.LastPlayed = end;
            });
        }
        catch (Exception)
        {
            // Speichern fehlgeschlagen – Sitzung trotzdem beenden.
        }
        finally
        {
            _running.TryRemove(gameId, out _);
        }

        var info = new GameExitInfo(gameId, duration, exitCode);
        tcs.TrySetResult(info);
        GameExited?.Invoke(this, info);
    }
}
