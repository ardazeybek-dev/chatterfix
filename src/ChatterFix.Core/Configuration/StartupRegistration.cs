using Microsoft.Win32;

namespace ChatterFix.Core.Configuration;

/// <summary>
/// Registers the application to start with Windows.
///
/// This uses the per-user Run key rather than a scheduled task or a service:
/// the filter must run in the same session as the desktop, because a low-level
/// hook only sees input for the session it belongs to.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ChatterFix";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Returns true when the change was applied.</summary>
    public static bool SetEnabled(bool enabled, string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;

            if (enabled)
                key.SetValue(ValueName, $"\"{executablePath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
