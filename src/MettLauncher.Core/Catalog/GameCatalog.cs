namespace MettLauncher.Core.Catalog;

/// <summary>Inhalt von <c>games.json</c> (Schema v1).</summary>
public sealed record GameCatalog
{
    public int Schema { get; init; }
    /// <summary>Discord-Anwendung des Launchers (öffentliche Id, kein Geheimnis); ohne sie gibt es keine Discord-Anzeige.</summary>
    public string? DiscordAppId { get; init; }
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
    /// <summary>Spiel nutzt den Mehrspieler des Launchers (<c>mett-net</c>); zeigt „Mit Freunden spielen“.</summary>
    public bool Multiplayer { get; init; }
    /// <summary>Eigene Discord-Anwendung des Spiels; ohne sie erscheint der Status unter der des Launchers.</summary>
    public string? DiscordAppId { get; init; }
    /// <summary>Eintrag in der umu-Datenbank (Proton-Fixes), z. B. <c>umu-12345</c>; ohne = <c>umu-default</c>.</summary>
    public string? UmuId { get; init; }
    /// <summary>Unter Linux: <c>proton</c> (Standard, Windows-Build über Proton) oder <c>native</c> (Linux-Build im Zip).</summary>
    public string Linux { get; init; } = "proton";
    public bool RunsNativeOnLinux => string.Equals(Linux, "native", StringComparison.OrdinalIgnoreCase);
}
