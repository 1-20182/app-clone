using System.Globalization;

namespace AppClone.Core;

/// <summary>
/// P4 分身定时启动的时间判定。
/// 纯函数、无副作用，便于探针直接验证边界 —— 定时逻辑最容易出的错是「跨天重复触发」和「凌晨漏触发」。
/// 只支持每天固定时刻 "HH:mm"：定时依赖本软件在运行（含托盘常驻），程序没开就不会发生。
/// </summary>
public static class AutoLaunchPlan
{
    /// <summary>解析 "HH:mm"；格式非法或越界返回 null。</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // 只接受两位小时 + 两位分钟，不接受 "8:5" 之类的模糊写法 —— 用户输入错误要能明确报错
        if (text.Trim().Length != 5) return null;
        if (!TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out var t))
            return null;
        return t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;
    }

    /// <summary>格式化当前设置值，供菜单显示。</summary>
    public static string Describe(string? schedule) => ParseTime(schedule) is { } t ? t.ToString(@"hh\:mm") : "";

    /// <summary>
    /// 现在是否该触发：已过当日目标时刻、今天还没触发过，且没有错过太久（6 小时内补启）。
    /// 用「已过点」判断而不是「恰好等于这一分钟」，避免主程序晚启动 / 计时器跳秒时整天漏触发；
    /// 「今天只触发一次」由调用方传入的上次触发时间保证。
    /// </summary>
    public static bool ShouldFire(string? schedule, DateTime now, DateTime? lastFired)
    {
        if (ParseTime(schedule) is not { } t) return false;
        var target = now.Date + t;
        if (now < target) return false;
        if (lastFired is { } last && last.Date == now.Date) return false;
        return now - target <= TimeSpan.FromHours(6);
    }
}
