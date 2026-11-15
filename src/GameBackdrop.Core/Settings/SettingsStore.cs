using System.Text.Json;

namespace GameBackdrop.Core.Settings;

public enum SettingsLoadStatus
{
    Loaded,
    /// <summary>No settings file yet: start empty.</summary>
    Missing,
    /// <summary>The file couldn't be read as settings: start empty, and leave the file alone until the next save.</summary>
    Corrupt,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, string? Error = null);

/// <summary>Reads and atomically writes the settings JSON file.</summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public string FilePath { get; } = filePath;

    /// <summary><c>%APPDATA%\GameBackdrop\settings.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameBackdrop", "settings.json");

    public SettingsLoadResult Load()
    {
        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(new AppSettings(), SettingsLoadStatus.Missing);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options)
                ?? throw new JsonException("The settings file holds null.");
            settings.Games ??= [];
            if (settings.Games.Any(g => g is null)) throw new JsonException("The game list holds a null entry.");
            return new(settings, SettingsLoadStatus.Loaded);
        }
        catch (JsonException e)
        {
            return new(new AppSettings(), SettingsLoadStatus.Corrupt, e.Message);
        }
    }

    /// <summary>Write the settings to a temp file beside the real one, then swap it in.</summary>
    public void Save(AppSettings settings)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
