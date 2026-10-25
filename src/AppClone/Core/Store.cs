using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppClone.Core;

public class AppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Args { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.Now;

    // ↓ 增量 1.1.0 新增字段（全部带默认值，老文件反序列化后取默认值，数据不丢）
    public string Source { get; set; } = "manual";   // manual | uninstall | apppaths | startmenu | steam | epic | uwp
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string Category { get; set; } = "";        // browser | social | game | dev | cloud | office | other | uwp
    public bool Recommended { get; set; }
}

public class CloneEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>深度隔离：注册表重定向 + 互斥体改名（注入 DLL）。</summary>
    public bool DeepIsolation { get; set; } = true;
    /// <summary>设备分身：随机设备名 / MachineGuid / 卷序列号等设备指纹。</summary>
    public bool DeviceClone { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ↓ 增量 1.1.0：分身角标颜色（空=默认蓝；否则 CloneColors 中的十六进制）
    public string Color { get; set; } = "";

    // ↓ 增量 1.2.0：启动热键（如 "Ctrl+Alt+1"，空=未绑定）
    public string Hotkey { get; set; } = "";

    // ↓ P1：快捷方式伪装（Disguise.All 的 Key；""=正常显示）
    public string Disguise { get; set; } = "";

    // ↓ P2：崩溃守护 —— 启动后早退崩溃时自动重启（最多 2 次）
    public bool Guard { get; set; }

    // ↓ P3：冻结 —— 禁止启动（右键「冻结分身」切换），运行中的实例会被先结束
    public bool Frozen { get; set; }

    // ↓ P3：网络代理 —— 只注入标准代理环境变量（Chromium / Electron / .NET 等遵守者生效）
    public string Proxy { get; set; } = "";

    // ↓ P4：随软件启动时自动运行该分身
    public bool AutoStart { get; set; }

    // ↓ P4：每天定时启动 "HH:mm"（空=不定时；依赖本软件常驻运行或托盘常驻）
    public string AutoStartAt { get; set; } = "";

    // ↓ 增量 1.3.0（F2）：创建分身那一刻的「源应用基线」，用于检测源应用升级 / 被替换。
    //   三项均带默认值 —— 老 apps.json 反序列化后为空，由 SourceWatch 静默回填，不产生误报。
    public string SourceVersionAtCreate { get; set; } = "";
    public string SourceExeMtimeAtCreate { get; set; } = "";
    public long SourceExeSizeAtCreate { get; set; }
}

public class StoreModel
{
    [JsonPropertyName("apps")] public List<AppEntry> Apps { get; set; } = new();
    [JsonPropertyName("clones")] public Dictionary<string, List<CloneEntry>> Clones { get; set; } = new();
}

