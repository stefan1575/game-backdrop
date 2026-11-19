using GameBackdrop.Core.Startup;

namespace GameBackdrop.Core.Tests;

/// <summary>
/// <c>--uninstall</c>: quit the running copy (which puts its games back), then remove the Startup
/// shortcut and the scheduled task. Administrator is asked for (one UAC prompt) only when the task
/// is there or the running copy is elevated.
/// </summary>
public sealed class UninstallTests
{
    private const string Exe = @"C:\Tools\GameBackdrop.exe";

    private readonly FakeRunningCopy _copy = new();
    private readonly FakeShortcut _shortcut = new();
    private readonly FakeScheduledTask _task = new(Exe);
    private int _elevatedRuns;

    private UninstallResult Run(bool elevated = false, int? elevatedExitCode = 0) =>
        new Uninstall(_copy, _shortcut, _task).Run(elevated, () =>
        {
            _elevatedRuns++;
            return elevatedExitCode;
        });

    [Fact]
    public void With_nothing_running_and_nothing_set_up_it_is_done_at_once_with_no_prompt()
    {
        var result = Run();

        Assert.True(result.Done);
        Assert.Equal(0, _elevatedRuns);
        Assert.Equal(0, _copy.Quits);
    }

    [Fact]
    public void A_running_copy_is_quit_before_the_shortcut_goes_with_no_prompt()
    {
        _copy.State = RunningCopyState.Running;
        _shortcut.PlaceByHand();
        var shortcutAtQuit = false;
        _copy.OnQuit = () => shortcutAtQuit = _shortcut.Exists;

        var result = Run();

        Assert.True(result.Done);
        Assert.Equal(1, _copy.Quits);
        Assert.True(shortcutAtQuit, "the shortcut went before the running copy had quit");
        Assert.False(_shortcut.Exists);
        Assert.Equal(0, _elevatedRuns);
    }

    [Fact]
    public void A_running_copy_that_does_not_quit_stops_the_uninstall_with_everything_left()
    {
        _copy.State = RunningCopyState.Running;
        _copy.RefusesToQuit = true;
        _shortcut.PlaceByHand();

        var result = Run();

        Assert.False(result.Done);
        Assert.Contains("quit", result.Message);
        Assert.True(_shortcut.Exists);
    }

    [Fact]
    public void The_task_needs_administrator_so_an_elevated_copy_does_the_uninstall()
    {
        _task.Register(atLogon: true);
        _shortcut.PlaceByHand();

        var result = Run(elevated: false, elevatedExitCode: 0);

        Assert.True(result.Done);
        Assert.Equal(1, _elevatedRuns);
        // The elevated copy (a fake here) does it all; this one touches nothing.
        Assert.NotNull(_task.Task);
        Assert.True(_shortcut.Exists);
    }

    [Fact]
    public void An_elevated_running_copy_needs_administrator_to_quit_so_an_elevated_copy_does_the_uninstall()
    {
        _copy.State = RunningCopyState.OutOfReach;

        var result = Run(elevated: false, elevatedExitCode: 0);

        Assert.True(result.Done);
        Assert.Equal(1, _elevatedRuns);
        Assert.Equal(0, _copy.Quits);
    }

    [Fact]
    public void Running_elevated_it_quits_the_copy_and_removes_the_shortcut_and_the_task_itself()
    {
        _copy.State = RunningCopyState.Running;
        _task.Register(atLogon: true);
        _shortcut.PlaceByHand();
        var taskAtQuit = false;
        _copy.OnQuit = () => taskAtQuit = _task.Task is not null;

        var result = Run(elevated: true);

        Assert.True(result.Done);
        Assert.Equal(0, _elevatedRuns);
        Assert.True(taskAtQuit, "the task went before the running copy had quit");
        Assert.Null(_task.Task);
        Assert.False(_shortcut.Exists);
    }

    [Fact]
    public void A_task_left_by_a_copy_somewhere_else_is_removed_too()
    {
        _task.RegisteredBy(@"D:\Old\GameBackdrop.exe");

        Assert.True(Run(elevated: true).Done);
        Assert.Null(_task.Task);
    }

    [Fact]
    public void A_declined_prompt_stops_the_uninstall_with_a_message()
    {
        _task.Register(atLogon: false);

        var result = Run(elevated: false, elevatedExitCode: null);

        Assert.False(result.Done);
        Assert.Contains("administrator", result.Message);
        Assert.False(result.ShownByElevatedCopy);
        Assert.NotNull(_task.Task);
    }

    [Fact]
    public void An_elevated_copy_that_fails_fails_the_uninstall_and_has_shown_why_itself()
    {
        _task.Register(atLogon: false);

        var result = Run(elevated: false, elevatedExitCode: 1);

        Assert.False(result.Done);
        Assert.True(result.ShownByElevatedCopy);
    }

    [Fact]
    public void A_task_that_cannot_be_deleted_fails_the_uninstall_with_why()
    {
        _task.Register(atLogon: false);
        _task.FailDelete = true;

        var result = Run(elevated: true);

        Assert.False(result.Done);
        Assert.Contains("The task can't be deleted.", result.Message);
    }

    [Fact]
    public void A_shortcut_that_cannot_be_removed_fails_the_uninstall_with_why()
    {
        _shortcut.PlaceByHand();
        _shortcut.FailRemove = true;

        var result = Run();

        Assert.False(result.Done);
        Assert.Contains("The Startup folder can't be written.", result.Message);
    }

    [Fact]
    public void A_task_that_cannot_be_read_fails_the_uninstall_with_why()
    {
        _task.FailRead = true;

        var result = Run();

        Assert.False(result.Done);
        Assert.Contains("The task can't be read.", result.Message);
        Assert.Equal(0, _elevatedRuns);
    }
}
