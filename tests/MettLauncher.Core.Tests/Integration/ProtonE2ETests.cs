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
/// lädt umu + GE-Proton und startet die Windows-Version des mett-net-Chat-Beispiels über den <see cref="ProtonRunner"/>
/// als Host – mit genau den Umgebungsvariablen, die der Launcher setzt. Ein nativer Linux-Chat tritt per Code bei.
/// Geprüft wird über das Netz statt über die Wine-Konsole: Host-Ereignis vom Netzdienst, Beitritt und die vom Host
/// (unter Proton) zurückgeschickte Nachricht.
/// Benötigt <c>METT_E2E_NETD</c>, <c>METT_E2E_CHAT_EXE</c> (Windows), <c>METT_E2E_CHAT_LINUX</c>, optional <c>METT_E2E_ROOT</c>.
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
        var game = new GameEntry { Id = "chat", Name = "Chat", Repo = "Mett-Gaming/none", AssetPattern = "x", Exe = Path.GetFileName(chatExe) };

        using var a = await Netd.StartAsync("Alice", Path.Combine(root, "net-a"));
        using var b = await Netd.StartAsync("Bob", Path.Combine(root, "net-b"));

        // Host unter Proton: METT_NET_ACTION=host -> chat.exe ruft net.host() auf.
        var psi = runner.CreateStartInfo(game, Path.GetDirectoryName(chatExe)!, chatExe, a.GameEnv("host", null));
        psi.RedirectStandardInput = true; // offen halten, damit der Chat nicht an EOF endet
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var host = new Watched(Process.Start(psi)!, "host-proton");

        // Beweis 1: Das Windows-Programm hat über Wine den nativen Dienst erreicht und hostet.
        var hosted = await a.WaitForEventAsync("hosted", TimeSpan.FromMinutes(20), host);
        var code = (string)hosted["code"]!;

        // Mitspieler nativ unter Linux.
        var jpsi = new ProcessStartInfo(Env("METT_E2E_CHAT_LINUX")) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var (k, v) in b.GameEnv("join", code)) jpsi.Environment[k] = v;
        using var join = new Watched(Process.Start(jpsi)!, "join-native");
        await join.WaitForAsync(l => l.Contains("[beigetreten] Host: Alice"), TimeSpan.FromMinutes(3));

        // Beweis 2: Peer-Verbindung zum Host unter Proton steht.
        await a.WaitForEventAsync("peers", TimeSpan.FromMinutes(1), host, e => (int?)e["count"] == 1);

        // Beweis 3: Host (unter Proton) empfängt die Zeile und verteilt sie zurück.
        await join.SendAsync("Hallo aus Wine");
        await join.WaitForAsync(l => l.Contains("#1: Hallo aus Wine"), TimeSpan.FromMinutes(1));
        await join.SendAsync("/quit");

        await runner.StopAsync(game, new Launch.GameSession("chat", host.Process.Id, DateTimeOffset.UtcNow,
            Task.FromResult(new Launch.GameExitInfo("chat", TimeSpan.Zero, 0))));
        Assert.True(Directory.Exists(runner.PrefixFor("chat")));
    }

    private sealed class Netd : IDisposable
    {
        private readonly List<JsonObject> _events = [];
        public Process Process { get; }
        public string Addr { get; private set; } = "";
        public string Token { get; private set; } = "";

        private Netd(Process p) => Process = p;

        public static async Task<Netd> StartAsync(string name, string dataDir)
        {
            var psi = new ProcessStartInfo(Env("METT_E2E_NETD")) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("--data-dir");
            psi.ArgumentList.Add(dataDir);
            psi.ArgumentList.Add("--name");
            psi.ArgumentList.Add(name);
            var n = new Netd(Process.Start(psi)!);
            n.Process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                try { if (JsonNode.Parse(e.Data) is JsonObject o) lock (n._events) n._events.Add(o); } catch { }
            };
            n.Process.BeginOutputReadLine();
            var ready = await n.WaitForEventAsync("ready", TimeSpan.FromSeconds(30));
            n.Addr = (string)ready["addr"]!;
            n.Token = (string)ready["token"]!;
            return n;
        }

        public async Task<JsonObject> WaitForEventAsync(string name, TimeSpan timeout, Watched? context = null, Func<JsonObject, bool>? where = null)
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                lock (_events)
                    if (_events.FirstOrDefault(e => (string?)e["event"] == name && (where?.Invoke(e) ?? true)) is { } hit) return hit;
                await Task.Delay(250);
            }
            string evs;
            lock (_events) evs = string.Join("\n", _events.Select(e => e.ToJsonString()));
            throw new TimeoutException($"Ereignis '{name}' kam nicht.\nmett-netd:\n{evs}\n{context?.Tail()}");
        }

        public Dictionary<string, string> GameEnv(string action, string? code)
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

    private sealed class Watched : IDisposable
    {
        private readonly List<string> _lines = [];
        private readonly string _label;
        public Process Process { get; }

        public Watched(Process p, string label)
        {
            Process = p;
            _label = label;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_lines) _lines.Add(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_lines) _lines.Add(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }

        public string Tail()
        {
            lock (_lines) return $"[{_label}] letzte Ausgabe:\n" + string.Join("\n", _lines.TakeLast(60));
        }

        public async Task WaitForAsync(Func<string, bool> match, TimeSpan timeout)
        {
            var end = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < end)
            {
                lock (_lines) if (_lines.Any(match)) return;
                if (Process.HasExited) break;
                await Task.Delay(250);
            }
            throw new TimeoutException($"erwartete Ausgabe kam nicht.\n{Tail()}");
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
