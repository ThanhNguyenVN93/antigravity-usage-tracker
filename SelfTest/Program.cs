using AntigravityUsageTracker.Models;
using AntigravityUsageTracker.Services;

namespace AntigravityUsageTracker.SelfTest;

internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL {name} {detail}"); }
    }

    private static string Line(string type, DateTime utc, string model, long i, long c, long o) =>
        $"{{\"type\":\"{type}\",\"created_at\":\"{utc:yyyy-MM-ddTHH:mm:ss.fffZ}\",\"model\":\"{model}\"," +
        $"\"input_tokens\":{i},\"cache_read_tokens\":{c},\"output_tokens\":{o}}}";

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "agy-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static string TranscriptPath(string brain, string session)
    {
        var dir = Path.Combine(brain, session, ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "transcript.jsonl");
    }

    private static int Main()
    {
        var dataDir = NewTempDir();
        Environment.SetEnvironmentVariable("ANTIGRAVITY_USAGE_TRACKER_DIR", dataDir);

        try
        {
            TestPaths(dataDir);
            TestParser();
            TestScanner();
            TestStats();
            TestConfig(dataDir);
            TestAlerts(dataDir);
            TestLocalization();
            TestCost();
            TestParserExtras();
            TestStatsExtras();
            TestHistory(dataDir);
            TestContextAlert(dataDir);
            TestConfigExtras();
            TestWatcher();
            TestAgyQuota(dataDir);
            TestCompact(dataDir);
            TestRealScan();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("  FAIL unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(dataDir, true); } catch { }
        }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static void TestPaths(string dataDir)
    {
        Console.WriteLine("Paths");
        Check("DataDir override", TrackerPaths.DataDir == dataDir);
        Environment.SetEnvironmentVariable("ANTIGRAVITY_BRAIN_DIR", @"C:\x\brain");
        Check("BrainRoot override", TrackerPaths.BrainRoot == @"C:\x\brain");
        Environment.SetEnvironmentVariable("ANTIGRAVITY_BRAIN_DIR", null);
        Check("BrainRoot default", TrackerPaths.BrainRoot.EndsWith(Path.Combine("antigravity", "brain")));
    }

    private static void TestParser()
    {
        Console.WriteLine("Parser");
        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var lines = new[]
        {
            Line("PLANNER_RESPONSE", t, "gemini-4-argon-medium", 100, 50, 25),
            "{ not json",
            "{\"type\":\"USER_INPUT\",\"created_at\":\"2026-01-01T10:00:00Z\"}",
            "",
            "{\"created_at\":\"2026-01-01T10:01:00Z\",\"input_tokens\":7}",
            "{\"input_tokens\":7}",
            "[1,2]",
            "{\"created_at\":\"2026-01-01T10:02:00Z\",\"input_tokens\":\"5\",\"output_tokens\":2",
        };
        var recs = TranscriptParser.ParseLines(lines, "sess");
        Check("keeps 2 valid rows", recs.Count == 2, recs.Count.ToString());
        Check("fields parsed", recs[0].InputTokens == 100 && recs[0].CacheReadTokens == 50 && recs[0].OutputTokens == 25
            && recs[0].Model == "gemini-4-argon-medium" && recs[0].TimeUtc == t && recs[0].SessionId == "sess");
        Check("total", recs[0].TotalTokens == 175 && recs[0].ContextTokens == 150);
        Check("missing fields default 0", recs[1].OutputTokens == 0 && recs[1].CacheReadTokens == 0 && recs[1].Model == "unknown");

        var tmp = NewTempDir();
        var path = TranscriptPath(tmp, "abc-guid");
        File.WriteAllLines(path, lines);
        using (var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var fromFile = TranscriptParser.ParseFile(path);
            Check("ParseFile with shared writer", fromFile.Count == 2 && fromFile[0].SessionId == "abc-guid");
        }
        Check("SessionIdFromPath", TranscriptParser.SessionIdFromPath(path) == "abc-guid");
        Directory.Delete(tmp, true);
    }

    private static void TestScanner()
    {
        Console.WriteLine("Scanner");
        var brain = NewTempDir();
        var t = DateTime.UtcNow.AddMinutes(-10);
        var p1 = TranscriptPath(brain, "s1");
        var p2 = TranscriptPath(brain, "s2");
        File.WriteAllLines(p1, new[] { Line("PLANNER_RESPONSE", t, "m", 1, 1, 1) });
        File.WriteAllLines(p2, new[] { Line("PLANNER_RESPONSE", t.AddMinutes(1), "m", 2, 2, 2) });
        Directory.CreateDirectory(Path.Combine(brain, "empty-session"));

        var sc = new TranscriptScanner(brain);
        var r1 = sc.Scan();
        Check("scan finds 2 records", r1.Count == 2 && sc.ParseCount == 2);
        sc.Scan();
        Check("second scan hits cache", sc.ParseCount == 2);

        File.AppendAllText(p1, Line("PLANNER_RESPONSE", t.AddMinutes(2), "m", 3, 3, 3) + Environment.NewLine);
        var r3 = sc.Scan();
        Check("changed file reparsed only", r3.Count == 3 && sc.ParseCount == 3);

        File.Delete(p2);
        Check("deleted file dropped", sc.Scan().Count == 2);
        Check("missing root is empty", new TranscriptScanner(Path.Combine(brain, "nope")).Scan().Count == 0);
        Directory.Delete(brain, true);
    }

    private static void TestStats()
    {
        Console.WriteLine("Stats");
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var recs = new List<UsageRecord>
        {
            new(now.AddMinutes(-30), "A", 100, 100, 100, "s1"),   // 300: in 1h/5h/24h/7d
            new(now.AddHours(-3), "B", 10, 10, 10, "s1"),         // 30: 5h
            new(now.AddHours(-10), "A", 1, 1, 1, "s2"),           // 3: 24h
            new(now.AddDays(-3), "B", 2, 2, 2, "s2"),             // 6: 7d
            new(now.AddDays(-8), "A", 1000, 0, 0, "s3"),          // out of window
        };
        var s = StatsService.Compute(recs, now, 600, 1000);
        Check("5h total", s.Total5h == 330, s.Total5h.ToString());
        Check("24h total", s.Total24h == 333);
        Check("7d total", s.Total7d == 339);
        Check("latest model/context", s.LatestModel == "A" && s.LatestContextTokens == 200);
        Check("burn rate uses last hour", Math.Abs(s.BurnRatePerHour - 300) < 0.001);
        Check("per-model", s.PerModel.Count == 2 && s.PerModel[0].Model == "A" && s.PerModel[0].Total == 303);
        Check("per-day sums", s.PerDay.Sum(d => d.Total) == 339);
        Check("top sessions", s.TopSessions.Count == 2 && s.TopSessions[0].SessionId == "s1" && s.TopSessions[0].Total == 330);
        Check("oldest in 5h/7d", s.OldestIn5hUtc == now.AddHours(-3) && s.OldestIn7dUtc == now.AddDays(-3),
            $"{s.OldestIn5hUtc} {s.OldestIn7dUtc}");
        Check("budget %", Math.Abs(s.Budget5hPercent!.Value - 55) < 0.001 && Math.Abs(s.Budget7dPercent!.Value - 33.9) < 0.001);

        Check("PerHour24 has 24 chronological buckets", s.PerHour24.Count == 24
            && s.PerHour24.Zip(s.PerHour24.Skip(1), (a, b) => b.HourLocal - a.HourLocal == TimeSpan.FromHours(1)).All(x => x));
        Check("PerHour24 last bucket = now's hour", s.PerHour24[23].HourLocal ==
            new DateTime(now.ToLocalTime().Year, now.ToLocalTime().Month, now.ToLocalTime().Day, now.ToLocalTime().Hour, 0, 0));
        var l30 = now.AddMinutes(-30).ToLocalTime();
        var b30 = s.PerHour24.First(h => h.HourLocal == new DateTime(l30.Year, l30.Month, l30.Day, l30.Hour, 0, 0));
        Check("PerHour24 bucket for -30min", b30.Total >= 300 && b30.Requests >= 1);
        Check("PerHour24 sums only in-window records", s.PerHour24.Sum(h => h.Total) == 333);
        // cache7d = 100+10+1+2 = 113, input7d = 100+10+1+2 = 113 -> 50%
        Check("cache hit %", Math.Abs(s.CacheHitPercent7d - 50) < 0.001, s.CacheHitPercent7d.ToString());

        var idle = StatsService.Compute(new[] { new UsageRecord(now.AddHours(-2), "A", 50, 0, 0, "s") }, now);
        Check("burn rate falls back to 5h avg", Math.Abs(idle.BurnRatePerHour - 10) < 0.001);
        Check("no budget -> null %", idle.Budget5hPercent == null && idle.Budget7dPercent == null);
        var empty = StatsService.Compute(Array.Empty<UsageRecord>(), now);
        Check("empty summary", !empty.HasData && empty.Total7d == 0);
        Check("empty/idle oldest", empty.OldestIn5hUtc == null && empty.OldestIn7dUtc == null
            && idle.OldestIn5hUtc == now.AddHours(-2));
        Check("empty summary still has 24 buckets, 0% cache", empty.PerHour24.Count == 24 && empty.CacheHitPercent7d == 0);
    }

    private static void TestConfig(string dataDir)
    {
        Console.WriteLine("Config");
        var def = new AppConfig();
        Check("defaults", def.Language == "vi" && def.Budget5hTokens == 0 && def.AlertThreshold1 == 80
            && def.AlertThreshold2 == 95 && def.ScanIntervalSeconds == 30 && !def.StartWithWindows
            && def.HudLeft == null);

        var path = Path.Combine(dataDir, "cfg-test.json");
        Check("missing file -> defaults", ConfigService.Load(path).Language == "vi");
        ConfigService.Save(new AppConfig { Language = "en", Budget5hTokens = 123, HudLeft = 10.5, HudTop = 20 }, path);
        var back = ConfigService.Load(path);
        Check("roundtrip", back.Language == "en" && back.Budget5hTokens == 123 && back.HudLeft == 10.5 && back.HudTop == 20);

        File.WriteAllText(path, "{\"Language\":\"xx\",\"AlertThreshold1\":500,\"ScanIntervalSeconds\":1,\"Budget7dTokens\":-5}");
        var n = ConfigService.Load(path);
        Check("normalized", n.Language == "vi" && n.AlertThreshold1 == 100 && n.ScanIntervalSeconds == 5 && n.Budget7dTokens == 0);
        File.WriteAllText(path, "garbage");
        Check("corrupt -> defaults", ConfigService.Load(path).ScanIntervalSeconds == 30);
    }

    private static void TestAlerts(string dataDir)
    {
        Console.WriteLine("Alerts");
        var state = Path.Combine(dataDir, "alert-test.json");
        var cfg = new AppConfig { Budget5hTokens = 1000 };
        var now = DateTime.UtcNow;
        UsageSummary Sum(long tokens) =>
            StatsService.Compute(new[] { new UsageRecord(now, "m", tokens, 0, 0, "s") }, now, cfg.Budget5hTokens, cfg.Budget7dTokens);

        var svc = new AlertService(state);
        Check("below threshold: none", svc.Check(Sum(500), cfg).Count == 0);
        var a = svc.Check(Sum(850), cfg);
        Check("crossing 80% alerts once", a.Count == 1 && a[0].Kind == "5h" && a[0].Threshold == 80);
        Check("no repeat", svc.Check(Sum(860), cfg).Count == 0);
        var b = svc.Check(Sum(960), cfg);
        Check("crossing 95% alerts", b.Count == 1 && b[0].Threshold == 95);

        var reloaded = new AlertService(state);
        Check("state persisted", reloaded.Check(Sum(970), cfg).Count == 0);
        reloaded.Check(Sum(100), cfg);
        Check("re-arms after drop", reloaded.Check(Sum(900), cfg).Count == 1);
        Check("7d budget 0 -> no alerts", new AlertService(Path.Combine(dataDir, "alert-test2.json"))
            .Check(Sum(5000), new AppConfig()).Count == 0);
    }

    private static void TestLocalization()
    {
        Console.WriteLine("Localization");
        var en = Lang.Strings(Lang.En);
        var vi = Lang.Strings(Lang.Vi);
        var missingVi = en.Keys.Except(vi.Keys).ToList();
        var missingEn = vi.Keys.Except(en.Keys).ToList();
        Check("EN/VI keys match", missingVi.Count == 0 && missingEn.Count == 0,
            $"missing vi: [{string.Join(",", missingVi)}] missing en: [{string.Join(",", missingEn)}]");
        Check("no empty values", en.Values.All(v => v.Length > 0) && vi.Values.All(v => v.Length > 0));

        var fmtOk = en.Keys.All(k => Placeholders(en[k]) == Placeholders(vi[k]));
        Check("placeholder parity", fmtOk);

        int fired = 0;
        void H() => fired++;
        Lang.LanguageChanged += H;
        Lang.Set("en");
        Check("Set en", Lang.Current == "en" && Lang.T("btn.save") == "Save" && fired == 1);
        Lang.Set("en");
        Check("no event when unchanged", fired == 1);
        Lang.Set("vi");
        Check("Set vi", Lang.Current == "vi" && Lang.T("btn.save") == "Lưu" && fired == 2);
        Lang.LanguageChanged -= H;
        Check("unknown key returns key", Lang.T("nope.key") == "nope.key");
        Check("format args", Lang.T("hud.perHour", "5K") == "5K/giờ");
    }

    private static void TestCost()
    {
        Console.WriteLine("Cost");
        Check("argon pricing", Math.Abs(CostService.EstimateUsd("gemini-4-argon", 1_000_000, 1_000_000, 1_000_000) - 6.375) < 1e-9);
        Check("sonnet pricing", Math.Abs(CostService.EstimateUsd("claude-sonnet", 1_000_000, 1_000_000, 1_000_000) - 2.55) < 1e-9);
        Check("default pricing", Math.Abs(CostService.EstimateUsd("flash", 1_000_000, 1_000_000, 1_000_000) - 0.765) < 1e-9);
        Check("unknown -> default", CostService.EstimateUsd("zzz", 1000, 0, 0) == CostService.EstimateUsd("haiku", 1000, 0, 0));
    }

    private static void TestParserExtras()
    {
        Console.WriteLine("Parser extras");
        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var withTools = "{\"created_at\":\"2026-01-01T10:00:00Z\",\"model\":\"m\",\"input_tokens\":1,\"output_tokens\":1," +
            "\"tool_calls\":[{\"name\":\"read_file\"},{\"name\":\"grep\"},{\"x\":1}]}";
        var err1 = "{\"type\":\"ERROR_MESSAGE\",\"created_at\":\"2026-01-01T10:01:00Z\"}";
        var err2 = "{\"status\":\"ERROR\",\"created_at\":\"2026-01-01T10:02:00Z\"}";
        var recs = TranscriptParser.ParseLines(new[] { withTools, err1, err2 }, "s");
        Check("3 records", recs.Count == 3, recs.Count.ToString());
        Check("tools extracted", recs[0].Tools is { Length: 2 } && recs[0].Tools![0] == "read_file" && !recs[0].IsError);
        Check("errors flagged", recs[1].IsError && recs[2].IsError && recs[1].TotalTokens == 0 && recs[1].Tools == null);
        Check("6-arg ctor compatible", !new UsageRecord(t, "m", 1, 1, 1, "s").IsError);

        var errContent = "{\"type\":\"ERROR_MESSAGE\",\"created_at\":\"2026-01-01T10:03:00Z\",\"content\":\"line1\\n  line2\\t\\tline3\"}";
        var errLong = "{\"type\":\"ERROR_MESSAGE\",\"created_at\":\"2026-01-01T10:04:00Z\",\"content\":\"" + new string('x', 300) + "\"}";
        var errEmpty = "{\"type\":\"ERROR_MESSAGE\",\"created_at\":\"2026-01-01T10:05:00Z\",\"content\":\"  \"}";
        var er = TranscriptParser.ParseLines(new[] { errContent, errLong, errEmpty, err2 }, "s");
        Check("snippet collapses whitespace", er[0].ErrorSnippet == "line1 line2 line3", er[0].ErrorSnippet);
        Check("snippet truncated to 140 with ...", er[1].ErrorSnippet!.Length == 140 && er[1].ErrorSnippet!.EndsWith("..."),
            er[1].ErrorSnippet?.Length.ToString());
        Check("snippet falls back to type", er[2].ErrorSnippet == "ERROR_MESSAGE", er[2].ErrorSnippet);
        Check("snippet fallback without type", er[3].ErrorSnippet == "ERROR", er[3].ErrorSnippet);
        Check("non-error has no snippet", recs[0].ErrorSnippet == null);
    }

    private static void TestStatsExtras()
    {
        Console.WriteLine("Stats extras");
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var recs = new List<UsageRecord>
        {
            new(now.AddMinutes(-30), "argon", 1_000_000, 0, 0, "s1", new[] { "grep", "read" }),
            new(now.AddMinutes(-20), "argon", 0, 0, 0, "s1", null, true),
            new(now.AddDays(-3), "flash", 100, 0, 0, "s2", new[] { "grep" }),
            new(now.AddDays(-20), "flash", 50, 0, 0, "s3", new[] { "old" }),
            new(now.AddDays(-5), "", 0, 0, 0, "s2", null, true),
        };
        var s = StatsService.Compute(recs, now, 10_000_000, 0, StatsRange.Days7);
        Check("error rows ignored for tokens", s.RecordCount == 3 && s.Total7d == 1_000_100 && s.RangeRequests == 2);
        Check("error counts", s.ErrorCount24h == 1 && s.ErrorCount7d == 2);
        Check("top tools 7d", s.TopTools.Count == 2 && s.TopTools[0].Tool == "grep" && s.TopTools[0].Count == 2);
        Check("30d range includes older tools", StatsService.Compute(recs, now, 0, 0, StatsRange.Days30).TopTools.Any(t => t.Tool == "old"));
        var all = StatsService.Compute(recs, now, 0, 0, StatsRange.All);
        Check("All range totals", all.RangeTotalTokens == 1_000_150 && all.RangeRequests == 3);
        Check("Today excludes 3d-old", StatsService.Compute(recs, now, 0, 0, StatsRange.Today).RangeRequests <= 1);
        Check("cost 24h", Math.Abs(s.EstimatedCost24hUsd - 1.25) < 1e-9, s.EstimatedCost24hUsd.ToString());
        Check("cost 7d >= 24h and range==7d", s.EstimatedCost7dUsd > s.EstimatedCost24hUsd
            && Math.Abs(s.EstimatedCostRangeUsd - s.EstimatedCost7dUsd) < 1e-9);
        // 5h total 1,000,000 at 1M/h burn, budget 10M -> 9 hours
        Check("projected hours", s.ProjectedHoursToBudget is double h && Math.Abs(h - 9) < 1e-6, s.ProjectedHoursToBudget?.ToString());
        Check("no projection without budget / when exceeded",
            StatsService.Compute(recs, now).ProjectedHoursToBudget == null
            && StatsService.Compute(recs, now, 500).ProjectedHoursToBudget == null);
        var onlyErr = StatsService.Compute(new[] { new UsageRecord(now, "", 0, 0, 0, "s", null, true) }, now);
        Check("errors only -> no data but counted", !onlyErr.HasData && onlyErr.ErrorCount24h == 1);

        var errRecs = new List<UsageRecord> { new(now.AddMinutes(-1), "m", 1, 0, 0, "ok") };
        for (int i = 0; i < 25; i++)
            errRecs.Add(new UsageRecord(now.AddMinutes(-10 - i), "", 0, 0, 0, "e" + i, null, true, "err" + i));
        errRecs.Add(new UsageRecord(now.AddDays(-20), "", 0, 0, 0, "old", null, true, "too-old"));
        var es = StatsService.Compute(errRecs, now, 0, 0, StatsRange.Days7);
        Check("RecentErrors capped at 20, newest first", es.RecentErrors.Count == 20
            && es.RecentErrors[0].Snippet == "err0" && es.RecentErrors[19].Snippet == "err19"
            && es.RecentErrors.Zip(es.RecentErrors.Skip(1), (a, b) => a.TimeUtc >= b.TimeUtc).All(x => x));
        Check("RecentErrors respects range", es.RecentErrors.All(e => e.SessionId != "old")
            && StatsService.Compute(errRecs, now, 0, 0, StatsRange.All).RecentErrors.Count == 20);
        Check("RecentErrors on errors-only", onlyErr.RecentErrors.Count == 1 && onlyErr.RecentErrors[0].Snippet == "ERROR");
        Check("errors strings EN+VI", new[] { "hud.errorsHint", "stats.recentErrors", "col.time", "col.detail" }
            .All(k => Lang.Strings(Lang.En).ContainsKey(k) && Lang.Strings(Lang.Vi).ContainsKey(k)));
    }

    private static void TestHistory(string dataDir)
    {
        Console.WriteLine("History");
        var file = Path.Combine(dataDir, "history-test.json");
        var svc = new DailyHistoryService(file);
        Check("missing -> empty", svc.Load().Count == 0);
        var now = DateTime.UtcNow;
        var r1 = new[]
        {
            new UsageRecord(now.AddDays(-1), "argon", 1_000_000, 0, 0, "a"),
            new UsageRecord(now, "flash", 100, 0, 0, "a"),
            new UsageRecord(now, "", 0, 0, 0, "a", null, true),
        };
        var merged = svc.MergeAndSave(r1);
        Check("two days saved", merged.Count == 2 && merged.Sum(e => e.Requests) == 2);
        var back = new DailyHistoryService(file).Load();
        Check("reload matches", back.Count == 2 && back.Sum(e => e.Tokens) == 1_000_100 && back[0].CostUsd > 1.0);

        // transcripts pruned: yesterday gone, today grew
        var merged2 = svc.MergeAndSave(new[] { new UsageRecord(now, "flash", 500, 0, 0, "a") });
        Check("old days kept, current replaced", merged2.Count == 2 && merged2.Sum(e => e.Tokens) == 1_000_500);
    }

    private static void TestContextAlert(string dataDir)
    {
        Console.WriteLine("Context alert");
        var cfg = new AppConfig { ContextAlertTokens = 1000 };
        var now = DateTime.UtcNow;
        UsageSummary Sum(long ctx) =>
            StatsService.Compute(new[] { new UsageRecord(now, "m", ctx, 0, 0, "s") }, now);
        var svc = new AlertService(Path.Combine(dataDir, "alert-ctx.json"));
        Check("below: none", svc.Check(Sum(500), cfg).Count == 0);
        var a = svc.Check(Sum(1200), cfg);
        Check("crossing alerts once", a.Count == 1 && a[0].Kind == "context" && a[0].Threshold == 1000);
        Check("no repeat", svc.Check(Sum(1500), cfg).Count == 0);
        svc.Check(Sum(100), cfg);
        Check("re-arms below", svc.Check(Sum(1100), cfg).Count == 1);
        Check("disabled at 0", new AlertService(Path.Combine(dataDir, "alert-ctx2.json"))
            .Check(Sum(9_999_999), new AppConfig { ContextAlertTokens = 0 }).Count == 0);
    }

    private static void TestConfigExtras()
    {
        Console.WriteLine("Config extras");
        var d = new AppConfig();
        Check("new defaults", d.HudOpacity == 0.95 && !d.CompactHud && !d.PrivacyMode
            && d.ContextAlertTokens == 150000 && d.WebhookUrl == "" && d.EnableHotkeys && d.DockAboveClaude);
        var c = new AppConfig { HudOpacity = 5, ContextAlertTokens = -3, WebhookUrl = "  x " };
        c.Normalize();
        Check("clamped", c.HudOpacity == 1.0 && c.ContextAlertTokens == 0 && c.WebhookUrl == "x");
        c.HudOpacity = 0.1; c.Normalize();
        Check("opacity min", c.HudOpacity == 0.5);

        var en = Lang.Strings(Lang.En);
        var keys = new[] { "hud.forecast", "hud.cost", "hud.errors", "stats.cost", "stats.errors", "stats.topTools",
            "stats.history", "stats.filter", "range.today", "range.7d", "range.30d", "range.all", "tray.privacy",
            "tray.compact", "settings.opacity", "settings.compactHud", "settings.privacyMode", "settings.contextAlert",
            "settings.hotkeys", "settings.webhook", "alert.contextBody" };
        Check("new keys in EN and VI", keys.All(k => en.ContainsKey(k) && Lang.Strings(Lang.Vi).ContainsKey(k)));
    }

    private static string Placeholders(string s) =>
        string.Concat(System.Text.RegularExpressions.Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).OrderBy(x => x));

    private static void TestWatcher()
    {
        Console.WriteLine("Watcher");
        var brain = NewTempDir();
        var path = TranscriptPath(brain, "w1");
        File.WriteAllText(path, "");
        int count = 0;
        using var w = new TranscriptWatcher(brain, TimeSpan.FromMilliseconds(200));
        w.Changed += () => Interlocked.Increment(ref count);
        Check("starts", w.Start());
        for (int i = 0; i < 3; i++) { File.AppendAllText(path, "x\n"); Thread.Sleep(30); }
        Thread.Sleep(1200);
        Check("debounced to one event", count == 1, count.ToString());
        Check("missing root doesn't start", !new TranscriptWatcher(Path.Combine(brain, "nope")).Start());
    }

    private static void TestAgyQuota(string dataDir)
    {
        Console.WriteLine("Agy quota");
        const string real =
            "Gemini Models\tWeekly Limit Remaining\t94%\t2026-11-30T00:24:44Z\n" +
            "Gemini Models\tFive Hour Limit Remaining\t79%\t2026-11-23T10:24:44Z\r\n" +
            "Claude and GPT models\tWeekly Limit Remaining\t100%\t2026-11-30T09:07:50Z\n" +
            "Claude and GPT models\tFive Hour Limit Remaining\t100%\t2026-11-23T14:07:50Z\n" +
            "Gemini 4 Argon\tGemini 4 Argon Weekly Limit\t58%\t2026-11-30T01:12:01Z\n";
        var items = AgyQuotaService.ParseOutput(real);
        Check("parses 5 lines", items.Count == 5, items.Count.ToString());
        Check("used % = 100 - remaining", Math.Abs(items[0].UsedPercent - 6) < 1e-9 && Math.Abs(items[4].UsedPercent - 42) < 1e-9);
        Check("reset parsed as UTC", items[1].ResetTimeUtc == new DateTime(2026, 11, 23, 10, 24, 44, DateTimeKind.Utc)
            && items[1].ResetTimeUtc!.Value.Kind == DateTimeKind.Utc);

        var bad = AgyQuotaService.ParseOutput("garbage\n\nA\tB\n\tX\t50%\t2026-01-01T00:00:00Z\nG\tL\tabc\t2026-01-01T00:00:00Z\nG\tL\t150%\tnot-a-date\n");
        Check("malformed lines skipped, clamped, bad date -> null",
            bad.Count == 1 && bad[0].RemainingPercent == 100 && bad[0].UsedPercent == 0 && bad[0].ResetTimeUtc == null);
        Check("null/empty output", AgyQuotaService.ParseOutput(null).Count == 0 && AgyQuotaService.ParseOutput("").Count == 0);

        var snap = new AgyQuotaSnapshot { FetchedUtc = DateTime.UtcNow, Items = items };
        Check("Gemini5h", snap.Gemini5h?.RemainingPercent == 79);
        Check("GeminiWeekly", snap.GeminiWeekly?.RemainingPercent == 94);
        Check("ArgonWeekly", snap.ArgonWeekly?.RemainingPercent == 58);
        Check("helpers null when absent", new AgyQuotaSnapshot().Gemini5h == null && new AgyQuotaSnapshot().ArgonWeekly == null);

        var file = Path.Combine(dataDir, "agy-quota-test.json");
        Check("missing cache -> null", AgyQuotaService.LoadCached(file) == null);
        AgyQuotaService.SaveCached(snap, file);
        var back = AgyQuotaService.LoadCached(file);
        Check("cache roundtrip", back != null && back.Items.Count == 5 && back.Gemini5h?.ResetTimeUtc == items[1].ResetTimeUtc
            && Math.Abs(back.FetchedUtc.Ticks - snap.FetchedUtc.Ticks) < TimeSpan.TicksPerSecond);
        File.WriteAllText(file, "garbage");
        Check("corrupt cache -> null", AgyQuotaService.LoadCached(file) == null);

        var cfg = new AppConfig { QuotaSyncMinutes = 999 };
        cfg.Normalize();
        Check("QuotaSyncMinutes clamped", cfg.QuotaSyncMinutes == 120 && new AppConfig().SyncAgyQuota && new AppConfig().QuotaSyncMinutes == 5);

        // CLI brain root scanning
        Check("CliBrainRoot path", TrackerPaths.CliBrainRoot.EndsWith(Path.Combine("antigravity-cli", "brain")));
        var sc = new TranscriptScanner();
        Check("default scanner scans both roots", sc.Roots.Count == 2 && sc.Roots[1] == TrackerPaths.CliBrainRoot);
        Check("explicit root scans one", new TranscriptScanner(dataDir).Roots.Count == 1);

        // quota fallback in alerts (no token budget)
        var alerts = new AlertService(Path.Combine(dataDir, "alert-quota.json"));
        var sum = StatsService.Compute(new[] { new UsageRecord(DateTime.UtcNow, "m", 1, 0, 0, "s") }, DateTime.UtcNow);
        var hot = new AgyQuotaSnapshot { Items = { new AgyQuotaItem("Gemini Models", "Five Hour Limit Remaining", 10, null) } };
        var fired = alerts.Check(sum, new AppConfig { ContextAlertTokens = 0 }, hot);
        Check("quota drives 5h alert", fired.Count == 1 && fired[0].Kind == "5h" && fired[0].Threshold == 80);

        var en = Lang.Strings(Lang.En);
        var vi = Lang.Strings(Lang.Vi);
        Check("quota strings EN+VI", new[] { "settings.syncAgy", "settings.syncAgyMin", "hud.serverQuota" }
            .All(k => en.ContainsKey(k) && vi.ContainsKey(k)));
    }

    private static void TestCompact(string dataDir)
    {
        Console.WriteLine("Compact");
        var now = DateTime.UtcNow;
        var sum = StatsService.Compute(new[]
        {
            new UsageRecord(now.AddMinutes(-10), "m", 1, 0, 0, "old-session"),
            new UsageRecord(now.AddMinutes(-1), "m", 1, 0, 0, "new-session"),
        }, now);
        Check("LatestSessionId", sum.LatestSessionId == "new-session", sum.LatestSessionId);
        Check("empty LatestSessionId", StatsService.Compute(Array.Empty<UsageRecord>(), now).LatestSessionId == "");

        var file = Path.Combine(dataDir, "active-session-test.txt");
        Check("missing active session -> empty", CompactService.ReadActiveSession(file) == "");
        CompactService.WriteActiveSession("abc-123", file);
        Check("active session roundtrip", CompactService.ReadActiveSession(file) == "abc-123");
        Check("default path in DataDir", CompactService.ActiveSessionPath == Path.Combine(dataDir, "active-session.txt"));

        var r = CompactService.RunCompactAsync("").GetAwaiter().GetResult();
        Check("empty session -> compact.noSession", !r.Ok && r.MessageKey == "compact.noSession");

        var cfg = new AppConfig { ContextAlertTokens = 0 };
        var hot = new AgyQuotaSnapshot { Items = { new AgyQuotaItem("Gemini 4 Argon", "Gemini 4 Argon Weekly Limit", 3, null) } };
        var alerts = new AlertService(Path.Combine(dataDir, "alert-argon.json"));
        var fired = alerts.Check(sum, cfg, hot);
        Check("argon7d alert fires", fired.Count == 1 && fired[0].Kind == "argon7d" && fired[0].Threshold == 95,
            string.Join(",", fired.Select(a => a.Kind)));
        Check("argon7d no repeat", alerts.Check(sum, cfg, hot).Count == 0);

        var en = Lang.Strings(Lang.En);
        var vi = Lang.Strings(Lang.Vi);
        Check("compact strings EN+VI", new[] { "btn.compact", "tray.compactSession", "compact.title", "compact.running",
                "compact.note", "compact.done", "compact.noSession", "compact.noAgy", "compact.failed",
                "compact.failedDetail", "alert.kindArgon7d" }
            .All(k => en.ContainsKey(k) && vi.ContainsKey(k)));
    }

    private static void TestRealScan()
    {
        Console.WriteLine("Real BrainRoot smoke");
        var root = TrackerPaths.BrainRoot;
        var sc = new TranscriptScanner();
        var recs = sc.Scan();
        var s = StatsService.Compute(recs, DateTime.UtcNow, 0, 0);
        Console.WriteLine($"  root={root} exists={Directory.Exists(root)} records={recs.Count} 5h={s.Total5h} 7d={s.Total7d} model={s.LatestModel}");
        Check("scan + stats don't throw", s.RecordCount == recs.Count(r => !r.IsError || r.TotalTokens > 0));
    }
}
