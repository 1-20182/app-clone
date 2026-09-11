using System.Diagnostics;
using Microsoft.Win32;

namespace AppClone.Core;

/// <summary>开机自启：通过 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 读写 AppClone 值。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AppClone";

    /// <summary>启用/禁用开机自启（路径加引号写入 Run 键）。</summary>
    public static void SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    try { exe = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
                }
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, "\"" + exe + "\"");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch { }
    }

    /// <summary>是否已启用开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (key == null) return false;
            var v = key.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch { return false; }
    }
}
