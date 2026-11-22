using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Proton;
using MettLauncher.Core.State;

namespace MettLauncher.Core.Tests.Integration;

/// <summary>
/// Echter Proton-Lauf (nur Linux, nur mit <c>METT_PROTON_E2E=1</c>, in der CI unter xvfb):
/// lädt umu + GE-Proton, startet die Windows-Version des mett-net-Chat-Beispiels zweimal über den
/// <see cref="ProtonRunner"/> (Host + Join) gegen zwei native mett-netd und prüft, dass eine Nachricht ankommt.
/// Benötigt <c>METT_E2E_NETD</c> (Linux-mett-netd), <c>METT_E2E_CHAT_EXE</c> (chat.exe) und optional
/// <c>METT_E2E_ROOT</c> (Arbeitsordner, in der CI gecacht).
/// </summary>
public class ProtonE2ETests
{
    private static string Env(string name) => Environment.GetEnvironmentVariable(name)
                                               ?? throw new InvalidOperationException($"{name} fehlt.");

    [IntegrationFact(requireEnv: "METT_PROTON_E2E")]
    public async Task Windows_game_under_proton_reaches_native_netd_and_peer()
    {
        Assert.True(OperatingSystem.IsLinux(), "Nur unter Linux sinnvoll.");
        var root = Environment.GetEnvironmentVariable("METT_E2E_ROOT") ?? Path.Combine(Path.GetTempPath(), "mett-e2e");
        var paths = new LauncherPaths(root);
        using var http = MettHttp.CreateClient();
        var releases = new ReleaseService(http, paths);
        var proton = new ProtonManager(http, paths, releases);
        var umu = new UmuManager(http, paths, releases);
        await umu.EnsureAsync();
        await proton.EnsureAsync();

        var library = new LibraryStore(paths);
        var runner = new ProtonRunner(paths, proton, umu, library, () => true);
        var chatExe = Env("METT_E2E_CHAT_EXE");
        var installDir = Path.GetDirectoryName(chatExe)!;
        var game = new GameEntry { Id = "chat", Name = "Chat", Repo = "Mett-Gaming/none", AssetPattern = "x", Exe = Path.GetFileName(chatExe) };

        using var a = await NetdAsync("Alice", Path.Combine(root, "net-a"));
        using var b = await NetdAsync("Bob", Path.Combine(root, "net-b"));

        // Host (Alice) – Umgebungsvariablen genau wie der Launcher sie setzt.
        using var host = StartGame(runner, game, installDir, chatExe, a.Env("host", null), "host");
        var code = await host.WaitForAsync(l => l.Contains("[host] Code: "), TimeSpan.FromMinutes(15));
        code = code[(code.IndexOf("Code: ", StringComparison.Ordinal) + 6)..].Trim();

        // Join (Bob) mit dem Code, schickt eine Zeile und beendet sich.
        using var join = StartGame(runner, game, installDir, chatExe, b.Env("join", code), "join");
        await join.WaitForAsync(l => l.Contains("[beigetreten] Host: Alice"), TimeSpan.FromMinutes(5));
        await join.SendAsync("Hallo aus Wine");
        await host.WaitForAsync(l => l.Contains("Hallo aus Wine"), TimeSpan.FromMinutes(2));
        await join.SendAsync("/quit");
        await host.SendAsync("/quit");

        await runner.StopAsync(game, new Launch.GameSession("chat", host.Process.Id, DateTimeOffset.UtcNow, Task.FromResult(new Launch.GameExitInfo("chat", TimeSpan.Zero, 0))));
        Assert.True(Directory.Exists(runner.PrefixFor("chat")), "Prefix wurde angelegt.");
    }

    private static GameProcess StartGame(ProtonRunner runner, GameEntry game, string installDir, string exe, Dictionary<string, string> env, string label)
    {
        var psi = runner.CreateStartInfo(game, installDir, exe, env);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        return new GameProcess(Process.Start(psi)!, label);
    }

    private static async Task<Netd> NetdAsync(string name, string dataDir)
    {
        var psi = new ProcessStartInfo(Env("METT_E2E_NETD")) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--name");
        psi.ArgumentList.Add(name);
        var p = Process.Start(psi)!;
        var line = await p.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) ?? throw new InvalidOperationException("mett-netd ohne ready");
        var ready = JsonNode.Parse(line)!.AsObject();
        return new Netd(p, (string)ready["addr"]!, (string)ready["token"]!);
    }

    private sealed record Netd(Process Process, string Addr, string Token) : IDisposable
    {
        public Dictionary<string, string> Env(string action, string? code)
        {
            var env = new Dictionary<string, string>
            {
                ["METT_NET_ADDR"] = Addr,
                ["METT_NET_TOKEN"] = Token,
                ["METT_NET_GAME"] = "chat",
                ["METT_NET_ACTION"] = action,
            };
            if (code is not null) env["METT_NET_CODE"] = code;
            return env;
        }

        public void Dispose()
        {
            try { Process.StandardInput.Close(); Process.WaitForExit(3000); } catch { }
            try { if (!Process.HasExited) Process.Kill(true); } catch { }
            Process.Dispose();
        }
    }

    private sealed class GameProcess : IDisposable
    {
        private readonly List<string> _lines = [];
        private readonly StringBuilder _all = new();
        public Process Process { get; }
        private readonly string _label;

        public GameProcess(Process p, string label)
        {
            Process = p;
            _label = label;
            p.OutputDataReceived += (_, e) => Add(e.Data);
            p.ErrorDataReceived += (_, e) => Add(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        private void Add(string? line)
        {
            if (line is null) return;
            lock (_lines) { _lines.Add(line); _all.AppendLine(line); }
        }

        public async Task<string> WaitForAsync(Func<string, bool> match, TimeSpan timeout)
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                lock (_lines)
                    if (_lines.FirstOrDefault(match) is { } hit) return hit;
                if (Process.HasExited) break;
                await Task.Delay(250);
            }
            string log;
            lock (_lines) log = _all.ToString();
            throw new TimeoutException($"[{_label}] erwartete Ausgabe kam nicht. Log:\n{log[^Math.Min(log.Length, 6000)..]}");
        }

        public async Task SendAsync(string line)
        {
            await Process.StandardInput.WriteLineAsync(line);
            await Process.StandardInput.FlushAsync();
        }

        public void Dispose()
        {
            try { if (!Process.HasExited) Process.Kill(true); } catch { }
            Process.Dispose();
        }
    }
}
