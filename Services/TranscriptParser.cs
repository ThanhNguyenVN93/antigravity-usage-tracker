using System.Globalization;
using System.Text.Json;
using AntigravityUsageTracker.Models;

namespace AntigravityUsageTracker.Services;

public static class TranscriptParser
{
    // <brain>\<guid>\.system_generated\logs\transcript.jsonl -> <guid>
    public static string SessionIdFromPath(string transcriptPath)
    {
        try
        {
            var logs = Path.GetDirectoryName(transcriptPath);
            var sys = logs == null ? null : Path.GetDirectoryName(logs);
            var session = sys == null ? null : Path.GetDirectoryName(sys);
            var name = session == null ? null : Path.GetFileName(session);
            return string.IsNullOrEmpty(name) ? "unknown" : name;
        }
        catch { return "unknown"; }
    }

    public static List<UsageRecord> ParseFile(string path)
    {
        var sessionId = SessionIdFromPath(path);
        var result = new List<UsageRecord>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (TryParseLine(line, sessionId, out var rec)) result.Add(rec);
        }
        return result;
    }

    public static List<UsageRecord> ParseLines(IEnumerable<string> lines, string sessionId)
    {
        var result = new List<UsageRecord>();
        foreach (var line in lines)
            if (TryParseLine(line, sessionId, out var rec)) result.Add(rec);
        return result;
    }

    public static bool TryParseLine(string line, string sessionId, out UsageRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            bool hasIn = TryLong(root, "input_tokens", out long input);
            bool hasOut = TryLong(root, "output_tokens", out long output);
            TryLong(root, "cache_read_tokens", out long cache);

            if (!root.TryGetProperty("created_at", out var ts) || ts.ValueKind != JsonValueKind.String) return false;
            if (!DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
                return false;

            if (!hasIn && !hasOut)
            {
                if (!IsErrorRow(root)) return false;
                record = new UsageRecord(when.UtcDateTime, "", 0, 0, 0, sessionId, null, true, ErrorSnippetOf(root));
                return true;
            }

            string model = "unknown";
            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(m.GetString()))
                model = m.GetString()!;

            record = new UsageRecord(when.UtcDateTime, model,
                Math.Max(0, input), Math.Max(0, cache), Math.Max(0, output), sessionId, ExtractTools(root));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsErrorRow(JsonElement root) =>
        IsString(root, "type", "ERROR_MESSAGE") || IsString(root, "status", "ERROR");

    public const int MaxSnippetLength = 140;

    private static string ErrorSnippetOf(JsonElement root)
    {
        string text = "";
        if (root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            text = Collapse(c.GetString());
        if (text.Length == 0 && root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
            text = Collapse(t.GetString());
        if (text.Length == 0) text = "ERROR";
        return text.Length > MaxSnippetLength ? text.Substring(0, MaxSnippetLength - 3) + "..." : text;
    }

    private static string Collapse(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsString(JsonElement obj, string name, string expected) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
        && string.Equals(p.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static string[]? ExtractTools(JsonElement root)
    {
        if (!root.TryGetProperty("tool_calls", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var names = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                names.Add(n.GetString()!);
        }
        return names.Count > 0 ? names.ToArray() : null;
    }

    private static bool TryLong(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number)
        {
            if (p.TryGetInt64(out value)) return true;
            if (p.TryGetDouble(out var d)) { value = (long)d; return true; }
            return false;
        }
        if (p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value))
            return true;
        return false;
    }
}
