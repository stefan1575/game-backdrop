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

    [Fact]
    public void Another_app_coming_to_the_front_frees_the_mouse()
    {
        StartController();
        OpenGame();

        OpenOtherApp();

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void Alt_tabbing_back_to_the_game_keeps_the_mouse_inside_it_again()
    {
        StartController();
        var game = OpenGame();
        var other = OpenOtherApp();

        _desktop.UserActivates(game);
        Assert.Equal(game.Client, _desktop.CursorClip);

        _desktop.UserActivates(other);
        Assert.Null(_desktop.CursorClip);

        _desktop.UserActivates(game);
        Assert.Equal(game.Client, _desktop.CursorClip);
    }

    [Fact]
    public void Clicking_the_black_hands_focus_back_to_the_game_and_keeps_the_mouse_inside_it()
    {
        StartController();
        var game = OpenGame();
        var backdrop = Assert.Single(_desktop.VisibleBackdropsOn(FakeWindowSystem.Primary));
        // A window on the other monitor has the foreground; the user clicks the black.
        _desktop.OpenWindowed(_desktop.StartProcess(Notepad), new Size(640, 480), 2000, 0);

        _desktop.ClickBackdrop(backdrop.Handle);

        Assert.Same(game, _desktop.Foreground);
        Assert.Equal(game.Client, _desktop.CursorClip);
    }

    [Fact]
    public void Minimising_the_game_frees_the_mouse_and_restoring_it_keeps_the_mouse_inside_again()
    {
        StartController();
        var game = OpenGame();
        OpenOtherApp();
        _desktop.UserActivates(game);

        _desktop.Minimise(game);

        Assert.Null(_desktop.CursorClip);

        _desktop.Restore(game);

        Assert.Equal(game.Client, _desktop.CursorClip);
    }

    [Fact]
    public void A_game_minimised_while_it_is_the_only_window_frees_the_mouse()
    {
        StartController();
        var game = OpenGame();

        _desktop.Minimise(game);

        Assert.Null(_desktop.Foreground);
        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void The_game_hiding_its_window_frees_the_mouse_even_while_it_keeps_the_foreground()
    {
        StartController();
        var game = OpenGame();

        _desktop.HideKeepingForeground(game);

        Assert.Null(_desktop.CursorClip);

        _desktop.ShowStillForeground(game);

        Assert.Equal(game.Client, _desktop.CursorClip);
    }

    [Fact]
    public void The_game_exiting_frees_the_mouse()
    {
        StartController();
        var game = OpenGame();

        _desktop.Close(game);

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void Quitting_the_app_frees_the_mouse()
    {
        var controller = StartController();
        OpenGame();

        controller.Stop();

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void A_splash_window_replaced_by_the_game_s_main_window_hands_the_mouse_to_the_main_window()
    {
        StartController();
        var process = _desktop.StartProcess(Dota);
        var splash = _desktop.Open(process, new WindowStyle(0x86000000 | WindowStyle.WS_THICKFRAME, 0), Rect.FromPosition(100, 100, new Size(400, 300)));
        Assert.Equal(splash.Client, _desktop.CursorClip);

        var main = _desktop.OpenWindowed(process, new Size(1280, 720));

        Assert.Equal(main.Client, _desktop.CursorClip);
        Assert.Equal(new Rect(320, 180, 1600, 900), _desktop.CursorClip);
    }

    [Fact]
    public void The_mouse_follows_the_game_s_client_area_when_the_game_moves_or_resizes_itself()
    {
        StartController();
        var game = OpenGame();

        // The controller centres the game again where it resized itself to; the clip is its client area there.
        _desktop.Move(game, Rect.FromPosition(400, 200, new Size(1024, 768)));

        Assert.Equal(game.Client, _desktop.CursorClip);
        Assert.Equal(new Size(1024, 768), _desktop.CursorClip!.Value.Size);
    }

    [Fact]
    public void The_mouse_follows_the_game_to_another_monitor()
    {
        StartController();
        var game = OpenGame();

        _desktop.Move(game, Rect.FromPosition(2100, 0, game.Outer.Size));

        Assert.Equal(game.Client, _desktop.CursorClip);
        Assert.Equal(new Rect(2800, 220, 3600, 820), _desktop.CursorClip);
    }

    [Fact]
    public void A_game_moving_while_another_app_is_in_front_leaves_the_mouse_free()
    {
        StartController();
        var game = OpenGame();
        OpenOtherApp();

        _desktop.Move(game, Rect.FromPosition(400, 200, game.Outer.Size));

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void A_late_foreground_event_for_a_game_the_user_has_left_leaves_the_mouse_free()
    {
        StartController();
        var game = OpenGame();
        OpenOtherApp();

        _desktop.Raise(WindowEventKind.Foreground, game);

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void A_game_with_keep_mouse_inside_off_never_holds_the_mouse()
    {
        StartController();
        var game = OpenGame(Novel);
        OpenOtherApp();

        _desktop.UserActivates(game);
        _desktop.Move(game, Rect.FromPosition(400, 200, game.Outer.Size));

        Assert.Null(_desktop.CursorClip);
        Assert.DoesNotContain(_desktop.Calls, c => c.Call == PortCall.ClipCursor);
        Assert.Single(_desktop.VisibleBackdropsOn(FakeWindowSystem.Primary)); // still backdropped
    }

    [Fact]
    public void Switching_from_a_game_that_holds_the_mouse_to_one_that_does_not_frees_it()
    {
        StartController();
        OpenGame(Dota);

        OpenGame(Novel, left: 2000, top: 0);

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void A_game_in_its_own_fullscreen_mode_is_left_to_handle_the_mouse_itself()
    {
        StartController();

        _desktop.OpenPopup(_desktop.StartProcess(Dota), FakeWindowSystem.Primary.Bounds);

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void A_disabled_game_never_holds_the_mouse()
    {
        _games[0].Enabled = false;
        StartController();

        OpenGame();

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void A_game_already_in_front_when_the_app_starts_keeps_the_mouse_inside_at_once()
    {
        var game = OpenGame();

        StartController();

        Assert.Equal(game.Client, _desktop.CursorClip);
    }

    [Fact]
    public void A_game_already_running_behind_another_app_when_the_app_starts_leaves_the_mouse_free()
    {
        OpenGame();
        OpenOtherApp();

        StartController();

        Assert.Null(_desktop.CursorClip);
    }

    [Fact]
    public void Freeing_the_mouse_leaves_a_clip_the_game_set_itself_alone()
    {
        var controller = StartController();
        var game = OpenGame();

        _desktop.SomeoneElseClipsCursor(new Rect(600, 300, 1000, 700));
        controller.Stop();

        Assert.Equal(new Rect(600, 300, 1000, 700), _desktop.CursorClip);
    }
}
