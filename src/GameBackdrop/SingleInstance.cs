using System.Runtime.InteropServices;

namespace GameBackdrop;

/// <summary>
/// One copy of the app per user session: a named mutex says which copy runs, and a named event
/// lets a second launch ask that copy to open its list window before the second one exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\GameBackdrop.SingleInstance";
    private const string OpenListEventName = @"Local\GameBackdrop.OpenList";

    private readonly EventWaitHandle _openList;
    private readonly Mutex _mutex;

    private SingleInstance(EventWaitHandle openList, Mutex mutex)
    {
        _openList = openList;
        _mutex = mutex;
    }

    /// <summary>
    /// Become the running copy, or, if one already runs, ask it to open its list window and
    /// return null.
    /// </summary>
    public static SingleInstance? Claim()
    {
        // The event comes first: once the mutex exists, so does the running copy's event, so a
        // second launch's signal always has somewhere to go.
        var openList = new EventWaitHandle(false, EventResetMode.AutoReset, OpenListEventName);
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew) return new SingleInstance(openList, mutex);

        mutex.Dispose();
        // This launch was the user's last input, so it may hand the foreground on: the running
        // copy's list window can then come to the front instead of flashing in the taskbar.
        AllowSetForegroundWindow(ASFW_ANY);
        openList.Set();
        openList.Dispose();
        return null;
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
        _openList.Dispose();
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
