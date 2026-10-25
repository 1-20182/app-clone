using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AppClone.Core;

/// <summary>日志级别（F5）。数值越大越严重，便于按「最低级别」过滤。</summary>
public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// 分级日志（F5）：把「Shell 输出镜像」升级为可检索、可轮转、可打包送修的结构化流水。
///
/// 落盘布局（全部在 data\logs\ 下）：
///   ops-YYYYMMDD.jsonl   每行一个 JSON 对象（ts / level / area / appId / cloneId / step / message / error）
///   crash-&lt;时间戳&gt;.json   结构化崩溃报告（版本 / 运行环境 / 最后一条操作 / 堆栈 / 内部异常）
///
/// 设计取舍：
///   · 只保留 7 天，超期自动清理 —— 日志是诊断耗材，不该无限增长占用户磁盘；
///   · 全程异常安全，任何一步失败都不得影响主流程（日志写不进去不能阻断启动分身）；
///   · 不引入任何依赖，导出诊断包直接用 BCL 的 ZipArchive。
/// </summary>
public static class Log
{
    /// <summary>日志保留天数，超期自动清理。</summary>
    public const int KeepDays = 7;

    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string _lastOp = "";
    private static string _cleanupDay = "";

    /// <summary>最后一条已记录的操作（崩溃报告要带上下文，否则只能看到一个孤零零的堆栈）。</summary>
    public static string LastOp { get { lock (_lock) return _lastOp; } }

    /// <summary>当日流水文件路径。</summary>
    public static string OpsFile => Path.Combine(Paths.LogsDir, $"ops-{DateTime.Now:yyyyMMdd}.jsonl");

    // ------------------------------------------------------------------ 写入

    /// <summary>
    /// 记录一条日志。<paramref name="level"/> 低于设置中的最低级别时不落盘（但仍会更新内存中的「最后操作」）。
    /// </summary>
    public static void Write(LogLevel level, string area, string message,
        string? appId = null, string? cloneId = null, string? step = null, Exception? ex = null)
    {
        string line;
        try { line = Build(level, area, message, appId, cloneId, step, ex); }
        catch { return; }

        lock (_lock)
        {
            _lastOp = line;
            try
            {
                if (!Settings.Current.WriteOpsLog) return;
                if ((int)level < (int)Settings.Current.MinLogLevel) return;
                Paths.EnsureAll();
                CleanupIfNeededLocked();
                AppendLineLocked(line);
            }
            catch { }
        }
    }

