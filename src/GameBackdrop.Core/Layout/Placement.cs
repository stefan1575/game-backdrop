using GameBackdrop.Core.Geometry;

namespace GameBackdrop.Core.Layout;

public static class Placement
{
    /// <summary>
    /// Where a borderless window of <paramref name="clientSize"/> goes: centred on the monitor at
    /// its own size, on whole pixels, with its top-left kept on the monitor when it is larger than it.
    /// </summary>
    public static Rect Centre(Size clientSize, Rect monitorBounds)
    {
        var left = monitorBounds.Left + Math.Max(0, (monitorBounds.Width - clientSize.Width) / 2);
        var top = monitorBounds.Top + Math.Max(0, (monitorBounds.Height - clientSize.Height) / 2);
        return Rect.FromPosition(left, top, clientSize);
    }
}
