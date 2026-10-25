using System.Text;
using AppCloneSetup;

namespace SetupUiProbe;

/// <summary>
/// 安装程序 UI 探针（无 GUI）。
/// 覆盖三件事：路径选择行的几何布局、路径校验文案、文件夹选择器的路径回溯。
/// 全程不创建任何窗口句柄 —— 本环境的策略不允许拉起 GUI 进程。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> Failures = new();
    private static readonly StringBuilder Out = new();

    private static readonly string TempDir = Path.GetTempPath().TrimEnd('\\');
    private static readonly string DefaultInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "应用分身");

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Out.AppendLine($"  [PASS] {name}");
        }
        else
        {
            Failures.Add(name);
            Out.AppendLine($"  [FAIL] {name}{(detail is null ? "" : "   -> " + detail)}");
        }
    }

    private static void Section(string title) => Out.AppendLine().AppendLine($"== {title} ==");

    private static PathSelector NewSelector() => new()
    {
        AppName = "应用分身",
        ExeName = "AppClone.exe",
        DefaultPath = DefaultInstallDir,
        LastPath = "",
        SizeHint = "约 100 MB",
    };

    private static int Main()
    {
        Out.AppendLine("安装程序 UI 探针（无 GUI）");

        TestNearestExisting();
        TestLayout();
        TestValidation();
        TestQuickLocations();

        Out.AppendLine();
        Out.AppendLine($"PASS={_pass} FAIL={Failures.Count} SKIP=0");
        if (Failures.Count > 0)
        {
            Out.AppendLine("失败项：");
            foreach (var f in Failures) Out.AppendLine("  - " + f);
        }

        Console.OutputEncoding = Encoding.UTF8;
        string report = Out.ToString();
        Console.WriteLine(report);

        // 控制台重定向到文件时中文编码会打架，所以额外落一份 UTF-8 报告，便于直接查看。
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ui-probe-report.txt");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            Console.WriteLine("report: " + path);
        }
        catch { }

        return Failures.Count == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 路径回溯

    private static void TestNearestExisting()
    {
        Section("文件夹选择器：初始目录回溯");

        Check("null 返回 null", FolderPicker.NearestExisting(null) is null);
        Check("空白串返回 null", FolderPicker.NearestExisting("   ") is null);
        Check("已存在目录原样返回", FolderPicker.NearestExisting(TempDir) == TempDir,
            FolderPicker.NearestExisting(TempDir));

        string deep = Path.Combine(TempDir, "fenshen_no_such_a", "fenshen_no_such_b", "fenshen_no_such_c");
        Check("深层不存在路径回溯到最近存在的祖先", FolderPicker.NearestExisting(deep) == TempDir,
            FolderPicker.NearestExisting(deep));

        string fileDropped = Path.Combine(TempDir, "fenshen_no_such_file.txt");
        Check("拖入文件时回溯到其所在目录", FolderPicker.NearestExisting(fileDropped) == TempDir,
            FolderPicker.NearestExisting(fileDropped));

        Check("相对路径返回 null", FolderPicker.NearestExisting("relative_only") is null);
        Check("盘符根目录可用时原样返回",
            FolderPicker.NearestExisting("C:\\") == "C:\\" || FolderPicker.NearestExisting("C:\\") is null,
            FolderPicker.NearestExisting("C:\\"));
    }

    // ---------------------------------------------------------------- 布局

    private static void TestLayout()
    {
        Section("路径选择行的几何布局");

        Check("最小可用宽度 = 各列宽度之和",
            PathSelector.MinUsableWidth == 120 + 8 + 84 + 8 + 100, PathSelector.MinUsableWidth.ToString());
        Check("最小可用宽度远小于 640 宽对话框的内宽", PathSelector.MinUsableWidth <= 480,
            PathSelector.MinUsableWidth.ToString());

        // 512 ≈ 向导窗口（640）扣掉左右各 60 边距后的真实可用宽度
        foreach (int w in new[] { PathSelector.MinUsableWidth, 512, 640, 900, 1400 })
        {
            var sel = NewSelector();
            sel.Size = new Size(w, PathSelector.PreferredHeight);
            sel.PerformLayout();
            var b = sel.BoundsSnapshot();

            Check($"w={w} 输入框有正尺寸", b.Box.Width >= 120 && b.Box.Height > 0, b.Box.ToString());
            Check($"w={w} 输入框不越过「浏览」", b.Box.Right <= b.Browse.Left,
                $"box.Right={b.Box.Right} browse.Left={b.Browse.Left}");
            Check($"w={w} 「浏览」不越过「快速定位」", b.Browse.Right <= b.Quick.Left,
                $"browse.Right={b.Browse.Right} quick.Left={b.Quick.Left}");
            Check($"w={w} 「快速定位」完全落在控件内", b.Quick.Right <= w, $"quick.Right={b.Quick.Right} w={w}");
            Check($"w={w} 三者同一行且等高",
                b.Box.Top == b.Browse.Top && b.Browse.Top == b.Quick.Top && b.Box.Height == b.Quick.Height,
                $"box={b.Box} browse={b.Browse} quick={b.Quick}");
            Check($"w={w} 提示文字不越出控件高度", b.Hint.Bottom <= PathSelector.PreferredHeight,
                $"hint.Bottom={b.Hint.Bottom}");
            Check($"w={w} 标题占满整行", b.Label.Width == w, b.Label.ToString());
            Check($"w={w} 输入框接受拖放", sel.AcceptsDrop);
        }

        // 关键回归：老写法（Anchor=Right 的子控件 + 构造期 200 宽的宿主）会把输入框算到容器外。
        // 这里断言新实现在「还没被 Dock 撑开」的初始宽度下也绝不越界，防止有人改回 Anchor。
        var fresh = NewSelector();
        fresh.Size = new Size(PathSelector.MinUsableWidth, PathSelector.PreferredHeight);
        fresh.PerformLayout();
        Check("构造后立即取宽度也不越界（防 Anchor 回流）",
            fresh.BoundsSnapshot().Quick.Right <= PathSelector.MinUsableWidth,
            fresh.BoundsSnapshot().Quick.Right.ToString());
    }

    // ---------------------------------------------------------------- 校验

    private static void TestValidation()
    {
        Section("路径校验文案");

        var sel = NewSelector();

        void Hint(string path, string expect, string label)
        {
            sel.SelectedPath = path;
            Check(label, sel.HintText.Contains(expect), $"path=\"{path}\" hint=\"{sel.HintText}\"");
        }

        Hint("", "浏览", "空路径时引导使用选择器／快速定位");
        Hint(@"C:\bad|name", "非法字符", "非法字符被拦下");
        Hint(@"relative\only", "完整路径", "相对路径被拦下");

        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        Hint(root, "根目录", "盘符根目录被劝退");

        Hint(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FenShenTest"),
            "管理员权限", "受保护目录提示需要提权");

        string freshDir = Path.Combine(TempDir, "fenshen_probe_" + Guid.NewGuid().ToString("N")[..8]);
        Hint(freshDir, "将新建", "不存在的目录提示将新建");

        Directory.CreateDirectory(freshDir);
        Hint(freshDir, "目录已存在", "已存在的目录提示直接使用");

        File.WriteAllText(Path.Combine(freshDir, "AppClone.exe"), "probe");
        Hint(freshDir, "已有", "检测到既有安装时提示会覆盖而非丢失数据");

        Hint(DefaultInstallDir, "安装", "默认位置也能给出提示");
        sel.SelectedPath = DefaultInstallDir;
        Check("默认位置的提示非空", sel.HintText.Trim().Length > 0, sel.HintText);

        Directory.Delete(freshDir, true);
    }

    // ---------------------------------------------------------------- 快速定位

    private static void TestQuickLocations()
    {
        Section("「快速定位」候选位置");

        var sel = NewSelector();
        var items = sel.QuickLocations();
        var real = items.Where(i => i.Text != "-").ToList();
        var drives = real.Where(i => i.Text.Contains('→')).ToList();

        Check("候选非空", items.Count > 0, items.Count.ToString());
        Check("首项不是分隔线", items.Count > 0 && items[0].Text != "-");
        Check("末项不是分隔线", items.Count > 0 && items[^1].Text != "-");
        Check("没有连续分隔线",
            !Enumerable.Range(1, Math.Max(0, items.Count - 1)).Any(i => items[i].Text == "-" && items[i - 1].Text == "-"));
        Check("至少两组分隔（默认组 / 盘符组）", items.Count(i => i.Text == "-") >= 2,
            items.Count(i => i.Text == "-").ToString());

        Check("含默认位置项", real.Any(i => i.Text.StartsWith("默认位置") && i.Path == DefaultInstallDir));
        Check("每项都有文案", real.All(i => i.Text.Trim().Length > 0));
        Check("每项都是绝对路径", real.All(i => Path.IsPathRooted(i.Path)),
            string.Join(" | ", real.Where(i => !Path.IsPathRooted(i.Path)).Select(i => i.Path)));
        Check("至少枚举出一个本机盘符", drives.Count > 0, drives.Count.ToString());
        Check("盘符项标注了可用空间", drives.All(i => i.Text.Contains("可用")),
            string.Join(" | ", drives.Select(i => i.Text)));

        bool drivesOk = true;
        string bad = "";
        foreach (var d in drives)
        {
            string dr = Path.GetPathRoot(d.Path) ?? "";
            if (dr.Length == 0
                || !d.Path.StartsWith(dr, StringComparison.OrdinalIgnoreCase)
                || !d.Path.EndsWith(sel.AppName, StringComparison.Ordinal))
            {
                drivesOk = false;
                bad = d.Path;
                break;
            }
        }
        Check("盘符项落在该盘符下且以应用名结尾", drivesOk, bad);

        // 上次使用位置：可用才收录、与默认相同时不重复、已失效则不收录
        var withLast = NewSelector();
        withLast.LastPath = TempDir;
        Check("可用的「上次使用」进入候选",
            withLast.QuickLocations().Any(i => i.Text.StartsWith("上次使用") && i.Path == TempDir));

        var sameAsDefault = NewSelector();
        sameAsDefault.LastPath = DefaultInstallDir;
        Check("上次位置与默认相同时不重复罗列",
            !sameAsDefault.QuickLocations().Any(i => i.Text.StartsWith("上次使用")));

        var dead = NewSelector();
        dead.LastPath = Path.Combine(TempDir, "fenshen_no_such_last_dir");
        Check("已失效的「上次使用」不进候选",
            !dead.QuickLocations().Any(i => i.Text.StartsWith("上次使用")));

        var current = NewSelector();
        current.LastPath = TempDir;
        current.SelectedPath = TempDir;
        Check("「上次使用」与当前路径相同时不重复罗列",
            !current.QuickLocations().Any(i => i.Text.StartsWith("上次使用")));

        // 路径往返
        var round = NewSelector();
        round.SelectedPath = "  D:\\fenshen_round_trip  ";
        Check("SelectedPath 去掉首尾空白", round.SelectedPath == "D:\\fenshen_round_trip", round.SelectedPath);

        var swap = NewSelector();
        string previous = swap.SelectedPath;
        int changes = 0;
        swap.PathChanged += (_, _) => changes++;
        swap.SelectedPath = TempDir;
        Check("改动路径会触发 PathChanged", changes == 1 && TempDir != previous, changes.ToString());
    }
}
