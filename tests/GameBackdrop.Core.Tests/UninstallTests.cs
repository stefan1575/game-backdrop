using GameBackdrop.Core.Startup;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// <c>--uninstall</c>, which a package manager runs before it deletes the exe: the running copy
/// quits (putting its games back), then the Startup shortcut and the task go.
/// </summary>
public sealed class UninstallTests
{
    private const string Exe = @"C:\Tools\GameBackdrop.exe";

    private readonly FakeScheduledTask _task = new(Exe);
    private readonly FakeShortcut _shortcut = new();
    private readonly FakeRunningCopy _running = new();

    private Uninstall NewUninstall() => new(_running, _shortcut, _task);

    [Fact]
    public void With_nothing_running_and_nothing_set_up_it_is_done_at_once()
    {
        Assert.Equal(UninstallResult.Done, NewUninstall().Run(elevated: false));
        Assert.Equal(0, _running.Asks);
    }

    [Fact]
    public void It_asks_the_running_copy_to_quit_then_removes_the_shortcut()
    {
        _running.Running = true;
        _shortcut.PlaceByHand();

        Assert.Equal(UninstallResult.Done, NewUninstall().Run(elevated: false));

        Assert.Equal(1, _running.Asks);
        Assert.False(_running.Running);
        Assert.False(_shortcut.Exists);
    }

    [Fact]
    public void The_shortcut_goes_only_once_the_running_copy_has_quit()
    {
        _running.Running = true;
        _shortcut.PlaceByHand();
        _running.QuitsInTime = false;

        Assert.Equal(UninstallResult.StillRunning, NewUninstall().Run(elevated: false));

        Assert.True(_shortcut.Exists);
    }

    [Fact]
    public void The_task_needs_administrator_before_the_running_copy_is_asked_anything()
    {
        _running.Running = true;
        _task.Register(atLogon: true);

        Assert.Equal(UninstallResult.NeedsAdministrator, NewUninstall().Run(elevated: false));

        Assert.Equal(0, _running.Asks);
        Assert.NotNull(_task.Task);
    }

    [Fact]
    public void As_administrator_it_deletes_the_task()
    {
        _running.Running = true;
        _task.Register(atLogon: true);

        _running.AskerElevated = true;
        Assert.Equal(UninstallResult.Done, NewUninstall().Run(elevated: true));

        Assert.Null(_task.Task);
        Assert.False(_running.Running);
    }

    [Fact]
    public void A_task_for_another_exe_goes_too()
    {
        _task.RegisteredBy(@"D:\Old\GameBackdrop.exe");

        Assert.Equal(UninstallResult.Done, NewUninstall().Run(elevated: true));

        Assert.Null(_task.Task);
    }

    [Fact]
    public void A_copy_running_as_administrator_needs_administrator_to_ask()
    {
        _running.Running = true;
        _running.Elevated = true;
        _shortcut.PlaceByHand();

        Assert.Equal(UninstallResult.NeedsAdministrator, NewUninstall().Run(elevated: false));
        Assert.True(_shortcut.Exists);

        _running.AskerElevated = true;
        Assert.Equal(UninstallResult.Done, NewUninstall().Run(elevated: true));
        Assert.False(_shortcut.Exists);
    }

    /// <summary>The single-instance guard, as another copy sees it.</summary>
    private sealed class FakeRunningCopy : IRunningCopy
    {
        public bool Running { get; set; }
        public bool Elevated { get; set; }
        public bool QuitsInTime { get; set; } = true;
        public int Asks { get; private set; }
        private bool _asked;

        /// <summary>Whether the copy asking runs as administrator.</summary>
        public bool AskerElevated { get; set; }

        public QuitRequest AskToQuit()
        {
            if (!Running) return QuitRequest.NoneRunning;
            if (Elevated && !AskerElevated) return QuitRequest.NeedsAdministrator;
            Asks++;
            _asked = true;
            return QuitRequest.Asked;
        }

        public IDisposable? WaitUntilGone()
        {
            if (Running && _asked && QuitsInTime) Running = false;
            return Running ? null : new Hold();
        }

        private sealed class Hold : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
