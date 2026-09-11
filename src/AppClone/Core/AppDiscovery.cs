using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace AppClone.Core;

/// <summary>
/// 扫描引擎：后台线程采集本机已安装程序。
/// 源：S1 注册表 Uninstall（主力）→ S2 App Paths → S3 开始菜单 .lnk →（可选）Steam/Epic/UWP。
/// 每源带超时 + CancellationToken 包裹；逐源产出并入 DiscoveryResult。
/// 严禁使用 Win32_Product / WMI（会触发 MSI 一致性检查）。
/// </summary>
public static class AppDiscovery
{
    public const int BatchSize = 20;          // 批回填大小（共享知识 7）
    public const int BatchIntervalMs = 100;   // 批回填间隔

    private const int TimeoutWinget = 8000;   // 共享知识 9
    private const int TimeoutAppx = 6000;
    private const int TimeoutOther = 3000;

    public enum SourceState { Ok, Timeout, Skipped, Error }

    public sealed record ScanProgress(int SourceIndex, int SourceTotal, string SourceName, int Scanned, int Total);

    public sealed class DiscoveryOptions
    {
        public bool Steam { get; set; } = true;    // Q3：Steam 默认开
        public bool Epic { get; set; }              // Q3：Epic 默认关
        public bool Uwp { get; set; } = true;       // Q2：UWP 默认扫（灰显）
        public bool Winget { get; set; }            // Q4：winget 默认关（P2）
    }

    public sealed class DiscoveryResult
    {
        public List<DiscoveredApp> Apps { get; set; } = new();
        public Dictionary<string, SourceState> Status { get; set; } = new();
    }

    // ---------------------------------------------------------------- 主入口

