using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MettLauncher.Core;

namespace MettLauncher.App.Services;

/// <summary>
/// Startet und überwacht den Peer-to-Peer-Netzdienst <c>mett-netd</c> (liegt neben MettLauncher.exe).
/// Spiele bekommen über <see cref="GameEnvironment"/> Adresse und Token und sprechen dann selbst mit dem Dienst.
/// </summary>
public sealed class NetService : IDisposable
{
    private Process? _process;

    /// <summary>Ereignis-JSON von mett-netd (auf einem Hintergrund-Thread).</summary>
    public event Action<JsonObject>? EventReceived;

    public bool IsAvailable { get; private set; }
    public bool IsOnline { get; private set; }
    public string? Id { get; private set; }
    public string? Name { get; private set; }
    public string? Address { get; private set; }
    public string? Token { get; private set; }
    public string? StartError { get; private set; }

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "mett-netd.exe" : "mett-netd");

    public void Start(LauncherPaths paths, string? displayName)
    {
        if (!File.Exists(ExePath))
        {
            StartError = $"Netzdienst fehlt ({Path.GetFileName(ExePath)}).";
            return;
        }
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--data-dir");
            psi.ArgumentList.Add(paths.NetDir);
            psi.ArgumentList.Add("--name");
            psi.ArgumentList.Add(displayName ?? "");
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Start fehlgeschlagen");
            _process.ErrorDataReceived += (_, _) => { };
            _process.BeginErrorReadLine();
            _ = Task.Run(ReadEventsAsync);
        }
        catch (Exception ex)
        {
            StartError = $"Netzdienst konnte nicht starten: {ex.Message}";
        }
    }

    private async Task ReadEventsAsync()
    {
        var reader = _process!.StandardOutput;
        while (await reader.ReadLineAsync() is { } line)
        {
            JsonObject? ev;
            try { ev = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (ev is null) continue;
            switch ((string?)ev["event"])
            {
                case "ready":
                    Id = (string?)ev["id"];
                    Name = (string?)ev["name"];
                    Address = (string?)ev["addr"];
                    Token = (string?)ev["token"];
                    IsAvailable = true;
                    break;
                case "online":
                    IsOnline = true;
                    break;
            }
            EventReceived?.Invoke(ev);
        }
        IsAvailable = false;
        EventReceived?.Invoke(new JsonObject { ["event"] = "stopped" });
    }

    public void SetName(string? name)
    {
        Name = string.IsNullOrWhiteSpace(name) ? Name : name.Trim();
        Send(new JsonObject { ["cmd"] = "name", ["name"] = name ?? "" });
    }

    private void Send(JsonObject cmd)
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.StandardInput.WriteLine(cmd.ToJsonString());
                _process.StandardInput.Flush();
            }
        }
        catch (IOException) { }
    }

    /// <summary>Umgebungsvariablen für ein Spiel. <paramref name="action"/>: <c>host</c>, <c>join</c> oder <c>null</c>.</summary>
    public IReadOnlyDictionary<string, string>? GameEnvironment(string gameId, string? action = null, string? code = null)
    {
        if (!IsAvailable || Address is null || Token is null) return null;
        var env = new Dictionary<string, string>
        {
            ["METT_NET_ADDR"] = Address,
            ["METT_NET_TOKEN"] = Token,
            ["METT_NET_GAME"] = gameId,
        };
        if (action is not null) env["METT_NET_ACTION"] = action;
        if (code is not null) env["METT_NET_CODE"] = code;
        return env;
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                Send(new JsonObject { ["cmd"] = "quit" });
                if (!_process.WaitForExit(1500)) _process.Kill();
            }
        }
        catch (Exception) { }
        _process?.Dispose();
    }
}
