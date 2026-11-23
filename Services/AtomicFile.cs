namespace AntigravityUsageTracker.Services;

// Write to a temp file then move over: a crash never leaves a truncated file.
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        string temp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }
}
