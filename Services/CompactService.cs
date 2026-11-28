using System.Text.RegularExpressions;

namespace AntigravityUsageTracker.Services;

public sealed record CompactResult(bool Ok, string MessageKey, string? Detail = null);

// Runs `agy --conversation <id> -p /compact` in the background and remembers the active session id.
public static class CompactService
{
    // Session ids come from brain folder names (normally GUIDs); reject anything else before
    // it reaches a child process's argument list.
    private static readonly Regex SessionIdRx = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

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
        sessionId = sessionId.Trim();
        if (!SessionIdRx.IsMatch(sessionId)) return new CompactResult(false, "compact.failed", "invalid session id");
        try
        {
            string? agy = AgyQuotaService.FindAgyExe();
            if (agy == null) return new CompactResult(false, "compact.noAgy");

            using var p = HiddenProcess.StartRedirected(agy,
                ["--conversation", sessionId, "-p", "/compact", "--print-timeout", "30s"]);
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
                p.Kill();
                try { await Task.WhenAll(stdout, stderr); } catch { }
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
