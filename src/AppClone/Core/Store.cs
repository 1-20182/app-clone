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

    public void RemoveClone(string appId, string cloneId)
    {
        if (Model.Clones.TryGetValue(appId, out var list))
            list.RemoveAll(c => c.Id == cloneId);
        Save();
        try { Directory.Delete(Paths.CloneDir(appId, cloneId), true); } catch { }
        RegClone.DeleteCloneRoot(appId, cloneId);
    }
}
