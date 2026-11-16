using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GameBackdrop.Core.Startup;

namespace GameBackdrop.Windows;

/// <summary>What a shortcut starts.</summary>
public sealed record ShortcutTarget(string ExePath, string Arguments);

/// <summary>
/// <c>Game Backdrop.lnk</c> in a Startup folder, written through the shell's own ShellLink object
/// (IShellLinkW), so Explorer and Task Manager's Startup apps treat it like any other.
/// </summary>
public sealed class StartupShortcut(string folder, string exePath, string arguments) : IStartupShortcut
{
    public const string FileName = "Game Backdrop.lnk";

    /// <summary>The signed-in user's Startup folder (<c>shell:startup</c>).</summary>
    public static string UserStartupFolder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public string FilePath { get; } = Path.Combine(folder, FileName);

    public bool Exists => File.Exists(FilePath);

    /// <summary>Write the shortcut to a temp file beside the real one, then swap it in.</summary>
    public void Create()
    {
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(exePath);
                link.SetArguments(arguments);
                link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
                link.SetDescription("Game Backdrop");
                ((IPersistFile)link).Save(temp, fRemember: false);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public void Remove()
    {
        // File.Delete is already a no-op for a file that isn't there.
        if (Directory.Exists(folder)) File.Delete(FilePath);
    }

    /// <summary>What the shortcut starts, or null when there's no shortcut.</summary>
    public ShortcutTarget? Read()
    {
        if (!Exists) return null;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(FilePath, 0 /* STGM_READ */);
            var path = new StringBuilder(32768);
            link.GetPath(path, path.Capacity, 0, 0x4 /* SLGP_RAWPATH */);
            var args = new StringBuilder(32768);
            link.GetArguments(args, args.Capacity);
            return new(path.ToString(), args.ToString());
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
