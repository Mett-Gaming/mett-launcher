using Velopack;
using Velopack.Sources;

namespace MettLauncher.App.Services;

/// <summary>Self-Update über Velopack aus den GitHub-Releases von <c>Mett-Gaming/mett-launcher</c>.</summary>
public sealed class LauncherUpdateService
{
    public const string RepoUrl = "https://github.com/Mett-Gaming/mett-launcher";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public LauncherUpdateService()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
        }
        catch (Exception)
        {
            _manager = null;
        }
    }

    /// <summary>Nur eine per Setup installierte Kopie kann sich selbst aktualisieren (nicht beim Start aus Visual Studio).</summary>
    public bool IsInstalled => _manager?.IsInstalled == true;

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? typeof(LauncherUpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Prüft und lädt im Hintergrund. Liefert die neue Version, wenn ein Neustart sie aktiviert.</summary>
    public async Task<string?> CheckAndDownloadAsync()
    {
        if (_manager is null || !IsInstalled) return null;
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;
        await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
        _pending = info;
        return info.TargetFullRelease.Version.ToString();
    }

    public void ApplyAndRestart()
    {
        if (_manager is not null && _pending is not null)
            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
