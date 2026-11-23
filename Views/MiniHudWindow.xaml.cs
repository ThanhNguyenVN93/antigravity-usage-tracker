using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AntigravityUsageTracker.Models;
using AntigravityUsageTracker.Services;

namespace AntigravityUsageTracker.Views;

public partial class MiniHudWindow : Window
{
    private static readonly Brush Green = Frozen("#A6E3A1");
    private static readonly Brush Amber = Frozen("#F9E2AF");
    private static readonly Brush Red = Frozen("#F38BA8");
    private static readonly Brush Blue = Frozen("#89B4FA");

    private UsageSummary? _summary;
    private AgyQuotaSnapshot? _quota;
    private AppConfig _config = new();
    private bool _placed;

    public event Action? RefreshRequested;
    public event Action? StatsRequested;
    public event Action? SettingsRequested;
    public event Action? ErrorsRequested;
    public event Action? CompactRequested;
    public event Action<double, double>? PositionChanged;

    public MiniHudWindow()
    {
        InitializeComponent();
        ApplyLanguage();
        Lang.LanguageChanged += ApplyLanguage;
        Closed += (_, _) => Lang.LanguageChanged -= ApplyLanguage;
        Loaded += (_, _) => PlaceOnce();
    }

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private void PlaceOnce()
    {
        if (_placed) return;
        _placed = true;
        if (_config.DockAboveClaude && TrySnapToClaude(_config)) return;
        if (_config.HudLeft is double l && _config.HudTop is double t
            && ScreenPlacement.IsOnAnyScreen(l, t, ActualWidth, ActualHeight))
        {
            Left = l; Top = t;
            return;
        }
        var wa = ScreenPlacement.WorkAreaForCursor(this);
        bool claudeRunning = false;
        try { claudeRunning = System.Diagnostics.Process.GetProcessesByName("ClaudeUsageTracker").Length > 0; } catch { }
        double rightOffset = claudeRunning ? 380 : 12;
        Left = Math.Max(wa.Left + 12, wa.Right - ActualWidth - rightOffset);
        Top = wa.Bottom - ActualHeight - 12;
    }

    public void SnapToClaudeIfEnabled(AppConfig config)
    {
        if (config.DockAboveClaude && IsVisible) TrySnapToClaude(config);
        RefreshResets();
    }

