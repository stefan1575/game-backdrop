using GameBackdrop.Core.Controller;
using GameBackdrop.Core.Games;
using GameBackdrop.Core.Geometry;
using GameBackdrop.Core.Settings;
using GameBackdrop.Core.Startup;
using GameBackdrop.Core.WindowSystem;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// Always run as administrator: a scheduled task that runs the app elevated with no prompt. A
/// normal launch runs the task and exits; while the task is set up, it is also what starts the
/// app at logon.
/// </summary>
public sealed class AlwaysAsAdministratorTests : IDisposable
{
    private const string Exe = @"C:\Tools\GameBackdrop.exe";
    private const string Dota = @"C:\Games\dota 2 beta\game\bin\win64\dota2.exe";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "GameBackdropTests", Guid.NewGuid().ToString("N"));
    private readonly FakeScheduledTask _task = new(Exe);
    private readonly FakeShortcut _shortcut = new();
    private SettingsStore Store => new(Path.Combine(_folder, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>One run of the app: what Program wires up.</summary>
    private (AlwaysAsAdministrator Always, StartAtLogon StartAtLogon) NewRun()
    {
        var task = new AdministratorTask(_task, Exe);
        var startAtLogon = new StartAtLogon(Store, _shortcut, task);
        return (new AlwaysAsAdministrator(task, startAtLogon), startAtLogon);
    }

    [Fact]
    public void A_normal_launch_with_the_task_set_up_runs_it_and_exits()
    {
        _task.Register(atLogon: false);

        Assert.True(NewRun().Always.LaunchThroughTask(elevated: false, startedByTask: false));
        Assert.Equal(1, _task.Runs);
    }

    [Fact]
    public void A_launch_with_a_task_for_another_exe_starts_normally()
    {
        _task.RegisteredBy(@"D:\Old\GameBackdrop.exe");

        var always = NewRun().Always;

        Assert.False(always.IsOn);
        Assert.False(always.LaunchThroughTask(elevated: false, startedByTask: false));
        Assert.Equal(0, _task.Runs);
    }

    [Fact]
    public void A_launch_with_no_task_starts_normally()
    {
        Assert.False(NewRun().Always.LaunchThroughTask(elevated: false, startedByTask: false));
        Assert.Equal(0, _task.Runs);
    }

    [Fact]
    public void An_elevated_launch_or_one_the_task_started_never_runs_the_task_again()
    {
        // A copy the task started runs unelevated if the user isn't an administrator: running
        // the task again from it would start copies for ever.
        _task.Register(atLogon: false);
        var always = NewRun().Always;

        Assert.False(always.LaunchThroughTask(elevated: true, startedByTask: false));
        Assert.False(always.LaunchThroughTask(elevated: false, startedByTask: true));
        Assert.Equal(0, _task.Runs);
    }

    [Fact]
    public void A_task_that_cannot_be_run_falls_back_to_a_normal_start()
    {
        _task.Register(atLogon: false);
        _task.FailRun = true;

        Assert.False(NewRun().Always.LaunchThroughTask(elevated: false, startedByTask: false));
    }

    [Fact]
    public void Turning_it_on_unelevated_hands_the_games_over_to_one_elevated_copy_which_creates_the_task()
    {
        var desktop = new FakeWindowSystem();
        var controller = new BackdropController(desktop, new GameList([new GameEntry { ExePath = Dota, DisplayName = "Dota 2" }]));
        controller.Start();
        var dota = desktop.OpenWindowed(desktop.StartProcess(Dota), new Size(800, 600), left: 50, top: 60);
        var original = new WindowFrame(FakeWindowSystem.Captioned, Rect.FromPosition(50, 60, new Size(816, 639)));
        NewRun().StartAtLogon.ApplyAtStart();
        Assert.True(_shortcut.Exists);
        var started = 0;

        var result = NewRun().Always.TurnOn(elevated: false, controller, () =>
        {
            started++;
            // The game is back before the elevated copy starts, and this copy has made no task.
            Assert.Equal(original.Style, dota.Style);
            Assert.Equal(original.Outer, dota.Outer);
            Assert.Null(_task.Task);
            return true;
        });

        Assert.True(result!.HandedOver);
        Assert.Equal(1, started);
        controller.Stop(); // this copy quits

        // The elevated copy, started to set the task up.
        var elevated = NewRun();
        elevated.Always.SetUp();
        Assert.True(elevated.Always.IsOn);
        Assert.Equal(1, _task.Registers);
        // Start at logon is on: the task's logon trigger starts the app now, and the shortcut is gone.
        Assert.True(_task.Task!.StartsAtLogon);
        Assert.False(_shortcut.Exists);
        Assert.True(elevated.StartAtLogon.IsOn);
    }

    [Fact]
    public void A_declined_prompt_leaves_this_copy_running_with_its_games_and_no_task()
    {
        var desktop = new FakeWindowSystem();
        var controller = new BackdropController(desktop, new GameList([new GameEntry { ExePath = Dota }]));
        controller.Start();
        var dota = desktop.OpenWindowed(desktop.StartProcess(Dota), new Size(800, 600));

        var result = NewRun().Always.TurnOn(elevated: false, controller, () => false);

        Assert.False(result!.HandedOver);
        Assert.False(dota.HasBorder);
        Assert.Null(_task.Task);
    }

    [Fact]
    public void Turning_it_on_already_elevated_sets_the_task_up_here_with_no_hand_over()
    {
        var controller = new BackdropController(new FakeWindowSystem(), new GameList([]));
        controller.Start();

        var result = NewRun().Always.TurnOn(elevated: true, controller, () => throw new InvalidOperationException("no copy to start"));

        Assert.Null(result);
        Assert.True(NewRun().Always.IsOn);
    }

    [Fact]
    public void Turning_it_on_with_start_at_logon_off_makes_a_task_with_no_logon_trigger()
    {
        var run = NewRun();
        run.StartAtLogon.ApplyAtStart();
        run.StartAtLogon.Set(false);

        NewRun().Always.SetUp();

        Assert.False(_task.Task!.StartsAtLogon);
        Assert.False(_shortcut.Exists);
        Assert.False(NewRun().StartAtLogon.IsOn);
    }

    [Fact]
    public void Turning_it_off_deletes_the_task_and_puts_the_startup_shortcut_back_when_start_at_logon_is_on()
    {
        NewRun().StartAtLogon.ApplyAtStart();
        NewRun().Always.SetUp();
        Assert.False(_shortcut.Exists);

        var run = NewRun();
        run.Always.TurnOff();

        Assert.Null(_task.Task);
        Assert.False(run.Always.IsOn);
        Assert.True(_shortcut.Exists);
        Assert.True(run.StartAtLogon.IsOn);
    }

    [Fact]
    public void Turning_it_off_with_start_at_logon_off_leaves_no_shortcut()
    {
        var run = NewRun();
        run.StartAtLogon.ApplyAtStart();
        run.StartAtLogon.Set(false);
        NewRun().Always.SetUp();

        NewRun().Always.TurnOff();

        Assert.Null(_task.Task);
        Assert.False(_shortcut.Exists);
    }

    [Fact]
    public void Start_at_logon_with_the_task_set_up_is_the_tasks_logon_trigger_and_the_shortcut_goes()
    {
        _task.Register(atLogon: false);
        _shortcut.PlaceByHand();

        var run = NewRun();
        run.StartAtLogon.ApplyAtStart();

        Assert.True(_task.Task!.StartsAtLogon);
        Assert.False(_shortcut.Exists);
        Assert.True(run.StartAtLogon.IsOn);

        run.StartAtLogon.Set(false);
        Assert.False(_task.Task!.StartsAtLogon);
        Assert.False(_shortcut.Exists);
        Assert.False(run.StartAtLogon.IsOn);

        run.StartAtLogon.Set(true);
        Assert.True(_task.Task!.StartsAtLogon);
        Assert.False(_shortcut.Exists);
        Assert.True(Store.Load().Settings.StartAtLogon);
    }

    [Fact]
    public void A_start_with_the_trigger_already_as_recorded_registers_nothing()
    {
        NewRun().StartAtLogon.ApplyAtStart();
        NewRun().Always.SetUp();
        var registers = _task.Registers;

        NewRun().StartAtLogon.ApplyAtStart();

        Assert.Equal(registers, _task.Registers);
    }

    [Fact]
    public void A_task_for_another_exe_leaves_start_at_logon_to_the_shortcut()
    {
        _task.RegisteredBy(@"D:\Old\GameBackdrop.exe", atLogon: true);

        NewRun().StartAtLogon.ApplyAtStart();

        Assert.True(_shortcut.Exists);
        Assert.Equal(0, _task.Registers);
    }
}
