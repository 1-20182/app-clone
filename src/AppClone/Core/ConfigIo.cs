using System.IO;
using System.IO.Compression;
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
    /// 兼容两种 JSON 形态：单包 {app,clones}（ExportClone）与数组 [{app,clones}…]（ExportAll）。
    /// 返回 (新建应用数, 新建分身数, 错误信息)；成功时错误信息为空。
    /// </summary>
    public static (int Apps, int Clones, string Error) Import(string path, Store store)
    {
        try
        {
            var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            var packs = root.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<CloneConfig>>(root.GetRawText(), JsonOpts) ?? new()
                : new List<CloneConfig> { JsonSerializer.Deserialize<CloneConfig>(root.GetRawText(), JsonOpts)! };
            return ImportEntries(packs, store, null);
        }
        catch (Exception ex)
        {
            return (0, 0, ex.Message);
        }
    }

    /// <summary>
    /// 整包导出（P1 数据迁移）：manifest.json + profiles\ 下的真实分身数据目录，可跨机搬走。
    /// appIds 为空 = 导出全部应用。
    /// </summary>
    public static (int Apps, int Clones, long Bytes) ExportPackage(string zipPath, Store store, IEnumerable<string>? appIds = null)
    {
        Paths.EnsureAll();
        var wanted = appIds?.ToHashSet(StringComparer.Ordinal);
        var packs = new List<CloneConfig>();
        foreach (var app in store.Model.Apps)
            if (wanted == null || wanted.Contains(app.Id))
                packs.Add(new CloneConfig(app, store.ClonesOf(app.Id)));

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var s = manifest.Open())
            JsonSerializer.Serialize(s, packs, JsonOpts);

        int clones = 0;
        long bytes = 0;
        foreach (var cfg in packs)
            foreach (var c in cfg.Clones)
            {
                clones++;
                var dir = Paths.CloneDir(cfg.App.Id, c.Id);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
                    var entry = zip.CreateEntry($"profiles/{cfg.App.Id}/{c.Id}/{rel}", CompressionLevel.Optimal);
                    using var input = File.OpenRead(f);
                    using var output = entry.Open();
                    input.CopyTo(output);
                    bytes += input.Length;
                }
            }
        return (packs.Count, clones, bytes);
    }

    /// <summary>导入整包：manifest 里的每个应用都新建 ID，profiles\ 数据解压到对应新分身目录。</summary>
    public static (int Apps, int Clones, string Error) ImportPackage(string zipPath, Store store)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifest = zip.GetEntry("manifest.json")
                ?? throw new InvalidDataException("压缩包里没有 manifest.json，不是有效的分身数据包");
            List<CloneConfig> packs;
            using (var s = manifest.Open())
                packs = JsonSerializer.Deserialize<List<CloneConfig>>(s, JsonOpts) ?? new();
            return ImportEntries(packs, store, zip);
        }
        catch (Exception ex)
        {
            return (0, 0, ex.Message);
        }
    }

    /// <summary>新建应用与分身；zip 非空时从包内解压数据目录，否则从本机原目录复制。</summary>
    private static (int Apps, int Clones, string Error) ImportEntries(List<CloneConfig> packs, Store store, ZipArchive? zip)
    {
        int apps = 0, clones = 0;
        foreach (var cfg in packs)
        {
            if (cfg?.App == null) continue;

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
            apps++;

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
                    Disguise = c.Disguise,
                    Guard = c.Guard,
                    Frozen = c.Frozen,
                    Proxy = c.Proxy,
                    AutoStart = c.AutoStart,
                    AutoStartAt = c.AutoStartAt,
                };
                store.Model.Clones[newApp.Id].Add(nc);
                clones++;
                if (zip != null) ExtractCloneData(zip, cfg.App.Id, c.Id, newApp.Id, nc.Id);
                else CopyCloneData(cfg.App.Id, c.Id, newApp.Id, nc.Id);
            }
        }
        store.Save();
        return (apps, clones, "");
    }

    private static void ExtractCloneData(ZipArchive zip, string srcApp, string srcClone, string dstApp, string dstClone)
    {
        try
        {
            var prefix = $"profiles/{srcApp}/{srcClone}/";
            var dst = Paths.CloneDir(dstApp, dstClone);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = e.FullName[prefix.Length..];
                // 防目录穿越：包内路径只允许落在新分身目录里
                if (rel.Length == 0 || rel.Contains("..")) continue;
                var target = Path.Combine(dst, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, true);
            }
        }
        catch { }
    }

    /// <summary>复制分身数据目录（P1 导入 / P2 分身克隆共用）。失败静默：克隆配置已完成，数据复制不该阻断操作。</summary>
    public static void CopyCloneData(string srcApp, string srcClone, string dstApp, string dstClone)
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
