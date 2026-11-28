using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntigravityUsageTracker.Services;

public record ReleaseInfo(string Tag, Version Version, string HtmlUrl, string ZipName, string ZipUrl, string SumsUrl, string? Notes);

public record UpdateOptions(string ApiBase, string AllowedAssetPrefix)
{
    public const string DefaultApiBase = "https://api.github.com/repos/ThanhNguyenVN93/antigravity-usage-tracker";
    public const string DefaultAssetPrefix = "https://github.com/ThanhNguyenVN93/antigravity-usage-tracker/releases/download/";

    // Local override for automated tests only.
    public static UpdateOptions FromEnvironment()
    {
        string? over = Environment.GetEnvironmentVariable("ANTIGRAVITY_USAGE_TRACKER_UPDATE_API");
        if (!string.IsNullOrWhiteSpace(over)
            && (over.StartsWith("http://127.0.0.1", StringComparison.Ordinal) || over.StartsWith("http://localhost", StringComparison.Ordinal)))
        {
            string origin = over.TrimEnd('/') + "/";
            return new UpdateOptions(over.TrimEnd('/'), origin);
        }
        return new UpdateOptions(DefaultApiBase, DefaultAssetPrefix);
    }
}

public static class UpdateLogic
{
    public const string ExeName = "AntigravityUsageTracker.exe";
    public const string SumsName = "SHA256SUMS.txt";
    public const long MaxZipBytes = 300L * 1024 * 1024;
    public const long MaxExeBytes = 300L * 1024 * 1024;

    private static readonly Regex TagRx = new(@"^v?(\d+)\.(\d+)\.(\d+)$", RegexOptions.Compiled);
    private static readonly Regex ZipRx = new(@"^AntigravityUsageTracker-v(\d+\.\d+\.\d+)-win-x64\.zip$", RegexOptions.Compiled);
    private static readonly Regex HashRx = new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = TagRx.Match(tag.Trim());
        return m.Success
            ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
            : null;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static bool IsNewer(Version current, Version latest) => latest > Normalize(current);

    public static ReleaseInfo? ParseRelease(string json, string allowedAssetPrefix)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var version = ParseVersion(tag);
            if (version == null || !root.TryGetProperty("assets", out var assets)) return null;

