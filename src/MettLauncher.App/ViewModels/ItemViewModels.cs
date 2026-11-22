using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MettLauncher.App.Services;
using MettLauncher.Core.GitHub;

namespace MettLauncher.App.ViewModels;

/// <summary>Eintrag in der Aktivitätsspalte der Spielseite (News-Meldung oder Release).</summary>
public sealed class ActivityItemViewModel
{
    public required DateTimeOffset Date { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = [];
    public string? Url { get; init; }

    public string DayHeader => Formatting.DayHeader(Date);
    public bool HasUrl => Url is not null;
    public IRelayCommand OpenCommand => new RelayCommand(() => Shell.OpenUrl(Url));
}

/// <summary>Meldung auf der Seite NEUIGKEITEN.</summary>
public sealed partial class NewsItemViewModel : ObservableObject
{
    public required string Title { get; init; }
    public required string Meta { get; init; }
    public string? Excerpt { get; init; }
    public string? Url { get; init; }
    public string? GameId { get; init; }
    public required DateTimeOffset Date { get; init; }
    public IRelayCommand? OpenCommand { get; init; }

    [ObservableProperty] public partial Bitmap? Image { get; set; }

    public bool HasExcerpt => !string.IsNullOrWhiteSpace(Excerpt);
    public bool HasImage => Image is not null;
    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));
}

/// <summary>Zeile im Dialog „Version wählen“.</summary>
public sealed class VersionItemViewModel(GameViewModel owner, ResolvedRelease release, bool isInstalled, bool isPinned)
{
    public ResolvedRelease Release { get; } = release;
    public string Version => Release.Tag;
    public string Date => Release.Release.PublishedAt is { } d ? Formatting.Date(d) : "";
    public string Size => Formatting.Size(Release.Asset.Size);
    public bool IsInstalled { get; } = isInstalled;
    public bool IsPinned { get; } = isPinned;
    public bool CanInstall => !IsInstalled;
    public string BadgeText => IsPinned ? "FESTGEPINNT" : IsInstalled ? "INSTALLIERT" : Release.Release.Prerelease ? "VORAB" : "VERFÜGBAR";

    public IAsyncRelayCommand InstallCommand => new AsyncRelayCommand(() => owner.InstallVersionAsync(Release));
    public IRelayCommand TogglePinCommand => new RelayCommand(owner.TogglePin);
    public string PinLabel => IsPinned ? "Lösen" : "Festpinnen";
}
