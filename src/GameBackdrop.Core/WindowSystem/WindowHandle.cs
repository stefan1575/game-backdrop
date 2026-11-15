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
