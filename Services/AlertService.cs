using System.Text.Json;
using AntigravityUsageTracker.Models;

namespace AntigravityUsageTracker.Services;

public sealed record UsageAlert(string Kind, double Percent, int Threshold);

// Non-repeating threshold alerts. Per kind ("5h"/"7d") we remember the highest level
// already announced (0 none, 1 threshold1, 2 threshold2); dropping below re-arms it.
public sealed class AlertService
{
    private readonly string _stateFile;
    private Dictionary<string, int> _state;

    public AlertService(string? stateFile = null)
    {
        _stateFile = stateFile ?? Path.Combine(TrackerPaths.DataDir, "alert-state.json");
        _state = LoadState();
    }

    public List<UsageAlert> Check(UsageSummary summary, AppConfig config, AgyQuotaSnapshot? quota = null)
    {
        var alerts = new List<UsageAlert>();
        bool changed = false;

        // Token budget wins when set; otherwise fall back to the server quota.
        changed |= Evaluate("5h", config.Budget5hTokens > 0 ? summary.Budget5hPercent : quota?.Gemini5h?.UsedPercent, config, alerts);
        changed |= Evaluate("7d", config.Budget7dTokens > 0 ? summary.Budget7dPercent : quota?.GeminiWeekly?.UsedPercent, config, alerts);

        changed |= Evaluate("argon7d", quota?.ArgonWeekly?.UsedPercent, config, alerts);

        changed |= EvaluateContext(summary, config, alerts);

        if (changed) SaveState();
        return alerts;
    }

    // Context alert: Percent carries the latest context tokens, Threshold the configured token limit.
    private bool EvaluateContext(UsageSummary summary, AppConfig cfg, List<UsageAlert> alerts)
    {
        int level = cfg.ContextAlertTokens > 0 && summary.LatestContextTokens >= cfg.ContextAlertTokens ? 1 : 0;
        _state.TryGetValue("context", out int last);
        if (level == last) return false;

        _state["context"] = level;
        if (level > last)
            alerts.Add(new UsageAlert("context", summary.LatestContextTokens,
                (int)Math.Min(int.MaxValue, cfg.ContextAlertTokens)));
        return true;
    }

    private bool Evaluate(string kind, double? pct, AppConfig cfg, List<UsageAlert> alerts)
    {
        int level = 0;
        if (pct is double p)
            level = p >= cfg.AlertThreshold2 ? 2 : p >= cfg.AlertThreshold1 ? 1 : 0;

        _state.TryGetValue(kind, out int last);
        if (level == last) return false;

        _state[kind] = level;
        if (level > last)
            alerts.Add(new UsageAlert(kind, pct ?? 0, level == 2 ? cfg.AlertThreshold2 : cfg.AlertThreshold1));
        return true;
    }

    private Dictionary<string, int> LoadState()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_stateFile));
                if (d != null) return d;
            }
        }
        catch (Exception ex) { ErrorLog.Write("AlertService.LoadState", ex); }
        return new Dictionary<string, int>();
    }

    private void SaveState()
    {
        try { AtomicFile.WriteAllText(_stateFile, JsonSerializer.Serialize(_state)); }
        catch (Exception ex) { ErrorLog.Write("AlertService.SaveState", ex); }
    }
}
