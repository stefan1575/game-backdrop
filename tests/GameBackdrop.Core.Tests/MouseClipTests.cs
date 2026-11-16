using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>Keeping the mouse inside a backdropped game while it is in front (<c>keepMouseInside</c>).</summary>
public sealed class MouseClipTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private readonly FakeWindowSystem _desktop = new();
    private readonly List<GameEntry> _games =
    [
        new() { ExePath = Dota, DisplayName = "Dota 2" },
        new() { ExePath = Novel, DisplayName = "Novel", KeepMouseInside = false },
    ];

    private BackdropController StartController()
    {
        var controller = new BackdropController(_desktop, new GameList(_games));
        controller.Start();
        return controller;
    }

    private FakeWindow OpenGame(string exe = Dota, int left = 100, int top = 100) =>
        _desktop.OpenWindowed(_desktop.StartProcess(exe), new Size(800, 600), left, top);

    private FakeWindow OpenOtherApp() => _desktop.OpenWindowed(_desktop.StartProcess(Notepad), new Size(640, 480), 50, 50);

    [Fact]
    public void A_backdropped_game_in_front_keeps_the_mouse_inside_its_client_area()
    {
        StartController();

        var game = OpenGame();

        Assert.Equal(new Rect(560, 240, 1360, 840), _desktop.CursorClip);
        Assert.Equal(game.Client, _desktop.CursorClip);
    }
}
