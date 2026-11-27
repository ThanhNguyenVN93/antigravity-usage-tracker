using System.Diagnostics;

namespace AntigravityUsageTracker.Services;

public sealed record CompactResult(bool Ok, string MessageKey, string? Detail = null);

// Runs `agy --conversation <id> -p /compact` in the background and remembers the active session id.
public static class CompactService
{
    public static string ActiveSessionPath => Path.Combine(TrackerPaths.DataDir, "active-session.txt");

    public static void WriteActiveSession(string sessionId, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        path ??= ActiveSessionPath;
        try { AtomicFile.WriteAllText(path, sessionId.Trim()); }
        catch (Exception ex) { ErrorLog.Write("CompactService.WriteActiveSession", ex); }
    }

    public static string ReadActiveSession(string? path = null)
    {
        path ??= ActiveSessionPath;
        try
        {
            if (File.Exists(path)) return File.ReadAllText(path).Trim();
        }
        catch (Exception ex) { ErrorLog.Write("CompactService.ReadActiveSession", ex); }
        return "";
    }

    public static async Task<CompactResult> RunCompactAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return new CompactResult(false, "compact.noSession");
        try
        {
            string? agy = AgyQuotaService.FindAgyExe();
            if (agy == null) return new CompactResult(false, "compact.noAgy");

            var psi = new ProcessStartInfo(agy)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--conversation");
            psi.ArgumentList.Add(sessionId.Trim());
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("/compact");
            psi.ArgumentList.Add("--print-timeout");
            psi.ArgumentList.Add("30s");

            using var p = Process.Start(psi);
            if (p == null) return new CompactResult(false, "compact.failed", "start");
            p.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(35));

            var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = p.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await p.WaitForExitAsync(cts.Token);
                await stdout;
                await stderr;
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return new CompactResult(false, "compact.failed", "timeout");
            }

            return p.ExitCode == 0
                ? new CompactResult(true, "compact.done")
                : new CompactResult(false, "compact.failed", $"exit {p.ExitCode}");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("CompactService.RunCompactAsync", ex);
            return new CompactResult(false, "compact.failed", ex.Message);
        }
    }
}
