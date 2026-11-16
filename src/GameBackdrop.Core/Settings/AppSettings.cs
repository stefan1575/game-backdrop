using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameBackdrop.Core.Settings;

/// <summary>The settings file: the game list, plus any fields this version doesn't know (kept as they are).</summary>
public sealed class AppSettings
{
    [JsonPropertyName("games")]
    public List<GameEntry> Games { get; set; } = [];

    /// <summary>
    /// Whether the app starts at logon (a shortcut in the Startup folder). Null until the first
    /// run records it on, so a later "off" sticks.
    /// </summary>
    [JsonPropertyName("startAtLogon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StartAtLogon { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}

/// <summary>One game on the list, matched by its full exe path (case-insensitive).</summary>
public sealed class GameEntry
{
    [JsonPropertyName("exePath")]
    public string ExePath { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("keepMouseInside")]
    public bool KeepMouseInside { get; set; } = true;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}
