using System.Text.Json;

namespace AppClone.Core;

/// <summary>
/// 分身快照（R10，Q9：纯目录复制，不压缩）：
/// 把 CloneDir 复制到 SnapshotsDir&lt;app&gt;&lt;clone&gt;&lt;timestamp&gt;，写入标记文件。
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
            int failedFiles = DirectoryCopy(src, dest);
            File.WriteAllText(Path.Combine(dest, ".snapshot"),
                JsonSerializer.Serialize(new SnapshotMeta { AppId = appId, CloneId = cloneId, CreatedAt = DateTime.Now },
                    new JsonSerializerOptions { WriteIndented = true }));
            // A5③：占用的文件未纳入快照时如实提示（ok 仍为 true）
            string okMsg = $"已快照：{stamp}";
            if (failedFiles > 0) okMsg += $"（{failedFiles} 个文件被占用未纳入快照）";
            Log.Info("snapshot", $"已为分身创建快照 {stamp}（{size / 1024} KB，{failedFiles} 个文件未纳入）", appId, cloneId, "snapshot");
            return (true, okMsg, dest);
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
        // A5①：分身运行中禁止恢复，避免文件被占用导致复制失败、数据损坏
        if (Launcher.IsRunning(appId, cloneId))
            return (false, "分身正在运行，请先结束再恢复");

        var src = Path.Combine(SnapshotRoot(appId, cloneId), stamp);
        var dest = Paths.CloneDir(appId, cloneId);
        if (!Directory.Exists(src)) return (false, "快照不存在");

        // A5②：原子两步恢复——先拷到目标旁临时目录，成功后再换入，避免中途失败造成数据永久丢失
        var tmp = dest + ".restore_tmp";
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            DirectoryCopy(src, tmp);

            if (Directory.Exists(dest))
            {
                try { Directory.Delete(dest, true); }
                catch (Exception ex)
                {
                    // 旧目录删除失败：保留 tmp（快照数据完整），由用户手动处理
                    return (false, $"恢复失败：旧分身目录删除失败（{ex.Message}）。快照已暂存于 {tmp}，可手动移回。");
                }
            }
            Directory.Move(tmp, dest);
            // F5：回滚留痕 —— 恢复快照会用旧数据覆盖当前分身数据，属破坏性操作
            Log.Rollback("snapshot", $"恢复快照 {stamp}：当前分身数据已被该快照覆盖", appId, cloneId);
            return (true, "已恢复到该快照");
        }
        catch (Exception ex)
        {
            // 兜底清理临时目录（Move 成功后 tmp 已不存在，Exists 判断可防误删）
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            Log.Error("snapshot", $"恢复快照 {stamp} 失败：{ex.Message}", appId, cloneId, "rollback", ex);
            return (false, ex.Message);
        }
    }

    public static bool Delete(string appId, string cloneId, string stamp)
    {
        try
        {
            var d = Path.Combine(SnapshotRoot(appId, cloneId), stamp);
            if (Directory.Exists(d)) Directory.Delete(d, true);
            Log.Rollback("snapshot", $"删除快照 {stamp}", appId, cloneId);
            return true;
        }
        catch { return false; }
    }

    /// <summary>递归复制目录；返回复制失败的文件数（A5③：不再静默吞掉全部失败）。</summary>
    private static int DirectoryCopy(string src, string dest)
    {
        int failed = 0;
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
        {
            try { File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true); } catch { failed++; }
        }
        foreach (var dir in Directory.GetDirectories(src))
        {
            try { failed += DirectoryCopy(dir, Path.Combine(dest, Path.GetFileName(dir))); }
            catch { failed++; }   // 整个子目录无法创建时按 1 个失败计
        }
        return failed;
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
