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
    private RegisteredWaitHandle? _wait;

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

    /// <summary>Run <paramref name="openList"/> on <paramref name="context"/> each time the app is launched again.</summary>
    public void OnSecondLaunch(SynchronizationContext context, Action openList)
    {
        _wait = ThreadPool.RegisterWaitForSingleObject(_openList,
            (_, _) => context.Post(_ => openList(), null), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _openList.Dispose();
        _mutex.Dispose();
    }

    private const uint ASFW_ANY = unchecked((uint)-1);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
