using System.Runtime.InteropServices;

namespace Lertaro.PluginSdk.Helpers;

// Split out of ShellPathHelper purely to keep that file under the repo's per-file line limit: this is
// the virtual shell-path half (resolving a shell: virtual path that has no physical folder, and naming
// one for display). No state of its own beyond the borrowed P/Invoke declarations it needs.
internal static class ShellVirtualPaths
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfo", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName([MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

    private const uint SHGFI_DISPLAYNAME = 0x000000200;
    private const uint SHGFI_PIDL = 0x000000008;
    private const int SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000);
    private const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);

    public static string TryResolveVirtualPath(string path)
    {
        if (!ShellPathHelper.IsVirtualShellPath(path)) return path;

        var token = path.Trim();
        var pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(token, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return path;

            var physical = NameFromPidl(pidl, SIGDN_FILESYSPATH);
            if (!string.IsNullOrEmpty(physical))
                return physical;

            var canonical = NameFromPidl(pidl, SIGDN_DESKTOPABSOLUTEPARSING);
            return string.IsNullOrEmpty(canonical) ? path : canonical;
        }
        catch
        {
            return path;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pidl);
        }
    }

    /// <summary>The shell's name for one item id, in the requested form, or null when it has none.</summary>
    /// <remarks>
    /// SIGDN_FILESYSPATH fails with ERROR_FILE_NOT_FOUND for an item that lives only in the shell
    /// namespace, which is a normal answer here rather than an error, hence the null.
    /// </remarks>
    private static string? NameFromPidl(IntPtr pidl, int sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out var value) != 0 || value == IntPtr.Zero)
            return null;

        try
        {
            return Marshal.PtrToStringUni(value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    /// <summary>
    /// Dynamically retrieves the localized user-friendly display name of a Windows shell virtual folder.
    /// </summary>
    public static string GetVirtualFolderDisplayName(string path, string fallback)
    {
        if (string.IsNullOrEmpty(path)) return fallback;
        var pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
            {
                var shfi = new SHFILEINFO();
                var res = SHGetFileInfoPidl(pidl, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_DISPLAYNAME | SHGFI_PIDL);
                if (res != IntPtr.Zero && !string.IsNullOrEmpty(shfi.szDisplayName))
                    return shfi.szDisplayName.Trim();
            }
        }
        catch { }
        finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
        return fallback;
    }

}
