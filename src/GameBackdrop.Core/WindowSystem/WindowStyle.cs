namespace GameBackdrop.Core.WindowSystem;

/// <summary>
/// A window's style and extended style bits. The constants are Win32's values, but this is plain
/// data: deciding what "borderless" means is a core decision, applying it is the port's job.
/// </summary>
public readonly record struct WindowStyle(uint Style, uint ExStyle)
{
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
    public const uint WS_POPUP = 0x80000000;

    // The bits that make a window's frame. Borderless takes these off, and WS_MAXIMIZE too; but
    // whether the frame is the one a window had goes by these alone, because WS_MAXIMIZE is a state
    // the window is in, not a frame a game sets.
    private const uint FrameBits = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MAXIMIZEBOX;
    private const uint FrameExBits = WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE;

    // What Borderless takes off: every bit the app can owe a window.
    private const uint StrippedBits = FrameBits | WS_MAXIMIZE;
    private const uint StrippedExBits = FrameExBits;

    public bool HasCaption => (Style & WS_CAPTION) == WS_CAPTION;
    public bool IsToolWindow => (ExStyle & WS_EX_TOOLWINDOW) != 0;

    /// <summary>No bits set.</summary>
    public bool IsEmpty => Style == 0 && ExStyle == 0;

    /// <summary>
    /// The same window without title bar or border. WS_MINIMIZEBOX is kept so the taskbar can
    /// still minimise the game.
    /// </summary>
    public WindowStyle Borderless() => new(
        Style & ~StrippedBits,
        ExStyle & ~StrippedExBits);

    /// <summary>The bits <see cref="Borderless"/> takes off this: what the app owes once it strips it.</summary>
    public WindowStyle TakenOffByBorderless() => new(
        Style & StrippedBits,
        ExStyle & StrippedExBits);

    /// <summary>These styles with <paramref name="bits"/> set as well.</summary>
    public WindowStyle With(WindowStyle bits) => new(Style | bits.Style, ExStyle | bits.ExStyle);

    /// <summary>
    /// Whether the two have the same frame bits and are both popups or both not. A window whose
    /// frame changed was given a new style as a whole (a game switching mode sets WS_POPUP, or its
    /// title bar); one whose other bits changed (WS_EX_TOPMOST, WS_DISABLED under a modal dialog,
    /// WS_MAXIMIZE) still has the frame it had.
    /// </summary>
    public bool HasSameFrameAs(WindowStyle other) =>
        (Style & (FrameBits | WS_POPUP)) == (other.Style & (FrameBits | WS_POPUP))
        && (ExStyle & FrameExBits) == (other.ExStyle & FrameExBits);

    /// <summary>
    /// These styles with the frame of <paramref name="other"/>: every bit <see cref="Borderless"/>
    /// takes off, and WS_POPUP, from it; the rest (WS_EX_TOPMOST, WS_DISABLED) from these.
    /// </summary>
    public WindowStyle WithFrameOf(WindowStyle other) => new(
        (Style & ~(StrippedBits | WS_POPUP)) | (other.Style & (StrippedBits | WS_POPUP)),
        (ExStyle & ~StrippedExBits) | (other.ExStyle & StrippedExBits));

    public override string ToString() => $"style=0x{Style:X8} ex=0x{ExStyle:X8}";
}