    public static async Task<DiscoveryResult> ScanAsync(
        CancellationToken ct,
        IProgress<ScanProgress>? progress = null,
        DiscoveryOptions? opt = null)
    {
        opt ??= Settings.Current.ToDiscoveryOptions();
        var result = new DiscoveryResult();

        var sources = new List<(string Name, bool Uwp, Func<CancellationToken, Task<List<DiscoveredApp>>>)>
        {
            ("注册表已安装程序", false, ScanUninstall),
            ("App Paths", false, ScanAppPaths),
            ("开始菜单快捷方式", false, ScanStartMenu),
        };
        if (opt.Steam) sources.Add(("Steam 游戏库", false, c => GameLibrary.ScanSteam(c)));
        if (opt.Epic) sources.Add(("Epic 游戏库", false, c => GameLibrary.ScanEpic(c)));
        if (opt.Uwp) sources.Add(("微软商店应用", true, c => UwpScanner.ScanAsync(c)));

        int idx = 0;
        foreach (var (name, isUwp, fn) in sources)
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(new ScanProgress(idx, sources.Count, name, 0, 0));
            var items = await RunSource(name, isUwp, fn, result, ct);
            result.Apps.AddRange(items);
            idx++;
            progress?.Report(new ScanProgress(idx, sources.Count, name, items.Count, items.Count));
        }
        return result;
    }

    private static async Task<List<DiscoveredApp>> RunSource(
        string name, bool isUwp,
        Func<CancellationToken, Task<List<DiscoveredApp>>> fn,
        DiscoveryResult result, CancellationToken ct)
    {
        int timeout = isUwp ? TimeoutAppx : TimeoutOther;
        try
        {
            var task = fn(ct);
            var done = await Task.WhenAny(task, Task.Delay(timeout, ct));
            if (done != task)
            {
                result.Status[name] = SourceState.Timeout;
                return new List<DiscoveredApp>();
            }
            var items = await task;
            result.Status[name] = SourceState.Ok;
            return items ?? new List<DiscoveredApp>();
        }
        catch (OperationCanceledException) { result.Status[name] = SourceState.Skipped; return new(); }
        catch { result.Status[name] = SourceState.Error; return new(); }
    }

    // ---------------------------------------------------------------- S1 注册表 Uninstall

    private static async Task<List<DiscoveredApp>> ScanUninstall(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var list = new List<DiscoveredApp>();
            var roots = new (RegistryHive Hive, RegistryView View)[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),  // WOW6432Node
                (RegistryHive.CurrentUser, RegistryView.Default),
            };
            foreach (var (hive, view) in roots)
            {
                if (ct.IsCancellationRequested) break;
                using var root = RegistryKey.OpenBaseKey(hive, view)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        using var key = root.OpenSubKey(sub);
                        if (key == null) continue;
                        var d = BuildFromUninstall(key);
                        if (d != null) list.Add(d);
                    }
                    catch { }
                }
            }
            return list;
        }, ct);
    }

    private static DiscoveredApp? BuildFromUninstall(RegistryKey key)
    {
        var displayName = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        var displayIcon = (key.GetValue("DisplayIcon") as string) ?? "";
        var uninstall = (key.GetValue("UninstallString") as string) ?? "";
        var installLoc = (key.GetValue("InstallLocation") as string) ?? "";
        var publisher = (key.GetValue("Publisher") as string) ?? "";
        var version = (key.GetValue("DisplayVersion") as string) ?? "";
        int systemComp = ToInt(key.GetValue("SystemComponent"));
        var parentKey = (key.GetValue("ParentKeyName") as string) ?? "";
        var releaseType = (key.GetValue("ReleaseType") as string) ?? "";

        bool excluded = systemComp == 1
            || !string.IsNullOrEmpty(parentKey)
            || releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
            || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
            || releaseType.Contains("Security", StringComparison.OrdinalIgnoreCase)
            || AppClassifier.IsBlacklistedPublisher(publisher)
            || AppClassifier.IsExcludedByName(displayName);

        string? exe = ResolveExeFromUninstall(displayIcon, uninstall, installLoc);
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;   // 无法启动则跳过

        return new DiscoveredApp
        {
            Name = displayName,
            DisplayName = displayName,
            ExePath = exe,
            Publisher = publisher,
            Version = version,
            InstallLocation = installLoc,
            IconPath = SplitIcon(displayIcon),
            Source = "uninstall",
            Excluded = excluded,
        };
    }

    private static string? ResolveExeFromUninstall(string displayIcon, string uninstall, string installLoc)
    {
        string iconFile = SplitIcon(displayIcon);
        if (iconFile.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return File.Exists(iconFile) ? iconFile : null;
        if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
        {
            var exe = Directory.GetFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe != null) return exe;
        }
        return null;
    }

    // ---------------------------------------------------------------- S2 App Paths

    private static async Task<List<DiscoveredApp>> ScanAppPaths(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var list = new List<DiscoveredApp>();
            var roots = new (RegistryHive Hive, RegistryView View)[]
            {
                (RegistryHive.LocalMachine, RegistryView.Registry64),
                (RegistryHive.LocalMachine, RegistryView.Registry32),
                (RegistryHive.CurrentUser, RegistryView.Default),
            };
            const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
            foreach (var (hive, view) in roots)
            {
                if (ct.IsCancellationRequested) break;
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(sub);
                if (root == null) continue;
                foreach (var subName in root.GetSubKeyNames())
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        using var key = root.OpenSubKey(subName);
                        var target = (key?.GetValue("") as string) ?? "";
                        target = target.Trim().Trim('"', '\'');
                        if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!File.Exists(target)) continue;
                        string name = Path.GetFileNameWithoutExtension(subName);
                        list.Add(new DiscoveredApp
                        {
                            Name = name,
                            DisplayName = name,
                            ExePath = target,
                            IconPath = target,
                            Source = "apppaths",
                            Excluded = AppClassifier.IsExcludedByName(name),
                        });
                    }
                    catch { }
                }
            }
            return list;
        }, ct);
    }

    // ---------------------------------------------------------------- S3 开始菜单 .lnk

    private static async Task<List<DiscoveredApp>> ScanStartMenu(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var list = new List<DiscoveredApp>();
            var dirs = new List<string>();
            try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs")); } catch { }
            try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft\\Windows\\Start Menu\\Programs")); } catch { }

            foreach (var dir in dirs)
            {
                if (ct.IsCancellationRequested) break;
                if (Directory.Exists(dir)) WalkStartMenu(dir, 0, ct, list);
            }
            return list;
        }, ct);
    }

    private static void WalkStartMenu(string dir, int depth, CancellationToken ct, List<DiscoveredApp> list)
    {
        if (depth > 5 || ct.IsCancellationRequested) return;
        try
        {
            foreach (var file in Directory.GetFiles(dir, "*.lnk"))
            {
                if (ct.IsCancellationRequested) return;
                string name = Path.GetFileNameWithoutExtension(file);
                if (AppClassifier.IsExcludedByName(name)) continue;   // 卸载/帮助等
                string? target = ParseLnkTarget(file);
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(target)) continue;
                list.Add(new DiscoveredApp
                {
                    Name = name,
                    DisplayName = name,
                    ExePath = target,
                    IconPath = target,
                    Source = "startmenu",
                    Excluded = AppClassifier.IsExcludedByName(name),
                });
            }
            foreach (var sub in Directory.GetDirectories(dir))
                WalkStartMenu(sub, depth + 1, ct, list);
        }
        catch { }
    }

    /// <summary>解析 .lnk 目标路径（二进制 Shell Link 格式，MTA 安全；失败回退扫描盘符路径）。</summary>
    private static string? ParseLnkTarget(string lnk)
    {
        try
        {
            byte[] data = File.ReadAllBytes(lnk);
            if (data.Length < 76) return null;
            if (BitConverter.ToInt32(data, 0) != 0x0000004C) return null;  // ShellLinkHeader 签名
            int linkFlags = BitConverter.ToInt32(data, 4);
            int pos = 76;

            if ((linkFlags & 0x1) != 0)   // HasLinkTargetIDList
            {
                int size = BitConverter.ToInt16(data, pos);
                if (size <= 0 || size > data.Length - pos) return null;
                pos += size;
            }
            if ((linkFlags & 0x2) != 0 && pos + 4 <= data.Length)  // HasLinkInfo
            {
                int linkInfoSize = BitConverter.ToInt32(data, pos);
                if (linkInfoSize >= 28 && pos + linkInfoSize <= data.Length)
                {
                    int linkInfoFlags = BitConverter.ToInt32(data, pos + 8);
                    int localBasePathOffset = BitConverter.ToInt32(data, pos + 16);
                    int unicodeOffset = (linkInfoFlags & 0x2) != 0 ? BitConverter.ToInt32(data, pos + 20) : 0;
                    if (unicodeOffset > 0)
                    {
                        var s = ReadUtf16Z(data, pos + unicodeOffset);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                    if (localBasePathOffset > 0)
                    {
                        var s = ReadUtf16Z(data, pos + localBasePathOffset);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
            return ScanDrivePath(data);
        }
        catch { return null; }
    }

    private static string ReadUtf16Z(byte[] data, int offset)
    {
        if (offset < 0 || offset >= data.Length) return "";
        int end = offset;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
        int len = end - offset;
        return len <= 0 ? "" : Encoding.Unicode.GetString(data, offset, len);
    }

    private static string? ScanDrivePath(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i += 2)
        {
            if (data[i + 1] == 0 && char.IsLetter((char)data[i]) &&
                data[i + 2] == (byte)'\\' && data[i + 3] == 0)
            {
                var s = ReadUtf16Z(data, i);
                if (s.Length > 3) return s;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- 工具

    private static string SplitIcon(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return "";
        return displayIcon.Split(',')[0].Trim().Trim('"');
    }

    private static int ToInt(object? v)
    {
        try { return v == null ? 0 : Convert.ToInt32(v); }
        catch { return 0; }
    }
}
