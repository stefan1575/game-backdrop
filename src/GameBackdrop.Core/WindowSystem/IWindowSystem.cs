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

    /// <summary>The window as it is now, or null if it no longer exists.</summary>
    WindowInfo? Describe(WindowHandle window);

    /// <summary>Set a window's styles and outer rect in one go, without activating it or changing its Z order.</summary>
    ChangeResult SetFrame(WindowHandle window, WindowFrame frame);
}
