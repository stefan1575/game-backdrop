using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.WindowSystem;
using Xunit.Abstractions;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// A game window can close at any moment, and Windows can give its handle to another program's
/// window at once. Whenever that happens between two of the controller's port calls, the other
/// window must be left exactly as it is, and quitting must still leave nothing behind.
/// </summary>
public sealed class HandleReuseTests(ITestOutputHelper output)
{
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";
    private const string Novel = @"D:\VN\Novel\novel.exe";
    private const string Notepad = @"C:\Windows\System32\notepad.exe";

    // Where the other program's window sits: on the game's monitor, captioned, and marked
    // fullscreen by its own program, so a move, a restyle or an unmark by the controller shows.
    private static readonly Rect NewcomerRect = Rect.FromPosition(300, 200, new Size(816, 639));

    /// <summary>
    /// One scenario, set up and ready to act: <paramref name="Target"/> is the window whose handle
    /// goes to another program, and <paramref name="PutBack"/> the game's other windows, with the
    /// frames they must have again once the controller has stopped.
    /// </summary>
    private sealed record Scenario(
        FakeWindowSystem Desktop,
        BackdropController Controller,
        FakeWindow Target,
        Action Act,
        IReadOnlyList<(FakeWindow Window, Rect Outer, WindowStyle Style)> PutBack);

    public static TheoryData<string> Scenarios() =>
    [
        "a windowed game opened",
        "a windowed game opened whose menu bar wraps",
        "a game already running at Start",
        "a splash replaced by the game window (the splash's handle)",
        "a splash replaced by the game window (the game window's handle)",
        "the game coming to the foreground",
        "minimise and restore",
        "hide and show",
        "a move to the other monitor",
        "a click on the backdrop",
        "Stop",
    ];

