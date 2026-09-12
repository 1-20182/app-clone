using System.Diagnostics;
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

    public SettingsForm()
    {
        Settings.Load();
        Ui.Theme = Settings.Current.Theme;

        Text = "设置";
        Size = new Size(580, 720);
        MinimumSize = new Size(480, 560);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = MainForm.TryAppIcon();

        BuildChrome();
        BuildContent();
        Shown += (_, _) => { Ui.RoundWindow(this); FadeAnimator.FadeIn(this); };
    }

    // ------------------------------------------------------------------ 外框

    private void BuildChrome()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Ui.BgOf() };
        header.Paint += (_, e) =>
        {
            using var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, header.Height - 2, header.Width, 2),
                Color.FromArgb(70, Ui.ThemedColor(ThemeRole.Accent)), Color.FromArgb(70, ColorTranslator.FromHtml("#5E5CE6")), 0F);
            e.Graphics.FillRectangle(b, 0, header.Height - 2, header.Width, 2);
        };
        var title = Ui.Label("设置", Ui.FTitle, true, Ui.TextPrimaryOf());
        title.Location = new Point(22, 14);
        header.Controls.Add(title);
        _titleLbl = title;
        Controls.Add(header);
        header.BringToFront();
        _header = header;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Ui.BgOf() };
        var btnClose = new AppleButton("完成", AppleButton.Style.Filled) { Size = new Size(120, 36), Location = new Point(footer.Width - 120 - 20, 12) };
        btnClose.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnClose.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        footer.Controls.Add(btnClose);
        Controls.Add(footer);
        footer.BringToFront();
        _footer = footer;

        _content = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            BackColor = Ui.BgOf(), Padding = new Padding(20, 16, 20, 12),
        };
        Controls.Add(_content);
    }

    // ------------------------------------------------------------------ 内容

    private void BuildContent()
    {
        _content.Controls.Clear();
        int y = 12;
        y = AddSection("外观", y, BuildAppearance());
        y = AddSection("发现 / 扫描源", y, BuildScanSources());
        y = AddSection("批量添加默认", y, BuildBatchDefaults());
        y = AddSection("启动与窗口", y, BuildStartup());
        y = AddSection("交互与安全", y, BuildSafety());
        y = AddSection("数据与目录", y, BuildDataDir());
        y = AddSection("关于", y, BuildAbout());
    }

    private int AddSection(string title, int y, Control[] rows)
    {
        var card = new AppleCard
        {
            Location = new Point(0, y), Width = _content.ClientSize.Width - 4, Radius = 14,
            DrawBorder = true, Shadow = true, BackColor = Ui.CardOf(),
        };
        int cy = 14;
        var cap = Ui.Label(title, Ui.FSection, true, Ui.TextPrimaryOf());
        cap.Location = new Point(16, cy);
        card.Controls.Add(cap);
        cy += 30;
        foreach (var row in rows)
        {
            row.Location = new Point(16, cy);
            card.Controls.Add(row);
            cy += row.Height + 10;
        }
        card.Height = cy + 12;
        _content.Controls.Add(card);
        return y + card.Height + 14;
    }

    private Control[] BuildAppearance()
    {
        var panel = new Panel { Height = 28, Width = _content.ClientSize.Width - 40 };
        var lbl = Ui.Label("主题", Ui.FBody, false, Ui.TextPrimaryOf());
        lbl.Location = new Point(0, 6);
        var rbLight = new RadioButton { Text = "浅色", AutoSize = true, Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextPrimaryOf(), Location = new Point(120, 6) };
        var rbDark = new RadioButton { Text = "深色", AutoSize = true, Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextPrimaryOf(), Location = new Point(200, 6) };
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
            Size = new Size(160, 30), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(Ui.FBody),
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
            Size = new Size(80, 30), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(Ui.FBody),
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
            Size = new Size(160, 30), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(Ui.FBody),
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
        };
    }

    private Control[] BuildSafety()
    {
        return new Control[]
        {
            MakeCheck("删除 / 重置前确认", Settings.Current.ConfirmDestructive, v => Settings.Current.ConfirmDestructive = v),
            MakeCheck("记住\"不再询问\"", Settings.Current.RememberDontAsk, v => Settings.Current.RememberDontAsk = v),
            MakeCheck("状态栏显示数据占用", Settings.Current.ShowDataUsage, v => Settings.Current.ShowDataUsage = v),
            MakeCheck("启动时检查更新", Settings.Current.UpdateCheck, v => Settings.Current.UpdateCheck = v),
        };
    }

    private Control[] BuildDataDir()
    {
        // 数据目录路径只读展示 + 打开按钮
        var row = new Panel { Height = 30, Width = _content.ClientSize.Width - 40 };
        var lbl = Ui.Label("数据目录", Ui.FBody, false, Ui.TextPrimaryOf());
        lbl.Location = new Point(0, 6);
        var path = new TextBox
        {
            Text = Paths.DataRoot, ReadOnly = true,
            Size = new Size(Math.Max(180, row.Width - 220), 26),
            Location = new Point(90, 2), Font = Ui.Font(9F),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Ui.CardOf(),
        };
        var btnOpen = new AppleButton("打开", AppleButton.Style.Secondary, 9F)
        { Size = new Size(64, 26), Location = new Point(row.Width - 64, 2) };
        btnOpen.Click += (_, _) => OpenDataFolder();
        row.Controls.Add(lbl); row.Controls.Add(path); row.Controls.Add(btnOpen);

        // 迁移：高级功能，后续开放（禁用 + 灰显）
        var migrate = new Panel { Height = 26, Width = _content.ClientSize.Width - 40 };
        var mLbl = Ui.Label("迁移数据目录到其它磁盘（高级功能 · 后续开放）", Ui.FCaption, false, Ui.TextSecondaryOf());
        mLbl.Location = new Point(0, 6);
        mLbl.ForeColor = Ui.TextSecondaryOf();
        migrate.Controls.Add(mLbl);

        return new Control[] { row, migrate };
    }

    private Control[] BuildAbout()
    {
        var row = new Panel { Height = 56, Width = _content.ClientSize.Width - 40 };
        var name = Ui.Label("应用分身", Ui.FSub, true, Ui.TextPrimaryOf());
        name.Location = new Point(0, 4);
        var ver = Ui.Label("版本 1.2.0", Ui.FBody, false, Ui.ThemedColor(ThemeRole.AccentDark));
        ver.Location = new Point(0, 28);
        var desc = Ui.Label("电脑版应用分身 / 沙盒多开工具", Ui.FCaption, false, Ui.TextSecondaryOf());
        desc.Location = new Point(140, 28);
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
        var p = new Panel { Height = 30, Width = _content.ClientSize.Width - 40 };
        var lbl = Ui.Label(label, Ui.FBody, false, Ui.TextPrimaryOf());
        lbl.Location = new Point(0, 6);
        control.Location = new Point(Math.Max(150, lbl.Width + 20), 0);
        control.Anchor = AnchorStyles.Top | AnchorStyles.Right;
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
        _titleLbl.ForeColor = Ui.TextPrimaryOf();
        BuildContent();
    }

    private static void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataRoot}\"") { UseShellExecute = true }); }
        catch { }
    }
}
