using System.IO;
using System.Security.Principal;

namespace AppClone.Core;

/// <summary>预检查条目严重级别。</summary>
public enum PreflightSeverity { Info, Warn, Error }

/// <summary>一条预检查结果。<paramref name="Blocking"/> 为 true 时不得继续执行。</summary>
public record PreflightItem(PreflightSeverity Level, string Title, string Detail, bool Blocking);

/// <summary>
/// 启动前预检查（F3）：把「复制到一半才失败」的坑提前暴露出来。
/// 检查项全部只读探测（仅数据目录可写性会写一个临时文件后立即删除），不做任何持久变更。
/// </summary>
public static class Preflight
{
    /// <summary>检查应用（可带分身）是否具备启动条件。</summary>
    public static List<PreflightItem> Check(AppEntry? app, CloneEntry? clone = null)
    {
        var list = new List<PreflightItem>();
        if (app == null) return list;

        bool exeOk = !string.IsNullOrWhiteSpace(app.ExePath) && File.Exists(app.ExePath);

        // 1) 目标程序存在 —— 唯一的「硬阻断」条件之一
        if (!exeOk)
            list.Add(new PreflightItem(PreflightSeverity.Error, "目标程序不存在",
                string.IsNullOrWhiteSpace(app.ExePath) ? "该应用没有关联的可执行文件路径" : app.ExePath, true));

        // 2) 数据目录可写 —— 另一条硬阻断条件
        try
        {
            Paths.EnsureAll();
            var probe = Path.Combine(Paths.DataRoot, ".preflight.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            list.Add(new PreflightItem(PreflightSeverity.Error, "数据目录不可写",
                $"{Paths.DataRoot}：{ex.Message}", true));
        }

        // 3) 磁盘剩余空间（阈值可在设置中调整）
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Paths.DataRoot));
            if (!string.IsNullOrEmpty(root))
            {
                var di = new DriveInfo(root);
                long thresholdGb = Math.Max(1, Settings.Current.MinFreeDiskGB);
                long need = thresholdGb * 1024L * 1024L * 1024L;
                if (di.AvailableFreeSpace < need)
                    list.Add(new PreflightItem(PreflightSeverity.Warn, "磁盘剩余空间不足",
                        $"{root} 剩余 {di.AvailableFreeSpace / 1024 / 1024} MB，低于阈值 {thresholdGb} GB", false));
            }
        }
        catch { }

        // 4) 深度隔离组件位数是否匹配（不匹配时启动会静默降级为标准隔离）
        if (exeOk && (clone?.DeepIsolation ?? true))
        {
            bool is64 = IsExe64Bit(app.ExePath);
            string want = Path.Combine(Paths.SandboxDir, is64 ? "inject64.dll" : "inject32.dll");
            if (!File.Exists(want))
                list.Add(new PreflightItem(PreflightSeverity.Warn, "缺少深度隔离组件",
                    $"未找到 {want}，启动时将降级为标准隔离（仅隔离数据目录，可能仍受单实例限制）", false));
        }

        // 5) 权限提示（不阻断：绝大多数应用无需提权）
        bool admin = false;
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { }
        if (!admin)
            list.Add(new PreflightItem(PreflightSeverity.Info, "当前未以管理员身份运行",
                "绝大多数应用无需提权；个别需要管理员权限的应用可能启动后立即退出", false));

        return list;
    }

    /// <summary>是否存在阻断项。</summary>
    public static bool HasBlocking(IEnumerable<PreflightItem> items) => items.Any(i => i.Blocking);

    /// <summary>把预检查结果格式化为纯文本（供 Dry Run 预览与日志复用）。</summary>
    public static string Format(IEnumerable<PreflightItem> items)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in items)
        {
            string tag = i.Blocking ? "阻断" : i.Level switch
            {
                PreflightSeverity.Error => "错误",
                PreflightSeverity.Warn => "警告",
                _ => "提示",
            };
            sb.Append('[').Append(tag).Append("] ").Append(i.Title);
            if (i.Detail.Length > 0) sb.Append(" —— ").Append(i.Detail);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 判断 PE 文件是否 64 位（手工解析 PE 头，零依赖）。
    /// 无法判定时按 64 位处理——与 <see cref="Launcher"/> 的默认注入行为一致。
    /// </summary>
    public static bool IsExe64Bit(string? exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return true;
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x40) return true;
            using var br = new BinaryReader(fs);

            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > fs.Length) return true;

            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return true;   // "PE\0\0"
            ushort machine = br.ReadUInt16();
            return machine switch
            {
                0x014C => false,   // IMAGE_FILE_MACHINE_I386
                0x8664 => true,    // IMAGE_FILE_MACHINE_AMD64
                0xAA64 => true,    // ARM64
                _ => true,
            };
        }
        catch { return true; }
    }
}
