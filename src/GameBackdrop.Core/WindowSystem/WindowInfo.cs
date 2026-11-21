using GameBackdrop.Core.Geometry;

namespace GameBackdrop.Core.WindowSystem;

/// <summary>A monitor: its full bounds (taskbar included) and its work area.</summary>
public sealed record MonitorInfo(MonitorHandle Handle, Rect Bounds, Rect WorkArea);

/// <summary>What the frame of a window is: its styles and its outer rect. Saved before a change, put back on restore.</summary>
public sealed record WindowFrame(WindowStyle Style, Rect Outer);

/// <summary>A snapshot of one top-level window, as the port sees it right now.</summary>
/// <param name="Lifetime">Which window is behind the handle. A handle can be reused by a new
/// window once its window is destroyed, so the port numbers each window the first time it
/// describes it and drops the number when that window's destroy event reaches it: a later window
/// with the same handle gets a new number. It doesn't change when the window changes itself.</param>
/// <param name="ProcessPath">Full path of the owning process's exe, or null when it can't be read.</param>
/// <param name="ProcessStarted">When the owning process started (a FILETIME), or 0 when it can't be
/// read. With <paramref name="ProcessId"/> it tells one run of a process from a later one that got
/// the same id: Windows reuses process ids.</param>
/// <param name="Outer">The window rect (GetWindowRect).</param>
/// <param name="Client">The client area in screen coordinates.</param>
/// <param name="IsOwned">True when the window has an owner (dialogs, tool palettes).</param>
/// <param name="Monitor">The monitor the window is on (nearest one).</param>
/// <param name="IsCloaked">True while the window is cloaked: visible, but not drawn (it is on
/// another virtual desktop than the one shown).</param>
public sealed record WindowInfo(
    WindowHandle Handle,
    long Lifetime,
    int ProcessId,
    string? ProcessPath,
    long ProcessStarted,
    Rect Outer,
    Rect Client,
    WindowStyle Style,
    bool IsVisible,
    bool IsMinimized,
    bool IsOwned,
    MonitorInfo Monitor,
    bool IsCloaked = false)
{
    public WindowFrame Frame => new(Style, Outer);
}
