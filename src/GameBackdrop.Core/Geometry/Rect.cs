namespace GameBackdrop.Core.Geometry;

/// <summary>A screen rectangle in physical pixels, right and bottom exclusive (like Win32's RECT).</summary>
public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public Size Size => new(Width, Height);

    public static Rect FromPosition(int left, int top, Size size) =>
        new(left, top, left + size.Width, top + size.Height);

    /// <summary>True when this rectangle covers all of <paramref name="other"/>.</summary>
    public bool Covers(Rect other) =>
        Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

public readonly record struct Size(int Width, int Height)
{
    public int Area => Width * Height;
    public override string ToString() => $"{Width}x{Height}";
}
