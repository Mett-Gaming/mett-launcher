using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MettLauncher.App.Services;
using MettLauncher.Core.Catalog;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.News;

namespace MettLauncher.App.ViewModels;

public enum Section { Library, News, Settings }
public enum SettingsCategory { Library, Updates, Multiplayer, Proton, About }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private NewsFeed _newsFeed = new();
    private Func<Task>? _confirmAction;

    public MainViewModel(AppServices services)
    {
        _s = services;
        LibraryDir = _s.Settings.LibraryDir;
        AutoUpdateGames = _s.Settings.Current.AutoUpdate;
        IncludePrereleases = _s.Settings.Current.IncludePrereleases;
        LauncherVersion = _s.LauncherUpdates.CurrentVersion;
        _s.Launcher.GameExited += (_, _) => Dispatcher.UIThread.Post(() => { foreach (var g in Games) g.Recompute(); });
        DisplayName = _s.Settings.Current.DisplayName ?? "";
        ProtonLog = _s.Settings.Current.ProtonLog;
        _s.Net.EventReceived += ev => Dispatcher.UIThread.Post(() => OnNetEvent(ev));
        UpdateNetStatus();
    }

    // ================= Linux / Proton =================

    public bool IsLinux => AppServices.IsLinux;
    public string MultiplayerHint => "Ohne Account und ohne eigenen Server: Spiele verbinden sich direkt (Peer-to-Peer, verschlüsselt). Klappt das wegen eines Routers nicht, läuft der Verkehr verschlüsselt über ein Relay von n0 (iroh)."
        + (OperatingSystem.IsWindows() ? " Fragt Windows nach Netzwerkzugriff für mett-netd, erlaube ihn für private Netzwerke, damit direkte Verbindungen klappen." : "");

    // Hilfsprogramm-Downloads (Proton, umu) teilen sich die Download-Leiste mit Spielen.
    [ObservableProperty] public partial string? ToolDownloadText { get; set; }
    [ObservableProperty] public partial string ToolDownloadDetail { get; set; } = "";
    [ObservableProperty] public partial double ToolDownloadProgress { get; set; }
    public bool HasToolDownload => ToolDownloadText is not null && ActiveDownload is null;
    partial void OnToolDownloadTextChanged(string? value) { OnPropertyChanged(nameof(HasToolDownload)); OnPropertyChanged(nameof(ShowIdleDownloads)); }
    private Task<bool>? _protonSetup;

    private Progress<MettLauncher.Core.IO.DownloadProgress> ToolProgress() => new(p =>
    {
        ToolDownloadProgress = p.Fraction ?? 0;
        ToolDownloadDetail = p.Total is { } total ? $"{Formatting.Size(p.Received)} / {Formatting.Size(total)}" : Formatting.Size(p.Received);
    });

    /// <summary>Richtet umu und (falls nötig) GE-Proton ein. Läuft nur einmal gleichzeitig; Fehler werden angezeigt.</summary>
    public Task<bool> EnsureProtonAsync() =>
        _protonSetup is { IsCompleted: false } running ? running : (_protonSetup = SetupProtonAsync());

    private async Task<bool> SetupProtonAsync()
    {
        try
        {
            if (_s.Umu.Resolve() is null)
            {
                ToolDownloadText = "PROTON · umu-launcher wird eingerichtet";
                await _s.Umu.EnsureAsync(ToolProgress());
            }
            if (_s.Proton.ListManaged().Count == 0 && _s.Proton.ListSteam().Count == 0)
            {
                ToolDownloadText = "PROTON · GE-Proton wird geladen (einmalig, ca. 600 MB)";
                await _s.Proton.EnsureAsync(ToolProgress());
            }
            RefreshProtonInfo();
            foreach (var g in Games) g.Recompute();
            return true;
        }
        catch (Exception ex)
        {
            ShowError($"Proton konnte nicht eingerichtet werden: {ex.Message}");
            return false;
        }
        finally
        {
            ToolDownloadText = null;
            ToolDownloadDetail = "";
            ToolDownloadProgress = 0;
        }
    }

    // ---- Dialog „Proton-Version“ pro Spiel ----
    [ObservableProperty] public partial GameViewModel? ProtonDialogGame { get; set; }
    public ObservableCollection<ProtonOptionViewModel> ProtonOptions { get; } = [];
    public bool IsProtonDialogOpen => ProtonDialogGame is not null;
    partial void OnProtonDialogGameChanged(GameViewModel? value) { OnPropertyChanged(nameof(IsProtonDialogOpen)); OnPropertyChanged(nameof(IsAnyDialogOpen)); }

    public void OpenProtonDialog(GameViewModel game)
    {
        ProtonOptions.Clear();
        var current = game.ProtonVersion;
        var newest = _s.Proton.ListManaged().FirstOrDefault();
        ProtonOptions.Add(new ProtonOptionViewModel(this, null,
            newest is null ? "Neueste GE-Proton (wird beim Start geladen)" : $"Neueste GE-Proton (aktuell {newest.Name})", current is null));
        foreach (var p in _s.Proton.ListAll())
            ProtonOptions.Add(new ProtonOptionViewModel(this, p.Name, p.Display, string.Equals(current, p.Name, StringComparison.Ordinal)));
        ProtonDialogGame = game;
    }

    public void SelectProton(string? name)
    {
        ProtonDialogGame?.SetProtonVersion(name);
        CloseDialogs();
    }

    // ---- Einstellungen → Proton ----
    [ObservableProperty] public partial string ProtonInfo { get; set; } = "";
    [ObservableProperty] public partial string UmuInfo { get; set; } = "";
    [ObservableProperty] public partial string SteamProtonInfo { get; set; } = "";
    [ObservableProperty] public partial string ProtonUpdateStatus { get; set; } = "";
    [ObservableProperty] public partial bool ProtonLog { get; set; }
    partial void OnProtonLogChanged(bool value) => _s.Settings.Update(s => s with { ProtonLog = value });

    private void RefreshProtonInfo()
    {
        if (!IsLinux) return;
        var managed = _s.Proton.ListManaged();
        ProtonInfo = managed.Count == 0
            ? "Noch keine GE-Proton geladen – passiert automatisch beim ersten Spielstart."
            : string.Join(", ", managed.Select(m => m.Name));
        var steam = _s.Proton.ListSteam();
        SteamProtonInfo = steam.Count == 0 ? "Keine gefunden." : string.Join(", ", steam.Select(m => m.Name));
        var umu = _s.Umu.Resolve();
        UmuInfo = umu switch
        {
            null => _s.Umu.HasPython ? "Wird beim ersten Spielstart eingerichtet." : "python3 fehlt – bitte über die Paketverwaltung installieren.",
            { Source: MettLauncher.Core.Proton.UmuSource.System } => $"System-Installation ({umu.Path})",
            _ => $"Vom Launcher verwaltet ({umu.Version ?? "?"})",
        };
    }

    [RelayCommand]
    private async Task UpdateProtonAsync()
    {
        ProtonUpdateStatus = "Wird geprüft …";
        try
        {
            var update = await _s.Proton.CheckForUpdateAsync();
            if (update is null) { ProtonUpdateStatus = "Du hast die neueste GE-Proton."; return; }
            ToolDownloadText = $"PROTON · {update.Tag} wird geladen";
            await _s.Proton.InstallAsync(update, ToolProgress());
            ProtonUpdateStatus = $"{update.Tag} installiert. Spiele ohne feste Version nutzen sie ab dem nächsten Start.";
        }
        catch (Exception ex) { ProtonUpdateStatus = $"Fehlgeschlagen: {ex.Message}"; }
        finally
        {
            ToolDownloadText = null;
            ToolDownloadDetail = "";
            RefreshProtonInfo();
            foreach (var g in Games) g.Recompute();
        }
    }

    [RelayCommand]
    private void OpenProtonLogs()
    {
        Directory.CreateDirectory(_s.Paths.LogDir);
        Shell.OpenFolder(_s.Paths.LogDir);
    }


    // ================= Mehrspieler =================

    [ObservableProperty] public partial string DisplayName { get; set; }
    [ObservableProperty] public partial string NetStatus { get; set; } = "";
    [ObservableProperty] public partial GameViewModel? JoinDialogGame { get; set; }
    [ObservableProperty] public partial string JoinCode { get; set; } = "";

    public bool IsJoinDialogOpen => JoinDialogGame is not null;
    public string NetId => _s.Net.Id ?? "–";

    /// <summary>Von der View gesetzt (Zwischenablage braucht das Fenster).</summary>
    public Func<string, Task>? CopyText { get; set; }

    partial void OnJoinDialogGameChanged(GameViewModel? value) { OnPropertyChanged(nameof(IsJoinDialogOpen)); OnPropertyChanged(nameof(IsAnyDialogOpen)); }

    private void OnNetEvent(System.Text.Json.Nodes.JsonObject ev)
    {
        var name = (string?)ev["event"] ?? "";
        var game = (string?)ev["game"];
        if (game is not null && Games.FirstOrDefault(g => g.Id.Equals(game, StringComparison.OrdinalIgnoreCase)) is { } vm)
            vm.OnNetEvent(name, ev);
        // Ein Spiel wollte hosten/beitreten, aber es gibt noch keinen Namen -> Dialog zeigen.
        if (name == "name_required" && !IsNameDialogOpen)
            RequireDisplayName(null, "Ein Spiel möchte den Mehrspieler nutzen. Lege dafür einen Anzeigenamen fest – danach im Spiel erneut versuchen.");
        UpdateNetStatus();
    }

    private void UpdateNetStatus()
    {
        NetStatus = _s.Net.StartError
            ?? (!_s.Net.IsAvailable ? "Wird gestartet …"
                : !HasDisplayName ? "Kein Anzeigename festgelegt – Hosten und Beitreten sind erst danach möglich."
                : _s.Net.IsOnline ? $"Bereit · du erscheinst als „{_s.Settings.Current.DisplayName}“"
                : "Bereit, sucht noch eine Verbindung ins Internet …");
        OnPropertyChanged(nameof(NetId));
    }

    /// <summary>Gleiche Regel wie in mett-netd: 2–24 sichtbare Zeichen, keine Steuerzeichen.</summary>
    public static string? ValidateDisplayName(string? raw)
    {
        if (raw is null) return null;
        var n = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        var len = new System.Globalization.StringInfo(n).LengthInTextElements;
        return len is >= 2 and <= 24 ? n : null;
    }

    public bool HasDisplayName => ValidateDisplayName(_s.Settings.Current.DisplayName) is not null;

    private bool ApplyDisplayName(string raw, out string? error)
    {
        var name = ValidateDisplayName(raw);
        if (name is null)
        {
            error = "Der Anzeigename braucht 2 bis 24 Zeichen.";
            return false;
        }
        _s.Settings.Update(s => s with { DisplayName = name });
        _s.Net.SetName(name);
        DisplayName = name;
        error = null;
        UpdateNetStatus();
        return true;
    }

    [ObservableProperty] public partial string? DisplayNameError { get; set; }

    [RelayCommand]
    private void SaveDisplayName()
    {
        DisplayNameError = ApplyDisplayName(DisplayName, out var error) ? null : error;
    }

    // ---- Pflichtdialog „Anzeigename festlegen“ ----
    private Action? _afterName;
    [ObservableProperty] public partial bool IsNameDialogOpen { get; set; }
    [ObservableProperty] public partial string NameDialogInput { get; set; } = "";
    [ObservableProperty] public partial string NameDialogText { get; set; } = "";
    [ObservableProperty] public partial string? NameDialogError { get; set; }
    partial void OnIsNameDialogOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));

    /// <summary>Führt <paramref name="then"/> aus, sobald ein gültiger Anzeigename feststeht (ggf. nach dem Dialog).</summary>
    public void RequireDisplayName(Action? then, string? text = null)
    {
        if (then is not null && HasDisplayName) { then(); return; }
        _afterName = then;
        NameDialogInput = _s.Settings.Current.DisplayName ?? "";
        NameDialogError = null;
        NameDialogText = text ?? "Für den Mehrspieler brauchst du einen Anzeigenamen. So sehen dich deine Mitspieler.";
        IsNameDialogOpen = true;
    }

    [RelayCommand]
    private void ConfirmName()
    {
        if (!ApplyDisplayName(NameDialogInput, out var error)) { NameDialogError = error; return; }
        var then = _afterName;
        _afterName = null;
        IsNameDialogOpen = false;
        then?.Invoke();
    }

    public void OpenJoinDialog(GameViewModel game)
    {
        JoinCode = "";
        JoinDialogGame = game;
    }

    [RelayCommand]
    private void ConfirmJoin()
    {
        var game = JoinDialogGame;
        var code = JoinCode.Trim();
        if (game is null || code.Length == 0) return;
        CloseDialogs();
        game.StartMultiplayer("join", code);
    }

    [RelayCommand]
    private async Task CopyOwnIdAsync()
    {
        if (_s.Net.Id is { } id) await CopyAsync(id);
    }

    public async Task CopyAsync(string text)
    {
        if (CopyText is not null) await CopyText(text);
    }

    public ObservableCollection<GameViewModel> Games { get; } = [];
    public ObservableCollection<GameViewModel> FilteredGames { get; } = [];
    public ObservableCollection<NewsItemViewModel> NewsItems { get; } = [];

    [ObservableProperty] public partial Section Section { get; set; } = Section.Library;
    [ObservableProperty] public partial GameViewModel? SelectedGame { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool IsOffline { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string? ErrorText { get; set; }
    [ObservableProperty] public partial string? PendingLauncherVersion { get; set; }
    [ObservableProperty] public partial GameViewModel? ActiveDownload { get; set; }
    [ObservableProperty] public partial GameViewModel? VersionDialogGame { get; set; }
    [ObservableProperty] public partial bool IsConfirmOpen { get; set; }
    [ObservableProperty] public partial string ConfirmTitle { get; set; } = "";
    [ObservableProperty] public partial string ConfirmText { get; set; } = "";
    [ObservableProperty] public partial string ConfirmButton { get; set; } = "OK";
    [ObservableProperty] public partial SettingsCategory SettingsCategory { get; set; } = SettingsCategory.Library;
    [ObservableProperty] public partial string LibraryDir { get; set; }
    [ObservableProperty] public partial bool AutoUpdateGames { get; set; }
    [ObservableProperty] public partial bool IncludePrereleases { get; set; }
    [ObservableProperty] public partial string LauncherVersion { get; set; }
    [ObservableProperty] public partial string LauncherUpdateStatus { get; set; } = "";

    /// <summary>Wird von der View gesetzt (Ordnerauswahl braucht das Fenster).</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public bool IsLibrary => Section == Section.Library;
    public bool IsNews => Section == Section.News;
    public bool IsSettings => Section == Section.Settings;
    public bool HasSelectedGame => SelectedGame is not null;
    public bool ShowEmptyLibrary => !IsLoading && Games.Count == 0;
    public bool HasError => ErrorText is not null;
    public bool HasLauncherUpdate => PendingLauncherVersion is not null;
    public bool HasDownload => ActiveDownload is not null;
    public bool ShowIdleDownloads => !HasDownload && !HasToolDownload;
    public bool IsVersionDialogOpen => VersionDialogGame is not null;
    public bool IsAnyDialogOpen => IsVersionDialogOpen || IsConfirmOpen || IsJoinDialogOpen || IsNameDialogOpen || IsProtonDialogOpen;
    public bool HasNews => NewsItems.Count > 0;
    public string GamesHeader => $"({Games.Count})";
    public string LauncherUpdateText => $"Mett Launcher {PendingLauncherVersion} ist bereit. Neustarten, um das Update abzuschließen.";
    public string DownloadIdleText => "DOWNLOADS · keine aktiven";
    public bool IsCatLibrary => SettingsCategory == SettingsCategory.Library;
    public bool IsCatUpdates => SettingsCategory == SettingsCategory.Updates;
    public bool IsCatAbout => SettingsCategory == SettingsCategory.About;
    public bool IsCatMultiplayer => SettingsCategory == SettingsCategory.Multiplayer;
    public bool IsCatProton => SettingsCategory == SettingsCategory.Proton;
    public bool CanSelfUpdate => _s.LauncherUpdates.IsInstalled;

    partial void OnSectionChanged(Section value)
    {
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsNews));
        OnPropertyChanged(nameof(IsSettings));
    }
    partial void OnSelectedGameChanged(GameViewModel? value) => OnPropertyChanged(nameof(HasSelectedGame));
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnPendingLauncherVersionChanged(string? value) { OnPropertyChanged(nameof(HasLauncherUpdate)); OnPropertyChanged(nameof(LauncherUpdateText)); }
    partial void OnActiveDownloadChanged(GameViewModel? value) { OnPropertyChanged(nameof(HasDownload)); OnPropertyChanged(nameof(HasToolDownload)); OnPropertyChanged(nameof(ShowIdleDownloads)); }
    partial void OnVersionDialogGameChanged(GameViewModel? value) { OnPropertyChanged(nameof(IsVersionDialogOpen)); OnPropertyChanged(nameof(IsAnyDialogOpen)); }
    partial void OnIsConfirmOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyLibrary));
    partial void OnSettingsCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(IsCatLibrary));
        OnPropertyChanged(nameof(IsCatUpdates));
        OnPropertyChanged(nameof(IsCatAbout));
        OnPropertyChanged(nameof(IsCatMultiplayer));
        OnPropertyChanged(nameof(IsCatProton));
        if (value == SettingsCategory.Proton) RefreshProtonInfo();
    }
    partial void OnAutoUpdateGamesChanged(bool value) => _s.Settings.Update(s => s with { AutoUpdate = value });
    partial void OnIncludePrereleasesChanged(bool value)
    {
        _s.Settings.Update(s => s with { IncludePrereleases = value });
        foreach (var g in Games) g.Recompute();
    }

    // ================= Start =================

    public async Task InitializeAsync()
    {
        // 1) Sofort aus dem Cache – der Launcher ist auch offline direkt benutzbar.
        if (_s.Catalog.TryLoadCache() is { } cachedCatalog) ApplyCatalog(cachedCatalog.Catalog);
        if (_s.News.TryLoadCache() is { } cachedNews) ApplyNews(cachedNews.Feed);

        // 2) Netz: Katalog, News und Launcher-Update parallel.
        var catalogTask = LoadCatalogAsync();
        var newsTask = LoadNewsAsync();
        _ = CheckLauncherUpdateAsync();
        await Task.WhenAll(catalogTask, newsTask);

        // 3) Releases je Spiel, danach ggf. Auto-Update.
        var sources = await Task.WhenAll(Games.Select(g => g.RefreshReleasesAsync()));
        if (sources.Any(s => s is null or ReleaseSource.Offline)) IsOffline = true;
        IsLoading = false;
        OnPropertyChanged(nameof(ShowEmptyLibrary));
        foreach (var g in Games.ToList()) await g.AutoUpdateIfNeededAsync();
    }

    private async Task LoadCatalogAsync()
    {
        try
        {
            var result = await _s.Catalog.LoadAsync();
            if (result.NetworkError is not null) IsOffline = true;
            ApplyCatalog(result.Catalog);
        }
        catch (CatalogUnavailableException ex)
        {
            IsOffline = true;
            ShowError(ex.Message);
        }
    }

    private async Task LoadNewsAsync()
    {
        var result = await _s.News.LoadAsync();
        if (result.NetworkError is not null && !result.FromCache) IsOffline = true;
        ApplyNews(result.Feed);
    }

    private void ApplyCatalog(GameCatalog catalog)
    {
        var selectedId = SelectedGame?.Id ?? _s.Library.Snapshot().Games.Keys.FirstOrDefault();
        foreach (var entry in catalog.Games)
        {
            var existing = Games.FirstOrDefault(g => g.Id == entry.Id);
            if (existing is null)
            {
                var vm = new GameViewModel(_s, this, entry);
                vm.SetNews(_newsFeed);
                Games.Add(vm);
                _ = vm.LoadImagesAsync();
            }
            else existing.UpdateEntry(entry);
        }
        foreach (var gone in Games.Where(g => catalog.Find(g.Id) is null).ToList()) Games.Remove(gone);
        ApplyFilter();
        SelectedGame = Games.FirstOrDefault(g => g.Id == selectedId) ?? SelectedGame ?? Games.FirstOrDefault();
        OnPropertyChanged(nameof(GamesHeader));
        OnPropertyChanged(nameof(ShowEmptyLibrary));
    }

    private void ApplyNews(NewsFeed feed)
    {
        _newsFeed = feed;
        foreach (var g in Games) g.SetNews(feed);
        NewsItems.Clear();
        foreach (var n in feed.Items.Take(30))
        {
            var gameName = n.Game is not null ? Games.FirstOrDefault(g => g.Id.Equals(n.Game, StringComparison.OrdinalIgnoreCase))?.Name ?? n.Game.ToUpperInvariant() : "METT GAMING";
            NewsItemViewModel? item = null;
            item = new NewsItemViewModel
            {
                OpenCommand = new RelayCommand(() => OpenNewsItem(item)),
                Title = n.Title,
                Meta = $"{gameName.ToUpper(Formatting.De)} · {Formatting.DayHeader(n.Date)}",
                Excerpt = n.Excerpt,
                Url = n.Url,
                GameId = n.Game,
                Date = n.Date,
            };
            NewsItems.Add(item);
            var url = _s.News.ResolveAssetUrl(n.Image);
            if (url is not null) _ = LoadNewsImageAsync(item, url);
        }
        OnPropertyChanged(nameof(HasNews));
    }

    private async Task LoadNewsImageAsync(NewsItemViewModel item, Uri url) => item.Image = await _s.Images.GetAsync(url);

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        FilteredGames.Clear();
        foreach (var g in Games.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            FilteredGames.Add(g);
    }

    private async Task CheckLauncherUpdateAsync()
    {
        try { PendingLauncherVersion = await _s.LauncherUpdates.CheckAndDownloadAsync(); }
        catch (Exception) { /* Self-Update ist nie kritisch */ }
    }

    // ================= Navigation & Befehle =================

    [RelayCommand] private void ShowLibrary() => Section = Section.Library;
    [RelayCommand] private void ShowNews() => Section = Section.News;
    [RelayCommand] private void ShowSettings() => Section = Section.Settings;
    [RelayCommand] private void ShowSettingsLibrary() => SettingsCategory = SettingsCategory.Library;
    [RelayCommand] private void ShowSettingsUpdates() => SettingsCategory = SettingsCategory.Updates;
    [RelayCommand] private void ShowSettingsAbout() => SettingsCategory = SettingsCategory.About;
    [RelayCommand] private void ShowSettingsMultiplayer() => SettingsCategory = SettingsCategory.Multiplayer;
    [RelayCommand] private void ShowSettingsProton() => SettingsCategory = SettingsCategory.Proton;

    [RelayCommand]
    private void OpenNewsItem(NewsItemViewModel? item)
    {
        if (item is null) return;
        if (item.GameId is not null && Games.FirstOrDefault(g => g.Id.Equals(item.GameId, StringComparison.OrdinalIgnoreCase)) is { } game)
        {
            SelectedGame = game;
            Section = Section.Library;
        }
        else Shell.OpenUrl(item.Url);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        IsOffline = false;
        await LoadCatalogAsync();
        await LoadNewsAsync();
        var sources = await Task.WhenAll(Games.Select(g => g.RefreshReleasesAsync()));
        if (sources.Any(s => s is null or ReleaseSource.Offline)) IsOffline = true;
    }

    [RelayCommand] private void RestartForUpdate() => _s.LauncherUpdates.ApplyAndRestart();
    [RelayCommand] private void DismissLauncherUpdate() => PendingLauncherVersion = null;
    [RelayCommand] private void DismissError() => ErrorText = null;
    [RelayCommand] private void OpenOrg() => Shell.OpenUrl("https://github.com/Mett-Gaming");

    [RelayCommand]
    private async Task CheckForLauncherUpdateAsync()
    {
        if (!_s.LauncherUpdates.IsInstalled) { LauncherUpdateStatus = "Nur in der installierten Version verfügbar."; return; }
        LauncherUpdateStatus = "Wird geprüft …";
        try
        {
            PendingLauncherVersion = await _s.LauncherUpdates.CheckAndDownloadAsync();
            LauncherUpdateStatus = PendingLauncherVersion is null ? "Du hast die neueste Version." : $"Version {PendingLauncherVersion} ist bereit.";
        }
        catch (Exception ex) { LauncherUpdateStatus = $"Prüfung fehlgeschlagen: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ChangeLibraryDirAsync()
    {
        if (PickFolder is null) return;
        var dir = await PickFolder();
        if (string.IsNullOrWhiteSpace(dir)) return;
        _s.Settings.Update(s => s with { LibraryDir = dir });
        LibraryDir = _s.Settings.LibraryDir;
    }

    [RelayCommand] private void OpenLibraryDir() => Shell.OpenFolder(LibraryDir);

    public void ShowError(string message) => ErrorText = message;

    public void OpenVersionDialog(GameViewModel game) => VersionDialogGame = game;

    [RelayCommand]
    public void CloseDialogs()
    {
        VersionDialogGame = null;
        JoinDialogGame = null;
        ProtonDialogGame = null;
        IsNameDialogOpen = false;
        _afterName = null;
        IsConfirmOpen = false;
        _confirmAction = null;
    }

    public void Confirm(string title, string text, string button, Func<Task> action)
    {
        ConfirmTitle = title;
        ConfirmText = text;
        ConfirmButton = button;
        _confirmAction = action;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmAcceptAsync()
    {
        var action = _confirmAction;
        CloseDialogs();
        if (action is not null) await action();
    }
}