    /// <summary>
    /// 追加一行流水。用 FileShare.ReadWrite 打开并带重试：
    /// GUI 进程与 --launch 无头进程可能同时在写同一份日志，独占打开会让其中一边静默丢日志。
    /// </summary>
    private static void AppendLineLocked(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(OpsFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes, 0, bytes.Length);
                return;
            }
            catch
            {
                try { Thread.Sleep(25); } catch { }
            }
        }
    }

    public static void Debug(string area, string message, string? appId = null, string? cloneId = null, string? step = null)
        => Write(LogLevel.Debug, area, message, appId, cloneId, step);

    public static void Info(string area, string message, string? appId = null, string? cloneId = null, string? step = null)
        => Write(LogLevel.Info, area, message, appId, cloneId, step);

    public static void Warn(string area, string message, string? appId = null, string? cloneId = null, string? step = null, Exception? ex = null)
        => Write(LogLevel.Warn, area, message, appId, cloneId, step, ex);

    public static void Error(string area, string message, string? appId = null, string? cloneId = null, string? step = null, Exception? ex = null)
        => Write(LogLevel.Error, area, message, appId, cloneId, step, ex);

    /// <summary>
    /// 回滚 / 破坏性操作的留痕（F5）。凡是删掉了用户数据、或把数据回退到某个快照的地方，
    /// 都要在这里留一条 —— 事后能回答「谁在什么时候动了什么」。
    /// </summary>
    public static void Rollback(string area, string message, string? appId = null, string? cloneId = null)
        => Write(LogLevel.Info, area, message, appId, cloneId, "rollback");

    private static string Build(LogLevel level, string area, string message,
        string? appId, string? cloneId, string? step, Exception? ex)
    {
        // 手写而不是直接序列化匿名对象：字段顺序稳定、缺省字段可省略，日志行更短更易读
        var sb = new StringBuilder(160);
        sb.Append("{\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('"');
        sb.Append(",\"level\":\"").Append(level).Append('"');
        sb.Append(",\"area\":").Append(Str(area));
        if (!string.IsNullOrEmpty(step)) sb.Append(",\"step\":").Append(Str(step));
        if (!string.IsNullOrEmpty(appId)) sb.Append(",\"appId\":").Append(Str(appId));
        if (!string.IsNullOrEmpty(cloneId)) sb.Append(",\"cloneId\":").Append(Str(cloneId));
        sb.Append(",\"message\":").Append(Str(message));
        if (ex != null) sb.Append(",\"error\":").Append(Str(ex.GetType().Name + ": " + ex.Message));
        sb.Append('}');
        return sb.ToString();
    }

    private static string Str(string s) => JsonSerializer.Serialize(s ?? "", JsonOpts);

    /// <summary>超期日志清理：每天最多执行一次（避免每次写日志都扫目录）。</summary>
    private static void CleanupIfNeededLocked()
    {
        string today = DateTime.Now.ToString("yyyyMMdd");
        if (_cleanupDay == today) return;
        _cleanupDay = today;
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-KeepDays);
            foreach (var f in Directory.GetFiles(Paths.LogsDir, "ops-*.jsonl"))
            {
                try
                {
                    var name = Path.GetFileNameWithoutExtension(f);   // ops-20260921
                    if (name.Length != 12) continue;
                    if (!DateTime.TryParseExact(name[4..], "yyyyMMdd", null,
                            System.Globalization.DateTimeStyles.None, out var d)) continue;
                    if (d < cutoff) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    // ------------------------------------------------------------------ 读取

    /// <summary>读取当日流水的最后 N 行（管理中心「查看日志」用）。</summary>
    public static List<string> Tail(int lines)
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(OpsFile)) return result;
            var all = File.ReadAllLines(OpsFile);
            int start = Math.Max(0, all.Length - Math.Max(1, lines));
            for (int i = start; i < all.Length; i++) result.Add(all[i]);
        }
        catch { }
        return result;
    }

    /// <summary>列出日志目录下的全部文件（诊断包 / 日志查看用）。</summary>
    public static List<string> ListFiles()
    {
        try
        {
            if (!Directory.Exists(Paths.LogsDir)) return new List<string>();
            return Directory.GetFiles(Paths.LogsDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<string>(); }
    }

    // ------------------------------------------------------------------ 崩溃报告

    /// <summary>
    /// 写一份结构化崩溃报告（F5）：光有堆栈不够，得知道「哪个版本、在哪个分身上、最后干了什么」。
    /// 同时向兼容用的 crash.log 追加一行摘要。返回报告文件路径（失败返回 null）。
    /// </summary>
    public static string? WriteCrashReport(string kind, Exception? ex, string? detail = null,
        string? appId = null, string? cloneId = null)
    {
        string? path = null;
        try
        {
            Paths.EnsureAll();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            path = Path.Combine(Paths.LogsDir, $"crash-{stamp}.json");
            var payload = new
            {
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                kind,
                appVersion = AppVersion,
                osVersion = Environment.OSVersion.VersionString,
                runtime = Environment.Version.ToString(),
                is64BitProcess = Environment.Is64BitProcess,
                dataRoot = Paths.DataRoot,
                appId,
                cloneId,
                detail,
                lastOp = LastOp,
                message = ex?.Message,
                type = ex?.GetType().FullName,
                stack = ex?.StackTrace,
                inner = ex?.InnerException?.ToString(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), Encoding.UTF8);

            // 兼容旧版：crash.log 仍保留一行摘要（README 已注明迁移关系）
            try
            {
                File.AppendAllText(Paths.LegacyCrashLog,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex?.Message ?? detail}\n");
            }
            catch { }

            Write(LogLevel.Error, "crash", $"{kind}：{ex?.Message ?? detail}", appId, cloneId, "crash", ex);
        }
        catch { }
        return path;
    }

    /// <summary>当前程序版本（取程序集版本，避免与 csproj 手写版本号漂移）。</summary>
    public static string AppVersion
    {
        get
        {
            try
            {
                var asm = Assembly.GetEntryAssembly() ?? typeof(Log).Assembly;
                var v = asm.GetName().Version;
                if (v == null) return "unknown";
                return v.Revision > 0 ? v.ToString() : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "unknown"; }
        }
    }

    // ------------------------------------------------------------------ 诊断包

    /// <summary>
    /// 导出诊断包（F5）：把日志与几个「状态描述型」json 打成一个 zip，方便用户发给你排查。
    /// 刻意**只收配置与日志，不收 profiles / snapshots** —— 分身数据目录里是用户的真实账号数据，
    /// 体积巨大且属于隐私，绝不能进诊断包。
    /// </summary>
    public static (bool Ok, string Message) ExportDiagnostics(string zipPath)
    {
        try
        {
            Paths.EnsureAll();
            var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            // 概要：一眼能看出「用户到底用的是哪版、数据有多少」
            var summary = new StringBuilder();
            summary.AppendLine($"应用分身 诊断包");
            summary.AppendLine($"导出时间   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            summary.AppendLine($"程序版本   : {AppVersion}");
            summary.AppendLine($"运行环境   : {Environment.OSVersion.VersionString} / .NET {Environment.Version}");
            summary.AppendLine($"64 位进程  : {Environment.Is64BitProcess}");
            summary.AppendLine($"数据目录   : {Paths.DataRoot}");
            summary.AppendLine($"日志保留   : {KeepDays} 天");
            try
            {
                var store = Store.Load();
                int clones = store.Model.Apps.Sum(a => store.ClonesOf(a.Id).Count);
                summary.AppendLine($"应用 / 分身: {store.Model.Apps.Count} / {clones}");
            }
            catch { }
            try
            {
                var entries = CompatibilityDb.All();
                summary.AppendLine($"兼容性记录 : {entries.Count} 条" +
                    (entries.Count > 0 ? $"（{string.Join("、", entries.Take(8).Select(e => e.Match + "=" + e.Level))}）" : ""));
            }
            catch { }
            summary.AppendLine();
            summary.AppendLine("包含内容：data\\logs\\ 全部日志、apps.json（应用与分身配置）、settings.json、");
            summary.AppendLine("          compat.json、stats.json、launch_rules.json（若存在）。");
            summary.AppendLine("不包含  ：profiles\\ 与 snapshots\\ —— 那里是用户的真实账号数据，不参与诊断。");

            AddText(zip, "summary.txt", summary.ToString());

            foreach (var f in ListFiles())
                TryAddFile(zip, f, "logs/" + Path.GetFileName(f));

            foreach (var name in new[] { "apps.json", "settings.json", "compat.json", "stats.json", "launch_rules.json" })
            {
                var p = Path.Combine(Paths.DataRoot, name);
                if (File.Exists(p)) TryAddFile(zip, p, name);
            }

            long size = 0;
            try { size = new FileInfo(zipPath).Length; } catch { }
            Write(LogLevel.Info, "diagnostics", $"已导出诊断包：{zipPath}（{size / 1024} KB）");
            return (true, $"已导出诊断包：\n{zipPath}\n\n大小 {size / 1024} KB。可直接发给开发者排查问题。");
        }
        catch (Exception ex)
        {
            Write(LogLevel.Error, "diagnostics", "导出诊断包失败", ex: ex);
            return (false, $"导出失败：{ex.Message}");
        }
    }

    private static void TryAddFile(ZipArchive zip, string path, string entryName)
    {
        try { zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal); } catch { }
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        try
        {
            var e = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = e.Open();
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            w.Write(content);
        }
        catch { }
    }
}
