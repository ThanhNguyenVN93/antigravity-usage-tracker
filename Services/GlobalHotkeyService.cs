using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AntigravityUsageTracker.Services;

// Ctrl+Alt+A (id 1) toggles the HUD, Ctrl+Alt+S (id 2) opens Stats.
public sealed class GlobalHotkeyService : IDisposable
{
    public const int ToggleHudId = 1;
    public const int ShowStatsId = 2;

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _toggleOk, _statsOk;

    public event Action<int>? HotkeyPressed;

    public bool IsStarted => _source != null;

    /// <summary>Registers both hotkeys. Returns true if at least one was registered.</summary>
    public bool Start()
    {
        if (_source != null) return true;
        try
        {
            var p = new HwndSourceParameters("AntigravityUsageTrackerHotkeys")
            {
                ParentWindow = HwndMessage, Width = 0, Height = 0, WindowStyle = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);
            uint mods = MOD_CONTROL | MOD_ALT | MOD_NOREPEAT;
            _toggleOk = RegisterHotKey(_source.Handle, ToggleHudId, mods, 0x41);
            _statsOk = RegisterHotKey(_source.Handle, ShowStatsId, mods, 0x53);
            return _toggleOk || _statsOk;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("GlobalHotkeyService.Start", ex);
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_source == null) return;
            if (_toggleOk) UnregisterHotKey(_source.Handle, ToggleHudId);
            if (_statsOk) UnregisterHotKey(_source.Handle, ShowStatsId);
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch (Exception ex) { ErrorLog.Write("GlobalHotkeyService.Stop", ex); }
        finally { _source = null; _toggleOk = _statsOk = false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            try { HotkeyPressed?.Invoke(wParam.ToInt32()); }
            catch (Exception ex) { ErrorLog.Write("GlobalHotkeyService.Handler", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Stop();
}
