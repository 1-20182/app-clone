using System.Drawing;

namespace AppClone.Core;

/// <summary>
/// P1 快捷方式图标伪装：把分身的桌面快捷方式伪装成系统普通程序（计算器 / 记事本 / 资源管理器），
/// 桌面上一眼看不出来是分身。只改 .lnk 的显示名与图标，启动目标仍是本程序。
/// ponytail: 仅伪装快捷方式，不 hook 分身进程的窗口标题/任务栏图标 —— 那需要注入层改窗口创建，改动与风险不成比例。
/// </summary>
public static class Disguise
{
    public sealed record Preset(string Key, string Name, string Exe);

    public static readonly Preset[] All =
    {
        new("calc", "计算器", Path.Combine(Environment.SystemDirectory, "calc.exe")),
        new("notepad", "记事本", Path.Combine(Environment.SystemDirectory, "notepad.exe")),
        new("explorer", "文件资源管理器", Path.Combine(Environment.SystemDirectory, "explorer.exe")),
    };

    public static Preset? Get(string key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>伪装图标 .ico 缓存路径；生成失败返回 null（调用方回退应用原图标）。</summary>
    public static string? IconPath(string key)
    {
        var p = Get(key);
        if (p == null) return null;
        var path = Path.Combine(Paths.IconsDir, "disguise-" + p.Key + ".ico");
        if (File.Exists(path)) return path;
        return BadgeIcon.Build(path, p.Exe, 0, Color.Empty) ? path : null;
    }
}
