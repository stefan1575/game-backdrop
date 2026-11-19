namespace GameBackdrop.Core.Startup;

/// <summary>What asking the running copy to quit came to.</summary>
public enum QuitRequest
{
    /// <summary>No copy of this settings scope runs.</summary>
    NoneRunning,

    /// <summary>A copy runs and was asked to quit.</summary>
    Asked,

    /// <summary>A copy runs as administrator, and only a copy running as administrator may ask it.</summary>
    NeedsAdministrator,
}

/// <summary>
/// The copy of the app running in this session, as seen from another copy. One copy runs per
/// session whatever its settings file, but each settings scope (the real one, or a test run's
/// <c>--settings</c> file) has its own quit signal, so an uninstall only quits a copy of its own.
/// </summary>
public interface IRunningCopy
{
    /// <summary>
    /// Ask the running copy of this scope to quit. It quits only once it has put every game back;
    /// a game it can't put back keeps it running.
    /// </summary>
    QuitRequest AskToQuit();

    /// <summary>
    /// Wait a short while for the running copy to let go, and hold its place so no copy starts
    /// meanwhile: null when a copy still runs. Dispose the hold to let a copy start again.
    /// </summary>
    IDisposable? TryHold();
}

/// <summary>What <see cref="Uninstall.Run"/> came to.</summary>
public enum UninstallResult
{
    /// <summary>Nothing of the app's is left outside its folder, and no copy of this scope runs.</summary>
    Done,

    /// <summary>Run it again as administrator: the task, or the copy running, needs that.</summary>
    NeedsAdministrator,

    /// <summary>The running copy didn't quit (a game it couldn't put back), so nothing was removed.</summary>
    StillRunning,
}

/// <summary>
/// Tidy up before the app's files are deleted (a package manager's uninstall): quit the running
/// copy, which puts every game back, then remove what starts the app, the Startup shortcut and the
/// Always run as administrator task, whichever exe they point at. The settings file stays, so a
/// reinstall keeps the game list.
/// </summary>
public sealed class Uninstall(IRunningCopy running, IStartupShortcut shortcut, IScheduledTask task)
{
    /// <summary>With a quarter-second <see cref="IRunningCopy.TryHold"/>, a minute.</summary>
    public const int DefaultAttempts = 240;

    public UninstallResult Run(bool elevated, int attempts = DefaultAttempts)
    {
        // The task runs at run level highest, and only an administrator may delete it. Asking for
        // that before anything else means the one prompt comes before the app quits, not after.
        if (!elevated && task.Read() is not null) return UninstallResult.NeedsAdministrator;

        var asked = false;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            // Asked until a copy has been: one that starts after a "none running" is asked too.
            // Once asked, it's left to put its games back rather than asked again.
            if (!asked)
            {
                var request = running.AskToQuit();
                if (request == QuitRequest.NeedsAdministrator) return NeedsAdministrator(elevated);
                asked = request == QuitRequest.Asked;
            }

            // Held while the shortcut and task go, so a logon start can't come back between them.
            using (var hold = running.TryHold())
            {
                if (hold is not null)
                {
                    Remove();
                    return UninstallResult.Done;
                }
            }
            if (asked) continue;

            // The guard is held. A copy makes its quit signal before it takes the guard, so if no
            // signal of this scope is there to ask now, after the guard was found held, the holder
            // is another scope's copy: leave it be. One of this scope that took over meanwhile is
            // asked here, and waited for.
            var afterHeld = running.AskToQuit();
            if (afterHeld == QuitRequest.NeedsAdministrator) return NeedsAdministrator(elevated);
            if (afterHeld == QuitRequest.Asked)
            {
                asked = true;
                continue;
            }
            Remove();

            // Removed without the guard: if the other scope's copy let go and one of this scope
            // started meanwhile (and put its shortcut back), it's asked, waited for, and removed after.
            var afterRemove = running.AskToQuit();
            if (afterRemove == QuitRequest.NeedsAdministrator) return NeedsAdministrator(elevated);
            if (afterRemove == QuitRequest.Asked)
            {
                asked = true;
                continue;
            }
            return UninstallResult.Done;
        }
        return UninstallResult.StillRunning;
    }

    /// <summary>A copy of this scope runs as administrator: rerun as administrator, or, already that, it can't be asked.</summary>
    private static UninstallResult NeedsAdministrator(bool elevated) =>
        elevated ? UninstallResult.StillRunning : UninstallResult.NeedsAdministrator;

    private void Remove()
    {
        shortcut.Remove();
        task.Delete();
    }
}
