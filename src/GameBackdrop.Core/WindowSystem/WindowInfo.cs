using GameBackdrop.Core.Geometry;

namespace GameBackdrop.Core.WindowSystem;

/// <summary>A monitor: its full bounds (taskbar included) and its work area.</summary>
public sealed record MonitorInfo(MonitorHandle Handle, Rect Bounds, Rect WorkArea);

/// <summary>What the frame of a window is: its styles and its outer rect. Saved before a change, put back on restore.</summary>
public sealed record WindowFrame(WindowStyle Style, Rect Outer);

/// <summary>A snapshot of one top-level window, as the port sees it right now.</summary>
/// <param name="ProcessPath">Full path of the owning process's exe, or null when it can't be read.</param>
/// <param name="Outer">The window rect (GetWindowRect).</param>
/// <param name="Client">The client area in screen coordinates.</param>
/// <param name="IsOwned">True when the window has an owner (dialogs, tool palettes).</param>
/// <param name="Monitor">The monitor the window is on (nearest one).</param>
public sealed record WindowInfo(
    WindowHandle Handle,
    int ProcessId,
    string? ProcessPath,
    Rect Outer,
    Rect Client,
    WindowStyle Style,
    bool IsVisible,
    bool IsMinimized,
    bool IsOwned,
    MonitorInfo Monitor)
{
    public WindowFrame Frame => new(Style, Outer);
}
