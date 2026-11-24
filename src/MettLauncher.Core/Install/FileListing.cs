using System.Text.Json;
using System.Text.Json.Serialization;
using MettLauncher.Core.GitHub;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Install;

/// <summary>Ein Stück einer Paketdatei: Ort in der Datei, Größe, sha256.</summary>
public sealed record FilePart(long Offset, long Size, string Sha256);

/// <summary>Eine Datei der neuen Version und ihr Ort im Zip.</summary>
/// <param name="Packed">Im Zip gepackt (deflate); sonst liegen die Bytes unverändert da.</param>
public sealed record ListedFile(string Path, long Size, string Sha256, long Offset, long Stored, bool Packed, IReadOnlyList<FilePart> Parts);

/// <summary>Dateiliste einer Version (<c>….dateien.json</c>, erzeugt von <c>mettpack</c> in der Engine).</summary>
public sealed record FileListing(string Zip, long ZipSize, IReadOnlyList<ListedFile> Files)
{
    private const int MaxFiles = 20_000, MaxParts = 200_000;
    private const long MaxFile = 1L << 30;

    private sealed record Raw(int Schema, string? Zip, [property: JsonPropertyName("zip_size")] long ZipSize, List<RawFile?>? Files);
    private sealed record RawFile(string? Path, long Size, string? Sha256, long Offset, long Stored, bool Packed, List<JsonElement[]>? Parts);

    /// <summary>Name der Dateiliste zu einem Zip.</summary>
    public static string NameFor(string zipName) =>
        (zipName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? zipName[..^4] : zipName) + ".dateien.json";

    public static ReleaseAsset? AssetFor(ResolvedRelease release) =>
        release.Release.Assets.FirstOrDefault(a => a.Name == NameFor(release.Asset.Name));

    /// <returns><c>null</c> = ungültig oder passt nicht zu <paramref name="zip"/>.</returns>
    public static FileListing? Parse(byte[] json, ReleaseAsset zip)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<Raw>(json, Json.Options);
            if (raw is not { Schema: 1, Files.Count: > 0 and <= MaxFiles } || raw.Zip != zip.Name || raw.ZipSize != zip.Size) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<ListedFile>();
            var parts = 0;
            foreach (var f in raw.Files)
            {
                if (f?.Path is null || !seen.Add(f.Path) || FileSystemUtil.SafeCombine(System.IO.Path.GetTempPath(), f.Path) is null) return null;
                if (f.Size is < 0 or > MaxFile || f.Offset < 0 || f.Stored < 0 || f.Stored > zip.Size || f.Offset > zip.Size - f.Stored) return null;
                if (!IsSha(f.Sha256) || (!f.Packed && f.Stored != f.Size)) return null;
                if (ReadParts(f) is not { } list || (parts += list.Count) > MaxParts) return null;
                files.Add(new ListedFile(f.Path, f.Size, f.Sha256!, f.Offset, f.Stored, f.Packed, list));
            }
            return new FileListing(raw.Zip, raw.ZipSize, files);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsSha(string? s) => s is { Length: 64 } && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Stücke müssen die Datei lückenlos ergeben und unverändert im Zip liegen.</summary>
    private static IReadOnlyList<FilePart>? ReadParts(RawFile f)
    {
        if (f.Parts is not { Count: > 0 }) return [];
        if (f.Packed) return null;
        var list = new List<FilePart>();
        long at = 0;
        foreach (var p in f.Parts)
        {
            if (p.Length != 3) return null;
            var part = new FilePart(p[0].GetInt64(), p[1].GetInt64(), p[2].GetString() ?? "");
            if (part.Offset != at || part.Size <= 0 || part.Size > f.Size - at || !IsSha(part.Sha256)) return null;
            at += part.Size;
            list.Add(part);
        }
        return at == f.Size ? list : null;
    }
}
