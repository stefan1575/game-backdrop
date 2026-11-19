using System.ComponentModel;
using System.Runtime.InteropServices;
using GameBackdrop.Core.Startup;

namespace GameBackdrop.Windows.Tests;

/// <summary>
/// <c>--uninstall</c>'s view of the running copy, against real named objects: a guard claimed on a
/// thread of its own stands in for the running copy, as in <see cref="SingleInstanceTests"/>.
/// </summary>
public sealed class RunningCopyTests
{
    private readonly string _name = $@"Local\GameBackdrop.Tests.{Guid.NewGuid():N}";
    private string OpenList => _name + ".OpenList";

    private RunningCopy Copy(TimeSpan wait) => new(_name, SingleInstance.QuitEventNameFor(_name), wait);

    [Fact]
    public void With_no_copy_running_there_is_none_and_quitting_it_is_done()
    {
        var copy = Copy(TimeSpan.FromSeconds(1));

        Assert.Equal(RunningCopyState.None, copy.Find());
        Assert.True(copy.Quit());
    }

    [Fact]
    public void A_running_copy_is_asked_to_quit_and_waited_for_until_it_lets_go()
    {
        using var running = new RunningElsewhere(_name, OpenList);
        var copy = Copy(TimeSpan.FromSeconds(10));

        Assert.Equal(RunningCopyState.Running, copy.Find());
        Assert.True(copy.Quit());
        Assert.True(running.QuitAsked);
        Assert.True(running.LetGo, "Quit returned before the running copy let go");
        Assert.Equal(RunningCopyState.None, copy.Find());
    }

    [Fact]
    public void A_running_copy_that_does_not_quit_in_time_is_not_quit()
    {
        using var running = new RunningElsewhere(_name, OpenList, ignoreQuit: true);

        Assert.False(Copy(TimeSpan.FromMilliseconds(300)).Quit());
    }

    [Fact]
    public void A_copy_with_no_quit_event_to_ask_is_waited_for_only_a_moment()
    {
        // A copy from before the quit event holds the guard and will never be asked to let go.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: true, _name);
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        try
        {
            var copy = Copy(TimeSpan.FromSeconds(60));
            var clock = System.Diagnostics.Stopwatch.StartNew();

            Assert.Equal(RunningCopyState.Running, copy.Find());
            Assert.False(copy.Quit());
            Assert.True(clock.Elapsed < RunningCopy.UnaskedWait + TimeSpan.FromSeconds(2), $"waited {clock.Elapsed}");
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void A_quit_event_this_user_may_not_open_is_a_copy_out_of_reach()
    {
        // A copy running as administrator makes its objects with an administrators-only DACL; a
        // DACL denying everyone stands in for it, as in SingleInstanceTests.
        using var running = new RunningElsewhere(_name, OpenList, ignoreQuit: true);
        var locked = CreateLockedEvent(SingleInstance.QuitEventNameFor(_name + ".Locked"));
        try
        {
            var copy = new RunningCopy(_name, SingleInstance.QuitEventNameFor(_name + ".Locked"), TimeSpan.FromMilliseconds(100));

            Assert.Equal(RunningCopyState.OutOfReach, copy.Find());
            Assert.False(copy.Quit());
        }
        finally
        {
            CloseHandle(locked);
        }
    }

    /// <summary>
    /// The running copy on a thread of its own: it claims the guard, and when asked to quit lets
    /// go, as the tray app does once it has put its games back.
    /// </summary>
    private sealed class RunningElsewhere : IDisposable
    {
        private readonly ManualResetEventSlim _claimed = new(), _quit = new(), _stop = new();
        private readonly Thread _thread;
        private volatile bool _letGo;

        public RunningElsewhere(string name, string openList, bool ignoreQuit = false)
        {
            _thread = new Thread(() =>
            {
                var guard = SingleInstance.Claim(name, openList, TimeSpan.Zero) ?? throw new InvalidOperationException("Not claimed.");
                using (guard.OnQuit(new SynchronizationContext(), _quit.Set))
                {
                    _claimed.Set();
                    if (ignoreQuit) _stop.Wait();
                    else WaitHandle.WaitAny([_quit.WaitHandle, _stop.WaitHandle]);
                }
                Thread.Sleep(200); // putting its games back
                _letGo = true;
                guard.Dispose();
            });
            _thread.Start();
            _claimed.Wait();
        }

        public bool QuitAsked => _quit.IsSet;
        public bool LetGo => _letGo;

        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
        }
    }

    private static nint CreateLockedEvent(string name)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(D;;GA;;;WD)", 1, out var descriptor, out _))
            throw new Win32Exception();
        try
        {
            var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = descriptor };
            var handle = CreateEvent(ref attributes, false, false, name);
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
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateEventW")]
    private static extern nint CreateEvent(ref SECURITY_ATTRIBUTES attributes, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
