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
    /// A window is fullscreen when its outer rect covers its monitor's full bounds (not just the
    /// work area); anything smaller is windowed.
    /// </summary>
    public static WindowMode Classify(Rect outer, Rect monitorBounds) =>
        outer.Covers(monitorBounds) ? WindowMode.Fullscreen : WindowMode.Windowed;
}
