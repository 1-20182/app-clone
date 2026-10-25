namespace AppClone.Core;

/// <summary>运行状态筛选。</summary>
public enum RunStateFilter { All, Running, Stopped }

/// <summary>管理中心表格的一行（F4）：把应用、分身、运行统计、兼容性结论联结成一条视图记录。</summary>
public sealed class OverviewRow
{
    public AppEntry App { get; init; } = null!;
    public CloneEntry Clone { get; init; } = null!;
    /// <summary>在该应用内的序号（从 1 开始）—— 与卡片头像上的角标数字一致。</summary>
    public int Index { get; init; }
    public CompatLevel Compat { get; init; }
    public string CompatNote { get; init; } = "";
    public StatsEntry Stats { get; init; } = new();
    public bool Running { get; init; }
    public string Key => App.Id + "|" + Clone.Id;
}

/// <summary>
/// 克隆总览的**数据装配**（F4）。刻意从窗体里剥离出来：
/// 视图只负责把行画到 ListView 上，而「哪些行该出现、每列显示什么」这类可判定逻辑放在这里，
/// 才能被回归探针直接断言 —— 否则整张表格的正确性只能靠人眼在 GUI 里数。
/// </summary>
public static class CloneOverview
{
    /// <summary>汇总全部应用的全部分身。运行状态在调用时刻采样一次（轮询刷新由 UI 负责）。</summary>
    public static List<OverviewRow> Build(Store store)
    {
        var rows = new List<OverviewRow>();
        if (store == null) return rows;

        foreach (var app in store.Model.Apps)
        {
            var clones = store.ClonesOf(app.Id);
            var compat = CompatibilityDb.Resolve(app, out var note);
            for (int i = 0; i < clones.Count; i++)
            {
                var clone = clones[i];
                rows.Add(new OverviewRow
                {
                    App = app,
                    Clone = clone,
                    Index = i + 1,
                    Compat = compat,
                    CompatNote = note,
                    Stats = CloneStats.Get(app.Id, clone.Id),
                    Running = Launcher.IsRunning(app.Id, clone.Id),
                });
            }
        }
        return rows;
    }

    /// <summary>筛选。空条件一律视为「不限制」，避免调用方到处写 null 判断。</summary>
    public static List<OverviewRow> Filter(IEnumerable<OverviewRow> rows, string? appName = null,
        RunStateFilter state = RunStateFilter.All, CompatLevel? compat = null, string? query = null)
    {
        var list = new List<OverviewRow>();
        if (rows == null) return list;
        string q = (query ?? "").Trim();

        foreach (var r in rows)
        {
            if (!string.IsNullOrEmpty(appName) && !string.Equals(r.App.Name, appName, StringComparison.Ordinal))
                continue;
            if (state == RunStateFilter.Running && !r.Running) continue;
            if (state == RunStateFilter.Stopped && r.Running) continue;
            if (compat.HasValue && r.Compat != compat.Value) continue;

            if (q.Length > 0)
            {
                string exe = "";
                try { exe = Path.GetFileName(r.App.ExePath); } catch { }
                bool hit = r.App.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.Clone.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || exe.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || r.App.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;
            }
            list.Add(r);
        }
        return list;
    }

    /// <summary>列文案统一出口：表格、导出、状态栏共用一套，避免同一个事实出现两种写法。</summary>
    public static string IsolationText(CloneEntry clone) =>
        clone.DeepIsolation ? "深度隔离" : "标准隔离";

    public static string DeviceText(CloneEntry clone) =>
        clone.DeviceClone ? "开" : "关";

    public static string RunningText(bool running) => running ? "运行中" : "未运行";

    /// <summary>累计时长：0 显示为占位符，避免表格里出现一片「0 秒」的视觉噪音。</summary>
    public static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} 分 {t.Seconds} 秒";
        return $"{t.Seconds} 秒";
    }

    public static string LastLaunchText(DateTime at) =>
        at == default ? "—" : at.ToString("yyyy-MM-dd HH:mm");
}
