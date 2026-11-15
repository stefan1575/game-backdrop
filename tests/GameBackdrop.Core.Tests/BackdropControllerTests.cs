using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

public sealed class BackdropControllerTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

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

    private static void AssertUntouched(FakeWindow window, WindowFrame original)
    {
        Assert.Equal(original.Style, window.Style);
        Assert.Equal(original.Outer, window.Outer);
    }

    [Fact]
    public void A_windowed_game_loses_its_border_and_is_centred_on_its_monitor_at_its_own_size()
    {
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600));

        Assert.False(game.HasBorder);
        Assert.Equal(new Rect(560, 240, 1360, 840), game.Outer);
        Assert.Equal(game.Outer, game.Client);
    }

    [Fact]
    public void A_windowed_game_on_another_monitor_is_centred_on_that_monitor()
    {
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Novel), new Size(1280, 720), left: 2000, top: 0);

        Assert.False(game.HasBorder);
        Assert.Equal(Rect.FromPosition(1920 + 640, -200 + 360, new Size(1280, 720)), game.Outer);
    }

    [Fact]
    public void An_odd_sized_game_is_placed_on_whole_pixels()
    {
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(801, 601));

        Assert.Equal(Rect.FromPosition(559, 239, new Size(801, 601)), game.Outer);
    }

    [Fact]
    public void A_game_larger_than_its_monitor_keeps_its_top_left_on_the_monitor()
    {
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(2000, 1200), left: 100, top: 100);

        Assert.Equal(Rect.FromPosition(0, 0, new Size(2000, 1200)), game.Outer);
    }

    [Fact]
    public void A_game_wider_but_not_taller_than_its_monitor_is_still_centred_vertically()
    {
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(2000, 600), left: 0, top: 0);

        Assert.Equal(Rect.FromPosition(0, 240, new Size(2000, 600)), game.Outer);
    }

    [Fact]
    public void A_borderless_fullscreen_game_is_left_alone()
    {
        StartController();

        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), FakeWindowSystem.Primary.Bounds);

        AssertUntouched(game, new WindowFrame(FakeWindowSystem.Popup, FakeWindowSystem.Primary.Bounds));
        Assert.Empty(_desktop.FrameChanges);
    }

    [Fact]
    public void A_fullscreen_game_whose_frame_overhangs_its_monitor_is_left_alone()
    {
        StartController();
        var overhanging = new Rect(-8, -31, 1928, 1088);

        var game = _desktop.Open(_desktop.StartProcess(Dota), FakeWindowSystem.Captioned, overhanging);

        AssertUntouched(game, new WindowFrame(FakeWindowSystem.Captioned, overhanging));
    }

    [Fact]
    public void A_window_that_only_fills_the_work_area_counts_as_windowed()
    {
        StartController();

        var game = _desktop.OpenPopup(_desktop.StartProcess(Dota), FakeWindowSystem.Primary.WorkArea);

        Assert.NotEmpty(_desktop.FrameChanges);
        Assert.Equal(Rect.FromPosition(0, 24, FakeWindowSystem.Primary.WorkArea.Size), game.Outer);
    }

    [Fact]
    public void An_unlisted_program_is_left_alone()
    {
        StartController();

        var other = _desktop.OpenWindowed(_desktop.StartProcess(Notepad), new Size(800, 600));

        Assert.True(other.HasBorder);
        Assert.Empty(_desktop.FrameChanges);
    }

    [Fact]
    public void A_disabled_game_is_left_alone()
    {
        _games[0].Enabled = false;
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600));

        Assert.True(game.HasBorder);
        Assert.Empty(_desktop.FrameChanges);
    }

    [Fact]
    public void A_game_is_matched_by_its_full_path_whatever_the_case()
    {
        StartController();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota.ToUpperInvariant()), new Size(800, 600));
        var sameNameElsewhere = _desktop.OpenWindowed(_desktop.StartProcess(@"E:\Other\dota2.exe"), new Size(800, 600));

        Assert.False(game.HasBorder);
        Assert.True(sameNameElsewhere.HasBorder);
    }

    [Fact]
    public void A_game_already_running_when_the_app_starts_is_restyled()
    {
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600));

        StartController();

        Assert.False(game.HasBorder);
        Assert.Equal(new Rect(560, 240, 1360, 840), game.Outer);
    }

    [Fact]
    public void Hidden_and_minimised_windows_are_not_touched()
    {
        var process = _desktop.StartProcess(Dota);
        var hidden = _desktop.OpenWindowed(process, new Size(800, 600), show: false);
        var minimised = _desktop.OpenWindowed(_desktop.StartProcess(Novel), new Size(800, 600), show: false);
        minimised.IsVisible = true;
        minimised.IsMinimized = true;

        StartController();

        Assert.True(hidden.HasBorder);
        Assert.True(minimised.HasBorder);
    }

    [Fact]
    public void A_game_s_dialogs_and_tool_windows_are_not_touched()
    {
        StartController();
        var process = _desktop.StartProcess(Dota);
        var main = _desktop.OpenWindowed(process, new Size(800, 600));

        var dialog = _desktop.Open(process, FakeWindowSystem.Captioned, new Rect(100, 100, 400, 300), owner: main.Handle);
        var tool = _desktop.Open(process, FakeWindowSystem.Captioned with { ExStyle = WindowStyle.WS_EX_TOOLWINDOW }, new Rect(100, 100, 400, 300));

        Assert.True(dialog.HasBorder);
        Assert.True(tool.HasBorder);
        Assert.False(main.HasBorder);
    }

    [Fact]
    public void The_real_game_window_replaces_a_splash_screen_which_is_put_back()
    {
        StartController();
        var process = _desktop.StartProcess(Novel);
        var splashRect = new Rect(100, 100, 500, 400);
        var splash = _desktop.OpenPopup(process, splashRect);

        var game = _desktop.OpenWindowed(process, new Size(1280, 720));

        Assert.False(game.HasBorder);
        Assert.Equal(Rect.FromPosition(320, 180, new Size(1280, 720)), game.Outer);
        AssertUntouched(splash, new WindowFrame(FakeWindowSystem.Popup, splashRect));
    }

    [Fact]
    public void A_smaller_frameless_window_does_not_replace_the_game_window()
    {
        StartController();
        var process = _desktop.StartProcess(Novel);
        var game = _desktop.OpenWindowed(process, new Size(1280, 720));
        var placed = game.Outer;

        var popup = _desktop.OpenPopup(process, new Rect(0, 0, 300, 200));

        Assert.Equal(placed, game.Outer);
        Assert.False(game.HasBorder);
        Assert.Equal(new Rect(0, 0, 300, 200), popup.Outer);
    }

    [Fact]
    public void Quitting_restores_every_changed_game()
    {
        var controller = StartController();
        var dota = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);
        var novel = _desktop.OpenWindowed(_desktop.StartProcess(Novel), new Size(1280, 720), left: 2000, top: 10);
        var dotaOriginal = new WindowFrame(FakeWindowSystem.Captioned, Rect.FromPosition(50, 60, new Size(816, 639)));
        var novelOriginal = new WindowFrame(FakeWindowSystem.Captioned, Rect.FromPosition(2000, 10, new Size(1296, 759)));

        controller.Stop();

        AssertUntouched(dota, dotaOriginal);
        AssertUntouched(novel, novelOriginal);
    }

    [Fact]
    public void Quitting_leaves_a_fullscreen_game_alone()
    {
        var controller = StartController();
        _desktop.OpenPopup(_desktop.StartProcess(Dota), FakeWindowSystem.Primary.Bounds);

        controller.Stop();

        Assert.Empty(_desktop.FrameChanges);
    }

    [Fact]
    public void A_game_that_exits_needs_nothing_restored()
    {
        var controller = StartController();
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600));
        _desktop.Close(game);
        var changesBeforeQuit = _desktop.FrameChanges.Count;

        controller.Stop();

        Assert.Equal(changesBeforeQuit, _desktop.FrameChanges.Count);
    }

    [Fact]
    public void After_quitting_new_game_windows_are_left_alone()
    {
        var controller = StartController();
        controller.Stop();

        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600));

        Assert.True(game.HasBorder);
    }

    [Fact]
    public void A_game_the_app_may_not_touch_is_left_as_it_is_and_not_restored_on_quit()
    {
        var controller = StartController();
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota, elevated: true), new Size(800, 600));
        var attempts = _desktop.FrameChanges.Count;

        controller.Stop();

        Assert.True(game.HasBorder);
        Assert.Equal(attempts, _desktop.FrameChanges.Count);
    }

    [Fact]
    public void Showing_a_restyled_game_again_does_not_restyle_it_twice()
    {
        var controller = StartController();
        var game = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);

        _desktop.Show(game);
        controller.Stop();

        AssertUntouched(game, new WindowFrame(FakeWindowSystem.Captioned, Rect.FromPosition(50, 60, new Size(816, 639))));
    }
}
