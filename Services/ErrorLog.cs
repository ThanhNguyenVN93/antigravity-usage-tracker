namespace AntigravityUsageTracker.Services;

public static class ErrorLog
{
    private const long MaxBytes = 256 * 1024;

    public static string PathOnDisk => Path.Combine(TrackerPaths.DataDir, "error.log");

    public static void Write(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(TrackerPaths.DataDir);
            var info = new FileInfo(PathOnDisk);
            if (info.Exists && info.Length > MaxBytes)
                File.Move(PathOnDisk, PathOnDisk + ".old", overwrite: true);

            File.AppendAllText(PathOnDisk,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // never throw from the logger
        }
    }
}
