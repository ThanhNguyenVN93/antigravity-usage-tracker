namespace AntigravityUsageTracker.Services;

// Rough USD estimate; prices are per 1M tokens (input / cache read / output).
public static class CostService
{
    public static double EstimateUsd(string model, long input, long cacheRead, long output)
    {
        string m = (model ?? "").ToLowerInvariant();
        double pIn, pCache, pOut;
        if (m.Contains("argon") || m.Contains("pro") || m.Contains("opus"))
        { pIn = 1.25; pCache = 0.125; pOut = 5.0; }
        else if (m.Contains("hydrogen") || m.Contains("sonnet"))
        { pIn = 0.50; pCache = 0.05; pOut = 2.0; }
        else
        { pIn = 0.15; pCache = 0.015; pOut = 0.60; }

        return (Math.Max(0, input) * pIn + Math.Max(0, cacheRead) * pCache + Math.Max(0, output) * pOut) / 1_000_000.0;
    }

    public static double EstimateUsd(Models.UsageRecord r) =>
        EstimateUsd(r.Model, r.InputTokens, r.CacheReadTokens, r.OutputTokens);
}
