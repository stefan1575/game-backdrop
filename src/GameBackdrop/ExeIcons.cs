namespace GameBackdrop;

/// <summary>Each exe's icon at one size, read from the exe file once and kept until disposed.</summary>
internal sealed class ExeIcons(int size) : IDisposable
{
    private readonly Dictionary<string, Bitmap> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exe's own icon, or the generic program icon if it has none or can't be read (moved, deleted).</summary>
    public Bitmap For(string exePath)
    {
        if (_icons.TryGetValue(exePath, out var known)) return known;
        Icon? icon = null;
        try
        {
            icon = File.Exists(exePath) ? Icon.ExtractIcon(exePath, 0, size) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fall back to the generic icon below.
        }
        using (icon)
        {
            var bitmap = icon?.ToBitmap() ?? GenericIcon();
            _icons[exePath] = bitmap;
            return bitmap;
        }
    }

    private Bitmap GenericIcon()
    {
        using var generic = SystemIcons.Application.ToBitmap();
        return new Bitmap(generic, size, size);
    }

    public void Dispose()
    {
        foreach (var bitmap in _icons.Values) bitmap.Dispose();
        _icons.Clear();
    }
}
