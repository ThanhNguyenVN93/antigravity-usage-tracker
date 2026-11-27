using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AntigravityUsageTracker.Models;

namespace AntigravityUsageTracker.Services;

// Reads server-side quota from `agy.exe -p "/usage"` (tab-separated: group, limit, "NN%", ISO-UTC reset).
public static class AgyQuotaService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string CachePath => Path.Combine(TrackerPaths.DataDir, "agy-quota.json");

    public static List<AgyQuotaItem> ParseOutput(string? output)
    {
        var items = new List<AgyQuotaItem>();
        if (string.IsNullOrWhiteSpace(output)) return items;

        foreach (var raw in output.Split('\n'))
        {
            var cols = raw.TrimEnd('\r').Split('\t');
            if (cols.Length < 3) continue;

            string group = cols[0].Trim(), limit = cols[1].Trim();
            string pct = cols[2].Trim().TrimEnd('%').Trim();
            if (group.Length == 0 || limit.Length == 0) continue;
            if (!double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out double remaining)
                || double.IsNaN(remaining) || double.IsInfinity(remaining)) continue;

            DateTime? reset = null;
            if (cols.Length >= 4 && DateTime.TryParse(cols[3].Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                reset = DateTime.SpecifyKind(dt, DateTimeKind.Utc);

            items.Add(new AgyQuotaItem(group, limit, Math.Clamp(remaining, 0, 100), reset));
        }
        return items;
    }

    public static string? FindAgyExe()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidate = Path.Combine(local, "agy", "bin", "agy.exe");
            if (File.Exists(candidate)) return candidate;

            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var name in new[] { "agy.exe", "agy.cmd" })
                {
                    try
                    {
                        var p = Path.Combine(dir.Trim().Trim('"'), name);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { ErrorLog.Write("AgyQuotaService.FindAgyExe", ex); }
        return null;
    }

    /// <summary>Runs agy and persists the result. Returns null when agy is missing, fails, or yields no rows.</summary>
    public static async Task<AgyQuotaSnapshot?> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            var exe = FindAgyExe();
            if (exe == null) return null;

            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("/usage");
            psi.ArgumentList.Add("--print-timeout");
            psi.ArgumentList.Add("15s");

            using var proc = Process.Start(psi);
            if (proc == null) return null;
            proc.StandardInput.Close();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            var stdout = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = proc.StandardError.ReadToEndAsync(cts.Token);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
                var text = await stdout;
                await stderr;

                var items = ParseOutput(text);
                if (items.Count == 0) return null;

                var snap = new AgyQuotaSnapshot { FetchedUtc = DateTime.UtcNow, Items = items };
                SaveCached(snap);
                return snap;
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return null;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("AgyQuotaService.FetchAsync", ex);
            return null;
        }
    }

    public static AgyQuotaSnapshot? LoadCached(string? path = null)
    {
        path ??= CachePath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AgyQuotaSnapshot>(File.ReadAllText(path), Options);
                if (s is { Items.Count: > 0 }) return s;
            }
        }
        catch (Exception ex) { ErrorLog.Write("AgyQuotaService.LoadCached", ex); }
        return null;
    }

    public static void SaveCached(AgyQuotaSnapshot snapshot, string? path = null)
    {
        path ??= CachePath;
        try { AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot, Options)); }
        catch (Exception ex) { ErrorLog.Write("AgyQuotaService.SaveCached", ex); }
    }
}
