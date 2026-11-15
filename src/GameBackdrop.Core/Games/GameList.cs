using GameBackdrop.Core.Settings;

namespace GameBackdrop.Core.Games;

/// <summary>The games the controller acts on, matched by full exe path, case-insensitively.</summary>
public sealed class GameList(IEnumerable<GameEntry> games)
{
    // An entry without a path can never match; dropping it keeps matching safe whoever built the list.
    private readonly IReadOnlyList<GameEntry> _games = games.Where(g => !string.IsNullOrWhiteSpace(g?.ExePath)).ToList();

    public static GameList Empty { get; } = new([]);

    /// <summary>The enabled game whose exe is <paramref name="exePath"/>, or null.</summary>
    public GameEntry? FindEnabled(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var path = Normalise(exePath);
        return _games.FirstOrDefault(g => g.Enabled && string.Equals(Normalise(g.ExePath), path, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalise(string path) => path.Trim().Replace('/', '\\');
}
