using System.Text.Json;

namespace AppClone.Core;

/// <summary>
/// 轻量设置（data\settings.json）：扫描缓存开关、各扫描源开关。
/// 仅保留基础读写，M3 T17 的"设置面板"复用此处。
/// </summary>
public class Settings
{
    public bool DisableDiscoveryCache { get; set; }
    public bool ScanSteam { get; set; } = true;
    public bool ScanEpic { get; set; }
    public bool ScanUwp { get; set; } = true;
    public bool ScanWinget { get; set; }

    private static Settings _current = new();
    private static readonly object _lock = new();
    private static bool _loaded;

    public static Settings Current
    {
        get { lock (_lock) { if (!_loaded) Load(); return _current; } }
    }

    /// <summary>已添加应用的规范化 exe 路径集合（供发现页去重比对）。</summary>
    public static void Load()
    {
        lock (_lock)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (File.Exists(Paths.SettingsFile))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile));
                    if (s != null) _current = s;
                }
            }
            catch { }
        }
    }

    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                Paths.EnsureAll();
                File.WriteAllText(Paths.SettingsFile,
                    JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    // 便捷访问器（供各处直接读开关）
    public static bool ScanSteamEnabled => Current.ScanSteam;
    public static bool ScanEpicEnabled => Current.ScanEpic;
    public static bool ScanUwpEnabled => Current.ScanUwp;
    public static bool ScanWingetEnabled => Current.ScanWinget;
}
