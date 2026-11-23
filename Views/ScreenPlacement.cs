using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace AntigravityUsageTracker.Views;

public static class ScreenPlacement
{
    // WPF work area (DIPs) of the screen containing the mouse cursor.
    public static Rect WorkAreaForCursor(Visual? visual)
    {
        try
        {
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var wa = screen.WorkingArea;
            double sx = 1, sy = 1;
            if (visual != null)
            {
                var dpi = VisualTreeHelper.GetDpi(visual);
                if (dpi.DpiScaleX > 0) sx = dpi.DpiScaleX;
                if (dpi.DpiScaleY > 0) sy = dpi.DpiScaleY;
            }
            return new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    // Bounds (DIPs) of the first visible ClaudeUsageTracker window that looks like its HUD.
    public static bool TryGetClaudeHudRect(Visual? visual, out Rect rect)
    {
        rect = Rect.Empty;
        try
        {
            var procs = Process.GetProcessesByName("ClaudeUsageTracker");
            var pids = new HashSet<uint>();
            foreach (var p in procs) { pids.Add((uint)p.Id); p.Dispose(); }
            if (pids.Count == 0) return false;

            NativeRect found = default;
            bool ok = false;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid) || !GetWindowRect(h, out var r)) return true;
                if (r.Right - r.Left < 200 || r.Bottom - r.Top < 120) return true;
                found = r;
                ok = true;
                return false;
            }, IntPtr.Zero);
            if (!ok) return false;

            double sx = 1, sy = 1;
            if (visual != null)
            {
                var dpi = VisualTreeHelper.GetDpi(visual);
                if (dpi.DpiScaleX > 0) sx = dpi.DpiScaleX;
                if (dpi.DpiScaleY > 0) sy = dpi.DpiScaleY;
            }
            rect = new Rect(found.Left / sx, found.Top / sy,
                (found.Right - found.Left) / sx, (found.Bottom - found.Top) / sy);
            return true;
        }
        catch { return false; }
    }

    // Saved coordinates may point at a monitor that was unplugged.
    public static bool IsOnAnyScreen(double left, double top, double width, double height)
    {
        try
        {
            var rect = new System.Drawing.Rectangle(
                (int)left, (int)top, (int)Math.Max(width, 1), (int)Math.Max(height, 1));
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(rect)) return true;
            return false;
        }
        catch { return true; }
    }
}
