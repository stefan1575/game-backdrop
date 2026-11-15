using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>The port calls a test can script something into (<see cref="FakeWindowSystem.DuringNext"/>, <see cref="FakeWindowSystem.ThrowOnNext"/>).</summary>
public enum PortCall { SetFrame, CreateBackdrop, ShowBackdrop, MarkFullscreen }

/// <summary>
/// A scripted desktop: monitors, windows with styles and rects, one Z order (windows and
/// backdrops), a foreground window, and the events they raise. Tests act on it the way a user or a
/// game would, then look at the desktop's final state. As on Windows, the port's own calls never
/// raise an event from inside the call: the events they cause are queued and delivered after the
/// event or click being handled returns.
/// </summary>
public sealed class FakeWindowSystem : IWindowSystem
{
    public static readonly MonitorInfo Primary =
        new(new MonitorHandle(1), new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1032));

    public static readonly MonitorInfo Secondary =
        new(new MonitorHandle(2), new Rect(1920, -200, 1920 + 2560, -200 + 1440), new Rect(1920, -200, 1920 + 2560, -200 + 1392));

    /// <summary>A normal captioned, resizable top-level window.</summary>
    public static readonly WindowStyle Captioned = new(0x14CF0000, 0x00000100); // WS_OVERLAPPEDWINDOW | WS_VISIBLE | WS_CLIPSIBLINGS, WS_EX_WINDOWEDGE

    /// <summary>A popup with no frame at all (borderless fullscreen games, splash screens).</summary>
    public static readonly WindowStyle Popup = new(0x96000000, 0); // WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN

    private readonly List<MonitorInfo> _monitors = [Primary, Secondary];
    private readonly List<FakeWindow> _windows = [];
    private nint _nextHandle = 0x1000;
    private int _nextProcessId = 100;
    private long _nextLifetime = 1;

    // Like the Win32 port: the lifetime it gave each handle when it first described it, dropped
    // when that window's destruction reaches the port. Until then, a reused handle still reports
    // the old lifetime.
    private readonly Dictionary<WindowHandle, long> _lifetimes = [];
    private readonly List<FakeWindow> _destroyedNotYetSeen = [];

    public event Action<WindowEvent>? WindowEvent;

    public event Action<BackdropHandle>? BackdropClicked;

    /// <summary>Every backdrop the port has made, destroyed ones included.</summary>
    public Dictionary<BackdropHandle, FakeBackdrop> Backdrops { get; } = [];

    /// <summary>The foreground window, or null when no window has it.</summary>
    public FakeWindow? Foreground { get; private set; }

    // Front first: FakeWindows and FakeBackdrops. Hidden ones keep their place.
    private readonly List<object> _zOrder = [];

    // Events the port's own calls caused, delivered after the current event or click returns.
    private readonly Queue<(WindowEventKind Kind, FakeWindow Window)> _posted = new();
    private bool _delivering;

    private nint _nextBackdrop = 0xB000;

    /// <summary>Every SetFrame call, in order, including ones that failed.</summary>
    public List<(WindowHandle Window, WindowFrame Frame)> FrameChanges { get; } = [];

    /// <summary>Start a process: windows opened with it share its id and exe path.</summary>
    public FakeProcess StartProcess(string exePath, bool elevated = false) => new(this, _nextProcessId++, exePath, elevated);

    /// <summary>Open a captioned window whose client area is <paramref name="clientSize"/>, at <paramref name="left"/>,<paramref name="top"/>.</summary>
    public FakeWindow OpenWindowed(FakeProcess process, Size clientSize, int left = 100, int top = 100, bool show = true) =>
        Open(process, Captioned, Rect.FromPosition(left, top, new Size(clientSize.Width + FakeWindow.FrameLeft + FakeWindow.FrameRight, clientSize.Height + FakeWindow.FrameTop + FakeWindow.FrameBottom)), show);

    /// <summary>Open a frameless popup covering <paramref name="outer"/>.</summary>
    public FakeWindow OpenPopup(FakeProcess process, Rect outer, bool show = true) => Open(process, Popup, outer, show);

    public FakeWindow Open(FakeProcess process, WindowStyle style, Rect outer, bool show = true, WindowHandle? owner = null, bool menu = false, int menuRows = 1)
    {
        var window = new FakeWindow(new WindowHandle(_nextHandle++), process, style, outer, _nextLifetime++) { Owner = owner, HasMenu = menu, MenuRows = menuRows };
        _windows.Add(window);
        _zOrder.Insert(0, window);
        if (show) Show(window);
        return window;
    }

    /// <summary>The window is shown: it comes to the front and gets the foreground (Shown, then Foreground).</summary>
    public void Show(FakeWindow window)
    {
        window.IsVisible = true;
        ToFront(window);
        Foreground = window;
        Raise(WindowEventKind.Shown, window);
        Raise(WindowEventKind.Foreground, window);
    }

    /// <summary>
    /// The window hides itself (Hidden); the foreground passes on if it had it. As Explorer does,
    /// the shell drops the hidden window's taskbar tab and forgets its fullscreen mark.
    /// </summary>
    public void Hide(FakeWindow window)
    {
        HideKeepingForeground(window);
        if (Foreground == window) PassForeground();
    }

    /// <summary>
    /// The window hides itself (Hidden) but stays the foreground window, as a window hidden with
    /// SW_HIDE by its own thread was measured to on Windows 11. The shell still forgets its mark.
    /// </summary>
    public void HideKeepingForeground(FakeWindow window)
    {
        window.IsVisible = false;
        window.IsMarkedFullscreen = false;
        Raise(WindowEventKind.Hidden, window);
    }

    /// <summary>
    /// A hidden window that kept the foreground is shown again (SW_SHOW): only Shown, since the
    /// foreground doesn't change.
    /// </summary>
    public void ShowStillForeground(FakeWindow window)
    {
        if (Foreground != window) throw new InvalidOperationException("Only a window that kept the foreground.");
        window.IsVisible = true;
        Raise(WindowEventKind.Shown, window);
    }

    /// <summary>The user brings the window to the foreground (alt-tab, a click, its taskbar button).</summary>
    public void UserActivates(FakeWindow window)
    {
        ToFront(window);
        Foreground = window;
        Raise(WindowEventKind.Foreground, window);
    }

    /// <summary>The user minimises the window (MinimizeStart); the foreground passes to the next window.</summary>
    public void Minimise(FakeWindow window)
    {
        window.IsMinimized = true;
        Raise(WindowEventKind.MinimizeStart, window);
        if (Foreground == window) PassForeground();
    }

    /// <summary>
    /// The minimised window is restored (MinimizeEnd). From the taskbar it then comes to the front
    /// with the foreground; restored without activation (SW_SHOWNOACTIVATE) it keeps its place.
    /// </summary>
    public void Restore(FakeWindow window, bool activate = true)
    {
        window.IsMinimized = false;
        Raise(WindowEventKind.MinimizeEnd, window);
        if (activate) UserActivates(window);
    }

    /// <summary>The user (or the game) moves the window (LocationChanged).</summary>
    public void Move(FakeWindow window, Rect outer)
    {
        window.Outer = outer;
        Raise(WindowEventKind.LocationChanged, window);
    }

    /// <summary>The window closes (or its process exits).</summary>
    public void Close(FakeWindow window)
    {
        CloseWithEventPending(window);
        Raise(WindowEventKind.Destroyed, window);
    }

    private void PassForeground()
    {
        var leaving = Foreground;
        Foreground = _zOrder.OfType<FakeWindow>().FirstOrDefault(w => w.IsVisible && !w.IsMinimized && w != leaving);
        if (Foreground is not null) Raise(WindowEventKind.Foreground, Foreground);
    }

    private void ToFront(object item)
    {
        _zOrder.Remove(item);
        _zOrder.Insert(0, item);
    }

    /// <summary>
    /// Deliver an event. As on Win32, the port takes in every destruction reported before it
    /// first, so a reused handle's events never carry the old window's lifetime.
    /// </summary>
    public void Raise(WindowEventKind kind, FakeWindow window)
    {
        TakeInDestructions();
        RaiseStale(kind, window);
    }

    /// <summary>
    /// Deliver an event that was posted to the controller before the pending destructions reached
    /// the port: on Win32 the controller's events are posted, so one already queued can run before
    /// a later destruction is taken in.
    /// </summary>
    public void RaiseStale(WindowEventKind kind, FakeWindow window)
    {
        var lifetime = kind == WindowEventKind.Destroyed ? window.Lifetime : _lifetimes.GetValueOrDefault(window.Handle);
        Deliver(() => WindowEvent?.Invoke(new WindowEvent(kind, window.Handle, lifetime)));
    }

    /// <summary>Run one delivery, then deliver whatever the port's calls queued meanwhile, in order.</summary>
    private void Deliver(Action delivery)
    {
        if (_delivering) throw new InvalidOperationException("An event was raised from inside a handler; the port never does that.");
        _delivering = true;
        try { delivery(); }
        finally { _delivering = false; }
        if (!_posted.TryDequeue(out var next)) return;
        // A controller that answers its own events with more calls would never settle.
        if (++_chain > 100) throw new InvalidOperationException("The port's own calls keep causing events: the controller never settles.");
        try { Raise(next.Kind, next.Window); }
        finally { _chain--; }
    }

    private int _chain;

    /// <summary>
    /// The window is destroyed, but neither the port nor the controller has heard yet: the OS
    /// event is still queued. The port takes it in at its next Describe or event; an event posted
    /// earlier (<see cref="RaiseStale"/>) can still reach the controller before that. Raise the Destroyed event later with
    /// <see cref="Raise"/>, which then refers to a stale handle.
    /// </summary>
    public void CloseWithEventPending(FakeWindow window)
    {
        _windows.Remove(window);
        _zOrder.Remove(window);
        window.IsGone = true;
        window.IsMarkedFullscreen = false; // the shell forgets a destroyed window
        if (Foreground == window) Foreground = null;
        _destroyedNotYetSeen.Add(window);
    }

    private void TakeInDestructions()
    {
        foreach (var gone in _destroyedNotYetSeen)
            if (_lifetimes.TryGetValue(gone.Handle, out var known) && known == gone.Lifetime) _lifetimes.Remove(gone.Handle);
        _destroyedNotYetSeen.Clear();
    }

    /// <summary>
    /// A new window that gets the handle of <paramref name="gone"/>, a window already destroyed
    /// (HWNDs are recycled). Like <see cref="Open"/>, it is shown unless <paramref name="show"/> is false.
    /// </summary>
    public FakeWindow OpenWithReusedHandle(FakeWindow gone, FakeProcess process, WindowStyle style, Rect outer, bool show = true)
    {
        if (!gone.IsGone) throw new InvalidOperationException("Only a destroyed window's handle can be reused.");
        var window = new FakeWindow(gone.Handle, process, style, outer, _nextLifetime++);
        _windows.Add(window);
        _zOrder.Insert(0, window);
        if (show) Show(window);
        return window;
    }

    public IReadOnlyList<WindowHandle> TopLevelWindows() => _windows.Select(w => w.Handle).ToList();

    public WindowInfo? Describe(WindowHandle handle)
    {
        TakeInDestructions(); // as the port does: Describe's lifetime is current
        var w = Find(handle);
        if (w is null) return null;
        if (!_lifetimes.TryGetValue(handle, out var lifetime)) _lifetimes[handle] = lifetime = w.Lifetime;
        return new WindowInfo(w.Handle, lifetime, w.Process.Id, w.Process.ExePath, w.Outer, w.Client, w.Style,
            w.IsVisible, w.IsMinimized, w.Owner is not null, MonitorOf(w.Outer));
    }

    public Size OuterSizeFor(WindowHandle handle, WindowStyle style, Size clientSize)
    {
        // Like AdjustWindowRectEx, this assumes a menu bar of one row.
        var hasMenu = Find(handle)?.HasMenu ?? false;
        var (left, top, right, bottom) = FakeWindow.Insets(style, hasMenu ? 1 : 0);
        return new Size(clientSize.Width + left + right, clientSize.Height + top + bottom);
    }

    public ChangeResult SetFrame(WindowHandle handle, WindowFrame frame)
    {
        Begin(PortCall.SetFrame);
        var result = ApplyFrame(handle, frame);
        End(PortCall.SetFrame);
        return result;
    }

    private ChangeResult ApplyFrame(WindowHandle handle, WindowFrame frame)
    {
        FrameChanges.Add((handle, frame));
        var w = Find(handle);
        if (w is null) return ChangeResult.WindowGone;
        if (w.Process.Elevated) return ChangeResult.AccessDenied;
        if (w.ScriptedResults.TryDequeue(out var scripted))
        {
            // A failure leaves the window as it was; a partial change applies the styles only.
            if (scripted == ChangeResult.PartlyChanged) w.Style = frame.Style;
            return scripted;
        }
        w.Style = frame.Style;
        w.Outer = frame.Outer;
        _posted.Enqueue((WindowEventKind.LocationChanged, w));
        return ChangeResult.Ok;
    }

    public BackdropHandle CreateBackdrop()
    {
        Begin(PortCall.CreateBackdrop);
        var handle = new BackdropHandle(_nextBackdrop++);
        var backdrop = new FakeBackdrop(handle);
        Backdrops[handle] = backdrop;
        _zOrder.Insert(0, backdrop);
        End(PortCall.CreateBackdrop);
        return handle;
    }

    public void ShowBackdrop(BackdropHandle backdrop, Rect bounds, WindowHandle behind)
    {
        Begin(PortCall.ShowBackdrop);
        Order(backdrop, bounds, behind);
        End(PortCall.ShowBackdrop);
    }

    private void Order(BackdropHandle backdrop, Rect bounds, WindowHandle behind)
    {
        if (!Backdrops.TryGetValue(backdrop, out var b) || b.IsDestroyed) return;
        var window = Find(behind);
        if (window is null) return; // SetWindowPos fails on a stale insert-after handle
        if (window == Foreground) ToFront(window); // as the port does: where Windows is about to put it
        b.Bounds = bounds;
        b.IsVisible = true;
        window.BackdropsShownBehind++;
        _zOrder.Remove(b);
        _zOrder.Insert(_zOrder.IndexOf(window) + 1, b);
    }

    public void HideBackdrop(BackdropHandle backdrop)
    {
        if (Backdrops.TryGetValue(backdrop, out var b)) b.IsVisible = false;
    }

    public void DestroyBackdrop(BackdropHandle backdrop)
    {
        if (!Backdrops.TryGetValue(backdrop, out var b)) return;
        b.IsVisible = false;
        b.IsDestroyed = true;
        _zOrder.Remove(b);
    }

    public ChangeResult MarkFullscreen(WindowHandle window, bool fullscreen)
    {
        Begin(PortCall.MarkFullscreen);
        var result = Mark(window, fullscreen);
        End(PortCall.MarkFullscreen);
        return result;
    }

    private ChangeResult Mark(WindowHandle window, bool fullscreen)
    {
        var w = Find(window);
        if (w is null) return ChangeResult.WindowGone;
        if (w.FailedMarks > 0)
        {
            w.FailedMarks--;
            return ChangeResult.Failed;
        }
        w.IsMarkedFullscreen = fullscreen;
        return ChangeResult.Ok;
    }

    public ChangeResult Activate(WindowHandle window)
    {
        var w = Find(window);
        if (w is null) return ChangeResult.WindowGone;
        ToFront(w);
        Foreground = w;
        _posted.Enqueue((WindowEventKind.Foreground, w));
        return ChangeResult.Ok;
    }

    // What tests scripted into the next call of each kind.
    private readonly HashSet<PortCall> _throwNext = [];
    private readonly Dictionary<PortCall, Action> _duringNext = [];

    /// <summary>
    /// Run <paramref name="action"/> inside the next <paramref name="call"/>, after the call has
    /// done its work and before it returns: the desktop changes while the port is busy (a game
    /// closes and its handle goes to another window, <see cref="ReplaceUnderItsHandle"/>).
    /// Whatever it does raises no event from inside the call.
    /// </summary>
    public void DuringNext(PortCall call, Action action) => _duringNext[call] = action;

    /// <summary>The next <paramref name="call"/> throws before doing anything, as the port's CreateWindowEx failure does.</summary>
    public void ThrowOnNext(PortCall call) => _throwNext.Add(call);

    private void Begin(PortCall call)
    {
        if (_throwNext.Remove(call)) throw new InvalidOperationException($"{call} failed (scripted).");
    }

    private void End(PortCall call)
    {
        if (_duringNext.Remove(call, out var action)) action();
    }

    /// <summary>
    /// <paramref name="gone"/> is destroyed and a new, visible window gets its handle at once,
    /// with both windows' events still pending (raise them later, if at all). It is marked
    /// fullscreen of its own accord if <paramref name="markedFullscreen"/>.
    /// </summary>
    public FakeWindow ReplaceUnderItsHandle(FakeWindow gone, FakeProcess process, WindowStyle style, Rect outer, bool markedFullscreen = false)
    {
        CloseWithEventPending(gone);
        var replacement = OpenWithReusedHandle(gone, process, style, outer, show: false);
        replacement.IsVisible = true;
        replacement.IsMarkedFullscreen = markedFullscreen;
        return replacement;
    }

    /// <summary>The user clicks the black of a backdrop.</summary>
    public void ClickBackdrop(BackdropHandle backdrop) => Deliver(() => BackdropClicked?.Invoke(backdrop));

    /// <summary>The next <paramref name="count"/> MarkFullscreen calls on the window fail (the shell call errs).</summary>
    public void FailNextMarks(FakeWindow window, int count = 1) => window.FailedMarks += count;

    /// <summary>
    /// Whether the taskbar on <paramref name="monitor"/> is hidden behind a window: as Explorer
    /// does it, when the foreground window is marked fullscreen and is on that monitor.
    /// </summary>
    public bool TaskbarCovered(MonitorInfo monitor) =>
        Foreground is { IsMarkedFullscreen: true } w && MonitorOf(w.Outer) == monitor;

    /// <summary>The visible backdrops that cover any part of <paramref name="monitor"/>.</summary>
    public IReadOnlyList<FakeBackdrop> VisibleBackdropsOn(MonitorInfo monitor) =>
        Backdrops.Values.Where(b => b.IsVisible && Overlaps(b.Bounds, monitor.Bounds)).ToList();

    /// <summary>The window or backdrop directly behind <paramref name="item"/> in the Z order, or null.</summary>
    public object? DirectlyBehind(object item)
    {
        var index = _zOrder.IndexOf(item);
        return index >= 0 && index + 1 < _zOrder.Count ? _zOrder[index + 1] : null;
    }

    /// <summary>The given windows and backdrops, front first.</summary>
    public IReadOnlyList<object> ZOrder(params object[] items) =>
        _zOrder.Where(items.Contains).ToList();

    private static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>The next SetFrame calls on <paramref name="window"/> return these results instead of succeeding.</summary>
    public void FailNextChanges(FakeWindow window, params ChangeResult[] results)
    {
        foreach (var result in results) window.ScriptedResults.Enqueue(result);
    }

    private FakeWindow? Find(WindowHandle handle) => _windows.FirstOrDefault(w => w.Handle == handle);

    /// <summary>The monitor holding the window's centre, else the first one (like MONITOR_DEFAULTTONEAREST, roughly).</summary>
    private MonitorInfo MonitorOf(Rect outer)
    {
        int x = outer.Left + outer.Width / 2, y = outer.Top + outer.Height / 2;
        return _monitors.FirstOrDefault(m => x >= m.Bounds.Left && x < m.Bounds.Right && y >= m.Bounds.Top && y < m.Bounds.Bottom)
            ?? _monitors[0];
    }
}

