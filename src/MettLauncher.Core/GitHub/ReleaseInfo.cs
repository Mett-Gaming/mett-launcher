namespace MettLauncher.Core.GitHub;

public sealed record ReleaseInfo
{
    public required string TagName { get; init; }
    public string? Name { get; init; }
    /// <summary>Aus dem Tag geparste Version; <c>null</c> wenn der Tag kein SemVer ist.</summary>
    public SemVersion? Version { get; init; }
    public bool Prerelease { get; init; }
    public bool Draft { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    /// <summary>Release-Notes (Markdown).</summary>
    public string? Body { get; init; }
    public string? HtmlUrl { get; init; }
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TagName : Name;
}

public sealed record ReleaseAsset
{
    public required string Name { get; init; }
    public long Size { get; init; }
    public required Uri DownloadUrl { get; init; }
    /// <summary>Rohwert aus der API, z. B. <c>sha256:5f50…</c>.</summary>
    public string? Digest { get; init; }

    /// <summary>sha256 als Kleinbuchstaben-Hex, falls die API einen sha256-Digest liefert.</summary>
    public string? Sha256 =>
        Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && d.Length == 7 + 64
            ? d[7..].ToLowerInvariant()
            : null;
}

/// <summary>Ein installierbares Release: Release + passendes Asset + Version.</summary>
public sealed record ResolvedRelease(ReleaseInfo Release, ReleaseAsset Asset, SemVersion Version)
{
    public string Tag => Release.TagName;
}
