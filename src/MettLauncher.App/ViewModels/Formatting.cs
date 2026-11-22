using System.Globalization;
using System.Text.RegularExpressions;

namespace MettLauncher.App.ViewModels;

/// <summary>Deutsche Anzeigeformate und das Aufräumen von GitHub-Release-Texten.</summary>
public static partial class Formatting
{
    public static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Size(long bytes) => bytes switch
    {
        <= 0 => "–",
        < 1024 * 1024 => string.Create(De, $"{bytes / 1024.0:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(De, $"{bytes / 1024.0 / 1024.0:0.0} MB"),
        _ => string.Create(De, $"{bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"),
    };

    public static string Playtime(TimeSpan t) => t.TotalMinutes switch
    {
        < 1 => "–",
        < 60 => $"{(int)t.TotalMinutes} Minuten",
        _ => string.Create(De, $"{t.TotalHours:0.0} Stunden"),
    };

    public static string LastPlayed(DateTimeOffset? when, DateTimeOffset now)
    {
        if (when is null) return "Noch nie";
        var local = when.Value.ToLocalTime();
        var days = (now.ToLocalTime().Date - local.Date).Days;
        return days switch
        {
            0 => "Heute",
            1 => "Gestern",
            < 7 => $"Vor {days} Tagen",
            _ => local.ToString("d. MMMM yyyy", De),
        };
    }

    public static string Date(DateTimeOffset d) => d.ToLocalTime().ToString("dd.MM.yyyy", De);
    public static string DayHeader(DateTimeOffset d) => d.ToLocalTime().ToString("d. MMMM", De).ToUpper(De);

    /// <summary>Macht aus Markdown-Release-Notizen einfache Zeilen (Aufzählungen, keine Links/Fettung, kein "Full Changelog").</summary>
    public static IReadOnlyList<string> NoteLines(string? markdown, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.Contains("Full Changelog", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("<!--")) continue;
            line = LinkRegex().Replace(line, "$1");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = BulletRegex().Replace(line, "");
            line = UrlRegex().Replace(line, "").Trim();
            if (line.Length == 0) continue;
            lines.Add(line);
            if (lines.Count >= max) break;
        }
        return lines;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")] private static partial Regex LinkRegex();
    [GeneratedRegex(@"^([-*+]|\d+\.)\s+")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"https?://\S+")] private static partial Regex UrlRegex();
}
