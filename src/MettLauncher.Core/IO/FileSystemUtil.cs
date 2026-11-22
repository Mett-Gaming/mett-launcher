namespace MettLauncher.Core.IO;

internal static class FileSystemUtil
{
    /// <summary>Löscht ein Verzeichnis rekursiv, auch mit schreibgeschützten Dateien; wiederholt kurz bei Sperren.</summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }

    public static void TryDeleteDirectory(string path)
    {
        try { DeleteDirectory(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>Relativ, nicht leer, ohne Laufwerk/Root und ohne <c>..</c>-Segmente.</summary>
    public static bool IsSafeRelativePath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return false;
        if (Path.IsPathRooted(relative) || relative.Contains(':')) return false;
        var segments = relative.Split('/', '\\');
        return segments.All(s => s != "..") && segments.Any(s => s.Length > 0 && s != ".");
    }

    /// <summary>
    /// Kombiniert <paramref name="root"/> mit einem relativen Pfad und stellt sicher, dass das Ergebnis
    /// innerhalb von <paramref name="root"/> liegt (Schutz vor <c>..</c> und absoluten Pfaden).
    /// </summary>
    public static string? SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var fullRoot = Path.GetFullPath(root);
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        var combined = Path.GetFullPath(Path.Combine(fullRoot, relative));
        return combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }
}
