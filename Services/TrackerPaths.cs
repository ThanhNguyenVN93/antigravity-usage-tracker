namespace AntigravityUsageTracker.Services;

public static class TrackerPaths
{
    public static string BrainRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ANTIGRAVITY_BRAIN_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "antigravity", "brain");
        }
    }

    public static string CliBrainRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "antigravity-cli", "brain");

    public static string DataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ANTIGRAVITY_USAGE_TRACKER_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AntigravityUsageTracker");
        }
    }
}
