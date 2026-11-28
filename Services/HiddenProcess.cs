using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AntigravityUsageTracker.Services;

/// <summary>
/// Launches child processes with CREATE_NO_WINDOW *and* an explicit SW_HIDE hint written into
/// STARTUPINFO. System.Diagnostics.Process (CreateNoWindow = true) only ever sets the
/// CREATE_NO_WINDOW creation flag — there is no ProcessStartInfo property that also sets
/// STARTUPINFO.dwFlags/wShowWindow (see dotnet/runtime#71515). On builds where Windows Terminal
/// is the registered default console host, CREATE_NO_WINDOW alone can still let a console
/// window flash on screen for a frame before it is torn down. Adding STARTF_USESHOWWINDOW +
/// SW_HIDE closes that gap for the console hosts that honor it.
/// </summary>
internal static class HiddenProcess
{
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint STARTF_USESHOWWINDOW = 0x00000001;
    private const uint STARTF_USESTDHANDLES = 0x00000100;
    private const short SW_HIDE = 0;
    private const uint HANDLE_FLAG_INHERIT = 1;

    // Only StartRedirected creates inheritable handles. Serializing its create-pipes /
    // CreateProcess / close-child-ends window against itself stops an unrelated concurrent
    // call (e.g. AgyQuotaService and CompactService racing) from inheriting each other's
    // pipe handles, which would otherwise wedge a ReadToEndAsync waiting for an EOF that
    // never comes until the other child exits.
    private static readonly object InheritanceLock = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle hProcess, uint uExitCode);

    public sealed class Handle : IDisposable
    {
        private readonly SafeProcessHandle _hProcess;

        public int Id { get; }
        public StreamWriter StandardInput { get; }
        public StreamReader StandardOutput { get; }
        public StreamReader StandardError { get; }

        public int ExitCode
        {
            get
            {
                if (!GetExitCodeProcess(_hProcess, out uint code))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)code);
            }
        }

        internal Handle(SafeProcessHandle hProcess, int id, StreamWriter stdin, StreamReader stdout, StreamReader stderr)
        {
            _hProcess = hProcess;
            Id = id;
            StandardInput = stdin;
            StandardOutput = stdout;
            StandardError = stderr;
        }

        /// <summary>Waits for the process to exit, honoring cancellation without blocking a thread-pool thread.</summary>
        public Task WaitForExitAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waitHandle = new ManualResetEvent(false)
            {
                SafeWaitHandle = new SafeWaitHandle(_hProcess.DangerousGetHandle(), ownsHandle: false)
            };

            RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle, (_, _) => tcs.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
            CancellationTokenRegistration ctReg = ct.CanBeCanceled
                ? ct.Register(() => tcs.TrySetCanceled(ct))
                : default;

            return tcs.Task.ContinueWith(t =>
            {
                registration.Unregister(null);
                waitHandle.Dispose();
                ctReg.Dispose();
                return t;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
        }

        public void Kill(bool _ = true)
        {
            try { TerminateProcess(_hProcess, 1); } catch { }
        }

        public void Dispose()
        {
            try { StandardInput.Dispose(); } catch { }
            try { StandardOutput.Dispose(); } catch { }
            try { StandardError.Dispose(); } catch { }
            _hProcess.Dispose();
        }
    }

    /// <summary>Starts a hidden process with redirected stdin/stdout/stderr. Throws on failure.</summary>
    public static Handle StartRedirected(string fileName, IEnumerable<string> arguments)
    {
        lock (InheritanceLock)
        {
            SafeFileHandle? stdinChild = null, stdinParent = null, stdoutChild = null, stdoutParent = null,
                stderrChild = null, stderrParent = null;
            SafeProcessHandle? hProcess = null;
            try
            {
                (stdinChild, stdinParent) = CreateInheritablePipe(childReads: true);
                (stdoutChild, stdoutParent) = CreateInheritablePipe(childReads: false);
                (stderrChild, stderrParent) = CreateInheritablePipe(childReads: false);

                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    dwFlags = STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES,
                    wShowWindow = SW_HIDE,
                    hStdInput = stdinChild.DangerousGetHandle(),
                    hStdOutput = stdoutChild.DangerousGetHandle(),
                    hStdError = stderrChild.DangerousGetHandle(),
                };

                if (!CreateProcess(fileName, new StringBuilder(BuildCommandLine(fileName, arguments)), IntPtr.Zero, IntPtr.Zero,
                        true, CREATE_NO_WINDOW, IntPtr.Zero, null, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                CloseHandle(pi.hThread);
                hProcess = new SafeProcessHandle(pi.hProcess, ownsHandle: true);

                var stdin = new StreamWriter(new FileStream(stdinParent, FileAccess.Write)) { AutoFlush = true };
                stdinParent = null; // ownership moved into the FileStream
                var stdout = new StreamReader(new FileStream(stdoutParent, FileAccess.Read));
                stdoutParent = null;
                var stderr = new StreamReader(new FileStream(stderrParent, FileAccess.Read));
                stderrParent = null;

                var handle = new Handle(hProcess, pi.dwProcessId, stdin, stdout, stderr);
                hProcess = null; // ownership moved into Handle
                return handle;
            }
            finally
            {
                stdinChild?.Dispose();
                stdoutChild?.Dispose();
                stderrChild?.Dispose();
                stdinParent?.Dispose();
                stdoutParent?.Dispose();
                stderrParent?.Dispose();
                hProcess?.Dispose();
            }
        }
    }

    /// <summary>Starts a hidden, detached process with no redirected I/O (fire-and-forget).</summary>
    public static void StartDetached(string fileName, IEnumerable<string> arguments)
    {
        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            dwFlags = STARTF_USESHOWWINDOW,
            wShowWindow = SW_HIDE,
        };

        if (!CreateProcess(fileName, new StringBuilder(BuildCommandLine(fileName, arguments)), IntPtr.Zero, IntPtr.Zero,
                false, CREATE_NO_WINDOW, IntPtr.Zero, null, ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    }

    private static (SafeFileHandle childEnd, SafeFileHandle parentEnd) CreateInheritablePipe(bool childReads)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out var readHandle, out var writeHandle, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var (child, parent) = childReads ? (readHandle, writeHandle) : (writeHandle, readHandle);
        if (!SetHandleInformation(parent, HANDLE_FLAG_INHERIT, 0))
        {
            int err = Marshal.GetLastWin32Error();
            readHandle.Dispose();
            writeHandle.Dispose();
            throw new Win32Exception(err);
        }
        return (child, parent);
    }

    private static string BuildCommandLine(string fileName, IEnumerable<string> arguments)
    {
        var sb = new StringBuilder();
        AppendArgument(sb, fileName);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }
        return sb.ToString();
    }

    // Standard Win32 argv quoting (matches CommandLineToArgvW's escaping rules). Always quotes,
    // even when the argument has no spaces, so a value that happens to start with a shell
    // metacharacter can never be mistaken for a separate token or flag.
    private static void AppendArgument(StringBuilder sb, string arg)
    {
        sb.Append('"');
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(arg[i]);
            }
        }
        sb.Append('"');
    }
}
