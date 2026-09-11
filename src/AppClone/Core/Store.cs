using System.Text.Json;
using System.Text.Json.Serialization;

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
        try
        {
            if (File.Exists(Paths.StoreFile))
            {
                var json = File.ReadAllText(Paths.StoreFile);
                s.Model = JsonSerializer.Deserialize<StoreModel>(json, JsonOpts) ?? new StoreModel();
            }
        }
        catch
        {
            // 配置损坏时以空仓库启动，避免崩溃；旧文件保留为备份
            try { File.Copy(Paths.StoreFile, Paths.StoreFile + ".bak", true); } catch { }
            s.Model = new StoreModel();
        }
        return s;
    }

    public void Save()
    {
        lock (_sync)
        {
            Paths.EnsureAll();
            var json = JsonSerializer.Serialize(Model, JsonOpts);
            File.WriteAllText(Paths.StoreFile, json);
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
        Model.Apps.RemoveAll(a => a.Id == appId);
        Model.Clones.Remove(appId);
        Save();
        if (app != null)
        {
            try { Directory.Delete(Paths.AppProfileDir(appId), true); } catch { }
            try { File.Delete(Path.Combine(Paths.IconsDir, appId + ".png")); } catch { }
        }
    }

    public CloneEntry AddClone(string appId)
    {
        var list = Model.Clones.TryGetValue(appId, out var l) ? l : Model.Clones[appId] = new List<CloneEntry>();
        var clone = new CloneEntry { Name = $"分身 {list.Count + 1}" };
        list.Add(clone);
        Save();
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
        if (Model.Clones.TryGetValue(appId, out var list))
            list.RemoveAll(c => c.Id == cloneId);
        Save();
        try { Directory.Delete(Paths.CloneDir(appId, cloneId), true); } catch { }
        RegClone.DeleteCloneRoot(appId, cloneId);
    }
}
