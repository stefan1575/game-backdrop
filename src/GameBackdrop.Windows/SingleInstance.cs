using System.Runtime.InteropServices;

namespace GameBackdrop.Windows;

/// <summary>
/// One copy of the app per user session, so two copies never fight over a game window: a named
/// mutex, held from start to quit, says which copy runs, and a named event lets a second launch
/// ask that copy to open its list window before the second one exits. A copy that takes over from
/// a running one (the restart as administrator) waits for that copy to quit, and so to put its
/// games back, before it touches any window. Dispose it on the thread that claimed it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\GameBackdrop.SingleInstance";
    public const string OpenListEventName = @"Local\GameBackdrop.OpenList";

    private readonly EventWaitHandle _openList;
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(EventWaitHandle openList, Mutex mutex)
    {
        _openList = openList;
        _mutex = mutex;
    }

    /// <summary>
    /// Become the running copy, or return null when another copy runs. A normal launch
    /// (<paramref name="waitForRunningCopy"/> zero) doesn't wait: it asks the running copy to open
    /// its list window. A takeover waits up to <paramref name="waitForRunningCopy"/> for the
    /// running copy to quit, and asks it nothing.
    /// </summary>
    public static SingleInstance? Claim(TimeSpan waitForRunningCopy = default) =>
        Claim(MutexName, OpenListEventName, waitForRunningCopy);

    /// <inheritdoc cref="Claim(TimeSpan)"/>
    /// <param name="mutexName">The mutex's name (tests use their own).</param>
    /// <param name="openListEventName">The open-list event's name (tests use their own).</param>
    public static SingleInstance? Claim(string mutexName, string openListEventName, TimeSpan waitForRunningCopy)
    {
        // The event comes first: once the mutex exists, so does the running copy's event, so a
        // second launch's signal always has somewhere to go. A copy running as administrator
        // makes both with an administrators-only DACL, which a normal copy can't open: that copy
        // runs, and this one can't reach it, so it just exits.
        if (TryOpen(() => new EventWaitHandle(false, EventResetMode.AutoReset, openListEventName)) is not { } openList)
            return null;
        if (TryOpen(() => new Mutex(initiallyOwned: false, mutexName)) is not { } mutex)
        {
            openList.Dispose();
            return null;
        }
        if (Acquire(mutex, waitForRunningCopy)) return new SingleInstance(openList, mutex);

        mutex.Dispose();
        if (waitForRunningCopy == TimeSpan.Zero)
        {
            // This launch was the user's last input, so it may hand the foreground on: the running
            // copy's list window can then come to the front instead of flashing in the taskbar.
            AllowSetForegroundWindow(ASFW_ANY);
            openList.Set();
        }
        openList.Dispose();
        return null;
    }

    private static T? TryOpen<T>(Func<T> open) where T : class
    {
        try
        {
            return open();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Acquire(Mutex mutex, TimeSpan wait)
    {
        try
        {
            return mutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            // The copy before ended without letting go (it crashed); the mutex is this one's now.
            return true;
        }
    }

    /// <summary>
    /// Run <paramref name="openList"/> on <paramref name="context"/> each time the app is launched
    /// again, until the returned registration is disposed. Dispose it before the context's UI goes:
    /// disposal waits for a signal being handled, so none is posted to a torn-down context.
    /// </summary>
    public IDisposable OnSecondLaunch(SynchronizationContext context, Action openList)
    {
        var wait = ThreadPool.RegisterWaitForSingleObject(_openList,
            (_, _) => context.Post(_ => openList(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        return new Listening(wait);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _openList.Dispose();
        // Let go before closing, so a copy taking over gets it at once rather than as abandoned.
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private sealed class Listening(RegisteredWaitHandle wait) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Unregister signals once no callback is running; the callback only posts, so this is short.
            using var done = new ManualResetEvent(false);
            if (wait.Unregister(done)) done.WaitOne();
        }
    }

    private const uint ASFW_ANY = unchecked((uint)-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
