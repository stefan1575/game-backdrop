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
}
