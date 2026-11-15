using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>The black backdrop behind a restyled game, and the taskbar on its monitor.</summary>
public sealed class BackdropTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private static readonly MonitorInfo Primary = FakeWindowSystem.Primary;
    private static readonly MonitorInfo Secondary = FakeWindowSystem.Secondary;

    private readonly FakeWindowSystem _desktop = new();
    private readonly List<GameEntry> _games =
    [
        new() { ExePath = Dota, DisplayName = "Dota 2" },
        new() { ExePath = Novel, DisplayName = "Novel" },
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

    /// <summary>The one visible backdrop on the monitor, covering all of it.</summary>
    private FakeBackdrop BackdropOver(MonitorInfo monitor)
    {
        var backdrop = Assert.Single(_desktop.VisibleBackdropsOn(monitor));
        Assert.Equal(monitor.Bounds, backdrop.Bounds);
        return backdrop;
    }

    private void AssertNoBackdropAndNotMarked(FakeWindow window)
    {
        Assert.Empty(_desktop.Backdrops);
        Assert.False(window.IsMarkedFullscreen);
    }

    [Fact]
    public void A_windowed_game_gets_a_black_backdrop_over_its_whole_monitor_taskbar_included_directly_behind_it()
    {
        StartController();

        var game = OpenGame();

        var backdrop = BackdropOver(Primary);
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
        Assert.Empty(_desktop.VisibleBackdropsOn(Secondary));
    }

    [Fact]
    public void A_windowed_game_on_the_second_monitor_gets_its_backdrop_there_only()
    {
        StartController();

        var game = OpenGame(Novel, left: 2000, top: 0);

        var backdrop = BackdropOver(Secondary);
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_borderless_fullscreen_game_gets_no_backdrop_and_is_not_marked()
    {
        StartController();

        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);

        AssertNoBackdropAndNotMarked(game);
    }

    [Fact]
    public void An_unlisted_program_gets_no_backdrop_and_is_not_marked()
    {
        StartController();

        AssertNoBackdropAndNotMarked(OpenOtherApp());
    }

    [Fact]
    public void A_disabled_game_gets_no_backdrop_and_is_not_marked()
    {
        _games[0].Enabled = false;
        StartController();

        AssertNoBackdropAndNotMarked(OpenGame());
    }

    [Fact]
    public void A_game_the_app_may_not_touch_gets_no_backdrop_and_is_not_marked()
    {
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota, elevated: true), new Size(800, 600));

        AssertNoBackdropAndNotMarked(game);
    }

    [Fact]
    public void Another_app_coming_to_the_foreground_is_in_front_of_the_game_and_its_backdrop()
    {
        StartController();
        var game = OpenGame();
        var backdrop = BackdropOver(Primary);

        var other = OpenOtherApp();

        Assert.Equal([other, game, backdrop], _desktop.ZOrder(game, backdrop, other));
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
        Assert.Same(other, _desktop.Foreground);
    }

    [Fact]
    public void The_game_brought_back_comes_in_front_with_its_backdrop_directly_behind_it()
    {
        StartController();
        var game = OpenGame();
        var backdrop = BackdropOver(Primary);
        var other = OpenOtherApp();

        _desktop.UserActivates(game);

        Assert.Equal([game, backdrop, other], _desktop.ZOrder(game, backdrop, other));
        Assert.Same(game, _desktop.Foreground);
    }

    [Fact]
    public void Minimising_the_game_hides_its_backdrop_and_restoring_it_shows_the_backdrop_behind_it()
    {
        StartController();
        var game = OpenGame();
        var other = OpenOtherApp();
        _desktop.UserActivates(game);

        _desktop.Minimise(game);

        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        Assert.Same(other, _desktop.Foreground);

        _desktop.Restore(game);

        var backdrop = BackdropOver(Primary);
        Assert.Equal([game, backdrop, other], _desktop.ZOrder(game, backdrop, other));
    }

    [Fact]
    public void A_game_restored_without_coming_to_the_front_gets_its_backdrop_back_directly_behind_it()
    {
        StartController();
        var game = OpenGame();
        var other = OpenOtherApp();
        _desktop.Minimise(game);

        _desktop.Restore(game, activate: false);

        var backdrop = BackdropOver(Primary);
        Assert.Equal([other, game, backdrop], _desktop.ZOrder(game, backdrop, other));
        Assert.Same(other, _desktop.Foreground);
    }

    [Fact]
    public void The_game_hiding_its_window_hides_its_backdrop_and_showing_it_shows_the_backdrop_behind_it()
    {
        StartController();
        var game = OpenGame();
        var other = OpenOtherApp();
        _desktop.UserActivates(game);

        _desktop.Hide(game);

        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));

        _desktop.Show(game);

        var backdrop = BackdropOver(Primary);
        Assert.Equal([game, backdrop, other], _desktop.ZOrder(game, backdrop, other));
    }

    [Fact]
    public void A_game_hidden_and_shown_again_covers_the_taskbar_again_while_it_is_in_front()
    {
        var controller = StartController();
        var game = OpenGame();

        _desktop.Hide(game);
        _desktop.Show(game);

        Assert.True(_desktop.TaskbarCovered(Primary));
        OpenOtherApp();
        Assert.False(_desktop.TaskbarCovered(Primary));
        _desktop.UserActivates(game);
        Assert.True(_desktop.TaskbarCovered(Primary));

        controller.Stop();
        Assert.False(game.IsMarkedFullscreen);
    }

    [Fact]
    public void A_game_that_hides_and_shows_its_window_while_keeping_the_foreground_covers_the_taskbar_again()
    {
        StartController();
        var game = OpenGame();

        _desktop.HideKeepingForeground(game);
        _desktop.ShowStillForeground(game);

        Assert.True(_desktop.TaskbarCovered(Primary));
        Assert.Same(BackdropOver(Primary), _desktop.DirectlyBehind(game));
    }

    [Fact]
    public void A_failed_mark_after_the_game_is_shown_again_leaves_its_backdrop_and_the_next_activation_marks_it()
    {
        var controller = StartController();
        var game = OpenGame();
        var other = OpenOtherApp();
        _desktop.UserActivates(game);
        _desktop.Hide(game);
        _desktop.FailNextMarks(game, 2); // the marks on Shown and on Foreground

        _desktop.Show(game);

        Assert.False(_desktop.TaskbarCovered(Primary));
        var backdrop = BackdropOver(Primary);
        Assert.Equal([game, backdrop, other], _desktop.ZOrder(game, backdrop, other));

        _desktop.UserActivates(other);
        _desktop.UserActivates(game);
        Assert.True(_desktop.TaskbarCovered(Primary));

        Assert.Empty(controller.Stop());
        Assert.False(game.IsMarkedFullscreen);
    }

    [Fact]
    public void A_click_on_the_backdrop_brings_the_game_back_with_the_backdrop_directly_behind_it()
    {
        StartController();
        var game = OpenGame();
        var backdrop = BackdropOver(Primary);
        var other = OpenOtherApp();

        _desktop.ClickBackdrop(backdrop.Handle);

        Assert.Same(game, _desktop.Foreground);
        Assert.Equal([game, backdrop, other], _desktop.ZOrder(game, backdrop, other));
    }

    [Fact]
    public void Moving_the_game_to_the_other_monitor_moves_the_backdrop_there_and_centres_the_game_on_it()
    {
        StartController();
        var game = OpenGame();

        _desktop.Move(game, Rect.FromPosition(2500, 100, new Size(800, 600)));

        Assert.Equal(Rect.FromPosition(1920 + 880, -200 + 420, new Size(800, 600)), game.Outer);
        Assert.False(game.HasBorder);
        var backdrop = BackdropOver(Secondary);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
        Assert.True(_desktop.TaskbarCovered(Secondary));
    }

    [Fact]
    public void Moving_the_game_within_its_monitor_changes_nothing()
    {
        StartController();
        var game = OpenGame();
        var changes = _desktop.FrameChanges.Count;
        var moved = Rect.FromPosition(100, 100, new Size(800, 600));

        _desktop.Move(game, moved);

        Assert.Equal(moved, game.Outer);
        Assert.Equal(changes, _desktop.FrameChanges.Count);
        Assert.Same(BackdropOver(Primary), _desktop.DirectlyBehind(game));
    }

    [Fact]
    public void The_game_exiting_removes_its_backdrop()
    {
        StartController();
        var game = OpenGame();
        var backdrop = BackdropOver(Primary);

        _desktop.Close(game);

        Assert.True(backdrop.IsDestroyed);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void Quitting_removes_every_backdrop_puts_the_game_back_unmarked_and_leaves_a_fullscreen_game_alone()
    {
        var controller = StartController();
        var game = OpenGame(left: 50, top: 60);
        var fullscreen = _desktop.OpenPopup(_desktop.StartProcess(Novel), Secondary.Bounds);
        var changes = _desktop.FrameChanges.Count;

        Assert.Empty(controller.Stop());

        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
        Assert.True(game.HasBorder);
        Assert.Equal(Rect.FromPosition(50, 60, new Size(816, 639)), game.Outer);
        Assert.False(game.IsMarkedFullscreen);
        Assert.Equal(changes + 1, _desktop.FrameChanges.Count); // the game's restore only
        Assert.Equal(Secondary.Bounds, fullscreen.Outer);
        Assert.False(fullscreen.IsMarkedFullscreen);
    }

    [Fact]
    public void The_taskbar_on_the_game_s_monitor_is_covered_only_while_the_game_is_in_front_and_not_after_quitting()
    {
        var controller = StartController();
        var game = OpenGame();

        Assert.True(_desktop.TaskbarCovered(Primary));
        Assert.False(_desktop.TaskbarCovered(Secondary));

        OpenOtherApp();
        Assert.False(_desktop.TaskbarCovered(Primary));

        _desktop.UserActivates(game);
        Assert.True(_desktop.TaskbarCovered(Primary));

        controller.Stop();
        Assert.Same(game, _desktop.Foreground);
        Assert.False(_desktop.TaskbarCovered(Primary));
    }

    [Fact]
    public void A_splash_replaced_by_the_game_window_loses_its_backdrop_and_mark_and_the_game_window_gets_them()
    {
        StartController();
        var process = _desktop.StartProcess(Novel);
        var splash = _desktop.OpenPopup(process, new Rect(100, 100, 500, 400));
        var splashBackdrop = BackdropOver(Primary);
        Assert.True(splash.IsMarkedFullscreen);

        var game = _desktop.OpenWindowed(process, new Size(1280, 720));

        Assert.True(splashBackdrop.IsDestroyed);
        Assert.False(splash.IsMarkedFullscreen);
        Assert.True(game.IsMarkedFullscreen);
        Assert.Same(BackdropOver(Primary), _desktop.DirectlyBehind(game));
    }

    [Fact]
    public void A_game_window_that_goes_fullscreen_while_its_splash_is_put_back_is_left_to_its_own_mode()
    {
        StartController();
        var process = _desktop.StartProcess(Novel);
        _desktop.OpenPopup(process, new Rect(100, 100, 500, 400));
        var game = _desktop.OpenWindowed(process, new Size(1280, 720), show: false);
        // While the splash is being put back, the game switches itself to borderless fullscreen.
        _desktop.DuringNext(PortCall.SetFrame, () =>
        {
            game.Style = FakeWindowSystem.Popup;
            game.Outer = Primary.Bounds;
        });

        _desktop.Show(game);

        Assert.Equal(Primary.Bounds, game.Outer);
        Assert.Equal(FakeWindowSystem.Popup, game.Style);
        Assert.False(game.IsMarkedFullscreen);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_game_already_running_when_the_app_starts_gets_a_backdrop()
    {
        var game = OpenGame();
        var other = OpenOtherApp();

        StartController();

        var backdrop = BackdropOver(Primary);
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
        Assert.True(game.IsMarkedFullscreen);
        Assert.Equal([other, game, backdrop], _desktop.ZOrder(game, backdrop, other));
    }

    [Fact]
    public void A_restore_that_fails_at_quit_still_removes_the_backdrop_and_the_next_quit_puts_the_game_back()
    {
        var controller = StartController();
        var game = OpenGame(left: 50, top: 60);
        var backdrop = BackdropOver(Primary);
        _desktop.FailNextChanges(game, ChangeResult.Failed);

        Assert.Single(controller.Stop());

        Assert.True(backdrop.IsDestroyed);
        Assert.False(game.HasBorder);

        Assert.Empty(controller.Stop());
        Assert.True(game.HasBorder);
        Assert.Equal(Rect.FromPosition(50, 60, new Size(816, 639)), game.Outer);
        Assert.False(game.IsMarkedFullscreen);
    }

    [Fact]
    public void A_failed_unmark_at_quit_is_retried_without_changing_the_frame_again()
    {
        var controller = StartController();
        var game = OpenGame(left: 50, top: 60);
        _desktop.FailNextMarks(game);

        Assert.Single(controller.Stop());
        Assert.True(game.HasBorder);
        Assert.True(game.IsMarkedFullscreen);
        var changes = _desktop.FrameChanges.Count;

        Assert.Empty(controller.Stop());
        Assert.False(game.IsMarkedFullscreen);
        Assert.Equal(changes, _desktop.FrameChanges.Count);
        Assert.Empty(controller.TrackedWindows);
    }

    public static TheoryData<WindowEventKind, bool, bool> StaleEvents()
    {
        var data = new TheoryData<WindowEventKind, bool, bool>();
        foreach (var kind in new[] { WindowEventKind.Foreground, WindowEventKind.LocationChanged, WindowEventKind.MinimizeStart,
                     WindowEventKind.MinimizeEnd, WindowEventKind.Hidden, WindowEventKind.Shown })
        foreach (var stale in new[] { true, false })
        foreach (var sameGame in new[] { true, false })
            data.Add(kind, stale, sameGame);
        return data;
    }

    /// <summary>
    /// The tracked game is destroyed with its Destroyed event still pending, and a new window has
    /// its handle: another window of the same game that isn't a game window (a tool window), or
    /// another program's. It sits on the other monitor, so a re-centre would move it.
    /// </summary>
    private (BackdropController Controller, FakeBackdrop OldBackdrop, FakeWindow Other, FakeWindow Newcomer, Rect NewcomerRect)
        GameReplacedUnderItsHandle(bool sameGame)
    {
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        var game = _desktop.OpenWindowed(process, new Size(800, 600));
        var oldBackdrop = BackdropOver(Primary);
        var other = OpenOtherApp();
        _desktop.CloseWithEventPending(game);
        var rect = Rect.FromPosition(2500, 100, new Size(816, 639));
        var newcomer = sameGame
            ? _desktop.OpenWithReusedHandle(game, process, FakeWindowSystem.Captioned with { ExStyle = WindowStyle.WS_EX_TOOLWINDOW }, rect, show: false)
            : _desktop.OpenWithReusedHandle(game, _desktop.StartProcess(Notepad), FakeWindowSystem.Captioned, rect, show: false);
        newcomer.IsVisible = true;
        return (controller, oldBackdrop, other, newcomer, rect);
    }

    private void AssertNewcomerUntouched(BackdropController controller, FakeBackdrop oldBackdrop, FakeWindow other, FakeWindow newcomer, Rect rect, WindowStyle style)
    {
        Assert.True(oldBackdrop.IsDestroyed);
        Assert.All(_desktop.Backdrops.Values, b => Assert.False(b.IsVisible));
        Assert.Equal(rect, newcomer.Outer);
        Assert.Equal(style, newcomer.Style);
        Assert.False(newcomer.IsMarkedFullscreen);
        Assert.Same(other, _desktop.Foreground);
        Assert.Empty(controller.TrackedWindows);
    }

    [Theory]
    [MemberData(nameof(StaleEvents))]
    public void An_event_for_a_destroyed_game_s_handle_does_not_touch_the_new_window_that_has_it(WindowEventKind kind, bool stale, bool sameGame)
    {
        var (controller, oldBackdrop, other, newcomer, rect) = GameReplacedUnderItsHandle(sameGame);
        var style = newcomer.Style;

        if (stale) _desktop.RaiseStale(kind, newcomer);
        else _desktop.Raise(kind, newcomer);

        AssertNewcomerUntouched(controller, oldBackdrop, other, newcomer, rect, style);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_click_on_a_destroyed_game_s_backdrop_does_not_activate_the_new_window_that_has_its_handle(bool sameGame)
    {
        var (controller, oldBackdrop, other, newcomer, rect) = GameReplacedUnderItsHandle(sameGame);
        var style = newcomer.Style;

        _desktop.ClickBackdrop(oldBackdrop.Handle);

        AssertNewcomerUntouched(controller, oldBackdrop, other, newcomer, rect, style);
    }
    [Fact]
    public void A_backdrop_that_cannot_be_created_still_lets_quitting_put_the_restyled_game_back()
    {
        var controller = StartController();
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), 50, 60, show: false);
        _desktop.ThrowOnNext(PortCall.CreateBackdrop);

        // The exception reaches the app's crash handler, which then quits.
        Assert.Throws<InvalidOperationException>(() => _desktop.Show(game));
        Assert.False(game.HasBorder);

        Assert.Empty(controller.Stop());
        Assert.True(game.HasBorder);
        Assert.Equal(Rect.FromPosition(50, 60, new Size(816, 639)), game.Outer);
        Assert.False(game.IsMarkedFullscreen);
        Assert.Empty(_desktop.Backdrops);
        Assert.Empty(controller.TrackedWindows);
    }

    [Theory]
    [InlineData(PortCall.ShowBackdrop)]
    [InlineData(PortCall.MarkFullscreen)]
    public void A_setup_step_that_throws_after_the_backdrop_was_made_leaves_no_backdrop_after_quitting(PortCall failing)
    {
        var controller = StartController();
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), 50, 60, show: false);
        _desktop.ThrowOnNext(failing);

        Assert.Throws<InvalidOperationException>(() => _desktop.Show(game));
        Assert.Single(_desktop.Backdrops);

        Assert.Empty(controller.Stop());
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
        Assert.True(game.HasBorder);
        Assert.Equal(Rect.FromPosition(50, 60, new Size(816, 639)), game.Outer);
        Assert.False(game.IsMarkedFullscreen);
    }
    // The game closes while a port call on it is running, and another program's window gets its
    // handle before the call returns. Every later call must see that the handle is no longer the
    // game's, and leave the newcomer as it is.
    private static readonly Rect NewcomerRect = Rect.FromPosition(300, 200, new Size(816, 639));

    private Func<FakeWindow> ReplaceDuringNext(PortCall call, FakeWindow game, bool markedFullscreen = false)
    {
        FakeWindow? newcomer = null;
        _desktop.DuringNext(call, () => newcomer = _desktop.ReplaceUnderItsHandle(
            game, _desktop.StartProcess(Notepad), FakeWindowSystem.Captioned, NewcomerRect, markedFullscreen));
        return () => newcomer ?? throw new InvalidOperationException($"No {call} ran.");
    }

    private void AssertNewcomerKeptAsItWas(FakeWindow newcomer, bool markedFullscreen)
    {
        Assert.Equal(NewcomerRect, newcomer.Outer);
        Assert.Equal(FakeWindowSystem.Captioned, newcomer.Style);
        Assert.Equal(markedFullscreen, newcomer.IsMarkedFullscreen);
        Assert.Equal(0, newcomer.BackdropsShownBehind); // not even for a moment
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_game_replaced_while_quitting_puts_it_back_leaves_the_newcomer_s_own_fullscreen_mark(bool callSaysGone)
    {
        var controller = StartController();
        var game = OpenGame();
        if (callSaysGone) _desktop.FailNextChanges(game, ChangeResult.WindowGone);
        var newcomer = ReplaceDuringNext(PortCall.SetFrame, game, markedFullscreen: true);

        Assert.Empty(controller.Stop());

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: true);
        Assert.Empty(controller.TrackedWindows);
    }

    [Theory]
    [InlineData(PortCall.CreateBackdrop)]
    [InlineData(PortCall.ShowBackdrop)]
    public void A_game_replaced_while_its_backdrop_is_made_gives_the_newcomer_no_backdrop_or_mark_and_quitting_leaves_nothing(PortCall during)
    {
        var controller = StartController();
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), show: false);
        var newcomer = ReplaceDuringNext(during, game);

        _desktop.Show(game);

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: false);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));

        Assert.Empty(controller.Stop());
        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: false);
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
        Assert.Empty(controller.TrackedWindows);
    }

    [Theory]
    [InlineData(1)] // the restyle
    [InlineData(2)] // the client-size correction: a menu bar on two rows is taller than the port predicts
    public void A_game_replaced_while_it_is_restyled_gives_the_newcomer_no_backdrop_or_mark(int setFrameCall)
    {
        var controller = StartController();
        var game = _desktop.Open(_desktop.StartProcess(Dota), FakeWindowSystem.Captioned, Rect.FromPosition(100, 100, new Size(816, 679)),
            show: false, menu: true, menuRows: 2);
        Func<FakeWindow> newcomer = () => throw new InvalidOperationException("No replacement yet.");
        if (setFrameCall == 1) newcomer = ReplaceDuringNext(PortCall.SetFrame, game);
        else _desktop.DuringNext(PortCall.SetFrame, () => newcomer = ReplaceDuringNext(PortCall.SetFrame, game));

        _desktop.Show(game);

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: false);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        Assert.Empty(controller.Stop());
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void A_game_replaced_while_it_is_moved_to_the_other_monitor_gets_no_backdrop_behind_the_newcomer()
    {
        var controller = StartController();
        var game = OpenGame();
        var backdrop = BackdropOver(Primary);
        _desktop.FailNextChanges(game, ChangeResult.WindowGone);
        var newcomer = ReplaceDuringNext(PortCall.SetFrame, game);

        _desktop.Move(game, Rect.FromPosition(2500, 100, new Size(800, 600)));

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: false);
        Assert.True(backdrop.IsDestroyed);
        Assert.Empty(controller.TrackedWindows);
    }

    [Fact]
    public void A_game_replaced_while_its_backdrop_is_ordered_on_coming_to_the_foreground_does_not_mark_the_newcomer()
    {
        var controller = StartController();
        var game = OpenGame();
        var backdrop = BackdropOver(Primary);
        OpenOtherApp();
        var newcomer = ReplaceDuringNext(PortCall.ShowBackdrop, game);

        _desktop.UserActivates(game);

        AssertNewcomerKeptAsItWas(newcomer(), markedFullscreen: false);
        Assert.True(backdrop.IsDestroyed);
        Assert.Empty(controller.TrackedWindows);
    }
}
