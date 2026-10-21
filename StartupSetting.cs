using Microsoft.Win32;

namespace AudioDesk;

/// <summary>"Start with Windows": a per-user Run entry that starts AudioDesk in the tray at sign-in.</summary>
internal static class StartupSetting
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AudioDesk";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
