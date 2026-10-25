using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppClone.Core;

/// <summary>
/// 兼容性等级（F1）。语义：
/// Unknown=尚未实测；Supported=已实测可用；Partial=可启动但有已知限制；Unsupported=不支持分身。
/// </summary>
public enum CompatLevel { Unknown = 0, Supported = 1, Partial = 2, Unsupported = 3 }

/// <summary>单个应用的兼容性记录（按 exe 文件名归档，落盘 data\compat.json）。</summary>
public class CompatEntry
{
    public string Match { get; set; } = "";                                  // exe 文件名（小写，含扩展名）
    public CompatLevel Level { get; set; } = CompatLevel.Unknown;
    public string Note { get; set; } = "";
    public string Source { get; set; } = "learned";                          // learned | user
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public int ConsecutiveFails { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 兼容性数据库（F1）：回答「这个应用能不能分身」而不是等用户撞墙。
///
/// 三级信息来源，优先级从低到高：
///   1. 内置推断（Seed，不落盘）—— UWP 判不支持；浏览器内核（LaunchRules 命中）判可用；其余为未实测；
///   2. 自学习（learned）—— 每次启动后的观察窗结果自动写回；
///   3. 用户手动标记（user）—— 最高优先级，自学习不再覆盖。
///
/// 与 apps.json / settings.json 分离，损坏不影响主数据；线程安全（单锁）。
/// </summary>
public static class CompatibilityDb
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, CompatEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>连续失败达到该次数即判定「不支持」。</summary>
    private const int UnsupportedAfterFails = 3;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>归一化键：exe 文件名（小写含扩展名）。同一应用的不同版本共享同一条记录。</summary>
    public static string Key(string? exePath)
    {
        try { return string.IsNullOrWhiteSpace(exePath) ? "" : Path.GetFileName(exePath).ToLowerInvariant(); }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ 查询

    /// <summary>解析某应用的兼容性等级与说明（永不返回 null 说明，便于直接进 ToolTip / 状态栏）。</summary>
    public static CompatLevel Resolve(AppEntry? app, out string note)
    {
        note = "";
        if (app == null) { note = "未选择应用"; return CompatLevel.Unknown; }

        string key = Key(app.ExePath);
        if (key.Length > 0)
        {
            lock (_lock)
            {
                EnsureLoaded();
                if (_entries.TryGetValue(key, out var e))
                {
                    note = e.Note.Length > 0 ? e.Note : Label(e.Level);
                    return e.Level;
                }
            }
        }
        return Infer(app, out note);
    }

    /// <summary>内置推断：不落盘，仅用于首次展示（实测后由自学习结果取代）。</summary>
    private static CompatLevel Infer(AppEntry app, out string note)
    {
        note = "";

        if (string.IsNullOrWhiteSpace(app.ExePath) || app.Category == AppClassifier.CategoryConstants.Uwp)
        {
            note = "应用商店（UWP）应用：受系统沙盒约束，无法分身";
            return CompatLevel.Unsupported;
        }

        string exe = Key(app.ExePath);
        bool ruleHit = exe.Length > 0 && LaunchRules.BuiltIn.Any(r =>
            r.Match.Any(m => m.Equals(exe, StringComparison.OrdinalIgnoreCase)));
        if (ruleHit)
        {
            note = "浏览器内核：已接入官方多实例通道（--user-data-dir / -profile）";
            return CompatLevel.Supported;
        }

        note = "尚未实测。启动一次后会自动记录结果";
        return CompatLevel.Unknown;
    }

    /// <summary>全部已落盘记录（含自学习与用户标记），供管理中心 / README 导出使用。</summary>
    public static List<CompatEntry> All()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _entries.Values
                .OrderBy(e => e.Match, StringComparer.OrdinalIgnoreCase)
                .Select(e => new CompatEntry
                {
                    Match = e.Match, Level = e.Level, Note = e.Note, Source = e.Source,
                    SuccessCount = e.SuccessCount, FailCount = e.FailCount,
                    ConsecutiveFails = e.ConsecutiveFails, UpdatedAt = e.UpdatedAt,
                })
                .ToList();
        }
    }