    private static Scenario Build(string name)
    {
        var desktop = new FakeWindowSystem();
        var controller = new BackdropController(desktop, new GameList(
        [
            new GameEntry { ExePath = Dota, DisplayName = "Dota 2" },
            new GameEntry { ExePath = Novel, DisplayName = "Novel" },
        ]));
        FakeWindow Game(bool show = true) => desktop.OpenWindowed(desktop.StartProcess(Dota), new Size(800, 600), show: show);
        FakeWindow OtherApp() => desktop.OpenWindowed(desktop.StartProcess(Notepad), new Size(640, 480), 50, 50);
        Scenario Ready(FakeWindow target, Action act, params FakeWindow[] putBack) =>
            new(desktop, controller, target, act, putBack.Select(w => (w, w.Outer, w.Style)).ToList());

        if (name == "a game already running at Start")
        {
            var running = Game();
            OtherApp();
            return Ready(running, controller.Start);
        }
        controller.Start();
        switch (name)
        {
            case "a windowed game opened":
            {
                var game = Game(show: false);
                return Ready(game, () => desktop.Show(game));
            }
            case "a windowed game opened whose menu bar wraps":
            {
                // The port's outer size is a menu row short, so the controller corrects it with a second SetFrame.
                var game = desktop.Open(desktop.StartProcess(Dota), FakeWindowSystem.Captioned,
                    Rect.FromPosition(100, 100, new Size(816, 679)), show: false, menu: true, menuRows: 2);
                return Ready(game, () => desktop.Show(game));
            }
            case "a splash replaced by the game window (the splash's handle)":
            case "a splash replaced by the game window (the game window's handle)":
            {
                var process = desktop.StartProcess(Novel);
                var splash = desktop.OpenPopup(process, new Rect(100, 100, 500, 400));
                var game = desktop.OpenWindowed(process, new Size(1280, 720), show: false);
                var act = () => desktop.Show(game);
                // The splash was restyled and centred when it opened; it must be back where it was first.
                return name.Contains("splash's handle")
                    ? new(desktop, controller, splash, act, [(game, game.Outer, game.Style)])
                    : new(desktop, controller, game, act, [(splash, new Rect(100, 100, 500, 400), FakeWindowSystem.Popup)]);
            }
            case "the game coming to the foreground":
            {
                var game = Game();
                OtherApp();
                return Ready(game, () => desktop.UserActivates(game));
            }
            case "minimise and restore":
            {
                var game = Game();
                return Ready(game, () =>
                {
                    desktop.Minimise(game);
                    if (!game.IsGone) desktop.Restore(game); // once it has closed, nobody can restore it
                });
            }
            case "hide and show":
            {
                var game = Game();
                return Ready(game, () =>
                {
                    desktop.Hide(game);
                    if (!game.IsGone) desktop.Show(game);
                });
            }
            case "a move to the other monitor":
            {
                var game = Game();
                return Ready(game, () => desktop.Move(game, Rect.FromPosition(2500, 100, new Size(800, 600))));
            }
            case "a click on the backdrop":
            {
                var game = Game();
                OtherApp();
                var backdrop = Assert.Single(desktop.Backdrops.Values).Handle;
                return Ready(game, () => desktop.ClickBackdrop(backdrop));
            }
            case "Stop":
            {
                var game = Game();
                return Ready(game, () => Assert.Empty(controller.Stop()));
            }
        }
        throw new ArgumentException($"No scenario '{name}'.", nameof(name));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void A_window_that_takes_a_game_s_handle_at_any_point_is_never_changed(string scenario)
    {
        // How many port calls the scenario makes when nothing interferes.
        var clean = Build(scenario);
        var before = clean.Desktop.Calls.Count;
        clean.Act();
        var calls = clean.Desktop.Calls.Count - before;
        Assert.True(calls > 0);

        var failures = new List<string>();
        var skipped = new List<int>();
        for (var k = 1; k <= calls; k++)
        {
            var run = Build(scenario);
            var after = run.Desktop.Calls.Count + k - 1;
            FakeWindow? newcomer = null;
            run.Desktop.AfterCall(k, () => newcomer = run.Desktop.ReplaceUnderItsHandle(
                run.Target, run.Desktop.StartProcess(Notepad), FakeWindowSystem.Captioned, NewcomerRect, markedFullscreen: true));

            run.Act();
            run.Desktop.FinishHandOver();
            if (newcomer is null)
            {
                Assert.True(run.Desktop.HandOverSkipped, $"k={k}: the handle was never handed over.");
                skipped.Add(k);
                continue;
            }
            var notPutBack = run.Controller.Stop();

            var problems = new List<string>();
            if (newcomer.Outer != NewcomerRect) problems.Add($"moved to {newcomer.Outer}");
            if (newcomer.Style != FakeWindowSystem.Captioned) problems.Add("restyled");
            if (!newcomer.IsMarkedFullscreen) problems.Add("unmarked");
            if (newcomer.PortChanges.Count > 0) problems.Add($"port calls on it: {string.Join(", ", newcomer.PortChanges)}");
            if (newcomer.BackdropsShownBehind > 0) problems.Add("a backdrop was shown behind it");
            if (run.Desktop.Backdrops.Values.Any(b => !b.IsDestroyed)) problems.Add("a backdrop is left after Stop");
            if (notPutBack.Count > 0) problems.Add($"Stop could not put back {notPutBack.Count} window(s)");
            if (run.Controller.TrackedWindows.Count > 0) problems.Add("windows still tracked after Stop");
            foreach (var (window, outer, style) in run.PutBack)
                if (window.Outer != outer || window.Style != style || window.IsMarkedFullscreen)
                    problems.Add("the game's other window was not put back");
            if (problems.Count > 0)
                failures.Add($"k={k} (after {run.Desktop.Calls[after].Call}): {string.Join("; ", problems)}");
        }

        output.WriteLine($"{scenario}: {calls} port calls; handed over after each but {skipped.Count} " +
            $"(a Describe and the change it confirms: k = {string.Join(", ", skipped)}).");
        foreach (var failure in failures) output.WriteLine(failure);
        Assert.True(failures.Count == 0, $"{scenario}:\n{string.Join("\n", failures)}");
    }
}
