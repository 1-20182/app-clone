using System.Reflection;
using System.Text;
using AppClone;
using AppClone.Core;
using AppClone.Forms;

namespace UiAudit;

/// <summary>
/// UI 布局审计探针（无 GUI）。
///
/// 全程**不创建窗口句柄、不显示任何窗口** —— 只靠 Control.PerformLayout() 逼出真实几何，
/// 再递归遍历控件树做三类判定：
///
///   1. 越界 —— 子控件右/下边缘超出父容器客户区（视觉上被裁掉，且无法滚动看到）
///   2. 重叠 —— 同一容器内的兄弟控件矩形相交（视觉上被盖住，且下层无法点击）
///   3. 硬裁 —— AutoSize=false 且未开 AutoEllipsis 的 Label，文本所需宽度大于控件宽度
///
/// 这三种正是「我自己看不到界面、用户却说某个按钮不见了」的根因类别。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static readonly List<string> Failures = new();
    private static readonly SortedSet<string> Issues = new(StringComparer.Ordinal);
    private static readonly List<string> Warnings = new();
    private static readonly StringBuilder Out = new();

    /// <summary>几何容差：停靠边框与舍入会产生 1px 误差，不算问题。</summary>
    private const int Tol = 1;

    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "tmp");

    // ------------------------------------------------------------------ 输出

    private static void Head(string s) => Out.AppendLine().AppendLine($"== {s} ==");

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Out.AppendLine($"  [PASS] {name}"); }
        else { Failures.Add(name); Out.AppendLine($"  [FAIL] {name}{(detail is null ? "" : "   -> " + detail)}"); }
    }

    /// <summary>审计发现的问题：去重后每条都是一处需要修的缺陷。</summary>
    private static void Issue(string s) => Issues.Add(s);

    private static void Warn(string s)
    {
        if (!Warnings.Contains(s)) Warnings.Add(s);
    }

    private static string Clip(string s, int n = 14)
    {
        s = s.Replace("\r", "").Replace("\n", "\\n");
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static string Desc(Control c)
    {
        string name = c.Name.Length > 0 ? c.Name : c.GetType().Name;
        string text = c.Text.Length > 0 ? $"「{Clip(c.Text)}」" : "";
        string dock = c.Dock == DockStyle.None ? "" : $"[{c.Dock}]";
        return name + text + dock;
    }

    // ------------------------------------------------------------------ 布局

    /// <summary>强制布局：自顶向下把每一层容器都排一遍。用显式栈而非递归，避免深树爆栈。</summary>
    private static void Relayout(Control root)
    {
        var stack = new Stack<Control>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            c.PerformLayout();
            foreach (Control child in c.Controls) stack.Push(child);
        }
    }

    /// <summary>实时步骤日志：崩溃时用来定位是哪一步炸的（控制台输出会被管道吞掉）。</summary>
    private static void Step(string s)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "ui-audit-steps.txt"),
                $"{DateTime.Now:HH:mm:ss.fff}  {s}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Control 内部的状态位方法，用来读 STATE_VISIBLE。</summary>
    private static readonly MethodInfo? GetStateMi =
        typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic);

    private const int StateVisible = 0x02;

    /// <summary>
    /// 控件**自身**是否可见 —— 不受父链影响。
    ///
    /// 关键：探针从不显示窗口（不调 Show / 不建句柄），而 <see cref="Control.Visible"/> 的 getter 会
    /// 沿父链上溯，只要顶层窗体没显示就一律返回 false。若直接用它过滤，会把**全部控件**滤掉，
    /// 审计变成对空集合判定 —— 结果是一片虚假的绿灯（本探针第一版就栽在这上面）。
    /// 内部 STATE_VISIBLE 位记录的才是"该控件自己被设成可见/隐藏"。
    /// </summary>
    private static bool SelfVisible(Control c)
    {
        if (GetStateMi is not null)
        {
            try { return (bool)GetStateMi.Invoke(c, new object[] { StateVisible })!; }
            catch { }
        }
        return c.Visible;
    }

    /// <summary>审计一个容器的直接子控件；随后递归进入每个子容器。</summary>
    private static void Audit(Control parent, string path)
    {
        var scrollable = parent as ScrollableControl;
        bool scroll = scrollable?.AutoScroll == true;
        var kids = parent.Controls.Cast<Control>().Where(SelfVisible).ToList();

        foreach (var c in kids)
        {
            // ---------- 1) 越界 ----------
            // AutoScroll 容器允许内容溢出（可滚动查看），用内容区宽度做上限。
            int limitW = scroll ? scrollable!.DisplayRectangle.Width : parent.ClientSize.Width;
            int limitH = scroll ? scrollable!.DisplayRectangle.Height : parent.ClientSize.Height;

            if (c.Dock != DockStyle.Fill && c.Right > limitW + Tol)
                Issue($"{path} → {Desc(c)} 右侧越界 {c.Right - limitW}px（right={c.Right}，上限 {limitW}）");
            // AutoScroll 容器允许垂直滚动 —— 内容比可视区高是设计意图，不算越界。
            if (!scroll && c.Dock != DockStyle.Fill && c.Bottom > limitH + Tol)
                Issue($"{path} → {Desc(c)} 底部越界 {c.Bottom - limitH}px（bottom={c.Bottom}，上限 {limitH}）");

            // ---------- 3) 硬裁（AutoSize=false 且没有省略号）----------
            if (c is Label { AutoSize: false, AutoEllipsis: false } lb && lb.Text.Length > 0)
            {
                int need = TextRenderer.MeasureText(lb.Text, lb.Font).Width;
                if (need > lb.Width + Tol)
                    Issue($"{path} → {Desc(c)} 文本被硬裁：需要 {need}px，控件仅 {lb.Width}px");
            }
        }

        // ---------- 2) 兄弟重叠 ----------
        for (int i = 0; i < kids.Count; i++)
            for (int j = i + 1; j < kids.Count; j++)
            {
                var a = kids[i];
                var b = kids[j];
                // 两个都停靠 → 由布局引擎切割空间，不会真重叠
                if (a.Dock != DockStyle.None && b.Dock != DockStyle.None) continue;

                var ra = new Rectangle(a.Location, a.Size);
                var rb = new Rectangle(b.Location, b.Size);
                var hit = Rectangle.Intersect(ra, rb);
                if (hit.Width <= Tol || hit.Height <= Tol) continue;   // 仅边界相接

                // 浮层（Anchor=None 的插画卡片压在 Fill 容器上）是**有意的**叠放 —— 只有当上层
                // 那层是"不透明的整面容器"时才算遮挡：Fill 容器会挡住下面所有兄弟。
                Issue($"{path} 兄弟重叠：{Desc(a)} ∩ {Desc(b)} = {hit.Width}×{hit.Height}px");
            }

        // ---------- AutoScroll 容器的横向滚动提示 ----------
        if (scroll && scrollable != null)
        {
            int contentRight = kids.Where(k => k.Dock == DockStyle.None)
                .Select(k => k.Right).DefaultIfEmpty(0).Max();
            if (contentRight > scrollable.ClientSize.Width + Tol)
                Warn($"{path} 内容宽 {contentRight}px > 可视宽 {scrollable.ClientSize.Width}px（窄窗口需横向滚动，卡片未随宽度自适应）");
        }

        foreach (var c in kids) Audit(c, path + "/" + Desc(c));
    }

    /// <summary>把窗体摆到指定尺寸并审计。</summary>
    private static readonly List<string> AuditedSizes = new();

    private static void AuditForm(Form f, string name, params Size[] sizes)
    {
        foreach (var s in sizes)
        {
            Step($"{name}: resize->{s.Width}x{s.Height}");
            f.Size = s;
            Step($"{name}: relayout");
            Relayout(f);
            string tag = $"{name}@{s.Width}x{s.Height}";
            Out.AppendLine($"  · {tag}（客户区 {f.ClientSize.Width}x{f.ClientSize.Height}）");
            Dump(f, "      ");
            Step($"{name}: audit");
            Audit(f, tag);
            Step($"{name}: ok");
            AuditedSizes.Add(tag);
        }
    }

    /// <summary>打印控件树几何（深度 ≤ 3），用来核对 Dock 布局到底有没有真的执行。</summary>
    private static void Dump(Control parent, string indent, int depth = 0)
    {
        if (depth > 5) return;
        foreach (Control c in parent.Controls)
        {
            if (depth <= 3)
                Out.AppendLine($"{indent}{c.GetType().Name}{(c.Name.Length > 0 ? " " + c.Name : "")}"
                    + $"{(c.Dock == DockStyle.None ? "" : " dock=" + c.Dock)}"
                    + $" [{c.Left},{c.Top} {c.Width}x{c.Height}] vis={(SelfVisible(c) ? 1 : 0)}"
                    + (c.Text.Length > 0 ? $" 「{Clip(c.Text, 10)}」" : ""));
            Dump(c, indent + "  ", depth + 1);
        }
    }

    /// <summary>调用窗体的私有刷新方法（如 Reload / RefreshList），把真实数据喂进控件。</summary>
    private static void Invoke(object target, string method)
    {
        var m = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        if (m == null) { Warn($"{target.GetType().Name}.{method}() 不存在 —— 数据未填充，审计结果不可信"); return; }
        try { m.Invoke(target, null); }
        catch (Exception ex) { Warn($"{target.GetType().Name}.{method}() 抛出：{ex.InnerException?.Message ?? ex.Message}"); }
    }

    // ------------------------------------------------------------------ DPI 150% 仿真

    /// <summary>
    /// 150% DPI 仿真：把整棵控件树的字体放大 1.5 倍（点数字体在真实高 DPI 下的行为），
    /// 再用 Control.Scale 把几何整体放大 1.5 倍（AutoScale / 手动 S() 在真实高 DPI 下的行为）。
    /// 两者叠加后做与 96 DPI 完全相同的三类判定 —— 「真机上显示不全、探针却全绿」的补丁。
    /// 探针进程跑在 96 DPI，窗体内的 S() 恒等，因此必须在这里显式施加缩放才能覆盖到高 DPI 场景。
    /// </summary>
    private static void EmulateDpi(Form f, float factor)
    {
        GrowFonts(f, factor);
        f.Scale(new SizeF(factor, factor));
        Relayout(f);
    }

    private static void GrowFonts(Control c, float k)
    {
        var font = c.Font;
        c.Font = new Font(font.FontFamily, (float)Math.Round(font.Size * k * 2) / 2f, font.Style, font.Unit);
        foreach (Control child in c.Controls) GrowFonts(child, k);
    }

    /// <summary>在 150% 仿真下摆到指定尺寸并审计（尺寸为 96 DPI 逻辑值 ×1.5）。</summary>
    private static void AuditForm150(Form f, string name, params Size[] sizes)
    {
        EmulateDpi(f, 1.5f);
        foreach (var s in sizes)
        {
            f.Size = s;
            Relayout(f);
            string tag = $"{name}@150%/{s.Width}x{s.Height}";
            Out.AppendLine($"  · {tag}（客户区 {f.ClientSize.Width}x{f.ClientSize.Height}）");
            Audit(f, tag);
            AuditedSizes.Add(tag);
        }
    }

    // ------------------------------------------------------------------ 入口

    private static int Main()
    {
        // 数据目录重定向到探针自己的 tmp\，绝不触碰用户真实数据（必须在任何 Paths 访问之前）
        Environment.SetEnvironmentVariable("FENSHEN_DATA_DIR", DataDir);
        Directory.CreateDirectory(DataDir);

        Out.AppendLine("AppClone UI 布局审计探针（无 GUI）");

        try { File.Delete(Path.Combine(AppContext.BaseDirectory, "ui-audit-steps.txt")); } catch { }
        Step("boot");

        var store = Store.Load();
        Step($"store.loaded apps={store.Model.Apps.Count}");

        AuditedSettings();
        Step("done:settings");
        AuditedHelp();
        Step("done:help");
        AuditedLock();
        Step("done:lock");
        AuditedCloneManager(store);
        Step("done:clonemanager");
        AuditedLogViewer();
        Step("done:logviewer");
        AuditedDryRun();
        Step("done:dryrun");
        AuditedSnapshots();
        Step("done:snapshots");

        Out.AppendLine();
        if (Warnings.Count > 0)
        {
            Out.AppendLine($"提示（{Warnings.Count} 条，不计入失败）：");
            foreach (var w in Warnings) Out.AppendLine("  ~ " + w);
            Out.AppendLine();
        }

        foreach (var issue in Issues) Check("布局: " + issue, false);
        if (Issues.Count == 0)
            Check($"布局审计未发现越界 / 重叠 / 硬裁（{AuditedSizes.Count} 个窗体尺寸组合）", true);

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
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ui-audit-report.txt");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            Console.WriteLine("report: " + path);
        }
        catch { }

        return Failures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 各窗体

    private static void AuditedSettings()
    {
        Head("SettingsForm（设置）");
        var f = new SettingsForm();
        // 默认 / 最小 / 最小以下（若 MinimumSize 生效则会被夹回最小）/ 放大
        AuditForm(f, "Settings", new Size(580, 720), new Size(480, 560), new Size(400, 500), new Size(900, 900));

        Head("SettingsForm（设置 · 150% DPI 仿真）");
        using var f150 = new SettingsForm();
        AuditForm150(f150, "Settings", new Size(870, 1080), new Size(720, 840), new Size(1350, 1350));
    }

    private static void AuditedHelp()
    {
        Head("HelpForm（内置帮助）");
        using (var f = new HelpForm()) AuditForm(f, "Help", f.Size, new Size(640, 440), new Size(600, 420), new Size(1200, 800));

        Head("HelpForm（内置帮助 · 150% DPI 仿真）");
        using var f150 = new HelpForm();
        AuditForm150(f150, "Help", new Size(1350, 930), new Size(960, 660), new Size(1800, 1200));
    }

    private static void AuditedLock()
    {
        Head("LockForm（启动解锁）");
        using (var f = new LockForm()) AuditForm(f, "Lock", f.Size);

        Head("PasswordSetupForm（设置密码）");
        AppLock.Set("");
        using (var f = new PasswordSetupForm()) AuditForm(f, "PasswordSetup", f.Size);

        Head("LockForm（启动解锁 · 150% DPI 仿真）");
        using (var f = new LockForm())
            AuditForm150(f, "Lock", new Size((int)(f.Width * 1.5f), (int)(f.Height * 1.5f)));

        Head("PasswordSetupForm（修改密码 · 150% DPI 仿真）");
        AppLock.Set("audit-1234");
        using (var f = new PasswordSetupForm())
            AuditForm150(f, "PasswordSetupChange", new Size((int)(f.Width * 1.5f), (int)(f.Height * 1.5f)));
        AppLock.Set("");
    }

    private static void AuditedCloneManager(Store store)
    {
        Head("CloneManagerForm（分身管理中心）");
        using var f = new CloneManagerForm(store);
        Invoke(f, "RefreshList");
        AuditForm(f, "CloneManager", new Size(1080, 660), new Size(880, 480), new Size(820, 460), new Size(1400, 900));

        Head("CloneManagerForm（分身管理中心 · 150% DPI 仿真）");
        using var f150 = new CloneManagerForm(store);
        Invoke(f150, "RefreshList");
        AuditForm150(f150, "CloneManager", new Size(1620, 990), new Size(1320, 720), new Size(2100, 1350));
    }

    private static void AuditedLogViewer()
    {
        Head("LogViewerForm（操作日志）");
        using var f = new LogViewerForm(null, null);
        Invoke(f, "Reload");
        AuditForm(f, "LogViewer", new Size(940, 600), new Size(700, 420), new Size(660, 400), new Size(1200, 800));

        Head("LogViewerForm（操作日志 · 150% DPI 仿真）");
        using var f150 = new LogViewerForm(null, null);
        Invoke(f150, "Reload");
        AuditForm150(f150, "LogViewer", new Size(1410, 900), new Size(1050, 630), new Size(1800, 1200));
    }

    private static void AuditedDryRun()
    {
        Head("DryRunForm（启动预览）");

        var app = new AppEntry
        {
            Id = "audit_app", Name = "某浏览器（长名称压力测试用）",
            ExePath = @"C:\Program Files\Some Vendor\Some Browser\browser.exe",
        };
        var clone = new CloneEntry { Id = "audit_clone", Name = "分身 1", DeepIsolation = true, DeviceClone = true };

        var items = new List<PreflightItem>
        {
            new(PreflightSeverity.Warn, "磁盘剩余空间偏低", "目标盘剩余 4.2 GB，低于设置的 5 GB 阈值", false),
            new(PreflightSeverity.Info, "源应用已更新", "创建分身时为 120.0.1，当前为 121.0.2", false),
        };
        var plan = new Launcher.LaunchPlan(
            new Dictionary<string, string?>
            {
                ["USERPROFILE"] = @"D:\fenshen\data\profiles\audit_app\audit_clone",
                ["APPDATA"] = @"D:\fenshen\data\profiles\audit_app\audit_clone\AppData\Roaming",
            },
            @"D:\fenshen\dist\sandbox\inject64.dll", "", @"Software\FenShen\audit_app\audit_clone",
            "DESKTOP-FENSHEN01", true, true,
            new List<string> { "准备分身数据目录", "写入注册表私有键快照", "注入沙盒组件并启动目标程序" });

        using var f = new DryRunForm(app, clone, items, plan);
        AuditForm(f, "DryRun", new Size(720, 620), new Size(560, 420), new Size(520, 400), new Size(1100, 800));

        Head("DryRunForm（启动预览 · 150% DPI 仿真）");
        using var f150 = new DryRunForm(app, clone, items, plan);
        AuditForm150(f150, "DryRun", new Size(1080, 930), new Size(840, 630), new Size(1650, 1200));
    }

    private static void AuditedSnapshots()
    {
        Head("SnapshotsForm（快照管理）");

        var app = new AppEntry { Id = "audit_app2", Name = "某聊天工具" };
        var clone = new CloneEntry { Id = "audit_clone2", Name = "小号" };

        using var f = new SnapshotsForm(app, clone);
        Invoke(f, "RefreshList");
        AuditForm(f, "Snapshots", new Size(460, 380), new Size(380, 280), new Size(360, 260), new Size(700, 600));

        Head("SnapshotsForm（快照管理 · 150% DPI 仿真）");
        using var f150 = new SnapshotsForm(app, clone);
        Invoke(f150, "RefreshList");
        AuditForm150(f150, "Snapshots", new Size(690, 570), new Size(570, 420), new Size(1050, 900));
    }
}
