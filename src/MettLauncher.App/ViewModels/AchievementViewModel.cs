using System.Globalization;
using Avalonia.Media.Imaging;
using MettLauncher.Core.Achievements;

namespace MettLauncher.App.ViewModels;

/// <summary>Ein Erfolg auf der Spielseite und im Overlay.</summary>
public sealed class AchievementViewModel
{
    public AchievementViewModel(AchievementDefinition definition, AchievementState? state, Bitmap? icon)
    {
        var s = state ?? new AchievementState();
        Unlocked = s.UnlockedAt is not null;
        var secret = definition.Hidden && !Unlocked;
        Name = secret ? "Verborgener Erfolg" : definition.Name;
        Text = secret ? "Wird nach dem Freischalten gezeigt." : definition.Text;
        Icon = secret ? null : icon;
        UnlockedAt = s.UnlockedAt;
        var now = Math.Min(s.Progress, definition.Goal);
        Fraction = Unlocked ? 1 : (double)now / definition.Goal;
        ShowProgress = !Unlocked && !secret && definition.Goal > 1;
        Info = s.UnlockedAt is { } at ? Formatting.Date(at)
            : ShowProgress ? $"{now.ToString("N0", Formatting.De)} / {definition.Goal.ToString("N0", Formatting.De)}" : "";
    }

    public string Name { get; }
    public string Text { get; }
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon is not null;
    public bool Unlocked { get; }
    public double IconOpacity => Unlocked ? 1 : 0.3;
    public double Fraction { get; }
    public bool ShowProgress { get; }
    public string Info { get; }
    public string Tip => Text.Length > 0 ? $"{Name}\n{Text}" : Name;
    public DateTimeOffset? UnlockedAt { get; }

    /// <summary>Alle Erfolge eines Spiels, zuletzt freigeschaltete zuerst.</summary>
    public static IReadOnlyList<AchievementViewModel> All(IReadOnlyList<AchievementDefinition> definitions,
        IReadOnlyDictionary<string, AchievementState> states) =>
        [.. definitions.Select(d => new AchievementViewModel(d, states.GetValueOrDefault(d.Id), LoadIcon(d)))
            .OrderByDescending(a => a.UnlockedAt ?? DateTimeOffset.MinValue)];

    public static Bitmap? LoadIcon(AchievementDefinition definition)
    {
        if (definition.Icon is null) return null;
        try
        {
            // Ganz lesen: Die Datei darf nicht offen bleiben, sonst scheitern Updates des Spiels.
            using var data = new MemoryStream(File.ReadAllBytes(definition.Icon));
            return Bitmap.DecodeToWidth(data, 128);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return null; // kaputtes Bild
        }
    }
}
