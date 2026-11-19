using GameBackdrop.Core.Startup;

namespace GameBackdrop.Windows;

/// <summary>
/// The copy of the app running in this session, as <c>--uninstall</c> sees it: the
/// <see cref="SingleInstance"/> guard's mutex says one runs, and its quit event asks it to quit.
/// A copy running as administrator made the event with an administrators-only DACL, so a normal
/// process can't open it: that copy is out of reach.
/// </summary>
/// <param name="wait">How long <see cref="Quit"/> waits for the copy to put its games back and let go.</param>
public sealed class RunningCopy(string mutexName, string quitEventName, TimeSpan wait) : IRunningCopy
{
    /// <summary>The app's own running copy.</summary>
    public static RunningCopy ForApp(TimeSpan wait) =>
        new(SingleInstance.MutexName, SingleInstance.QuitEventNameFor(SingleInstance.MutexName), wait);

    public RunningCopyState Find()
    {
        if (!Exists(mutexName)) return RunningCopyState.None;
        try
        {
            EventWaitHandle.OpenExisting(quitEventName).Dispose();
            return RunningCopyState.Running;
        }
        catch (UnauthorizedAccessException)
        {
            return RunningCopyState.OutOfReach;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The guard is there with no quit event: a copy is just starting or ending. Quit
            // waits on the guard either way.
            return RunningCopyState.Running;
        }
    }

    public bool Quit()
    {
        try
        {
            using var quit = EventWaitHandle.OpenExisting(quitEventName);
            quit.Set();
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No copy to ask; the guard below says whether one still runs.
        }

        Mutex mutex;
        try
        {
            mutex = Mutex.OpenExisting(mutexName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(wait)) return false;
            }
            catch (AbandonedMutexException)
            {
                // The copy ended without letting go (it crashed): it runs no more, and the mutex is this one's.
            }
            mutex.ReleaseMutex();
            return true;
        }
    }

    private static bool Exists(string name)
    {
        try
        {
            Mutex.OpenExisting(name).Dispose();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
