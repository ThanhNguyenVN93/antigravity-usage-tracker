using System.Text.Json;

namespace AntigravityUsageTracker.Services;

public sealed class AppConfig
{
    public string Language { get; set; } = "vi";
    public long Budget5hTokens { get; set; } = 0;
    public long Budget7dTokens { get; set; } = 0;
    public int AlertThreshold1 { get; set; } = 80;
    public int AlertThreshold2 { get; set; } = 95;
    public int ScanIntervalSeconds { get; set; } = 30;
    public bool StartWithWindows { get; set; }
    public bool ShowHudOnStartup { get; set; } = true;
    public bool AutoHideHud { get; set; } = false;
    public double? HudLeft { get; set; }
    public double? HudTop { get; set; }
    public double HudOpacity { get; set; } = 0.95;
    public bool CompactHud { get; set; } = false;
    public bool PrivacyMode { get; set; } = false;
    public long ContextAlertTokens { get; set; } = 150000;
    public string WebhookUrl { get; set; } = "";
    public bool EnableHotkeys { get; set; } = true;
    public bool DockAboveClaude { get; set; } = true;
    public bool SyncAgyQuota { get; set; } = true;
    public int QuotaSyncMinutes { get; set; } = 5;

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    public void Normalize()
    {
        if (Language != "en" && Language != "vi") Language = "vi";
        if (Budget5hTokens < 0) Budget5hTokens = 0;
        if (Budget7dTokens < 0) Budget7dTokens = 0;
        AlertThreshold1 = Math.Clamp(AlertThreshold1, 1, 100);
        AlertThreshold2 = Math.Clamp(AlertThreshold2, 1, 100);
        if (AlertThreshold2 < AlertThreshold1) AlertThreshold2 = AlertThreshold1;
        if (ScanIntervalSeconds < 5) ScanIntervalSeconds = 5;
        if (ScanIntervalSeconds > 3600) ScanIntervalSeconds = 3600;
        HudOpacity = double.IsNaN(HudOpacity) ? 0.95 : Math.Clamp(HudOpacity, 0.5, 1.0);
        if (ContextAlertTokens < 0) ContextAlertTokens = 0;
        QuotaSyncMinutes = Math.Clamp(QuotaSyncMinutes, 1, 120);
        WebhookUrl = (WebhookUrl ?? "").Trim();
        if (HudLeft is double l && (double.IsNaN(l) || double.IsInfinity(l))) HudLeft = null;
        if (HudTop is double t && (double.IsNaN(t) || double.IsInfinity(t))) HudTop = null;
    }
}

public static class ConfigService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(TrackerPaths.DataDir, "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
                if (cfg != null)
                {
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ConfigService.Load", ex);
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            config.Normalize();
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(config, Options));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("ConfigService.Save", ex);
        }
    }
}
