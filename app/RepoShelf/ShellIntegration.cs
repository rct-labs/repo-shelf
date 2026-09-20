using Microsoft.Win32;

namespace RepoShelf;

/// <summary>Shell integration: launch-at-login via the HKCU Run key.</summary>
public static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RepoShelf";

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)!;
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown");
            key.SetValue(ValueName, $"\"{exe}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
