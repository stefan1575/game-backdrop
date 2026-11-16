using GameBackdrop.Core.Games;

namespace GameBackdrop.Core.Settings;

/// <summary>The settings file couldn't be written; the list is left as it was.</summary>
public sealed class SettingsSaveException(string filePath, Exception inner)
    : Exception($"The settings file couldn't be saved: {filePath}\n{inner.Message}", inner);

public enum AddResult
{
    Added,
    /// <summary>A game with the same exe (compared as <see cref="GameEntry.IsFor"/> does) is already listed.</summary>
    AlreadyListed,
    /// <summary>The path isn't a full path to a file.</summary>
    NotAFullPath,
}

/// <summary>
/// The game list as the list window edits it. Every change is written to the settings file at
/// once (atomically, by <see cref="SettingsStore.Save"/>) and only then made here: a change that
/// can't be saved throws <see cref="SettingsSaveException"/> and leaves the list as it was, so the list never shows what the file
/// doesn't hold.
/// </summary>
/// <remarks>
/// Entries are never changed in place: each change puts new <see cref="GameEntry"/> objects in a
/// new list. The controller keeps the entries of the list it was given, so nothing it holds
/// changes under it; it sees a change only through <see cref="Changed"/>. Callers must not change
/// the entries they read either.
/// </remarks>
public sealed class GameListModel
{
    private readonly SettingsStore _store;
    private AppSettings _settings;
    // A corrupt file not yet copied aside: the first save copies it first, so it isn't lost.
    private bool _corruptFileToKeep;

    private GameListModel(SettingsStore store, SettingsLoadResult loaded)
    {
        _store = store;
        _settings = loaded.Settings;
        LoadStatus = loaded.Status;
        LoadError = loaded.Error;
        _corruptFileToKeep = loaded.Status == SettingsLoadStatus.Corrupt;
    }

    /// <summary>
    /// Read the settings file. A missing or corrupt one gives an empty list (see
    /// <see cref="LoadStatus"/>); a corrupt one is left as it is until the first change, which
    /// copies it to <c>&lt;name&gt;.corrupt-&lt;time&gt;.json</c> beside it before saving over it.
    /// </summary>
    public static GameListModel Load(SettingsStore store) => new(store, store.Load());

    public SettingsLoadStatus LoadStatus { get; }

    /// <summary>Why a corrupt file couldn't be read.</summary>
    public string? LoadError { get; }

    /// <summary>A saved change to the list. Raised after the file is written, on the thread that made the change.</summary>
    public event Action? Changed;

    public IReadOnlyList<GameEntry> Games => _settings.Games;

    /// <summary>The list as the controller matches it.</summary>
    public GameList ToGameList() => new(_settings.Games);

    /// <summary>Add a game, switched on and keeping the mouse inside.</summary>
    public AddResult Add(string exePath, string? displayName)
    {
        exePath = exePath.Trim();
        if (!Path.IsPathFullyQualified(exePath)) return AddResult.NotAFullPath;
        if (_settings.Games.Any(g => g.IsFor(exePath))) return AddResult.AlreadyListed;
        var entry = new GameEntry { ExePath = exePath, DisplayName = NameOrNull(displayName) };
        Commit([.. _settings.Games, entry]);
        return AddResult.Added;
    }

    /// <summary>Give a game a new name; a blank one goes back to the exe's name. False if the game isn't listed.</summary>
    public bool Rename(string exePath, string? displayName) =>
        Change(exePath, g => g.DisplayName = NameOrNull(displayName));

    /// <summary>Switch a game's backdrop on or off. False if the game isn't listed.</summary>
    public bool SetEnabled(string exePath, bool enabled) => Change(exePath, g => g.Enabled = enabled);

    /// <summary>Switch a game's "keep mouse inside" on or off. False if the game isn't listed.</summary>
    public bool SetKeepMouseInside(string exePath, bool keepInside) => Change(exePath, g => g.KeepMouseInside = keepInside);

    /// <summary>Take a game off the list (every entry for its exe). False if it isn't listed.</summary>
    public bool Remove(string exePath)
    {
        if (!_settings.Games.Any(g => g.IsFor(exePath))) return false;
        Commit(_settings.Games.Where(g => !g.IsFor(exePath)).ToList());
        return true;
    }

    /// <summary>
    /// Change every entry for the exe. Add refuses a second one, but a file written by hand can
    /// list an exe twice (other case or slashes); the controller matches the first one on, so a
    /// change to only one of them could leave the game on.
    /// </summary>
    private bool Change(string exePath, Action<GameEntry> change)
    {
        if (!_settings.Games.Any(g => g.IsFor(exePath))) return false;
        Commit(_settings.Games.Select(g =>
        {
            if (!g.IsFor(exePath)) return g;
            var changed = g.Copy();
            change(changed);
            return changed;
        }).ToList());
        return true;
    }

    private void KeepCorruptFile()
    {
        var path = _store.FilePath;
        var copy = Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        // Gone since it was read: nothing to keep.
        if (File.Exists(path)) File.Copy(path, copy, overwrite: false);
        _corruptFileToKeep = false;
    }

    private static string? NameOrNull(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary>Save the settings with this game list, then make it the current one and announce it.</summary>
    private void Commit(List<GameEntry> games)
    {
        var next = new AppSettings { Games = games, UnknownFields = _settings.UnknownFields };
        try
        {
            if (_corruptFileToKeep) KeepCorruptFile();
            _store.Save(next);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SettingsSaveException(_store.FilePath, e);
        }
        _settings = next;
        Changed?.Invoke();
    }
}
