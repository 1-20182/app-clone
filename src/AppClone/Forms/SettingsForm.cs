using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using AppClone;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 全局设置窗口（T17）：承载外观 / 扫描源 / 批量默认 / 启动与窗口 / 交互安全 / 数据与目录 / 关于 全部设置项，
/// 编辑即持久化（Settings.Save）。按 Settings.Theme 应用主题，并通过 Ui.*Of() 主题访问器配色。
/// 浅色 / 深色 单选切换后即时重绘本窗口并写入设置、广播主题变更。
/// </summary>
public class SettingsForm : Form
{
    private Panel _content = null!;
    private Panel _header = null!;
    private Panel _footer = null!;
    private Label _titleLbl = null!;
    private readonly List<AppleCard> _cards = new();

    /// <summary>导入回调（由 MainForm 注入）：用它持有的 _store 落地，避免另开 Store 实例互相覆盖。</summary>
    private readonly Func<string, string>? _import;

    public SettingsForm(Func<string, string>? import = null)
    {
        _import = import;
        Settings.Load();
        Ui.Theme = Settings.Current.Theme;

        Text = "设置";
        Size = new Size(S(580), S(720));
        MinimumSize = new Size(S(480), S(560));
        StartPosition = FormStartPosition.CenterParent;
        // 注意：本窗体**不走** AutoScaleMode —— ApplyTheme 会运行期重建内容，
        // 构造期逻辑值 + 加载期框架缩放 + 重建期物理值三套口径会互相打架；
        // 这里统一为「所有字面量一律经 S() 手动换算为物理像素」，构造期与重建期同一条代码路径。
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = MainForm.TryAppIcon();

        BuildChrome();
        BuildContent();
        Shown += (_, _) => { Ui.RoundWindow(this); FadeAnimator.FadeIn(this); };
    }

    // ------------------------------------------------------------------ 外框

    /// <summary>
    /// 96 DPI 逻辑像素 → 当前设备物理像素。窗体不走 AutoScale（见构造函数注释），
    /// 所有手写像素必须经此换算；96 DPI 环境下恒等，探针断言不受影响。
    /// </summary>
    private int S(int v) => Ui.S(this, v);

