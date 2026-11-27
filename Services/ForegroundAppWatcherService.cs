using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace AntigravityUsageTracker.Services;

/// <summary>Polls the foreground window and reports whether it belongs to Antigravity or a terminal/editor running agy.</summary>
public sealed class ForegroundAppWatcherService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private static readonly HashSet<string> TerminalsAndEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "windowsterminal", "pwsh", "powershell", "cmd", "wt"
    };

    private readonly DispatcherTimer _timer;
    private readonly int _selfPid = Environment.ProcessId;
    private bool? _last;

    public event Action<bool>? ForegroundChanged;

    public ForegroundAppWatcherService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _last = null;
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Poll()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            // our own windows (HUD/stats/settings) must not count as "left the relevant apps"
            if (pid == _selfPid) return;

            using var p = Process.GetProcessById((int)pid);
            bool relevant = IsRelevantWindow(hwnd, p.ProcessName, (int)pid);
            if (_last == relevant) return;
            _last = relevant;
            ForegroundChanged?.Invoke(relevant);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ForegroundAppWatcher.Poll", ex);
        }
    }

    private static bool IsRelevantWindow(IntPtr hwnd, string processName, int pid)
    {
        if (processName.Equals("antigravity", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("agy", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!TerminalsAndEditors.Contains(processName))
            return false;

        var sb = new StringBuilder(256);
        string title = GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        if (title.Contains("claude", StringComparison.OrdinalIgnoreCase))
            return false;
        if (title.Contains("antigravity", StringComparison.OrdinalIgnoreCase)
            || title.Contains("agy", StringComparison.OrdinalIgnoreCase))
            return true;

        return HasDescendantProcess(pid, "agy.exe");
    }

    private static bool HasDescendantProcess(int rootPid, string targetExe)
    {
        IntPtr snap = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return false;
        try
        {
            var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref pe)) return false;
            var children = new Dictionary<int, List<int>>();
            var names = new Dictionary<int, string>();
            do
            {
                int id = (int)pe.th32ProcessID;
                int parent = (int)pe.th32ParentProcessID;
                names[id] = pe.szExeFile ?? "";
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = new List<int>();
                list.Add(id);
            } while (Process32Next(snap, ref pe));

            var q = new Queue<int>();
            q.Enqueue(rootPid);
            var seen = new HashSet<int> { rootPid };
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (!children.TryGetValue(cur, out var list)) continue;
                foreach (int child in list)
                {
                    if (!seen.Add(child)) continue;
                    if (names.TryGetValue(child, out var exe)
                        && exe.Equals(targetExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                    q.Enqueue(child);
                }
            }
            return false;
        }
        finally { CloseHandle(snap); }
    }

    public void Dispose() => _timer.Stop();
}