    private bool TrySnapToClaude(AppConfig config)
    {
        if (!config.DockAboveClaude || !ScreenPlacement.TryGetClaudeHudRect(this, out var cr)) return false;
        var wa = ScreenPlacement.WorkAreaForCursor(this);
        double w = ActualWidth, h = ActualHeight;
        double left = Math.Clamp(cr.Right - w, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - w - 8));
        double top = cr.Top - h - 8;
        if (top < wa.Top + 8)
        {
            left = Math.Max(wa.Left + 8, cr.Left - w - 8);
            top = Math.Clamp(cr.Bottom - h, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - h - 8));
        }
        if (Math.Abs(Left - left) > 2 || double.IsNaN(Left)) Left = left;
        if (Math.Abs(Top - top) > 2 || double.IsNaN(Top)) Top = top;
        return true;
    }

    private static string FormatRemaining(DateTime? oldestUtc, TimeSpan window)
    {
        if (oldestUtc is not DateTime o) return "-";
        var rem = o + window - DateTime.UtcNow;
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
        if (rem.TotalDays >= 1) return $"{(int)rem.TotalDays}d {rem.Hours}h";
        if (rem.TotalHours >= 1) return $"{(int)rem.TotalHours}h {rem.Minutes}m";
        return $"{rem.Minutes}m";
    }

    private void RefreshResets()
    {
        ValReset5h.Text = _quota?.Gemini5h is { ResetTimeUtc: { } r5 }
            ? FormatRemaining(r5, TimeSpan.Zero)
            : FormatRemaining(_summary?.OldestIn5hUtc, TimeSpan.FromHours(5));
        ValReset7d.Text = _quota?.GeminiWeekly is { ResetTimeUtc: { } r7 }
            ? FormatRemaining(r7, TimeSpan.Zero)
            : FormatRemaining(_summary?.OldestIn7dUtc, TimeSpan.FromDays(7));
    }

    private static string Pct0(double p) =>
        Math.Round(p).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private void ApplyQuotaLine()
    {
        var q = _quota;
        var parts = new List<string>();
        if (q?.GeminiWeekly is { } gw) parts.Add($"Gemini 7d: {Pct0(gw.UsedPercent)}");
        if (q?.ArgonWeekly is { } aw) parts.Add($"Argon 7d: {Pct0(aw.UsedPercent)}");
        TxtServerQuota.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (parts.Count > 0)
            TxtServerQuota.Text = Lang.T("hud.serverQuota", string.Join(" • ", parts));
    }

    public void ApplyLanguage()
    {
        TxtHudTitle.Text = Lang.T("hud.title");
        LblBudget5h.Text = Lang.T("hud.budget5h");
        LblBudget7d.Text = Lang.T("hud.budget7d");
        Lbl5h.Text = Lang.T("hud.w5");
        Lbl24h.Text = Lang.T("hud.w24");
        Lbl7d.Text = Lang.T("hud.w7");
        LblBurn.Text = Lang.T("hud.burn");
        LblContext.Text = Lang.T("hud.context");
        LblCost.Text = Lang.T("hud.cost");
        LblReset5h.Text = Lang.T("hud.reset5h");
        LblReset7d.Text = Lang.T("hud.reset7d");
        BtnRefresh.Content = Lang.T("btn.refresh");
        BtnStats.Content = Lang.T("btn.stats");
        BtnSettings.Content = Lang.T("btn.settings");
        BtnCompact.Content = Lang.T("btn.compact");
        TxtNoData.Text = Lang.T("hud.noData");
        Update(_summary, _config, _quota);
    }

    public void Update(UsageSummary? s, AppConfig config, AgyQuotaSnapshot? quota = null)
    {
        _summary = s;
        _config = config;
        _quota = config.SyncAgyQuota ? quota : null;
        ApplyQuotaLine();
        bool has = s is { HasData: true };

        Opacity = double.IsNaN(config.HudOpacity) ? 0.95 : Math.Clamp(config.HudOpacity, 0.5, 1.0);
        var detail = config.CompactHud ? Visibility.Collapsed : Visibility.Visible;
        MetricsGrid.Visibility = detail;
        PanelExtra.Visibility = has ? detail : Visibility.Collapsed;

        TxtNoData.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        TxtModel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        if (!has || s == null)
        {
            Panel5h.Visibility = Panel7d.Visibility = Visibility.Collapsed;
            Val5h.Text = Val24h.Text = Val7d.Text = ValBurn.Text = ValContext.Text = ValReset5h.Text = ValReset7d.Text = "-";
            TxtUpdated.Text = "";
            if (_quota?.Gemini5h is { } gq)
            {
                SetRing(gq.UsedPercent, RingBrush(gq.UsedPercent), Fmt.Percent(gq.UsedPercent), "5h");
                RefreshResets();
            }
            else
                SetRing(0, Blue, "-", "5h");
            return;
        }

        TxtModel.Text = string.IsNullOrEmpty(s.LatestModel) ? Lang.T("hud.model")
            : config.PrivacyMode ? "***" : s.LatestModel;
        ValCost.Text = "$" + s.EstimatedCost24hUsd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        TxtForecast.Visibility = s.ProjectedHoursToBudget != null ? Visibility.Visible : Visibility.Collapsed;
        if (s.ProjectedHoursToBudget is double fh)
            TxtForecast.Text = Lang.T("hud.forecast", fh.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        BadgeErrors.Visibility = s.ErrorCount24h > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtErrors.Text = Lang.T("hud.errors", s.ErrorCount24h);
        BadgeErrors.ToolTip = BuildErrorsTooltip(s, config.PrivacyMode);
        Val5h.Text = Fmt.Tokens(s.Total5h);
        Val24h.Text = Fmt.Tokens(s.Total24h);
        Val7d.Text = Fmt.Tokens(s.Total7d);
        ValBurn.Text = Lang.T("hud.perHour", Fmt.Tokens(s.BurnRatePerHour));
        ValContext.Text = Fmt.Tokens(s.LatestContextTokens);
        RefreshResets();
        TxtUpdated.Text = Lang.T("hud.updated", s.GeneratedUtc.ToLocalTime().ToString("HH:mm:ss"));

        if (_quota?.Gemini5h is { } g5)
            SetRing(g5.UsedPercent, RingBrush(g5.UsedPercent), Fmt.Percent(g5.UsedPercent), "5h");
        else if (s.Budget5hPercent is double bp)
            SetRing(bp, RingBrush(bp), Fmt.Percent(bp), Fmt.Tokens(s.Total5h));
        else if (s.Budget7dPercent is double wp)
            SetRing(wp, RingBrush(wp), Fmt.Percent(wp), Fmt.Tokens(s.Total7d));
        else
        {
            double ratio = s.Total24h > 0 ? Math.Clamp(s.Total5h * 100.0 / s.Total24h, 8, 100) : 0;
            SetRing(ratio, Blue, Fmt.Tokens(s.Total5h), "5h");
        }

        SetBar(Panel5h, Pb5h, TxtPct5h, s.Budget5hPercent);
        SetBar(Panel7d, Pb7d, TxtPct7d, s.Budget7dPercent);
    }

    private static string BuildErrorsTooltip(UsageSummary s, bool mask)
    {
        var lines = s.RecentErrors.Take(3).Select(e =>
            e.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            + "  " + (mask ? "***" : e.Snippet.Length > 60 ? e.Snippet.Substring(0, 57) + "..." : e.Snippet))
            .ToList();
        lines.Add(Lang.T("hud.errorsHint"));
        return string.Join("\n", lines);
    }

    private void BadgeErrors_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ErrorsRequested?.Invoke();
        e.Handled = true;
    }

    private Brush RingBrush(double p) =>
        p >= _config.AlertThreshold2 ? Red : p >= _config.AlertThreshold1 ? Amber : Green;

    private void SetRing(double pct, Brush brush, string mainText, string subText)
    {
        TxtRingMain.Text = mainText;
        TxtRingMain.Foreground = brush;
        TxtRingSub.Text = subText;

        if (pct <= 0)
        {
            RingArc.Visibility = Visibility.Collapsed;
            return;
        }

        double clamped = Math.Clamp(pct, 0.5, 99.99);
        double rad = (clamped / 100.0 * 360.0 - 90.0) * Math.PI / 180.0;
        const double cx = 46, cy = 46, r = 38.5;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Cos(rad), cy + r * Math.Sin(rad));
        var seg = new ArcSegment(end, new Size(r, r), 0, clamped > 50, SweepDirection.Clockwise, true);
        var fig = new PathFigure(start, new[] { seg }, false);
        RingArc.Data = new PathGeometry(new[] { fig });
        RingArc.Stroke = brush;
        RingArc.Visibility = Visibility.Visible;
    }

    private void SetBar(StackPanel panel, ProgressBar bar, TextBlock label, double? pct)
    {
        if (pct is not double p)
        {
            panel.Visibility = Visibility.Collapsed;
            return;
        }
        panel.Visibility = Visibility.Visible;
        bar.Value = Math.Min(100, p);
        var brush = p >= _config.AlertThreshold2 ? Red : p >= _config.AlertThreshold1 ? Amber : Green;
        bar.Foreground = brush;
        label.Foreground = brush;
        label.Text = Fmt.Percent(p);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
            PositionChanged?.Invoke(Left, Top);
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>True after the user pressed ✕; auto-hide logic must not re-show until toggled via tray.</summary>
    public bool HiddenByUser { get; set; }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        HiddenByUser = true;
        Hide();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void BtnStats_Click(object sender, RoutedEventArgs e) => StatsRequested?.Invoke();
    private void BtnSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void BtnCompact_Click(object sender, RoutedEventArgs e) => CompactRequested?.Invoke();
}
