namespace GameBackdrop.Core.WindowSystem;

/// <summary>
/// A window's style and extended style bits. The constants are Win32's values, but this is plain
/// data: deciding what "borderless" means is a core decision, applying it is the port's job.
/// </summary>
public readonly record struct WindowStyle(uint Style, uint ExStyle)
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_CAPTION = 0x00C00000;      // WS_BORDER | WS_DLGFRAME
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_MAXIMIZE = 0x01000000;
    public const uint WS_EX_DLGMODALFRAME = 0x00000001;
    public const uint WS_EX_WINDOWEDGE = 0x00000100;
    public const uint WS_EX_CLIENTEDGE = 0x00000200;
    public const uint WS_EX_STATICEDGE = 0x00020000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;

    public bool HasCaption => (Style & WS_CAPTION) == WS_CAPTION;
    public bool IsToolWindow => (ExStyle & WS_EX_TOOLWINDOW) != 0;

    /// <summary>
    /// The same window without title bar or border. WS_MINIMIZEBOX is kept so the taskbar can
    /// still minimise the game.
    /// </summary>
    public WindowStyle Borderless() => new(
        Style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX | WS_MAXIMIZE),
        ExStyle & ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE));

    /// <summary>
    /// Only the bits that shape the frame (popup, title bar, resize border, edges), without the
    /// ones that change with the window's state (visible, minimised, disabled by a dialog). A
    /// new top-level window always has WS_POPUP or a title bar, so this tells a borderless window
    /// the app made apart from a new one.
    /// </summary>
    public WindowStyle FrameBits => new(
        Style & (WS_POPUP | WS_CAPTION | WS_THICKFRAME),
        ExStyle & (WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE));

    public override string ToString() => $"style=0x{Style:X8} ex=0x{ExStyle:X8}";
}
