using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>The hotkey's toggle: the backdrop off for the game in front (its window put back), and on again.</summary>
public sealed class ToggleTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    private static readonly MonitorInfo Primary = FakeWindowSystem.Primary;

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

    /// <summary>The frame a game opened by <see cref="OpenGame"/> at 100,100 has before the app touches it.</summary>
    private static WindowFrame OpenedAt(int left, int top) =>
        new(FakeWindowSystem.Captioned, Rect.FromPosition(left, top, new Size(800 + 16, 600 + 39)));

    private static readonly Rect Centred = new(560, 240, 1360, 840);

    private void AssertBackdropOff(FakeWindow game, WindowFrame original)
    {
        Assert.Equal(original.Style, game.Style);
        Assert.Equal(original.Outer, game.Outer);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        Assert.False(game.IsMarkedFullscreen);
        Assert.False(_desktop.TaskbarCovered(Primary));
    }

    private void AssertBackdropOn(FakeWindow game)
    {
        Assert.False(game.HasBorder);
        Assert.Equal(Centred, game.Outer);
        var backdrop = Assert.Single(_desktop.VisibleBackdropsOn(Primary));
        Assert.Equal(Primary.Bounds, backdrop.Bounds);
        Assert.Same(backdrop, _desktop.DirectlyBehind(game));
        Assert.True(game.IsMarkedFullscreen);
    }

    [Fact]
    public void Toggling_off_puts_the_game_window_back_and_hides_the_black()
    {
        var controller = StartController();
        var game = OpenGame();

        controller.ToggleBackdrop();

        AssertBackdropOff(game, OpenedAt(100, 100));
    }

    [Fact]
    public void Toggling_on_again_restyles_centres_and_backs_the_game_again()
    {
        var controller = StartController();
        var game = OpenGame();

        controller.ToggleBackdrop();
        controller.ToggleBackdrop();

        AssertBackdropOn(game);
        Assert.True(_desktop.TaskbarCovered(Primary));
    }

    [Fact]
    public void Toggling_with_no_tracked_game_changes_nothing()
    {
        var controller = StartController();
        var other = OpenOtherApp();
        var callsBefore = _desktop.Calls.Count;

        controller.ToggleBackdrop();

        Assert.Empty(other.PortChanges);
        Assert.DoesNotContain(_desktop.Calls.Skip(callsBefore), c => c.Call is PortCall.SetFrame or PortCall.CreateBackdrop or PortCall.MarkFullscreen);
    }

    [Fact]
    public void Toggling_a_game_in_its_own_fullscreen_mode_changes_nothing()
    {
        var controller = StartController();
        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);

        controller.ToggleBackdrop();

        Assert.Empty(game.PortChanges);
        Assert.Empty(_desktop.Backdrops);
    }

    [Fact]
    public void A_game_toggled_off_stays_off_through_focus_minimise_hide_and_moves()
    {
        var controller = StartController();
        var game = OpenGame();
        controller.ToggleBackdrop();

        var other = OpenOtherApp();
        _desktop.UserActivates(game);
        _desktop.Minimise(game);
        _desktop.Restore(game);
        _desktop.Hide(game);
        _desktop.Show(game);
        _desktop.Move(game, Rect.FromPosition(2000, 0, game.Outer.Size));
        _desktop.UserActivates(other);
        _desktop.UserActivates(game);

        Assert.True(game.Style.HasCaption);
        Assert.Equal(Rect.FromPosition(2000, 0, OpenedAt(0, 0).Outer.Size), game.Outer);
        Assert.DoesNotContain(_desktop.Backdrops.Values, b => b.IsVisible);
        Assert.False(game.IsMarkedFullscreen);
    }

    [Fact]
    public void A_game_toggled_off_gets_its_backdrop_again_when_relaunched()
    {
        var controller = StartController();
        var first = OpenGame();
        controller.ToggleBackdrop();

        _desktop.Close(first);
        var relaunched = OpenGame();

        AssertBackdropOn(relaunched);
    }

    [Fact]
    public void A_game_toggled_off_on_its_splash_stays_off_when_its_main_window_replaces_it()
    {
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        var splash = _desktop.OpenPopup(process, Rect.FromPosition(800, 400, new Size(320, 200)));
        Assert.Single(_desktop.VisibleBackdropsOn(Primary));
        controller.ToggleBackdrop();

        var main = _desktop.OpenWindowed(process, new Size(800, 600));

        Assert.True(main.Style.HasCaption);
        Assert.Empty(main.PortChanges);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));

        controller.ToggleBackdrop();

        AssertBackdropOn(main);
    }

    [Fact]
    public void A_game_toggled_off_on_its_splash_stays_off_when_the_splash_closes_before_its_main_window_opens()
    {
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        var splash = _desktop.OpenPopup(process, Rect.FromPosition(800, 400, new Size(320, 200)));
        controller.ToggleBackdrop();

        _desktop.Close(splash);
        var main = _desktop.OpenWindowed(process, new Size(800, 600));

        Assert.True(main.Style.HasCaption);
        Assert.Empty(main.PortChanges);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));

        controller.ToggleBackdrop();

        AssertBackdropOn(main);
    }

    [Fact]
    public void A_game_toggled_off_stays_off_when_it_closes_its_window_and_opens_a_new_one()
    {
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        var first = _desktop.OpenWindowed(process, new Size(800, 600));
        controller.ToggleBackdrop();

        // A game that recreates its window (a renderer or resolution change) is still the same run.
        _desktop.Close(first);
        var second = _desktop.OpenWindowed(process, new Size(800, 600));

        Assert.Empty(second.PortChanges);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_game_toggled_off_gets_its_backdrop_again_when_relaunched_under_the_same_process_id()
    {
        var controller = StartController();
        var first = OpenGame();
        controller.ToggleBackdrop();

        _desktop.Close(first);
        var relaunched = _desktop.OpenWindowed(_desktop.StartProcess(Dota, reusingIdOf: first.Process), new Size(800, 600));

        Assert.Equal(first.Process.Id, relaunched.Process.Id);
        AssertBackdropOn(relaunched);
    }

    [Fact]
    public void The_toggle_acts_on_the_game_in_front_not_on_another_running_game()
    {
        var controller = StartController();
        var dota = OpenGame(Dota);
        var novel = OpenGame(Novel, left: 2000, top: 0);

        controller.ToggleBackdrop();

        Assert.True(novel.Style.HasCaption);
        Assert.False(dota.HasBorder);
        Assert.True(dota.IsMarkedFullscreen);
    }

    [Fact]
    public void With_no_game_in_front_the_toggle_acts_on_the_game_most_recently_in_front()
    {
        var controller = StartController();
        var dota = OpenGame(Dota);
        var novel = OpenGame(Novel, left: 2000, top: 0);
        _desktop.UserActivates(dota);
        OpenOtherApp();

        controller.ToggleBackdrop();

        AssertBackdropOff(dota, OpenedAt(100, 100));
        Assert.False(novel.HasBorder);
    }

    [Fact]
    public void A_game_already_running_and_in_front_when_the_app_starts_can_be_toggled()
    {
        OpenGame(Novel, left: 2000, top: 0);
        var dota = OpenGame(Dota);
        var controller = StartController();

        controller.ToggleBackdrop();

        AssertBackdropOff(dota, OpenedAt(100, 100));
    }

    [Fact]
    public void The_game_in_front_when_the_app_starts_is_toggled_after_the_user_switches_to_another_app()
    {
        var dota = OpenGame(Dota);
        var controller = StartController();
        OpenOtherApp();

        controller.ToggleBackdrop();

        AssertBackdropOff(dota, OpenedAt(100, 100));
    }

    [Fact]
    public void Closing_the_game_most_recently_in_front_hands_the_toggle_to_the_one_in_front_before_it()
    {
        var controller = StartController();
        var dota = OpenGame(Dota);
        var novel = OpenGame(Novel, left: 2000, top: 0);
        _desktop.UserActivates(dota);
        _desktop.UserActivates(novel);
        OpenOtherApp();
        _desktop.Close(novel);

        controller.ToggleBackdrop();

        AssertBackdropOff(dota, OpenedAt(100, 100));
    }

    [Fact]
    public void A_main_window_that_replaced_the_splash_in_front_keeps_its_place_in_the_toggle_s_history()
    {
        var controller = StartController();
        var process = _desktop.StartProcess(Dota);
        _desktop.OpenPopup(process, Rect.FromPosition(800, 400, new Size(320, 200)));
        // The main window comes up without taking the foreground, so it never gets a Foreground event.
        var main = _desktop.OpenWindowed(process, new Size(800, 600), show: false);
        _desktop.ShowWithoutActivating(main);
        AssertBackdropOn(main);
        OpenOtherApp();

        controller.ToggleBackdrop();

        AssertBackdropOff(main, OpenedAt(100, 100));
    }

    [Fact]
    public void Toggling_on_takes_the_window_as_the_user_left_it_and_quitting_puts_that_back()
    {
        var controller = StartController();
        var game = OpenGame();
        controller.ToggleBackdrop();
        var moved = Rect.FromPosition(300, 200, game.Outer.Size);
        _desktop.Move(game, moved);

        controller.ToggleBackdrop();
        AssertBackdropOn(game);
        controller.Stop();

        AssertBackdropOff(game, new WindowFrame(FakeWindowSystem.Captioned, moved));
    }

    [Fact]
    public void Quitting_with_the_game_toggled_off_leaves_it_as_it_is()
    {
        var controller = StartController();
        var game = OpenGame();
        controller.ToggleBackdrop();
        var changes = game.PortChanges.Count;

        Assert.Empty(controller.Stop());

        Assert.Equal(changes, game.PortChanges.Count);
        AssertBackdropOff(game, OpenedAt(100, 100));
    }

    [Fact]
    public void A_toggle_off_whose_restore_fails_keeps_the_black_and_the_next_toggle_tries_again()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.FailNextChanges(game, ChangeResult.Failed);

        controller.ToggleBackdrop();
        Assert.False(game.HasBorder);
        Assert.Single(_desktop.VisibleBackdropsOn(Primary));

        controller.ToggleBackdrop();
        AssertBackdropOff(game, OpenedAt(100, 100));
    }

    // Review 3 F3: a toggle-off that puts the frame back but can't unmark leaves the record
    // restyled with its frame restored. Nothing may follow that window as a restyled game.
    [Fact]
    public void A_toggle_off_whose_unmark_fails_leaves_the_game_alone_when_it_moves()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.FailNextMarks(game);
        controller.ToggleBackdrop();

        _desktop.Move(game, Rect.FromPosition(200, 150, OpenedAt(100, 100).Outer.Size));

        Assert.True(game.HasBorder);
        Assert.Equal(Rect.FromPosition(200, 150, OpenedAt(100, 100).Outer.Size), game.Outer);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_toggle_off_whose_unmark_fails_leaves_the_game_alone_on_the_restores_own_event()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.FailNextMarks(game);
        controller.ToggleBackdrop();

        _desktop.Raise(WindowEventKind.LocationChanged, game);

        Assert.True(game.HasBorder);
        Assert.Equal(OpenedAt(100, 100).Outer, game.Outer);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_toggle_off_whose_unmark_fails_leaves_the_game_alone_when_it_resizes_or_goes_fullscreen()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.FailNextMarks(game);
        controller.ToggleBackdrop();

        _desktop.ResizeClient(game, new Size(1024, 768));
        Assert.True(game.HasBorder);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));

        _desktop.SetOwnFrame(game, FakeWindowSystem.Popup, Primary.Bounds);
        Assert.Equal(FakeWindowSystem.Popup, game.Style);
        Assert.Equal(Primary.Bounds, game.Outer);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_toggle_off_whose_unmark_fails_is_finished_by_the_next_press_and_the_one_after_turns_it_on()
    {
        var controller = StartController();
        var game = OpenGame();
        _desktop.FailNextMarks(game);
        controller.ToggleBackdrop();
        Assert.True(game.IsMarkedFullscreen);
        _desktop.Move(game, Rect.FromPosition(200, 150, OpenedAt(100, 100).Outer.Size));

        controller.ToggleBackdrop();
        AssertBackdropOff(game, OpenedAt(200, 150));

        controller.ToggleBackdrop();
        AssertBackdropOn(game);
    }

    [Fact]
    public void A_game_replaced_under_its_handle_before_the_toggle_leaves_the_newcomer_alone()
    {
        var controller = StartController();
        var game = OpenGame();
        var newcomer = _desktop.ReplaceUnderItsHandle(game, _desktop.StartProcess(Notepad), FakeWindowSystem.Captioned,
            Rect.FromPosition(50, 50, new Size(640, 480)));

        controller.ToggleBackdrop();

        Assert.Empty(newcomer.PortChanges);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_toggled_off_game_replaced_under_its_handle_is_not_toggled_on_onto_the_newcomer()
    {
        var controller = StartController();
        var game = OpenGame();
        controller.ToggleBackdrop();
        var newcomer = _desktop.ReplaceUnderItsHandle(game, _desktop.StartProcess(Dota), FakeWindowSystem.Captioned,
            Rect.FromPosition(50, 50, new Size(640, 480)));

        controller.ToggleBackdrop();

        Assert.Empty(newcomer.PortChanges);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }
}
