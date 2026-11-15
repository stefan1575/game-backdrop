using System.Runtime.InteropServices;
using System.Text;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.WindowSystem;
using static GameBackdrop.Windows.NativeMethods;

namespace GameBackdrop.Windows;

/// <summary>
/// The window system port on Win32: thin calls on window handles, and window events from
/// SetWinEventHook (out of context, so nothing is loaded into any other process).
/// Create it on a thread with a message loop and a SynchronizationContext (the WinForms UI
/// thread); events are posted back to that context, so the controller never runs re-entrantly
/// inside a Win32 call.
/// </summary>
public sealed class Win32WindowSystem : IWindowSystem, IDisposable
{
    private static readonly (uint Min, uint Max)[] HookedEvents =
    [
        (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
        (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
        (EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW),
        (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
    ];

    private readonly SynchronizationContext _context;
    private readonly WinEventProc _callback; // kept alive for as long as the hooks are
    private readonly List<nint> _hooks = [];

    public Win32WindowSystem()
    {
        _context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Create the window system on a UI thread with a SynchronizationContext.");
        _callback = OnWinEvent;
        foreach (var (min, max) in HookedEvents)
        {
            var hook = SetWinEventHook(min, max, 0, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (hook == 0) throw new InvalidOperationException($"SetWinEventHook(0x{min:X}) failed.");
            _hooks.Add(hook);
        }
    }

    public event Action<WindowEvent>? WindowEvent;

    public IReadOnlyList<WindowHandle> TopLevelWindows()
    {
        var handles = new List<WindowHandle>();
        EnumWindows((hwnd, _) => { handles.Add(new WindowHandle(hwnd)); return true; }, 0);
        return handles;
    }

    public WindowInfo? Describe(WindowHandle window)
    {
        var hwnd = window.Value;
        if (!IsWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out var processId);
        if (!GetWindowRect(hwnd, out var outer) || !GetClientRect(hwnd, out var client)) return null;
        var origin = new POINT();
        ClientToScreen(hwnd, ref origin);

        var style = new WindowStyle((uint)GetWindowLongPtr(hwnd, GWL_STYLE), (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE));
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return null;

        return new WindowInfo(
            window,
            (int)processId,
            ProcessPath(processId),
            ToRect(outer),
            new Rect(origin.X, origin.Y, origin.X + client.Right, origin.Y + client.Bottom),
            style,
            IsWindowVisible(hwnd),
            IsIconic(hwnd),
            GetWindow(hwnd, GW_OWNER) != 0,
            new MonitorInfo(new MonitorHandle(monitor), ToRect(monitorInfo.rcMonitor), ToRect(monitorInfo.rcWork)));
    }

    public ChangeResult SetFrame(WindowHandle window, WindowFrame frame)
    {
        var hwnd = window.Value;
        if (!IsWindow(hwnd)) return ChangeResult.WindowGone;
        var previousStyle = (uint)GetWindowLongPtr(hwnd, GWL_STYLE);
        var previousExStyle = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE);

        if (TrySetLong(hwnd, GWL_STYLE, frame.Style.Style, out var error)
            && TrySetLong(hwnd, GWL_EXSTYLE, frame.Style.ExStyle, out error)
            && TrySetPos(hwnd, frame.Outer, out error))
        {
            return ChangeResult.Ok;
        }

        if (!IsWindow(hwnd)) return ChangeResult.WindowGone;
        // Don't leave a half-changed window behind: put back whatever styles did change.
        TrySetLong(hwnd, GWL_STYLE, previousStyle, out _);
        TrySetLong(hwnd, GWL_EXSTYLE, previousExStyle, out _);
        return error == ERROR_ACCESS_DENIED ? ChangeResult.AccessDenied : ChangeResult.Failed;
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
    }

    private static bool TrySetPos(nint hwnd, Rect outer, out int error)
    {
        var ok = SetWindowPos(hwnd, 0, outer.Left, outer.Top, outer.Width, outer.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    /// <summary>SetWindowLongPtr returns the previous value, which can be 0, so failure is told apart by the last error.</summary>
    private static bool TrySetLong(nint hwnd, int index, uint value, out int error)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(hwnd, index, (nint)value);
        error = previous == 0 ? Marshal.GetLastPInvokeError() : 0;
        return error == 0;
    }

    private static string? ProcessPath(uint processId)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static Rect ToRect(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == 0 || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        WindowEventKind kind;
        switch (eventType)
        {
            case EVENT_OBJECT_SHOW: kind = WindowEventKind.Shown; break;
            case EVENT_OBJECT_DESTROY: kind = WindowEventKind.Destroyed; break;
            case EVENT_SYSTEM_FOREGROUND: kind = WindowEventKind.Foreground; break;
            case EVENT_OBJECT_LOCATIONCHANGE: kind = WindowEventKind.LocationChanged; break;
            case EVENT_SYSTEM_MINIMIZESTART: kind = WindowEventKind.MinimizeStart; break;
            case EVENT_SYSTEM_MINIMIZEEND: kind = WindowEventKind.MinimizeEnd; break;
            default: return;
        }
        // A destroyed window can't be asked for its ancestor; the controller ignores handles it doesn't track.
        if (kind != WindowEventKind.Destroyed && GetAncestor(hwnd, GA_ROOT) != hwnd) return;

        var e = new WindowEvent(kind, new WindowHandle(hwnd));
        _context.Post(_ => WindowEvent?.Invoke(e), null);
    }
}
