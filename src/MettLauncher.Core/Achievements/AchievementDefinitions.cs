using System.Text.Json;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Achievements;

/// <summary>Ein Erfolg, wie ihn das Spiel beschreibt.</summary>
public sealed record AchievementDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>Bild im Ordner <c>erfolge</c>.</summary>
    public string? Icon { get; init; }
    /// <summary>Name und Text erst nach dem Freischalten zeigen.</summary>
    public bool Hidden { get; init; }
    /// <summary>Wie viel nötig ist; 1 = einfach freischalten.</summary>
    public int Goal { get; init; } = 1;
}

/// <summary>Liest <c>erfolge/erfolge.json</c> aus dem Ordner eines installierten Spiels.</summary>
public static class AchievementDefinitions
{
    public const string Folder = "erfolge", FileName = "erfolge.json";
    public const int MaxCount = 200;
    private const int MaxFile = 512 * 1024, MaxIcon = 256 * 1024;

    private sealed record Content(int Schema, List<AchievementDefinition?>? Achievements);

    /// <returns>Leer, wenn es keine gibt oder die Datei kaputt ist (<paramref name="problem"/> nennt dann den Grund).</returns>
    public static IReadOnlyList<AchievementDefinition> Load(string gameDir, out string? problem)
    {
        problem = null;
        var dir = Path.Combine(gameDir, Folder);
        var path = Path.Combine(dir, FileName);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return [];
            if (info.Length > MaxFile) problem = "Datei zu groß";
            else
            {
                var file = JsonSerializer.Deserialize<Content>(File.ReadAllText(path), Json.Options);
                if (Check(file) is not { } why) return [.. file!.Achievements!.Select(a => a! with { Icon = IconOf(dir, a!.Icon) })];
                problem = why;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = ex.Message;
        }
        return [];
    }

    private static string? Check(Content? file)
    {
        if (file?.Achievements is not { } list) return "keine Liste";
        if (file.Schema != 1) return $"unbekanntes Schema {file.Schema}";
        if (list.Count > MaxCount) return $"mehr als {MaxCount} Erfolge";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in list)
        {
            if (a is null || !IsId(a.Id)) return $"ungültige Id „{a?.Id}“";
            if (!ids.Add(a.Id)) return $"Id „{a.Id}“ doppelt";
            if (string.IsNullOrWhiteSpace(a.Name) || a.Name.Length > 80) return $"„{a.Id}“: Name fehlt oder ist zu lang";
            if (a.Text is not { Length: <= 200 }) return $"„{a.Id}“: Text zu lang";
            if (a.Goal is < 1 or > 1_000_000_000) return $"„{a.Id}“: Ziel ungültig";
        }
        return null;
    }

    /// <summary>Kleinbuchstaben, Ziffern, Unterstrich; höchstens 64 Zeichen.</summary>
    public static bool IsId(string? id) => id is { Length: >= 1 and <= 64 }
        && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    /// <summary>Vollständiger Pfad eines erlaubten Bildes, sonst <c>null</c>.</summary>
    private static string? IconOf(string dir, string? icon)
    {
        if (icon is null || FileSystemUtil.SafeCombine(dir, icon) is not { } path) return null;
        var info = new FileInfo(path);
        return info is { Exists: true, Length: <= MaxIcon, LinkTarget: null }
            && info.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}