/// <summary>A backdrop as the fake port last left it.</summary>
public sealed class FakeBackdrop(BackdropHandle handle)
{
    public BackdropHandle Handle { get; } = handle;
    public Rect Bounds { get; set; }
    public bool IsVisible { get; set; }
    public bool IsDestroyed { get; set; }
}

public sealed record FakeProcess(FakeWindowSystem System, int Id, string ExePath, bool Elevated);

public sealed class FakeWindow(WindowHandle handle, FakeProcess process, WindowStyle style, Rect outer, long lifetime)
{
    // The non-client frame a captioned window has: invisible resize borders plus the title bar.
    public const int FrameLeft = 8, FrameTop = 31, FrameRight = 8, FrameBottom = 8;

    public WindowHandle Handle { get; } = handle;

    /// <summary>This window's own lifetime number: what the port reports for it once it has caught up.</summary>
    public long Lifetime { get; } = lifetime;

    public FakeProcess Process { get; } = process;
    public WindowStyle Style { get; set; } = style;
    public Rect Outer { get; set; } = outer;
    public bool IsVisible { get; set; }
    public bool IsMinimized { get; set; }
    public bool IsGone { get; set; }

    /// <summary>The shell has been told this window is fullscreen (MarkFullscreen).</summary>
    public bool IsMarkedFullscreen { get; set; }

