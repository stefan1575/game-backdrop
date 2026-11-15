using GameBackdrop.Core.Geometry;

namespace GameBackdrop.Core.Layout;

public enum WindowMode
{
    /// <summary>Exclusive or borderless fullscreen: the game's own mode wins.</summary>
    Fullscreen,
    Windowed,
}

public static class WindowClassifier
{
    /// <summary>
    /// A window is fullscreen when its client area covers its monitor's full bounds (not just the
    /// work area) and it has no title bar. A captioned window is windowed even when its client
    /// area is the monitor's size: stripping and centring it leaves it filling the screen.
    /// </summary>
    public static WindowMode Classify(Rect client, bool hasCaption, Rect monitorBounds) =>
        !hasCaption && client.Covers(monitorBounds) ? WindowMode.Fullscreen : WindowMode.Windowed;
}
