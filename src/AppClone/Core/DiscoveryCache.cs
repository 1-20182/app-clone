using System.Text.Json;
using Microsoft.Win32;

namespace AppClone.Core;

/// <summary>一条被发现的已安装应用（扫描缓存统一数据类，不混进 apps.json）。</summary>
public class DiscoveredApp
{
    public string Name { get; set; } = "";              // 展示名（DisplayName，回退文件名）
    public string ExePath { get; set; } = "";           // 规范化前的真实路径（主键）
    public string DisplayName { get; set; } = "";       // 原始 DisplayName
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string IconPath { get; set; } = "";          // DisplayIcon 原路径（供 AppIcons 提取）
    public string Source { get; set; } = "";            // uninstall|apppaths|startmenu|steam|epic|uwp
    public string Category { get; set; } = "";          // 分类词典命中结果（由 Merge 填充）
    public bool Recommended { get; set; }               // 是否判为「推荐分身」
    public bool Supported { get; set; } = true;         // UWP=false（灰显不支持）
    public DateTime FoundAt { get; set; } = DateTime.Now;

    // —— 运行期辅助字段（不持久化关键逻辑，但随缓存落盘无害）——
    public bool Excluded { get; set; }                  // 注册表噪音预过滤
    public string Tier { get; set; } = "";              // 推荐三档标签
    public bool AlreadyAdded { get; set; }             // 是否已存在于库中（置灰）
}

/// <summary>增量指纹：Uninstall 三键的子键数 + 最后写入时间（FILETIME）。</summary>
public class SourceStamp
{
    public int KeyCount { get; set; }
    public long LastWrite { get; set; }
}

/// <summary>扫描结果落盘数据类。</summary>
public class DiscoveryCache
{
    public const int SchemaVersion = 1;
    public int SchemaVersion_ { get; set; } = SchemaVersion;
    public DateTime ScannedAt { get; set; }
    public string MachineId { get; set; } = "";
    public Dictionary<string, SourceStamp> Stamps { get; set; } = new();
    public List<DiscoveredApp> Apps { get; set; } = new();
}

internal static class DiscoveryCacheIO
{
    private static readonly object _ioLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DiscoveryCache? Load()
    {
        if (Settings.Current.DisableDiscoveryCache) return null;
        try
        {
            if (!File.Exists(Paths.DiscoveryCacheFile)) return null;
            var json = File.ReadAllText(Paths.DiscoveryCacheFile);
            var c = JsonSerializer.Deserialize<DiscoveryCache>(json, JsonOpts);
            if (c == null || c.SchemaVersion_ != DiscoveryCache.SchemaVersion) { SafeDelete(); return null; }
            return c;
        }
        catch { SafeDelete(); return null; }
    }

    public static void Save(DiscoveryCache c)
    {
        if (Settings.Current.DisableDiscoveryCache) return;
        lock (_ioLock)
        {
            try
            {
                Paths.EnsureAll();
                File.WriteAllText(Paths.DiscoveryCacheFile, JsonSerializer.Serialize(c, JsonOpts));
            }
            catch { }
        }
    }

    public static void SafeDelete()
    {
        try { if (File.Exists(Paths.DiscoveryCacheFile)) File.Delete(Paths.DiscoveryCacheFile); } catch { }
    }

    /// <summary>读取 Uninstall 三键的廉价指纹（子键数 + 最后写入时间）。</summary>
    public static Dictionary<string, SourceStamp> CurrentStamps()
    {
        var stamps = new Dictionary<string, SourceStamp>();
        foreach (var (name, root, view, sub) in UninstallKeys())
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(root, view).OpenSubKey(sub);
                if (key == null) { stamps[name] = new SourceStamp { KeyCount = 0, LastWrite = 0 }; continue; }
                stamps[name] = new SourceStamp { KeyCount = key.SubKeyCount, LastWrite = LastWriteTime(key) };
            }
            catch { stamps[name] = new SourceStamp { KeyCount = 0, LastWrite = 0 }; }
        }
        return stamps;
    }

    /// <summary>任意源指纹变化即视为需全量重扫（简单稳健，仍 <3s）。</summary>
    public static bool NeedsRescan(DiscoveryCache cache)
    {
        var cur = CurrentStamps();
        foreach (var kv in cur)
        {
            if (!cache.Stamps.TryGetValue(kv.Key, out var old) ||
                old.KeyCount != kv.Value.KeyCount || old.LastWrite != kv.Value.LastWrite)
                return true;
        }
        return false;
    }

    private static IEnumerable<(string Name, RegistryHive Root, RegistryView View, string Sub)> UninstallKeys()
    {
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        yield return ("uninstall_hklm64", RegistryHive.LocalMachine, RegistryView.Registry64, sub);
        yield return ("uninstall_hklm32", RegistryHive.LocalMachine, RegistryView.Registry32, sub);
        yield return ("uninstall_hkcu", RegistryHive.CurrentUser, RegistryView.Default, sub);
    }

    private static long LastWriteTime(RegistryKey key)
    {
        try
        {
            var hk = key.Handle.DangerousGetHandle();
            if (RegQueryInfoKeyW(hk, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out int subKeys,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    out long lastWrite) == 0)
                return lastWrite;
        }
        catch { }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(
        IntPtr hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        out int lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen,
        IntPtr lpcValues, IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);
}
