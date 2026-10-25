using System.Diagnostics;
using System.IO;

namespace AppClone.Core;

/// <summary>源应用相对分身创建基线的变化种类（F2）。</summary>
public enum SourceChangeKind
{
    /// <summary>无变化。</summary>
    None,
    /// <summary>无法判定（老数据没有基线）。调用方应静默回填基线，不要报警。</summary>
    Unknown,
    /// <summary>源程序路径已不存在（被卸载或移动）。</summary>
    PathMissing,
    /// <summary>版本号升高/变化。</summary>
    VersionUpgraded,
    /// <summary>文件被替换但版本号未变（静默更新、补丁回滚等）。</summary>
    ExeReplaced,
}

/// <summary>
/// 源应用监视（F2）：分身不会复制应用本体，所以源应用一旦升级，分身的数据目录/设备身份虽仍在，
/// 但「当时是按哪个版本建的」这一基线就过期了——表现为行为静默改变，用户只感到「今天不好使了」。
/// 本类负责记录基线并检测变化。
///
/// 基线三元组：文件版本号 + 时间戳 + 大小。任一可得即视为有基线；
/// 版本号优先比对（可读性好），版本号取不到时退化为文件指纹比对。
/// </summary>
public static class SourceWatch
{
    /// <summary>读取源应用当前版本号：文件版本 → 产品版本 → 空串。</summary>
    public static string CaptureVersion(AppEntry? app)
    {
        try
        {
            if (app == null || string.IsNullOrWhiteSpace(app.ExePath) || !File.Exists(app.ExePath)) return "";
            var fvi = FileVersionInfo.GetVersionInfo(app.ExePath);
            if (!string.IsNullOrWhiteSpace(fvi.FileVersion)) return fvi.FileVersion.Trim();
            if (!string.IsNullOrWhiteSpace(fvi.ProductVersion)) return fvi.ProductVersion.Trim();
        }
        catch { }
        return "";
    }

    /// <summary>把源应用「当前」状态写成分身基线（创建分身 / 更新记录 / 重建后调用）。</summary>
    public static void Stamp(AppEntry? app, CloneEntry? clone)
    {
        if (app == null || clone == null) return;
        clone.SourceVersionAtCreate = CaptureVersion(app);
        try
        {
            var fi = new FileInfo(app.ExePath);
            if (fi.Exists)
            {
                clone.SourceExeMtimeAtCreate = fi.LastWriteTimeUtc.ToString("O");
                clone.SourceExeSizeAtCreate = fi.Length;
            }
        }
        catch { }
    }

    /// <summary>该分身是否已有源基线（老数据可能三项全空）。</summary>
    public static bool HasBaseline(CloneEntry? c) =>
        c != null && (c.SourceVersionAtCreate.Length > 0 || c.SourceExeMtimeAtCreate.Length > 0 || c.SourceExeSizeAtCreate > 0);

    /// <summary>
    /// 检测源应用是否偏离基线。同一应用的全部克隆共享基线与检测结果，故传入克隆集合。
    /// 返回 <see cref="SourceChangeKind.Unknown"/> 表示「无基线可判」，调用方应静默回填而非提示升级。
    /// </summary>
    public static SourceChangeKind Check(AppEntry? app, IEnumerable<CloneEntry>? clones,
        out string baseline, out string current, out string reason)
    {
        baseline = ""; current = ""; reason = "";
        if (app == null) return SourceChangeKind.Unknown;

        if (string.IsNullOrWhiteSpace(app.ExePath) || !File.Exists(app.ExePath))
        {
            reason = "源程序路径不存在（可能已被卸载或移动）";
            return SourceChangeKind.PathMissing;
        }

        CloneEntry? refC = null;
        if (clones != null)
            foreach (var c in clones)
                if (HasBaseline(c)) { refC = c; break; }

        current = CaptureVersion(app);
        if (refC == null)
        {
            reason = "尚未记录源应用版本基线";
            return SourceChangeKind.Unknown;
        }

        baseline = refC.SourceVersionAtCreate;
        if (baseline.Length > 0 && current.Length > 0 &&
            !string.Equals(baseline, current, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"源应用版本 {baseline} → {current}";
            return SourceChangeKind.VersionUpgraded;
        }

        // 版本号一致或双方都读不到版本 → 退化为文件指纹比对
        try
        {
            var fi = new FileInfo(app.ExePath);
            if (fi.Exists)
            {
                bool mtimeChanged = refC.SourceExeMtimeAtCreate.Length > 0 &&
                                    !string.Equals(refC.SourceExeMtimeAtCreate,
                                        fi.LastWriteTimeUtc.ToString("O"), StringComparison.Ordinal);
                bool sizeChanged = refC.SourceExeSizeAtCreate > 0 && refC.SourceExeSizeAtCreate != fi.Length;
                if (mtimeChanged || sizeChanged)
                {
                    reason = "源程序文件已被替换（版本号未变）";
                    return SourceChangeKind.ExeReplaced;
                }
            }
        }
        catch { }

        return SourceChangeKind.None;
    }
}
