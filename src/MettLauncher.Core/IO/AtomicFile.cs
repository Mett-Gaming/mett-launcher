using System.Text;

namespace MettLauncher.Core.IO;

/// <summary>Schreibt Dateien atomar: erst in eine temporäre Datei daneben, dann per Rename ersetzen.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));

    public static void WriteAllBytes(string path, byte[] content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
