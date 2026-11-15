using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// A scripted desktop: monitors, windows with styles and rects, and the events they raise. Tests
/// act on it the way a user or a game would, then look at the windows' final state.
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

    public event Action<WindowEvent>? WindowEvent;

    /// <summary>Every SetFrame call, in order, including ones that failed.</summary>
    public List<(WindowHandle Window, WindowFrame Frame)> FrameChanges { get; } = [];

    /// <summary>Start a process: windows opened with it share its id and exe path.</summary>
    public FakeProcess StartProcess(string exePath, bool elevated = false) => new(this, _nextProcessId++, exePath, elevated);

    /// <summary>Open a captioned window whose client area is <paramref name="clientSize"/>, at <paramref name="left"/>,<paramref name="top"/>.</summary>
    public FakeWindow OpenWindowed(FakeProcess process, Size clientSize, int left = 100, int top = 100, bool show = true) =>
        Open(process, Captioned, Rect.FromPosition(left, top, new Size(clientSize.Width + FakeWindow.FrameLeft + FakeWindow.FrameRight, clientSize.Height + FakeWindow.FrameTop + FakeWindow.FrameBottom)), show);

    /// <summary>Open a frameless popup covering <paramref name="outer"/>.</summary>
    public FakeWindow OpenPopup(FakeProcess process, Rect outer, bool show = true) => Open(process, Popup, outer, show);

    public FakeWindow Open(FakeProcess process, WindowStyle style, Rect outer, bool show = true, WindowHandle? owner = null, bool menu = false)
    {
        var window = new FakeWindow(new WindowHandle(_nextHandle++), process, style, outer) { Owner = owner, HasMenu = menu };
        _windows.Add(window);
        if (show) Show(window);
        return window;
    }

    public void Show(FakeWindow window)
    {
        window.IsVisible = true;
        Raise(WindowEventKind.Shown, window);
    }

    /// <summary>The window closes (or its process exits).</summary>
    public void Close(FakeWindow window)
    {
        _windows.Remove(window);
        window.IsGone = true;
        Raise(WindowEventKind.Destroyed, window);
    }

    public void Raise(WindowEventKind kind, FakeWindow window) => WindowEvent?.Invoke(new WindowEvent(kind, window.Handle));

    /// <summary>
    /// The window is destroyed but its Destroyed event hasn't been delivered yet (the real one is
    /// queued). Raise it later with <see cref="Raise"/>, which then refers to a stale handle.
    /// </summary>
    public void CloseWithEventPending(FakeWindow window)
    {
        _windows.Remove(window);
        window.IsGone = true;
    }

    /// <summary>
    /// A new window that gets the handle of <paramref name="gone"/>, a window already destroyed
    /// (HWNDs are recycled). Like <see cref="Open"/>, it is shown unless <paramref name="show"/> is false.
    /// </summary>
    public FakeWindow OpenWithReusedHandle(FakeWindow gone, FakeProcess process, WindowStyle style, Rect outer, bool show = true)
    {
        if (!gone.IsGone) throw new InvalidOperationException("Only a destroyed window's handle can be reused.");
        var window = new FakeWindow(gone.Handle, process, style, outer);
        _windows.Add(window);
        if (show) Show(window);
        return window;
    }

    public IReadOnlyList<WindowHandle> TopLevelWindows() => _windows.Select(w => w.Handle).ToList();

    public WindowInfo? Describe(WindowHandle handle)
    {
        var w = Find(handle);
        if (w is null) return null;
        return new WindowInfo(w.Handle, w.Process.Id, w.Process.ExePath, w.Outer, w.Client, w.Style,
            w.IsVisible, w.IsMinimized, w.Owner is not null, MonitorOf(w.Outer));
    }

    public Size OuterSizeFor(WindowHandle handle, WindowStyle style, Size clientSize)
    {
        var (left, top, right, bottom) = FakeWindow.Insets(style, Find(handle)?.HasMenu ?? false);
        return new Size(clientSize.Width + left + right, clientSize.Height + top + bottom);
    }

    public ChangeResult SetFrame(WindowHandle handle, WindowFrame frame)
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
        return ChangeResult.Ok;
    }

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

public sealed record FakeProcess(FakeWindowSystem System, int Id, string ExePath, bool Elevated);

public sealed class FakeWindow(WindowHandle handle, FakeProcess process, WindowStyle style, Rect outer)
{
    // The non-client frame a captioned window has: invisible resize borders plus the title bar.
    public const int FrameLeft = 8, FrameTop = 31, FrameRight = 8, FrameBottom = 8;

    public WindowHandle Handle { get; } = handle;
    public FakeProcess Process { get; } = process;
    public WindowStyle Style { get; set; } = style;
    public Rect Outer { get; set; } = outer;
    public bool IsVisible { get; set; }
    public bool IsMinimized { get; set; }
    public bool IsGone { get; set; }
    public WindowHandle? Owner { get; init; }
    public Queue<ChangeResult> ScriptedResults { get; } = new();

    public bool HasBorder => (Style.Style & (WindowStyle.WS_CAPTION | WindowStyle.WS_THICKFRAME)) != 0;

    // A menu bar sits between the title bar and the client area, whatever the style.
    public const int MenuHeight = 20;

    /// <summary>The window has a menu bar (a Win32 menu, not a style bit).</summary>
    public bool HasMenu { get; init; }

    public Rect Client
    {
        get
        {
            var (left, top, right, bottom) = Insets(Style, HasMenu);
            return new Rect(Outer.Left + left, Outer.Top + top, Outer.Right - right, Outer.Bottom - bottom);
        }
    }

    /// <summary>The non-client frame on each side of a window with this style.</summary>
    public static (int Left, int Top, int Right, int Bottom) Insets(WindowStyle style, bool hasMenu)
    {
        var menu = hasMenu ? MenuHeight : 0;
        if (style.HasCaption) return (FrameLeft, FrameTop + menu, FrameRight, FrameBottom);
        if ((style.Style & WindowStyle.WS_THICKFRAME) != 0) return (FrameLeft, FrameLeft + menu, FrameRight, FrameBottom);
        return (0, menu, 0, 0);
    }
}
