using AntigravityUsageTracker.Models;

namespace AntigravityUsageTracker.Services;

// Pure logic: no I/O, no clock access.
public static class StatsService
{
    public static UsageSummary Compute(IReadOnlyList<UsageRecord> records, DateTime nowUtc,
        long budget5h = 0, long budget7d = 0, StatsRange range = StatsRange.Days7)
    {
        var s = new UsageSummary { GeneratedUtc = nowUtc };

        var nowLocal = nowUtc.ToLocalTime();
        var nowHour = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);
        var hourTotals = new long[24];
        var hourReqs = new int[24];
        long input7d = 0, cache7d = 0;

        var c1 = nowUtc.AddHours(-1);
        var c5 = nowUtc.AddHours(-5);
        var c24 = nowUtc.AddHours(-24);
        var c7 = nowUtc.AddDays(-7);
        DateTime cRange = range switch
        {
            StatsRange.Today => nowLocal.Date.ToUniversalTime(),
            StatsRange.Days30 => nowUtc.AddDays(-30),
            StatsRange.All => DateTime.MinValue,
            _ => c7
        };

        // Errors and tools are tracked on the raw list; token stats ignore empty error rows.
        var valid = new List<UsageRecord>(records.Count);
        var toolCounts = new Dictionary<string, int>();
        var errorsInRange = new List<UsageRecord>();
        foreach (var r in records)
        {
            if (r.IsError)
            {
                if (r.TimeUtc >= cRange) errorsInRange.Add(r);
                if (r.TimeUtc >= c24) s.ErrorCount24h++;
                if (r.TimeUtc >= c7) s.ErrorCount7d++;
            }
            if (r.Tools != null && r.TimeUtc >= cRange)
                foreach (var t in r.Tools)
                    toolCounts[t] = toolCounts.TryGetValue(t, out int n) ? n + 1 : 1;
            if (!(r.IsError && r.TotalTokens == 0)) valid.Add(r);
        }

        s.RecentErrors = errorsInRange.OrderByDescending(r => r.TimeUtc).Take(20)
            .Select(r => new ErrorEntry(r.TimeUtc, r.SessionId, r.ErrorSnippet ?? "ERROR")).ToList();
        s.RecordCount = valid.Count;
        s.TopTools = toolCounts.Select(kv => new ToolStat(kv.Key, kv.Value))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Tool, StringComparer.Ordinal).Take(10).ToList();

        if (valid.Count == 0)
        {
            s.PerHour24 = Enumerable.Range(0, 24).Select(i => new HourStat(nowHour.AddHours(i - 23), 0, 0)).ToList();
            return s;
        }

        long t1 = 0;
        var inWeek = new List<UsageRecord>();
        var inRange = new List<UsageRecord>();
        UsageRecord latest = valid[0];

        foreach (var r in valid)
        {
            if (r.TimeUtc > latest.TimeUtc) latest = r;

            if (r.TimeUtc >= cRange)
            {
                inRange.Add(r);
                s.RangeTotalTokens += r.TotalTokens;
                s.RangeRequests++;
                s.EstimatedCostRangeUsd += CostService.EstimateUsd(r);
            }

            if (r.TimeUtc < c7) continue;
            inWeek.Add(r);
            long total = r.TotalTokens;
            double cost = CostService.EstimateUsd(r);
            s.Total7d += total;
            s.EstimatedCost7dUsd += cost;
            input7d += r.InputTokens;
            cache7d += r.CacheReadTokens;
            if (r.TimeUtc >= c24) { s.Total24h += total; s.EstimatedCost24hUsd += cost; }
            if (s.OldestIn7dUtc == null || r.TimeUtc < s.OldestIn7dUtc) s.OldestIn7dUtc = r.TimeUtc;
            if (r.TimeUtc >= c5)
            {
                s.Total5h += total;
                if (s.OldestIn5hUtc == null || r.TimeUtc < s.OldestIn5hUtc) s.OldestIn5hUtc = r.TimeUtc;
            }
            if (r.TimeUtc >= c1) t1 += total;

            var l = r.TimeUtc.ToLocalTime();
            var rh = new DateTime(l.Year, l.Month, l.Day, l.Hour, 0, 0);
            int idx = 23 - (int)Math.Round((nowHour - rh).TotalHours);
            if (idx >= 0 && idx < 24)
            {
                hourTotals[idx] += total;
                hourReqs[idx]++;
            }
        }

        s.PerHour24 = Enumerable.Range(0, 24)
            .Select(i => new HourStat(nowHour.AddHours(i - 23), hourTotals[i], hourReqs[i])).ToList();
        long denom = input7d + cache7d;
        s.CacheHitPercent7d = denom > 0 ? cache7d * 100.0 / denom : 0;

        s.LatestModel = latest.Model;
        s.LatestTimeUtc = latest.TimeUtc;
        s.LatestContextTokens = latest.ContextTokens;
        s.LatestSessionId = latest.SessionId;
        s.BurnRatePerHour = t1 > 0 ? t1 : s.Total5h / 5.0;

        s.PerModel = inRange.GroupBy(r => r.Model)
            .Select(g => new ModelStat(g.Key, g.Sum(r => r.TotalTokens), g.Sum(r => r.InputTokens),
                g.Sum(r => r.CacheReadTokens), g.Sum(r => r.OutputTokens), g.Count()))
            .OrderByDescending(m => m.Total).ToList();

        s.PerDay = inWeek.GroupBy(r => r.TimeUtc.ToLocalTime().Date)
            .Select(g => new DayStat(g.Key, g.Sum(r => r.TotalTokens), g.Count()))
            .OrderByDescending(d => d.Date).ToList();

        s.TopSessions = inRange.GroupBy(r => r.SessionId)
            .Select(g => new SessionStat(g.Key, g.Sum(r => r.TotalTokens), g.Count(), g.Max(r => r.TimeUtc)))
            .OrderByDescending(x => x.Total).Take(5).ToList();

        if (budget5h > 0) s.Budget5hPercent = s.Total5h * 100.0 / budget5h;
        if (budget7d > 0) s.Budget7dPercent = s.Total7d * 100.0 / budget7d;
        if (budget5h > 0 && s.Total5h < budget5h && s.BurnRatePerHour > 0)
            s.ProjectedHoursToBudget = (budget5h - s.Total5h) / s.BurnRatePerHour;
        return s;
    }
}
