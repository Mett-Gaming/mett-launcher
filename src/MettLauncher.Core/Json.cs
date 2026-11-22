using System.Text.Json;
using System.Text.Json.Serialization;

namespace MettLauncher.Core;

internal static class Json
{
    /// <summary>Für eigene Dateien (library.json, settings.json, Caches) und games.json.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Für GitHub-API-Antworten (snake_case).</summary>
    public static readonly JsonSerializerOptions GitHub = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
