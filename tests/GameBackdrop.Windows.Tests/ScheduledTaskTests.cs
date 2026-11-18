namespace GameBackdrop.Windows.Tests;

/// <summary>
/// The Task Scheduler port against the real Task Scheduler, unelevated: each test's task is at the
/// user's normal run level, in a folder of its own that is gone afterwards. Registering at run
/// level highest needs administrator, so that is checked by hand.
/// </summary>
public sealed class ScheduledTaskTests : IDisposable
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private readonly string _folder = $@"\GameBackdropTests {Guid.NewGuid():N}";
    private readonly string _marker = Path.Combine(Path.GetTempPath(), "GameBackdropTests", $"{Guid.NewGuid():N}.ran");

    private ScheduledTask Task(string arguments = "/c exit") =>
        new(_folder, "Always as administrator", Cmd, arguments, highest: false);

    public void Dispose()
    {
        Task().Delete();
        if (File.Exists(_marker)) File.Delete(_marker);
    }

    [Fact]
    public void No_task_reads_as_none_and_deleting_it_is_harmless()
    {
        var task = Task();

        Assert.Null(task.Read());
        task.Delete();
    }

    [Fact]
    public void A_registered_task_reads_back_its_exe_and_whether_it_starts_at_logon()
    {
        var task = Task();

        task.Register(atLogon: false);
        Assert.Equal(new(Cmd, false), task.Read());

        task.Register(atLogon: true);
        Assert.Equal(new(Cmd, true), task.Read());
    }

    [Fact]
    public void Running_the_task_starts_its_exe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_marker)!);
        var task = Task($"/c type nul > \"{_marker}\"");
        task.Register(atLogon: false);

        task.Run();

        for (var i = 0; i < 100 && !File.Exists(_marker); i++) Thread.Sleep(100);
        Assert.True(File.Exists(_marker), "the task's exe didn't run");
    }

    [Fact]
    public void Deleting_the_task_removes_it_and_its_empty_folder()
    {
        var task = Task();
        task.Register(atLogon: true);

        task.Delete();

        Assert.Null(task.Read());
        Assert.Throws<InvalidOperationException>(task.Run);
    }

    [Fact]
    public void A_settings_run_has_a_task_name_of_its_own_from_its_path()
    {
        var real = ScheduledTask.NameFor(null);
        var test = ScheduledTask.NameFor(@"C:\Temp\a\settings.json");

        Assert.NotEqual(real, test);
        Assert.Equal(test, ScheduledTask.NameFor(@"c:\temp\A\settings.json"));
        Assert.NotEqual(test, ScheduledTask.NameFor(@"C:\Temp\b\settings.json"));
        Assert.DoesNotContain('\\', test);
    }
}
