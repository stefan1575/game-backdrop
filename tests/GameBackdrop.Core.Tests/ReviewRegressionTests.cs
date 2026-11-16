using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// Whatever the app changed on a game is put back on quit, however the game changed its own style
/// meanwhile and whichever restyle or put-back Windows refused along the way (Sol's review 2 of
/// PR #14: F4, F5, F6, and the other ways into the same class).
/// </summary>
public sealed class ReviewRegressionTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";

    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_DISABLED = 0x08000000;

    private static readonly MonitorInfo Primary = FakeWindowSystem.Primary;

    private readonly FakeWindowSystem _desktop = new();
    private readonly ManualTime _time = new();

    private BackdropController StartController()
    {
        var controller = new BackdropController(_desktop, new GameList([new GameEntry { ExePath = Dota, DisplayName = "Dota 2" }]), _time);
        controller.Start();
        return controller;
    }

    /// <summary>An 800x600 captioned game at 100,100.</summary>
    private FakeWindow OpenGame() => _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), 100, 100);

    private static readonly WindowFrame GameOriginal =
        new(FakeWindowSystem.Captioned, Rect.FromPosition(100, 100, new Size(816, 639)));

    /// <summary>A frameless game with a resize border: stripping it takes the border off.</summary>
    private static readonly WindowStyle ThickPopup = new(FakeWindowSystem.Popup.Style | WindowStyle.WS_THICKFRAME, 0);

    private FakeWindow OpenThickPopup(FakeProcess? process = null) =>
        _desktop.Open(process ?? _desktop.StartProcess(Dota), ThickPopup, Rect.FromPosition(100, 100, new Size(816, 608)));

    private static WindowStyle Plus(WindowStyle style, uint bits = 0, uint exBits = 0) => new(style.Style | bits, style.ExStyle | exBits);

    private static void AssertFrame(FakeWindow window, WindowFrame frame)
    {
        Assert.Equal(frame.Style, window.Style);
        Assert.Equal(frame.Outer, window.Outer);
    }

    private void AssertBackdropped(FakeWindow game)
    {
        var backdrop = Assert.Single(_desktop.VisibleBackdropsOn(Primary));
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
        Assert.True(game.IsMarkedFullscreen);
    }

    // --- F6: a completed restoration leaves nothing pending ---

    [Fact]
    public void A_replaced_launcher_whose_style_could_not_be_put_back_gets_it_back_on_quit_and_is_not_reported()
    {
        // F6: the launcher went fullscreen itself, and putting its own style back failed twice:
        // once on release, once when the game window replaced it.
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        var launcher = OpenThickPopup(process);
        var stripped = launcher.Style;
        _desktop.FailNextChanges(launcher, ChangeResult.Failed);
        _desktop.SetOwnFrame(launcher, stripped, Primary.Bounds);
        _desktop.FailNextChanges(launcher, ChangeResult.Failed);

        var game = _desktop.OpenWindowed(process, new Size(1280, 720));

        Assert.Equal(stripped, launcher.Style);
        Assert.Contains(controller.TrackedWindows, t => t.Window == game.Handle && t.State == TrackedState.Restyled);
        Assert.Empty(controller.Stop());
        // As on release, only its style was owed: it keeps the place it filled itself.
        Assert.Equal(ThickPopup, launcher.Style);
        Assert.Equal(Primary.Bounds, launcher.Client);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void A_replaced_launcher_that_went_fullscreen_itself_keeps_its_frame_when_only_its_mark_is_retried()
    {
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        var launcher = _desktop.OpenWindowed(process, new Size(400, 300), left: 40, top: 50);
        _desktop.FailNextMarks(launcher);
        _desktop.SetOwnFrame(launcher, FakeWindowSystem.Popup, Primary.Bounds);
        _desktop.FailNextMarks(launcher);

        _desktop.OpenWindowed(process, new Size(1280, 720));

        Assert.True(launcher.IsMarkedFullscreen);
        Assert.Empty(controller.Stop());
        Assert.False(launcher.IsMarkedFullscreen);
        AssertFrame(launcher, new WindowFrame(FakeWindowSystem.Popup, Primary.Bounds));
    }

    // --- F5: a restyle that changed nothing leaves what is owed owed ---

    [Theory]
    [InlineData(ChangeResult.AccessDenied)]
    [InlineData(ChangeResult.Failed)]
    public void A_released_game_whose_style_is_still_owed_gets_it_back_on_quit_after_a_refused_restyle(ChangeResult refused)
    {
        var controller = StartController();
        var game = OpenThickPopup();
        var stripped = game.Style;
        _desktop.FailNextChanges(game, ChangeResult.Failed);
        _desktop.SetOwnFrame(game, stripped, Primary.Bounds);
        _desktop.FailNextChanges(game, refused);

        _desktop.SetOwnFrame(game, stripped, Rect.FromPosition(200, 200, new Size(800, 600)));

        Assert.Equal(stripped, game.Style);
        Assert.Empty(controller.Stop());
        Assert.Equal(ThickPopup, game.Style);
        Assert.Equal(new Size(800, 600), game.Client.Size);
    }

    // --- F4: a change the game makes outside its frame keeps the frame the app saved ---

    [Theory]
    [InlineData(0u, WS_EX_TOPMOST)]
    [InlineData(WS_DISABLED, 0u)] // a modal dialog disables it
    public void A_captioned_game_that_changes_another_style_bit_and_fills_its_monitor_keeps_its_backdrop_and_gets_its_title_bar_back(uint bits, uint exBits)
    {
        var controller = StartController();
        var game = OpenGame();

        _desktop.SetOwnFrame(game, Plus(game.Style, bits, exBits), Primary.Bounds);

        // It still has its title bar as far as it knows: windowed, at its monitor's size.
        Assert.Equal(TrackedState.Restyled, Assert.Single(controller.TrackedWindows).State);
        AssertBackdropped(game);
        Assert.Empty(controller.Stop());
        AssertFrame(game, GameOriginal);
    }

    [Fact]
    public void A_captioned_game_that_goes_topmost_and_later_fills_its_monitor_still_gets_its_title_bar_back()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.SetOwnFrame(game, Plus(game.Style, exBits: WS_EX_TOPMOST), game.Outer);
        _time.Advance(TimeSpan.FromSeconds(5));

        _desktop.SetOwnFrame(game, game.Style, Primary.Bounds);

        Assert.Equal(TrackedState.Restyled, Assert.Single(controller.TrackedWindows).State);
        AssertBackdropped(game);
        Assert.Empty(controller.Stop());
        AssertFrame(game, GameOriginal);
    }

    [Fact]
    public void A_frameless_game_that_goes_topmost_as_it_fills_its_monitor_gets_its_resize_border_back_and_keeps_topmost()
    {
        var controller = StartController();
        var game = OpenThickPopup();

        _desktop.SetOwnFrame(game, Plus(game.Style, exBits: WS_EX_TOPMOST), Primary.Bounds);

        Assert.Equal(Plus(ThickPopup, exBits: WS_EX_TOPMOST), game.Style);
        Assert.Equal(Primary.Bounds, game.Client);
        Assert.Equal(TrackedState.Fullscreen, Assert.Single(controller.TrackedWindows).State);
        var changes = _desktop.FrameChanges.Count;
        Assert.Empty(controller.Stop());
        Assert.Equal(changes, _desktop.FrameChanges.Count);
    }

    [Fact]
    public void A_released_game_whose_style_is_still_owed_and_that_goes_topmost_gets_its_resize_border_back_on_quit()
    {
        var controller = StartController();
        var game = OpenThickPopup();
        var stripped = game.Style;
        _desktop.FailNextChanges(game, ChangeResult.Failed);
        _desktop.SetOwnFrame(game, stripped, Primary.Bounds);

        _desktop.SetOwnFrame(game, Plus(stripped, exBits: WS_EX_TOPMOST), game.Outer);

        Assert.Empty(controller.Stop());
        Assert.Equal(Plus(ThickPopup, exBits: WS_EX_TOPMOST), game.Style);
        Assert.Equal(Primary.Bounds, game.Client);
    }

    [Fact]
    public void A_released_game_whose_style_is_still_owed_and_that_goes_windowed_after_going_topmost_gets_its_first_frame_back_on_quit()
    {
        var controller = StartController();
        var game = OpenThickPopup();
        var stripped = game.Style;
        _desktop.FailNextChanges(game, ChangeResult.Failed);
        _desktop.SetOwnFrame(game, stripped, Primary.Bounds);
        _desktop.SetOwnFrame(game, Plus(stripped, exBits: WS_EX_TOPMOST), game.Outer);
        _time.Advance(TimeSpan.FromSeconds(5));

        _desktop.SetOwnFrame(game, game.Style, Rect.FromPosition(200, 200, new Size(800, 600)));

        Assert.Equal(TrackedState.Restyled, Assert.Single(controller.TrackedWindows).State);
        Assert.Empty(controller.Stop());
        AssertFrame(game, new WindowFrame(ThickPopup, Rect.FromPosition(100, 100, new Size(816, 608))));
    }

    [Fact]
    public void A_released_game_that_puts_its_own_resize_border_back_itself_is_left_alone_on_quit()
    {
        var controller = StartController();
        var game = OpenThickPopup();
        var stripped = game.Style;
        _desktop.FailNextChanges(game, ChangeResult.Failed);
        _desktop.SetOwnFrame(game, stripped, Primary.Bounds);

        var ownFullscreen = new Rect(-8, -8, 1928, 1088);
        _desktop.SetOwnFrame(game, ThickPopup, ownFullscreen);

        var changes = _desktop.FrameChanges.Count;
        Assert.Empty(controller.Stop());
        Assert.Equal(changes, _desktop.FrameChanges.Count);
        AssertFrame(game, new WindowFrame(ThickPopup, ownFullscreen));
    }

    [Fact]
    public void A_game_that_sets_its_own_popup_style_still_keeps_it_and_quitting_leaves_it()
    {
        // Stefan's decision (2026-11-16, option A) stands: a new frame of the game's own is the
        // game's, and nothing of the app's is left in it.
        var controller = StartController();
        var game = OpenGame();

        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Primary.Bounds);

        Assert.Equal(TrackedState.Fullscreen, Assert.Single(controller.TrackedWindows).State);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        var changes = _desktop.FrameChanges.Count;
        Assert.Empty(controller.Stop());
        Assert.Equal(changes, _desktop.FrameChanges.Count);
        AssertFrame(game, new WindowFrame(FakeWindowSystem.Popup, Primary.Bounds));
    }
}
