using GameBackdrop.Core.Startup;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// <c>--uninstall</c>, which a package manager runs before it deletes the exe: the running copy
/// quits (putting its games back), then the Startup shortcut and the task go.
/// </summary>
public sealed class UninstallTests
{
    private const string Exe = @"C:\Tools\GameBackdrop.exe";
    private const int Attempts = 8;

    private readonly FakeScheduledTask _task = new(Exe);
    private readonly FakeShortcut _shortcut = new();
    private readonly FakeRunningCopy _running = new();

    private UninstallResult Run(bool elevated)
    {
        _running.AskerElevated = elevated;
        return new Uninstall(_running, _shortcut, _task).Run(elevated, Attempts);
    }

    [Fact]
    public void With_nothing_running_and_nothing_set_up_it_is_done_at_once()
    {
        Assert.Equal(UninstallResult.Done, Run(elevated: false));
        Assert.Equal(0, _running.Asks);
    }

    [Fact]
    public void It_asks_the_running_copy_to_quit_then_removes_the_shortcut()
    {
        _running.Running = true;
        _shortcut.PlaceByHand();

        Assert.Equal(UninstallResult.Done, Run(elevated: false));

        Assert.Equal(1, _running.Asks);
        Assert.False(_running.Running);
        Assert.False(_shortcut.Exists);
    }

    [Fact]
    public void A_copy_that_keeps_running_keeps_its_shortcut_and_is_asked_once()
    {
        // As when a game can't be put back: the copy carries on rather than quit.
        _running.Running = true;
        _running.Quits = false;
        _shortcut.PlaceByHand();

        Assert.Equal(UninstallResult.StillRunning, Run(elevated: false));

        Assert.True(_shortcut.Exists);
        Assert.Equal(1, _running.Asks);
    }

    [Fact]
    public void A_copy_that_starts_after_the_first_look_is_asked_too()
    {
        _running.StartsAfterFirstAsk = true;
        _shortcut.PlaceByHand();

        Assert.Equal(UninstallResult.Done, Run(elevated: false));

        Assert.Equal(1, _running.Asks);
        Assert.False(_running.Running);
        Assert.False(_shortcut.Exists);
    }

    [Fact]
    public void A_copy_of_another_settings_scope_is_left_running()
    {
        // The real app runs while a test run's --settings uninstall tidies up its own shortcut.
        _running.OtherScopeRunning = true;
        _shortcut.PlaceByHand();

        Assert.Equal(UninstallResult.Done, Run(elevated: false));

        Assert.True(_running.OtherScopeRunning);
        Assert.Equal(0, _running.Asks);
        Assert.False(_shortcut.Exists);
    }

    [Fact]
    public void The_task_needs_administrator_before_the_running_copy_is_asked_anything()
    {
        _running.Running = true;
        _task.Register(atLogon: true);

        Assert.Equal(UninstallResult.NeedsAdministrator, Run(elevated: false));

        Assert.Equal(0, _running.Asks);
        Assert.NotNull(_task.Task);
    }

    [Fact]
    public void As_administrator_it_deletes_the_task()
    {
        _running.Running = true;
        _task.Register(atLogon: true);

        Assert.Equal(UninstallResult.Done, Run(elevated: true));

        Assert.Null(_task.Task);
        Assert.False(_running.Running);
    }

    [Fact]
    public void A_task_for_another_exe_goes_too()
    {
        _task.RegisteredBy(@"D:\Old\GameBackdrop.exe");

        Assert.Equal(UninstallResult.Done, Run(elevated: true));

        Assert.Null(_task.Task);
    }

    [Fact]
    public void A_copy_running_as_administrator_needs_administrator_to_ask()
    {
        _running.Running = true;
        _running.Elevated = true;
        _shortcut.PlaceByHand();

        Assert.Equal(UninstallResult.NeedsAdministrator, Run(elevated: false));
        Assert.True(_shortcut.Exists);

        Assert.Equal(UninstallResult.Done, Run(elevated: true));
        Assert.False(_shortcut.Exists);
    }

    /// <summary>
    /// The single-instance guard, as another copy sees it: one guard per session, and a quit
    /// signal per settings scope that a copy makes before it takes the guard.
    /// </summary>
    private sealed class FakeRunningCopy : IRunningCopy
    {
        /// <summary>A copy of this scope runs.</summary>
        public bool Running { get; set; }

        /// <summary>A copy of another scope holds the guard; it has no quit signal of this scope.</summary>
        public bool OtherScopeRunning { get; set; }

        /// <summary>A copy of this scope starts just after the first ask finds none.</summary>
        public bool StartsAfterFirstAsk { get; set; }

        public bool Elevated { get; set; }
        public bool AskerElevated { get; set; }
        public bool Quits { get; set; } = true;
        public int Asks { get; private set; }
        private bool _asked;
        private bool _looked;

        public QuitRequest AskToQuit()
        {
            var first = !_looked;
            _looked = true;
            if (!Running)
            {
                if (first && StartsAfterFirstAsk) Running = true;
                return QuitRequest.NoneRunning;
            }
            if (Elevated && !AskerElevated) return QuitRequest.NeedsAdministrator;
            Asks++;
            _asked = true;
            return QuitRequest.Asked;
        }

        public IDisposable? TryHold()
        {
            if (Running && _asked && Quits) Running = false;
            return Running || OtherScopeRunning ? null : new Hold();
        }

        private sealed class Hold : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