    /// <summary>How many times a backdrop has been shown behind this window, even one destroyed since.</summary>
    public int BackdropsShownBehind { get; set; }

    /// <summary>How many more MarkFullscreen calls on this window fail.</summary>
    public int FailedMarks { get; set; }
    public WindowHandle? Owner { get; init; }
    public Queue<ChangeResult> ScriptedResults { get; } = new();

    public bool HasBorder => (Style.Style & (WindowStyle.WS_CAPTION | WindowStyle.WS_THICKFRAME)) != 0;

    // A menu bar sits between the title bar and the client area, whatever the style.
    public const int MenuHeight = 20;

    /// <summary>The window has a menu bar (a Win32 menu, not a style bit).</summary>
    public bool HasMenu { get; init; }

    /// <summary>How many rows the menu bar wraps onto (a long menu in a narrow window).</summary>
    public int MenuRows { get; init; } = 1;

    public Rect Client
    {
        get
        {
            var (left, top, right, bottom) = Insets(Style, HasMenu ? MenuRows : 0);
            return new Rect(Outer.Left + left, Outer.Top + top, Outer.Right - right, Outer.Bottom - bottom);
        }
    }

    /// <summary>The non-client frame on each side of a window with this style.</summary>
    public static (int Left, int Top, int Right, int Bottom) Insets(WindowStyle style, int menuRows)
    {
        var menu = menuRows * MenuHeight;
        if (style.HasCaption) return (FrameLeft, FrameTop + menu, FrameRight, FrameBottom);
        if ((style.Style & WindowStyle.WS_THICKFRAME) != 0) return (FrameLeft, FrameLeft + menu, FrameRight, FrameBottom);
        return (0, menu, 0, 0);
    }
}
