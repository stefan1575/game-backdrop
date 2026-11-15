using GameBackdrop.Core.Geometry;

namespace GameBackdrop.Core.Layout;

public static class Placement
{
    /// <summary>
    /// Where a borderless window of outer size <paramref name="size"/> goes: centred on the monitor
    /// at its own size, on whole pixels, with its top-left kept on the monitor when it is larger than it.
    /// </summary>
    public static Rect Centre(Size size, Rect monitorBounds)
    {
        var left = monitorBounds.Left + Math.Max(0, (monitorBounds.Width - size.Width) / 2);
        var top = monitorBounds.Top + Math.Max(0, (monitorBounds.Height - size.Height) / 2);
        return Rect.FromPosition(left, top, size);
    }
}
