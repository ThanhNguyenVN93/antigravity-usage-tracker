using System.IO;

namespace AntigravityUsageTracker.Services;

// Watches BrainRoot for transcript.jsonl changes; raises Changed once after a 1.5s quiet period.
public sealed class TranscriptWatcher : IDisposable
{
    private readonly string? _root;
    private readonly TimeSpan _debounce;
    private FileSystemWatcher? _fsw;
    private Timer? _timer;
    private bool _disposed;

    public event Action? Changed;

    public TranscriptWatcher(string? brainRoot = null, TimeSpan? debounce = null)
    {
        _root = brainRoot;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(1500);
    }

    public bool Start()
    {
        try
        {
            var root = _root ?? TrackerPaths.BrainRoot;
            if (!Directory.Exists(root)) return false;

            _timer = new Timer(_ => { if (!_disposed) Changed?.Invoke(); }, null, Timeout.Infinite, Timeout.Infinite);
            _fsw = new FileSystemWatcher(root, "transcript.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _fsw.Changed += OnFsEvent;
            _fsw.Created += OnFsEvent;
            _fsw.Deleted += OnFsEvent;
            _fsw.Renamed += (_, _) => Kick();
            _fsw.Error += (_, e) => ErrorLog.Write("TranscriptWatcher", e.GetException());
            _fsw.EnableRaisingEvents = true;
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("TranscriptWatcher.Start", ex);
            return false;
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => Kick();

    private void Kick()
    {
        try { _timer?.Change(_debounce, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _disposed = true;
        _fsw?.Dispose();
        _timer?.Dispose();
    }
}
