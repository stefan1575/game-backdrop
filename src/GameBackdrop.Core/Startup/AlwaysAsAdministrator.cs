using System.Runtime.InteropServices;
using GameBackdrop.Core.Controller;

namespace GameBackdrop.Core.Startup;

/// <summary>
/// "Always run as administrator": the app's scheduled task runs it elevated with no UAC prompt,
/// so the one prompt is the one that sets the task up. A normal launch that finds the task runs it
/// and exits; while the task is set up, its logon trigger is what starts the app at logon.
/// </summary>
public sealed class AlwaysAsAdministrator(AdministratorTask task, StartAtLogon startAtLogon)
{
    /// <summary>Whether it's on: the task is set up for this exe.</summary>
    public bool IsOn => task.IsSetUp;

    /// <summary>
    /// At launch, before anything else: with the task set up, run it and return true, and this
    /// copy is to exit, since the task starts the elevated one. A copy that is already elevated,
    /// or that the task started (which runs unelevated for a user who isn't an administrator),
    /// never runs it again. A task that can't be read or run gives a normal start.
    /// </summary>
    public bool LaunchThroughTask(bool elevated, bool startedByTask)
    {
        if (elevated || startedByTask) return false;
        try
        {
            if (!task.IsSetUp) return false;
            task.Run();
            return true;
        }
        catch (Exception e) when (IsTaskSchedulerError(e))
        {
            return false;
        }
    }

    /// <summary>
    /// Turn it on. Elevated: set the task up here, and return null. Otherwise hand the games over
    /// to an elevated copy (the UAC prompt) that sets the task up and takes over, as in
    /// <see cref="BackdropController.HandOver"/>: every game is put back before
    /// <paramref name="startElevatedCopy"/> runs, and a game that can't be put back, or a declined
    /// prompt, leaves this copy running with its games changed again.
    /// </summary>
    public HandOverResult? TurnOn(bool elevated, BackdropController controller, Func<bool> startElevatedCopy,
        int retries = 0, Action? beforeRetry = null)
    {
        if (!elevated) return controller.HandOver(startElevatedCopy, retries, beforeRetry);
        SetUp();
        return null;
    }

    /// <summary>
    /// In the elevated copy: create the task (replacing one for another exe), with a logon trigger
    /// if the settings ask for a start at logon, and remove the Startup shortcut.
    /// </summary>
    public void SetUp()
    {
        task.Register(atLogon: startAtLogon.IsWanted);
        startAtLogon.ApplyAtStart();
    }

    /// <summary>
    /// Turn it off: delete the task (this copy runs as administrator, so no prompt), and put the
    /// Startup shortcut back if the settings ask for a start at logon. This copy stays elevated
    /// until it quits; the next launch is a normal one.
    /// </summary>
    public void TurnOff()
    {
        task.Delete();
        startAtLogon.ApplyAtStart();
    }

    /// <summary>What Task Scheduler throws when a task can't be read, run or changed.</summary>
    public static bool IsTaskSchedulerError(Exception e) =>
        e is COMException or UnauthorizedAccessException or IOException or InvalidOperationException;
}
