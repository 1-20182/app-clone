using System.IO;
using System.Text.Json;

namespace AppClone.Core;

/// <summary>配置导入 / 导出（仅 JSON，不打包）。</summary>
public static class ConfigIo
{
    /// <summary>单分身导出包：应用 + 其分身列表。</summary>
    public record CloneConfig(AppEntry App, List<CloneEntry> Clones);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>导出单个分身为 {app, clone} JSON（不复制数据目录）。</summary>
    public static void ExportClone(string path, Store store, string appId, string cloneId)
    {
        var app = store.FindApp(appId);
        var clone = store.FindClone(appId, cloneId);
        if (app == null || clone == null) return;
        var cfg = new CloneConfig(app, new List<CloneEntry> { clone });
        Paths.EnsureAll();
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, JsonOpts));
    }

    /// <summary>全量导出：JSON 数组 [{app, clones}...]（不复制数据目录）。</summary>
    public static void ExportAll(string path, Store store)
    {
        var list = new List<CloneConfig>();
        foreach (var app in store.Model.Apps)
            list.Add(new CloneConfig(app, store.ClonesOf(app.Id)));
        Paths.EnsureAll();
        File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOpts));
    }

    /// <summary>
    /// 导入：新建应用 ID + 复制数据目录，不覆盖同名。
    /// 返回 (新建应用, 新建分身列表)；失败返回 (null, 空列表)。
    /// </summary>
    public static (AppEntry? App, List<CloneEntry> Clones) Import(string path, Store store)
    {
        try
        {
            var text = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<CloneConfig>(text, JsonOpts);
            if (cfg?.App == null) return (null, new List<CloneEntry>());

            var newApp = new AppEntry
            {
                Name = cfg.App.Name,
                ExePath = cfg.App.ExePath,
                Args = cfg.App.Args,
                Source = cfg.App.Source,
                Publisher = cfg.App.Publisher,
                Version = cfg.App.Version,
                Category = cfg.App.Category,
                Recommended = cfg.App.Recommended,
            };
            store.Model.Apps.Add(newApp);
            store.Model.Clones[newApp.Id] = new List<CloneEntry>();

            var newClones = new List<CloneEntry>();
            foreach (var c in cfg.Clones ?? new List<CloneEntry>())
            {
                var nc = new CloneEntry
                {
                    Name = c.Name,
                    DeepIsolation = c.DeepIsolation,
                    DeviceClone = c.DeviceClone,
                    CreatedAt = c.CreatedAt,
                    Color = c.Color,
                    Hotkey = c.Hotkey,
                };
                store.Model.Clones[newApp.Id].Add(nc);
                newClones.Add(nc);
                CopyCloneData(cfg.App.Id, c.Id, newApp.Id, nc.Id);
            }
            store.Save();
            return (newApp, newClones);
        }
        catch
        {
            return (null, new List<CloneEntry>());
        }
    }

    private static void CopyCloneData(string srcApp, string srcClone, string dstApp, string dstClone)
    {
        try
        {
            var src = Paths.CloneDir(srcApp, srcClone);
            var dst = Paths.CloneDir(dstApp, dstClone);
            if (!Directory.Exists(src)) return;
            CopyDirectory(src, dst);
        }
        catch { }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}
