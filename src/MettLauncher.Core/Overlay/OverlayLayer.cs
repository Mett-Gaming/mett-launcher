using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MettLauncher.Core.IO;

namespace MettLauncher.Core.Overlay;

/// <summary>
/// Legt die Overlay-Bibliothek samt Beschreibung bereit und nennt die Umgebungsvariablen, mit denen der Vulkan-Lader sie
/// nur in Spielen des Launchers einschaltet. Geht etwas schief, startet das Spiel ohne Overlay.
/// </summary>
public sealed class OverlayLayer(LauncherPaths paths, string sourceDir, string? linuxDataHome = null, bool? windows = null)
{
    public const string LayerName = "VK_LAYER_METT_overlay", EnableVariable = "METT_OVERLAY";
    private readonly bool _windows = windows ?? OperatingSystem.IsWindows();

    private string Library => Path.Combine(sourceDir, _windows ? "mett_overlay.dll" : "libmett_overlay.so");

    public bool IsAvailable => File.Exists(Library);

    /// <summary>Nur zur Anzeige und fürs Protokoll.</summary>
    public string? LastError { get; private set; }

    public IReadOnlyDictionary<string, string> Environment(Func<string, string?> current)
    {
        try
        {
            if (!IsAvailable) return new Dictionary<string, string>();
            var bytes = File.ReadAllBytes(Library);
            var dir = Path.Combine(paths.OverlayDir, Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant());
            var library = Path.Combine(dir, Path.GetFileName(Library));
            if (!File.Exists(library) || new FileInfo(library).Length != bytes.Length) AtomicFile.WriteAllBytes(library, bytes);
            RemoveOld(dir);
            LastError = null;
            if (!_windows)
            {
                var home = linuxDataHome ?? LauncherPaths.LocalAppData; // ~/.local/share
                Write(Path.Combine(home, "vulkan", "implicit_layer.d", "mett_overlay.json"), library, switchable: true);
                return new Dictionary<string, string> { [EnableVariable] = "1" };
            }
            Write(Path.Combine(dir, "mett_overlay.json"), library, switchable: false);
            return new Dictionary<string, string>
            {
                ["VK_ADD_LAYER_PATH"] = Join(dir, current("VK_ADD_LAYER_PATH")),
                ["VK_INSTANCE_LAYERS"] = Join(LayerName, current("VK_INSTANCE_LAYERS")),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            return new Dictionary<string, string>();
        }
    }

    private string Join(string own, string? other) =>
        string.IsNullOrEmpty(other) || other.Split(';', ':').Contains(own) ? own : own + (_windows ? ';' : ':') + other;

    private static void Write(string path, string library, bool switchable)
    {
        var layer = new JsonObject
        {
            ["name"] = LayerName,
            ["type"] = "GLOBAL",
            ["library_path"] = library,
            ["api_version"] = "1.4.0",
            ["implementation_version"] = "1",
            ["description"] = "Overlay des Mett Launchers",
            ["functions"] = new JsonObject { ["vkNegotiateLoaderLayerInterfaceVersion"] = "vkNegotiateLoaderLayerInterfaceVersion" },
        };
        if (switchable)
        {
            layer["enable_environment"] = new JsonObject { [EnableVariable] = "1" };
            layer["disable_environment"] = new JsonObject { ["DISABLE_METT_OVERLAY"] = "1" };
        }
        var text = new JsonObject { ["file_format_version"] = "1.2.0", ["layer"] = layer }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        AtomicFile.WriteAllText(path, text);
    }

    /// <summary>Frühere Fassungen; in Gebrauch befindliche bleiben liegen.</summary>
    private void RemoveOld(string keep)
    {
        foreach (var old in Directory.GetDirectories(paths.OverlayDir).Where(d => d != keep))
            FileSystemUtil.TryDeleteDirectory(old);
    }
}
