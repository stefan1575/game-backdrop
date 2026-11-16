using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.WindowSystem;
using static GameBackdrop.Windows.NativeMethods;

namespace GameBackdrop.Windows;

/// <summary>
/// The window system port on Win32: thin calls on window handles, and window events from
/// SetWinEventHook (out of context, so nothing is loaded into any other process). Backdrops are
/// raw Win32 windows of this process, owned by the thread that created the port.
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
        (EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE),
        (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
    ];

    private readonly SynchronizationContext _context;
    private readonly WinEventProc _callback; // kept alive for as long as the hooks are
    private readonly List<nint> _hooks = [];

    // Each window's lifetime number, given the first time it is described and dropped when its
    // destroy event reaches this thread (WindowInfo.Lifetime). Only touched on this thread: the
    // hook callback runs here too.
    private readonly Dictionary<nint, long> _lifetimes = [];
    private long _nextLifetime = 1;

    // The backdrop window class is registered once per process; its window procedure finds the
    // port that made each backdrop here. The delegate is static, so it lives as long as the class.
    private const string BackdropClass = "GameBackdrop.Backdrop";
    private static readonly WndProc BackdropProc = OnBackdropMessage;
    private static readonly Lazy<string> BackdropClassRegistered = new(RegisterBackdropClass);
    private static readonly ConcurrentDictionary<nint, Win32WindowSystem> BackdropOwners = new();
    private readonly HashSet<nint> _backdrops = [];

    // The shell's taskbar list, created on first use on this thread.
    private ITaskbarList2? _taskbar;

    // A message nobody ever posts (registered for this alone): CatchUp's PeekMessage filters on
    // it, so it never stops at a posted message.
    private static readonly uint NeverPosted = RegisterWindowMessage("GameBackdrop.CatchUp.NeverPosted");

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

    public event Action<BackdropHandle>? BackdropClicked;

    public IReadOnlyList<WindowHandle> TopLevelWindows()
    {
        var handles = new List<WindowHandle>();
        EnumWindows((hwnd, _) => { handles.Add(new WindowHandle(hwnd)); return true; }, 0);
        return handles;
    }

    public WindowHandle? ForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == 0 ? null : new WindowHandle(hwnd);
    }

    public WindowInfo? Describe(WindowHandle window)
    {
        CatchUp();
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
        if (!_lifetimes.TryGetValue(hwnd, out var lifetime)) _lifetimes[hwnd] = lifetime = _nextLifetime++;

        return new WindowInfo(
            window,
            lifetime,
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

    public Size OuterSizeFor(WindowHandle window, WindowStyle style, Size clientSize)
    {
        // A top-level window's menu bar isn't a style bit: it stays attached when the caption goes.
        var hwnd = window.Value;
        var rect = new RECT { Right = clientSize.Width, Bottom = clientSize.Height };
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0 || !AdjustWindowRectExForDpi(ref rect, style.Style, GetMenu(hwnd) != 0, style.ExStyle, dpi)) return clientSize;
        return new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public ChangeResult SetFrame(WindowHandle window, WindowFrame frame)
    {
        var hwnd = window.Value;
        if (!IsWindow(hwnd)) return ChangeResult.WindowGone;
        var previousStyle = (uint)GetWindowLongPtr(hwnd, GWL_STYLE);
        var previousExStyle = (uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE);

        if (!TrySetLong(hwnd, GWL_STYLE, frame.Style.Style, out var error))
        {
            // Nothing has changed yet.
            if (!IsWindow(hwnd)) return ChangeResult.WindowGone;
            return error == ERROR_ACCESS_DENIED ? ChangeResult.AccessDenied : ChangeResult.Failed;
        }
        if (TrySetLong(hwnd, GWL_EXSTYLE, frame.Style.ExStyle, out error) && TrySetRect(hwnd, frame, out error))
        {
            return ChangeResult.Ok;
        }

        if (!IsWindow(hwnd)) return ChangeResult.WindowGone;
        // Don't leave a half-changed window behind: put back the styles, and say so if that fails too.
        var rolledBack = TrySetLong(hwnd, GWL_STYLE, previousStyle, out _)
            & TrySetLong(hwnd, GWL_EXSTYLE, previousExStyle, out _)
            & SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        if (!rolledBack) return ChangeResult.PartlyChanged;
        return error == ERROR_ACCESS_DENIED ? ChangeResult.AccessDenied : ChangeResult.Failed;
    }

    public BackdropHandle CreateBackdrop()
    {
        var hwnd = CreateWindowEx(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, BackdropClassRegistered.Value, null, WS_POPUP,
            0, 0, 0, 0, 0, 0, GetModuleHandle(null), 0);
        if (hwnd == 0) throw new InvalidOperationException($"CreateWindowEx(backdrop) failed: {Marshal.GetLastPInvokeError()}.");
        BackdropOwners[hwnd] = this;
        _backdrops.Add(hwnd);
        return new BackdropHandle(hwnd);
    }

    // Show and hide only act on a backdrop this port still holds: a destroyed one's handle may
    // since belong to another window.
    public void ShowBackdrop(BackdropHandle backdrop, Rect bounds, WindowHandle behind)
    {
        if (!_backdrops.Contains(backdrop.Value)) return;
        // Windows makes a window the foreground (and reports it) a moment before the window's own
        // thread raises it, longer for a busy game: ordered behind it in that gap, the backdrop
        // would stay under every window the game then jumps over. So the foreground window is put
        // at the top of its band (topmost or not) first, where Windows is about to put it anyway.
        // Any other window keeps its place.
        var foreground = GetForegroundWindow() == behind.Value;
        if (foreground)
            SetWindowPos(behind.Value, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        // A window ordered directly behind a topmost one becomes topmost itself (it is then above
        // any topmost window below that one), so behind a topmost game the backdrop must end up
        // among the ordinary windows. While the game is the foreground it goes to their top: it is
        // ordered behind the game, then demoted below (HWND_NOTOPMOST puts a topmost window right
        // below every topmost window). HWND_TOP can't do this: Windows ignores it from a process
        // that isn't the foreground, as this app isn't. Otherwise it goes to the bottom of the Z
        // order, so it never covers the window the user is in: its own place is no safe one (a
        // new window made while the app holds the foreground starts above every ordinary window).
        // When the game comes to the front, its Foreground event brings the backdrop up.
        var insertAfter = !IsTopmost(behind.Value) || foreground ? behind.Value : HWND_BOTTOM;
        SetWindowPos(backdrop.Value, insertAfter, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
        // Demote a backdrop that is topmost now: ordered behind a topmost game in front, or behind
        // a game that turned topmost between the check and the call.
        if (IsTopmost(backdrop.Value))
            SetWindowPos(backdrop.Value, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private static bool IsTopmost(nint hwnd) => ((uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    public void HideBackdrop(BackdropHandle backdrop)
    {
        if (_backdrops.Contains(backdrop.Value)) ShowWindow(backdrop.Value, SW_HIDE);
    }

    public void DestroyBackdrop(BackdropHandle backdrop)
    {
        if (!_backdrops.Remove(backdrop.Value)) return;
        BackdropOwners.TryRemove(backdrop.Value, out _);
        DestroyWindow(backdrop.Value);
    }

    public ChangeResult MarkFullscreen(WindowHandle window, bool fullscreen)
    {
        if (!IsWindow(window.Value)) return ChangeResult.WindowGone;
        try
        {
            if (_taskbar is null)
            {
                var taskbar = (ITaskbarList2)new TaskbarList();
                taskbar.HrInit();
                _taskbar = taskbar;
            }
            _taskbar.MarkFullscreenWindow(window.Value, fullscreen);
            return ChangeResult.Ok;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // No shell taskbar, or Explorer restarted since: create the taskbar list again next time.
            _taskbar = null;
            return IsWindow(window.Value) ? ChangeResult.Failed : ChangeResult.WindowGone;
        }
    }

    public ChangeResult Activate(WindowHandle window)
    {
        if (!IsWindow(window.Value)) return ChangeResult.WindowGone;
        if (SetForegroundWindow(window.Value)) return ChangeResult.Ok;
        return IsWindow(window.Value) ? ChangeResult.Failed : ChangeResult.WindowGone;
    }

    public void ClipCursor(Rect area) => throw new NotImplementedException();

    public void ReleaseCursor() => throw new NotImplementedException();

    /// <summary>
    /// Take in the window destructions the OS has already reported, so lifetimes are current.
    /// Public for the native tests; <see cref="Describe"/> calls it first.
    /// </summary>
    public void CatchUp()
    {
        // Out-of-context WinEvents are queued to this thread and delivered while it checks its
        // queue, after posted messages: an unfiltered PeekMessage stops at the first posted
        // message (WinForms nearly always has one waiting) before reaching them. Filtering on a
        // message that is never posted makes it pass over the posted ones, deliver the pending
        // WinEvents (OnWinEvent runs, dropping destroyed windows' lifetimes) and remove nothing.
        // No posted message is dispatched, and the controller's events are posted, so it never
        // runs from inside this call. (Like any PeekMessage, it does deliver messages other
        // threads are sending to this thread's windows.)
        PeekMessage(out _, 0, NeverPosted, NeverPosted, PM_NOREMOVE);
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        foreach (var backdrop in _backdrops.ToList()) DestroyBackdrop(new BackdropHandle(backdrop));
        if (_taskbar is not null) Marshal.ReleaseComObject(_taskbar);
        _taskbar = null;
    }

    private static string RegisterBackdropClass()
    {
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(BackdropProc),
            hInstance = GetModuleHandle(null),
            hCursor = LoadCursor(0, IDC_ARROW),
            hbrBackground = GetStockObject(BLACK_BRUSH),
            lpszClassName = BackdropClass,
        };
        if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
            throw new InvalidOperationException($"RegisterClassEx(backdrop) failed: {Marshal.GetLastPInvokeError()}.");
        return BackdropClass;
    }

    private static nint OnBackdropMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;
            case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:
                // Posted, like window events: the controller never runs inside this window procedure.
                if (BackdropOwners.TryGetValue(hwnd, out var owner))
                {
                    var backdrop = new BackdropHandle(hwnd);
                    owner._context.Post(_ => owner.BackdropClicked?.Invoke(backdrop), null);
                }
                return 0;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static bool TrySetRect(nint hwnd, WindowFrame frame, out int error)
    {
        bool ok;
        if (IsIconic(hwnd))
        {
            // A minimised window's rect is its icon's. Its normal placement is where it reopens,
            // so set that (keeping it minimised), then apply the new styles' frame.
            ok = TrySetNormalPlacement(hwnd, frame, out error)
                && SetWindowPos(hwnd, 0, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        }
        else
        {
            var outer = frame.Outer;
            ok = SetWindowPos(hwnd, 0, outer.Left, outer.Top, outer.Width, outer.Height,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
        }
        error = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    private static bool TrySetNormalPlacement(nint hwnd, WindowFrame frame, out int error)
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }
        // rcNormalPosition is in workspace coordinates (relative to the monitor's work area)
        // unless the window is a tool window.
        var normal = new RECT { Left = frame.Outer.Left, Top = frame.Outer.Top, Right = frame.Outer.Right, Bottom = frame.Outer.Bottom };
        if ((frame.Style.ExStyle & WS_EX_TOOLWINDOW) == 0)
        {
            var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromRect(ref normal, MONITOR_DEFAULTTONEAREST), ref monitor))
            {
                int dx = monitor.rcWork.Left - monitor.rcMonitor.Left, dy = monitor.rcWork.Top - monitor.rcMonitor.Top;
                normal = new RECT { Left = normal.Left - dx, Top = normal.Top - dy, Right = normal.Right - dx, Bottom = normal.Bottom - dy };
            }
        }
        placement.rcNormalPosition = normal;
        placement.showCmd = SW_SHOWMINNOACTIVE;
        var ok = SetWindowPlacement(hwnd, ref placement);
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
            case EVENT_OBJECT_HIDE: kind = WindowEventKind.Hidden; break;
            case EVENT_OBJECT_DESTROY: kind = WindowEventKind.Destroyed; break;
            case EVENT_SYSTEM_FOREGROUND: kind = WindowEventKind.Foreground; break;
            case EVENT_OBJECT_LOCATIONCHANGE: kind = WindowEventKind.LocationChanged; break;
            case EVENT_SYSTEM_MINIMIZESTART: kind = WindowEventKind.MinimizeStart; break;
            case EVENT_SYSTEM_MINIMIZEEND: kind = WindowEventKind.MinimizeEnd; break;
            default: return;
        }
        long lifetime;
        if (kind == WindowEventKind.Destroyed)
        {
            // A destroyed window can't be asked for its ancestor; the controller ignores handles it
            // doesn't track. Its lifetime ends now, so a later window with this handle gets a new one.
            _lifetimes.Remove(hwnd, out lifetime);
        }
        else
        {
            if (GetAncestor(hwnd, GA_ROOT) != hwnd) return;
            lifetime = _lifetimes.GetValueOrDefault(hwnd);
        }

        var e = new WindowEvent(kind, new WindowHandle(hwnd), lifetime);
        _context.Post(_ => WindowEvent?.Invoke(e), null);
    }
}
