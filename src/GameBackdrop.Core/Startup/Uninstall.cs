namespace GameBackdrop.Core.Startup;

/// <summary>What <see cref="Uninstall.Run"/> did: done, or not, with why.</summary>
/// <param name="ShownByElevatedCopy">The message is the elevated copy's, which it has shown itself.</param>
public sealed record UninstallResult(bool Done, string? Message = null, bool ShownByElevatedCopy = false)
{
    public static UninstallResult Success { get; } = new(true);

    public static UninstallResult Failed(string message, bool shownByElevatedCopy = false) => new(false, message, shownByElevatedCopy);
}

/// <summary>
/// <c>GameBackdrop.exe --uninstall</c>: remove everything the app puts outside its own folder, for
/// this user. The running copy is asked to quit first, so it puts every game back, and then the
/// Startup shortcut and the scheduled task go. The settings file stays. The task, or a running copy
/// that is elevated, needs administrator: only then is an elevated copy of this run started (the
/// one UAC prompt), which does all of it.
/// </summary>
/// <param name="task">The app's task by its name, whichever exe it starts: one left by a copy that
/// moved is the app's too.</param>
public sealed class Uninstall(IRunningCopy copy, IStartupShortcut shortcut, IScheduledTask task)
{
    /// <param name="elevated">This process runs as administrator.</param>
    /// <param name="runElevated">Start this run again as administrator and wait for it: its exit
    /// code, or null when it didn't start (the UAC prompt was declined).</param>
    public UninstallResult Run(bool elevated, Func<int?> runElevated)
    {
        try
        {
            var running = copy.Find();
            if (!elevated && (running == RunningCopyState.OutOfReach || task.Read() is not null))
            {
                return runElevated() switch
                {
                    0 => UninstallResult.Success,
                    null => UninstallResult.Failed(
                        "Uninstalling Game Backdrop needs administrator (for its Always run as administrator task, or a copy " +
                        "running as administrator), and it didn't get it. Nothing was removed."),
                    var code => UninstallResult.Failed($"Uninstalling Game Backdrop as administrator failed (exit code {code}).",
                        shownByElevatedCopy: true),
                };
            }

            // First, so the running copy puts every game back while what it set up is still there.
            if (running != RunningCopyState.None && !copy.Quit())
                return UninstallResult.Failed("The running Game Backdrop didn't quit, so nothing was removed. Quit it from the tray and try again.");

            shortcut.Remove();
            task.Delete();
            return UninstallResult.Success;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException || AlwaysAsAdministrator.IsTaskSchedulerError(e))
        {
            return UninstallResult.Failed($"Couldn't uninstall Game Backdrop: {e.Message}");
        }
    }
}
