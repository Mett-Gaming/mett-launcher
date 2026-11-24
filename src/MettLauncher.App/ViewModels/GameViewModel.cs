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
using MettLauncher.Core.Launch;
using MettLauncher.Core.News;
using MettLauncher.Core.State;

namespace MettLauncher.App.ViewModels;

public enum GamePhase { Loading, NotInstalled, UpdateAvailable, Ready, Installing, Running }

/// <summary>Ein Spiel aus dem Katalog: Status, Hauptaktion, Infos, Aktivität, Versionen.</summary>
public sealed partial class GameViewModel : ObservableObject, IOverlayGame
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private IReadOnlyList<ReleaseInfo> _releases = [];
    private NewsFeed _news = new();
    private CancellationTokenSource? _installCts;
    /// <summary>Vom Klick auf „Spielen“ bis das Spiel läuft oder der Start abgebrochen ist.</summary>
    private bool _launching;

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

    // ---- Erfolge ----
    [ObservableProperty] public partial IReadOnlyList<AchievementViewModel> Achievements { get; set; } = [];
    public bool HasAchievements => Achievements.Count > 0;
    public int AchievementsUnlocked => Achievements.Count(a => a.Unlocked);
    public string AchievementsText => $"Freigeschaltet: {AchievementsUnlocked}/{Achievements.Count} ({Math.Floor(AchievementsFraction * 100):0} %)";
    public string AchievementsShort => $"{AchievementsUnlocked}/{Achievements.Count}";
    /// <summary>Zuletzt freigeschaltet (die Liste ist danach sortiert).</summary>
    public AchievementViewModel? LatestAchievement => Achievements.FirstOrDefault(a => a.Unlocked);
    public bool HasLatestAchievement => LatestAchievement is not null;
    public bool HasOpenAchievements => AchievementsUnlocked < Achievements.Count;
    public IEnumerable<AchievementViewModel> UnlockedPreview => Achievements.Where(a => a.Unlocked).Skip(1).Take(10);
    public IEnumerable<AchievementViewModel> OpenPreview => Achievements.Where(a => !a.Unlocked).Take(10);
    public string ShowAllAchievementsText => Achievements.Count - UnlockedPreview.Count() - OpenPreview.Count() - (HasLatestAchievement ? 1 : 0) is > 0 and var rest
        ? $"Alle anzeigen (+{rest})" : "Alle anzeigen";
    [RelayCommand] private void ShowAchievements() => _main.OpenAchievements(this);
    public double AchievementsFraction => HasAchievements ? (double)AchievementsUnlocked / Achievements.Count : 0;
    partial void OnAchievementsChanged(IReadOnlyList<AchievementViewModel> value)
    {
        foreach (var p in new[] { nameof(HasAchievements), nameof(AchievementsUnlocked), nameof(AchievementsText), nameof(AchievementsShort), nameof(AchievementsFraction), nameof(LatestAchievement),
                     nameof(HasLatestAchievement), nameof(HasOpenAchievements), nameof(UnlockedPreview), nameof(OpenPreview), nameof(ShowAllAchievementsText), nameof(SidebarCount) })
            OnPropertyChanged(p);
    }
    /// <summary>Zähler in der Bibliothek, wenn dort sonst nichts steht.</summary>
    public string? SidebarCount => SidebarStatus is null && HasAchievements && !SidebarDimmed ? $"{AchievementsUnlocked}/{Achievements.Count}" : null;

    public void RefreshAchievements()
    {
        Achievements = AchievementViewModel.All(_s.Achievements.Definitions(Id), _s.Achievements.States(Id));
        RebuildActivity();
    }

    public IReadOnlyList<MettLauncher.App.Views.OverlayNews> OverlayNews =>
        [.. Activity.Where(a => a.Kind != "ERFOLGE").Select(a => new MettLauncher.App.Views.OverlayNews(a.Title, a.Date.ToLocalTime().ToString("d. MMM", Formatting.De)))];
    public Task StopAsync() => _s.Launcher.StopAsync(Entry);

    public TimeSpan TotalPlaytime => _s.Library.GetGame(Id)?.TotalPlaytime ?? TimeSpan.Zero;

    [ObservableProperty] public partial Bitmap? Cover { get; set; }
    [ObservableProperty] public partial Bitmap? Hero { get; set; }
    [ObservableProperty] public partial GamePhase Phase { get; set; } = GamePhase.Loading;
    [ObservableProperty] public partial bool IsCheckingUpdate { get; set; }
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
    public string PrimaryLabel => IsCheckingUpdate ? "SUCHE NACH UPDATES …" : Phase switch
    {
        GamePhase.Loading => "LÄDT …",
        GamePhase.NotInstalled => "INSTALLIEREN",
        GamePhase.UpdateAvailable => "AKTUALISIEREN",
        GamePhase.Running => "BEENDEN",
        _ => "SPIELEN",
    };
    public string PrimaryIcon => IsCheckingUpdate ? "IconRefresh" : Phase switch
    {
        GamePhase.NotInstalled => "IconDownload",
        GamePhase.UpdateAvailable => "IconRefresh",
        GamePhase.Running => "IconStop",
        _ => "IconPlay",
    };
    public bool PrimaryIsMuted => IsCheckingUpdate || Phase is GamePhase.Running or GamePhase.Loading;
    public bool ShowPrimary => Phase is not GamePhase.Installing;
    public bool PrimaryFilledIcon => !IsCheckingUpdate && Phase is GamePhase.Ready;
    /// <summary>Update bekannt: neben „Aktualisieren“ bleibt das Spiel startbar (mit Nachfrage).</summary>
    public bool ShowSecondaryPlay => !IsCheckingUpdate && Phase is GamePhase.UpdateAvailable;

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

    partial void OnPhaseChanged(GamePhase value) => NotifyPhase();
    partial void OnIsCheckingUpdateChanged(bool value) => NotifyPhase();

    private void NotifyPhase()
    {
        foreach (var p in new[] { nameof(PrimaryLabel), nameof(PrimaryIcon), nameof(PrimaryIsMuted), nameof(ShowPrimary), nameof(PrimaryFilledIcon), nameof(ShowSecondaryPlay),
                     nameof(SidebarStatus), nameof(SidebarStatusIsHighlight), nameof(SidebarCount), nameof(SidebarDimmed), nameof(IsBusy), nameof(IsInstalled) })
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

        _s.Achievements.Reload(Id);
        RefreshAchievements();
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
        foreach (var day in Achievements.Where(a => a.Unlocked).GroupBy(a => a.UnlockedAt!.Value.ToLocalTime().Date))
            items.Add(new ActivityItemViewModel
            {
                Date = day.Max(a => a.UnlockedAt!.Value),
                Kind = "ERFOLGE",
                Title = day.Count() == 1 ? "Erfolg freigeschaltet" : $"{day.Count()} Erfolge freigeschaltet",
                Lines = [.. day.Take(6).Select(a => a.Name), .. day.Count() > 6 ? [$"und {day.Count() - 6} weitere"] : Array.Empty<string>()],
            });
        Activity.Clear();
        foreach (var i in items.OrderByDescending(i => i.Date).Take(12)) Activity.Add(i);
        OnPropertyChanged(nameof(HasActivity));
    }

    // ================= Aktionen =================

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (_launching) return;
        switch (Phase)
        {
            case GamePhase.NotInstalled:
            case GamePhase.UpdateAvailable:
                if (Status?.Latest is { } latest) await InstallVersionAsync(latest);
                else _main.ShowError($"Für {Name} gibt es noch kein installierbares Release.");
                break;
            case GamePhase.Ready:
                await PlayAsync();
                break;
            case GamePhase.Running:
                await _s.Launcher.StopAsync(Entry);
                break;
        }
    }

    // ---- Mehrspieler ----
    public bool IsMultiplayer => Entry.Multiplayer;
    [ObservableProperty] public partial string? SessionCode { get; set; }
    [ObservableProperty] public partial int PeerCount { get; set; }
    [ObservableProperty] public partial string? SessionText { get; set; }
    /// <summary>Namen der Mitspieler, soweit bekannt.</summary>
    public IReadOnlyList<string> Players { get; private set; } = [];
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
                Players = [];
                SessionText = "Du hostest · noch keine Mitspieler";
                break;
            case "peers":
                Players = data["names"] is System.Text.Json.Nodes.JsonArray names ? [.. names.Select(n => (string?)n ?? "")] : [];
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
                Players = [];
                SessionText = $"Verbunden mit {(string?)data["host"]}";
                break;
            case "ended":
                SessionCode = null;
                SessionText = null;
                Players = [];
                PeerCount = 0;
                break;
        }
    }

    [RelayCommand]
    private void HostGame() => _main.RequireDisplayName(() => StartMultiplayer("host", null));

    [RelayCommand]
    private void JoinGame() => _main.RequireDisplayName(() => _main.OpenJoinDialog(this));

    public void StartMultiplayer(string action, string? code)
    {
        if (Phase == GamePhase.Running) { _main.ShowError($"{Name} läuft bereits. Hosten/Beitreten bitte im Spiel oder nach dem Beenden."); return; }
        if (Phase == GamePhase.Installing) { _main.ShowError($"{Name} wird gerade installiert bzw. aktualisiert."); return; }
        if (Phase is not (GamePhase.Ready or GamePhase.UpdateAvailable)) { _main.ShowError($"{Name} muss zuerst installiert werden."); return; }
        if (_s.Net.GameEnvironment(Id, action, code) is null) { ShowNetNotReady(); return; }
        _ = PlayAsync(action, code);
    }

    [RelayCommand]
    public async Task CopyCodeAsync()
    {
        if (SessionCode is not null) await _main.CopyAsync(SessionCode);
    }

    private void ShowNetNotReady() => _main.ShowError(_s.Net.StartError ?? "Der Mehrspieler-Dienst ist noch nicht bereit.");

    [RelayCommand]
    private Task StartGameAsync() => PlayAsync();

    /// <summary>Einziger Startweg: erst nach Updates suchen und ggf. fragen, dann starten.</summary>
    private async Task PlayAsync(string? action = null, string? code = null)
    {
        if (_launching || Phase is not (GamePhase.Ready or GamePhase.UpdateAvailable)) return;
        _launching = true;
        IsCheckingUpdate = true;
        try
        {
            var start = await _s.LaunchGate.RunAsync(Entry, OnUpdateChecked,
                (offer, error) => _main.AskLaunchUpdateAsync(Name, offer, error, multiplayer: action is not null),
                release => InstallVersionAsync(release, reportError: false, closeDialogs: false));
            if (start) await LaunchAsync(action, code);
        }
        catch (Exception ex)
        {
            _main.ShowError(ex.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
            _launching = false;
        }
    }

    private bool OnUpdateChecked(LaunchUpdateCheck check)
    {
        IsCheckingUpdate = false;
        if (check.Releases is { } fresh)
        {
            _releases = fresh;
            Recompute();
        }
        // Während der Prüfung kann z. B. „Dateien überprüfen“ oder die Deinstallation gestartet worden sein.
        return Phase is GamePhase.Ready or GamePhase.UpdateAvailable;
    }

    private async Task LaunchAsync(string? action, string? code)
    {
        try
        {
            // Unter Linux: Proton/umu beim ersten Start einrichten (Download mit Fortschritt unten).
            if (UsesProton && !await _main.EnsureProtonAsync()) return;
            // Erst jetzt holen: ein Update kann gedauert haben.
            var env = IsMultiplayer || action is not null ? _s.Net.GameEnvironment(Id, action, code) : null;
            if (env is null && action is not null) { ShowNetNotReady(); return; }
            var session = _s.Launcher.Launch(Entry, _s.Overlay.Environment(Id, _s.Presence.Environment(Id, env)));
            Phase = GamePhase.Running;
            LastPlayedText = "Jetzt";
            _ = session.Exited.ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                SessionCode = null;
                SessionText = null;
                Phase = GamePhase.Ready;
                Recompute();
                if (t.IsCompletedSuccessfully) ReportSuspiciousExit(t.Result);
            }), TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _main.ShowError(ex.Message);
            Recompute();
        }
    }

    /// <summary>Spiel nach wenigen Sekunden mit Fehlercode beendet -> wahrscheinlich Startproblem.</summary>
    private void ReportSuspiciousExit(MettLauncher.Core.Launch.GameExitInfo exit)
    {
        if (exit.Duration > TimeSpan.FromSeconds(10) || exit.ExitCode is null or 0) return;
        var hint = UsesProton
            ? " Unter Einstellungen → Proton kannst du das Proton-Log einschalten oder eine andere Proton-Version wählen."
            : "";
        _main.ShowError($"{Name} wurde nach {exit.Duration.TotalSeconds:0} s mit Fehlercode {exit.ExitCode} beendet.{hint}");
    }

    // ---- Linux / Proton ----
    public bool UsesProton => _s.Launcher.RunnerFor(Entry) is MettLauncher.Core.Proton.ProtonRunner;
    public string RunnerText => _s.Launcher.RunnerFor(Entry).Describe(Entry);
    public bool ShowRunner => AppServices.IsLinux;

    [RelayCommand]
    private void ChooseProton() => _main.OpenProtonDialog(this);

    [RelayCommand]
    private void OpenPrefix()
    {
        var dir = _s.ProtonRunner.PrefixFor(Id);
        Directory.CreateDirectory(dir);
        Shell.OpenFolder(dir);
    }

    [RelayCommand]
    private void AskResetPrefix()
    {
        if (Phase == GamePhase.Running) { _main.ShowError($"{Name} läuft gerade."); return; }
        _main.Confirm($"Wine-Prefix von {Name} zurücksetzen?",
            "Der Prefix wird gelöscht und beim nächsten Start neu angelegt. Spielstände, die das Spiel im Prefix speichert, gehen dabei verloren.",
            "Zurücksetzen",
            () => { _s.ProtonRunner.ResetPrefix(Entry); return Task.CompletedTask; });
    }

    public void SetProtonVersion(string? name)
    {
        _s.Library.UpdateGame(Id, g => g.ProtonVersion = name);
        OnPropertyChanged(nameof(RunnerText));
    }

    public string? ProtonVersion => _s.Library.GetGame(Id)?.ProtonVersion;

    /// <param name="reportError">Fehler als Meldung anzeigen; sonst nur im Ergebnis zurückgeben.</param>
    /// <param name="closeDialogs"><c>false</c> für automatische Updates: offene Dialoge stehen lassen.</param>
    public async Task<LaunchUpdateResult> InstallVersionAsync(ResolvedRelease release, bool reportError = true, bool closeDialogs = true)
    {
        if (Phase == GamePhase.Installing) return new LaunchUpdateResult(LaunchUpdateOutcome.Cancelled);
        if (Phase == GamePhase.Running)
        {
            var running = $"{Name} läuft gerade. Bitte zuerst beenden.";
            if (reportError) _main.ShowError(running);
            return new LaunchUpdateResult(LaunchUpdateOutcome.Failed, running);
        }
        if (closeDialogs) _main.CloseDialogs();
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
                    if (p.FullBytes is { } full) ProgressText += $" (statt {Formatting.Size(full)})";
                    break;
                case InstallPhase.Verifying: ProgressText = "Prüfsumme wird geprüft …"; break;
                case InstallPhase.Extracting: ProgressText = "Wird entpackt …"; break;
                case InstallPhase.Finalizing: ProgressText = "Wird abgeschlossen …"; break;
            }
        });
        try
        {
            await _s.Installer.InstallAsync(Entry, release, progress, _installCts.Token);
            return new LaunchUpdateResult(LaunchUpdateOutcome.Installed);
        }
        catch (OperationCanceledException)
        {
            // abgebrochen – alte Installation bleibt
            return new LaunchUpdateResult(LaunchUpdateOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            if (reportError) _main.ShowError($"{Name}: {ex.Message}");
            return new LaunchUpdateResult(LaunchUpdateOutcome.Failed, ex.Message);
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
        // Wird das Spiel gerade gestartet, entscheidet die Nachfrage vor dem Start.
        if (_launching || !_s.Settings.Current.AutoUpdate || Phase != GamePhase.UpdateAvailable || Status?.Latest is null) return;
        await InstallVersionAsync(Status.Latest, closeDialogs: false);
    }
}
