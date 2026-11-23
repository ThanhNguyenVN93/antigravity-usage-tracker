using System.Globalization;
using System.Windows;
using AntigravityUsageTracker.Services;

namespace AntigravityUsageTracker.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public event Action<AppConfig>? Saved;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config.Clone();

        RbEn.IsChecked = _config.Language == Lang.En;
        RbVi.IsChecked = _config.Language != Lang.En;
        TbBudget5h.Text = _config.Budget5hTokens.ToString(CultureInfo.InvariantCulture);
        TbBudget7d.Text = _config.Budget7dTokens.ToString(CultureInfo.InvariantCulture);
        TbAlert1.Text = _config.AlertThreshold1.ToString(CultureInfo.InvariantCulture);
        TbAlert2.Text = _config.AlertThreshold2.ToString(CultureInfo.InvariantCulture);
        TbScan.Text = _config.ScanIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        CbStartup.IsChecked = _config.StartWithWindows;
        CbShowHud.IsChecked = _config.ShowHudOnStartup;
        CbAutoHideHud.IsChecked = _config.AutoHideHud;
        CbCompactHud.IsChecked = _config.CompactHud;
        CbPrivacyMode.IsChecked = _config.PrivacyMode;
        CbHotkeys.IsChecked = _config.EnableHotkeys;
        CbDockClaude.IsChecked = _config.DockAboveClaude;
        SlOpacity.Value = Math.Clamp(_config.HudOpacity, 0.5, 1.0);
        CbSyncAgyQuota.IsChecked = _config.SyncAgyQuota;
        TbQuotaSyncMin.Text = _config.QuotaSyncMinutes.ToString(CultureInfo.InvariantCulture);
        TbContextAlert.Text = _config.ContextAlertTokens.ToString(CultureInfo.InvariantCulture);
        TbWebhookUrl.Text = _config.WebhookUrl;

        ApplyLanguage();
        Lang.LanguageChanged += ApplyLanguage;
        Closed += (_, _) => Lang.LanguageChanged -= ApplyLanguage;
        Loaded += (_, _) =>
        {
            var wa = ScreenPlacement.WorkAreaForCursor(this);
            Left = wa.Left + Math.Max(0, (wa.Width - ActualWidth) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - ActualHeight) / 2);
        };
    }

    private void ApplyLanguage()
    {
        Title = Lang.T("settings.title");
        LblLanguage.Text = Lang.T("settings.language");
        LblBudget5h.Text = Lang.T("settings.budget5h");
        LblBudget7d.Text = Lang.T("settings.budget7d");
        LblHint.Text = Lang.T("settings.budgetHint");
        LblAlert1.Text = Lang.T("settings.alert1");
        LblAlert2.Text = Lang.T("settings.alert2");
        LblScan.Text = Lang.T("settings.scan");
        CbStartup.Content = Lang.T("settings.startWithWindows");
        CbShowHud.Content = Lang.T("settings.showHud");
        CbAutoHideHud.Content = Lang.T("settings.autoHideHud");
        CbCompactHud.Content = Lang.T("settings.compactHud");
        CbPrivacyMode.Content = Lang.T("settings.privacyMode");
        CbHotkeys.Content = Lang.T("settings.hotkeys");
        CbDockClaude.Content = Lang.T("settings.dockClaude");
        LblOpacity.Text = Lang.T("settings.opacity");
        CbSyncAgyQuota.Content = Lang.T("settings.syncAgy");
        LblQuotaSyncMin.Text = Lang.T("settings.syncAgyMin");
        LblContextAlert.Text = Lang.T("settings.contextAlert");
        LblWebhook.Text = Lang.T("settings.webhook");
        BtnSave.Content = Lang.T("btn.save");
        BtnCancel.Content = Lang.T("btn.cancel");
        TxtError.Text = Lang.T("settings.invalid");
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!long.TryParse(TbBudget5h.Text.Trim(), NumberStyles.Integer, inv, out long b5) || b5 < 0
            || !long.TryParse(TbBudget7d.Text.Trim(), NumberStyles.Integer, inv, out long b7) || b7 < 0
            || !int.TryParse(TbAlert1.Text.Trim(), NumberStyles.Integer, inv, out int a1) || a1 < 1 || a1 > 100
            || !int.TryParse(TbAlert2.Text.Trim(), NumberStyles.Integer, inv, out int a2) || a2 < 1 || a2 > 100
            || !int.TryParse(TbScan.Text.Trim(), NumberStyles.Integer, inv, out int scan) || scan < 5
            || !int.TryParse(TbQuotaSyncMin.Text.Trim(), NumberStyles.Integer, inv, out int qmin) || qmin < 1 || qmin > 120
            || !long.TryParse(TbContextAlert.Text.Trim(), NumberStyles.Integer, inv, out long ctx) || ctx < 0)
        {
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        _config.Language = RbEn.IsChecked == true ? Lang.En : Lang.Vi;
        _config.Budget5hTokens = b5;
        _config.Budget7dTokens = b7;
        _config.AlertThreshold1 = a1;
        _config.AlertThreshold2 = a2;
        _config.ScanIntervalSeconds = scan;
        _config.StartWithWindows = CbStartup.IsChecked == true;
        _config.ShowHudOnStartup = CbShowHud.IsChecked == true;
        _config.AutoHideHud = CbAutoHideHud.IsChecked == true;
        _config.CompactHud = CbCompactHud.IsChecked == true;
        _config.PrivacyMode = CbPrivacyMode.IsChecked == true;
        _config.EnableHotkeys = CbHotkeys.IsChecked == true;
        _config.DockAboveClaude = CbDockClaude.IsChecked == true;
        _config.HudOpacity = SlOpacity.Value;
        _config.SyncAgyQuota = CbSyncAgyQuota.IsChecked == true;
        _config.QuotaSyncMinutes = qmin;
        _config.ContextAlertTokens = ctx;
        _config.WebhookUrl = TbWebhookUrl.Text.Trim();
        _config.Normalize();

        Saved?.Invoke(_config);
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();
}
