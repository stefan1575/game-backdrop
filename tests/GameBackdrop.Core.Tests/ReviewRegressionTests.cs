using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// Whatever the app changed on a game is put back on quit, however the game changed its own style
/// meanwhile and whichever restyle or put-back Windows refused along the way (reviews 2 and 3 of
/// PR #14: F4 to F8, and the other ways into the same class). Only what the app changed goes
/// back: bits the game set itself meanwhile (WS_EX_TOPMOST, WS_DISABLED) stay.
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
        // Its title bar and place come back; the bit it set itself stays.
        AssertFrame(game, GameOriginal with { Style = Plus(FakeWindowSystem.Captioned, bits, exBits) });
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
        AssertFrame(game, GameOriginal with { Style = Plus(FakeWindowSystem.Captioned, exBits: WS_EX_TOPMOST) });
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
        AssertFrame(game, new WindowFrame(Plus(ThickPopup, exBits: WS_EX_TOPMOST), Rect.FromPosition(100, 100, new Size(816, 608))));
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

    // --- Review 3 (F7, F8): what the app owes is what it took off, nothing older or narrower ---

    [Fact]
    public void A_game_that_picks_a_frameless_window_after_its_resize_border_was_put_back_does_not_get_that_border_again()
    {
        // F7: the border put back on the first release is the game's again; the frameless window
        // it then picks is its own choice, so the second release owes nothing.
        var controller = StartController();
        var game = OpenThickPopup();
        var stripped = game.Style;
        _desktop.SetOwnFrame(game, stripped, Primary.Bounds);
        Assert.Equal(ThickPopup, game.Style);
        _time.Advance(TimeSpan.FromSeconds(5));
        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Rect.FromPosition(200, 200, new Size(800, 600)));
        Assert.Equal(TrackedState.Restyled, Assert.Single(controller.TrackedWindows).State);
        _time.Advance(TimeSpan.FromSeconds(5));

        _desktop.SetOwnFrame(game, game.Style, Primary.Bounds);

        Assert.Equal(FakeWindowSystem.Popup, game.Style);
        Assert.Empty(controller.Stop());
        Assert.Equal(FakeWindowSystem.Popup, game.Style);
        Assert.Equal(Primary.Bounds, game.Client);
    }

    [Fact]
    public void A_maximised_frameless_game_that_goes_topmost_as_it_fills_its_monitor_gets_maximised_again()
    {
        // F8: stripping takes WS_MAXIMIZE off, so it is owed like the frame bits are.
        var controller = StartController();
        var maximised = Plus(FakeWindowSystem.Popup, WindowStyle.WS_MAXIMIZE);
        var game = _desktop.Open(_desktop.StartProcess(Dota), maximised, Primary.WorkArea);
        Assert.Equal(0u, game.Style.Style & WindowStyle.WS_MAXIMIZE);

        _desktop.SetOwnFrame(game, Plus(game.Style, exBits: WS_EX_TOPMOST), Primary.Bounds);

        Assert.Equal(TrackedState.Fullscreen, Assert.Single(controller.TrackedWindows).State);
        Assert.Equal(Plus(maximised, exBits: WS_EX_TOPMOST), game.Style);
        Assert.Empty(controller.Stop());
        Assert.Equal(Plus(maximised, exBits: WS_EX_TOPMOST), game.Style);
    }

    [Fact]
    public void A_maximised_frameless_game_whose_put_back_fails_gets_maximised_again_on_quit()
    {
        var controller = StartController();
        var maximised = Plus(FakeWindowSystem.Popup, WindowStyle.WS_MAXIMIZE);
        var game = _desktop.Open(_desktop.StartProcess(Dota), maximised, Primary.WorkArea);
        _desktop.FailNextChanges(game, ChangeResult.Failed);

        _desktop.SetOwnFrame(game, Plus(game.Style, exBits: WS_EX_TOPMOST), Primary.Bounds);

        Assert.Equal(0u, game.Style.Style & WindowStyle.WS_MAXIMIZE);
        Assert.Empty(controller.Stop());
        Assert.Equal(Plus(maximised, exBits: WS_EX_TOPMOST), game.Style);
    }

    [Fact]
    public void A_released_game_whose_border_was_put_back_and_that_goes_windowed_with_it_is_stripped_and_owes_it_again()
    {
        // The border is the game's again once it is back, so the next strip owes it afresh.
        var controller = StartController();
        var game = OpenThickPopup();
        _desktop.SetOwnFrame(game, game.Style, Primary.Bounds);
        Assert.Equal(ThickPopup, game.Style);
        _time.Advance(TimeSpan.FromSeconds(5));
        _desktop.SetOwnFrame(game, game.Style, Rect.FromPosition(200, 200, new Size(816, 608)));
        Assert.Equal(FakeWindowSystem.Popup, game.Style);
        _time.Advance(TimeSpan.FromSeconds(5));

        _desktop.SetOwnFrame(game, game.Style, Primary.Bounds);

        Assert.Equal(ThickPopup, game.Style);
        Assert.Equal(Primary.Bounds, game.Client);
        Assert.Empty(controller.Stop());
        Assert.Equal(ThickPopup, game.Style);
    }

    [Fact]
    public void A_released_game_that_sets_its_own_border_and_then_drops_it_again_itself_owes_nothing_on_quit()
    {
        // The put-back failed, so the border was owed; the game then set a border of its own and
        // took it off again. Both are the game's choices: by then nothing of the app's is left.
        var controller = StartController();
        var game = OpenThickPopup();
        var stripped = game.Style;
        _desktop.FailNextChanges(game, ChangeResult.Failed);
        _desktop.SetOwnFrame(game, stripped, Primary.Bounds);
        _desktop.SetOwnFrame(game, ThickPopup, new Rect(-8, -8, 1928, 1088));

        _desktop.SetOwnFrame(game, stripped, Primary.Bounds);

        var changes = _desktop.FrameChanges.Count;
        Assert.Empty(controller.Stop());
        Assert.Equal(changes, _desktop.FrameChanges.Count);
        Assert.Equal(stripped, game.Style);
    }

    [Fact]
    public void A_game_a_modal_dialog_disabled_stays_disabled_when_quitting_puts_its_title_bar_back()
    {
        // Putting back the title bar is the app's; the WS_DISABLED is the game's, and stays.
        var controller = StartController();
        var game = OpenGame();

        _desktop.SetOwnFrame(game, Plus(game.Style, WS_DISABLED), game.Outer);

        Assert.Empty(controller.Stop());
        AssertFrame(game, GameOriginal with { Style = Plus(FakeWindowSystem.Captioned, WS_DISABLED) });
    }

    [Fact]
    public void A_game_that_sets_its_own_popup_style_still_keeps_it_and_quitting_leaves_it()
    {
        // The decision (2026-11-16, option A) stands: a new frame of the game's own is the
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