    /// <summary>UI 展示文案（四态），统一出口避免各处各写一套。</summary>
    public static string Label(CompatLevel level) => level switch
    {
        CompatLevel.Supported => "✓ 可用",
        CompatLevel.Partial => "⚠ 部分",
        CompatLevel.Unsupported => "✗ 不支持",
        _ => "— 未测试",
    };

    // ------------------------------------------------------------------ 写入

    /// <summary>
    /// 记录一次启动观察结果（F1 自学习）。
    /// 成功 → Supported；失败 → Partial；连续失败达阈值 → Unsupported。
    /// 用户手动标记的记录（Source=user）不会被覆盖。
    /// </summary>
    public static void Report(string? exePath, bool ok, string? reason = null)
    {
        string key = Key(exePath);
        if (key.Length == 0) return;

        lock (_lock)
        {
            EnsureLoaded();
            if (_entries.TryGetValue(key, out var exist) && exist.Source == "user") return;

            if (!_entries.TryGetValue(key, out var e))
            {
                e = new CompatEntry { Match = key };
                _entries[key] = e;
            }

            e.Source = "learned";
            e.UpdatedAt = DateTime.Now;

            if (ok)
            {
                e.SuccessCount++;
                e.ConsecutiveFails = 0;
                e.Level = CompatLevel.Supported;
                e.Note = $"已实测启动成功（累计 {e.SuccessCount} 次）";
            }
            else
            {
                e.FailCount++;
                e.ConsecutiveFails++;
                if (e.ConsecutiveFails >= UnsupportedAfterFails)
                {
                    e.Level = CompatLevel.Unsupported;
                    e.Note = $"连续 {e.ConsecutiveFails} 次启动失败（{reason ?? "原因未知"}）";
                }
                else
                {
                    e.Level = CompatLevel.Partial;
                    e.Note = reason ?? "启动异常，可再试一次";
                }
            }
            SaveLocked();
        }
    }

    /// <summary>用户手动标记（最高优先级，自学习不再覆盖）。</summary>
    public static void SetManual(string? exePath, CompatLevel level, string? note = null)
    {
        string key = Key(exePath);
        if (key.Length == 0) return;

        lock (_lock)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue(key, out var e))
            {
                e = new CompatEntry { Match = key };
                _entries[key] = e;
            }
            e.Level = level;
            e.Source = "user";
            e.Note = string.IsNullOrWhiteSpace(note) ? $"用户标记：{Label(level)}" : note!;
            e.UpdatedAt = DateTime.Now;
            SaveLocked();
        }
    }

    /// <summary>清除手动/自学习记录，回到内置推断。</summary>
    public static void Reset(string? exePath)
    {
        string key = Key(exePath);
        if (key.Length == 0) return;
        lock (_lock)
        {
            EnsureLoaded();
            if (_entries.Remove(key)) SaveLocked();
        }
    }

    // ------------------------------------------------------------------ 持久化

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(Paths.CompatFile)) return;
            var list = JsonSerializer.Deserialize<List<CompatEntry>>(File.ReadAllText(Paths.CompatFile), JsonOpts);
            if (list == null) return;
            foreach (var e in list)
                if (!string.IsNullOrWhiteSpace(e.Match))
                    _entries[e.Match.ToLowerInvariant()] = e;
        }
        catch
        {
            // 损坏 → 以空库启动（下次写入即覆盖），不影响主数据
            try { File.Copy(Paths.CompatFile, Paths.CompatFile + ".corrupt", true); } catch { }
        }
    }

    private static void SaveLocked()
    {
        try
        {
            Paths.EnsureAll();
            var json = JsonSerializer.Serialize(_entries.Values.ToList(), JsonOpts);
            var tmp = Paths.CompatFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Paths.CompatFile, overwrite: true);
        }
        catch { }
    }

    /// <summary>外部（退出前）落盘入口。</summary>
    public static void Save()
    {
        lock (_lock) { if (_loaded) SaveLocked(); }
    }
}
