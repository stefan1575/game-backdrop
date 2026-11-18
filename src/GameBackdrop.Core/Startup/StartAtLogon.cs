using GameBackdrop.Core.Settings;

namespace GameBackdrop.Core.Startup;

/// <summary>
/// The "Start at logon" choice: the Startup shortcut follows what the settings file records.
/// Unrecorded (the first run) means on, so the first run creates the shortcut and records it;
/// once "off" is recorded, no run creates it again. The settings file is read fresh for every
/// change, so a hand edit made while the app runs isn't overwritten.
/// </summary>
public sealed class StartAtLogon(SettingsStore store, IStartupShortcut shortcut)
{
    /// <summary>Whether the app will start at logon: the shortcut is there.</summary>
    public bool IsOn => shortcut.Exists;

    /// <summary>
    /// Whether the choice can be recorded: false while the settings file can't be read, which is
    /// left alone rather than overwritten. That covers a file that isn't valid settings and one
    /// that can't be opened right now (another program holds it, or access is denied); this
    /// never throws for either, since the tray asks every time its menu opens.
    /// </summary>
    public bool CanRecord
    {
        get
        {
            try
            {
                return store.Load().Status != SettingsLoadStatus.Corrupt;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// At app start. Recorded on, or nothing recorded yet: create the shortcut (replacing any old
    /// one, so it points at this exe even if the exe moved) and record on. Recorded off, or an
    /// unreadable settings file: leave the shortcut alone. If the shortcut can't be created the
    /// exception propagates and nothing is recorded, so the next start tries again.
    /// </summary>
    public void ApplyAtStart()
    {
        var loaded = store.Load();
        if (loaded.Status == SettingsLoadStatus.Corrupt || loaded.Settings.StartAtLogon == false) return;

        shortcut.Create();
        if (loaded.Settings.StartAtLogon == true) return;
        loaded.Settings.StartAtLogon = true;
        store.Save(loaded.Settings);
    }

    /// <summary>
    /// Turn it on or off and record the choice. Throws <see cref="InvalidOperationException"/>,
    /// changing nothing, while the settings file can't be read.
    /// </summary>
    public void Set(bool on)
    {
        var loaded = store.Load();
        if (loaded.Status == SettingsLoadStatus.Corrupt)
            throw new InvalidOperationException($"The settings file can't be read, so the choice can't be recorded: {store.FilePath}");

        if (on) shortcut.Create();
        else shortcut.Remove();
        loaded.Settings.StartAtLogon = on;
        store.Save(loaded.Settings);
    }
}
