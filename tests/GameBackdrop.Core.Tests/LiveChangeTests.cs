using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>A game that changes its own window while it runs: fullscreen and back, resolution changes, a launcher.</summary>
public sealed class LiveChangeTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private static readonly MonitorInfo Primary = FakeWindowSystem.Primary;
    private static readonly MonitorInfo Secondary = FakeWindowSystem.Secondary;
    private static readonly Rect NewcomerRect = Rect.FromPosition(300, 200, new Size(416, 339));

    private readonly FakeWindowSystem _desktop = new();
    private readonly ManualTime _time = new();
    private readonly List<GameEntry> _games =
    [
        new() { ExePath = Dota, DisplayName = "Dota 2" },
        new() { ExePath = Novel, DisplayName = "Novel" },
    ];

    private BackdropController StartController()
    {
        var controller = new BackdropController(_desktop, new GameList(_games), _time);
        controller.Start();
        return controller;
    }

    /// <summary>An 800x600 windowed game at 100,100, so restyled to 800x600 at 560,240.</summary>
    private FakeWindow OpenGame(int left = 100, int top = 100) =>
        _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left, top);

    private static readonly WindowFrame GameOriginal =
        new(FakeWindowSystem.Captioned, Rect.FromPosition(100, 100, new Size(816, 639)));

    private FakeBackdrop BackdropOver(MonitorInfo monitor)
    {
        var backdrop = Assert.Single(_desktop.VisibleBackdropsOn(monitor));
        Assert.Equal(monitor.Bounds, backdrop.Bounds);
        return backdrop;
    }

    private void AssertBackdroppedAndMarked(FakeWindow game, MonitorInfo monitor)
    {
        Assert.Same(BackdropOver(monitor), _desktop.DirectlyBehind(game));
        Assert.True(game.IsMarkedFullscreen);
        Assert.True(_desktop.TaskbarCovered(monitor));
    }

    private void AssertNoVisibleBackdropAndNotMarked(FakeWindow game)
    {
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        Assert.Empty(_desktop.VisibleBackdropsOn(Secondary));
        Assert.False(game.IsMarkedFullscreen);
    }

    private static void AssertFrame(FakeWindow window, WindowFrame frame)
    {
        Assert.Equal(frame.Style, window.Style);
        Assert.Equal(frame.Outer, window.Outer);
    }

    // --- Windowed to fullscreen ---

    public static TheoryData<string> FullscreenStyles => ["the game's own popup", "the app's borderless style kept"];

    [Theory]
    [MemberData(nameof(FullscreenStyles))]
    public void A_restyled_game_that_switches_to_fullscreen_loses_its_backdrop_and_mark_and_keeps_its_own_mode(string how)
    {
        var controller = StartController();
        var game = OpenGame();
        var style = how == "the game's own popup" ? FakeWindowSystem.Popup : game.Style;

        _desktop.SetOwnFrame(game, style, Primary.Bounds);

        AssertNoVisibleBackdropAndNotMarked(game);
        AssertFrame(game, new WindowFrame(style, Primary.Bounds));
        Assert.Equal(TrackedState.Fullscreen, Assert.Single(controller.TrackedWindows).State);
    }

    [Fact]
    public void Quitting_leaves_a_game_that_went_fullscreen_in_its_own_mode()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Primary.Bounds);
        var changes = _desktop.FrameChanges.Count;

        Assert.Empty(controller.Stop());

        AssertFrame(game, new WindowFrame(FakeWindowSystem.Popup, Primary.Bounds));
        Assert.Equal(changes, _desktop.FrameChanges.Count);
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
    }

    [Fact]
    public void A_failed_unmark_when_the_game_goes_fullscreen_is_retried_on_quit_without_touching_its_frame()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.FailNextMarks(game);

        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Primary.Bounds);

        Assert.True(game.IsMarkedFullscreen);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        var changes = _desktop.FrameChanges.Count;
        Assert.Empty(controller.Stop());
        Assert.False(game.IsMarkedFullscreen);
        AssertFrame(game, new WindowFrame(FakeWindowSystem.Popup, Primary.Bounds));
        Assert.Equal(changes, _desktop.FrameChanges.Count);
    }

    [Fact]
    public void A_captioned_game_whose_client_area_is_its_monitor_s_size_keeps_its_backdrop_once_stripped()
    {
        var controller = StartController();
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(1920, 1080), left: 0, top: 0);

        _desktop.Move(game, game.Outer); // any later change of place, its own included

        Assert.Equal(Primary.Bounds, game.Outer);
        AssertBackdroppedAndMarked(game, Primary);
        Assert.Equal(TrackedState.Restyled, Assert.Single(controller.TrackedWindows).State);
    }

    // --- Fullscreen to windowed ---

    [Fact]
    public void A_fullscreen_game_that_switches_to_windowed_is_restyled_centred_and_backdropped()
    {
        StartController();
        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);
        Assert.Empty(_desktop.Backdrops);

        _desktop.SetOwnFrame(game, FakeWindowSystem.Captioned, Rect.FromPosition(100, 100, new Size(1296, 759)));

        Assert.False(game.HasBorder);
        Assert.Equal(Rect.FromPosition(320, 180, new Size(1280, 720)), game.Outer);
        AssertBackdroppedAndMarked(game, Primary);
    }

    [Fact]
    public void Quitting_puts_back_a_game_that_went_windowed_as_it_was_before_the_app_restyled_it()
    {
        var controller = StartController();
        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);
        var windowed = new WindowFrame(FakeWindowSystem.Captioned, Rect.FromPosition(100, 100, new Size(1296, 759)));
        _desktop.SetOwnFrame(game, windowed.Style, windowed.Outer);

        Assert.Empty(controller.Stop());

        AssertFrame(game, windowed);
        Assert.False(game.IsMarkedFullscreen);
    }

    [Fact]
    public void A_game_that_goes_fullscreen_and_back_to_windowed_gets_its_backdrop_again_and_quitting_puts_back_its_first_frame()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Primary.Bounds);

        _desktop.SetOwnFrame(game, FakeWindowSystem.Captioned, Rect.FromPosition(300, 300, new Size(816, 639)));

        Assert.False(game.HasBorder);
        Assert.Equal(Rect.FromPosition(560, 240, new Size(800, 600)), game.Outer);
        AssertBackdroppedAndMarked(game, Primary);
        Assert.Empty(controller.Stop());
        AssertFrame(game, GameOriginal);
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
    }

    // --- Resolution changes ---

    [Fact]
    public void A_restyled_game_that_changes_its_resolution_is_centred_again_with_its_backdrop_behind_it()
    {
        var controller = StartController();
        var game = OpenGame();

        _desktop.ResizeClient(game, new Size(1280, 720));

        Assert.False(game.HasBorder);
        Assert.Equal(Rect.FromPosition(320, 180, new Size(1280, 720)), game.Outer);
        AssertBackdroppedAndMarked(game, Primary);
        Assert.Empty(controller.Stop());
        AssertFrame(game, GameOriginal);
    }

    [Fact]
    public void A_game_that_puts_its_title_bar_back_on_a_resolution_change_is_stripped_again_at_its_new_size()
    {
        StartController();
        var game = OpenGame();

        _desktop.SetOwnFrame(game, FakeWindowSystem.Captioned, Rect.FromPosition(560, 240, new Size(1296, 759)));

        Assert.False(game.HasBorder);
        Assert.Equal(Rect.FromPosition(320, 180, new Size(1280, 720)), game.Outer);
        AssertBackdroppedAndMarked(game, Primary);
    }

    [Fact]
    public void A_game_on_the_second_monitor_that_changes_its_resolution_is_centred_again_there()
    {
        StartController();
        var game = OpenGame(left: 2500);

        _desktop.ResizeClient(game, new Size(1280, 720));

        Assert.Equal(Rect.FromPosition(1920 + 640, -200 + 360, new Size(1280, 720)), game.Outer);
        AssertBackdroppedAndMarked(game, Secondary);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_game_that_resizes_about_its_centre_is_left_where_it_is()
    {
        StartController();
        var game = OpenGame();
        var changes = _desktop.FrameChanges.Count;

        _desktop.Move(game, Rect.FromPosition(320, 180, new Size(1280, 720)));

        Assert.Equal(changes, _desktop.FrameChanges.Count);
        AssertBackdroppedAndMarked(game, Primary);
    }

    [Fact]
    public void A_game_moved_to_the_centre_of_the_other_monitor_takes_its_backdrop_there()
    {
        StartController();
        var game = OpenGame();
        var changes = _desktop.FrameChanges.Count;

        _desktop.Move(game, Rect.FromPosition(1920 + 880, -200 + 420, new Size(800, 600)));

        Assert.Equal(changes, _desktop.FrameChanges.Count);
        AssertBackdroppedAndMarked(game, Secondary);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    // --- A switch made while the window is hidden ---

    [Fact]
    public void A_restyled_game_that_hides_its_window_to_go_fullscreen_is_released_when_it_shows_again()
    {
        StartController();
        var game = OpenGame();
        _desktop.Hide(game);
        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Primary.Bounds);

        _desktop.Show(game);

        AssertNoVisibleBackdropAndNotMarked(game);
        AssertFrame(game, new WindowFrame(FakeWindowSystem.Popup, Primary.Bounds));
    }

    [Fact]
    public void A_fullscreen_game_that_hides_its_window_to_go_windowed_is_restyled_when_it_shows_again()
    {
        StartController();
        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);
        _desktop.Hide(game);
        _desktop.SetOwnFrame(game, FakeWindowSystem.Captioned, Rect.FromPosition(100, 100, new Size(1296, 759)));

        _desktop.Show(game);

        Assert.Equal(Rect.FromPosition(320, 180, new Size(1280, 720)), game.Outer);
        AssertBackdroppedAndMarked(game, Primary);
    }

    // --- Never fighting the game ---

    [Fact]
    public void The_app_s_own_restyle_and_re_centring_set_off_no_further_change()
    {
        StartController();
        var game = OpenGame();
        Assert.Single(_desktop.FrameChanges);

        _desktop.ResizeClient(game, new Size(1280, 720));
        Assert.Equal(2, _desktop.FrameChanges.Count);

        // Its own LocationChanged events have been delivered; more of them change nothing either.
        _desktop.Move(game, game.Outer);
        _desktop.Move(game, game.Outer);
        Assert.Equal(2, _desktop.FrameChanges.Count);
        Assert.Equal(Rect.FromPosition(320, 180, new Size(1280, 720)), game.Outer);
    }

    [Fact]
    public void A_game_that_answers_every_placement_by_resizing_again_wins_after_a_few_tries_and_is_followed_again_later()
    {
        StartController();
        var game = OpenGame();
        game.AnswersFrameChange = w => w.Outer = Rect.FromPosition(w.Outer.Left, w.Outer.Top, new Size(w.Outer.Width + 10, w.Outer.Height + 10));

        _desktop.ResizeClient(game, new Size(1280, 720)); // without a limit, the fake throws: the controller never settles

        Assert.Equal(1 + BackdropController.MaxFights, _desktop.FrameChanges.Count);
        Assert.False(game.HasBorder);
        AssertBackdroppedAndMarked(game, Primary);

        game.AnswersFrameChange = null;
        _time.Advance(TimeSpan.FromSeconds(5));
        _desktop.ResizeClient(game, new Size(1024, 768));
        Assert.Equal(Rect.FromPosition(448, 156, new Size(1024, 768)), game.Outer);
    }

    [Fact]
    public void Resolution_changes_a_while_apart_are_each_followed()
    {
        StartController();
        var game = OpenGame();

        foreach (var size in new[] { new Size(1024, 768), new Size(1280, 720), new Size(640, 480), new Size(800, 600), new Size(1600, 900) })
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            _desktop.ResizeClient(game, size);
            Assert.Equal(Placement(size), game.Outer);
        }

        static Rect Placement(Size size) => Rect.FromPosition((1920 - size.Width) / 2, (1080 - size.Height) / 2, size);
    }

    // --- A launcher, then the game window ---

    [Fact]
    public void A_launcher_window_is_put_back_and_released_once_the_game_window_appears_which_alone_is_tracked_and_backdropped()
    {
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        var launcher = _desktop.OpenWindowed(process, new Size(400, 300), left: 40, top: 50);
        var launcherOriginal = new WindowFrame(FakeWindowSystem.Captioned, Rect.FromPosition(40, 50, new Size(416, 339)));
        Assert.False(launcher.HasBorder); // the launcher looked like the game at first

        var game = _desktop.OpenWindowed(process, new Size(1280, 720));

        AssertFrame(launcher, launcherOriginal);
        Assert.False(launcher.IsMarkedFullscreen);
        Assert.Equal(game.Handle, Assert.Single(controller.TrackedWindows).Window);
        Assert.Equal(Rect.FromPosition(320, 180, new Size(1280, 720)), game.Outer);
        AssertBackdroppedAndMarked(game, Primary);

        // The released launcher is no longer followed.
        var changes = _desktop.FrameChanges.Count;
        _desktop.ResizeClient(launcher, new Size(500, 400));
        Assert.Equal(changes, _desktop.FrameChanges.Count);
    }

    // --- The game's handle going to another window meanwhile ---

    [Fact]
    public void A_game_replaced_while_it_is_stripped_again_leaves_the_newcomer_alone()
    {
        var controller = StartController();
        var game = OpenGame();
        var newcomer = ReplaceDuringNext(PortCall.OuterSizeFor, game);

        _desktop.SetOwnFrame(game, FakeWindowSystem.Captioned, Rect.FromPosition(560, 240, new Size(1296, 759)));

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: false);
        Assert.Empty(controller.TrackedWindows);
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
    }

    [Fact]
    public void A_game_replaced_while_it_is_released_does_not_unmark_the_newcomer()
    {
        var controller = StartController();
        var game = OpenGame();
        var newcomer = ReplaceDuringNext(PortCall.DestroyBackdrop, game, markedFullscreen: true);

        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Primary.Bounds);

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: true);
        Assert.Empty(newcomer().PortChanges);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void A_fullscreen_game_replaced_while_it_is_restyled_for_windowed_leaves_the_newcomer_alone()
    {
        var controller = StartController();
        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);
        var newcomer = ReplaceDuringNext(PortCall.OuterSizeFor, game);

        _desktop.SetOwnFrame(game, FakeWindowSystem.Captioned, Rect.FromPosition(100, 100, new Size(1296, 759)));

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: false);
        Assert.Empty(newcomer().PortChanges);
        Assert.Empty(controller.TrackedWindows);
        Assert.Empty(_desktop.Backdrops);
    }

    private Func<FakeWindow> ReplaceDuringNext(PortCall call, FakeWindow game, bool markedFullscreen = false)
    {
        FakeWindow? newcomer = null;
        _desktop.DuringNext(call, () => newcomer = _desktop.ReplaceUnderItsHandle(
            game, _desktop.StartProcess(Notepad), FakeWindowSystem.Captioned, NewcomerRect, markedFullscreen));
        return () => newcomer ?? throw new InvalidOperationException($"No {call} ran.");
    }

    private static void AssertNewcomerKeptAsItWas(FakeWindow newcomer, bool markedFullscreen)
    {
        Assert.Equal(NewcomerRect, newcomer.Outer);
        Assert.Equal(FakeWindowSystem.Captioned, newcomer.Style);
        Assert.Equal(markedFullscreen, newcomer.IsMarkedFullscreen);
        Assert.Equal(0, newcomer.BackdropsShownBehind);
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
