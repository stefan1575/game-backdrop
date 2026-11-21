using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Layout;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// A cloaked game: switching to another virtual desktop cloaks the game's window. The backdrop is
/// on every desktop, so it is hidden while its game is cloaked, or it would black out the desktop
/// switched to.
/// </summary>
public sealed class CloakTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private static readonly MonitorInfo Primary = FakeWindowSystem.Primary;

    private readonly FakeWindowSystem _desktop = new();
    private readonly List<GameEntry> _games = [new() { ExePath = Dota, DisplayName = "Dota 2", KeepMouseInside = true }];

    private BackdropController StartController()
    {
        var controller = new BackdropController(_desktop, new GameList(_games));
        controller.Start();
        return controller;
    }

    private FakeWindow OpenGame(bool show = true) =>
        _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), 100, 100, show);

    private FakeWindow OpenOtherApp() => _desktop.OpenWindowed(_desktop.StartProcess(Notepad), new Size(640, 480), 50, 50);

    private static Rect Centred => Placement.Centre(new Size(800, 600), Primary.Bounds);

    /// <summary>The one visible backdrop, over the whole primary monitor, directly behind the game.</summary>
    private void AssertBackdropBehind(FakeWindow game)
    {
        var backdrop = Assert.Single(_desktop.VisibleBackdropsOn(Primary));
        Assert.Equal(Primary.Bounds, backdrop.Bounds);
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
    }

    private void AssertNoVisibleBackdrop() => Assert.Empty(_desktop.VisibleBackdropsOn(Primary));

    [Fact]
    public void Cloaking_the_game_hides_its_backdrop_and_uncloaking_it_shows_the_backdrop_behind_it()
    {
        StartController();
        var game = OpenGame();
        var other = OpenOtherApp();
        _desktop.UserActivates(game);

        _desktop.Cloak(game);

        AssertNoVisibleBackdrop();
        Assert.Same(other, _desktop.Foreground);

        _desktop.Uncloak(game);

        AssertBackdropBehind(game);
        Assert.Equal([game, other], _desktop.ZOrder(game, other));
    }

    [Fact]
    public void Switching_to_an_empty_desktop_leaves_nothing_black_and_switching_back_brings_the_backdrop_back()
    {
        StartController();
        var game = OpenGame();

        _desktop.Cloak(game);

        AssertNoVisibleBackdrop();
        Assert.Null(_desktop.Foreground);

        _desktop.Uncloak(game);

        AssertBackdropBehind(game);
        Assert.Same(game, _desktop.Foreground);
    }

    [Fact]
    public void The_game_coming_to_the_front_before_it_is_uncloaked_gets_its_backdrop_only_once_it_is_uncloaked()
    {
        // Windows uncloaks the game a moment after the switch back: its Foreground event can come first.
        StartController();
        var game = OpenGame();
        _desktop.Cloak(game);

        _desktop.UserActivates(game);

        AssertNoVisibleBackdrop();

        _desktop.Uncloak(game, activate: false);

        AssertBackdropBehind(game);
    }

    [Fact]
    public void Cloaking_the_game_keeps_its_restyle_and_its_fullscreen_mark()
    {
        StartController();
        var game = OpenGame();
        var restyled = (game.Style, game.Outer);

        _desktop.Cloak(game);

        Assert.Equal(restyled, (game.Style, game.Outer));
        Assert.False(game.HasBorder);
        Assert.True(game.IsMarkedFullscreen);
    }

    [Fact]
    public void A_game_already_cloaked_when_the_app_starts_is_restyled_but_gets_its_backdrop_only_once_uncloaked()
    {
        var game = OpenGame();
        game.IsCloaked = true;

        StartController();

        Assert.False(game.HasBorder);
        Assert.Equal(Centred, game.Outer);
        AssertNoVisibleBackdrop();

        _desktop.Uncloak(game);

        AssertBackdropBehind(game);
    }

    [Fact]
    public void A_game_shown_while_cloaked_is_restyled_but_gets_its_backdrop_only_once_uncloaked()
    {
        StartController();
        var game = OpenGame(show: false);
        game.IsCloaked = true;

        _desktop.ShowWithoutActivating(game);

        Assert.False(game.HasBorder);
        AssertNoVisibleBackdrop();

        _desktop.Uncloak(game, activate: false);

        AssertBackdropBehind(game);
    }

    [Fact]
    public void A_fullscreen_game_that_goes_windowed_while_cloaked_is_restyled_but_gets_its_backdrop_only_once_uncloaked()
    {
        StartController();
        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);
        _desktop.Cloak(game);

        _desktop.SetOwnFrame(game, FakeWindowSystem.Captioned, Rect.FromPosition(100, 100,
            new Size(800 + FakeWindow.FrameLeft + FakeWindow.FrameRight, 600 + FakeWindow.FrameTop + FakeWindow.FrameBottom)));

        Assert.False(game.HasBorder);
        Assert.Equal(Centred, game.Outer);
        AssertNoVisibleBackdrop();

        _desktop.Uncloak(game);

        AssertBackdropBehind(game);
    }

    [Fact]
    public void A_game_cloaked_then_minimised_gets_its_backdrop_back_only_once_it_is_both_uncloaked_and_restored()
    {
        StartController();
        var game = OpenGame();
        OpenOtherApp();
        _desktop.Cloak(game);
        _desktop.Minimise(game);

        _desktop.Uncloak(game, activate: false);

        AssertNoVisibleBackdrop();

        _desktop.Restore(game);

        AssertBackdropBehind(game);
    }

    [Fact]
    public void A_game_minimised_then_cloaked_gets_its_backdrop_back_only_once_it_is_both_restored_and_uncloaked()
    {
        StartController();
        var game = OpenGame();
        OpenOtherApp();
        _desktop.Minimise(game);
        _desktop.Cloak(game);

        _desktop.Restore(game, activate: false);

        AssertNoVisibleBackdrop();

        _desktop.Uncloak(game);

        AssertBackdropBehind(game);
    }

    [Fact]
    public void A_game_that_closes_while_cloaked_loses_its_backdrop_and_is_forgotten()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.Cloak(game);

        _desktop.Close(game);

        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
        Assert.Empty(controller.TrackedWindows);
        Assert.Empty(controller.Stop());
    }

    [Fact]
    public void Quitting_while_the_game_is_cloaked_puts_it_back_as_it_was()
    {
        var game = OpenGame();
        var original = (game.Style, game.Outer);
        var controller = StartController();
        Assert.False(game.HasBorder);
        _desktop.Cloak(game);

        Assert.Empty(controller.Stop());

        Assert.Equal(original, (game.Style, game.Outer));
        Assert.False(game.IsMarkedFullscreen);
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
    }

    [Fact]
    public void Cloaking_the_game_frees_the_mouse_and_uncloaking_it_keeps_the_mouse_inside_again()
    {
        StartController();
        var game = OpenGame();
        Assert.Equal(game.Client, _desktop.CursorClip);

        _desktop.Cloak(game);

        Assert.Null(_desktop.CursorClip);

        _desktop.Uncloak(game);

        Assert.Equal(game.Client, _desktop.CursorClip);
    }

    [Fact]
    public void A_game_cloaked_while_it_keeps_the_foreground_frees_the_mouse()
    {
        // Cloaked for another reason than a desktop switch, with nothing else to take the foreground.
        StartController();
        var game = OpenGame();
        game.IsCloaked = true;

        _desktop.Raise(WindowEventKind.Cloaked, game);

        Assert.Same(game, _desktop.Foreground);
        Assert.Null(_desktop.CursorClip);
        AssertNoVisibleBackdrop();
    }
}
