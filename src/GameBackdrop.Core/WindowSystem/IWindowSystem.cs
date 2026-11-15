using GameBackdrop.Core.Geometry;

namespace GameBackdrop.Core.WindowSystem;

/// <summary>
/// The window system port: everything the backdrop controller needs from the OS. The Win32
/// implementation only calls functions on window handles; nothing is injected into, or read from,
/// another process. Events are raised on the thread that owns the controller, never re-entrantly
/// from inside one of these calls.
/// </summary>
public interface IWindowSystem
{
    /// <summary>Window events for every top-level window on the desktop.</summary>
    event Action<WindowEvent>? WindowEvent;

    /// <summary>
    /// A mouse button went down on a backdrop. Posted like <see cref="WindowEvent"/>, never raised
    /// from inside one of these calls.
    /// </summary>
    event Action<BackdropHandle>? BackdropClicked;

    /// <summary>All top-level windows, visible or not, in Z order.</summary>
    IReadOnlyList<WindowHandle> TopLevelWindows();

    /// <summary>
    /// The window as it is now, or null if it no longer exists. Its <see cref="WindowInfo.Lifetime"/>
    /// is current: the port first takes in the window destructions the OS has already reported,
    /// so even an event handled late (posted before a destruction) never sees a dead window's
    /// lifetime on a handle reused since. Their Destroyed events are still raised later, in
    /// order, never from inside this call.
    /// </summary>
    WindowInfo? Describe(WindowHandle window);

    /// <summary>
    /// The outer size <paramref name="window"/> needs for a client area of <paramref name="clientSize"/>
    /// once it has <paramref name="style"/>, counting the non-client parts the style doesn't
    /// control, such as a menu bar.
    /// </summary>
    Size OuterSizeFor(WindowHandle window, WindowStyle style, Size clientSize);

    /// <summary>
    /// Set a window's styles and outer rect in one go, without activating it or changing its Z
    /// order. For a minimised window the rect is its normal (restored) position and size, and it
    /// stays minimised.
    /// </summary>
    ChangeResult SetFrame(WindowHandle window, WindowFrame frame);

    /// <summary>
    /// A black backdrop window in the app's own process, created hidden. It never activates, and
    /// it is in neither alt-tab nor the taskbar.
    /// </summary>
    BackdropHandle CreateBackdrop();

    /// <summary>
    /// Cover <paramref name="bounds"/> exactly (a monitor's full bounds, taskbar area included)
    /// and show the backdrop without activating it, ordered directly behind
    /// <paramref name="behind"/> in the Z order. It is never topmost, and a window directly
    /// behind a topmost one would be topmost itself: so behind a topmost window that is the
    /// foreground, it goes to the top of the non-topmost windows (right below every topmost
    /// window), and behind a topmost window that isn't, it keeps its place in the Z order (so it
    /// never covers the window the user switched to). If <paramref name="behind"/>
    /// is the foreground window, it is first put in front of the other windows of its band, where
    /// Windows is about to put it anyway; any other window keeps its place.
    /// </summary>
    void ShowBackdrop(BackdropHandle backdrop, Rect bounds, WindowHandle behind);

    /// <summary>Hide the backdrop, keeping it for a later <see cref="ShowBackdrop"/>.</summary>
    void HideBackdrop(BackdropHandle backdrop);

    /// <summary>Destroy the backdrop. Safe on one already destroyed.</summary>
    void DestroyBackdrop(BackdropHandle backdrop);

    /// <summary>
    /// Tell the shell the window is (or no longer is) fullscreen, so the taskbar on its monitor
    /// drops behind it while it is the active window. A shell call on the handle; nothing touches
    /// the window's process. <see cref="ChangeResult.WindowGone"/> if the handle is no window,
    /// <see cref="ChangeResult.Failed"/> if the shell call fails.
    /// </summary>
    ChangeResult MarkFullscreen(WindowHandle window, bool fullscreen);

    /// <summary>
    /// Bring the window to the foreground. <see cref="ChangeResult.WindowGone"/> if the handle is
    /// no window, <see cref="ChangeResult.Failed"/> if Windows refuses.
    /// </summary>
    ChangeResult Activate(WindowHandle window);
}
