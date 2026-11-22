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
public enum SettingsCategory { Library, Updates, About }

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
    public bool IsVersionDialogOpen => VersionDialogGame is not null;
    public bool IsAnyDialogOpen => IsVersionDialogOpen || IsConfirmOpen;
    public bool HasNews => NewsItems.Count > 0;
    public string GamesHeader => $"({Games.Count})";
    public string LauncherUpdateText => $"Mett Launcher {PendingLauncherVersion} ist bereit. Neustarten, um das Update abzuschließen.";
    public string DownloadIdleText => "DOWNLOADS · keine aktiven";
    public bool IsCatLibrary => SettingsCategory == SettingsCategory.Library;
    public bool IsCatUpdates => SettingsCategory == SettingsCategory.Updates;
    public bool IsCatAbout => SettingsCategory == SettingsCategory.About;
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
    partial void OnActiveDownloadChanged(GameViewModel? value) => OnPropertyChanged(nameof(HasDownload));
    partial void OnVersionDialogGameChanged(GameViewModel? value) { OnPropertyChanged(nameof(IsVersionDialogOpen)); OnPropertyChanged(nameof(IsAnyDialogOpen)); }
    partial void OnIsConfirmOpenChanged(bool value) => OnPropertyChanged(nameof(IsAnyDialogOpen));
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyLibrary));
    partial void OnSettingsCategoryChanged(SettingsCategory value)
    {
        OnPropertyChanged(nameof(IsCatLibrary));
        OnPropertyChanged(nameof(IsCatUpdates));
        OnPropertyChanged(nameof(IsCatAbout));
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
