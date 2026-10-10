using Microsoft.Win32;

namespace HuaCi.Services;

/// <summary>通过 HKCU\...\Run 实现开机自启。</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HuaCi";
    public const string AutoStartArg = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {AutoStartArg}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string s && s.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        else if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>如果 exe 被移动过，更新注册表中的路径。</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string s && !string.Equals(s, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warn("刷新自启路径失败：" + ex.Message);
        }
    }
}
