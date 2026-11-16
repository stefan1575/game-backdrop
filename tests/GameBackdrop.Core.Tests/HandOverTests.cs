using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// The restart as administrator: this copy hands its games over to an elevated copy that takes
/// over. The elevated copy saves each window's frame as it finds it as the original, so it may
/// start only once every game is back as it was.
/// </summary>
public sealed class HandOverTests
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";

    private static readonly WindowFrame DotaOriginal =
        new(FakeWindowSystem.Captioned, Rect.FromPosition(50, 60, new Size(816, 639)));

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

    private void AssertRestyledWithBackdrop(FakeWindow game)
    {
        Assert.False(game.HasBorder);
        Assert.Equal(new Rect(560, 240, 1360, 840), game.Outer);
        Assert.Single(_desktop.VisibleBackdropsOn(FakeWindowSystem.Primary));
        Assert.True(game.IsMarkedFullscreen);
    }

    [Fact]
    public void A_game_that_cannot_be_put_back_stops_the_hand_over_and_this_copy_carries_on()
    {
        var controller = StartController();
        var dota = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);
        _desktop.OpenWindowed(_desktop.StartProcess(Novel, elevated: true), new Size(800, 600));
        _desktop.FailNextChanges(dota, ChangeResult.Failed, ChangeResult.Failed, ChangeResult.Failed, ChangeResult.Failed);
        var started = 0;

        var result = controller.HandOver(() => { started++; return true; }, retries: 3);

        Assert.False(result.HandedOver);
        Assert.Equal(dota.Handle, Assert.Single(result.NotRestored).Window);
        Assert.Equal(0, started);
        // Still this copy's: borderless, centred, blacked out behind, as before the attempt.
        AssertRestyledWithBackdrop(dota);
        Assert.Empty(controller.Stop());
        AssertUntouched(dota, DotaOriginal);
    }

    [Fact]
    public void A_restore_that_succeeds_on_a_retry_hands_over()
    {
        var controller = StartController();
        var dota = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);
        _desktop.FailNextChanges(dota, ChangeResult.Failed, ChangeResult.Failed);
        var retries = 0;

        var result = controller.HandOver(() => true, retries: 3, beforeRetry: () => retries++);

        Assert.True(result.HandedOver);
        Assert.Equal(2, retries);
        AssertUntouched(dota, DotaOriginal);
    }

    [Fact]
    public void The_copy_taking_over_starts_only_after_every_game_is_back_so_quitting_it_puts_them_back()
    {
        var controller = StartController();
        var dota = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);
        _desktop.OpenWindowed(_desktop.StartProcess(Novel, elevated: true), new Size(800, 600));
        _desktop.FailNextChanges(dota, ChangeResult.Failed);
        Assert.False(controller.HandOver(() => true).HandedOver);

        BackdropController? elevated = null;
        var result = controller.HandOver(() =>
        {
            // The games are back when the replacement starts.
            AssertUntouched(dota, DotaOriginal);
            elevated = new BackdropController(_desktop, new GameList(_games));
            return true;
        });
        controller.Stop(); // this copy quits
        elevated!.Start();

        Assert.True(result.HandedOver);
        AssertRestyledWithBackdrop(dota);
        Assert.Empty(elevated.Stop());
        AssertUntouched(dota, DotaOriginal);
    }

    [Fact]
    public void A_replacement_that_does_not_start_leaves_this_copy_running_with_its_games_changed_again()
    {
        var controller = StartController();
        var dota = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);

        var result = controller.HandOver(() =>
        {
            AssertUntouched(dota, DotaOriginal);
            return false; // the UAC prompt was declined
        });

        Assert.False(result.HandedOver);
        Assert.Empty(result.NotRestored);
        AssertRestyledWithBackdrop(dota);
        _desktop.UserActivates(dota); // events are handled again
        Assert.Empty(controller.Stop());
        AssertUntouched(dota, DotaOriginal);
    }

    [Fact]
    public void A_game_whose_unmark_failed_is_unmarked_and_changed_again_when_this_copy_carries_on()
    {
        var controller = StartController();
        var dota = _desktop.OpenWindowed(_desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);
        _desktop.FailNextMarks(dota);

        var result = controller.HandOver(() => true);

        Assert.False(result.HandedOver);
        AssertRestyledWithBackdrop(dota);
        Assert.Empty(controller.Stop());
        AssertUntouched(dota, DotaOriginal);
        Assert.False(dota.IsMarkedFullscreen);
    }

    [Fact]
    public void The_administrator_notice_is_not_raised_again_when_this_copy_carries_on()
    {
        var controller = StartController();
        var notices = 0;
        controller.GameRunsAsAdministrator += _ => notices++;
        _desktop.OpenWindowed(_desktop.StartProcess(Novel, elevated: true), new Size(800, 600));

        controller.HandOver(() => false);

        Assert.Equal(1, notices);
    }
}
