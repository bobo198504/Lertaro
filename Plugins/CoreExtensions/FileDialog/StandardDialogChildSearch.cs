using System.Runtime.InteropServices;
using System.Text;

namespace Lertaro.Plugins.CoreExtensions.FileDialog;

// Split out of StandardFileDialogAdapter purely to keep that file under the repo's per-file line limit:
// the child-window searches that locate a dialog's breadcrumb bar, a named control and the edit box.
// It has no state of its own -- every entry point takes the parent window handle it searches under.
internal static class StandardDialogChildSearch
{
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hWnd);

    internal static IntPtr FindBreadcrumbParent(IntPtr parent)
    {
        if (parent == IntPtr.Zero) return IntPtr.Zero;
        var result = IntPtr.Zero;
        EnumChildWindows(parent, (childHwnd, lParam) =>
        {
            var classNameSb = new StringBuilder(256);
            GetClassName(childHwnd, classNameSb, classNameSb.Capacity);
            if (classNameSb.ToString().Equals("Breadcrumb Parent", StringComparison.OrdinalIgnoreCase))
            {
                result = childHwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static IntPtr FindDescendant(IntPtr parent, string className, int controlId)
    {
        if (parent == IntPtr.Zero) return IntPtr.Zero;
        var result = IntPtr.Zero;
        EnumChildWindows(parent, (childHwnd, lParam) =>
        {
            var classNameSb = new StringBuilder(256);
            GetClassName(childHwnd, classNameSb, classNameSb.Capacity);
            if (classNameSb.ToString().Equals(className, StringComparison.OrdinalIgnoreCase) && GetDlgCtrlID(childHwnd) == controlId)
            {
                result = childHwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    internal static IntPtr FindSubEditBox(IntPtr parent)
    {
        if (parent == IntPtr.Zero) return IntPtr.Zero;
        var result = IntPtr.Zero;
        EnumChildWindows(parent, (childHwnd, lParam) =>
        {
            var classNameSb = new StringBuilder(256);
            GetClassName(childHwnd, classNameSb, classNameSb.Capacity);
            if (classNameSb.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase))
            {
                result = childHwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
