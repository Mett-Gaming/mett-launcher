using System.Buffers.Binary;
using System.Security.Cryptography;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Install;

/// <summary>Ein Stück der neuen Datei: aus der alten Installation oder aus dem Zip.</summary>
/// <param name="LocalFile">Vorhandene Datei, <c>null</c> = laden.</param>
/// <param name="Source">Ort in der vorhandenen Datei bzw. im Zip.</param>
/// <param name="Length">Bytes an der Quelle.</param>
public sealed record DeltaPiece(string? LocalFile, long Source, long Length);

/// <summary>So entsteht eine Datei der neuen Version.</summary>
public sealed record DeltaFile(ListedFile File, IReadOnlyList<DeltaPiece> Pieces);

/// <summary>Zusammenhängender Abruf aus dem Zip.</summary>
public sealed record DeltaRange(long Offset, long Length);

public sealed record DeltaPlan(IReadOnlyList<DeltaFile> Files, IReadOnlyList<DeltaRange> Ranges)
{
    public long DownloadSize => Ranges.Sum(r => r.Length);
}

/// <summary>Vergleicht eine vorhandene Installation mit der Dateiliste der neuen Version.</summary>
public static class DeltaPlanner
{
    /// <summary>Kleine Lücken lieber mitladen als neu anfragen.</summary>
    private const long MaxGap = 64 * 1024;

    public static DeltaPlan Plan(FileListing listing, string installDir, CancellationToken ct = default)
    {
        var packs = LocalParts(installDir, ct);
        var files = new List<DeltaFile>();
        foreach (var f in listing.Files)
        {
            ct.ThrowIfCancellationRequested();
            var local = FileSystemUtil.SafeCombine(installDir, f.Path);
            if (local is not null && SameFile(local, f, ct))
                files.Add(new DeltaFile(f, [new DeltaPiece(local, 0, f.Size)]));
            else if (f.Parts.Count > 0)
                files.Add(new DeltaFile(f, [.. f.Parts.Select(p => packs.TryGetValue((p.Sha256, p.Size), out var have)
                    ? new DeltaPiece(have.File, have.Offset, p.Size)
                    : new DeltaPiece(null, f.Offset + p.Offset, p.Size))]));
            else
                files.Add(new DeltaFile(f, f.Stored == 0 ? [] : [new DeltaPiece(null, f.Offset, f.Stored)]));
        }
        return new DeltaPlan(files, Merge(files.SelectMany(f => f.Pieces).Where(p => p.LocalFile is null)));
    }

    private static List<DeltaRange> Merge(IEnumerable<DeltaPiece> pieces)
    {
        var ranges = new List<DeltaRange>();
        foreach (var p in pieces.OrderBy(p => p.Source))
        {
            var end = p.Source + p.Length;
            if (ranges.Count > 0 && ranges[^1] is var last && p.Source <= last.Offset + last.Length + MaxGap)
                ranges[^1] = last with { Length = Math.Max(last.Length, end - last.Offset) };
            else ranges.Add(new DeltaRange(p.Source, p.Length));
        }
        return ranges;
    }

    private static bool SameFile(string path, ListedFile f, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != f.Size || info.LinkTarget is not null) return false;
            ct.ThrowIfCancellationRequested();
            using var s = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(s)) == f.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Unbeschädigte Stücke der vorhandenen Paketdateien.</summary>
    private static Dictionary<(string Sha, long Size), (string File, long Offset)> LocalParts(string installDir, CancellationToken ct)
    {
        var result = new Dictionary<(string, long), (string, long)>();
        if (!Directory.Exists(installDir)) return result;
        foreach (var path in Directory.EnumerateFiles(installDir, "*.mpak", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var s = File.OpenRead(path);
                foreach (var (sha, size, offset) in ReadIndex(path))
                {
                    ct.ThrowIfCancellationRequested();
                    var bytes = new byte[size];
                    s.Seek(offset, SeekOrigin.Begin);
                    s.ReadExactly(bytes);
                    if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha) result.TryAdd((sha, size), (path, offset));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // nicht lesbar: wird neu geladen
            }
        }
        return result;
    }

    /// <summary>Verzeichnis einer Paketdatei (Aufbau: <c>mpak</c> in der Engine); leer, wenn sie kaputt ist.</summary>
    internal static IReadOnlyList<(string Sha, long Size, long Offset)> ReadIndex(string path)
    {
        const int header = 48, fixedPart = 2 + 8 * 3 + 1 + 32;
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[header];
            s.ReadExactly(head);
            if (!head[..4].SequenceEqual("MPK1"u8)) return [];
            var count = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(head[8..]);
            if (length > 64 << 20 || (long)length > s.Length - header || count > length / fixedPart) return [];
            var index = new byte[length];
            s.ReadExactly(index);
            if (!SHA256.HashData(index).AsSpan().SequenceEqual(head[16..])) return [];
            var list = new List<(string, long, long)>();
            ReadOnlySpan<byte> rest = index;
            for (var i = 0; i < count; i++)
            {
                var name = BinaryPrimitives.ReadUInt16LittleEndian(rest);
                rest = rest[(2 + name)..];
                var offset = BinaryPrimitives.ReadInt64LittleEndian(rest);
                var size = BinaryPrimitives.ReadInt64LittleEndian(rest[8..]);
                var sha = Convert.ToHexStringLower(rest.Slice(25, 32));
                rest = rest[57..];
                if (offset >= 0 && size is > 0 and <= 1 << 30 && size <= s.Length && offset <= s.Length - size) list.Add((sha, size, offset));
            }
            return list;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }
}
