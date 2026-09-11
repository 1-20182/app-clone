using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO;
using AppClone.Core;
using AppClone.Forms;

namespace AppClone;

/// <summary>
/// 主界面：左侧应用列表（搜索 / 分类筛选 / 批量模式），右侧所选应用的分身卡片。
/// 苹果风格视觉与动画。
/// 增量 1.1.0：下拉添加入口、空态扫描按钮、状态栏、图标懒加载、轮询空闲暂停、注册表快照后台化、
/// 分身改名/设色、结束全部、快照重置、深色主题。
/// 增量 1.2.0（T12–T14）：侧栏搜索与分类筛选、插画化空状态、信息密度状态栏；批量模式（多选 +
/// 批量新建/结束/导出配置）；系统托盘（TrayContext）、开机自启（AutoStart）、全局热键（HotkeyManager）、
/// 运行统计（CloneStats）。
/// </summary>
public class MainForm : Form
{
    private readonly Store _store;
    private readonly TrayContext _tray;
    private readonly HotkeyManager _hotkey;

    // —— 侧栏 ——
    private FlowLayoutPanel _appFlow = null!;
    private TextBox _appSearch = null!;
    private FlowLayoutPanel _appCats = null!;
    private AppleCard _appEmpty = null!;
    private AppleButton _appEmptyBtn = null!;
    private Label _noMatch = null!;

    // —— 右栏 ——
    private Label _rightTitle = null!;
    private Label _rightPath = null!;
    private PictureBox _rightIcon = null!;
    private AppleButton _btnNewClone = null!;
    private AppleButton _btnEndAll = null!;
    private FlowLayoutPanel _cloneFlow = null!;
    private AppleCard _cloneEmpty = null!;
    private AppleCard _rightPlaceholder = null!;

    // —— 状态栏 ——
    private Label _statusText = null!;

    // —— 批量条 ——
    private bool _batchMode;
    private readonly HashSet<string> _appSelected = new();
    private Panel _batchBar = null!;
    private AppleButton _btnBatch = null!;
    private AppleButton _btnBatchNew = null!;
    private AppleButton _btnBatchEnd = null!;
    private AppleButton _btnBatchExport = null!;
    private AppleButton _btnBatchExit = null!;

    // —— 选择 / 过滤 ——
    private string? _selectedAppId;
    private string _appFilterText = "";
    private string _appCategory = "全部";

    // —— 运行态 / 统计 / 托盘 ——
    private readonly List<(string app, string clone, Label dot, Label text, AppleCard card)> _statusLabels = new();
    private readonly List<(string app, Label count)> _appCountLabels = new();
    private readonly List<(string appName, string cloneName, string appId, string cloneId)> _recent = new();
    private readonly HashSet<string> _hotkeyConflicts = new();
    private readonly Dictionary<string, DateTime> _runStart = new();

    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _pulse;
    private readonly System.Windows.Forms.Timer _sizeTimer;
    private float _pulsePhase;
    private bool _reallyClosing;
    private AppTheme _appliedTheme = AppTheme.Light;
    private string _dataSizeText = "—";

    public MainForm()
    {
        _store = Store.Load();
        Settings.Load();
        Ui.Theme = Settings.Current.Theme;        // 按设置应用主题
        _appliedTheme = Settings.Current.Theme;

        Text = "应用分身";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1120, 740);
        MinimumSize = new Size(960, 640);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = TryAppIcon();

        // T13：托盘（Icon 非 null 兜底）
        _tray = new TrayContext(TryAppIcon() ?? SystemIcons.Application);
        _tray.OpenRequested += () => ShowFromTray();
        _tray.NewRequested += () => OpenDiscover();
        _tray.ExitRequested += () => RequestExit();
        _tray.SetRunningCount(0, 0, Settings.Current.TrayShowCount);

        // T14：热键管理器（宿主窗体即本窗体）
        _hotkey = new HotkeyManager(this);
        _hotkey.HotkeyFired += (appId, cloneId) => BeginInvoke(new Action(() => LaunchByHotkey(appId, cloneId)));

        BuildTopBar();
        BuildSidebar();
        BuildRightPane();
        BuildStatusBar();
        BuildBatchBar();          // 停靠底部、默认隐藏；加入顺序在状态栏之后 → 视觉上位于状态栏上方

        SetStatus($"数据目录 {Paths.DataRoot} · 已加载 {_store.Model.Apps.Count} 个应用");

        if (_store.Model.Apps.Count > 0)
            SelectApp(_store.Model.Apps[0].Id);
        else
            RefreshRightPane();
        RefreshAppList();

        // T14：注册已有分身热键（此时 Handle 已就绪）
        RegisterAllHotkeys();

        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += (_, _) => UpdateRunningStates();
        _timer.Start();

        _pulse = new System.Windows.Forms.Timer { Interval = 60 };
        _pulse.Tick += (_, _) => { _pulsePhase += 0.09f; UpdatePulse(); };
        _pulse.Start();

        _sizeTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        _sizeTimer.Tick += (_, _) => RefreshDataSize();
        _sizeTimer.Start();
        RefreshDataSize();

        // T13/T14：启动态变化 → 刷新托盘运行数
        Launcher.StateChanged += UpdateTrayCount;

        // T13：开机自启
        AutoStart.SetEnabled(Settings.Current.LaunchOnStartup);

