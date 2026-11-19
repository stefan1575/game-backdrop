namespace GameBackdrop;

/// <summary>
/// The app's icon (<c>GameBackdrop.ico</c>, drawn by <c>tools/make-icon.ps1</c>): a game window
/// centred on a black screen. The exe carries it too, for Explorer and the Start menu. Each size is
/// loaded once and kept for the app's life, since the list window can open many times.
/// </summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon> SmallIcon = new(() => Load(SystemInformation.SmallIconSize));
    private static readonly Lazy<Icon> LargeIcon = new(() => Load(SystemInformation.IconSize));

    /// <summary>At the small icon size: the tray, a window's title bar.</summary>
    public static Icon Small => SmallIcon.Value;

    /// <summary>At the large icon size: Alt+Tab, the taskbar.</summary>
    public static Icon Large => LargeIcon.Value;

    private static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("GameBackdrop.GameBackdrop.ico")
            ?? throw new InvalidOperationException("The app icon isn't embedded.");
        return new Icon(stream, size);
    }
}
