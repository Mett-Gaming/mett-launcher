using System.Diagnostics;

namespace MettLauncher.App.Services;

/// <summary>Öffnet Ordner und Links im System (Explorer bzw. Standardbrowser).</summary>
public static class Shell
{
    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else
            XdgOpen(path);
    }

    public static void OpenUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        else
            XdgOpen(u.AbsoluteUri);
    }

    /// <summary>Linux: Datei-Manager bzw. Browser über xdg-open (Argument als eigenes argv, kein Shell-Parsing).</summary>
    private static void XdgOpen(string target)
    {
        try
        {
            var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            using var _ = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception) { /* kein xdg-open vorhanden */ }
    }
}
