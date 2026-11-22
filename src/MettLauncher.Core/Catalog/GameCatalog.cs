namespace MettLauncher.Core.Catalog;

/// <summary>Inhalt von <c>games.json</c> (Schema v1).</summary>
public sealed record GameCatalog
{
    public int Schema { get; init; }
    public IReadOnlyList<GameEntry> Games { get; init; } = [];

    public GameEntry? Find(string id) => Games.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Ein Spiel im Katalog.</summary>
public sealed record GameEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>GitHub-Repo im Format <c>owner/name</c>.</summary>
    public required string Repo { get; init; }
    public string? Tagline { get; init; }
    /// <summary>Relativ zur Katalog-Basis-URL, z. B. <c>covers/kasse0.png</c>.</summary>
    public string? Cover { get; init; }
    public string? Hero { get; init; }
    /// <summary>Regex auf den Asset-Namen, z. B. <c>^KASSE0-v.*\.zip$</c>.</summary>
    public required string AssetPattern { get; init; }
    public bool StripTopFolder { get; init; } = true;
    /// <summary>Pfad der exe relativ zum Installordner.</summary>
    public required string Exe { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    /// <summary>Relative Pfade im Installordner, die Updates überleben.</summary>
    public IReadOnlyList<string> Preserve { get; init; } = [];
    public bool IncludePrereleases { get; init; }
}
