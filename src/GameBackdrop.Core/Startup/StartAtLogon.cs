using GameBackdrop.Core.Settings;

namespace GameBackdrop.Core.Startup;

/// <summary>
/// The "Start at logon" choice: what starts the app at logon follows what the settings file
/// records. That is the Startup shortcut, or, while the app always runs as administrator, a logon
/// trigger on its scheduled task, with the shortcut removed so the app never starts twice.
/// Unrecorded (the first run) means on, so the first run sets it up and records it; once "off" is
/// recorded, no run sets it up again. The settings file is read fresh for every change, so a hand
/// edit made while the app runs isn't overwritten.
/// </summary>
public sealed class StartAtLogon(SettingsStore store, IStartupShortcut shortcut, AdministratorTask? task = null)
{
    /// <summary>Whether the app will start at logon: the task's logon trigger, or else the shortcut.</summary>
    public bool IsOn => TaskIsSetUp ? task!.StartsAtLogon : shortcut.Exists;

    private bool TaskIsSetUp => task?.IsSetUp == true;

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
    /// Whether the settings ask for a start at logon: recorded on, or nothing recorded yet (the
    /// first run turns it on). False for an unreadable settings file.
    /// </summary>
    public bool IsWanted
    {
        get
        {
            var loaded = store.Load();
            return loaded.Status != SettingsLoadStatus.Corrupt && loaded.Settings.StartAtLogon != false;
        }
    }

    /// <summary>
    /// At app start. With the task set up: the shortcut is removed, and the task's logon trigger
    /// follows the record (nothing recorded counts as on, and is recorded). Without it: recorded
    /// on, or nothing recorded yet, creates the shortcut (replacing any old one, so it points at
    /// this exe even if the exe moved) and records on; recorded off leaves the shortcut alone. An
    /// unreadable settings file leaves everything alone. If the change can't be made the exception
    /// propagates and nothing is recorded, so the next start tries again.
    /// </summary>
    public void ApplyAtStart()
    {
        var loaded = store.Load();
        if (loaded.Status == SettingsLoadStatus.Corrupt) return;
        var wanted = loaded.Settings.StartAtLogon != false;

        if (TaskIsSetUp)
        {
            if (task!.StartsAtLogon != wanted) task.Register(atLogon: wanted);
            shortcut.Remove();
        }
        else
        {
            if (!wanted) return;
            shortcut.Create();
        }

        if (loaded.Settings.StartAtLogon is not null) return;
        loaded.Settings.StartAtLogon = true;
        store.Save(loaded.Settings);
    }

    /// <summary>
    /// Turn it on or off and record the choice. Throws <see cref="InvalidOperationException"/>,
    /// changing nothing, while the settings file can't be read. With the task set up this changes
    /// its logon trigger, which needs administrator.
    /// </summary>
    public void Set(bool on)
    {
        var loaded = store.Load();
        if (loaded.Status == SettingsLoadStatus.Corrupt)
            throw new InvalidOperationException($"The settings file can't be read, so the choice can't be recorded: {store.FilePath}");

        if (TaskIsSetUp)
        {
            task!.Register(atLogon: on);
            shortcut.Remove();
        }
        else if (on) shortcut.Create();
        else shortcut.Remove();
        loaded.Settings.StartAtLogon = on;
        store.Save(loaded.Settings);
    }
}