    private void BuildChrome()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = S(56), BackColor = Ui.BgOf() };
        header.Paint += (_, e) =>
        {
            using var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, header.Height - 2, header.Width, 2),
                Color.FromArgb(70, Ui.ThemedColor(ThemeRole.Accent)), Color.FromArgb(70, ColorTranslator.FromHtml("#5E5CE6")), 0F);
            e.Graphics.FillRectangle(b, 0, header.Height - 2, header.Width, 2);
        };
        var title = Ui.Label("设置", Ui.FTitle, true, Ui.TextPrimaryOf());
        title.Location = new Point(S(22), S(14));
        header.Controls.Add(title);
        _titleLbl = title;
        Controls.Add(header);
        header.BringToFront();
        _header = header;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(60), BackColor = Ui.BgOf() };
        var btnClose = new AppleButton("完成", AppleButton.Style.Filled) { Size = new Size(S(120), S(36)), Location = new Point(footer.Width - S(140), S(12)) };
        btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClose.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        footer.Controls.Add(btnClose);
        Controls.Add(footer);
        footer.BringToFront();
        _footer = footer;

        _content = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            BackColor = Ui.BgOf(), Padding = new Padding(S(20), S(16), S(20), S(12)),
        };
        // 卡片宽度跟随窗口伸缩（行面板内部已锚定，随之联动）
        _content.Resize += (_, _) =>
        {
            int w = InnerCardWidth;
            foreach (var c in _cards) c.Width = w;
        };
        Controls.Add(_content);
    }

    /// <summary>内容区可用宽度：扣掉左右 Padding（此前直接用 ClientSize，卡片右溢 36px）。</summary>
    private int InnerCardWidth => _content.ClientSize.Width - _content.Padding.Horizontal - S(4);

    // ------------------------------------------------------------------ 内容

    private void BuildContent()
    {
        _content.Controls.Clear();
        _cards.Clear();
        int y = 12;
        y = AddSection("外观", y, BuildAppearance());
        y = AddSection("发现 / 扫描源", y, BuildScanSources());
        y = AddSection("批量添加默认", y, BuildBatchDefaults());
        y = AddSection("启动与窗口", y, BuildStartup());
        y = AddSection("交互与安全", y, BuildSafety());
        y = AddSection("日志与诊断", y, BuildLogging());
        y = AddSection("数据与目录", y, BuildDataDir());
        y = AddSection("关于", y, BuildAbout());
    }

    private int AddSection(string title, int y, Control[] rows)
    {
        var card = new AppleCard
        {
            Location = new Point(0, y), Width = InnerCardWidth, Radius = S(14),
            DrawBorder = true, Shadow = true, BackColor = Ui.CardOf(),
        };
        _cards.Add(card);
        int cy = S(14);
        var cap = Ui.Label(title, Ui.FSection, true, Ui.TextPrimaryOf());
        cap.Location = new Point(S(16), cy);
        card.Controls.Add(cap);
        cy += S(30);
        foreach (var row in rows)
        {
            row.Location = new Point(S(16), cy);
            if (row is Panel p)
            {
                // 行面板随卡片伸缩，内部的右锚定控件（下拉/按钮）随之移动；
                // AutoSize 的勾选框不参与（否则会被拉伸）。
                p.Width = card.Width - S(32);
                p.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            }
            card.Controls.Add(row);
            cy += row.Height + S(10);
        }
        card.Height = cy + S(12);
        _content.Controls.Add(card);
        return y + card.Height + S(14);
    }

    private Control[] BuildAppearance()
    {
        var panel = new Panel { Height = S(28), Width = _content.ClientSize.Width - S(76) };
        var lbl = Ui.Label("主题", Ui.FBody, false, Ui.TextPrimaryOf());
        lbl.Location = new Point(0, S(6));
        var rbLight = new RadioButton { Text = "浅色", AutoSize = true, Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextPrimaryOf(), Location = new Point(S(120), S(6)) };
        var rbDark = new RadioButton { Text = "深色", AutoSize = true, Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextPrimaryOf(), Location = new Point(S(200), S(6)) };
        rbLight.Checked = Settings.Current.Theme == AppTheme.Light;
        rbDark.Checked = Settings.Current.Theme == AppTheme.Dark;
        rbLight.CheckedChanged += (_, _) => { if (rbLight.Checked) ApplyTheme(AppTheme.Light); };
        rbDark.CheckedChanged += (_, _) => { if (rbDark.Checked) ApplyTheme(AppTheme.Dark); };
        panel.Controls.Add(lbl); panel.Controls.Add(rbLight); panel.Controls.Add(rbDark);
        return new Control[] { panel };
    }

    private Control[] BuildScanSources()
    {
        return new Control[]
        {
            MakeCheck("启用发现缓存（关闭后每次全量扫描，且不写 discovered.json）", Settings.Current.EnableDiscoveryCache, v => Settings.Current.EnableDiscoveryCache = v),
            MakeCheck("扫描 Steam 游戏库", Settings.Current.ScanSteam, v => Settings.Current.ScanSteam = v),
            MakeCheck("扫描 Epic 游戏库", Settings.Current.ScanEpic, v => Settings.Current.ScanEpic = v),
            MakeCheck("扫描微软商店应用（灰显，暂不支持分身）", Settings.Current.ScanUwp, v => Settings.Current.ScanUwp = v),
            MakeCheck("扫描 winget（默认关闭，较慢）", Settings.Current.ScanWinget, v => Settings.Current.ScanWinget = v),
        };
    }

    private Control[] BuildBatchDefaults()
    {
        var mode = new ComboBox
        {
            Size = new Size(S(160), S(30)), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(Ui.FBody),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Ui.CardOf(),
        };
        mode.Items.AddRange(new object[] { "标准隔离", "深度隔离（推荐）" });
        mode.SelectedIndex = Settings.Current.DefaultIsolationMode == IsolationMode.Deep ? 1 : 0;
        mode.SelectedIndexChanged += (_, _) =>
        {
            Settings.Current.DefaultIsolationMode = mode.SelectedIndex == 1 ? IsolationMode.Deep : IsolationMode.Standard;
            Settings.Current.Save();
        };
        var modeRow = RowWithLabel("默认隔离模式", mode);

        var count = new ComboBox
        {
            Size = new Size(S(80), S(30)), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(Ui.FBody),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Ui.CardOf(),
        };
        count.Items.AddRange(new object[] { "1", "2", "3" });
        count.SelectedIndex = Math.Clamp(Settings.Current.DefaultCloneCount, 1, 3) - 1;
        count.SelectedIndexChanged += (_, _) =>
        {
            Settings.Current.DefaultCloneCount = count.SelectedIndex + 1;
            Settings.Current.Save();
        };
        var countRow = RowWithLabel("每个应用默认创建分身数", count);

        return new Control[]
        {
            modeRow,
            countRow,
            MakeCheck("默认开启设备分身（独立设备指纹）", Settings.Current.DefaultDeviceClone, v => Settings.Current.DefaultDeviceClone = v),
        };
    }

    private Control[] BuildStartup()
    {
        var close = new ComboBox
        {
            Size = new Size(S(160), S(30)), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(Ui.FBody),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Ui.CardOf(),
        };
        close.Items.AddRange(new object[] { "最小化到托盘", "直接退出" });
        close.SelectedIndex = Settings.Current.CloseBehavior == CloseBehavior.Exit ? 1 : 0;
        close.SelectedIndexChanged += (_, _) =>
        {
            Settings.Current.CloseBehavior = close.SelectedIndex == 1 ? CloseBehavior.Exit : CloseBehavior.Minimize;
            Settings.Current.Save();
        };
        var closeRow = RowWithLabel("关闭主窗口时", close);

        return new Control[]
        {
            // T13：开机自启联动 AutoStart
            MakeCheck("开机自动启动", Settings.Current.LaunchOnStartup, v =>
            {
                Settings.Current.LaunchOnStartup = v;
                AutoStart.SetEnabled(v);
            }),
            closeRow,
            MakeCheck("双击原图标走分身（强制规则 R7）", Settings.Current.LaunchBehavior, v => Settings.Current.LaunchBehavior = v),
            MakeCheck("最小化到系统托盘", Settings.Current.MinimizeToTray, v => Settings.Current.MinimizeToTray = v),
            MakeCheck("托盘图标显示分身数量", Settings.Current.TrayShowCount, v => Settings.Current.TrayShowCount = v),
            // F6：桌面快捷方式与任务栏差异化
            MakeCheck("桌面快捷方式使用分身角标图标（图标右下角显示序号）", Settings.Current.BadgeShortcutIcon, v => Settings.Current.BadgeShortcutIcon = v),
            MakeCheck("任务栏分组（AppUserModelID，需深度隔离）", Settings.Current.TaskbarGrouping, v => Settings.Current.TaskbarGrouping = v),
        };
    }

    /// <summary>F5：日志与诊断。级别越低越详细，但流水体积也越大。</summary>
    private Control[] BuildLogging()
    {
        var level = new ComboBox
        {
            Size = new Size(S(200), S(30)), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(Ui.FBody),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Ui.CardOf(),
        };
        level.Items.AddRange(new object[] { "仅错误", "警告及以上", "信息及以上（推荐）", "调试（最详细）" });
        level.SelectedIndex = Math.Clamp(3 - (int)Settings.Current.MinLogLevel, 0, 3);
        level.SelectedIndexChanged += (_, _) =>
        {
            Settings.Current.MinLogLevel = (LogLevel)Math.Clamp(3 - level.SelectedIndex, 0, 3);
            Settings.Current.Save();
        };
        var levelRow = RowWithLabel("日志级别", level);

        var buttons = new Panel { Height = S(32), Width = _content.ClientSize.Width - S(76) };
        var btnOpenLogs = new AppleButton("打开日志目录", AppleButton.Style.Secondary, 9F) { Size = new Size(S(112), S(30)), Location = new Point(0, 0) };
        btnOpenLogs.Click += (_, _) => OpenPath(Paths.LogsDir);
        var btnDiag = new AppleButton("导出诊断包…", AppleButton.Style.Secondary, 9F) { Size = new Size(S(112), S(30)), Location = new Point(S(120), 0) };
        btnDiag.Click += (_, _) => ExportDiagnostics();
        buttons.Controls.Add(btnOpenLogs);
        buttons.Controls.Add(btnDiag);

        return new Control[]
        {
            MakeCheck("写入操作流水日志（data\\logs\\，仅保留 7 天）", Settings.Current.WriteOpsLog, v => Settings.Current.WriteOpsLog = v),
            levelRow,
            buttons,
        };
    }

    /// <summary>导出诊断包：日志 + 配置（不含 profiles / snapshots，那里是用户的真实账号数据）。</summary>
    private void ExportDiagnostics()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出诊断包", Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"AppClone-Diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip", AddExtension = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var (ok, msg) = Log.ExportDiagnostics(dlg.FileName);
        MessageBox.Show(this, msg, ok ? "导出诊断包" : "导出失败", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private Control[] BuildSafety()
    {
        // P1：启动密码锁入口。按钮文案随启用状态变化，保存成功后就地刷新，无需重建整页
        var lockRow = new Panel { Height = S(30), Width = _content.ClientSize.Width - S(76) };
        var lockLbl = Ui.Label(AppLock.Enabled ? "启动密码：已开启" : "启动密码：未开启", Ui.FBody, false, Ui.TextPrimaryOf());
        lockLbl.Location = new Point(0, S(6));
        var btnLock = new AppleButton(AppLock.Enabled ? "修改 / 关闭…" : "设置密码…", AppleButton.Style.Secondary, 9F)
        { Size = new Size(S(112), S(28)), Location = new Point(S(200), 0) };
        btnLock.Click += (_, _) =>
        {
            using var f = new PasswordSetupForm();
            if (f.ShowDialog(this) != DialogResult.OK) return;
            lockLbl.Text = AppLock.Enabled ? "启动密码：已开启" : "启动密码：未开启";
            btnLock.Text = AppLock.Enabled ? "修改 / 关闭…" : "设置密码…";
        };
        lockRow.Controls.Add(lockLbl);
        lockRow.Controls.Add(btnLock);

        return new Control[]
        {
            lockRow,
            MakeCheck("删除 / 重置前确认", Settings.Current.ConfirmDestructive, v => Settings.Current.ConfirmDestructive = v),
            MakeCheck("记住\"不再询问\"", Settings.Current.RememberDontAsk, v => Settings.Current.RememberDontAsk = v),
            MakeCheck("状态栏显示数据占用", Settings.Current.ShowDataUsage, v => Settings.Current.ShowDataUsage = v),
            MakeCheck("启动前显示执行预览（Dry Run）", Settings.Current.ShowDryRun, v => Settings.Current.ShowDryRun = v),
            MakeCheck("启动时检查更新", Settings.Current.UpdateCheck, v => Settings.Current.UpdateCheck = v),
        };
    }

    private Control[] BuildDataDir()
    {
        // 数据目录路径只读展示 + 打开按钮
        var row = new Panel { Height = S(30), Width = _content.ClientSize.Width - S(76) };
        var lbl = Ui.Label("数据目录", Ui.FBody, false, Ui.TextPrimaryOf());
        lbl.Location = new Point(0, S(6));
        var path = new TextBox
        {
            Text = Paths.DataRoot, ReadOnly = true,
            Size = new Size(Math.Max(S(120), row.Width - S(162)), S(26)),
            Location = new Point(S(90), S(2)), Font = Ui.Font(9F),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Ui.CardOf(),
        };
        var btnOpen = new AppleButton("打开", AppleButton.Style.Secondary, 9F)
        { Size = new Size(S(64), S(26)), Location = new Point(row.Width - S(64), S(2)) };
        btnOpen.Click += (_, _) => OpenDataFolder();
        row.Controls.Add(lbl); row.Controls.Add(path); row.Controls.Add(btnOpen);
        // 行随卡片伸缩（AddSection 里锚定 L|R），按钮与文本框的几何用显式重算跟随 —— 锚定差值在行缩放时不可靠
        row.Resize += (_, _) =>
        {
            // 以按钮自身实际宽度为基准（构造期 = S(64)，DPI/仿真缩放后同为放大值），任何缩放口径下都自洽
            btnOpen.Left = row.Width - btnOpen.Width;
            path.Width = Math.Max(S(120), btnOpen.Left - path.Left - S(8));
        };

        // 迁移：高级功能，后续开放（禁用 + 灰显）
        var migrate = new Panel { Height = S(26), Width = _content.ClientSize.Width - S(76) };
        var mLbl = Ui.Label("迁移数据目录到其它磁盘（高级功能 · 后续开放）", Ui.FCaption, false, Ui.TextSecondaryOf());
        mLbl.Location = new Point(0, S(6));
        mLbl.ForeColor = Ui.TextSecondaryOf();
        migrate.Controls.Add(mLbl);

        // P1：整包导出 / 导入（含 profiles 真实分身数据），用于换机或备份
        var pack = new Panel { Height = S(32), Width = _content.ClientSize.Width - S(76) };
        var btnExport = new AppleButton("导出全部数据…", AppleButton.Style.Secondary, 9F)
        { Size = new Size(S(120), S(30)), Location = new Point(0, 0) };
        btnExport.Click += async (_, _) => await ExportPackageAsync();
        var btnImport = new AppleButton("导入数据包…", AppleButton.Style.Secondary, 9F)
        { Size = new Size(S(120), S(30)), Location = new Point(S(128), 0) };
        btnImport.Click += (_, _) => ImportPackage();
        pack.Controls.Add(btnExport);
        pack.Controls.Add(btnImport);

        // F3：预检查的磁盘剩余空间阈值
        var freeGb = new NumericUpDown
        {
            Minimum = 1, Maximum = 100,
            Value = Math.Clamp(Settings.Current.MinFreeDiskGB, 1, 100),
            Size = new Size(S(72), S(26)), Font = Ui.Font(9F),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Ui.CardOf(),
        };
        freeGb.ValueChanged += (_, _) =>
        {
            Settings.Current.MinFreeDiskGB = (int)freeGb.Value;
            Settings.Current.Save();
        };
        var freeRow = RowWithLabel("低磁盘空间提醒阈值（GB）", freeGb);

        return new Control[] { row, freeRow, migrate, pack };
    }

    /// <summary>整包导出：manifest + profiles 真实数据，异步执行避免大目录把界面卡死。</summary>
    private async Task ExportPackageAsync()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出全部数据", Filter = "分身数据包 (*.zip)|*.zip",
            FileName = $"AppClone-Data-{DateTime.Now:yyyyMMdd-HHmm}.zip", AddExtension = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var store = Store.Load();
        UseWaitCursor = true;
        try
        {
            var (apps, clones, bytes) = await Task.Run(() => ConfigIo.ExportPackage(dlg.FileName, store));
            MessageBox.Show(this,
                $"已导出 {apps} 个应用 / {clones} 个分身（约 {bytes / 1048576} MB）\n{dlg.FileName}",
                "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { UseWaitCursor = false; }
    }

    /// <summary>导入：交给 MainForm 用它的 _store 落地（否则两个 Store 实例会互相覆盖）。</summary>
    private void ImportPackage()
    {
        if (_import == null)
        {
            MessageBox.Show(this, "请从主界面打开设置后再导入。", "导入数据包",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Title = "导入数据包", Filter = "分身数据包 (*.zip)|*.zip|配置文件 (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        UseWaitCursor = true;
        try
        {
            var msg = _import(dlg.FileName);
            MessageBox.Show(this, msg, "导入数据包", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { UseWaitCursor = false; }
    }

    private Control[] BuildAbout()
    {
        var row = new Panel { Height = S(64), Width = _content.ClientSize.Width - S(76) };
        var name = Ui.Label("应用分身", Ui.FSub, true, Ui.TextPrimaryOf());
        name.Location = new Point(0, S(4));
        var ver = Ui.Label("版本 1.4.1", Ui.FBody, false, Ui.ThemedColor(ThemeRole.AccentDark));
        ver.Location = new Point(0, name.Bottom + S(4));   // FSub 字号的名称比想象高，固定 y=28 会被压住
        var desc = Ui.Label("电脑版应用分身 / 沙盒多开工具", Ui.FCaption, false, Ui.TextSecondaryOf());
        desc.Location = new Point(Math.Max(S(140), ver.Right + S(12)), ver.Top + S(2));
        row.Controls.Add(name); row.Controls.Add(ver); row.Controls.Add(desc);
        return new Control[] { row };
    }

    // ------------------------------------------------------------------ 辅助

    private CheckBox MakeCheck(string text, bool value, Action<bool> onChanged)
    {
        var c = new CheckBox
        {
            Text = text, AutoSize = true, Font = Ui.Font(Ui.FBody),
            ForeColor = Ui.TextPrimaryOf(), Checked = value,
        };
        c.CheckedChanged += (_, _) => { onChanged(c.Checked); Settings.Current.Save(); };
        return c;
    }

    private Panel RowWithLabel(string label, Control control)
    {
        // 行宽 = 内容区宽 - 内容 Padding(40) - 卡片左右内边距(32)；与 AddSection 的 p.Width = card.Width - S(32) 一致，
        // 保证构建期零缩放（若构建后再缩行，右锚定控件的差值是在旧宽下捕获的，会被拖进标签里）。
        var p = new Panel { Height = S(30), Width = _content.ClientSize.Width - S(76) };
        var lbl = Ui.Label(label, Ui.FBody, false, Ui.TextPrimaryOf());
        lbl.Location = new Point(0, S(6));
        // 不能读 lbl.Width：AutoSize 标签此刻还没量完（拿到的是默认值 100）。
        // 控件一律左锚定、紧跟标签 —— 右锚定会在行变窄时把控件拖到标签上（实测重叠 16~72px）。
        // lblW 由 MeasureText 按 DPI 实测，已是物理像素；偏移量走 S()。
        int lblW = TextRenderer.MeasureText(label, Ui.Font(Ui.FBody)).Width;
        control.Location = new Point(Math.Max(S(150), lblW + S(16)), 0);
        p.Controls.Add(lbl);
        p.Controls.Add(control);
        return p;
    }

    private void ApplyTheme(AppTheme theme)
    {
        Settings.Current.Theme = theme;
        Ui.Theme = theme;                 // 桥接 ThemeManager.Set，广播主题变更
        Settings.Current.Save();
        // 重绘本窗口以即时预览（主界面会在关闭后按设置重建）
        BackColor = Ui.BgOf();
        _header.BackColor = Ui.BgOf();
        _footer.BackColor = Ui.BgOf();
        _content.BackColor = Ui.BgOf();   // C5：BuildChrome 时设置的浅色底需随主题重设，消除深色残底
        _titleLbl.ForeColor = Ui.TextPrimaryOf();
        BuildContent();
    }

    private static void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataRoot}\"") { UseShellExecute = true }); }
        catch { }
    }
}
