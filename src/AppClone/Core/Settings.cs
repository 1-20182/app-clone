using System.IO;
using System.Text.Json;

namespace AppClone.Core;

/// <summary>外观主题。</summary>
public enum AppTheme { Light, Dark }

/// <summary>分身隔离模式（对应 CloneEntry.DeepIsolation）。</summary>
public enum IsolationMode { Standard, Deep }

/// <summary>关闭主窗口时的行为。</summary>
public enum CloseBehavior { Minimize, Exit }

/// <summary>
/// 全局设置模型（设计 §3.8）：全部字段带默认值。
/// Load/Save 走 JSON + .bak 备份 + 空仓启动；老 settings.json 缺失字段取默认、数据不丢。
/// 设置文件 settings.json 独立于 apps.json / discovered.json，互不污染。
/// </summary>
public class Settings
{
    // —— 外观 ——
    public AppTheme Theme { get; set; } = AppTheme.Light;

    // —— 发现 / 扫描源开关（默认开启）——
    /// <summary>新字段：发现缓存总开关（默认开启）。</summary>
    public bool EnableDiscoveryCache { get; set; } = true;

    /// <summary>
    /// 兼容旧 settings.json 的反向语义字段（老文件用 DisableDiscoveryCache）。
    /// 读取时始终映射为 !EnableDiscoveryCache；旧文件反序列化由 Load() 的兼容迁移处理。
    /// DiscoveryCacheIO 仍读取此属性，语义与 EnableDiscoveryCache 保持一致。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool DisableDiscoveryCache { get => !EnableDiscoveryCache; set => EnableDiscoveryCache = !value; }
    public bool ScanSteam { get; set; } = true;
    public bool ScanEpic { get; set; } = false;
    public bool ScanUwp { get; set; } = true;
    public bool ScanWinget { get; set; } = false;

    // —— 批量添加默认（读自此处，T07 验收）——
    public IsolationMode DefaultIsolationMode { get; set; } = IsolationMode.Deep;
    public bool DefaultDeviceClone { get; set; } = true;
    public int DefaultCloneCount { get; set; } = 1;        // 1 / 2 / 3

    // —— 启动与窗口行为 ——
    public bool LaunchOnStartup { get; set; } = false;       // 用户显式 → 写 Run 键
    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Minimize;
    public bool LaunchBehavior { get; set; } = false;        // 双击原图标走分身（P1 R7 关联）
    public bool MinimizeToTray { get; set; } = false;        // 需 NotifyIcon（P2 接线）
    public bool TrayShowCount { get; set; } = true;

    // —— 交互与安全 ——
    public bool ConfirmDestructive { get; set; } = true;     // 删除 / 重置前确认
    public bool RememberDontAsk { get; set; } = false;       // 记住"不再询问"
    public bool ShowDataUsage { get; set; } = false;         // 数据占用提示
    public bool UpdateCheck { get; set; } = true;            // 启动检查更新（P2 接线）

    // —— P1 启动密码锁 ——
    /// <summary>PBKDF2 哈希串「salt:hash」(Base64)；空串 = 未启用。明文永不落盘。</summary>
    public string PasswordHash { get; set; } = "";

    // —— 增量 1.3.0（F3）——
    public bool ShowDryRun { get; set; } = true;             // 启动前显示执行预览（Dry Run），默认开
    public int MinFreeDiskGB { get; set; } = 2;              // 预检查的磁盘剩余空间阈值（GB）

    // —— 增量 1.4.0（F5 / F6）——
    public bool WriteOpsLog { get; set; } = true;            // 写操作流水日志（data\logs\ops-*.jsonl，保留 7 天）
    public LogLevel MinLogLevel { get; set; } = LogLevel.Info; // 落盘的最低级别（Debug 会把观察窗心跳也写进去）
    public bool BadgeShortcutIcon { get; set; } = true;      // 桌面快捷方式使用带序号角标的图标
    public bool TaskbarGrouping { get; set; } = false;       // 实验性：为快捷方式写入 AppUserModelID，便于任务栏分组

    // —— P2 窗口位置记忆（W/H = 0 表示从未记录，回退默认居中）——
    // 存物理像素：跨 DPI 更换屏幕时由 MainForm 做越界回收，不会把窗口丢到屏幕外。
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowW { get; set; }
    public int WindowH { get; set; }
    public bool WindowMaximized { get; set; }

    // —— P3 新手引导 ——
    /// <summary>新手教程是否已完成/已跳过（首次启动自动播放，可从菜单重播）。</summary>
    public bool OnboardingDone { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static Settings _current = new();
    private static readonly object _lock = new();
    private static bool _loaded;

    /// <summary>设置单例：首次访问时 Load()。任意修改后调用 Save() 持久化。</summary>
    public static Settings Current
    {
        get { lock (_lock) { if (!_loaded) Load(); return _current; } }
    }

    /// <summary>
    /// JSON + .bak 备份 + 空仓启动。settings.json 损坏时备份为 .bak 并以默认值启动（数据不丢）；
    /// 若 .bak 可解析则用 .bak 恢复。
    /// </summary>
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
                var text = File.ReadAllText(Paths.SettingsFile);
                var s = JsonSerializer.Deserialize<Settings>(text, JsonOpts);
                if (s != null)
                {
                    // 兼容迁移：旧文件用反向语义 DisableDiscoveryCache，新版用 EnableDiscoveryCache
                    try
                    {
                        using var doc = JsonDocument.Parse(text);
                        if (doc.RootElement.TryGetProperty("DisableDiscoveryCache", out var d) &&
                            !doc.RootElement.TryGetProperty("EnableDiscoveryCache", out _))
                            s.EnableDiscoveryCache = !d.GetBoolean();
                    }
                    catch { }
                    _current = s;
                    return;
                }
            }
                // 文件缺失 → 默认值启动（空仓启动）
                return;
            }
            catch
            {
                // 损坏 → 备份 + 默认值启动
                try { if (File.Exists(Paths.SettingsFile)) File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".bak", true); } catch { }
            }
            // 尝试从 .bak 恢复一次
            try
            {
                if (File.Exists(Paths.SettingsFile + ".bak"))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile + ".bak"), JsonOpts);
                    if (s != null) _current = s;
                }
            }
            catch { }
        }
    }

    /// <summary>持久化当前实例；写之前把上一份好数据备份为 .bak。</summary>
    public void Save()
    {
        lock (_lock)
        {
            try
            {
                Paths.EnsureAll();
                if (File.Exists(Paths.SettingsFile))
                    try { File.Copy(Paths.SettingsFile, Paths.SettingsFile + ".bak", true); } catch { }
                File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch { }
        }
    }

    /// <summary>映射扫描源开关 → AppDiscovery.DiscoveryOptions（T03 / T05 验收：发现扫描源默认读此处）。</summary>
    public AppDiscovery.DiscoveryOptions ToDiscoveryOptions() => new()
    {
        Steam = ScanSteam,
        Epic = ScanEpic,
        Uwp = ScanUwp,
        Winget = ScanWinget,
    };
}
