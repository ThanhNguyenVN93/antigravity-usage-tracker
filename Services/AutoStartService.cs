using Microsoft.Win32;

namespace AntigravityUsageTracker.Services;

public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AntigravityUsageTracker";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) { ErrorLog.Write("AutoStart.IsEnabled", ex); return false; }
    }

    public static bool SetEnabled(bool enabled, string? exePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                exePath ??= Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;
                key.SetValue(ValueName, "\"" + exePath + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex) { ErrorLog.Write("AutoStart.SetEnabled", ex); return false; }
    }
}
