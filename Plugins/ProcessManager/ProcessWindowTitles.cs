using System.Runtime.InteropServices;

namespace Lertaro.Plugins.ProcessManager;

// Split out of ProcessManagerInstantProvider purely to keep that file under the repo's per-file line
// limit: this is the Win32 window-title snapshot (visible top-level windows grouped by owning pid, with
// bounded WM_GETTEXT reads). It has no state of its own; the provider calls it once per search.
internal static class ProcessWindowTitles
{
    // CharSet.Unicode is required, not cosmetic: without it this binds to SendMessageTimeoutA, whose
    // WM_GETTEXT path writes ANSI bytes that PtrToStringUni then decodes as mojibake for any title
    // outside 7-bit ASCII (same reasoning WindowEnumerator documents for its own declaration).
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMs, out IntPtr result);

    private const uint WM_GETTEXTLENGTH = 0x000E;
    private const uint WM_GETTEXT = 0x000D;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint GetTextTimeoutMs = 150;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    internal static Dictionary<int, List<string>> GetVisibleWindowTitles()
    {
        var titles = new Dictionary<int, List<string>>();
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var title = GetWindowTitle(hWnd);
                if (string.IsNullOrWhiteSpace(title)) return true;

                GetWindowThreadProcessId(hWnd, out var processId);
                if (processId != 0)
                {
                    var pid = (int)processId;
                    if (!titles.TryGetValue(pid, out var processTitles))
                    {
                        processTitles = [];
                        titles.Add(pid, processTitles);
                    }

                    if (!processTitles.Contains(title, StringComparer.Ordinal)) processTitles.Add(title);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // A failed title snapshot only removes title matching; processes remain searchable by name and PID.
        }

        return titles;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        // Cross-process GetWindowText/GetWindowTextLength send WM_GETTEXT to the owning thread and
        // block with no deadline -- one hung window anywhere froze this provider on every keystroke
        // (this runs on the UI thread inside GetInstantResults). SendMessageTimeout with
        // SMTO_ABORTIFHUNG bounds each read, the same defense WindowEnumerator uses.
        var titleLength = SafeGetWindowTextLength(hWnd);
        if (titleLength <= 0)
            return string.Empty;

        return SafeGetWindowText(hWnd, titleLength);
    }

    private static int SafeGetWindowTextLength(IntPtr hWnd) =>
        SendMessageTimeout(hWnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, GetTextTimeoutMs, out var result) == IntPtr.Zero
            ? 0
            : result.ToInt32();

    private static string SafeGetWindowText(IntPtr hWnd, int titleLength)
    {
        var capacity = titleLength + 1;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            if (SendMessageTimeout(hWnd, WM_GETTEXT, new IntPtr(capacity), buffer, SMTO_ABORTIFHUNG, GetTextTimeoutMs, out var result) == IntPtr.Zero)
                return string.Empty;
            return Marshal.PtrToStringUni(buffer, result.ToInt32()) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
