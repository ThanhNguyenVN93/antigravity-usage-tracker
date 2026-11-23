using AntigravityUsageTracker.Models;

namespace AntigravityUsageTracker.Services;

public sealed class TranscriptScanner
{
    private sealed record CacheEntry(DateTime LastWriteUtc, long Length, List<UsageRecord> Records);

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly string? _root;

    public TranscriptScanner(string? brainRoot = null) { _root = brainRoot; }

    public string Root => _root ?? TrackerPaths.BrainRoot;

    /// <summary>Explicit root or env override -> that root only; otherwise legacy + CLI brain roots.</summary>
    public IReadOnlyList<string> Roots
    {
        get
        {
            if (_root != null
                || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTIGRAVITY_BRAIN_DIR")))
                return new[] { Root };
            return new[] { TrackerPaths.BrainRoot, TrackerPaths.CliBrainRoot };
        }
    }

    /// <summary>Number of file parses performed (cache misses); useful for tests.</summary>
    public int ParseCount { get; private set; }

    public IReadOnlyList<UsageRecord> Scan()
    {
        lock (_lock)
        {
            var all = new List<UsageRecord>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in Roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root); }
                catch (Exception ex) { ErrorLog.Write("Scanner.GetDirectories", ex); dirs = Array.Empty<string>(); }

                foreach (var dir in dirs)
                {
                    var file = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
                    try
                    {
                        var info = new FileInfo(file);
                        if (!info.Exists) continue;
                        if (!seen.Add(file)) continue;

                        if (!_cache.TryGetValue(file, out var entry)
                            || entry.LastWriteUtc != info.LastWriteTimeUtc || entry.Length != info.Length)
                        {
                            ParseCount++;
                            entry = new CacheEntry(info.LastWriteTimeUtc, info.Length, TranscriptParser.ParseFile(file));
                            _cache[file] = entry;
                        }
                        all.AddRange(entry.Records);
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Write("Scanner.File " + file, ex);
                    }
                }
            }

            foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
                _cache.Remove(stale);

            all.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
            return all;
        }
    }
}
