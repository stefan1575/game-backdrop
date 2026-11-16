using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>The running controller picking up changes made in the game list window.</summary>
public sealed class ListChangeTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";

    private static readonly MonitorInfo Primary = FakeWindowSystem.Primary;
    private static readonly WindowFrame DotaOriginal =
        new(FakeWindowSystem.Captioned, Rect.FromPosition(50, 60, new Size(816, 639)));

    private readonly FakeWindowSystem _desktop = new();
    private BackdropController _controller = null!;

    private static GameEntry Entry(string exe, string name, bool enabled = true) =>
        new() { ExePath = exe, DisplayName = name, Enabled = enabled };

    private void StartController(params GameEntry[] games)
    {
        _controller = new BackdropController(_desktop, new GameList(games));
        _controller.Start();
    }

    private void ListChangesTo(params GameEntry[] games) => _controller.UpdateGames(new GameList(games));

    private FakeWindow OpenDota() => _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);

    private void AssertPutBack(FakeWindow game, WindowFrame original)
    {
        Assert.Equal(original.Style, game.Style);
        Assert.Equal(original.Outer, game.Outer);
        Assert.False(game.IsMarkedFullscreen);
        Assert.All(_desktop.Backdrops.Values, b => Assert.True(b.IsDestroyed));
    }

    [Fact]
    public void Switching_off_a_backdropped_game_puts_it_back_and_removes_its_backdrop()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = OpenDota();
        Assert.False(game.HasBorder);

        ListChangesTo(Entry(Dota, "Dota 2", enabled: false));

        AssertPutBack(game, DotaOriginal);
        Assert.Empty(_controller.TrackedWindows);
    }

    [Fact]
    public void Removing_a_backdropped_game_puts_it_back_and_removes_its_backdrop()
    {
        StartController(Entry(Dota, "Dota 2"), Entry(Novel, "Novel"));
        var game = OpenDota();

        ListChangesTo(Entry(Novel, "Novel"));

        AssertPutBack(game, DotaOriginal);
        Assert.False(_desktop.TaskbarCovered(Primary));
    }

    [Fact]
    public void Switching_off_one_game_leaves_another_game_s_backdrop_alone()
    {
        StartController(Entry(Dota, "Dota 2"), Entry(Novel, "Novel"));
        var dota = OpenDota();
        var novel = _desktop.OpenWindowed(_desktop.StartProcess(Novel), new Size(1280, 720), left: 2000, top: 10);

        ListChangesTo(Entry(Dota, "Dota 2", enabled: false), Entry(Novel, "Novel"));

        Assert.True(dota.HasBorder);
        Assert.False(novel.HasBorder);
        var backdrop = Assert.Single(_desktop.VisibleBackdropsOn(FakeWindowSystem.Secondary));
        Assert.Same(backdrop, _desktop.DirectlyBehind(novel));
    }

    [Fact]
    public void A_switched_off_game_shown_again_is_left_alone()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = OpenDota();
        ListChangesTo(Entry(Dota, "Dota 2", enabled: false));

        _desktop.Minimise(game);
        _desktop.Restore(game);
        _desktop.Hide(game);
        _desktop.Show(game);

        Assert.True(game.HasBorder);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_game_switched_back_on_while_it_runs_gets_its_backdrop_without_a_restart()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = OpenDota();
        ListChangesTo(Entry(Dota, "Dota 2", enabled: false));

        ListChangesTo(Entry(Dota, "Dota 2"));

        Assert.False(game.HasBorder);
        Assert.Same(Assert.Single(_desktop.VisibleBackdropsOn(Primary)), _desktop.DirectlyBehind(game));
    }

    /// <summary>A Dota process with a frameless splash in front (listed first) and its captioned main window.</summary>
    private (FakeWindow Splash, FakeWindow Main) OpenDotaWithSplash()
    {
        var process = _desktop.StartProcess(Dota);
        var splash = _desktop.OpenPopup(process, new Rect(100, 100, 500, 400));
        var main = _desktop.OpenWindowed(process, new Size(800, 600), left: 50, top: 60);
        return (splash, main);
    }

    private void AssertMainWindowChosen(FakeWindow splash, FakeWindow main)
    {
        Assert.Equal(main.Handle, Assert.Single(_controller.TrackedWindows).Window);
        Assert.False(main.HasBorder);
        Assert.Same(Assert.Single(_desktop.VisibleBackdropsOn(Primary)), _desktop.DirectlyBehind(main));
        Assert.Equal(new WindowFrame(FakeWindowSystem.Popup, new Rect(100, 100, 500, 400)), new WindowFrame(splash.Style, splash.Outer));
    }

    [Fact]
    public void A_game_added_while_running_gets_its_main_window_chosen_as_at_start()
    {
        StartController();
        var (splash, main) = OpenDotaWithSplash();

        ListChangesTo(Entry(Dota, "Dota 2"));

        AssertMainWindowChosen(splash, main);
    }

    [Fact]
    public void A_game_switched_back_on_gets_its_main_window_chosen_as_at_start()
    {
        StartController(Entry(Dota, "Dota 2", enabled: false));
        var (splash, main) = OpenDotaWithSplash();

        ListChangesTo(Entry(Dota, "Dota 2"));

        AssertMainWindowChosen(splash, main);
    }

    [Fact]
    public void A_game_kept_on_through_a_change_keeps_its_window_over_another_it_has_open()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = OpenDota();
        // A second, larger captioned window the controller never saw shown (a missed event).
        var other = _desktop.OpenWindowed(game.Process, new Size(1024, 768), left: 300, top: 200, show: false);
        other.IsVisible = true;

        ListChangesTo(Entry(Dota, "Dota 2 Reborn"));

        Assert.Equal(game.Handle, Assert.Single(_controller.TrackedWindows).Window);
        Assert.False(game.HasBorder);
        Assert.True(other.HasBorder);
    }

    [Fact]
    public void A_running_game_added_to_the_list_gets_its_backdrop_at_once()
    {
        StartController();
        var game = OpenDota();
        Assert.True(game.HasBorder);

        ListChangesTo(Entry(Dota, "Dota 2"));

        Assert.False(game.HasBorder);
        Assert.Single(_desktop.VisibleBackdropsOn(Primary));
    }

    [Fact]
    public void A_list_change_that_keeps_a_game_on_leaves_its_backdrop_as_it_is_and_takes_its_new_name()
    {
        StartController(Entry(Dota, "dota2"));
        var game = OpenDota();
        var backdrop = Assert.Single(_desktop.Backdrops.Values);
        var changesBefore = game.PortChanges.Count;

        ListChangesTo(Entry(Dota, "Dota 2"), Entry(Novel, "Novel"));

        Assert.False(game.HasBorder);
        Assert.False(backdrop.IsDestroyed);
        Assert.Equal(changesBefore, game.PortChanges.Count);
        Assert.Equal("Dota 2", Assert.Single(_controller.TrackedWindows).Game.DisplayName);
    }

    [Fact]
    public void A_switched_off_game_that_cannot_be_put_back_yet_is_put_back_on_quit()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = OpenDota();
        _desktop.FailNextChanges(game, ChangeResult.Failed);

        ListChangesTo(Entry(Dota, "Dota 2", enabled: false));

        Assert.False(game.HasBorder);
        Assert.Empty(_desktop.VisibleBackdropsOn(Primary));
        Assert.Empty(_controller.Stop());
        AssertPutBack(game, DotaOriginal);
    }

    [Fact]
    public void A_switched_off_game_that_could_not_be_put_back_is_retried_when_it_is_switched_on_again()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = OpenDota();
        _desktop.FailNextChanges(game, ChangeResult.Failed);
        ListChangesTo(Entry(Dota, "Dota 2", enabled: false));

        ListChangesTo(Entry(Dota, "Dota 2"));

        Assert.False(game.HasBorder);
        Assert.Same(Assert.Single(_desktop.VisibleBackdropsOn(Primary)), _desktop.DirectlyBehind(game));
        _controller.Stop();
        AssertPutBack(game, DotaOriginal);
    }

    [Fact]
    public void Switching_off_a_fullscreen_game_changes_nothing_on_it()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), Primary.Bounds);

        ListChangesTo(Entry(Dota, "Dota 2", enabled: false));

        Assert.Empty(game.PortChanges);
        Assert.Empty(_controller.TrackedWindows);
    }

    [Fact]
    public void Switching_off_a_game_whose_window_has_closed_leaves_alone_the_window_that_now_has_its_handle()
    {
        StartController(Entry(Dota, "Dota 2"));
        var game = OpenDota();
        var other = _desktop.ReplaceUnderItsHandle(game, _desktop.StartProcess(@"C:\Apps\other.exe"),
            FakeWindowSystem.Captioned, Rect.FromPosition(10, 10, new Size(400, 300)));

        ListChangesTo(Entry(Dota, "Dota 2", enabled: false));

        Assert.Empty(other.PortChanges);
        Assert.Equal(Rect.FromPosition(10, 10, new Size(400, 300)), other.Outer);
    }

    [Fact]
    public void A_list_change_after_quitting_backdrops_nothing()
    {
        StartController();
        var game = OpenDota();
        _controller.Stop();

        ListChangesTo(Entry(Dota, "Dota 2"));

        Assert.True(game.HasBorder);
        Assert.Empty(_desktop.Backdrops);
    }
}
