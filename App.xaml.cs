using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AntigravityUsageTracker.Models;
using AntigravityUsageTracker.Services;
using AntigravityUsageTracker.Views;

namespace AntigravityUsageTracker;

public partial class App : System.Windows.Application
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private Mutex? _mutex;
    private AppConfig _config = new();
    private readonly TranscriptScanner _scanner = new();
    private readonly AlertService _alerts = new();
    private TranscriptWatcher? _watcher;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _dockTimer;
    private System.Windows.Forms.NotifyIcon? _tray;
    private IntPtr _iconHandle = IntPtr.Zero;
    private string _iconKey = "";

    private MiniHudWindow? _hud;
    private StatsWindow? _stats;
    private SettingsWindow? _settings;
    private UsageSummary? _summary;
    private bool _refreshing;
    private ForegroundAppWatcherService? _fgWatcher;
    private bool _hudAutoHidden;
    private readonly DailyHistoryService _historyService = new();
    private IReadOnlyList<DailyHistoryEntry> _history = Array.Empty<DailyHistoryEntry>();
    private GlobalHotkeyService? _hotkeys;
    private StatsRange _statsRange = StatsRange.Days7;
    private AgyQuotaSnapshot? _quota;
    private DispatcherTimer? _quotaTimer;
    private bool _quotaFetching;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, @"Local\AntigravityUsageTracker.SingleInstance", out bool created);
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            ErrorLog.Write("DispatcherUnhandledException", ex.Exception);
            ex.Handled = true;
        };

        _config = ConfigService.Load();
        Lang.Set(_config.Language);

        _hud = new MiniHudWindow();
        _hud.RefreshRequested += () => RefreshAll();
        _hud.StatsRequested += ShowStats;
        _hud.SettingsRequested += ShowSettings;
        _hud.ErrorsRequested += ShowStats;
        _hud.CompactRequested += () => _ = RunCompactUiAsync();
        _hud.PositionChanged += (l, t) =>
        {
            _config.HudLeft = l;
            _config.HudTop = t;
            ConfigService.Save(_config);
        };
        _quota = AgyQuotaService.LoadCached();
        _hud.Update(null, _config, _quota);

        _tray = new System.Windows.Forms.NotifyIcon { Visible = true };
        _tray.MouseClick += (_, me) =>
        {
            if (me.Button == System.Windows.Forms.MouseButtons.Left) ToggleHud();
        };
        BuildMenu();
        Lang.LanguageChanged += BuildMenu;
        SetTrayIcon(System.Drawing.Color.FromArgb(0x89, 0xB4, 0xFA), "blue");
        _tray.Text = Lang.T("tray.tooltipEmpty");

        if (_config.ShowHudOnStartup) _hud.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_config.ScanIntervalSeconds) };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        _quotaTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_config.QuotaSyncMinutes) };
        _quotaTimer.Tick += (_, _) => _ = SyncQuotaAsync();
        _quotaTimer.Start();

        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _dockTimer.Tick += (_, _) => _hud?.SnapToClaudeIfEnabled(_config);
        _dockTimer.Start();

        _watcher = new TranscriptWatcher();
        _watcher.Changed += () => Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync()));
        _watcher.Start();

        _fgWatcher = new ForegroundAppWatcherService();
        _fgWatcher.ForegroundChanged += OnForegroundChanged;
        _fgWatcher.Start();

        ApplyHotkeys();
        RefreshAll();
    }

    private void RefreshAll()
    {
        _ = RefreshAsync();
        _ = SyncQuotaAsync();
    }

    private async Task SyncQuotaAsync()
    {
        if (_quotaFetching || !_config.SyncAgyQuota) return;
        _quotaFetching = true;
        try
        {
            var snap = await AgyQuotaService.FetchAsync();
            if (snap == null) return;
            _quota = snap;
            if (_summary != null) ApplySummary(_summary);
            else _hud?.Update(null, _config, _quota);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("SyncQuotaAsync", ex);
        }
        finally
        {
            _quotaFetching = false;
        }
    }

    private AgyQuotaSnapshot? ActiveQuota => _config.SyncAgyQuota ? _quota : null;

    private void ApplyHotkeys()
    {
        if (_config.EnableHotkeys)
        {
            if (_hotkeys == null)
            {
                _hotkeys = new GlobalHotkeyService();
                _hotkeys.HotkeyPressed += id =>
                {
                    if (id == GlobalHotkeyService.ToggleHudId) ToggleHud();
                    else if (id == GlobalHotkeyService.ShowStatsId) ShowStats();
                };
            }
            _hotkeys.Start();
        }
        else
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
        }
    }

    private void SaveAndRefreshView()
    {
        ConfigService.Save(_config);
        _hud?.Update(_summary, _config, ActiveQuota);
        _stats?.Update(_summary, _history, _config);
        BuildMenu();
    }

    private void OnForegroundChanged(bool relevant)
    {
        if (_hud == null || !_config.AutoHideHud) return;
        if (relevant)
        {
            if (_hudAutoHidden && !_hud.HiddenByUser) ShowHudNoActivate();
            _hudAutoHidden = false;
        }
        else if (_hud.IsVisible)
        {
            _hudAutoHidden = true;
            _hud.Hide();
        }
    }

    private void ShowHudNoActivate()
    {
        if (_hud == null) return;
        _hud.ShowActivated = false;
        _hud.Show();
        _hud.ShowActivated = true;
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var cfg = _config;
            var range = _statsRange;
            var (summary, history) = await Task.Run(() =>
            {
                var records = _scanner.Scan();
                var sum = StatsService.Compute(records, DateTime.UtcNow, cfg.Budget5hTokens, cfg.Budget7dTokens, range);
                return (sum, (IReadOnlyList<DailyHistoryEntry>)_historyService.MergeAndSave(records));
            });
            _history = history;
            ApplySummary(summary);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("RefreshAsync", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplySummary(UsageSummary s)
    {
        _summary = s;
        if (!string.IsNullOrWhiteSpace(s.LatestSessionId)) CompactService.WriteActiveSession(s.LatestSessionId);
        var quota = ActiveQuota;
        _hud?.Update(s, _config, quota);
        _stats?.Update(s, _history, _config);

        if (!s.HasData)
        {
            SetTrayIcon(System.Drawing.Color.FromArgb(0x89, 0xB4, 0xFA), "--");
            if (_tray != null) _tray.Text = Lang.T("tray.tooltipEmpty");
            return;
        }

        double pct = s.MaxBudgetPercent;
        bool hasBudget = s.Budget5hPercent != null || s.Budget7dPercent != null;
        if (!hasBudget && (quota?.Gemini5h != null || quota?.GeminiWeekly != null))
        {
            pct = Math.Max(quota.Gemini5h?.UsedPercent ?? 0, quota.GeminiWeekly?.UsedPercent ?? 0);
            hasBudget = true;
        }
        var green = System.Drawing.Color.FromArgb(0xA6, 0xE3, 0xA1);
        if (hasBudget)
        {
            string label = Math.Clamp((int)Math.Round(pct), 0, 99).ToString();
            if (pct >= _config.AlertThreshold2)
                SetTrayIcon(System.Drawing.Color.FromArgb(0xF3, 0x8B, 0xA8), label);
            else if (pct >= _config.AlertThreshold1)
                SetTrayIcon(System.Drawing.Color.FromArgb(0xF9, 0xE2, 0xAF), label);
            else
                SetTrayIcon(green, label);
        }
        else
        {
            long t = s.Total5h;
            string label = t >= 1_000_000 ? $"{t / 1_000_000}M" : t >= 1_000 ? $"{t / 1_000}K" : $"{t}";
            if (label.Length > 3) label = "99K";
            SetTrayIcon(green, label);
        }

        UpdateTooltip();

        foreach (var a in _alerts.Check(s, _config, quota))
        {
            string body;
            if (a.Kind == "context")
                body = Lang.T("alert.contextBody", Fmt.Tokens((long)a.Percent), Fmt.Tokens(a.Threshold));
            else
            {
                string kind = Lang.T(a.Kind switch
                {
                    "5h" => "alert.kind5h",
                    "argon7d" => "alert.kindArgon7d",
                    _ => "alert.kind7d"
                });
                body = Lang.T("alert.body", kind, (int)Math.Round(a.Percent), a.Threshold);
            }
            _tray?.ShowBalloonTip(8000, Lang.T("alert.title"), body, System.Windows.Forms.ToolTipIcon.Warning);
            if (!string.IsNullOrWhiteSpace(_config.WebhookUrl))
                _ = WebhookService.SendAsync(_config.WebhookUrl, Lang.T("alert.title") + ": " + body);
        }
    }

    private void UpdateTooltip()
    {
        if (_tray == null) return;
        string text = _summary is { HasData: true } s
            ? Lang.T("tray.tooltip", Fmt.Tokens(s.Total5h), Fmt.Tokens(s.Total7d))
            : Lang.T("tray.tooltipEmpty");
        _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
    }

    private void SetTrayIcon(System.Drawing.Color color, string label)
    {
        string key = $"{color.ToArgb()}:{label}";
        if (_tray == null || key == _iconKey) return;
        try
        {
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(System.Drawing.Color.Transparent);
                using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x18, 0x18, 0x25));
                using var ring = new System.Drawing.Pen(color, 3);
                g.FillEllipse(bg, 1, 1, 30, 30);
                g.DrawEllipse(ring, 2.5f, 2.5f, 27, 27);

                float size = label.Length <= 2 ? 13f : 10f;
                using var font = new System.Drawing.Font("Segoe UI", size, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point);
                using var textBrush = new System.Drawing.SolidBrush(color);
                using var fmt = new System.Drawing.StringFormat
                {
                    Alignment = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center,
                    FormatFlags = System.Drawing.StringFormatFlags.NoWrap
                };
                g.DrawString(label, font, textBrush, new System.Drawing.RectangleF(0, 1, 32, 32), fmt);
            }
            IntPtr handle = bmp.GetHicon();
            var old = _iconHandle;
            _tray.Icon = System.Drawing.Icon.FromHandle(handle);
            _iconHandle = handle;
            _iconKey = key;
            if (old != IntPtr.Zero) DestroyIcon(old);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("SetTrayIcon", ex);
        }
    }

    private void BuildMenu()
    {
        if (_tray == null) return;
        var menu = new System.Windows.Forms.ContextMenuStrip();

        bool visible = _hud?.IsVisible == true;
        var toggle = menu.Items.Add(Lang.T(visible ? "tray.hideHud" : "tray.showHud"));
        toggle.Click += (_, _) => ToggleHud();
        menu.Items.Add(Lang.T("btn.refresh")).Click += (_, _) => RefreshAll();
        menu.Items.Add(Lang.T("btn.stats")).Click += (_, _) => ShowStats();
        menu.Items.Add(Lang.T("btn.settings")).Click += (_, _) => ShowSettings();
        menu.Items.Add(Lang.T("tray.compactSession")).Click += (_, _) => _ = RunCompactUiAsync();
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var privacy = new System.Windows.Forms.ToolStripMenuItem(Lang.T("tray.privacy")) { Checked = _config.PrivacyMode };
        privacy.Click += (_, _) => { _config.PrivacyMode = !_config.PrivacyMode; SaveAndRefreshView(); };
        menu.Items.Add(privacy);
        var compact = new System.Windows.Forms.ToolStripMenuItem(Lang.T("tray.compact")) { Checked = _config.CompactHud };
        compact.Click += (_, _) => { _config.CompactHud = !_config.CompactHud; SaveAndRefreshView(); };
        menu.Items.Add(compact);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var langMenu = new System.Windows.Forms.ToolStripMenuItem(Lang.T("tray.language"));
        var en = new System.Windows.Forms.ToolStripMenuItem("English") { Checked = Lang.Current == Lang.En };
        var vi = new System.Windows.Forms.ToolStripMenuItem("Tiếng Việt") { Checked = Lang.Current == Lang.Vi };
        en.Click += (_, _) => ChangeLanguage(Lang.En);
        vi.Click += (_, _) => ChangeLanguage(Lang.Vi);
        langMenu.DropDownItems.Add(en);
        langMenu.DropDownItems.Add(vi);
        menu.Items.Add(langMenu);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(Lang.T("tray.exit")).Click += (_, _) => ExitApp();

        // refresh the Show/Hide label each time the menu opens
        menu.Opening += (_, _) => toggle.Text = Lang.T(_hud?.IsVisible == true ? "tray.hideHud" : "tray.showHud");

        var oldMenu = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        oldMenu?.Dispose();
        UpdateTooltip();
    }

    private bool _compacting;

    private async Task RunCompactUiAsync()
    {
        if (_compacting) return;
        _compacting = true;
        CompactToastWindow? toast = null;
        try
        {
            try { System.Windows.Clipboard.SetText("/compact"); }
            catch (Exception ex) { ErrorLog.Write("RunCompactUiAsync.Clipboard", ex); }

            string sessionId = CompactService.ReadActiveSession();
            if (string.IsNullOrWhiteSpace(sessionId)) sessionId = _summary?.LatestSessionId ?? "";

            toast = new CompactToastWindow();
            toast.Show();
            toast.SetRunning(sessionId);

            var result = await CompactService.RunCompactAsync(sessionId);
            string text = Lang.T(result.MessageKey);
            if (!result.Ok && result.MessageKey == "compact.failed" && !string.IsNullOrEmpty(result.Detail))
                text = Lang.T("compact.failedDetail", result.Detail);
            toast.SetDone(result.Ok, text);

            if (result.Ok) await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("RunCompactUiAsync", ex);
            toast?.SetDone(false, Lang.T("compact.failed"));
        }
        finally
        {
            _compacting = false;
        }
    }

    private void ChangeLanguage(string lang)
    {
        _config.Language = lang;
        ConfigService.Save(_config);
        Lang.Set(lang);
    }

    private void ToggleHud()
    {
        if (_hud == null) return;
        _hudAutoHidden = false;
        if (_hud.IsVisible) { _hud.HiddenByUser = true; _hud.Hide(); }
        else { _hud.HiddenByUser = false; _hud.Show(); _hud.Activate(); }
    }

    private void ShowStats()
    {
        if (_stats == null)
        {
            _stats = new StatsWindow();
            _stats.Closed += (_, _) => _stats = null;
            _stats.RangeChanged += r => { _statsRange = r; _ = RefreshAsync(); };
            _stats.Range = _statsRange;
            _stats.Update(_summary, _history, _config);
            _stats.Show();
        }
        else
        {
            _stats.Activate();
        }
    }

    private void ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsWindow(_config);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Saved += OnSettingsSaved;
        _settings.Show();
    }

    private void OnSettingsSaved(AppConfig cfg)
    {
        // keep HUD position that may have changed while the dialog was open
        cfg.HudLeft = _config.HudLeft;
        cfg.HudTop = _config.HudTop;
        _config = cfg;
        ConfigService.Save(_config);
        AutoStartService.SetEnabled(_config.StartWithWindows);
        if (_timer != null) _timer.Interval = TimeSpan.FromSeconds(_config.ScanIntervalSeconds);
        if (_quotaTimer != null) _quotaTimer.Interval = TimeSpan.FromMinutes(_config.QuotaSyncMinutes);
        Lang.Set(_config.Language);
        ApplyHotkeys();
        BuildMenu();
        _hud?.Update(_summary, _config, ActiveQuota);
        if (!_config.AutoHideHud && _hudAutoHidden)
        {
            _hudAutoHidden = false;
            if (_hud is { HiddenByUser: false }) ShowHudNoActivate();
        }
        RefreshAll();
    }

    private void ExitApp()
    {
        _timer?.Stop();
        _quotaTimer?.Stop();
        _dockTimer?.Stop();
        _fgWatcher?.Dispose();
        _hotkeys?.Dispose();
        _watcher?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        if (_iconHandle != IntPtr.Zero) { DestroyIcon(_iconHandle); _iconHandle = IntPtr.Zero; }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
        Shutdown();
    }
}
