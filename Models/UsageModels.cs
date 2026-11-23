using System.Text.Json.Serialization;

namespace AntigravityUsageTracker.Models;

public sealed record UsageRecord(
    DateTime TimeUtc,
    string Model,
    long InputTokens,
    long CacheReadTokens,
    long OutputTokens,
    string SessionId,
    string[]? Tools = null,
    bool IsError = false,
    string? ErrorSnippet = null)
{
    public long TotalTokens => InputTokens + CacheReadTokens + OutputTokens;
    public long ContextTokens => InputTokens + CacheReadTokens;
}

public sealed record AgyQuotaItem(string Group, string LimitName, double RemainingPercent, DateTime? ResetTimeUtc)
{
    [JsonIgnore] public double UsedPercent => Math.Clamp(100.0 - RemainingPercent, 0, 100);
}

public sealed class AgyQuotaSnapshot
{
    public DateTime FetchedUtc { get; set; }
    public List<AgyQuotaItem> Items { get; set; } = new();

    private AgyQuotaItem? Find(string group, string limit) =>
        Items.FirstOrDefault(i =>
            i.Group.Contains(group, StringComparison.OrdinalIgnoreCase)
            && i.LimitName.Contains(limit, StringComparison.OrdinalIgnoreCase));

    [JsonIgnore] public AgyQuotaItem? Gemini5h => Find("Gemini Models", "Five Hour");
    [JsonIgnore] public AgyQuotaItem? GeminiWeekly => Find("Gemini Models", "Weekly");
    [JsonIgnore] public AgyQuotaItem? ArgonWeekly => Find("Argon", "Weekly");
}

public sealed record ToolStat(string Tool, int Count);

public sealed record DailyHistoryEntry(string Date, long Tokens, double CostUsd, int Requests);

public enum StatsRange { Today, Days7, Days30, All }

public sealed record ModelStat(string Model, long Total, long Input, long CacheRead, long Output, int Requests);

public sealed record DayStat(DateTime Date, long Total, int Requests);

public sealed record HourStat(DateTime HourLocal, long Total, int Requests);

public sealed record SessionStat(string SessionId, long Total, int Requests, DateTime LastUtc);

public sealed record ErrorEntry(DateTime TimeUtc, string SessionId, string Snippet);

public sealed class UsageSummary
{
    public DateTime GeneratedUtc { get; set; }
    public int RecordCount { get; set; }
    public bool HasData => RecordCount > 0;

    public long Total5h { get; set; }
    public long Total24h { get; set; }
    public long Total7d { get; set; }

    /// <summary>Earliest record inside the rolling 5h / 7d window; the window drops it at +5h / +7d.</summary>
    public DateTime? OldestIn5hUtc { get; set; }
    public DateTime? OldestIn7dUtc { get; set; }

    public string LatestModel { get; set; } = "";
    public DateTime? LatestTimeUtc { get; set; }
    public long LatestContextTokens { get; set; }
    public string LatestSessionId { get; set; } = "";

    /// <summary>Tokens per hour (last 1h if active, otherwise 5h average).</summary>
    public double BurnRatePerHour { get; set; }

    public IReadOnlyList<ModelStat> PerModel { get; set; } = Array.Empty<ModelStat>();
    public IReadOnlyList<DayStat> PerDay { get; set; } = Array.Empty<DayStat>();
    public IReadOnlyList<SessionStat> TopSessions { get; set; } = Array.Empty<SessionStat>();

    /// <summary>24 local-hour buckets in chronological order (oldest first).</summary>
    public IReadOnlyList<HourStat> PerHour24 { get; set; } = Array.Empty<HourStat>();

    /// <summary>cache / (input + cache) over 7 days, 0-100.</summary>
    public double CacheHitPercent7d { get; set; }

    /// <summary>Null when no budget is configured.</summary>
    public double? Budget5hPercent { get; set; }
    public double? Budget7dPercent { get; set; }

    public double EstimatedCost24hUsd { get; set; }
    public double EstimatedCost7dUsd { get; set; }
    public double EstimatedCostRangeUsd { get; set; }

    /// <summary>Hours until the 5h budget is exhausted at the current burn rate; null when unknown.</summary>
    public double? ProjectedHoursToBudget { get; set; }

    public int ErrorCount24h { get; set; }
    public int ErrorCount7d { get; set; }

    public IReadOnlyList<ToolStat> TopTools { get; set; } = Array.Empty<ToolStat>();

    public IReadOnlyList<ErrorEntry> RecentErrors { get; set; } = Array.Empty<ErrorEntry>();

    public long RangeTotalTokens { get; set; }
    public int RangeRequests { get; set; }

    public double MaxBudgetPercent => Math.Max(Budget5hPercent ?? 0, Budget7dPercent ?? 0);
}
