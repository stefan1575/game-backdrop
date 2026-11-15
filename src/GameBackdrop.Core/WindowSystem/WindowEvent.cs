namespace GameBackdrop.Core.WindowSystem;

/// <summary>The window events the port reports (from SetWinEventHook on Windows).</summary>
public enum WindowEventKind
{
    Shown,
    Destroyed,
    Foreground,
    LocationChanged,
    MinimizeStart,
    MinimizeEnd,
}

public readonly record struct WindowEvent(WindowEventKind Kind, WindowHandle Window);

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
}
