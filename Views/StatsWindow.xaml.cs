using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AntigravityUsageTracker.Models;
using AntigravityUsageTracker.Services;

namespace AntigravityUsageTracker.Views;

public partial class StatsWindow : Window
{
    private UsageSummary? _summary;
    private IReadOnlyList<DailyHistoryEntry> _history = Array.Empty<DailyHistoryEntry>();
    private AppConfig _config = new();
    private StatsRange _range = StatsRange.Days7;

    public event Action<StatsRange>? RangeChanged;

    public StatsRange Range
    {
        get => _range;
        set { _range = value; Render(); }
    }

    public StatsWindow()
    {
        InitializeComponent();
        Lang.LanguageChanged += Render;
        Closed += (_, _) => Lang.LanguageChanged -= Render;
        Loaded += (_, _) =>
        {
            var wa = ScreenPlacement.WorkAreaForCursor(this);
            Left = wa.Left + Math.Max(0, (wa.Width - ActualWidth) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - ActualHeight) / 2);
        };
        Render();
    }

    public void Update(UsageSummary? summary, IReadOnlyList<DailyHistoryEntry> history, AppConfig config)
    {
        _summary = summary;
        _history = history;
        _config = config;
        Render();
    }

    private void BtnRange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<StatsRange>(tag, out var r) && r != _range)
        {
            _range = r;
            Render();
            RangeChanged?.Invoke(r);
        }
    }

    private void TbFilter_TextChanged(object sender, TextChangedEventArgs e) => Render();

    private string Mask(string value) => _config.PrivacyMode ? "***" : value;

    private static bool Match(string filter, string value) =>
        filter.Length == 0 || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string Usd(double v) => "$" + v.ToString("0.00", CultureInfo.InvariantCulture);

    private void StyleRangeButtons()
    {
        void Set(Button b, string key, StatsRange r)
        {
            b.Content = Lang.T(key);
            bool on = _range == r;
            b.Background = Brush(on ? "#89B4FA" : "#313244");
            b.Foreground = Brush(on ? "#11111B" : "#CDD6F4");
        }
        Set(BtnToday, "range.today", StatsRange.Today);
        Set(Btn7d, "range.7d", StatsRange.Days7);
        Set(Btn30d, "range.30d", StatsRange.Days30);
        Set(BtnAll, "range.all", StatsRange.All);
        TbFilter.ToolTip = Lang.T("stats.filter");
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    private void Render()
    {
        Title = Lang.T("stats.title");
        TxtTitle.Text = Lang.T("stats.title");
        BtnCopy.Content = Lang.T("stats.copySummary");
        BtnCopy.IsEnabled = _summary is { HasData: true };
        BtnExport.Content = Lang.T("stats.export");
        BtnExport.IsEnabled = _summary is { HasData: true };
        StyleRangeButtons();
        Host.Children.Clear();

        var s = _summary;
        string filter = TbFilter.Text.Trim();
        if (s == null || !s.HasData)
        {
            Host.Children.Add(new TextBlock
            {
                Text = Lang.T("stats.noData"), Foreground = Brush("#A6ADC8"), FontSize = 12
            });
            return;
        }

        // Summary cards
        var cards = new UniformGrid { Columns = 4, Margin = new Thickness(0, 0, 0, 8) };
        cards.Children.Add(Card(Lang.T("hud.w5"), Fmt.Tokens(s.Total5h), "#A6E3A1"));
        cards.Children.Add(Card(Lang.T("hud.w24"), Fmt.Tokens(s.Total24h), "#89B4FA"));
        cards.Children.Add(Card(Lang.T("hud.w7"), Fmt.Tokens(s.Total7d), "#CBA6F7"));
        cards.Children.Add(Card(Lang.T("hud.burn"), Lang.T("hud.perHour", Fmt.Tokens(s.BurnRatePerHour)), "#FAB387"));
        cards.Children.Add(Card(Lang.T("hud.context"), Fmt.Tokens(s.LatestContextTokens), "#F9E2AF"));
        cards.Children.Add(Card(Lang.T("stats.cacheHit"), Fmt.Percent(s.CacheHitPercent7d), "#94E2D5"));
        var costCard = Card(Lang.T("stats.cost"), Usd(s.EstimatedCostRangeUsd), "#94E2D5");
        costCard.ToolTip = $"24h: {Usd(s.EstimatedCost24hUsd)}   7d: {Usd(s.EstimatedCost7dUsd)}";
        cards.Children.Add(costCard);
        cards.Children.Add(Card(Lang.T("stats.errors"), $"{s.ErrorCount24h} / {s.ErrorCount7d}",
            s.ErrorCount24h > 0 ? "#F38BA8" : "#A6E3A1"));
        Host.Children.Add(cards);

        Heading("stats.chart24h");
        Host.Children.Add(HourChart(s));
        Heading("stats.chart7d");
        Host.Children.Add(DayChart(s));

        var models = s.PerModel.Where(m => Match(filter, m.Model)).ToList();
        Heading("stats.modelShare");
        Host.Children.Add(ModelShare(models, _config.PrivacyMode));

        Section("stats.perModel", new[] { "col.model", "col.requests", "col.input", "col.cache", "col.output", "col.tokens" },
            models.Select(m => new[]
            {
                Mask(m.Model), m.Requests.ToString(CultureInfo.InvariantCulture), Fmt.Tokens(m.Input),
                Fmt.Tokens(m.CacheRead), Fmt.Tokens(m.Output), Fmt.Tokens(m.Total)
            }));

        Section("stats.topTools", new[] { "col.tool", "col.count" },
            s.TopTools.Where(t => Match(filter, t.Tool)).Select(t => new[]
            {
                t.Tool, t.Count.ToString(CultureInfo.InvariantCulture)
            }));

        Section("stats.perDay", new[] { "col.day", "col.requests", "col.tokens" },
            s.PerDay.Select(d => new[]
            {
                d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                d.Requests.ToString(CultureInfo.InvariantCulture), Fmt.Tokens(d.Total)
            }));

        var sessions = s.TopSessions.Where(x => Match(filter, x.SessionId)).ToList();
        Section("stats.topSessions", new[] { "col.session", "col.requests", "col.lastActive", "col.tokens" },
            sessions.Select(x => new[]
            {
                Mask(x.SessionId), x.Requests.ToString(CultureInfo.InvariantCulture),
                x.LastUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), Fmt.Tokens(x.Total)
            }),
            (i, cell) =>
            {
                string id = sessions[i].SessionId;
                cell.Background = Brushes.Transparent;
                cell.Cursor = System.Windows.Input.Cursors.Hand;
                cell.ToolTip = (_config.PrivacyMode ? "" : id + "\n") + Lang.T("stats.openSessionHint");
                cell.MouseLeftButtonUp += (_, _) => OpenSession(id);
            });

        Section("stats.history", new[] { "col.day", "col.requests", "col.tokens", "col.cost" },
            _history.OrderByDescending(h => h.Date, StringComparer.Ordinal).Select(h => new[]
            {
                h.Date, h.Requests.ToString(CultureInfo.InvariantCulture), Fmt.Tokens(h.Tokens), Usd(h.CostUsd)
            }));

        if (s.RecentErrors.Count > 0)
        {
            var errors = s.RecentErrors.Where(e => Match(filter, e.SessionId) || Match(filter, e.Snippet)).ToList();
            Section("stats.recentErrors", new[] { "col.time", "col.session", "col.detail" },
                errors.Select(e => new[]
                {
                    e.TimeUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Mask(e.SessionId), Mask(e.Snippet)
                }),
                (i, cell) =>
                {
                    string id = errors[i].SessionId;
                    cell.Background = Brushes.Transparent;
                    cell.Cursor = System.Windows.Input.Cursors.Hand;
                    cell.ToolTip = (_config.PrivacyMode ? "" : errors[i].Snippet + "\n") + Lang.T("stats.openSessionHint");
                    cell.MouseLeftButtonUp += (_, _) => OpenSession(id);
                });
        }
    }

    private static void OpenSession(string sessionId)
    {
        try
        {
            string path = Path.Combine(TrackerPaths.BrainRoot, sessionId);
            if (!Directory.Exists(path)) return;
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("StatsWindow.OpenSession", ex);
        }
    }

    private async void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_summary is not { HasData: true } s) return;
        try
        {
            Clipboard.SetText(BuildSummaryText(s));
            BtnCopy.Content = Lang.T("stats.copied");
            await Task.Delay(1500);
            BtnCopy.Content = Lang.T("stats.copySummary");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("StatsWindow.Copy", ex);
        }
    }

    private async void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_summary is not { HasData: true } s) return;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
                FileName = $"antigravity-usage-{DateTime.Now:yyyy-MM-dd}"
            };
            if (dlg.ShowDialog(this) != true) return;

            if (string.Equals(Path.GetExtension(dlg.FileName), ".json", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                    new System.Text.UTF8Encoding(false));
            else
                File.WriteAllText(dlg.FileName, BuildCsv(s), new System.Text.UTF8Encoding(true));

            BtnExport.Content = Lang.T("stats.exported");
            await Task.Delay(1500);
            BtnExport.Content = Lang.T("stats.export");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("StatsWindow.Export", ex);
        }
    }

    private static string Csv(object? v)
    {
        string t = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        return t.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + t.Replace("\"", "\"\"") + "\"" : t;
    }

    private static string BuildCsv(UsageSummary s)
    {
        var sb = new System.Text.StringBuilder();
        void Row(params object?[] cells) => sb.Append(string.Join(",", cells.Select(Csv))).Append("\r\n");
        var inv = CultureInfo.InvariantCulture;

        Row("Summary");
        Row("Metric", "Value");
        Row("GeneratedUtc", s.GeneratedUtc.ToString("o", inv));
        Row("Total5h", s.Total5h);
        Row("Total24h", s.Total24h);
        Row("Total7d", s.Total7d);
        Row("BurnRatePerHour", s.BurnRatePerHour.ToString("0.##", inv));
        Row("LatestModel", s.LatestModel);
        Row("LatestContextTokens", s.LatestContextTokens);
        Row("CacheHitPercent7d", s.CacheHitPercent7d.ToString("0.##", inv));
        Row("EstimatedCost24hUsd", s.EstimatedCost24hUsd.ToString("0.0000", inv));
        Row("EstimatedCost7dUsd", s.EstimatedCost7dUsd.ToString("0.0000", inv));
        Row("EstimatedCostRangeUsd", s.EstimatedCostRangeUsd.ToString("0.0000", inv));
        Row("ErrorCount24h", s.ErrorCount24h);
        Row("ErrorCount7d", s.ErrorCount7d);
        if (s.Budget5hPercent is double b5) Row("Budget5hPercent", b5.ToString("0.##", inv));
        if (s.Budget7dPercent is double b7) Row("Budget7dPercent", b7.ToString("0.##", inv));
        sb.Append("\r\n");

        Row("PerModel");
        Row("Model", "Requests", "Input", "CacheRead", "Output", "Total");
        foreach (var m in s.PerModel) Row(m.Model, m.Requests, m.Input, m.CacheRead, m.Output, m.Total);
        sb.Append("\r\n");

        Row("PerDay");
        Row("Date", "Requests", "Total");
        foreach (var d in s.PerDay) Row(d.Date.ToString("yyyy-MM-dd", inv), d.Requests, d.Total);
        sb.Append("\r\n");

        Row("PerHour24");
        Row("Hour", "Requests", "Total");
        foreach (var h in s.PerHour24) Row(h.HourLocal.ToString("yyyy-MM-dd HH:00", inv), h.Requests, h.Total);
        sb.Append("\r\n");

        Row("TopSessions");
        Row("Session", "Requests", "LastActiveUtc", "Total");
        foreach (var x in s.TopSessions) Row(x.SessionId, x.Requests, x.LastUtc.ToString("o", inv), x.Total);
        sb.Append("\r\n");

        Row("TopTools");
        Row("Tool", "Count");
        foreach (var t in s.TopTools) Row(t.Tool, t.Count);
        return sb.ToString();
    }

    private static string BuildSummaryText(UsageSummary s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Lang.T("stats.title"));
        sb.AppendLine($"{Lang.T("hud.w5")}: {Fmt.Tokens(s.Total5h)}");
        sb.AppendLine($"{Lang.T("hud.w24")}: {Fmt.Tokens(s.Total24h)}");
        sb.AppendLine($"{Lang.T("hud.w7")}: {Fmt.Tokens(s.Total7d)}");
        sb.AppendLine($"{Lang.T("hud.burn")}: {Lang.T("hud.perHour", Fmt.Tokens(s.BurnRatePerHour))}");
        sb.AppendLine($"{Lang.T("hud.context")}: {Fmt.Tokens(s.LatestContextTokens)}");
        sb.AppendLine($"{Lang.T("stats.cacheHit")}: {Fmt.Percent(s.CacheHitPercent7d)}");
        if (s.Budget5hPercent is double b5) sb.AppendLine($"{Lang.T("hud.budget5h")}: {Fmt.Percent(b5)}");
        if (s.Budget7dPercent is double b7) sb.AppendLine($"{Lang.T("hud.budget7d")}: {Fmt.Percent(b7)}");
        sb.AppendLine();
        sb.AppendLine(Lang.T("stats.perModel"));
        foreach (var m in s.PerModel)
            sb.AppendLine($"- {m.Model}: {Fmt.Tokens(m.Total)} ({m.Requests})");
        return sb.ToString().TrimEnd();
    }

    private void Heading(string key) => Host.Children.Add(new TextBlock
    {
        Text = Lang.T(key), FontSize = 13, FontWeight = FontWeights.SemiBold,
        Foreground = Brush("#F5E0DC"), Margin = new Thickness(0, 6, 0, 6)
    });

    private static Border ChartFrame(UIElement child) => new()
    {
        Background = Brush("#181825"), CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 8, 10, 6), Margin = new Thickness(0, 0, 0, 8), Child = child
    };

    private const double ChartHeight = 90;

    // One chart column: [value label] [bar anchored to bottom] [axis label]
    private static Grid BarColumn(double ratio, string fill, string? valueText, string? axisText, string tip)
    {
        var col = new Grid { Margin = new Thickness(1.5, 0, 1.5, 0), ToolTip = tip, Background = Brushes.Transparent };
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ChartHeight) });
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });

        var val = new TextBlock
        {
            Text = valueText ?? "", FontSize = 9, Foreground = Brush("#A6ADC8"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom
        };
        var bar = new Border
        {
            Height = Math.Max(2, ratio * ChartHeight), Background = Brush(fill), CornerRadius = new CornerRadius(2, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        var axis = new TextBlock
        {
            Text = axisText ?? "", FontSize = 9, Foreground = Brush("#6C7086"),
            HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.NoWrap
        };
        Grid.SetRow(bar, 1);
        Grid.SetRow(axis, 2);
        col.Children.Add(val);
        col.Children.Add(bar);
        col.Children.Add(axis);
        return col;
    }

    private static Border HourChart(UsageSummary s)
    {
        var grid = new Grid();
        long max = s.PerHour24.Max(h => h.Total);
        for (int i = 0; i < s.PerHour24.Count; i++)
        {
            var h = s.PerHour24[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            bool peak = max > 0 && h.Total == max;
            string fill = peak ? "#89B4FA" : i == s.PerHour24.Count - 1 ? "#74C7EC" : "#45475A";
            string tip = Lang.T("stats.hourTip", h.HourLocal.ToString("MM-dd HH:00", CultureInfo.InvariantCulture),
                Fmt.Tokens(h.Total), $"{Lang.T("col.requests")}: {h.Requests}");
            var col = BarColumn(max > 0 ? (double)h.Total / max : 0, fill, null,
                i % 3 == 0 || i == s.PerHour24.Count - 1 ? h.HourLocal.ToString("HH", CultureInfo.InvariantCulture) : null, tip);
            Grid.SetColumn(col, i);
            grid.Children.Add(col);
        }
        return ChartFrame(grid);
    }

    private static Border DayChart(UsageSummary s)
    {
        var grid = new Grid();
        var byDate = s.PerDay.ToDictionary(d => d.Date.Date);
        var today = s.GeneratedUtc.ToLocalTime().Date;
        var days = Enumerable.Range(0, 7).Select(i => today.AddDays(i - 6))
            .Select(d => byDate.TryGetValue(d, out var st) ? st : new DayStat(d, 0, 0)).ToList();
        long max = days.Max(d => d.Total);
        for (int i = 0; i < days.Count; i++)
        {
            var d = days[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            bool peak = max > 0 && d.Total == max;
            string fill = peak ? "#89B4FA" : i == days.Count - 1 ? "#74C7EC" : "#45475A";
            string tip = Lang.T("stats.dayTip", d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Fmt.Tokens(d.Total), d.Requests);
            var col = BarColumn(max > 0 ? (double)d.Total / max : 0, fill, d.Total > 0 ? Fmt.Tokens(d.Total) : null,
                d.Date.ToString("MM-dd", CultureInfo.InvariantCulture), tip);
            col.Margin = new Thickness(6, 0, 6, 0);
            Grid.SetColumn(col, i);
            grid.Children.Add(col);
        }
        return ChartFrame(grid);
    }

    private static readonly string[] SharePalette =
        { "#3B82F6", "#10B981", "#EC4899", "#F59E0B", "#A855F7", "#06B6D4", "#EF4444", "#84CC16" };

    private static Border ModelShare(IReadOnlyList<ModelStat> models, bool mask)
    {
        var host = new StackPanel();
        long total = models.Sum(m => m.Total);
        var bar = new Grid { Height = 14, Margin = new Thickness(0, 0, 0, 8), ClipToBounds = true };
        var legend = new WrapPanel();

        for (int i = 0; i < models.Count; i++)
        {
            var m = models[i];
            string name = mask ? "***" : m.Model;
            string color = SharePalette[i % SharePalette.Length];
            double pct = total > 0 ? m.Total * 100.0 / total : 0;
            string tip = $"{name}: {Fmt.Tokens(m.Total)} ({Fmt.Percent(pct)})";

            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(m.Total, 1), GridUnitType.Star) });
            var seg = new Border { Background = Brush(color), ToolTip = tip };
            Grid.SetColumn(seg, i);
            bar.Children.Add(seg);

            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 2), ToolTip = tip };
            item.Children.Add(new Border
            {
                Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = Brush(color),
                Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center
            });
            item.Children.Add(new TextBlock
            {
                Text = $"{name} {Fmt.Percent(pct)}", FontSize = 10, Foreground = Brush("#CDD6F4")
            });
            legend.Children.Add(item);
        }

        host.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = bar });
        host.Children.Add(legend);
        return new Border
        {
            Background = Brush("#181825"), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 10, 10, 6), Margin = new Thickness(0, 0, 0, 8), Child = host
        };
    }

    private static Border Card(string label, string value, string colorHex)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Brush("#A6ADC8"), TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brush(colorHex), Margin = new Thickness(0, 2, 0, 0) });
        return new Border
        {
            Background = Brush("#181825"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 6, 6), Child = sp
        };
    }

    private void Section(string titleKey, string[] headerKeys, IEnumerable<string[]> rows,
        Action<int, TextBlock>? decorateCell = null)
    {
        Host.Children.Add(new TextBlock
        {
            Text = Lang.T(titleKey), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#F5E0DC"), Margin = new Thickness(0, 6, 0, 6)
        });

        var grid = new Grid();
        for (int c = 0; c < headerKeys.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c == 0 ? 2.2 : 1, GridUnitType.Star) });

        int r = 0;
        grid.RowDefinitions.Add(new RowDefinition());
        for (int c = 0; c < headerKeys.Length; c++)
            AddCell(grid, r, c, Lang.T(headerKeys[c]), "#A6ADC8", FontWeights.Bold);

        foreach (var row in rows)
        {
            r++;
            grid.RowDefinitions.Add(new RowDefinition());
            for (int c = 0; c < row.Length; c++)
            {
                var tb = AddCell(grid, r, c, row[c], "#CDD6F4", FontWeights.Normal);
                decorateCell?.Invoke(r - 1, tb);
            }
        }

        Host.Children.Add(new Border
        {
            Background = Brush("#181825"), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 8), Child = grid
        });
    }

    private static TextBlock AddCell(Grid g, int row, int col, string text, string color, FontWeight weight)
    {
        var tb = new TextBlock
        {
            Text = text, FontSize = 11, Foreground = Brush(color), FontWeight = weight,
            Margin = new Thickness(0, 2, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
        return tb;
    }
}
