using System.Drawing;
using System.Collections.Generic;

namespace AppClone.Core;

/// <summary>外观主题（深 / 浅）。与 Settings.AppTheme 互转由 Ui.Theme 桥接。</summary>
public enum Theme { Light, Dark }

/// <summary>主题颜色角色：所有取色统一走 ThemeRole，禁止新增代码硬编码浅色常量。</summary>
public enum ThemeRole
{
    Bg, Card, Accent, AccentDark, AccentLight, ChipPurple, ChipPurpleText,
    TextPrimary, TextSecondary, Border, Ok, Danger, FillGray, FillGrayHover,
    // ↓ 增量 1.3.0：兼容性徽章（✓ 可用 / ⚠ 部分 / ✗ 不支持）与源应用升级提示条配色。
    //   追加在枚举末尾——既有成员顺序不变，避免任何按序号取值的老逻辑受影响。
    OkLight, Warn, WarnLight, DangerLight
}

/// <summary>主题色板：Light / Dark 两套，键为 ThemeRole。色值与 Ui.cs 既有浅/深色板保持一致。</summary>
internal static class Palette
{
    private static readonly Dictionary<ThemeRole, Color> Light = new()
    {
        { ThemeRole.Bg, ColorTranslator.FromHtml("#F5F5F7") },
        { ThemeRole.Card, Color.White },
        { ThemeRole.Accent, ColorTranslator.FromHtml("#0A84FF") },
        { ThemeRole.AccentDark, ColorTranslator.FromHtml("#0071E3") },
        { ThemeRole.AccentLight, ColorTranslator.FromHtml("#E8F1FF") },
        { ThemeRole.ChipPurple, ColorTranslator.FromHtml("#F3E8FF") },
        { ThemeRole.ChipPurpleText, ColorTranslator.FromHtml("#8E44EC") },
        { ThemeRole.TextPrimary, ColorTranslator.FromHtml("#1D1D1F") },
        { ThemeRole.TextSecondary, ColorTranslator.FromHtml("#86868B") },
        { ThemeRole.Border, ColorTranslator.FromHtml("#E8E8ED") },
        { ThemeRole.Ok, ColorTranslator.FromHtml("#34C759") },
        { ThemeRole.Danger, ColorTranslator.FromHtml("#FF3B30") },
        { ThemeRole.FillGray, ColorTranslator.FromHtml("#E9E9EB") },
        { ThemeRole.FillGrayHover, ColorTranslator.FromHtml("#DCDCE1") },
        { ThemeRole.OkLight, ColorTranslator.FromHtml("#E7F8EC") },
        { ThemeRole.Warn, ColorTranslator.FromHtml("#FF9500") },
        { ThemeRole.WarnLight, ColorTranslator.FromHtml("#FFF4E6") },
        { ThemeRole.DangerLight, ColorTranslator.FromHtml("#FFECEB") },
    };

    private static readonly Dictionary<ThemeRole, Color> Dark = new()
    {
        { ThemeRole.Bg, ColorTranslator.FromHtml("#1C1C1E") },
        { ThemeRole.Card, ColorTranslator.FromHtml("#2C2C2E") },
        { ThemeRole.Accent, ColorTranslator.FromHtml("#0A84FF") },
        { ThemeRole.AccentDark, ColorTranslator.FromHtml("#0071E3") },
        { ThemeRole.AccentLight, ColorTranslator.FromHtml("#0A3A66") },
        { ThemeRole.ChipPurple, ColorTranslator.FromHtml("#2A1F3D") },
        { ThemeRole.ChipPurpleText, ColorTranslator.FromHtml("#BF5AF2") },
        { ThemeRole.TextPrimary, ColorTranslator.FromHtml("#F5F5F7") },
        { ThemeRole.TextSecondary, ColorTranslator.FromHtml("#98989D") },
        { ThemeRole.Border, ColorTranslator.FromHtml("#38383A") },
        { ThemeRole.Ok, ColorTranslator.FromHtml("#30D158") },
        { ThemeRole.Danger, ColorTranslator.FromHtml("#FF453A") },
        { ThemeRole.FillGray, ColorTranslator.FromHtml("#3A3A3C") },
        { ThemeRole.FillGrayHover, ColorTranslator.FromHtml("#48484A") },
        { ThemeRole.OkLight, ColorTranslator.FromHtml("#14301B") },
        { ThemeRole.Warn, ColorTranslator.FromHtml("#FF9F0A") },
        { ThemeRole.WarnLight, ColorTranslator.FromHtml("#3A2A12") },
        { ThemeRole.DangerLight, ColorTranslator.FromHtml("#3A1E1D") },
    };

    public static Color Get(Theme t, ThemeRole r)
    {
        var dict = t == Theme.Dark ? Dark : Light;
        return dict.TryGetValue(r, out var c) ? c : dict[ThemeRole.Bg];
    }
}

/// <summary>
/// 主题管理器：持有当前主题 Current 与 ThemeChanged 事件。
/// Ui.Theme / Ui.XxxOf() / Ui.ThemedColor() 均以其为唯一来源，保证全应用配色一致。
/// </summary>
public static class ThemeManager
{
    public static Theme Current { get; private set; } = Theme.Light;
    public static event Action? ThemeChanged;

    /// <summary>启动期初始化（不触发 ThemeChanged，避免控件尚未就绪时重绘）。</summary>
    public static void Init(Theme t)
    {
        Current = t;
    }

    /// <summary>切换主题并广播 ThemeChanged（自绘控件订阅后自动重绘）。</summary>
    public static void Set(Theme t)
    {
        if (Current == t) return;
        Current = t;
        ThemeChanged?.Invoke();
    }

    /// <summary>按当前主题返回某角色颜色。</summary>
    public static Color Color(ThemeRole role) => Palette.Get(Current, role);
}
