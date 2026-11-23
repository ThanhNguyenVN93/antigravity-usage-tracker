using System.Globalization;
using System.Text.Json;
using AntigravityUsageTracker.Models;

namespace AntigravityUsageTracker.Services;

// Long-term per-day totals, kept after transcripts are pruned. Stored oldest-first.
public sealed class DailyHistoryService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _file;

    public DailyHistoryService(string? file = null)
    {
        _file = file ?? Path.Combine(TrackerPaths.DataDir, "history-daily.json");
    }

    public List<DailyHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var list = JsonSerializer.Deserialize<List<DailyHistoryEntry>>(File.ReadAllText(_file));
                if (list != null) return list.OrderBy(e => e.Date, StringComparer.Ordinal).ToList();
            }
        }
        catch (Exception ex) { ErrorLog.Write("DailyHistoryService.Load", ex); }
        return new List<DailyHistoryEntry>();
    }

    public List<DailyHistoryEntry> MergeAndSave(IReadOnlyList<UsageRecord> records)
    {
        var merged = Load().ToDictionary(e => e.Date);
        bool changed = false;

        var fresh = records.Where(r => !(r.IsError && r.TotalTokens == 0))
            .GroupBy(r => r.TimeUtc.ToLocalTime().Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Select(g => new DailyHistoryEntry(g.Key, g.Sum(r => r.TotalTokens),
                g.Sum(r => CostService.EstimateUsd(r)), g.Count()));

        foreach (var e in fresh)
        {
            // Transcripts can be pruned, so never let a day shrink.
            if (merged.TryGetValue(e.Date, out var old) && old.Tokens > e.Tokens) continue;
            if (old != e) changed = true;
            merged[e.Date] = e;
        }

        var result = merged.Values.OrderBy(e => e.Date, StringComparer.Ordinal).ToList();
        if (changed || !File.Exists(_file))
        {
            try { AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(result, Options)); }
            catch (Exception ex) { ErrorLog.Write("DailyHistoryService.Save", ex); }
        }
        return result;
    }
}
