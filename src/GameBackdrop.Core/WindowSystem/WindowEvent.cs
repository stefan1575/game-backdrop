namespace GameBackdrop.Core.WindowSystem;

/// <summary>The window events the port reports (from SetWinEventHook on Windows).</summary>
public enum WindowEventKind
{
    Shown,
    /// <summary>The window was hidden (not minimised: that is <see cref="MinimizeStart"/>).</summary>
    Hidden,
    Destroyed,
    Foreground,
    LocationChanged,
    MinimizeStart,
    MinimizeEnd,
    /// <summary>
    /// The window was cloaked: still visible to Windows, but not drawn. Switching to another
    /// virtual desktop cloaks the windows of the one left.
    /// </summary>
    Cloaked,
    /// <summary>The window was uncloaked (switching back to its virtual desktop).</summary>
    Uncloaked,
}

/// <param name="Lifetime">Which lifetime of <paramref name="Window"/> the event is about (see
/// <see cref="WindowInfo.Lifetime"/>). For Destroyed, the lifetime that just ended; 0 when the
/// port never described that window.</param>
public readonly record struct WindowEvent(WindowEventKind Kind, WindowHandle Window, long Lifetime);

/// <summary>The outcome of asking the port to change a window.</summary>
public enum ChangeResult
{
    Ok,
    /// <summary>The window belongs to a process we may not touch (an elevated game).</summary>
    AccessDenied,
    /// <summary>The window no longer exists.</summary>
    WindowGone,
    /// <summary>The change failed for another reason; the window was left as it was.</summary>
    Failed,
    /// <summary>The change failed part-way and couldn't be undone: the window may be half-changed.</summary>
    PartlyChanged,
}
