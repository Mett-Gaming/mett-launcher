using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MettLauncher.App.Services;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.Install;
using MettLauncher.Core.News;
using MettLauncher.Core.State;

namespace MettLauncher.App.ViewModels;

public enum GamePhase { Loading, NotInstalled, UpdateAvailable, Ready, Installing, Running }

/// <summary>Ein Spiel aus dem Katalog: Status, Hauptaktion, Infos, Aktivität, Versionen.</summary>
public sealed partial class GameViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private IReadOnlyList<ReleaseInfo> _releases = [];
    private NewsFeed _news = new();
    private CancellationTokenSource? _installCts;

    public GameViewModel(AppServices services, MainViewModel main, GameEntry entry)
    {
        _s = services;
        _main = main;
        Entry = entry;
        Recompute();
    }

    public GameEntry Entry { get; private set; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string? Tagline => Entry.Tagline;
    public string RepoUrl => $"https://github.com/{Entry.Repo}";
    public string Developer => Entry.Repo.Split('/')[0].Replace('-', ' ');

    public ObservableCollection<ActivityItemViewModel> Activity { get; } = [];
    public ObservableCollection<VersionItemViewModel> Versions { get; } = [];

    [ObservableProperty] public partial Bitmap? Cover { get; set; }
    [ObservableProperty] public partial Bitmap? Hero { get; set; }
    [ObservableProperty] public partial GamePhase Phase { get; set; } = GamePhase.Loading;
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string ProgressText { get; set; } = "";
    [ObservableProperty] public partial string PercentText { get; set; } = "";
    [ObservableProperty] public partial string LastPlayedText { get; set; } = "Noch nie";
    [ObservableProperty] public partial string PlaytimeText { get; set; } = "–";
    [ObservableProperty] public partial string VersionText { get; set; } = "–";
    [ObservableProperty] public partial string SizeText { get; set; } = "–";
    [ObservableProperty] public partial string ReleasedText { get; set; } = "–";
    [ObservableProperty] public partial string? FolderText { get; set; }
    [ObservableProperty] public partial bool AutoUpdate { get; set; } = true;
    [ObservableProperty] public partial GameStatusInfo? Status { get; set; }

    public bool HasHero => Hero is not null;
    public bool HasCover => Cover is not null;
    public bool IsInstalled => _s.Library.GetGame(Id)?.IsInstalled == true;
    public bool IsBusy => Phase is GamePhase.Installing;
    public bool HasFolder => FolderText is not null;
    public bool HasActivity => Activity.Count > 0;

    // ---- Hauptbutton ----
    public string PrimaryLabel => Phase switch
    {
        GamePhase.Loading => "LÄDT …",
        GamePhase.NotInstalled => "INSTALLIEREN",
        GamePhase.UpdateAvailable => "AKTUALISIEREN",
        GamePhase.Running => "BEENDEN",
        _ => "SPIELEN",
    };
    public string PrimaryIcon => Phase switch
    {
        GamePhase.NotInstalled => "IconDownload",
        GamePhase.UpdateAvailable => "IconRefresh",
        GamePhase.Running => "IconStop",
        _ => "IconPlay",
    };
    public bool PrimaryIsMuted => Phase is GamePhase.Running or GamePhase.Loading;
    public bool ShowPrimary => Phase is not GamePhase.Installing;
    public bool PrimaryFilledIcon => Phase is GamePhase.Ready;

    // ---- Sidebar ----
    public string? SidebarStatus => Phase switch
    {
        GamePhase.UpdateAvailable => "Update",
        GamePhase.Installing => PercentText,
        GamePhase.Running => "Läuft",
        _ => null,
    };
    public bool SidebarStatusIsHighlight => Phase == GamePhase.UpdateAvailable;
    public bool SidebarDimmed => Phase is GamePhase.NotInstalled or GamePhase.Loading;

    partial void OnPhaseChanged(GamePhase value)
    {
        foreach (var p in new[] { nameof(PrimaryLabel), nameof(PrimaryIcon), nameof(PrimaryIsMuted), nameof(ShowPrimary), nameof(PrimaryFilledIcon),
                     nameof(SidebarStatus), nameof(SidebarStatusIsHighlight), nameof(SidebarDimmed), nameof(IsBusy), nameof(IsInstalled) })
            OnPropertyChanged(p);
    }
    partial void OnPercentTextChanged(string value) => OnPropertyChanged(nameof(SidebarStatus));
    partial void OnHeroChanged(Bitmap? value) => OnPropertyChanged(nameof(HasHero));
    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCover));
    partial void OnFolderTextChanged(string? value) => OnPropertyChanged(nameof(HasFolder));

    public void UpdateEntry(GameEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(string.Empty);
    }

    public async Task LoadImagesAsync()
    {
        Cover = await _s.Images.GetAsync(_s.Catalog.ResolveAssetUrl(Entry.Cover));
        Hero = await _s.Images.GetAsync(_s.Catalog.ResolveAssetUrl(Entry.Hero));
    }

    /// <summary>Holt Releases (mit ETag-Cache) und berechnet den Status neu.</summary>
    public async Task<ReleaseSource?> RefreshReleasesAsync()
    {
        try
        {
            var result = await _s.Releases.GetReleasesAsync(Entry.Repo);
            _releases = result.Releases;
            Recompute();
            return result.Source;
        }
        catch (Exception)
        {
            Recompute();
            return null;
        }
    }

    public void SetNews(NewsFeed feed)
    {
        _news = feed;
        RebuildActivity();
    }

    /// <summary>Status, Texte, Versionsliste und Aktivität aus State + Releases ableiten.</summary>
    public void Recompute()
    {
        var state = _s.Library.GetGame(Id);
        var include = _s.Settings.Current.IncludePrereleases || Entry.IncludePrereleases;
        Status = GameStatus.Compute(Entry, state, _releases, include);

        if (Phase is not (GamePhase.Installing or GamePhase.Running))
        {
            Phase = _s.Launcher.IsRunning(Id) ? GamePhase.Running
                : Status.Kind switch
                {
                    GameStatusKind.NotInstalled => _releases.Count == 0 && state is null ? GamePhase.NotInstalled : GamePhase.NotInstalled,
                    GameStatusKind.UpdateAvailable => GamePhase.UpdateAvailable,
                    _ => GamePhase.Ready,
                };
        }

        PlaytimeText = Formatting.Playtime(state?.TotalPlaytime ?? TimeSpan.Zero);
        LastPlayedText = Phase == GamePhase.Running ? "Jetzt" : Formatting.LastPlayed(state?.LastPlayed, DateTimeOffset.Now);
        AutoUpdate = state?.PinnedTag is null;
        FolderText = state is { IsInstalled: true } ? state.InstallPath : null;

        var installedTag = state?.IsInstalled == true ? state.InstalledTag : null;
        var current = (installedTag is not null ? ReleaseSelector.FindByTag(_releases, Entry, installedTag) : null) ?? Status.Latest;
        VersionText = installedTag is not null && Status.Kind == GameStatusKind.UpdateAvailable && Status.Latest is not null
            ? $"{installedTag} → {Status.Latest.Tag}"
            : installedTag ?? Status.Latest?.Tag ?? "–";
        SizeText = current is not null ? Formatting.Size(current.Asset.Size) : "–";
        var first = _releases.Where(r => r.PublishedAt is not null && !r.Draft).MinBy(r => r.PublishedAt);
        ReleasedText = first?.PublishedAt is { } d ? Formatting.Date(d) : "–";

        Versions.Clear();
        foreach (var r in ReleaseSelector.Installable(_releases, Entry, include))
            Versions.Add(new VersionItemViewModel(this, r,
                isInstalled: string.Equals(r.Tag, installedTag, StringComparison.OrdinalIgnoreCase),
                isPinned: string.Equals(r.Tag, state?.PinnedTag, StringComparison.OrdinalIgnoreCase)));

        RebuildActivity();
        OnPropertyChanged(nameof(IsInstalled));
    }

    private void RebuildActivity()
    {
        var items = new List<ActivityItemViewModel>();
        var newsTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in NewsService.ForGame(_news, Id))
        {
            newsTags.Add(n.Id);
            items.Add(new ActivityItemViewModel
            {
                Date = n.Date,
                Kind = n.Kind.ToUpperInvariant() == "PATCHNOTES" ? "PATCHNOTES" : n.Kind.ToUpperInvariant(),
                Title = n.Title,
                Lines = n.Body is not null ? Formatting.NoteLines(n.Body) : n.Excerpt is not null ? [n.Excerpt] : [],
                Url = n.Url,
            });
        }
        foreach (var r in _releases.Where(r => !r.Draft && r.PublishedAt is not null))
        {
            if (newsTags.Contains($"{Id}-{r.TagName}")) continue; // eigene News ersetzt das Roh-Release
            items.Add(new ActivityItemViewModel
            {
                Date = r.PublishedAt!.Value,
                Kind = "UPDATE",
                Title = $"{Name} {r.TagName}",
                Lines = Formatting.NoteLines(r.Body, 5),
                Url = r.HtmlUrl,
            });
        }
        Activity.Clear();
        foreach (var i in items.OrderByDescending(i => i.Date).Take(12)) Activity.Add(i);
        OnPropertyChanged(nameof(HasActivity));
    }

    // ================= Aktionen =================

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        switch (Phase)
        {
            case GamePhase.NotInstalled:
            case GamePhase.UpdateAvailable:
                if (Status?.Latest is { } latest) await InstallVersionAsync(latest);
                else _main.ShowError($"Für {Name} gibt es noch kein installierbares Release.");
                break;
            case GamePhase.Ready:
                Play();
                break;
            case GamePhase.Running:
                Stop();
                break;
        }
    }

    // ---- Mehrspieler ----
    public bool IsMultiplayer => Entry.Multiplayer;
    [ObservableProperty] public partial string? SessionCode { get; set; }
    [ObservableProperty] public partial int PeerCount { get; set; }
    [ObservableProperty] public partial string? SessionText { get; set; }
    public bool HasSession => SessionText is not null;
    public bool IsHosting => SessionCode is not null;
    public string SessionCodeDisplay => SessionCode is null ? "" : string.Join(" ", Enumerable.Range(0, (SessionCode.Length + 7) / 8)
        .Select(i => SessionCode.Substring(i * 8, Math.Min(8, SessionCode.Length - i * 8))).Take(4)) + (SessionCode.Length > 32 ? " …" : "");
    partial void OnSessionTextChanged(string? value) => OnPropertyChanged(nameof(HasSession));
    partial void OnSessionCodeChanged(string? value) { OnPropertyChanged(nameof(IsHosting)); OnPropertyChanged(nameof(SessionCodeDisplay)); }

    /// <summary>Vom Netzdienst gemeldete Sitzungsereignisse.</summary>
    public void OnNetEvent(string ev, System.Text.Json.Nodes.JsonObject data)
    {
        switch (ev)
        {
            case "hosted":
                SessionCode = (string?)data["code"];
                PeerCount = 0;
                SessionText = "Du hostest · noch keine Mitspieler";
                break;
            case "peers":
                PeerCount = (int?)data["count"] ?? 0;
                SessionText = PeerCount switch
                {
                    0 => "Du hostest · noch keine Mitspieler",
                    1 => "Du hostest · 1 Mitspieler",
                    var n => $"Du hostest · {n} Mitspieler",
                };
                break;
            case "joined":
                SessionCode = null;
                SessionText = $"Verbunden mit {(string?)data["host"]}";
                break;
            case "ended":
                SessionCode = null;
                SessionText = null;
                PeerCount = 0;
                break;
        }
    }

    [RelayCommand]
    private void HostGame() => StartMultiplayer("host", null);

    [RelayCommand]
    private void JoinGame() => _main.OpenJoinDialog(this);

    public void StartMultiplayer(string action, string? code)
    {
        if (Phase == GamePhase.Running) { _main.ShowError($"{Name} läuft bereits. Hosten/Beitreten bitte im Spiel oder nach dem Beenden."); return; }
        if (Phase != GamePhase.Ready) { _main.ShowError($"{Name} muss zuerst installiert bzw. aktualisiert werden."); return; }
        var env = _s.Net.GameEnvironment(Id, action, code);
        if (env is null) { _main.ShowError(_s.Net.StartError ?? "Der Mehrspieler-Dienst ist noch nicht bereit."); return; }
        Play(env);
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        if (SessionCode is not null) await _main.CopyAsync(SessionCode);
    }

    private void Play(IReadOnlyDictionary<string, string>? env = null)
    {
        try
        {
            env ??= IsMultiplayer ? _s.Net.GameEnvironment(Id) : null;
            var session = _s.Launcher.Launch(Entry, env);
            Phase = GamePhase.Running;
            LastPlayedText = "Jetzt";
            _ = session.Exited.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                SessionCode = null;
                SessionText = null;
                Phase = GamePhase.Ready;
                Recompute();
            }), TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _main.ShowError(ex.Message);
            Recompute();
        }
    }

    private void Stop()
    {
        var session = _s.Launcher.GetSession(Id);
        if (session is null) return;
        try
        {
            using var p = Process.GetProcessById(session.ProcessId);
            if (!p.CloseMainWindow() || !p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Prozess schon weg
        }
    }

    public async Task InstallVersionAsync(ResolvedRelease release)
    {
        if (Phase == GamePhase.Installing) return;
        if (Phase == GamePhase.Running) { _main.ShowError($"{Name} läuft gerade. Bitte zuerst beenden."); return; }
        _main.CloseDialogs();
        _installCts = new CancellationTokenSource();
        Phase = GamePhase.Installing;
        Progress = 0;
        PercentText = "0 %";
        ProgressText = "Wird vorbereitet …";
        _main.ActiveDownload = this;
        var progress = new Progress<InstallProgress>(p =>
        {
            switch (p.Phase)
            {
                case InstallPhase.Downloading:
                    Progress = p.Fraction ?? 0;
                    PercentText = $"{(int)(Progress * 100)} %";
                    ProgressText = p.TotalBytes is { } t
                        ? $"{Formatting.Size(p.BytesReceived)} / {Formatting.Size(t)}"
                        : Formatting.Size(p.BytesReceived);
                    break;
                case InstallPhase.Verifying: ProgressText = "Prüfsumme wird geprüft …"; break;
                case InstallPhase.Extracting: ProgressText = "Wird entpackt …"; break;
                case InstallPhase.Finalizing: ProgressText = "Wird abgeschlossen …"; break;
            }
        });
        try
        {
            await _s.Installer.InstallAsync(Entry, release, progress, _installCts.Token);
        }
        catch (OperationCanceledException)
        {
            // abgebrochen – alte Installation bleibt
        }
        catch (Exception ex)
        {
            _main.ShowError($"{Name}: {ex.Message}");
        }
        finally
        {
            _installCts.Dispose();
            _installCts = null;
            if (_main.ActiveDownload == this) _main.ActiveDownload = null;
            Phase = GamePhase.Loading; // erzwingt Neuberechnung
            Recompute();
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCts?.Cancel();

    [RelayCommand]
    private void OpenFolder()
    {
        if (FolderText is not null) Shell.OpenFolder(FolderText);
    }

    [RelayCommand]
    private void OpenRepo() => Shell.OpenUrl(RepoUrl);

    [RelayCommand]
    private async Task VerifyAsync()
    {
        var tag = _s.Library.GetGame(Id)?.InstalledTag;
        var r = tag is not null ? ReleaseSelector.FindByTag(_releases, Entry, tag) : null;
        if (r is null) { _main.ShowError("Die installierte Version ist auf GitHub nicht mehr verfügbar."); return; }
        await InstallVersionAsync(r);
    }

    [RelayCommand]
    private void ShowVersions() => _main.OpenVersionDialog(this);

    [RelayCommand]
    private void ToggleAutoUpdate() => TogglePin();

    /// <summary>Festpinnen = keine automatischen Updates für die installierte Version.</summary>
    public void TogglePin()
    {
        var state = _s.Library.GetGame(Id);
        if (state is not { IsInstalled: true }) return;
        _s.Library.UpdateGame(Id, g => g.PinnedTag = g.PinnedTag is null ? g.InstalledTag : null);
        Recompute();
    }

    [RelayCommand]
    private void AskUninstall()
    {
        if (!IsInstalled) return;
        _main.Confirm($"{Name} deinstallieren?",
            $"Der Spielordner ({SizeText}) wird gelöscht. Spielstände und Einstellungen des Spiels in %APPDATA% bleiben erhalten.",
            "Deinstallieren",
            async () =>
            {
                try { await _s.Installer.UninstallAsync(Entry); }
                catch (Exception ex) { _main.ShowError(ex.Message); }
                Phase = GamePhase.Loading;
                Recompute();
            });
    }

    /// <summary>Automatisches Update beim Start, wenn eingeschaltet und das Spiel nicht festgepinnt ist.</summary>
    public async Task AutoUpdateIfNeededAsync()
    {
        if (!_s.Settings.Current.AutoUpdate || Phase != GamePhase.UpdateAvailable || Status?.Latest is null) return;
        await InstallVersionAsync(Status.Latest);
    }
}