            string? zipName = null, zipUrl = null, sumsUrl = null;
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name == null || url == null) continue;
                if (!url.StartsWith(allowedAssetPrefix, StringComparison.Ordinal)) continue;

                var zm = ZipRx.Match(name);
                if (zm.Success && zm.Groups[1].Value == version.ToString(3)) { zipName = name; zipUrl = url; }
                else if (name == SumsName) sumsUrl = url;
            }

            if (zipName == null || zipUrl == null || sumsUrl == null) return null;

            string html = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            string? body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            if (body != null && body.Length > 800) body = body[..800] + "…";

            return new ReleaseInfo(tag!, version, html, zipName, zipUrl, sumsUrl, body);
        }
        catch
        {
            return null;
        }
    }

    public static string? ParseSha256(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            int sp = line.IndexOf(' ');
            if (sp != 64) continue;
            string hash = line[..64];
            string name = line[64..].Trim().TrimStart('*');
            if (name == fileName && HashRx.IsMatch(hash)) return hash.ToLowerInvariant();
        }
        return null;
    }

    public static bool VerifySha256(string filePath, string expectedHex)
    {
        using var fs = File.OpenRead(filePath);
        string actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractExe(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        string destFull = Path.GetFullPath(destDir);

        using var zip = ZipFile.OpenRead(zipPath);
        bool exeFound = false;
        long totalExtracted = 0;

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string dest = Path.GetFullPath(Path.Combine(destFull, relative));
            if (!dest.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Blocked zip path traversal: {entry.FullName}");

            if (entry.Length > MaxExeBytes)
                throw new InvalidDataException($"Entry exceeds size limit: {entry.Name}");

            totalExtracted += entry.Length;
            if (totalExtracted > MaxZipBytes)
                throw new InvalidDataException("Extracted size exceeds limit");

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var input = entry.Open())
            using (var output = File.Create(dest))
                input.CopyTo(output);

            if (!relative.Contains(Path.DirectorySeparatorChar)
                && entry.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                exeFound = true;
        }

        if (!exeFound) throw new InvalidDataException($"Zip does not contain {ExeName} at root");

        string exePath = Path.Combine(destFull, ExeName);
        using var fs = File.OpenRead(exePath);
        if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
            throw new InvalidDataException("Staged file is not a valid Windows executable");
        return exePath;
    }

    public static void CleanupOld(string currentPath)
    {
        try { File.Delete(currentPath + ".old"); } catch { }
    }

    public static void ApplyStagedUpdate(string stagedExePath, string currentExePath)
    {
        string stagedDir = Path.GetDirectoryName(stagedExePath)!;
        string targetDir = Path.GetDirectoryName(currentExePath)!;
        string tempRoot = Path.GetDirectoryName(stagedDir)!;
        string scriptPath = Path.Combine(tempRoot, "apply-update.ps1");
        int pid = Environment.ProcessId;

        string Esc(string s) => s.Replace("'", "''");
        string script =
            $"$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            $"Wait-Process -Id {pid} -Timeout 15\r\n" +
            $"Start-Sleep -Milliseconds 300\r\n" +
            $"Copy-Item -Path '{Esc(stagedDir)}\\*' -Destination '{Esc(targetDir)}' -Recurse -Force\r\n" +
            $"Start-Process -FilePath '{Esc(currentExePath)}'\r\n" +
            $"Start-Sleep -Seconds 2\r\n" +
            $"Remove-Item -Path '{Esc(tempRoot)}' -Recurse -Force\r\n";

        File.WriteAllText(scriptPath, script, Encoding.UTF8);

        // CreateProcess does not search PATH for a bare file name, so resolve the full path.
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        HiddenProcess.StartDetached(powershell,
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", scriptPath]);
    }
}

public sealed class UpdateService : IDisposable
{
    private readonly Version _current;
    private readonly UpdateOptions _options;
    private readonly HttpClient _http;

    public UpdateService(Version currentVersion, UpdateOptions? options = null, HttpMessageHandler? handler = null)
    {
        _current = currentVersion;
        _options = options ?? UpdateOptions.FromEnvironment();
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"AntigravityUsageTracker/{UpdateLogic.Normalize(currentVersion)}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"{_options.ApiBase}/releases/latest", ct);
        if (!resp.IsSuccessStatusCode) return null;

        string json = await resp.Content.ReadAsStringAsync(ct);
        var release = UpdateLogic.ParseRelease(json, _options.AllowedAssetPrefix);
        return release != null && UpdateLogic.IsNewer(_current, release.Version) ? release : null;
    }

    public async Task<string> DownloadAndStageAsync(ReleaseInfo release, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AntigravityUsageTracker-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string sums = await DownloadTextAsync(release.SumsUrl, 8 * 1024, ct);
            string? expected = UpdateLogic.ParseSha256(sums, release.ZipName);
            if (expected == null)
                throw new InvalidDataException($"{UpdateLogic.SumsName} does not contain hash for {release.ZipName}");

            string zipPath = Path.Combine(dir, release.ZipName);
            await DownloadFileAsync(release.ZipUrl, zipPath, UpdateLogic.MaxZipBytes, ct);

            if (!UpdateLogic.VerifySha256(zipPath, expected))
                throw new InvalidDataException("SHA-256 mismatch — update aborted");

            return UpdateLogic.ExtractExe(zipPath, Path.Combine(dir, "extracted"));
        }
        catch
        {
            try { Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    private async Task<string> DownloadTextAsync(string url, long maxBytes, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("File exceeds size limit");

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        await CopyLimitedAsync(stream, ms, maxBytes, ct);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task DownloadFileAsync(string url, string path, long maxBytes, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("File exceeds size limit");

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var fs = File.Create(path);
        await CopyLimitedAsync(stream, fs, maxBytes, ct);
    }

    public void Dispose() => _http.Dispose();

    private static async Task CopyLimitedAsync(Stream input, Stream output, long maxBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException("File exceeds size limit");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }
}
