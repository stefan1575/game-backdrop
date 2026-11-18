using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GameBackdrop.Windows.Tests;

/// <summary>
/// The single-instance guard against real named mutexes. A mutex belongs to the thread that took
/// it, so each "other copy" here runs on a thread of its own, as a second process would.
/// </summary>
public sealed class SingleInstanceTests
{
    private readonly string _name = $@"Local\GameBackdrop.Tests.{Guid.NewGuid():N}";
    private string _event => _name + ".OpenList";

    [Fact]
    public void A_second_launch_asks_the_running_copy_to_open_its_list()
    {
        using var running = SingleInstance.Claim(_name, _event, TimeSpan.Zero)!;
        using var opened = new ManualResetEventSlim();
        using var listening = running.OnSecondLaunch(new SynchronizationContext(), opened.Set);

        Assert.Null(OnOwnThread(() => SingleInstance.Claim(_name, _event, TimeSpan.Zero)));

        Assert.True(opened.Wait(TimeSpan.FromSeconds(5)), "the running copy wasn't asked to open its list");
    }

    [Fact]
    public void A_takeover_that_gives_up_does_not_ask_the_running_copy_to_open_its_list()
    {
        using var running = SingleInstance.Claim(_name, _event, TimeSpan.Zero)!;
        using var opened = new ManualResetEventSlim();
        using var listening = running.OnSecondLaunch(new SynchronizationContext(), opened.Set);

        Assert.Null(OnOwnThread(() => SingleInstance.Claim(_name, _event, TimeSpan.FromMilliseconds(200))));

        Assert.False(opened.Wait(TimeSpan.FromMilliseconds(500)), "a takeover opened the running copy's list");
    }

    [Fact]
    public void An_open_list_event_this_user_may_not_open_counts_as_a_running_copy()
    {
        // An administrator copy's event, as its mutex in the test below.
        var handle = CreateLockedEvent(_event);
        try
        {
            Assert.Null(SingleInstance.Claim(_name, _event, TimeSpan.Zero));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [Fact]
    public void The_first_copy_gets_the_guard_and_a_second_does_not()
    {
        using var first = SingleInstance.Claim(_name, _event, TimeSpan.Zero);

        Assert.NotNull(first);
        Assert.Null(OnOwnThread(() => SingleInstance.Claim(_name, _event, TimeSpan.Zero)));
    }

    [Fact]
    public void A_copy_started_after_the_first_one_quit_gets_the_guard()
    {
        SingleInstance.Claim(_name, _event, TimeSpan.Zero)!.Dispose();

        using var next = SingleInstance.Claim(_name, _event, TimeSpan.Zero);

        Assert.NotNull(next);
    }

    [Fact]
    public async Task A_copy_taking_over_waits_until_the_running_copy_has_quit()
    {
        using var running = new HeldElsewhere(_name);

        var takeover = Task.Run(() =>
        {
            using var guard = SingleInstance.Claim(_name, _event, TimeSpan.FromSeconds(10));
            return guard is not null;
        });
        await Task.Delay(300);
        Assert.False(takeover.IsCompleted, "the takeover went ahead while the other copy still ran");
        running.Release();

        Assert.True(await takeover);
    }

    [Fact]
    public void A_copy_taking_over_gives_up_when_the_running_copy_does_not_quit_in_time()
    {
        using var running = new HeldElsewhere(_name);

        Assert.Null(OnOwnThread(() => SingleInstance.Claim(_name, _event, TimeSpan.FromMilliseconds(200))));
    }

    [Fact]
    public void A_copy_that_ended_without_letting_go_does_not_block_the_next_one()
    {
        // A thread that ends holding a mutex abandons it, as a copy that crashes does.
        var holder = new Thread(() => SingleInstance.Claim(_name, _event, TimeSpan.Zero));
        holder.Start();
        holder.Join();

        using var next = SingleInstance.Claim(_name, _event, TimeSpan.FromSeconds(5));

        Assert.NotNull(next);
    }

    [Fact]
    public void A_guard_this_user_may_not_open_counts_as_a_running_copy()
    {
        // A copy running as administrator makes the mutex with an administrators-only DACL, which
        // a normal copy can't open. Here a DACL denying everyone stands in for it.
        var handle = CreateLockedMutex(_name);
        try
        {
            Assert.Null(SingleInstance.Claim(_name, _event, TimeSpan.Zero));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static T OnOwnThread<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>The guard held by another copy (another thread) until <see cref="Release"/>.</summary>
    private sealed class HeldElsewhere : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new(), _release = new();
        private readonly Thread _thread;

        public HeldElsewhere(string name)
        {
            _thread = new Thread(() =>
            {
                using var guard = SingleInstance.Claim(name, name + ".OpenList", TimeSpan.Zero) ?? throw new InvalidOperationException("Not acquired.");
                _acquired.Set();
                _release.Wait();
            });
            _thread.Start();
            _acquired.Wait();
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
        }
    }

    private static nint CreateLockedMutex(string name) => WithLockedDescriptor(a => CreateMutex(ref a, false, name));

    private static nint CreateLockedEvent(string name) => WithLockedDescriptor(a => CreateEvent(ref a, false, false, name));

    /// <summary>Create a named object with a DACL that denies everyone, standing in for an administrator copy's.</summary>
    private static nint WithLockedDescriptor(Func<SECURITY_ATTRIBUTES, nint> create)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(D;;GA;;;WD)", 1, out var descriptor, out _))
            throw new Win32Exception();
        try
        {
            var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = descriptor };
            var handle = create(attributes);
            return handle != 0 ? handle : throw new Win32Exception();
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public nint lpSecurityDescriptor; public int bInheritHandle; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW")]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateMutexW")]
    private static extern nint CreateMutex(ref SECURITY_ATTRIBUTES attributes, bool initialOwner, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateEventW")]
    private static extern nint CreateEvent(ref SECURITY_ATTRIBUTES attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
