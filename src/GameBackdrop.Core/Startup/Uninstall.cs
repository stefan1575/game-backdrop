namespace GameBackdrop.Core.Startup;

/// <summary>What asking the running copy to quit came to.</summary>
public enum QuitRequest
{
    /// <summary>No copy runs.</summary>
    NoneRunning,

    /// <summary>A copy runs and was asked to quit.</summary>
    Asked,

    /// <summary>A copy runs as administrator, and only a copy running as administrator may ask it.</summary>
    NeedsAdministrator,
}

/// <summary>The copy of the app running in this session, as seen from another copy.</summary>
public interface IRunningCopy
{
    /// <summary>Ask the running copy to quit, as its tray menu's Quit does: it puts every game back first.</summary>
    QuitRequest AskToQuit();

    /// <summary>
    /// Wait for the running copy to quit, and hold its place so no copy starts meanwhile: null
    /// when it didn't quit in time. Dispose the hold to let a copy start again.
    /// </summary>
    IDisposable? WaitUntilGone();
}

/// <summary>What <see cref="Uninstall.Run"/> came to.</summary>
public enum UninstallResult
{
    /// <summary>Nothing of the app's is left outside its folder, and no copy runs.</summary>
    Done,

    /// <summary>Run it again as administrator: the task, or the copy running, needs that.</summary>
    NeedsAdministrator,

    /// <summary>The running copy didn't quit, so nothing was removed.</summary>
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
    public UninstallResult Run(bool elevated)
    {
        // The task runs at run level highest, and only an administrator may delete it. Asking for
        // that before anything else means the one prompt comes before the app quits, not after.
        if (!elevated && task.Read() is not null) return UninstallResult.NeedsAdministrator;
        if (running.AskToQuit() == QuitRequest.NeedsAdministrator)
            return elevated ? UninstallResult.StillRunning : UninstallResult.NeedsAdministrator;

        // Held while the shortcut and task go, so a logon start can't come back between them.
        using var gone = running.WaitUntilGone();
        if (gone is null) return UninstallResult.StillRunning;
        shortcut.Remove();
        task.Delete();
        return UninstallResult.Done;
    }
}
