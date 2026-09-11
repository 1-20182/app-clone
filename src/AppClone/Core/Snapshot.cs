using System.Text.Json;

namespace AppClone.Core;

/// <summary>
/// 分身快照（R10，Q9：纯目录复制，不压缩）：
/// 把 CloneDir 复制到 SnapshotsDir\&lt;app&gt;\&lt;clone&gt;\&lt;timestamp&gt;，写入标记文件。
/// 占用 &gt;2GB 时提示清理。支持列表 / 回滚 / 删除。
/// </summary>
public static class Snapshot
{
    private const long WarnBytes = 2L * 1024 * 1024 * 1024; // 2GB

    public static string SnapshotRoot(string appId, string cloneId) =>
        Path.Combine(Paths.SnapshotsDir, appId, cloneId);

    public static (bool Ok, string Message, string? Path) Create(string appId, string cloneId)
    {
        var src = Paths.CloneDir(appId, cloneId);
        if (!Directory.Exists(src)) return (false, "分身目录不存在", null);

        long size = DirSize(src);
        if (size > WarnBytes)
            return (false, $"分身数据约 {size / 1024 / 1024 / 1024}GB，已超过 2GB，请先清理或手动备份后再快照", null);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var dest = Path.Combine(SnapshotRoot(appId, cloneId), stamp);
        try
        {
            Directory.CreateDirectory(dest);
            DirectoryCopy(src, dest);
            File.WriteAllText(Path.Combine(dest, ".snapshot"),
                JsonSerializer.Serialize(new SnapshotMeta { AppId = appId, CloneId = cloneId, CreatedAt = DateTime.Now },
                    new JsonSerializerOptions { WriteIndented = true }));
            return (true, $"已快照：{stamp}", dest);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public static List<string> ListStamps(string appId, string cloneId)
    {
        var root = SnapshotRoot(appId, cloneId);
        if (!Directory.Exists(root)) return new List<string>();
        var list = new List<string>();
        try
        {
            foreach (var d in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(d, ".snapshot")))
                    list.Add(Path.GetFileName(d) ?? "");
        }
        catch { }
        list.Sort();
        return list;
    }

    public static (bool Ok, string Message) Restore(string appId, string cloneId, string stamp)
    {
        var src = Path.Combine(SnapshotRoot(appId, cloneId), stamp);
        var dest = Paths.CloneDir(appId, cloneId);
        if (!Directory.Exists(src)) return (false, "快照不存在");
        try
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            DirectoryCopy(src, dest);
            return (true, "已恢复到该快照");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static bool Delete(string appId, string cloneId, string stamp)
    {
        try
        {
            var d = Path.Combine(SnapshotRoot(appId, cloneId), stamp);
            if (Directory.Exists(d)) Directory.Delete(d, true);
            return true;
        }
        catch { return false; }
    }

    private static void DirectoryCopy(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true); } catch { }
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            try { DirectoryCopy(dir, Path.Combine(dest, Path.GetFileName(dir))); } catch { }
        }
    }

    private static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            foreach (var sub in Directory.GetDirectories(dir))
                total += DirSize(sub);
        }
        catch { }
        return total;
    }

    private sealed class SnapshotMeta
    {
        public string AppId { get; set; } = "";
        public string CloneId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }
}
