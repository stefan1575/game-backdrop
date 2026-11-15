namespace GameBackdrop.Core.WindowSystem;

/// <summary>An opaque top-level window handle (an HWND on Windows).</summary>
public readonly record struct WindowHandle(nint Value)
{
    public override string ToString() => $"0x{Value:X}";
}

/// <summary>An opaque monitor handle (an HMONITOR on Windows).</summary>
public readonly record struct MonitorHandle(nint Value)
{
    public override string ToString() => $"0x{Value:X}";
}

/// <summary>
/// An opaque handle to a backdrop: a black window the port made in the app's own process (an
/// HWND on Windows).
/// </summary>
public readonly record struct BackdropHandle(nint Value)
{
    public override string ToString() => $"0x{Value:X}";
}
