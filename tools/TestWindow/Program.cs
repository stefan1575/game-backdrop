namespace TestWindow;

/// <summary>
/// A stand-in game for checking Game Backdrop's real Win32 path without a game. List this exe in
/// the settings file, then switch it between windowed, borderless-fullscreen and other sizes with
/// its buttons (or start it in a mode from the command line).
/// Usage: <c>TestWindow.exe [--mode windowed|fullscreen] [--size WIDTHxHEIGHT]</c>
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var fullscreen = Option(args, "--mode") == "fullscreen";
        var size = ParseSize(Option(args, "--size")) ?? new Size(800, 600);
        Application.Run(new TestForm(size, fullscreen));
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Size? ParseSize(string? text)
    {
        var parts = text?.Split('x');
        return parts is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height)
            ? new Size(width, height)
            : null;
    }
}

internal sealed class TestForm : Form
{
    private static readonly Size[] Sizes = [new(800, 600), new(1280, 720), new(640, 480)];
    private readonly Label _status = new() { AutoSize = true, Location = new Point(12, 48), ForeColor = Color.White };
    private Size _windowedClientSize;
    private int _nextSize;

    public TestForm(Size clientSize, bool fullscreen)
    {
        Text = "Game Backdrop test window";
        BackColor = Color.DarkSlateBlue;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(100, 100);
        _windowedClientSize = clientSize;
        ClientSize = clientSize;

        var windowed = new Button { Text = "Windowed", Location = new Point(12, 12), AutoSize = true, BackColor = SystemColors.Control };
        var borderless = new Button { Text = "Borderless fullscreen", Location = new Point(110, 12), AutoSize = true, BackColor = SystemColors.Control };
        var resize = new Button { Text = "Next size", Location = new Point(270, 12), AutoSize = true, BackColor = SystemColors.Control };
        windowed.Click += (_, _) => MakeWindowed();
        borderless.Click += (_, _) => MakeBorderlessFullscreen();
        resize.Click += (_, _) => NextSize();
        Controls.AddRange([windowed, borderless, resize, _status]);

        if (fullscreen) MakeBorderlessFullscreen();
        UpdateStatus();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateStatus();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        UpdateStatus();
    }

    private void MakeWindowed()
    {
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = _windowedClientSize;
        Location = new Point(100, 100);
    }

    private void MakeBorderlessFullscreen()
    {
        FormBorderStyle = FormBorderStyle.None;
        Bounds = Screen.FromControl(this).Bounds;
    }

    /// <summary>Change the client size, the way a game's resolution change would.</summary>
    private void NextSize()
    {
        _windowedClientSize = Sizes[_nextSize++ % Sizes.Length];
        ClientSize = _windowedClientSize;
    }

    private void UpdateStatus()
    {
        if (_status is null) return;
        _status.Text = $"Bounds {Bounds}\nClient {ClientSize}\nBorder {FormBorderStyle}";
    }
}