        Ui.RoundWindow(this);
        FadeAnimator.FadeIn(this);
    }

    public static Icon? TryAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 关闭 / 退出

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_reallyClosing) { base.OnFormClosing(e); return; }
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // T13：最小化到托盘
            bool minimize = Settings.Current.CloseBehavior == CloseBehavior.Minimize
                            && Settings.Current.MinimizeToTray;
            if (minimize)
            {
                e.Cancel = true;
                Hide();
                SetStatus("已最小化到系统托盘");
                return;
            }
            // 真退出：先淡出再清理
            e.Cancel = true;
            FadeAnimator.FadeOutThen(this, () => { _reallyClosing = true; RealExit(); });
            return;
        }
        base.OnFormClosing(e);
    }

    /// <summary>真退出：注销热键、落盘统计、释放托盘，结束进程。</summary>
    private void RealExit()
    {
        try { _hotkey.UnregisterAll(); } catch { }
        try { CloneStats.Save(); } catch { }
        try { _tray.Dispose(); } catch { }
        try { _timer.Stop(); _pulse.Stop(); _sizeTimer.Stop(); } catch { }
        Environment.Exit(0);
    }

    /// <summary>T14：捕获全局热键消息并转发。</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == HotkeyManager.WmHotkey) _hotkey.HandleWmHotkey(m.Msg, m.WParam);
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------------ UI 搭建

    private void BuildTopBar()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Ui.BgOf() };
        top.Paint += (_, e) =>
        {
            using var b = new LinearGradientBrush(
                new Rectangle(0, top.Height - 2, top.Width, 2),
                Color.FromArgb(70, Ui.ThemedColor(ThemeRole.Accent)),
                Color.FromArgb(70, ColorTranslator.FromHtml("#5E5CE6")), 0F);
            e.Graphics.FillRectangle(b, 0, top.Height - 2, top.Width, 2);
        };

        var iconBox = new PictureBox
        {
            Size = new Size(30, 30), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(22, 15), Image = TryAppIcon()?.ToBitmap(),
        };

        var title = new Label
        {
            Text = "应用分身", AutoSize = true, Font = Ui.Font(Ui.FTitle, true),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Location = new Point(60, 13),
        };
        var sub = new Label
        {
            Text = "数据隔离 · 沙盒多开", AutoSize = true, Font = Ui.Font(Ui.FCaption),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent, Location = new Point(62, 40),
        };

        var btnData = new AppleButton("打开数据目录", AppleButton.Style.Secondary, 9F)
        { Size = new Size(112, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnData.Click += (_, _) => OpenFolder(Paths.DataRoot);

        var btnSettings = new AppleButton("设置", AppleButton.Style.Secondary, 9F)
        { Size = new Size(84, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnSettings.Click += (_, _) => OpenSettings();

        var btnBatch = new AppleButton("批量", AppleButton.Style.Ghost, 9F)
        { Size = new Size(80, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnBatch.Click += (_, _) => ToggleBatchMode();
        _btnBatch = btnBatch;

        var btnAdd = new AppleButton("＋ 添加应用 ▾", AppleButton.Style.Filled)
        { Size = new Size(140, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        var addMenu = new ContextMenuStrip { Font = Ui.Font(Ui.FBody) };
        var mDiscover = new ToolStripMenuItem("🗂  从本机已安装应用添加（推荐）") { Font = Ui.Font(Ui.FSub, true), ForeColor = Ui.ThemedColor(ThemeRole.AccentDark) };
        mDiscover.Click += (_, _) => OpenDiscover();
        var mManual = new ToolStripMenuItem("📄  手动选择 exe 文件…");
        mManual.Click += (_, _) => AddAppFlow();
        addMenu.Items.Add(mDiscover);
        addMenu.Items.Add(mManual);
        btnAdd.Click += (_, _) => addMenu.Show(btnAdd, new Point(0, btnAdd.Height)));

        top.Controls.Add(iconBox);
        top.Controls.Add(title);
        top.Controls.Add(sub);
        top.Controls.Add(btnData);
        top.Controls.Add(btnAdd);
        top.Controls.Add(btnSettings);
        top.Controls.Add(btnBatch);

        // 右侧按钮布局：btnBatch(80) | btnSettings(84) | btnAdd(140) | btnData(112)，间距 12，右缩 24
        top.Resize += (_, _) =>
        {
            btnBatch.Location = new Point(top.Width - 80 - 24, 17);
            btnSettings.Location = new Point(top.Width - 80 - 12 - 84 - 24, 17);
            btnAdd.Location = new Point(top.Width - 80 - 12 - 84 - 12 - 140 - 24, 17);
            btnData.Location = new Point(top.Width - 80 - 12 - 84 - 12 - 140 - 12 - 112 - 24, 17);
        };
        top.Controls.Add(new Panel());
        btnBatch.Location = new Point(top.Width - 80 - 24, 17);
        btnSettings.Location = new Point(top.Width - 80 - 12 - 84 - 24, 17);
        btnAdd.Location = new Point(top.Width - 80 - 12 - 84 - 12 - 140 - 24, 17);
        btnData.Location = new Point(top.Width - 80 - 12 - 84 - 12 - 140 - 12 - 112 - 24, 17);

        Controls.Add(top);
        top.BringToFront();
    }

    private void BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 280, BackColor = Ui.BgOf() };

        var cap = Ui.Label("我的应用", Ui.FSection, true, Ui.TextSecondaryOf());
        cap.Location = new Point(20, 16);
        sidebar.Controls.Add(cap);

        // T12：搜索框
        _appSearch = new TextBox
        {
            Size = new Size(sidebar.Width - 40, 30),
            Location = new Point(20, 44),
            Font = Ui.Font(Ui.FBody),
            Text = "搜索应用或发布者…",
            ForeColor = Ui.TextSecondaryOf(),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _appSearch.GotFocus += (_, _) =>
        {
            if (_appSearch.Text == "搜索应用或发布者…") { _appSearch.Text = ""; _appSearch.ForeColor = Ui.TextPrimaryOf(); }
        };
        _appSearch.LostFocus += (_, _) =>
        {
            if (_appSearch.Text.Length == 0) { _appSearch.Text = "搜索应用或发布者…"; _appSearch.ForeColor = Ui.TextSecondaryOf(); }
        };
        _appSearch.TextChanged += (_, _) =>
        {
            if (_appSearch.Text != "搜索应用或发布者…") { _appFilterText = _appSearch.Text.Trim(); RefreshAppList(); }
        };
        sidebar.Controls.Add(_appSearch);

        // T12：分类筛选
        _appCats = new FlowLayoutPanel
        {
            Location = new Point(16, 80),
            Size = new Size(sidebar.Width - 32, 36),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Ui.BgOf(),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        foreach (var c in new[] { "全部", "浏览器", "社交", "游戏", "开发", "云盘", "办公", "其它" })
            _appCats.Controls.Add(MakeAppCatChip(c));
        sidebar.Controls.Add(_appCats);

        _appFlow = new FlowLayoutPanel
        {
            Location = new Point(12, 124),
            Size = new Size(sidebar.Width - 24, sidebar.Height - 136),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.BgOf(), Padding = new Padding(0),
        };
        sidebar.Controls.Add(_appFlow);

        // T12：插画化空状态
        _appEmpty = BuildAppEmptyCard();
        _appEmpty.Location = new Point(16, 140);
        _appEmpty.Size = new Size(sidebar.Width - 32, 220);
        _appEmpty.Visible = false;
        sidebar.Controls.Add(_appEmpty);

        _noMatch = Ui.Label("没有匹配的应用", Ui.FBody, false, Ui.TextSecondaryOf());
        _noMatch.Location = new Point(24, 150);
        _noMatch.Visible = false;
        sidebar.Controls.Add(_noMatch);

        Controls.Add(sidebar);
        sidebar.BringToFront();
    }

    private PillChip MakeAppCatChip(string name)
    {
        bool on = name == _appCategory;
        var chip = new PillChip(name, on ? Ui.AccentLightOf() : Ui.FillGrayOf(), on ? Ui.ThemedColor(ThemeRole.AccentDark) : Ui.TextSecondaryOf())
        { Margin = new Padding(0, 0, 8, 0) };
        chip.Click += (_, _) => { _appCategory = name; RefreshAppChips(); RefreshAppList(); };
        return chip;
    }

    private void RefreshAppChips()
    {
        foreach (Control c in _appCats.Controls)
            if (c is PillChip chip)
            {
                bool on = chip.Text == _appCategory;
                var fresh = new PillChip(chip.Text, on ? Ui.AccentLightOf() : Ui.FillGrayOf(), on ? Ui.ThemedColor(ThemeRole.AccentDark) : Ui.TextSecondaryOf())
                { Margin = chip.Margin, Location = chip.Location };
                var idx = _appCats.Controls.GetChildIndex(chip);
                _appCats.Controls.Remove(chip);
                fresh.Click += (_, _) => { _appCategory = chip.Text; RefreshAppChips(); RefreshAppList(); };
                _appCats.Controls.Add(fresh);
                _appCats.Controls.SetChildIndex(fresh, idx);
            }
    }

    private AppleCard BuildAppEmptyCard()
    {
        var card = new AppleCard { Radius = 14, DrawBorder = true, Shadow = true, BackColor = Ui.CardOf() };
        var icon = new PictureBox
        {
            Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(24, 24), BackColor = Color.Transparent, Image = MakeFallbackIcon(),
        };
        var title = Ui.Label("还没有添加应用", Ui.FCardTitle, true, Ui.TextPrimaryOf());
        title.Location = new Point(96, 26);
        var guide = Ui.Label("点击下方按钮，从本机已安装的应用中\n一键发现并分身，30 秒即可开始多开。",
            Ui.FBody, false, Ui.TextSecondaryOf());
        guide.Location = new Point(96, 50);
        guide.Size = new Size(150, 44);
        _appEmptyBtn = new AppleButton("立即扫描本机应用", AppleButton.Style.Filled, 9.5F)
        { Size = new Size(180, 36), Location = new Point(24, 150) };
        _appEmptyBtn.Click += (_, _) => OpenDiscover();
        card.Controls.Add(icon);
        card.Controls.Add(title);
        card.Controls.Add(guide);
        card.Controls.Add(_appEmptyBtn);
        return card;
    }

    private void BuildRightPane()
    {
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Ui.BgOf(), Padding = new Padding(16, 12, 20, 8) };

        // T12：插画化空状态（未选应用）
        _rightPlaceholder = new AppleCard { Radius = 14, DrawBorder = true, Shadow = true, BackColor = Ui.CardOf(), Size = new Size(360, 180) };
        _rightPlaceholder.Controls.Add(new PictureBox
        {
            Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(40, 40), BackColor = Color.Transparent, Image = MakeFallbackIcon(),
        });
        var phText = Ui.Label("选择左侧应用，管理它的分身", Ui.FCardTitle, true, Ui.TextSecondaryOf());
        phText.Location = new Point(112, 52);
        phText.Size = new Size(200, 60);
        _rightPlaceholder.Controls.Add(phText);
        _rightPlaceholder.Location = new Point(60, 120);

        _rightIcon = new PictureBox { Size = new Size(52, 52), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(12, 10) };
        _rightTitle = new Label { AutoSize = true, Font = Ui.Font(Ui.FTitle, true), ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Location = new Point(78, 8) };
        _rightPath = new Label
        {
            AutoSize = false, Font = Ui.Font(Ui.FCaption), ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent,
            Location = new Point(80, 40), AutoEllipsis = true, Size = new Size(560, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _btnNewClone = new AppleButton("＋ 新建分身", AppleButton.Style.Filled)
        { Size = new Size(112, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _btnNewClone.Click += async (_, _) => await NewCloneFlow();

        _btnEndAll = new AppleButton("结束全部", AppleButton.Style.Secondary, 9F)
        { Size = new Size(96, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _btnEndAll.Click += (_, _) => EndAllClones();

        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Ui.BgOf() };
        header.Controls.Add(_rightIcon);
        header.Controls.Add(_rightTitle);
        header.Controls.Add(_rightPath);
        header.Controls.Add(_btnNewClone);
        header.Controls.Add(_btnEndAll);
        header.Resize += (_, _) =>
        {
            _btnNewClone.Location = new Point(header.Width - 112 - 4, 14);
            _btnEndAll.Location = new Point(header.Width - 112 - 96 - 12, 14);
        };

        _cloneFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Ui.BgOf(), Padding = new Padding(0, 8, 0, 0),
        };
        _cloneFlow.Resize += (_, _) => StretchCloneCards();

        // T12：插画化空状态（无分身）
        _cloneEmpty = new AppleCard { Radius = 14, DrawBorder = true, Shadow = true, BackColor = Ui.CardOf(), Size = new Size(420, 200) };
        _cloneEmpty.Controls.Add(new PictureBox
        {
            Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(40, 30), BackColor = Color.Transparent, Image = MakeFallbackIcon(),
        });
        var ceText = Ui.Label("该应用还没有分身\n\n点击按钮创建第一个分身，开始多开。",
            Ui.FBody, false, Ui.TextSecondaryOf());
        ceText.Location = new Point(112, 34);
        ceText.Size = new Size(260, 60);
        _cloneEmpty.Controls.Add(ceText);
        var ceBtn = new AppleButton("＋ 新建分身", AppleButton.Style.Filled, 9.5F) { Size = new Size(140, 36), Location = new Point(40, 130) };
        ceBtn.Click += async (_, _) => await NewCloneFlow();
        _cloneEmpty.Controls.Add(ceBtn);
        _cloneEmpty.Location = new Point(40, 60);
        _cloneEmpty.Visible = false;

        right.Controls.Add(_cloneFlow);
        right.Controls.Add(header);
        right.Controls.Add(_rightPlaceholder);
        right.Controls.Add(_cloneEmpty);
        _rightPlaceholder.BringToFront();

        Controls.Add(right);
        right.BringToFront();
    }

    private void BuildStatusBar()
    {
        var status = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Ui.BgOf() };
        status.Paint += (_, e) =>
        {
            using var p = new Pen(Ui.BorderOf(), 1F);
            e.Graphics.DrawLine(p, 0, 0, status.Width, 0);
        };
        _statusText = new Label
        {
            Text = "就绪", Font = Ui.Font(Ui.FCaption), ForeColor = Ui.TextSecondaryOf(),
            BackColor = Color.Transparent, AutoSize = true, Location = new Point(24, 9),
        };
        status.Controls.Add(_statusText);
        Controls.Add(status);
        status.BringToFront();
    }

    private void BuildBatchBar()
    {
        _batchBar = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Ui.BgOf(), Visible = false };
        _batchBar.Paint += (_, e) =>
        {
            using var p = new Pen(Ui.BorderOf(), 1F);
            e.Graphics.DrawLine(p, 0, 0, _batchBar.Width, 0);
        };
        _btnBatchNew = new AppleButton("批量新建分身", AppleButton.Style.Filled, 9F) { Size = new Size(132, 34), Location = new Point(20, 9) };
        _btnBatchNew.Click += (_, _) => BatchNewClones();
        _btnBatchEnd = new AppleButton("批量结束运行", AppleButton.Style.Secondary, 9F) { Size = new Size(120, 34), Location = new Point(164, 9) };
        _btnBatchEnd.Click += (_, _) => BatchEndRunning();
        _btnBatchExport = new AppleButton("批量导出配置", AppleButton.Style.Secondary, 9F) { Size = new Size(120, 34), Location = new Point(296, 9) };
        _btnBatchExport.Click += (_, _) => BatchExportConfig();
        _btnBatchExit = new AppleButton("退出批量", AppleButton.Style.Ghost, 9F) { Size = new Size(84, 34), Location = new Point(_batchBar.Width - 84 - 20, 9) };
        _btnBatchExit.Click += (_, _) => ToggleBatchMode();
        _batchBar.Controls.Add(_btnBatchNew);
        _batchBar.Controls.Add(_btnBatchEnd);
        _batchBar.Controls.Add(_btnBatchExport);
        _batchBar.Controls.Add(_btnBatchExit);
        Controls.Add(_batchBar);
        _batchBar.BringToFront();
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusText.Parent?.Invalidate();
    }

    // ------------------------------------------------------------------ 数据流

    private AppEntry? SelectedApp() =>
        _selectedAppId == null ? null : _store.FindApp(_selectedAppId);

    private void OpenDiscover()
    {
        using var f = new DiscoverForm(_store);
        if (f.ShowDialog(this) == DialogResult.OK)
            SetStatus("已添加新应用，可在左侧管理");
        RefreshAppList();
        if (_selectedAppId == null && _store.Model.Apps.Count > 0)
            SelectApp(_store.Model.Apps[0].Id);
        else
            RefreshRightPane();
    }

    private void OpenSettings()
    {
        var prev = _appliedTheme;
        using var sf = new SettingsForm();
        sf.ShowDialog(this);   // 设置窗口编辑即持久化（Settings.Save）
        RefreshAppList();
        if (_selectedAppId != null) RefreshRightPane();
        if (Settings.Current.Theme != prev)
            BeginInvoke(new Action(ApplyTheme));   // 延后到当前消息循环结束后重建，避免释放正在处理事件的控件
    }

    /// <summary>T18：主题变更后重新应用 Ui.Theme 并重建主界面布局（全树重绘）。</summary>
    private void ApplyTheme()
    {
        _appliedTheme = Settings.Current.Theme;
        Ui.Theme = Settings.Current.Theme;
        RebuildLayout();
        SetStatus($"主题：{(Settings.Current.Theme == AppTheme.Dark ? "深色" : "浅色")}");
    }

    private void RebuildLayout()
    {
        while (Controls.Count > 0) { var c = Controls[0]; Controls.RemoveAt(0); c.Dispose(); }
        BuildTopBar();
        BuildSidebar();
        BuildRightPane();
        BuildStatusBar();
        BuildBatchBar();
        if (_selectedAppId != null) SelectApp(_selectedAppId); else RefreshRightPane();
        RefreshAppList();
    }

    private async void AddAppFlow()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要分身的应用", Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var app = _store.AddApp(dlg.FileName);
        SetStatus($"已添加 {app.Name}，正在提取图标…");
        await Task.Run(() => AppIcons.ExtractWithRetry(app.Id, app.ExePath));
        _selectedAppId = app.Id;
        RefreshAppList();
        RefreshRightPane();
        SetStatus($"已添加 {app.Name}");
    }

    private async Task NewCloneFlow()
    {
        var app = SelectedApp();
        if (app == null) return;

        // T11：注册表快照首次在后台线程准备，UI 显示「准备隔离环境…」；RegClone 已幂等（已就绪跳过）
        SetStatus("准备隔离环境…");
        var clone = _store.AddClone(app.Id);

        if (clone.DeepIsolation)
        {
            var ok = await Task.Run(() => RegClone.EnsureCloneRegistry(app.Id, clone.Id, out _));
            if (!ok) SetStatus("注册表快照初始化失败，首次启动时会自动重试");
        }
        if (clone.DeviceClone)
            await Task.Run(() => RegClone.EnsureDeviceFake(app.Id, clone.Id));

        RefreshRightPane();
        SetStatus($"已创建 {clone.Name}，可以启动了");
    }

    // ------------------------------------------------------------------ 列表渲染（T12 过滤）

    private void RefreshAppList()
    {
        string q = _appSearch.Text == "搜索应用或发布者…" ? "" : _appSearch.Text.Trim();
        bool empty = _store.Model.Apps.Count == 0;
        _appEmpty.Visible = empty;
        _noMatch.Visible = false;

        _appFlow.SuspendLayout();
        _appFlow.Controls.Clear();
        _appCountLabels.Clear();
        foreach (var app in _store.Model.Apps)
        {
            if (!AppMatches(app, q, _appCategory)) continue;
            _appFlow.Controls.Add(MakeAppCard(app));
        }
        _appFlow.ResumeLayout();

        bool anyMatch = _appFlow.Controls.Count > 0;
        _noMatch.Visible = !empty && !anyMatch;
        if (empty) _appEmpty.BringToFront();
    }

    private bool AppMatches(AppEntry a, string q, string cat)
    {
        if (cat != "全部")
        {
            var map = new Dictionary<string, string>
            {
                ["浏览器"] = AppClassifier.CategoryConstants.Browser,
                ["社交"] = AppClassifier.CategoryConstants.Social,
                ["游戏"] = AppClassifier.CategoryConstants.Game,
                ["开发"] = AppClassifier.CategoryConstants.Dev,
                ["云盘"] = AppClassifier.CategoryConstants.Cloud,
                ["办公"] = AppClassifier.CategoryConstants.Office,
            };
            if (map.TryGetValue(cat, out var c))
            {
                if (a.Category != c) return false;
            }
            else if (cat == "其它")
            {
                if (!(a.Category == AppClassifier.CategoryConstants.Other
                      || a.Category == AppClassifier.CategoryConstants.Uwp
                      || string.IsNullOrEmpty(a.Category))) return false;
            }
        }
        if (q.Length > 0)
        {
            if (!(a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                  || a.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    private Panel MakeAppCard(AppEntry app)
    {
        bool selected = app.Id == _selectedAppId;
        var card = new AppleCard
        {
            Width = 248, Height = 66, Margin = new Padding(4, 4, 4, 4),
            Cursor = Cursors.Hand, Radius = 14, BackColor = Ui.CardOf(), DrawBorder = true,
        };
        card.BackColorOverride = () => app.Id == _selectedAppId ? Ui.AccentLightOf() : Ui.CardOf();
        if (selected) card.DisableHoverAnimation();

        CheckBox? chk = null;
        int iconX = 13;
        if (_batchMode)
        {
            chk = new CheckBox { Size = new Size(18, 18), Location = new Point(12, 23), Checked = _appSelected.Contains(app.Id) };
            chk.CheckedChanged += (_, _) =>
            {
                if (chk.Checked) _appSelected.Add(app.Id); else _appSelected.Remove(app.Id);
                UpdateBatchBar();
            };
            card.Controls.Add(chk);
            iconX = 36;
        }

        var icon = new PictureBox
        {
            Size = new Size(40, 40), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(iconX, 13), BackColor = Color.Transparent, Image = AppIcons.Load(app.Id),
        };
        // T09：图标缺失时后台补提取，仅更新该卡片（不整表重建）
        if (icon.Image == null && File.Exists(app.ExePath))
        {
            var appId = app.Id;
            var pb = icon;
            AppIcons.LoadAsync(appId, app.ExePath, 48, img => BeginInvoke(new Action(() =>
            {
                if (!IsDisposed && pb.IsHandleCreated) { pb.Image?.Dispose(); pb.Image = img; }
            })));
        }
        var name = new Label
        {
            Text = app.Name, AutoSize = true, Font = Ui.Font(Ui.FCardTitle, true),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Location = new Point(iconX + 51, 13),
        };
        var count = new Label
        {
            Text = CountText(app), AutoSize = true, Font = Ui.Font(Ui.FCaption),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent, Location = new Point(iconX + 53, 38),
        };
        count.Paint += (_, e) =>
        {
            int running = _store.ClonesOf(app.Id).Count(c => Launcher.IsRunning(app.Id, c.Id));
            if (running > 0)
                using (var b = new SolidBrush(Ui.OkOf()))
                    e.Graphics.DrawString($" · {running} 运行中", count.Font, b, count.Width + 2, 0);
        };
        _appCountLabels.Add((app.Id, count));

        card.Controls.Add(icon);
        card.Controls.Add(name);
        card.Controls.Add(count);

        void Select(object? s, EventArgs e)
        {
            if (_batchMode)
            {
                if (chk != null) chk.Checked = !chk.Checked;   // 批量模式：点击切换勾选
                return;
            }
            SelectApp(app.Id);
        }
        card.Click += Select;
        icon.Click += Select;
        name.Click += Select;
        count.Click += Select;

        var menu = new ContextMenuStrip { Font = Ui.Font(Ui.FBody) };
        menu.Items.Add("打开程序所在目录", null, (_, _) => OpenFolder(Path.GetDirectoryName(app.ExePath) ?? ""));
        var remove = new ToolStripMenuItem("移除应用（连同全部分身数据）") { ForeColor = Ui.DangerOf() };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"确定移除「{app.Name}」吗？\n该应用的全部分身数据与登录状态将被永久删除。",
                    "移除应用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _store.RemoveApp(app.Id);
            if (_selectedAppId == app.Id) _selectedAppId = null;
            RefreshAppList();
            RefreshRightPane();
            SetStatus($"已移除 {app.Name}");
        };
        menu.Items.Add(remove);
        card.ContextMenuStrip = menu;

        return card;
    }

    private void SelectApp(string appId)
    {
        _selectedAppId = appId;
        RefreshAppList();
        RefreshRightPane();
    }

    private void RefreshRightPane()
    {
        var app = SelectedApp();
        _rightPlaceholder.Visible = app == null;
        bool has = app != null;

        _rightIcon.Visible = has;
        _rightTitle.Visible = has;
        _rightPath.Visible = has;
        _btnNewClone.Visible = has;
        _btnEndAll.Visible = has;
        _cloneFlow.Visible = has;
        _cloneEmpty.Visible = false;

        if (app == null)
        {
            _rightTitle.Text = ""; _rightPath.Text = ""; _rightIcon.Image = null;
            _cloneFlow.Controls.Clear();
            return;
        }

        _rightIcon.Image?.Dispose();
        _rightIcon.Image = AppIcons.Load(app.Id) ?? MakeFallbackIcon();
        _rightTitle.Text = app.Name;
        _rightPath.Text = app.ExePath;

        RebuildCloneCards(app);
    }

    private static Image? _fallback;
    private static Image MakeFallbackIcon()
    {
        if (_fallback != null) return _fallback;
        var bmp = new Bitmap(48, 48);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Color.FromArgb(209, 213, 219));
        g.FillEllipse(b, 8, 8, 32, 32);
        _fallback = bmp;
        return bmp;
    }

    private void RebuildCloneCards(AppEntry app)
    {
        _cloneFlow.SuspendLayout();
        _cloneFlow.Controls.Clear();
        _statusLabels.Clear();

        var clones = _store.ClonesOf(app.Id);
        for (int i = 0; i < clones.Count; i++)
            _cloneFlow.Controls.Add(MakeCloneCard(app, clones[i], i + 1));

        StretchCloneCards();
        _cloneFlow.ResumeLayout();
        _cloneEmpty.Visible = clones.Count == 0;
    }

    private void StretchCloneCards()
    {
        int w = Math.Max(420, _cloneFlow.ClientSize.Width - 16);
        foreach (Control c in _cloneFlow.Controls) c.Width = w;
    }

    private Panel MakeCloneCard(AppEntry app, CloneEntry clone, int index)
    {
        bool running = Launcher.IsRunning(app.Id, clone.Id);
        var card = new AppleCard
        {
            Width = 620, Height = 92, Margin = new Padding(2, 7, 2, 7), Radius = 14,
            BackColor = Ui.CardOf(), DrawBorder = true, Shadow = true, AccentBar = running ? Ui.OkOf() : null,
        };

        // 分身头像：渐变圆 + 序号；非运行时用 CloneColors 角标色
        var avatar = new Label
        {
            Size = new Size(38, 38), Text = index.ToString(), TextAlign = ContentAlignment.MiddleCenter,
            Font = Ui.Font(12F, true), ForeColor = Color.White, BackColor = Color.Transparent, Location = new Point(20, 27),
        };
        avatar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var gPath = Ui.Round(new Rectangle(0, 0, 38, 38), 13);
            if (running)
            {
                using var b = new LinearGradientBrush(avatar.ClientRectangle, Ui.ThemedColor(ThemeRole.Accent), ColorTranslator.FromHtml("#5E5CE6"), 60F);
                e.Graphics.FillPath(b, gPath);
            }
            else
            {
                var col = CloneColors.Resolve(clone.Color);
                using var b = new LinearGradientBrush(avatar.ClientRectangle, col, Ui.Lerp(col, Color.White, 0.35f), 60F);
                e.Graphics.FillPath(b, gPath);
            }
        };
        avatar.Resize += (_, _) => avatar.Invalidate();
        if (running) avatar.Invalidate();

        var name = new Label
        {
            Text = clone.Name, AutoSize = true, Font = Ui.Font(Ui.FCardTitle, true),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Location = new Point(72, 16),
        };

        var dot = new Label
        {
            Text = "●", AutoSize = true, Font = new Font("Segoe UI", 10F),
            ForeColor = running ? Ui.OkOf() : Color.FromArgb(210, 210, 215), Location = new Point(74, 50), BackColor = Color.Transparent, Tag = running,
        };
        var state = new Label
        {
            Text = running ? "运行中" : "未运行", AutoSize = true, Font = Ui.Font(Ui.FCaption),
            ForeColor = running ? Ui.OkOf() : Ui.TextSecondaryOf(), Location = new Point(90, 51), BackColor = Color.Transparent,
        };

        int chipX = 150;
        card.Controls.Add(new PillChip(clone.DeepIsolation ? "深度隔离" : "标准隔离",
            clone.DeepIsolation ? Ui.AccentLightOf() : Color.FromArgb(243, 244, 246),
            clone.DeepIsolation ? Ui.ThemedColor(ThemeRole.Accent) : Ui.TextSecondaryOf())
        { Location = new Point(chipX, 49) });
        if (clone.DeviceClone)
            card.Controls.Add(new PillChip("设备分身", Ui.ChipPurpleOf(), Ui.ChipPurpleTextOf())
            { Location = new Point(chipX + 76, 49) });

        // T14：热键冲突标红
        int confX = chipX + (clone.DeviceClone ? 158 : 84);
        if (_hotkeyConflicts.Contains(clone.Id))
            card.Controls.Add(new PillChip("热键冲突", Ui.DangerOf(), Ui.DangerOf(), true) { Location = new Point(confX, 49) });

        card.Controls.Add(new Label
        {
            Text = $"创建于 {clone.CreatedAt:yyyy-MM-dd}", AutoSize = true, Font = Ui.Font(Ui.FChip),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent, Location = new Point(confX + (_hotkeyConflicts.Contains(clone.Id) ? 82 : 0), 51),
        });

        var btnLaunch = new AppleButton(running ? "再次启动" : "启 动", AppleButton.Style.Filled) { Size = new Size(96, 36) };
        btnLaunch.Click += (_, _) => LaunchClone(app, clone);

        var btnMore = new AppleButton("管理  ▾", AppleButton.Style.Secondary, 9F) { Size = new Size(84, 36) };
        btnMore.Click += (_, _) => ShowCloneMenu(btnMore, app, clone);

        card.Controls.Add(avatar);
        card.Controls.Add(name);
        card.Controls.Add(dot);
        card.Controls.Add(state);
        card.Controls.Add(btnLaunch);
        card.Controls.Add(btnMore);
        card.Resize += (_, _) =>
        {
            btnLaunch.Location = new Point(card.Width - 96 - 84 - 12 - 20, 28);
            btnMore.Location = new Point(card.Width - 84 - 16 - 8, 28);
        };

        _statusLabels.Add((app.Id, clone.Id, dot, state, card));
        return card;
    }

    private void UpdateRunningStates()
    {
        int running = 0;
        foreach (var (appId, cloneId, _, text, _) in _statusLabels)
        {
            bool r = Launcher.IsRunning(appId, cloneId);
            if (r) running++;
            text.Text = r ? "运行中" : "未运行";
            text.ForeColor = r ? Ui.OkOf() : Ui.TextSecondaryOf();
            text.Tag = r;
        }
        foreach (var (_, count) in _appCountLabels) count.Invalidate();
        UpdatePulse();
        UpdateStatusBar();
        UpdateTrayCount();

        // T10：无运行分身时降频定时器 + 停止呼吸动画；有运行时恢复
        if (running == 0)
        {
            if (_timer.Interval != 5000) _timer.Interval = 5000;
            if (_pulse.Enabled) _pulse.Stop();
        }
        else
        {
            if (_timer.Interval != 1500) _timer.Interval = 1500;
            if (!_pulse.Enabled) _pulse.Start();
        }
    }

    private void UpdateStatusBar()
    {
        int running = 0, total = 0;
        foreach (var app in _store.Model.Apps)
            foreach (var c in _store.ClonesOf(app.Id))
            {
                total++;
                if (Launcher.IsRunning(app.Id, c.Id)) running++;
            }
        string s = $"● {running} 运行中 · 共 {total} 个分身";
        if (Settings.Current.ShowDataUsage) s += $" · 数据目录 {_dataSizeText}";
        _statusText.Text = s;
        _statusText.Parent?.Invalidate();
    }

    /// <summary>后台递归计算数据目录占用，结果缓存到 _dataSizeText 并刷新状态栏。</summary>
    private void RefreshDataSize()
    {
        Task.Run(() =>
        {
            long bytes = 0;
            try
            {
                var di = new DirectoryInfo(Paths.DataRoot);
                if (di.Exists)
                    foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories))
                        bytes += f.Length;
            }
            catch { }
            _dataSizeText = FormatSize(bytes);
            BeginInvoke(new Action(UpdateStatusBar));
        });
    }

    private static string FormatSize(long bytes)
    {
        double v = bytes;
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:F2} {u[i]}";
    }

    private void UpdatePulse()
    {
        float k = 0.5f + 0.5f * MathF.Sin(_pulsePhase);
        foreach (var (_, _, dot, text, card) in _statusLabels)
        {
            if (dot.Tag is true)
            {
                var baseColor = Ui.OkOf();
                var lit = Color.FromArgb(120, 205, 130);
                dot.ForeColor = Ui.Lerp(baseColor, lit, k * 0.55f);
                card.AccentBar = Ui.Lerp(baseColor, lit, k * 0.45f);
                card.Invalidate();
            }
            else if (card.AccentBar != null)
            {
                card.AccentBar = null;
                card.Invalidate();
            }
        }
    }

    // ------------------------------------------------------------------ 动作

    private void LaunchClone(AppEntry app, CloneEntry clone)
    {
        SetStatus($"正在启动 {app.Name} · {clone.Name} …");
        var outcome = Launcher.Launch(_store, app, clone);
        if (!outcome.Ok)
        {
            MessageBox.Show(this, outcome.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("启动失败");
            return;
        }
        // T14：记录启动统计
        CloneStats.RecordLaunch(app.Id, clone.Id);
        _runStart[$"{app.Id}|{clone.Id}"] = DateTime.Now;
        PushRecent(app, clone);
        RebuildTrayMenu();
        UpdateTrayCount();
        SetStatus(string.IsNullOrEmpty(outcome.Message)
            ? $"已启动 {app.Name} · {clone.Name}"
            : $"{app.Name} · {clone.Name}：{outcome.Message}");
        RefreshRightPane();
    }

    /// <summary>T14：由热键触发启动。</summary>
    private void LaunchByHotkey(string appId, string cloneId)
    {
        var app = _store.FindApp(appId);
        var clone = _store.FindClone(appId, cloneId);
        if (app == null || clone == null) return;
        if (Launcher.IsRunning(appId, cloneId)) { SetStatus($"{app.Name} · {clone.Name} 已在运行"); return; }
        LaunchClone(app, clone);
    }

    /// <summary>停止分身并记录统计（时长）。</summary>
    private void StopClone(AppEntry app, CloneEntry clone)
    {
        if (!Launcher.IsRunning(app.Id, clone.Id)) return;
        string key = $"{app.Id}|{clone.Id}";
        Launcher.Stop(app.Id, clone.Id);
        if (_runStart.TryGetValue(key, out var t))
        {
            CloneStats.RecordStop(app.Id, clone.Id, DateTime.Now - t);
            _runStart.Remove(key);
        }
        else
        {
            CloneStats.RecordStop(app.Id, clone.Id, TimeSpan.Zero);
        }
        UpdateTrayCount();
    }

    private void EndAllClones()
    {
        int n = 0;
        foreach (var app in _store.Model.Apps)
            foreach (var c in _store.ClonesOf(app.Id))
                if (Launcher.IsRunning(app.Id, c.Id)) n++;
        if (n == 0) { SetStatus("当前没有运行中的分身"); return; }
        if (Settings.Current.ConfirmDestructive
            && MessageBox.Show(this, $"确定结束全部 {n} 个运行中的分身吗？", "结束全部",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var app in _store.Model.Apps)
            foreach (var c in _store.ClonesOf(app.Id))
                if (Launcher.IsRunning(app.Id, c.Id)) StopClone(app, c);
        RefreshRightPane();
        SetStatus("已结束全部运行分身");
    }

    // ------------------------------------------------------------------ 批量模式（T13）

    private void ToggleBatchMode()
    {
        _batchMode = !_batchMode;
        if (!_batchMode) _appSelected.Clear();
        _batchBar.Visible = _batchMode;
        _btnBatch.Text = _batchMode ? "退出批量" : "批量";
        RefreshAppList();
        UpdateBatchBar();
        SetStatus(_batchMode ? "已开启批量模式，勾选应用后可批量操作" : "已退出批量模式");
    }

    private void UpdateBatchBar()
    {
        int n = _appSelected.Count;
        _btnBatchNew.Text = $"批量新建分身（{n}）";
        _btnBatchNew.Enabled = n > 0;
        _btnBatchEnd.Enabled = n > 0;
        _btnBatchExport.Enabled = n > 0;
    }

    private void BatchNewClones()
    {
        if (_appSelected.Count == 0) return;
        int n = Settings.Current.DefaultCloneCount;
        int total = 0;
        foreach (var id in _appSelected.ToArray())
            total += _store.BatchAddClones(id, n).Count;
        SetStatus($"已为 {_appSelected.Count} 个应用批量新建 {total} 个分身");
        RefreshRightPane();
    }

    private void BatchEndRunning()
    {
        int ended = 0;
        foreach (var id in _appSelected.ToArray())
        {
            var app = _store.FindApp(id);
            if (app == null) continue;
            foreach (var c in _store.ClonesOf(id))
                if (Launcher.IsRunning(id, c.Id)) { StopClone(app, c); ended++; }
        }
        SetStatus($"已结束 {ended} 个运行中的分身");
        RefreshRightPane();
    }

    private void BatchExportConfig()
    {
        if (_appSelected.Count == 0) return;
        using var dlg = new SaveFileDialog
        {
            Title = "导出选中应用配置", Filter = "JSON 文件 (*.json)|*.json",
            FileName = "appclone-config.json", AddExtension = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        ConfigIo.ExportAll(dlg.FileName, _store);
        SetStatus($"已导出配置到 {Path.GetFileName(dlg.FileName)}");
    }

    // ------------------------------------------------------------------ 托盘 / 热键（T13/T14）

    private void ShowFromTray()
    {
        if (InvokeRequired) { BeginInvoke(new Action(ShowFromTray)); return; }
        if (!Visible) Visible = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void RequestExit()
    {
        if (InvokeRequired) { BeginInvoke(new Action(RequestExit)); return; }
        _reallyClosing = true;
        RealExit();
    }

    private void PushRecent(AppEntry app, CloneEntry clone)
    {
        _recent.RemoveAll(r => r.appId == app.Id && r.cloneId == clone.Id);
        _recent.Insert(0, (app.Name, clone.Name, app.Id, clone.Id));
        while (_recent.Count > 5) _recent.RemoveAt(_recent.Count - 1);
    }

    private void RebuildTrayMenu() => _tray.BuildMenu(_recent);

    private void UpdateTrayCount()
    {
        int running = 0, total = 0;
        foreach (var app in _store.Model.Apps)
            foreach (var c in _store.ClonesOf(app.Id))
            {
                total++;
                if (Launcher.IsRunning(app.Id, c.Id)) running++;
            }
        _tray.SetRunningCount(running, total, Settings.Current.TrayShowCount);
    }

    private void RegisterAllHotkeys()
    {
        _hotkeyConflicts.Clear();
        foreach (var app in _store.Model.Apps)
            foreach (var c in _store.ClonesOf(app.Id))
                if (!string.IsNullOrWhiteSpace(c.Hotkey))
                    if (!_hotkey.Register(c.Hotkey, app.Id, c.Id)) _hotkeyConflicts.Add(c.Id);
    }

    // ------------------------------------------------------------------ 分身菜单

    private void ShowCloneMenu(Control anchor, AppEntry app, CloneEntry clone)
    {
        var menu = new ContextMenuStrip { Font = Ui.Font(Ui.FBody) };

        menu.Items.Add("启动分身", null, (_, _) => LaunchClone(app, clone));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) =>
        {
            Directory.CreateDirectory(Paths.CloneDir(app.Id, clone.Id));
            OpenFolder(Paths.CloneDir(app.Id, clone.Id));
        });
        menu.Items.Add("创建桌面快捷方式", null, (_, _) => CreateShortcut(app, clone));

        // R8：重命名分身
        var rename = new ToolStripMenuItem("重命名分身");
        rename.Click += (_, _) =>
        {
            var n = PromptText("重命名分身", "输入分身名称：", clone.Name);
            if (!string.IsNullOrWhiteSpace(n))
            {
                clone.Name = n; _store.Save(); RefreshRightPane();
                SetStatus($"已重命名为「{n}」");
            }
        };
        menu.Items.Add(rename);

        // R8：设置角标颜色（6 色）
        var colorMenu = new ToolStripMenuItem("设置角标颜色");
        foreach (var hex in CloneColors.All)
        {
            var item = new ToolStripMenuItem(ColorName(hex)) { Tag = hex };
            item.Click += (_, _) => { clone.Color = (string)item.Tag!; _store.Save(); RefreshRightPane(); };
            colorMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(colorMenu);

        // T14：绑定启动热键
        var hotkeyItem = new ToolStripMenuItem(
            clone.Hotkey.Length > 0 ? $"启动热键：{clone.Hotkey}（点击修改）" : "设置启动热键…");
        hotkeyItem.Click += (_, _) => BindHotkey(app, clone);
        menu.Items.Add(hotkeyItem);

        var deep = new ToolStripMenuItem("深度隔离（注册表 + 多开解锁）")
        { Checked = clone.DeepIsolation, ToolTipText = "关闭后仅使用环境变量隔离数据" };
        deep.Click += (_, _) => { clone.DeepIsolation = !clone.DeepIsolation; _store.Save(); RefreshRightPane(); };
        menu.Items.Add(deep);

        var dev = new ToolStripMenuItem("设备分身（随机设备名 / 设备ID）")
        { Checked = clone.DeviceClone, ToolTipText = "为该分身生成独立的设备指纹，绕过“同一设备”限制" };
        dev.Click += (_, _) =>
        {
            clone.DeviceClone = !clone.DeviceClone; _store.Save(); RefreshRightPane();
            SetStatus(clone.DeviceClone ? "已开启设备分身，下次启动生效" : "已关闭设备分身，下次启动生效");
        };
        menu.Items.Add(dev);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("结束进程", null, (_, _) => { StopClone(app, clone); SetStatus("已请求结束分身进程"); });

        // R10：重置分身数据（先存快照）
        var resetSnap = menu.Items.Add("重置分身数据（先存快照）");
        resetSnap.Click += (_, _) => ResetWithSnapshot(app, clone);

        // R10：管理快照
        var manage = menu.Items.Add("管理快照…");
        manage.Click += (_, _) =>
        {
            using var sf = new SnapshotsForm(app, clone);
            sf.ShowDialog(this);
            RefreshRightPane();
        };

        var del = new ToolStripMenuItem("删除分身") { ForeColor = Ui.DangerOf() };
        del.Click += (_, _) =>
        {
            if (Settings.Current.ConfirmDestructive
                && MessageBox.Show(this, $"确定删除「{clone.Name}」吗？\n该分身的全部数据将被永久删除。",
                    "删除分身", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            StopClone(app, clone);
            _store.RemoveClone(app.Id, clone.Id);
            RefreshAppList(); RefreshRightPane();
            SetStatus("分身已删除");
        };
        menu.Items.Add(del);

        menu.Show(anchor, new Point(0, anchor.Height));
    }

    /// <summary>T14：绑定 / 取消启动热键。冲突静默标红 + 状态提示，不弹窗打断。</summary>
    private void BindHotkey(AppEntry app, CloneEntry clone)
    {
        var combo = PromptText("设置启动热键", "输入组合键（如 Ctrl+Alt+1），留空则取消：", clone.Hotkey);
        if (combo == null) return;
        combo = combo.Trim();

        // 重新注册：先全注销，再按当前设置重注册全部
        _hotkey.UnregisterAll();
        _hotkeyConflicts.Remove(clone.Id);
        clone.Hotkey = combo;
        _store.Save();
        RegisterAllHotkeys();

        if (combo.Length > 0 && _hotkeyConflicts.Contains(clone.Id))
            SetStatus($"「{combo}」已被占用，热键未生效");
        else
            SetStatus(combo.Length > 0 ? $"已为 {clone.Name} 绑定热键 {combo}" : "已取消启动热键");
        RefreshRightPane();
    }

    private void ResetWithSnapshot(AppEntry app, CloneEntry clone)
    {
        if (Settings.Current.ConfirmDestructive
            && MessageBox.Show(this, $"确定重置「{clone.Name}」吗？\n将先自动保存一份快照，再清空分身数据。",
                "重置分身（先存快照）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        SetStatus("正在保存快照…");
        StopClone(app, clone);
        var (ok, msg, _) = Snapshot.Create(app.Id, clone.Id);
        if (ok) SetStatus(msg);
        else MessageBox.Show(this, $"快照失败：{msg}\n仍将继续重置。", "快照", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try { Directory.Delete(Paths.CloneDir(app.Id, clone.Id), true); } catch { }
        Directory.CreateDirectory(Paths.CloneHome(app.Id, clone.Id));
        if (clone.DeepIsolation) RegClone.ResetCloneRegistry(app.Id, clone.Id);
        RefreshRightPane();
        SetStatus("分身数据已重置（已存快照）");
    }

    private void CreateShortcut(AppEntry app, CloneEntry clone)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnk = Path.Combine(desktop, $"{app.Name} - {clone.Name}.lnk");
            string self = Application.ExecutablePath;
            new AppClone.Shared.NativeLink
            {
                Target = self, Arguments = $"--launch {app.Id} {clone.Id}",
                WorkingDirectory = Path.GetDirectoryName(self), IconPath = app.ExePath,
                Description = $"启动 {app.Name} · {clone.Name}",
            }.Save(lnk);
            SetStatus($"已在桌面创建快捷方式：{app.Name} - {clone.Name}");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "创建快捷方式失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ------------------------------------------------------------------ 通用弹窗 / 工具

    private string? PromptText(string title, string prompt, string current)
    {
        var dlg = new Form
        {
            Width = 380, Height = 180, Text = title, StartPosition = FormStartPosition.CenterParent,
            BackColor = Ui.BgOf(), Font = Ui.Font(Ui.FBody),
        };
        var lbl = Ui.Label(prompt, Ui.FBody, false, Ui.TextSecondaryOf());
        lbl.Location = new Point(20, 18);
        lbl.Size = new Size(320, 40);
        var tb = new TextBox { Text = current, Location = new Point(20, 56), Width = 340, Font = Ui.Font(10F) };
        var ok = new AppleButton("确定", AppleButton.Style.Filled) { Size = new Size(96, 34), Location = new Point(172, 104) };
        var cancel = new AppleButton("取消", AppleButton.Style.Secondary) { Size = new Size(96, 34), Location = new Point(276, 104) };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text.Trim(); dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
        dlg.Controls.Add(lbl); dlg.Controls.Add(tb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? (string.IsNullOrWhiteSpace(result) ? null : result) : null;
    }

    private string CountText(AppEntry app) => $"{_store.ClonesOf(app.Id).Count} 个分身";

    private static string ColorName(string hex) => hex switch
    {
        CloneColors.Default => "蓝", CloneColors.Green => "绿", CloneColors.Orange => "橙",
        CloneColors.Pink => "粉", CloneColors.Purple => "紫", CloneColors.Gray => "灰", _ => "蓝",
    };

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }
}
