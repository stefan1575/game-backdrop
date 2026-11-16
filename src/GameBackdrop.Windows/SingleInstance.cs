namespace GameBackdrop.Windows;

/// <summary>
/// The guard that keeps one copy of the app running, so two copies never fight over a game
/// window: a named mutex, held from start to quit. A copy that takes over from a running one (the
/// restart as administrator) waits for that copy to quit, and so to put its games back, before it
/// touches any window. Dispose it on the thread that acquired it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// The guard, or null when another copy holds it and doesn't let go within
    /// <paramref name="waitForRunningCopy"/> (zero for a normal start: don't wait).
    /// </summary>
    public static SingleInstance? TryAcquire(string name, TimeSpan waitForRunningCopy)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (UnauthorizedAccessException)
        {
            // A copy running as administrator made the mutex with a DACL this (normal) copy can't
            // open: that copy is running.
            return null;
        }

        try
        {
            if (mutex.WaitOne(waitForRunningCopy)) return new SingleInstance(mutex);
        }
        catch (AbandonedMutexException)
        {
            // The copy before ended without letting go (it crashed); the mutex is ours now.
            return new SingleInstance(mutex);
        }
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