/// <summary>JSON 持久化的应用与分身仓库。</summary>
public class Store
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public StoreModel Model { get; private set; } = new();
    private readonly object _sync = new();

    public static Store Load()
    {
        var s = new Store();
        Paths.EnsureAll();

        // 主文件读取：失败时保留 .corrupt 供人工找回，随后优先尝试 .bak 恢复
        bool mainOk = false;
        if (File.Exists(Paths.StoreFile))
        {
            try
            {
                var json = File.ReadAllText(Paths.StoreFile);
                s.Model = JsonSerializer.Deserialize<StoreModel>(json, JsonOpts) ?? new StoreModel();
                mainOk = true;
            }
            catch
            {
                try { File.Copy(Paths.StoreFile, Paths.StoreFile + ".corrupt", true); } catch { }
            }
        }

        if (!mainOk)
        {
            // 主文件缺失或损坏：尝试 .bak 恢复；.bak 也不可用才以空仓库启动
            try
            {
                if (File.Exists(Paths.StoreFile + ".bak"))
                {
                    var bak = File.ReadAllText(Paths.StoreFile + ".bak");
                    s.Model = JsonSerializer.Deserialize<StoreModel>(bak, JsonOpts) ?? new StoreModel();
                }
            }
            catch
            {
                s.Model = new StoreModel();
            }
        }
        return s;
    }

    public void Save()
    {
        lock (_sync)
        {
            Paths.EnsureAll();
            var json = JsonSerializer.Serialize(Model, JsonOpts);
            var tmp = Paths.StoreFile + ".tmp";
            var bak = Paths.StoreFile + ".bak";
            try
            {
                // 原子写：先落 .tmp，成功后 Replace（同目录同卷）交换主文件并把旧主文件滚动为 .bak
                File.WriteAllText(tmp, json);
                if (File.Exists(Paths.StoreFile))
                {
                    File.Replace(tmp, Paths.StoreFile, bak);
                }
                else
                {
                    // 首次写入无主文件：直接提交，并同步生成一份 .bak
                    File.Move(tmp, Paths.StoreFile, overwrite: true);
                    try { File.Copy(Paths.StoreFile, bak, true); } catch { }
                }
            }
            catch
            {
                // 失败时清理残留 tmp；主文件保持上一份好数据，异常继续上抛（与原语义一致）
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    public List<CloneEntry> ClonesOf(string appId) =>
        Model.Clones.TryGetValue(appId, out var list) ? list : new List<CloneEntry>();

    public AppEntry? FindApp(string appId) => Model.Apps.FirstOrDefault(a => a.Id == appId);

    public CloneEntry? FindClone(string appId, string cloneId) =>
        ClonesOf(appId).FirstOrDefault(c => c.Id == cloneId);

    /// <summary>已添加应用的规范化 exe 路径集合（供发现页去重比对）。</summary>
    public HashSet<string> AddedExeKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Model.Apps)
            if (!string.IsNullOrWhiteSpace(a.ExePath))
                set.Add(AppClassifier.NormalizeExePath(a.ExePath));
        return set;
    }

    public AppEntry AddApp(string exePath, string? nameOverride = null)
    {
        var app = new AppEntry
        {
            Name = nameOverride ?? Path.GetFileNameWithoutExtension(exePath),
            ExePath = exePath,
        };
        Model.Apps.Add(app);
        Model.Clones[app.Id] = new List<CloneEntry>();
        Save();
        return app;
    }

    /// <summary>
    /// 从发现结果入库单条应用：写 AppEntry（名称取 DisplayName）、自动建 N 个分身、提取图标。
    /// 分身数 / 隔离模式 / 设备分身默认读 Settings.Current（T07 验收）；调用方传入则覆盖。
    /// </summary>
    public (AppEntry App, List<CloneEntry> Clones) AddAppFromDiscovery(
        DiscoveredApp d, int cloneCount = -1, bool? deepIsolation = null, bool? deviceClone = null)
    {
        var app = new AppEntry
        {
            Name = string.IsNullOrWhiteSpace(d.DisplayName)
                ? (string.IsNullOrWhiteSpace(d.Name) ? Path.GetFileNameWithoutExtension(d.ExePath) : d.Name)
                : d.DisplayName,
            ExePath = d.ExePath,
            Source = d.Source,
            Publisher = d.Publisher,
            Version = d.Version,
            Category = d.Category,
            Recommended = d.Recommended,
        };
        Model.Apps.Add(app);
        Model.Clones[app.Id] = new List<CloneEntry>();

        int n = cloneCount < 1 ? Settings.Current.DefaultCloneCount : Math.Clamp(cloneCount, 1, 9);
        bool deep = deepIsolation ?? (Settings.Current.DefaultIsolationMode == IsolationMode.Deep);
        bool dev = deviceClone ?? Settings.Current.DefaultDeviceClone;

        var clones = new List<CloneEntry>();
        for (int i = 0; i < n; i++)
        {
            var c = new CloneEntry { Name = $"分身 {i + 1}", DeepIsolation = deep, DeviceClone = dev };
            SourceWatch.Stamp(app, c);   // F2：记录创建时刻的源应用基线
            Model.Clones[app.Id].Add(c);
            clones.Add(c);
        }

        Save();
        AppIcons.ExtractWithRetry(app.Id, d.ExePath);
        return (app, clones);
    }

    /// <summary>批量从发现结果入库，自动跳过已添加的应用。返回新增数量与明细。</summary>
    public int AddAppsFromDiscovery(IEnumerable<DiscoveredApp> list, int cloneCount,
        out List<(AppEntry App, List<CloneEntry> Clones)> created)
    {
        created = new List<(AppEntry, List<CloneEntry>)>();
        var existing = AddedExeKeys();
        int added = 0;
        foreach (var d in list)
        {
            if (d is not { Supported: true } || string.IsNullOrWhiteSpace(d.ExePath)) continue;
            var key = AppClassifier.NormalizeExePath(d.ExePath);
            if (string.IsNullOrEmpty(key) || existing.Contains(key)) continue;
            existing.Add(key);
            var (app, clones) = AddAppFromDiscovery(d, cloneCount);
            created.Add((app, clones));
            added++;
        }
        return added;
    }

    public void RemoveApp(string appId)
    {
        var app = FindApp(appId);
        int cloneCount = app == null ? 0 : ClonesOf(appId).Count;
        Model.Apps.RemoveAll(a => a.Id == appId);
        Model.Clones.Remove(appId);
        Save();
        if (app != null)
        {
            // F5：删除应用是不可逆的破坏性操作，必须留痕（谁、何时、删掉了什么）
            Log.Rollback("store", $"移除应用「{app.Name}」及其 {cloneCount} 个分身数据（{app.ExePath}）", appId);
            try { Directory.Delete(Paths.AppProfileDir(appId), true); } catch { }
            try { File.Delete(Path.Combine(Paths.IconsDir, appId + ".png")); } catch { }
        }
    }

    public CloneEntry AddClone(string appId)
    {
        var list = Model.Clones.TryGetValue(appId, out var l) ? l : Model.Clones[appId] = new List<CloneEntry>();
        // 取现存最大序号 +1（删除中间分身后用 Count+1 会重名）
        int max = 0;
        foreach (var c in list)
        {
            var m = Regex.Match(c.Name, @"^分身 (\d+)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > max) max = n;
        }
        string name = $"分身 {max + 1}";
        while (list.Any(c => c.Name == name)) { max++; name = $"分身 {max + 1}"; }
        var clone = new CloneEntry { Name = name };
        SourceWatch.Stamp(FindApp(appId), clone);   // F2：记录创建时刻的源应用基线
        list.Add(clone);
        Save();
        Log.Info("store", $"新建分身「{clone.Name}」", appId, clone.Id, "create");
        return clone;
    }

    /// <summary>批量新建分身：循环复用 AddClone 的默认创建逻辑，返回新建列表。</summary>
    public List<CloneEntry> BatchAddClones(string appId, int count)
    {
        var created = new List<CloneEntry>();
        int n = Math.Clamp(count, 1, 99);
        for (int i = 0; i < n; i++)
            created.Add(AddClone(appId));
        return created;
    }

    /// <summary>结束全部运行中分身（遍历 Launcher.Stop），返回成功结束数量。</summary>
    public int EndAllRunning()
    {
        int ended = 0;
        foreach (var app in Model.Apps)
        {
            foreach (var clone in ClonesOf(app.Id))
            {
                if (!Launcher.IsRunning(app.Id, clone.Id)) continue;
                if (Launcher.Stop(app.Id, clone.Id)) ended++;
            }
        }
        return ended;
    }

    public void RemoveClone(string appId, string cloneId)
    {
        var clone = FindClone(appId, cloneId);
        if (Model.Clones.TryGetValue(appId, out var list))
            list.RemoveAll(c => c.Id == cloneId);
        Save();
        // F5：回滚留痕 —— 记下「删了谁的分身、连带清掉了哪些数据与注册表键」
        // 注：注册表清理走 RegClone（隔离核心，冻结不改），此处只描述其结果。
        Log.Rollback("store",
            $"删除分身「{clone?.Name ?? cloneId}」：数据目录 {Paths.CloneDir(appId, cloneId)}" +
            "、注册表私有键（若为深度隔离）均已清理",
            appId, cloneId);
        try { Directory.Delete(Paths.CloneDir(appId, cloneId), true); } catch { }
        try { RegClone.DeleteCloneRoot(appId, cloneId); } catch { }
    }
}
