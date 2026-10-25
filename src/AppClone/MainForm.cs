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
    private Splitter _splitter = null!;
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
    private readonly List<(string app, string clone, Label dot, Label text, AppleCard card, Label avatar, AppleButton btnLaunch)> _statusLabels = new();
    private readonly List<(string app, Label count)> _appCountLabels = new();
    private readonly List<(string appName, string cloneName, string appId, string cloneId)> _recent = new();
    private readonly HashSet<string> _hotkeyConflicts = new();
    private readonly Dictionary<string, DateTime> _runStart = new();
    // P2 崩溃守护：每个分身最近一次自动重启的时间与次数（间隔超过 30 分钟视为新一轮）
    private readonly Dictionary<string, (int Count, DateTime At)> _guardAttempts = new();
    // P4 定时启动：每个分身最近一次自动/定时拉起的时刻（保证同一天只触发一次）
    private readonly Dictionary<string, DateTime> _scheduleFired = new();

    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _pulse;
    private readonly System.Windows.Forms.Timer _sizeTimer;
    private float _pulsePhase;
    private bool _reallyClosing;
    private AppTheme _appliedTheme = AppTheme.Light;
    private string _dataSizeText = "—";
    private bool _busy;   // C9③：NewCloneFlow / ResetWithSnapshot 重入保护

    // —— 增量 1.3.0（F1 兼容性数据库 / F2 源应用监视 / F3 预检查 + Dry Run）——
    private Panel _upgradeBar = null!;      // 源应用升级提示条（Dock=Top，默认隐藏）
    private Label _upgradeText = null!;
    private AppleButton _btnUpgradeStamp = null!;
    private AppleButton _btnUpgradeRebuild = null!;

    // —— DPI（v1.4.1 真机 150% 修复，见 EnsureDpiScaled 注释）——
    private TableLayoutPanel _topBar = null!;   // 顶栏引用（探针/哨兵用）

    // —— 新手教程锚点（顶栏按钮在 BuildTopBar 里创建，需留引用给 Tutorial）——
    private AppleButton _btnAdd = null!;
    private AppleButton _btnSettings = null!;
    private AppleButton _btnMore = null!;
    private TourForm? _tour;

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
        // P2 窗口位置记忆：必须在 Load（句柄与 DPI 缩放完工）之后套用，
        // 构造期设 Bounds 会被 AutoScaleMode.Dpi 再放大一次。
        Load += (_, _) => RestoreWindowBounds();
        // PerMonitorV2 下点数字体随 DPI 放大，构造期像素布局必须整树缩放，否则 125%/150% 下文本被裁
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = TryAppIcon();

        // T13：托盘（Icon 非 null 兜底）
        _tray = new TrayContext(TryAppIcon() ?? SystemIcons.Application);
        _tray.OpenRequested += () => ShowFromTray();
        _tray.NewRequested += () => OpenDiscover();
        _tray.ExitRequested += () => RequestExit();
        // 1.4.0（F4 / F5）：托盘直达管理中心与操作日志
        _tray.ManagerRequested += () => { ShowFromTray(); OpenCloneManager(); };
        _tray.LogsRequested += () => { ShowFromTray(); OpenLogViewer(null, null); };
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

        // T14：注册已有分身热键 —— 已移至 OnHandleCreated（句柄就绪即注册）。
        // 不能在构造期注册：Register 内部读 _owner.Handle 会强制提前创建句柄，
        // 劫持框架的 DPI 整树缩放（v1.4.1 真机 150%：窗体缩了、子树没缩，界面大面积裁切）。

        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += (_, _) => { UpdateRunningStates(); CheckScheduledLaunches(); };
        _timer.Start();

        _pulse = new System.Windows.Forms.Timer { Interval = 60 };
        _pulse.Tick += (_, _) => { _pulsePhase += 0.09f; UpdatePulse(); };
        _pulse.Start();

        _sizeTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        _sizeTimer.Tick += (_, _) => RefreshDataSize();
        _sizeTimer.Start();
        RefreshDataSize();

        // T13/T14：启动态变化 → 即时刷新运行状态与托盘计数。
        // C7：Launcher.StateChanged 在线程池线程触发，必须编组回 UI 线程并做存活判断
        Launcher.StateChanged += () =>
        {
            if (IsHandleCreated) BeginInvoke(new Action(() =>
            {
                if (!IsDisposed) UpdateRunningStates();
            }));
        };

        // P2 崩溃守护：观察窗判定启动早退 → 编组回 UI 线程，仅对启用了守护的分身自动重启
        LaunchWatch.Crashed += (appId, cloneId, code) =>
        {
            if (IsHandleCreated) BeginInvoke(new Action(() => OnCloneCrashed(appId, cloneId, code)));
        };

        // F2：源应用基线回填（老数据静默补记）+ 后台升级巡检
        BackfillSourceBaselines();
        SweepSourceChanges();

        // F5：启动一条 INFO 流水，方便事后把「界面行为」与「数据变化」对上时间轴
        Log.Info("app", $"主界面就绪 · 应用 {_store.Model.Apps.Count} 个 · 主题 {Settings.Current.Theme}");

        // T13：开机自启
        AutoStart.SetEnabled(Settings.Current.LaunchOnStartup);

        // 圆角与淡入都需要句柄，挂到 Shown（SettingsForm 同款模式）。
        // 构造期绝不能强制创建句柄 —— 否则框架的 DPI 缩放只缩窗体不缩子树。
        Shown += (_, _) =>
        {
            Ui.RoundWindow(this);
            FadeAnimator.FadeIn(this);
            // 首次启动自动播新手教程；延后到本消息结束后，避免与淡入动画抢焦点
            if (!Settings.Current.OnboardingDone) BeginInvoke(new Action(StartTour));
            else BeginInvoke(new Action(RunAutoStarts));
        };

        // 构造末尾统一补 DPI 缩放（根因见 EnsureDpiScaled 注释）
        EnsureDpiScaled();
    }

    private bool _hotkeysRegistered;

    /// <summary>
    /// 整窗合成（WS_EX_COMPOSITED）：主界面大量自绘卡片 + 半透明子控件，
    /// 默认逐控件重绘时一旦有子控件刷新（状态点、悬停动画、滚动）就会露出底下的空白帧 —— 这就是闪屏。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000;   // WS_EX_COMPOSITED
            return cp;
        }
    }

    /// <summary>句柄就绪后注册全局热键（Register 内部依赖 _owner.Handle，见 HotkeyManager 文档）。</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_hotkeysRegistered || _reallyClosing) return;
        _hotkeysRegistered = true;
        RegisterAllHotkeys();
    }

    /// <summary>
    /// 显式 DPI 整树缩放（构造末尾调用一次）。
    ///
    /// <para><b>根因</b>（v1.4.1 真机 150% 大面积裁切，test/main-geom MiniProbe 逐语句实证）：
    /// PMv2 进程里 <c>AutoScaleDimensions = (96,96)</c> 的 <b>setter 本身</b>会立刻把窗体按
    /// 当前 DPI（144/96=1.5 倍）缩放并记下「已缩放」标记 —— 此刻子控件一个都还不存在，
    /// 于是只有窗体壳被放大（1120→1669），之后构建的整棵子树停在 96 坐标，
    /// Show 时的框架 AutoScale 又因标记已记账而跳过 → 1.5 倍字体塞进 96 像素布局。</para>
    ///
    /// <para><b>对策</b>：窗体壳交给 setter 缩放（行为已验证），子树在构造末尾由这里统一补缩。
    /// 构造期新建的卡片（MakeAppCard/MakeCloneCard 等）因无句柄不会自我缩放，恰好由本方法
    /// 一次覆盖；运行期（有句柄后）新建的卡片仍走各自的 Scale 闸门，两不相扰。</para>
    /// </summary>
    private void EnsureDpiScaled()
    {
        float f = Ui.DpiScaleOf(this);   // 无句柄时 = 主屏 DPI（PMv2 进程），MiniProbe [0] 实证
        if (Math.Abs(f - 1f) <= 0.001f) return;
        foreach (Control c in Controls)
            c.Scale(new SizeF(f, f));
    }

    public static Icon? TryAppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    // ------------------------------------------------------------------ 关闭 / 退出

    /// <summary>P2：套用上次退出时的窗口边界；越界（拔掉显示器 / 换分辨率）就退回居中，绝不把窗口丢到屏幕外。</summary>
    private void RestoreWindowBounds()
    {
        var s = Settings.Current;
        if (s.WindowW <= 0 || s.WindowH <= 0) return;
        var r = new Rectangle(s.WindowX, s.WindowY,
            Math.Max(s.WindowW, MinimumSize.Width), Math.Max(s.WindowH, MinimumSize.Height));
        if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(r))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = r;
        if (s.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>P2：退出时记录窗口边界（最大化时取还原后的边界）。</summary>
    private void SaveWindowBounds()
    {
        try
        {
            var s = Settings.Current;
            s.WindowMaximized = WindowState == FormWindowState.Maximized;
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (r.Width > 0 && r.Height > 0)
            {
                s.WindowX = r.X; s.WindowY = r.Y; s.WindowW = r.Width; s.WindowH = r.Height;
            }
            s.Save();
        }
        catch { }
    }

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

    /// <summary>真退出：注销热键、落盘统计，释放托盘，结束进程。</summary>
    private void RealExit()
    {
        Log.Info("app", "用户退出：注销热键、落盘统计与兼容性记录");
        SaveWindowBounds();
        try { _hotkey.UnregisterAll(); } catch { }
        try { CloneStats.Save(); } catch { }
        try { CompatibilityDb.Save(); } catch { }
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
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 64, BackColor = Ui.BgOf(),
            ColumnCount = 3, RowCount = 1, Padding = new Padding(20, 0, 20, 0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 品牌区
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // 弹性间隔
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 按钮区
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        top.Paint += (_, e) =>
        {
            using var p = new Pen(Ui.BorderOf(), 1F);
            e.Graphics.DrawLine(p, 0, top.Height - 1, top.Width, top.Height - 1);
        };

        var brand = new Panel { BackColor = Color.Transparent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var iconBox = new PictureBox
        {
            Size = new Size(28, 28), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(0, 14), Image = TryAppIcon()?.ToBitmap(),
        };
        var title = new Label
        {
            Text = "应用分身", AutoSize = true, Font = Ui.Font(Ui.FTitle, true),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Location = new Point(38, 12),
        };
        var sub = new Label
        {
            Text = "数据隔离 · 沙盒多开", AutoSize = true, Font = Ui.Font(Ui.FCaption),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent, Location = new Point(40, 34),
        };
        brand.Controls.Add(iconBox); brand.Controls.Add(title); brand.Controls.Add(sub);
        top.Controls.Add(brand, 0, 0);

        var btnData = new AppleButton("打开数据目录", AppleButton.Style.Secondary, 9F) { Size = new Size(112, 34) };
        btnData.Click += (_, _) => OpenFolder(Paths.DataRoot);

        var btnSettings = new AppleButton("设置", AppleButton.Style.Secondary, 9F) { Size = new Size(84, 34) };
        btnSettings.Click += (_, _) => OpenSettings();
        _btnSettings = btnSettings;

        var btnBatch = new AppleButton("批量", AppleButton.Style.Ghost, 9F) { Size = new Size(80, 34) };
        btnBatch.Click += (_, _) => ToggleBatchMode();
        _btnBatch = btnBatch;

        // 1.4.0（F4/F5）：跨应用总览与管理入口，收进「⋯ 更多」下拉，避免顶栏继续膨胀
        var btnMore = new AppleButton("⋯ 更多 ▾", AppleButton.Style.Ghost, 9F) { Size = new Size(96, 34) };
        _btnMore = btnMore;
        var moreMenu = new ContextMenuStrip { Font = Ui.Font(Ui.FBody) };
        var mManager = new ToolStripMenuItem("📋  分身管理中心") { Font = Ui.Font(Ui.FSub, true), ForeColor = Ui.ThemedColor(ThemeRole.AccentDark) };
        mManager.Click += (_, _) => OpenCloneManager();
        var mLogs = new ToolStripMenuItem("📜  操作日志");
        mLogs.Click += (_, _) => OpenLogViewer(SelectedApp(), null);
        var mTour = new ToolStripMenuItem("🎓  新手教程");
        mTour.Click += (_, _) => StartTour();
        var mHelp = new ToolStripMenuItem("❓  使用帮助");
        mHelp.Click += (_, _) => OpenHelp();
        var mDiag = new ToolStripMenuItem("📦  导出诊断包…");
        mDiag.Click += (_, _) => ExportDiagnostics();
        moreMenu.Items.Add(mManager);
        moreMenu.Items.Add(mLogs);
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add(mTour);
        moreMenu.Items.Add(mHelp);
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add(mDiag);
        btnMore.Click += (_, _) => moreMenu.Show(btnMore, new Point(0, btnMore.Height));

        var btnAdd = new AppleButton("＋ 添加应用 ▾", AppleButton.Style.Filled) { Size = new Size(140, 34) };
        _btnAdd = btnAdd;
        var addMenu = new ContextMenuStrip { Font = Ui.Font(Ui.FBody) };
        var mDiscover = new ToolStripMenuItem("🗂  从本机已安装应用添加（推荐）") { Font = Ui.Font(Ui.FSub, true), ForeColor = Ui.ThemedColor(ThemeRole.AccentDark) };
        mDiscover.Click += (_, _) => OpenDiscover();
        var mManual = new ToolStripMenuItem("📄  手动选择 exe 文件…");
        mManual.Click += (_, _) => AddAppFlow();
        addMenu.Items.Add(mDiscover);
        addMenu.Items.Add(mManual);
        btnAdd.Click += (_, _) => addMenu.Show(btnAdd, new Point(0, btnAdd.Height));

        // 右侧按钮容器：FlowLayoutPanel 自动排列，永不重叠（替代手写坐标 + Resize 补丁）
        var btnWrap = new FlowLayoutPanel
        {
            BackColor = Color.Transparent, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.None, Margin = new Padding(0),
            FlowDirection = FlowDirection.RightToLeft,
        };
        btnWrap.Controls.Add(btnData);
        btnWrap.Controls.Add(btnAdd);
        btnWrap.Controls.Add(btnSettings);
        btnWrap.Controls.Add(btnBatch);
        btnWrap.Controls.Add(btnMore);
        top.Controls.Add(btnWrap, 2, 0);

        _topBar = top;
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
            // 8 个分类 chip 在 280px 侧栏（150% DPI 下更宽）一行放不下，WrapContents=false 会把右侧
            // 「办公/其它」裁掉且无法滚动 —— 改为换行并预留两行高度。
            Size = new Size(sidebar.Width - 32, 60),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Ui.BgOf(),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        foreach (var c in new[] { "全部", "浏览器", "社交", "游戏", "开发", "云盘", "办公", "其它" })
            _appCats.Controls.Add(MakeAppCatChip(c));
        sidebar.Controls.Add(_appCats);

        _appFlow = new FlowLayoutPanel
        {
            Location = new Point(12, 148),
            Size = new Size(sidebar.Width - 24, sidebar.Height - 160),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.BgOf(), Padding = new Padding(0),
        };
        sidebar.Controls.Add(_appFlow);

        // T12：插画化空状态
        _appEmpty = BuildAppEmptyCard();
        _appEmpty.Location = new Point(16, 164);
        _appEmpty.Size = new Size(sidebar.Width - 32, 220);
        _appEmpty.Visible = false;
        sidebar.Controls.Add(_appEmpty);

        _noMatch = Ui.Label("没有匹配的应用", Ui.FBody, false, Ui.TextSecondaryOf());
        _noMatch.Location = new Point(24, 174);
        _noMatch.Visible = false;
        sidebar.Controls.Add(_noMatch);

        Controls.Add(sidebar);

        // 可拖拽分隔条：让侧栏宽度可调，贴合用户偏好（响应式）
        _splitter = new Splitter
        {
            Dock = DockStyle.Left, Width = 5, MinSize = 200, MinExtra = 380,
            BackColor = Ui.BorderOf(), Cursor = Cursors.VSplit,
        };
        _splitter.SplitterMoved += (_, _) => StretchAppCards();
        Controls.Add(_splitter);

        // 侧栏宽度变化（含分隔条拖拽）→ 应用卡片自适应宽度
        sidebar.Resize += (_, _) => StretchAppCards();
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

    // 左侧为空时的展示态：仅作说明，不做任何添加操作（添加入口统一在顶栏「＋ 添加应用」）
    private AppleCard BuildAppEmptyCard()
    {
        var card = new AppleCard { Radius = 16, DrawBorder = true, Shadow = true, BackColor = Ui.CardOf() };
        var icon = new PictureBox
        {
            Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(24, 24), BackColor = Color.Transparent, Image = MakeFallbackIcon(),
        };
        var title = Ui.Label("还没有应用", Ui.FCardTitle, true, Ui.TextPrimaryOf());
        title.Location = new Point(96, 28);
        var guide = Ui.Label("添加入口在右上角「＋ 添加应用」\n从本机已安装程序中一键发现并分身。",
            Ui.FBody, false, Ui.TextSecondaryOf());
        // AutoSize 标签设了 Size 也不会换行，文本会横向撑出卡片 —— 改为固定多行区域、横跨卡片全宽
        guide.AutoSize = false;
        guide.Location = new Point(24, 96);
        guide.Size = new Size(200, 100);
        card.Controls.Add(icon);
        card.Controls.Add(title);
        card.Controls.Add(guide);
        return card;
    }

    private void BuildRightPane()
    {
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Ui.BgOf(), Padding = new Padding(16, 12, 20, 8) };

        // T12：插画化空状态（未选应用）
        _rightPlaceholder = new AppleCard { Radius = 16, DrawBorder = true, Shadow = true, BackColor = Ui.CardOf(), Size = new Size(360, 180), Anchor = AnchorStyles.None };
        _rightPlaceholder.Controls.Add(new PictureBox
        {
            Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(40, 40), BackColor = Color.Transparent, Image = MakeFallbackIcon(),
        });
        var phText = Ui.Label("选择左侧应用，管理它的分身", Ui.FCardTitle, true, Ui.TextSecondaryOf());
        phText.Location = new Point(112, 52);
        phText.Size = new Size(200, 60);
        _rightPlaceholder.Controls.Add(phText);

        _rightIcon = new PictureBox { Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.None };
        _rightTitle = new Label { AutoSize = true, Font = Ui.Font(Ui.FTitle, true), ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Anchor = AnchorStyles.Left };
        _rightPath = new Label
        {
            AutoSize = false, Font = Ui.Font(Ui.FCaption), ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, AutoEllipsis = true,
        };

        _btnNewClone = new AppleButton("＋ 新建分身", AppleButton.Style.Filled) { Size = new Size(112, 36) };
        _btnNewClone.Click += async (_, _) => await NewCloneFlow();
        _btnEndAll = new AppleButton("结束全部", AppleButton.Style.Secondary, 9F) { Size = new Size(96, 36) };
        _btnEndAll.Click += (_, _) => EndAllClones();

        var info = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Padding = new Padding(0) };
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        info.Controls.Add(_rightTitle, 0, 0);
        info.Controls.Add(_rightPath, 0, 1);

        // 右侧按钮容器：FlowLayoutPanel 自动排列 + 垂直居中，永不重叠。
        // 必须显式尺寸，禁用 AutoSize+GrowAndShrink —— 该组合在 TLP AutoSize 列里
        // 遇 DPI 整树 Scale 会被算成 0×0（test/main-geom FlowProbe 实证），按钮集体消失
        var actions = new FlowLayoutPanel
        {
            BackColor = Color.Transparent, WrapContents = false,
            Size = new Size(224, 42), Anchor = AnchorStyles.None, Margin = new Padding(0),
            FlowDirection = FlowDirection.RightToLeft,
        };
        actions.Controls.Add(_btnNewClone);
        actions.Controls.Add(_btnEndAll);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 68, BackColor = Ui.BgOf(), ColumnCount = 3, RowCount = 1,
            Padding = new Padding(16, 0, 20, 0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 图标
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // 信息区
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 按钮区
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Paint += (_, e) =>
        {
            using var p = new Pen(Ui.BorderOf(), 1F);
            e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        header.Controls.Add(_rightIcon, 0, 0);
        header.Controls.Add(info, 1, 0);
        header.Controls.Add(actions, 2, 0);

        _cloneFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Ui.BgOf(), Padding = new Padding(0, 8, 0, 0),
        };
        _cloneFlow.Resize += (_, _) => StretchCloneCards();

        // T12：插画化空状态（无分身）
        _cloneEmpty = new AppleCard { Radius = 16, DrawBorder = true, Shadow = true, BackColor = Ui.CardOf(), Size = new Size(420, 200), Anchor = AnchorStyles.None };
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
        _cloneEmpty.Visible = false;

        // ---- F2：源应用升级 / 丢失提示条（默认隐藏）----
        _upgradeBar = new Panel
        {
            Dock = DockStyle.Top, Height = 46, BackColor = Ui.WarnLightOf(),
            Visible = false, Padding = new Padding(16, 0, 20, 0),
        };
        _upgradeText = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
            Font = Ui.Font(Ui.FBody), ForeColor = Ui.WarnOf(), BackColor = Color.Transparent,
        };
        _btnUpgradeStamp = new AppleButton("仅更新版本记录", AppleButton.Style.Secondary, 9F) { Size = new Size(122, 32) };
        _btnUpgradeStamp.Click += (_, _) => { var a = SelectedApp(); if (a != null) StampSourceRecord(a); };
        _btnUpgradeRebuild = new AppleButton("重建分身数据", AppleButton.Style.Filled, 9F) { Size = new Size(112, 32) };
        _btnUpgradeRebuild.Click += async (_, _) => { var a = SelectedApp(); if (a != null) await RebuildForSource(a); };

        var upgradeActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent,
            Padding = new Padding(0, 7, 0, 7),
        };
        _btnUpgradeStamp.Margin = new Padding(8, 0, 0, 0);
        upgradeActions.Controls.Add(_btnUpgradeRebuild);
        upgradeActions.Controls.Add(_btnUpgradeStamp);

        _upgradeBar.Controls.Add(_upgradeText);      // Fill 先加入
        _upgradeBar.Controls.Add(upgradeActions);    // Dock=Right 后加入 → 停靠优先，先占右侧

        // 停靠顺序说明：WinForms 从 Controls 集合「末尾向前」依次停靠 →
        // 最后加入的 header 先占顶部，_upgradeBar 紧随其下，_cloneFlow 填充剩余空间。
        // 注意：_upgradeBar 不可调用 BringToFront（会改变停靠次序、打乱布局）。
        right.Controls.Add(_cloneFlow);
        right.Controls.Add(_upgradeBar);
        right.Controls.Add(header);
        right.Controls.Add(_rightPlaceholder);
        right.Controls.Add(_cloneEmpty);
        _rightPlaceholder.BringToFront();

        Controls.Add(right);
        right.BringToFront();
        right.Resize += (_, _) => CenterRightOverlays();
        CenterRightOverlays();
    }

    /// <summary>
    /// 右栏两张浮动卡片（未选应用占位 / 无分身空态）是 Anchor=None 的非停靠控件，
    /// 此前从未设置 Location，一直缩在客户区左上角 —— 这里统一水平垂直居中。
    /// </summary>
    private void CenterRightOverlays()
    {
        var host = _rightPlaceholder.Parent;
        if (host == null) return;
        int w = host.ClientSize.Width, h = host.ClientSize.Height;
        _rightPlaceholder.Location = new Point(
            Math.Max(0, (w - _rightPlaceholder.Width) / 2), Math.Max(0, (h - _rightPlaceholder.Height) / 2));
        _cloneEmpty.Location = new Point(
            Math.Max(0, (w - _cloneEmpty.Width) / 2), Math.Max(0, (h - _cloneEmpty.Height) / 2));
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

        // C4：批量条按钮全部收入 Dock=Right 的 FlowLayoutPanel（与顶栏右侧按钮容器同款做法），
        // FlowDirection.RightToLeft → 从右往左依次排列，任何窗体宽度下都不会重叠
        var wrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.BgOf(),
            Padding = new Padding(0, 0, 20, 0),
        };

        _btnBatchNew = new AppleButton("批量新建分身", AppleButton.Style.Filled, 9F) { Size = new Size(132, 34) };
        _btnBatchNew.Click += (_, _) => BatchNewClones();
        _btnBatchEnd = new AppleButton("批量结束运行", AppleButton.Style.Secondary, 9F) { Size = new Size(120, 34) };
        _btnBatchEnd.Click += (_, _) => BatchEndRunning();
        _btnBatchExport = new AppleButton("批量导出配置", AppleButton.Style.Secondary, 9F) { Size = new Size(120, 34) };
        _btnBatchExport.Click += (_, _) => BatchExportConfig();
        _btnBatchExit = new AppleButton("退出批量", AppleButton.Style.Ghost, 9F) { Size = new Size(84, 34) };
        _btnBatchExit.Click += (_, _) => ToggleBatchMode();

        // 统一垂直居中（条高 52 - 按钮高 34 = 上下各 9），水平间距 12
        foreach (var b in new[] { _btnBatchNew, _btnBatchEnd, _btnBatchExport, _btnBatchExit })
            b.Margin = new Padding(0, 9, 12, 9);

        // RightToLeft 流向：先加入的在最右 → 依次：退出批量 / 导出 / 结束 / 新建
        wrap.Controls.Add(_btnBatchExit);
        wrap.Controls.Add(_btnBatchExport);
        wrap.Controls.Add(_btnBatchEnd);
        wrap.Controls.Add(_btnBatchNew);

        _batchBar.Controls.Add(wrap);
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
        using var sf = new SettingsForm(ImportDataPackage);
        sf.ShowDialog(this);   // 设置窗口编辑即持久化（Settings.Save）
        RefreshAppList();
        if (_selectedAppId != null) RefreshRightPane();
        if (Settings.Current.Theme != prev)
            BeginInvoke(new Action(ApplyTheme));   // 延后到当前消息循环结束后重建，避免释放正在处理事件的控件
    }

    /// <summary>
    /// 遮罩式新手教程：挖孔高亮 + 分步气泡。首次启动自动播放，也可从「更多 → 新手教程」或帮助窗口重播。
    /// 已有一轮在播时直接忽略，避免叠两层遮罩。
    /// </summary>
    private void StartTour()
    {
        if (_tour != null || IsDisposed || !Visible) return;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        var steps = new List<TourForm.Step>
        {
            new(null, "欢迎使用「应用分身」",
                "分身 = 同一个程序的独立副本：各自的账号、存档、缓存互不干扰。\n\n下面用 7 步把主要操作过一遍，随时可点「跳过」，以后在「⋯ 更多 → 新手教程」重播。"),
            new(_btnAdd, "第 1 步 · 添加要分身的应用",
                "点这里，优先选「从本机已安装应用添加」自动扫描；找不到再手动选 exe 文件。\n\n同一程序只需添加一次，之后可以为它开多个分身。"),
            new(_appFlow, "第 2 步 · 选择应用",
                "左侧是已添加的应用列表，点一下即可切换。\n选中后，右侧会显示这个应用的分身卡片与操作入口。"),
            new(_btnNewClone, "第 3 步 · 新建分身",
                "点「＋ 新建分身」为当前应用创建副本。\n默认使用「深度隔离」（注册表 + 环境变量双重隔离），一般直接确认即可。"),
            new(_cloneFlow, "第 4 步 · 启动、停止与更多操作",
                "卡片上可直接「启动 / 停止」。右键菜单还有：重命名、快照、重置、克隆、冻结、代理、快捷方式、导出等高级功能。"),
            new(_btnSettings, "第 5 步 · 全局设置",
                "主题（浅色 / 深色）、启动密码、默认隔离模式、开机自启、数据目录与备份都在这里。\n改完即时生效并保存。"),
            new(null, "完成了，随时可查详细说明",
                "需要逐项功能说明时，打开「⋯ 更多 → 使用帮助」：内置离线文档，覆盖全部功能与常见问题。\n\n（帮助窗口里也能重播本教程。）"),
        };

        // 教程结束后再拉起「随软件启动」的分身，避免遮罩气泡与自动启动同时抢舞台
        _tour = new TourForm(this, steps) { Completed = () => { _tour = null; RunAutoStarts(); } };
        _tour.Start();
    }

    /// <summary>内置帮助文档（离线，不联网）。</summary>
    private void OpenHelp()
    {
        using var hf = new HelpForm();
        hf.ReplayTour = () => BeginInvoke(new Action(StartTour));
        hf.ShowDialog(this);
    }

    /// <summary>
    /// 设置页「导入数据包」：必须用主界面这份 _store 落地 —— 另开 Store 实例保存会覆盖主界面内存里的数据。
    /// 返回给设置页显示的提示文案。
    /// </summary>
    private string ImportDataPackage(string path)
    {
        try
        {
            var (apps, clones, err) = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? ConfigIo.Import(path, _store)
                : ConfigIo.ImportPackage(path, _store);
            if (err.Length > 0) return $"导入失败：{err}";
            RefreshAppList();
            if (_selectedAppId != null) RefreshRightPane();
            return $"已导入 {apps} 个应用 / {clones} 个分身。";
        }
        catch (Exception ex)
        {
            return $"导入失败：{ex.Message}";
        }
    }

    /// <summary>P3：把单个应用（含全部分身数据）导出成 .zip，便于换机或分享配置。</summary>
    private async void ExportAppPackage(AppEntry app)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出应用包", Filter = "分身应用包 (*.zip)|*.zip",
            FileName = $"{app.Name}-分身包.zip", AddExtension = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int clones = _store.ClonesOf(app.Id).Count;
        UseWaitCursor = true;
        try
        {
            var (_, exported, bytes) = await Task.Run(() =>
                ConfigIo.ExportPackage(dlg.FileName, _store, new[] { app.Id }));
            MessageBox.Show(this,
                $"已导出「{app.Name}」：{exported} 个分身（约 {bytes / 1048576} MB）\n{dlg.FileName}",
                "导出完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SetStatus($"已导出「{app.Name}」（{clones} 个分身）");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { UseWaitCursor = false; }
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
        // 重建发生在窗体加载之后，AutoScale 不会再跑 —— 新树必须手动按 DPI 缩放，
        // 否则主题切换一次，界面就回到 96 DPI 的像素尺寸、文本全被裁
        float f = Ui.DpiScaleOf(this);
        if (IsHandleCreated && Math.Abs(f - 1f) > 0.001f)
            foreach (Control c in Controls)
                c.Scale(new SizeF(f, f));
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
        RefreshAll();
        SetStatus($"已添加 {app.Name}");
    }

    private async Task NewCloneFlow()
    {
        if (_busy) return;   // C9③：重入保护（异步流程进行中忽略重复点击）
        var app = SelectedApp();
        if (app == null) return;
        _busy = true;
        try
        {
            SetStatus("正在创建分身…");
            var clone = _store.AddClone(app.Id);

            // 立即刷新：新分身卡片与左侧计数同步出现，不再等待隔离环境准备
            RefreshAll();
            SetStatus($"已创建 {clone.Name}");

            // 隔离环境后台准备（不阻塞 UI、不延迟卡片显示）；RegClone 已幂等（已就绪跳过）
            if (clone.DeepIsolation)
            {
                SetStatus("准备隔离环境…");
                var ok = await Task.Run(() => RegClone.EnsureCloneRegistry(app.Id, clone.Id, out _));
                if (!ok) SetStatus("注册表快照初始化失败，首次启动时会自动重试");
            }
            if (clone.DeviceClone)
                await Task.Run(() => RegClone.EnsureDeviceFake(app.Id, clone.Id));
            if (clone.DeepIsolation || clone.DeviceClone)
                SetStatus($"已创建 {clone.Name}，可以启动了");
        }
        finally
        {
            _busy = false;
        }
    }

    // ------------------------------------------------------------------ 列表渲染（T12 过滤）

    private void RefreshAppList()
    {
        string q = _appSearch.Text == "搜索应用或发布者…" ? "" : _appSearch.Text.Trim();
        bool empty = _store.Model.Apps.Count == 0;
        _appEmpty.Visible = empty;
        _noMatch.Visible = false;

        _appFlow.SuspendLayout();
        var stale = _appFlow.Controls.Cast<Control>().ToList();   // 先摘下再释放，避免句柄泄漏
        _appFlow.Controls.Clear();
        _appCountLabels.Clear();
        foreach (var app in _store.Model.Apps)
        {
            if (!AppMatches(app, q, _appCategory)) continue;
            _appFlow.Controls.Add(MakeAppCard(app));
        }
        _appFlow.ResumeLayout();
        StretchAppCards();
        foreach (var c in stale) c.Dispose();

        bool anyMatch = _appFlow.Controls.Count > 0;
        _noMatch.Visible = !empty && !anyMatch;
        if (empty) _appEmpty.BringToFront();
        else if (_noMatch.Visible) _noMatch.BringToFront();   // 不置顶会被从不隐藏的 _appFlow（Dock=Fill）盖住，提示从未显示过
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

    private AppleCard MakeAppCard(AppEntry app)
    {
        bool selected = app.Id == _selectedAppId;
        var card = new AppleCard
        {
            Width = 248, Height = 64, Margin = new Padding(4, 4, 4, 4),
            Cursor = Cursors.Hand, Radius = 16, BackColor = Ui.CardOf(), DrawBorder = true,
        };
        card.BackColorOverride = () => app.Id == _selectedAppId ? Ui.AccentLightOf() : Ui.CardOf();
        card.AccentBar = selected ? Ui.ThemedColor(ThemeRole.Accent) : null;
        if (selected) card.DisableHoverAnimation();

        CheckBox? chk = null;
        int colCount = 2;
        if (_batchMode)
        {
            colCount = 3;
            chk = new CheckBox { Size = new Size(18, 18), Checked = _appSelected.Contains(app.Id), Anchor = AnchorStyles.None };
            chk.CheckedChanged += (_, _) =>
            {
                if (chk.Checked) _appSelected.Add(app.Id); else _appSelected.Remove(app.Id);
                UpdateBatchBar();
            };
        }

        var icon = new PictureBox
        {
            Size = new Size(36, 36), SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent, Anchor = AnchorStyles.None, Image = AppIcons.Load(app.Id),
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

        var text = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Padding = new Padding(0) };
        text.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        text.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var name = new Label
        {
            Text = app.Name, AutoSize = false, AutoEllipsis = true, Font = Ui.Font(Ui.FCardTitle, true),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        var count = new Label
        {
            // C6：运行数并入文本（原 Paint 订阅绘制在客户区外被裁剪，永不可见），
            // 运行态文本由 UpdateRunningStates 统一追加/还原
            Text = CountText(app.Id), AutoSize = true, Font = Ui.Font(Ui.FCaption),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent,
        };
        _appCountLabels.Add((app.Id, count));

        // F1：兼容性徽章与运行计数同行，窄卡片下自动换行（FlowLayoutPanel）
        var chip = MakeCompatChip(app);
        count.Margin = new Padding(0, 0, 6, 0);
        chip.Margin = new Padding(0, 0, 0, 0);
        var metaRow = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        metaRow.Controls.Add(count);
        metaRow.Controls.Add(chip);

        text.Controls.Add(name, 0, 0);
        text.Controls.Add(metaRow, 0, 1);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent, RowCount = 1, ColumnCount = colCount,
            Padding = new Padding(12, 0, 12, 0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (colCount == 3) root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        if (chk != null)
        {
            root.Controls.Add(chk, 0, 0);
            root.Controls.Add(icon, 1, 0);
            root.Controls.Add(text, 2, 0);
        }
        else
        {
            root.Controls.Add(icon, 0, 0);
            root.Controls.Add(text, 1, 0);
        }
        card.Controls.Add(root);

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
        root.Click += Select;
        icon.Click += Select;
        name.Click += Select;
        count.Click += Select;
        metaRow.Click += Select;
        chip.Click += Select;
        if (chk != null) chk.Click += Select;

        // 键盘可达：聚焦后 Enter / Space 选中应用（无障碍）
        card.Selectable = true;
        card.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { e.SuppressKeyPress = true; SelectApp(app.Id); }
        };

        var menu = new ContextMenuStrip { Font = Ui.Font(Ui.FBody) };
        // F1：兼容性结论只读展示（说明文字太长放不进徽章，故进菜单）
        var compatLevel = CompatibilityDb.Resolve(app, out var compatNote);
        var compatInfo = new ToolStripMenuItem($"兼容性：{CompatibilityDb.Label(compatLevel)}") { Enabled = false };
        if (compatNote.Length > 0) compatInfo.ToolTipText = compatNote;
        menu.Items.Add(compatInfo);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开程序所在目录", null, (_, _) => OpenFolder(Path.GetDirectoryName(app.ExePath) ?? ""));
        menu.Items.Add("导出此应用（含全部分身数据）…", null, (_, _) => ExportAppPackage(app));
        var remove = new ToolStripMenuItem("移除应用（连同全部分身数据）") { ForeColor = Ui.DangerOf() };
        remove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"确定移除「{app.Name}」吗？\n该应用的全部分身数据与登录状态将被永久删除。",
                    "移除应用", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _store.RemoveApp(app.Id);
            if (_selectedAppId == app.Id) _selectedAppId = null;
            RefreshAll();
            SetStatus($"已移除 {app.Name}");
        };
        menu.Items.Add(remove);
        card.ContextMenuStrip = menu;

        // 运行期动态创建的卡片不参与窗体 AutoScale（加载后创建的都拿不到），
        // 高 DPI 下必须自己缩放；构造期创建的（首次 RefreshAppList）交给窗体 AutoScale，缩了会翻倍
        if (IsHandleCreated)
        {
            float f = Ui.DpiScaleOf(this);
            if (Math.Abs(f - 1f) > 0.001f) card.Scale(new SizeF(f, f));
        }

        return card;
    }

    private void SelectApp(string appId)
    {
        _selectedAppId = appId;
        RefreshAppList();
        RefreshRightPane();
    }

    /// <summary>数据变更后的统一刷新入口：左侧列表 + 右栏分身卡 + 运行状态即时同步。</summary>
    private void RefreshAll()
    {
        RefreshAppList();
        RefreshRightPane();
        UpdateRunningStates();
    }

    private void RefreshRightPane()
    {
        var app = SelectedApp();
        _rightPlaceholder.Visible = app == null;
        CenterRightOverlays();
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
            _statusLabels.Clear();   // C9②：清死引用，防止已释放卡片被后续刷新触达
            UpdateUpgradeBar(null);
            return;
        }

        _rightIcon.Image?.Dispose();
        _rightIcon.Image = AppIcons.Load(app.Id) ?? MakeFallbackIcon();
        _rightTitle.Text = app.Name;
        _rightPath.Text = app.ExePath;

        UpdateUpgradeBar(app);   // F2：源应用基线偏离时点亮提示条
        RebuildCloneCards(app);
    }

    private static Image? _fallback;

    /// <summary>
    /// C1：兜底图标每次返回私有克隆（内部缓存原件不再外借）。
    /// 调用方（如 PictureBox 换图时）对返回值 Dispose 是安全的，不会毒化共享缓存。
    /// </summary>
    private static Image MakeFallbackIcon()
    {
        if (_fallback == null)
        {
            var bmp = new Bitmap(48, 48);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(209, 213, 219));
            g.FillEllipse(b, 8, 8, 32, 32);
            _fallback = bmp;
        }
        return new Bitmap(_fallback);
    }

    private void RebuildCloneCards(AppEntry app)
    {
        var stale = _cloneFlow.Controls.Cast<Control>().ToList();   // 先摘下再释放，避免句柄泄漏
        _cloneFlow.SuspendLayout();
        _cloneFlow.Controls.Clear();
        _statusLabels.Clear();

        var clones = _store.ClonesOf(app.Id);
        for (int i = 0; i < clones.Count; i++)
            _cloneFlow.Controls.Add(MakeCloneCard(app, clones[i], i + 1));

        StretchCloneCards();
        _cloneFlow.ResumeLayout();
        // _cloneFlow 是不透明的 Dock=Fill，_cloneEmpty 压在它底下 → 空态时必须把它藏起来
        _cloneFlow.Visible = clones.Count > 0;
        _cloneEmpty.Visible = clones.Count == 0;
        CenterRightOverlays();
        foreach (var c in stale) c.Dispose();
    }

    private void StretchCloneCards()
    {
        int w = Math.Max(420, _cloneFlow.ClientSize.Width - 16);
        foreach (Control c in _cloneFlow.Controls) c.Width = w;
    }

    /// <summary>侧栏应用卡片宽度随侧栏宽度自适应；卡片内部由 TableLayoutPanel 自动重排（响应式 + 零重叠）。</summary>
    private void StretchAppCards()
    {
        if (_appFlow == null) return;
        int w = Math.Max(150, _appFlow.ClientSize.Width - 8);
        foreach (Control c in _appFlow.Controls)
            if (c is AppleCard card) card.Width = w;
    }

    private AppleCard MakeCloneCard(AppEntry app, CloneEntry clone, int index)
    {
        bool running = Launcher.IsRunning(app.Id, clone.Id);
        var card = new AppleCard
        {
            Width = 620, Height = 96, Margin = new Padding(2, 8, 2, 8), Radius = 16,
            BackColor = Ui.CardOf(), DrawBorder = true, Shadow = true, AccentBar = running ? Ui.OkOf() : null,
        };

        // 分身头像：渐变圆角方块 + 序号；非运行时用 CloneColors 角标色（圆角方块，非圆形）
        var avatar = new Label
        {
            Size = new Size(40, 40), Text = index.ToString(), TextAlign = ContentAlignment.MiddleCenter,
            Font = Ui.Font(12F, true), ForeColor = Color.White, BackColor = Color.Transparent, Anchor = AnchorStyles.None,
        };
        avatar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // 尺寸跟控件走（DPI 缩放后不再是 40×40），半径也按 DPI 换算
            using var gPath = Ui.Round(new Rectangle(Point.Empty, avatar.Size), Ui.S(this, 12));
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
            Text = clone.Name, AutoSize = false, AutoEllipsis = true,
            Font = Ui.Font(Ui.FCardTitle, true),
            ForeColor = Ui.TextPrimaryOf(), BackColor = Color.Transparent, Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };

        var dot = new Label
        {
            Text = "●", AutoSize = true, Font = new Font("Segoe UI", 10F),
            ForeColor = running ? Ui.OkOf() : Ui.ThemedColor(ThemeRole.FillGrayHover), BackColor = Color.Transparent, Tag = running,
        };
        var state = new Label
        {
            Text = running ? "运行中" : "未运行", AutoSize = true, Font = Ui.Font(Ui.FCaption),
            ForeColor = running ? Ui.OkOf() : Ui.TextSecondaryOf(), BackColor = Color.Transparent,
        };

        // 元数据行：状态 + 隔离/设备/冲突 徽章 + 创建日期，统一进入可换行 FlowLayoutPanel；
        // 关键——元数据置于独立的「内容列」，与右侧按钮列由 TableLayoutPanel 分隔，
        // 任何卡片宽度下都绝不会与启动/管理按钮重叠（响应式 + 零重叠）
        var meta = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
            BackColor = Color.Transparent, Padding = new Padding(0), Margin = new Padding(0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        dot.Margin = new Padding(0, 0, 2, 0);
        state.Margin = new Padding(0, 0, 8, 0);
        meta.Controls.Add(dot);
        meta.Controls.Add(state);
        meta.Controls.Add(new PillChip(clone.DeepIsolation ? "深度隔离" : "标准隔离",
            clone.DeepIsolation ? Ui.AccentLightOf() : Ui.FillGrayOf(),
            clone.DeepIsolation ? Ui.ThemedColor(ThemeRole.Accent) : Ui.TextSecondaryOf())
        { Margin = new Padding(0, 0, 6, 0) });
        if (clone.DeviceClone)
            meta.Controls.Add(new PillChip("设备分身", Ui.ChipPurpleOf(), Ui.ChipPurpleTextOf())
            { Margin = new Padding(0, 0, 6, 0) });
        if (_hotkeyConflicts.Contains(clone.Id))
            meta.Controls.Add(new PillChip("热键冲突", Ui.DangerOf(), Ui.DangerOf(), true) { Margin = new Padding(0, 0, 6, 0) });

        // F1：兼容性徽章（✓ 可用 / ⚠ 部分 / ✗ 不支持 / — 未测试）
        var compatChip = MakeCompatChip(app);
        compatChip.Margin = new Padding(0, 0, 6, 0);
        meta.Controls.Add(compatChip);

        meta.Controls.Add(new Label
        {
            Text = $"创建于 {clone.CreatedAt:yyyy-MM-dd}", AutoSize = true, Font = Ui.Font(Ui.FChip),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 0),
        });

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 1, RowCount = 2, Padding = new Padding(0),
        };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(name, 0, 0);
        content.Controls.Add(meta, 0, 1);

        var btnLaunch = new AppleButton(running ? "再次启动" : "启 动", AppleButton.Style.Filled) { Size = new Size(96, 36) };
        btnLaunch.Click += (_, _) => LaunchClone(app, clone);
        var btnMore = new AppleButton("管理  ▾", AppleButton.Style.Secondary, 9F) { Size = new Size(84, 36) };
        btnMore.Click += (_, _) => ShowCloneMenu(btnMore, app, clone);

        // 右侧按钮列：自动排列、垂直居中，与内容列物理隔离 → 永不重叠
        var actions = new TableLayoutPanel
        {
            BackColor = Color.Transparent, ColumnCount = 2, RowCount = 1,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.None, Padding = new Padding(0),
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        btnMore.Margin = new Padding(10, 0, 0, 0);
        btnLaunch.Anchor = AnchorStyles.None;
        btnMore.Anchor = AnchorStyles.None;
        actions.Controls.Add(btnLaunch, 0, 0);
        actions.Controls.Add(btnMore, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent, ColumnCount = 3, RowCount = 1,
            Padding = new Padding(24, 0, 24, 0),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));       // 头像
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // 内容
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 按钮
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(avatar, 0, 0);
        root.Controls.Add(content, 1, 0);
        root.Controls.Add(actions, 2, 0);
        card.Controls.Add(root);

        // 运行期动态创建：高 DPI 下自行缩放（构造期创建的交给窗体 AutoScale，见 MakeAppCard 注释）
        if (IsHandleCreated)
        {
            float f = Ui.DpiScaleOf(this);
            if (Math.Abs(f - 1f) > 0.001f) card.Scale(new SizeF(f, f));
        }

        _statusLabels.Add((app.Id, clone.Id, dot, state, card, avatar, btnLaunch));
        return card;
    }

    private void UpdateRunningStates()
    {
        int running = 0;
        foreach (var (appId, cloneId, dot, text, card, avatar, btnLaunch) in _statusLabels)
        {
            bool r = Launcher.IsRunning(appId, cloneId);
            if (r) running++;

            // 状态点 + 文字
            text.Text = r ? "运行中" : "未运行";
            text.ForeColor = r ? Ui.OkOf() : Ui.TextSecondaryOf();
            text.Tag = r;
            dot.Tag = r;
            dot.ForeColor = r ? Ui.OkOf() : Ui.ThemedColor(ThemeRole.FillGrayHover);
            dot.Invalidate();

            // 头像运行态变色（Tag 驱动，仅在变化时重绘）
            if ((avatar.Tag is true) != r) { avatar.Tag = r; avatar.Invalidate(); }

            // 启动按钮文字跟随状态
            string want = r ? "再次启动" : "启 动";
            if (btnLaunch.Text != want) btnLaunch.Text = want;

            // 强调条：开始运行点亮（随后由呼吸动画接管）；停止运行立即熄灭（防止残留）
            if (r && card.AccentBar == null) { card.AccentBar = Ui.OkOf(); card.Invalidate(); }
            else if (!r && card.AccentBar != null) { card.AccentBar = null; card.Invalidate(); }

            // 时长统计兜底：热键/桌面快捷方式启动也能记录；外部退出（直接关程序）也记时长
            string key = $"{appId}|{cloneId}";
            if (r && !_runStart.ContainsKey(key)) _runStart[key] = DateTime.Now;
            if (!r && _runStart.TryGetValue(key, out var t0))
            {
                CloneStats.RecordStop(appId, cloneId, DateTime.Now - t0);
                _runStart.Remove(key);
            }
        }
        // C6：左侧应用卡片计数标签 —— 运行数并入文本（替代被裁剪的 Paint 绘制），
        // 运行中 > 0 时追加「· N 运行中」并切换为绿色前景，恢复为 0 时还原
        foreach (var (countAppId, count) in _appCountLabels)
        {
            int appRunning = _store.ClonesOf(countAppId).Count(c => Launcher.IsRunning(countAppId, c.Id));
            string want = appRunning > 0 ? $"{CountText(countAppId)} · {appRunning} 运行中" : CountText(countAppId);
            if (count.Text != want) count.Text = want;
            count.ForeColor = appRunning > 0 ? Ui.OkOf() : Ui.TextSecondaryOf();
        }
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
            if (IsHandleCreated) BeginInvoke(new Action(UpdateStatusBar));
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
        foreach (var (_, _, dot, _, card, _, _) in _statusLabels)
        {
            if (dot.Tag is true)
            {
                var baseColor = Ui.OkOf();
                var lit = Color.FromArgb(120, 205, 130);
                dot.ForeColor = Ui.Lerp(baseColor, lit, k * 0.55f);
                card.AccentBar = Ui.Lerp(baseColor, lit, k * 0.45f);
                // 只重绘左侧强调条区域：整卡 Invalidate 会连带重绘卡片里所有子控件，
                // 60ms 一次（约 16FPS）就是持续闪屏的来源。
                card.Invalidate(new Rectangle(0, 0, 16, card.Height));
            }
            else if (card.AccentBar != null)
            {
                card.AccentBar = null;
                card.Invalidate(new Rectangle(0, 0, 16, card.Height));
            }
        }
    }

    // ------------------------------------------------------------------ 动作

    private void LaunchClone(AppEntry app, CloneEntry clone)
    {
        // P2：用户手动启动 = 新一轮守护（之前的自动重启次数清零）
        _guardAttempts.Remove($"{app.Id}|{clone.Id}");

        // F3：预检查 —— 阻断项（目标程序不存在 / 数据目录不可写）直接中止
        var preflight = Preflight.Check(app, clone);
        if (Preflight.HasBlocking(preflight))
        {
            Log.Error("launch", "预检查未通过，已中止启动：" + Preflight.Format(preflight).Replace('\n', ' '),
                app.Id, clone.Id, "preflight");
            MessageBox.Show(DialogOwner(), Preflight.Format(preflight), "预检查未通过", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("预检查未通过，已中止启动");
            return;
        }

        // F3：执行预览（Dry Run，默认开）；关闭预览时至少把警告回显到状态栏
        if (!ConfirmDryRun(app, clone, preflight)) { SetStatus("已取消启动"); return; }
        if (!Settings.Current.ShowDryRun)
        {
            var warn = preflight.FirstOrDefault(i => i.Level == PreflightSeverity.Warn);
            if (warn != null) SetStatus($"注意：{warn.Title} —— {warn.Detail}");
        }

        SetStatus($"正在启动 {app.Name} · {clone.Name} …");
        var outcome = Launcher.Launch(_store, app, clone);
        if (!outcome.Ok)
        {
            Log.Error("launch", "启动失败：" + outcome.Message, app.Id, clone.Id, "launch");
            MessageBox.Show(this, outcome.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("启动失败");
            return;
        }
        Log.Info("launch", $"已启动（PID {outcome.Pid}）" +
            (string.IsNullOrEmpty(outcome.Message) ? "" : " · " + outcome.Message), app.Id, clone.Id, "launch");
        // T14：记录启动统计
        CloneStats.RecordLaunch(app.Id, clone.Id);
        // F1：启动观察窗 —— 观察窗结束后回写兼容性数据库（存活 → ✓ / 异常早退 → ⚠）
        LaunchWatch.Watch(app.Id, clone.Id, app.ExePath, outcome.Pid);
        _runStart[$"{app.Id}|{clone.Id}"] = DateTime.Now;
        PushRecent(app, clone);
        RebuildTrayMenu();
        UpdateTrayCount();
        SetStatus(string.IsNullOrEmpty(outcome.Message)
            ? $"已启动 {app.Name} · {clone.Name}"
            : $"{app.Name} · {clone.Name}：{outcome.Message}");
        RefreshAll();
    }

    // ------------------------------------------------------------------ P4 自动 / 定时启动

    /// <summary>软件启动后拉起所有标记了「随软件启动」的分身（冻结 / 已在运行的不重复启动）。</summary>
    private void RunAutoStarts()
    {
        if (IsDisposed) return;
        foreach (var app in _store.Model.Apps)
            foreach (var clone in _store.ClonesOf(app.Id))
            {
                if (!clone.AutoStart || clone.Frozen) continue;
                if (Launcher.IsRunning(app.Id, clone.Id)) continue;
                LaunchCloneSilent(app, clone, "随软件启动");
            }
    }

    /// <summary>主计时器每 1.5s 调一次：到点且今天未触发过的分身，静默拉起（同一天只触发一次）。</summary>
    private void CheckScheduledLaunches()
    {
        if (IsDisposed) return;
        var now = DateTime.Now;
        foreach (var app in _store.Model.Apps)
            foreach (var clone in _store.ClonesOf(app.Id))
            {
                if (string.IsNullOrWhiteSpace(clone.AutoStartAt) || clone.Frozen) continue;
                var key = app.Id + "|" + clone.Id;
                var last = _scheduleFired.TryGetValue(key, out var t) ? t : (DateTime?)null;
                if (!AutoLaunchPlan.ShouldFire(clone.AutoStartAt, now, last)) continue;
                // 先记账再启动：启动失败也不能每 1.5 秒重试弹错
                _scheduleFired[key] = now;
                if (Launcher.IsRunning(app.Id, clone.Id)) continue;
                LaunchCloneSilent(app, clone, $"定时启动 {AutoLaunchPlan.Describe(clone.AutoStartAt)}");
            }
    }

    /// <summary>
    /// 无人值守启动（自启 / 定时）：跳过 Dry Run 确认，阻断项只在状态栏与日志里说明，不弹模态框。
    /// 冻结拦截在 <see cref="Launcher.Launch"/> 内部统一处理。
    /// </summary>
    private void LaunchCloneSilent(AppEntry app, CloneEntry clone, string reason)
    {
        var preflight = Preflight.Check(app, clone);
        if (Preflight.HasBlocking(preflight))
        {
            Log.Error("launch", $"{reason} 已跳过：{Preflight.Format(preflight).Replace('\n', ' ')}",
                app.Id, clone.Id, "preflight");
            SetStatus($"{reason}跳过：{app.Name} · {clone.Name}（预检查未通过）");
            return;
        }

        var outcome = Launcher.Launch(_store, app, clone);
        if (!outcome.Ok)
        {
            Log.Error("launch", $"{reason} 启动失败：" + outcome.Message, app.Id, clone.Id, "launch");
            SetStatus($"{reason}失败：{app.Name} · {clone.Name}");
            return;
        }

        Log.Info("launch", $"{reason} 已启动（PID {outcome.Pid}）", app.Id, clone.Id, "launch");
        CloneStats.RecordLaunch(app.Id, clone.Id);
        LaunchWatch.Watch(app.Id, clone.Id, app.ExePath, outcome.Pid);
        _runStart[$"{app.Id}|{clone.Id}"] = DateTime.Now;
        PushRecent(app, clone);
        RebuildTrayMenu();
        UpdateTrayCount();
        UpdateRunningStates();
        SetStatus($"{reason}：已启动 {app.Name} · {clone.Name}");
    }

    /// <summary>
    /// P2 崩溃守护：启动观察窗报「早退崩溃」时自动重启，最多 2 次；30 分钟内未再崩溃则重新计数。
    /// 重启走 Launcher 直启，跳过 Dry Run 确认 —— 无人值守时不能弹窗。
    /// </summary>
    private void OnCloneCrashed(string appId, string cloneId, int exitCode)
    {
        var app = _store.FindApp(appId);
        var clone = _store.FindClone(appId, cloneId);
        if (app == null || clone == null || !clone.Guard) return;

        var key = appId + "|" + cloneId;
        var now = DateTime.Now;
        int count = 0;
        if (_guardAttempts.TryGetValue(key, out var prev) && now - prev.At < TimeSpan.FromMinutes(30))
            count = prev.Count;

        if (count >= 2)
        {
            _guardAttempts.Remove(key);
            Log.Warn("guard", $"崩溃守护放弃：连续 {count} 次自动重启后仍启动失败（退出码 {exitCode}）", appId, cloneId, "guard");
            SetStatus($"崩溃守护已停止：{app.Name} · {clone.Name}（连续 {count} 次启动失败）");
            return;
        }

        _guardAttempts[key] = (++count, now);
        SetStatus($"崩溃守护：{app.Name} · {clone.Name} 启动异常，正在重启（第 {count} 次）");
        Log.Warn("guard", $"崩溃守护自动重启第 {count} 次（退出码 {exitCode}）", appId, cloneId, "guard");

        var outcome = Launcher.Launch(_store, app, clone);
        if (outcome.Ok)
            LaunchWatch.Watch(app.Id, clone.Id, app.ExePath, outcome.Pid);
        else
            Log.Error("guard", "自动重启失败：" + outcome.Message, app.Id, clone.Id, "guard");
        UpdateRunningStates();
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

    /// <summary>停止分身并记录统计（时长）。taskkill 放后台执行，完成后立即刷新运行状态。</summary>
    private async Task StopClone(AppEntry app, CloneEntry clone)
    {
        if (!Launcher.IsRunning(app.Id, clone.Id)) return;
        // F1：本次结束是用户主动发起的 → 观察窗跳过兼容性失败判定，避免误伤
        LaunchWatch.MarkUserStopped(app.Id, clone.Id);
        Log.Info("launch", "用户结束分身进程", app.Id, clone.Id, "stop");
        string key = $"{app.Id}|{clone.Id}";
        await Task.Run(() => Launcher.Stop(app.Id, clone.Id));
        if (_runStart.TryGetValue(key, out var t))
        {
            CloneStats.RecordStop(app.Id, clone.Id, DateTime.Now - t);
            _runStart.Remove(key);
        }
        else
        {
            CloneStats.RecordStop(app.Id, clone.Id, TimeSpan.Zero);
        }
        UpdateRunningStates();   // 状态点/文字/头像/按钮/强调条即时同步，不等轮询
        UpdateTrayCount();
    }

    private async void EndAllClones()
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
                if (Launcher.IsRunning(app.Id, c.Id)) await StopClone(app, c);
        RefreshAll();
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
        RefreshAll();
    }

    private async void BatchEndRunning()
    {
        int ended = 0;
        foreach (var id in _appSelected.ToArray())
        {
            var app = _store.FindApp(id);
            if (app == null) continue;
            foreach (var c in _store.ClonesOf(id))
                if (Launcher.IsRunning(id, c.Id)) { await StopClone(app, c); ended++; }
        }
        SetStatus($"已结束 {ended} 个运行中的分身");
        RefreshAll();
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

        // P3：冻结 —— 禁止启动；若正在运行则先结束进程
        var freezeItem = new ToolStripMenuItem("冻结分身（禁止启动）")
        { Checked = clone.Frozen, CheckOnClick = true };
        freezeItem.Click += async (_, _) =>
        {
            clone.Frozen = freezeItem.Checked;
            _store.Save();
            if (clone.Frozen && Launcher.IsRunning(app.Id, clone.Id))
            {
                try { await StopClone(app, clone); } catch { }
            }
            RefreshRightPane();
            SetStatus(clone.Frozen ? $"已冻结 {clone.Name}" : $"已解冻 {clone.Name}");
        };
        menu.Items.Add(freezeItem);

        // P4：随软件启动自动运行（冻结的分身即使勾选也不会启动，RunAutoStarts 统一跳过）
        var autoStartItem = new ToolStripMenuItem("随软件启动时自动运行")
        { Checked = clone.AutoStart, CheckOnClick = true };
        autoStartItem.Click += (_, _) =>
        {
            clone.AutoStart = autoStartItem.Checked;
            _store.Save();
            SetStatus(clone.AutoStart ? $"已设为随软件启动：{clone.Name}" : $"已取消随软件启动：{clone.Name}");
        };
        menu.Items.Add(autoStartItem);

        // P4：每天定时启动（依赖本软件常驻运行；HH:mm）
        var schedule = AutoLaunchPlan.Describe(clone.AutoStartAt);
        var scheduleItem = new ToolStripMenuItem(schedule.Length > 0
            ? $"每天定时启动：{schedule}（点击修改）" : "设置每天定时启动…");
        scheduleItem.Click += (_, _) =>
        {
            var v = PromptText("每天定时启动", "输入时间 HH:mm（如 08:30）：", clone.AutoStartAt);
            if (v == null) return;   // 取消输入
            if (AutoLaunchPlan.ParseTime(v) == null)
            {
                MessageBox.Show(this, "时间格式不对，请用 24 小时制 HH:mm，例如 08:30。", "定时启动",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            clone.AutoStartAt = v.Trim();
            _store.Save();
            SetStatus($"已设置每天 {clone.AutoStartAt} 启动 {clone.Name}（需本软件保持运行）");
        };
        menu.Items.Add(scheduleItem);
        if (schedule.Length > 0)
            menu.Items.Add("取消定时启动", null, (_, _) =>
            {
                clone.AutoStartAt = "";
                _store.Save();
                SetStatus($"已取消定时启动：{clone.Name}");
            });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) =>
        {
            Directory.CreateDirectory(Paths.CloneDir(app.Id, clone.Id));
            OpenFolder(Paths.CloneDir(app.Id, clone.Id));
        });
        menu.Items.Add("创建桌面快捷方式", null, (_, _) => CreateShortcut(app, clone));

        // P2：克隆分身 —— 复制隔离/颜色/伪装设置，并后台复制数据目录（大目录不卡界面）
        menu.Items.Add("克隆此分身（含数据）", null, (_, _) =>
        {
            var dup = new CloneEntry
            {
                Name = clone.Name + " 副本",
                DeepIsolation = clone.DeepIsolation,
                DeviceClone = clone.DeviceClone,
                Color = clone.Color,
                Disguise = clone.Disguise,
                Guard = clone.Guard,
            };
            _store.ClonesOf(app.Id).Add(dup);
            _store.Save();
            RefreshAppList();
            RefreshRightPane();
            SetStatus($"正在复制分身「{dup.Name}」的数据…");
            var appId = app.Id;
            var srcId = clone.Id;
            Task.Run(() =>
            {
                ConfigIo.CopyCloneData(appId, srcId, appId, dup.Id);
                if (IsHandleCreated) BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed) SetStatus($"已克隆分身「{dup.Name}」");
                }));
            });
        });

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

        // P1：快捷方式伪装 —— 桌面图标与名字装成系统普通程序，重新创建快捷方式后生效
        var disguiseMenu = new ToolStripMenuItem("快捷方式伪装");
        foreach (var preset in Disguise.All)
        {
            var key = preset.Key;
            var item = new ToolStripMenuItem(preset.Key.Length == 0 ? "不伪装" : preset.Name) { Tag = key };
            item.Checked = clone.Disguise == key;
            item.Click += (_, _) =>
            {
                clone.Disguise = key; _store.Save(); RefreshRightPane();
                SetStatus(key.Length == 0
                    ? "已取消伪装（重新创建快捷方式后生效）"
                    : $"已伪装为「{preset.Name}」（重新创建快捷方式后生效）");
            };
            disguiseMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(disguiseMenu);

        // P3：网络代理 —— 每个分身可走不同出口（标准代理环境变量）
        var proxyMenu = new ToolStripMenuItem(string.IsNullOrWhiteSpace(clone.Proxy)
            ? "网络代理：不使用" : $"网络代理：{clone.Proxy}");
        proxyMenu.DropDownItems.Add("设置代理…", null, (_, _) =>
        {
            var v = PromptText("网络代理", "输入代理地址（如 http://127.0.0.1:7890）", clone.Proxy);
            if (v == null) return;
            if (!v.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !v.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !v.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("代理地址需以 http:// 或 socks5:// 开头");
                return;
            }
            clone.Proxy = v; _store.Save();
            SetStatus($"已设置代理：{v}");
        });
        var noProxy = new ToolStripMenuItem("不使用代理") { Checked = string.IsNullOrWhiteSpace(clone.Proxy) };
        noProxy.Click += (_, _) => { clone.Proxy = ""; _store.Save(); SetStatus("已取消网络代理"); };
        proxyMenu.DropDownItems.Add(noProxy);
        menu.Items.Add(proxyMenu);

        // P2：崩溃守护开关（默认关，用户按需开启）
        var guardItem = new ToolStripMenuItem("崩溃守护（启动失败自动重启）")
        { Checked = clone.Guard, CheckOnClick = true };
        guardItem.Click += (_, _) =>
        {
            clone.Guard = guardItem.Checked; _store.Save(); RefreshRightPane();
            SetStatus(clone.Guard ? "已开启崩溃守护" : "已关闭崩溃守护");
        };
        menu.Items.Add(guardItem);

        // F1：手动标记兼容性 —— 优先级高于自学习结果（用户说了算）
        var currentCompat = CompatibilityDb.Resolve(app, out var compatNote);
        var compatMenu = new ToolStripMenuItem($"兼容性标记：{CompatibilityDb.Label(currentCompat)}");
        if (compatNote.Length > 0) compatMenu.ToolTipText = compatNote;
        var compatChoices = new[]
        {
            (CompatLevel.Supported, "✓ 可用"),
            (CompatLevel.Partial, "⚠ 部分可用"),
            (CompatLevel.Unsupported, "✗ 不支持"),
            (CompatLevel.Unknown, "— 未测试"),
        };
        foreach (var (lvl, text) in compatChoices)
        {
            var l = lvl;
            var item = new ToolStripMenuItem(text) { Checked = currentCompat == l };
            item.Click += (_, _) =>
            {
                CompatibilityDb.SetManual(app.ExePath, l);
                RefreshAll();
                SetStatus($"{app.Name} 兼容性已标记为「{text}」");
            };
            compatMenu.DropDownItems.Add(item);
        }
        compatMenu.DropDownItems.Add(new ToolStripSeparator());
        var compatReset = new ToolStripMenuItem("恢复自动判定（清除标记与实测记录）");
        compatReset.Click += (_, _) =>
        {
            CompatibilityDb.Reset(app.ExePath);
            RefreshAll();
            SetStatus("已恢复兼容性自动判定");
        };
        compatMenu.DropDownItems.Add(compatReset);
        menu.Items.Add(compatMenu);

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
        menu.Items.Add("结束进程", null, async (_, _) => { await StopClone(app, clone); SetStatus("已结束分身进程"); });

        // R10：重置分身数据（先存快照）
        var resetSnap = menu.Items.Add("重置分身数据（先存快照）");
        resetSnap.Click += async (_, _) => await ResetWithSnapshot(app, clone);

        // R10：管理快照
        var manage = menu.Items.Add("管理快照…");
        manage.Click += (_, _) =>
        {
            using var sf = new SnapshotsForm(app, clone);
            sf.ShowDialog(this);
            RefreshAll();
        };

        var del = new ToolStripMenuItem("删除分身") { ForeColor = Ui.DangerOf() };
        del.Click += async (_, _) => await DeleteClone(app, clone);
        menu.Items.Add(del);

        menu.Show(anchor, new Point(0, anchor.Height));
    }

    /// <summary>
    /// 删除分身（含数据目录与注册表私有键）。抽成公共方法供分身菜单与管理中心共用，
    /// 避免两处各写一份确认文案与错误处理而漂移。
    /// </summary>
    private async Task DeleteClone(AppEntry app, CloneEntry clone)
    {
        if (Settings.Current.ConfirmDestructive
            && MessageBox.Show(this, $"确定删除「{clone.Name}」吗？\n该分身的全部数据将被永久删除。",
                "删除分身", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        SetStatus("正在删除分身…");
        Log.Rollback("ui", $"用户确认删除分身「{clone.Name}」（应用 {app.Name}）", app.Id, clone.Id);
        try { await StopClone(app, clone); } catch { }   // 停止失败不阻断删除
        try { _store.RemoveClone(app.Id, clone.Id); }
        catch (Exception ex)
        {
            Log.Error("ui", $"删除分身失败：{ex.Message}", app.Id, clone.Id, "delete", ex);
            MessageBox.Show(this, $"删除失败：{ex.Message}", "删除分身", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        RefreshAll();
        SetStatus($"已删除 {clone.Name}");
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

    private async Task ResetWithSnapshot(AppEntry app, CloneEntry clone)
    {
        if (_busy) return;   // C9③：重入保护（异步流程进行中忽略重复点击）
        if (Settings.Current.ConfirmDestructive
            && MessageBox.Show(this, $"确定重置「{clone.Name}」吗？\n将先自动保存一份快照，再清空分身数据。",
                "重置分身（先存快照）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        // F5：重置 = 快照 + 清空，属破坏性操作，先留痕再动手
        Log.Rollback("ui", $"用户确认重置分身数据「{clone.Name}」（先存快照后清空）", app.Id, clone.Id);
        _busy = true;
        try
        {
            SetStatus("正在保存快照…");
            try { await StopClone(app, clone); } catch { }
            var (ok, msg, _) = await Task.Run(() => Snapshot.Create(app.Id, clone.Id));
            if (ok) SetStatus(msg);
            else MessageBox.Show(this, $"快照失败：{msg}\n仍将继续重置。", "快照", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            try { Directory.Delete(Paths.CloneDir(app.Id, clone.Id), true); } catch { }
            Directory.CreateDirectory(Paths.CloneHome(app.Id, clone.Id));
            if (clone.DeepIsolation) RegClone.ResetCloneRegistry(app.Id, clone.Id);
            RefreshAll();
            SetStatus("分身数据已重置（已存快照）");
        }
        finally
        {
            _busy = false;
        }
    }

    private void CreateShortcut(AppEntry app, CloneEntry clone)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string self = Application.ExecutablePath;
            int index = Math.Max(1, _store.ClonesOf(app.Id).FindIndex(c => c.Id == clone.Id) + 1);

            // P1：伪装优先 —— 快捷方式的名字、图标、悬停说明都换成系统普通程序，桌面上一眼看不出是分身
            string lnkName = $"{app.Name} - {clone.Name}";
            string desc = $"启动 {app.Name} · {clone.Name}";
            string icon = app.ExePath;
            var preset = Disguise.Get(clone.Disguise);
            if (preset != null)
            {
                lnkName = $"{preset.Name} {index}";
                desc = preset.Name;
                icon = Disguise.IconPath(preset.Key) ?? icon;
            }
            else if (Settings.Current.BadgeShortcutIcon)
            {
                // F6：桌面图标带序号角标。没有它，桌面上并排的「微信 - 分身 1/2/3」三个图标
                // 长得一模一样，用户只能靠文件名去猜 —— 这是感知最强、成本最低的一处优化。
                var badged = BadgeIcon.Ensure(clone.Id, index, clone.Color, app.ExePath);
                if (badged != null) icon = badged;
                else Log.Warn("shortcut", "角标图标生成失败，已回退为应用原始图标", app.Id, clone.Id);
            }

            string lnk = Path.Combine(desktop, lnkName + ".lnk");
            var link = new AppClone.Shared.NativeLink
            {
                Target = self, Arguments = $"--launch {app.Id} {clone.Id}",
                WorkingDirectory = Path.GetDirectoryName(self),
                IconPath = icon,
                Description = desc,
            };
            // F6 实验性：写入 AppUserModelID 以便任务栏分组（默认关；失败静默，不影响快捷方式本身）
            if (Settings.Current.TaskbarGrouping)
                link.AppUserModelId = $"AppClone.{app.Id}.{clone.Id}";
            link.Save(lnk);

            Log.Info("shortcut", $"已创建桌面快捷方式 {lnk}（图标：{(icon == app.ExePath ? "应用原始图标" : preset != null ? "伪装图标" : "分身角标图标")}）",
                app.Id, clone.Id, "shortcut");
            SetStatus($"已在桌面创建快捷方式：{lnkName}");
        }
        catch (Exception ex)
        {
            Log.Error("shortcut", "创建快捷方式失败：" + ex.Message, app.Id, clone.Id, "shortcut", ex);
            MessageBox.Show(this, ex.Message, "创建快捷方式失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ------------------------------------------------------------------ F1 兼容性徽章

    /// <summary>生成兼容性徽章（四态）；配色走主题访问器，深浅色主题自适应。</summary>
    private static PillChip MakeCompatChip(AppEntry app)
    {
        var level = CompatibilityDb.Resolve(app, out _);
        return new PillChip(CompatibilityDb.Label(level), CompatFill(level), CompatText(level));
    }

    private static Color CompatFill(CompatLevel level) => level switch
    {
        CompatLevel.Supported => Ui.OkLightOf(),
        CompatLevel.Partial => Ui.WarnLightOf(),
        CompatLevel.Unsupported => Ui.DangerLightOf(),
        _ => Ui.FillGrayOf(),
    };

    private static Color CompatText(CompatLevel level) => level switch
    {
        CompatLevel.Supported => Ui.OkOf(),
        CompatLevel.Partial => Ui.WarnOf(),
        CompatLevel.Unsupported => Ui.DangerOf(),
        _ => Ui.TextSecondaryOf(),
    };

    // ------------------------------------------------------------------ F2 源应用监视

    /// <summary>F2：为老数据补记源应用基线（静默，绝不提示 —— 缺基线不等于升级）。</summary>
    private void BackfillSourceBaselines()
    {
        bool changed = false;
        foreach (var app in _store.Model.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.ExePath)) continue;
            foreach (var c in _store.ClonesOf(app.Id))
                if (!SourceWatch.HasBaseline(c)) { SourceWatch.Stamp(app, c); changed = true; }
        }
        if (changed)
        {
            try { _store.Save(); } catch { }
        }
    }

    /// <summary>F2：启动后台巡检全部应用 —— 找出已升级 / 文件被替换 / 路径丢失者，状态栏提示一次。</summary>
    private void SweepSourceChanges()
    {
        Task.Run(() =>
        {
            var notes = new List<string>();
            List<AppEntry> apps;
            try { apps = _store.Model.Apps.ToList(); } catch { return; }

            foreach (var app in apps)
            {
                try
                {
                    var kind = SourceWatch.Check(app, _store.ClonesOf(app.Id), out _, out _, out var reason);
                    if (kind is SourceChangeKind.VersionUpgraded
                             or SourceChangeKind.ExeReplaced
                             or SourceChangeKind.PathMissing)
                        notes.Add($"{app.Name}（{reason}）");
                }
                catch { }
            }
            if (notes.Count == 0 || !IsHandleCreated) return;

            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    SetStatus($"检测到 {notes.Count} 个源应用变化：{notes[0]}"
                              + (notes.Count > 1 ? $" 等 {notes.Count} 项" : ""));
                    RefreshRightPane();   // 让升级提示条亮起来
                }));
            }
            catch { }
        });
    }

    /// <summary>F2：按当前所选应用刷新升级提示条（无偏离则不显示）。</summary>
    private void UpdateUpgradeBar(AppEntry? app)
    {
        if (_upgradeBar is null || _upgradeText is null) return;   // 布局尚未搭建

        if (app == null) { _upgradeBar.Visible = false; return; }

        var kind = SourceWatch.Check(app, _store.ClonesOf(app.Id), out var baseline, out var current, out var reason);
        if (kind is SourceChangeKind.None or SourceChangeKind.Unknown) { _upgradeBar.Visible = false; return; }

        _upgradeText.Text = kind switch
        {
            SourceChangeKind.VersionUpgraded =>
                $"源应用已升级：{baseline} → {current}。当前分身仍基于旧版本建立，建议重建分身数据。",
            SourceChangeKind.ExeReplaced =>
                "源程序文件已被替换（版本号未变）。建议重建分身数据，避免新旧行为混用。",
            _ => $"{reason}。该应用的克隆记录可能已失效。",
        };

        bool hasClones = _store.ClonesOf(app.Id).Count > 0;
        _btnUpgradeStamp.Enabled = hasClones;
        _btnUpgradeRebuild.Enabled = hasClones;
        _upgradeBar.Visible = true;
    }

    /// <summary>F2：仅把基线刷新为当前源版本，不动任何分身数据。</summary>
    private void StampSourceRecord(AppEntry app)
    {
        var clones = _store.ClonesOf(app.Id);
        if (clones.Count == 0) { SetStatus("该应用还没有分身"); return; }

        foreach (var c in clones) SourceWatch.Stamp(app, c);
        var current = SourceWatch.CaptureVersion(app);
        if (current.Length > 0) app.Version = current;
        _store.Save();
        RefreshAll();
        SetStatus($"已更新 {app.Name} 的源版本基线（分身数据未改动）");
    }

    /// <summary>
    /// F2：重建分身数据 —— 先存快照再清空；分身身份（名称 / 角标色 / 热键 / 设备指纹）全部保留。
    /// 与「重新复制应用」无关：本项目不复制应用本体，重建只是让分身带着新基线重新开始。
    /// </summary>
    private async Task RebuildForSource(AppEntry app)
    {
        if (_busy) return;   // 与 NewCloneFlow / ResetWithSnapshot 共用重入保护
        var clones = _store.ClonesOf(app.Id).ToList();
        if (clones.Count == 0) { SetStatus("该应用还没有分身"); return; }

        if (Settings.Current.ConfirmDestructive &&
            MessageBox.Show(DialogOwner(),
                $"确定重建「{app.Name}」的 {clones.Count} 个分身数据吗？\n\n" +
                "· 每个分身会先自动保存一份快照\n" +
                "· 分身名称、角标颜色、热键与设备身份保持不变\n" +
                "· 分身的登录状态与本地数据将被清空",
                "重建分身数据", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        // F5：重建会清空每个分身的数据，属破坏性操作，逐个留痕
        Log.Rollback("ui", $"用户确认重建 {clones.Count} 个分身数据（源应用 {app.Name}）", app.Id);

        _busy = true;
        try
        {
            int done = 0;
            foreach (var clone in clones)
            {
                SetStatus($"正在重建 {clone.Name} …（{done + 1}/{clones.Count}）");
                try { await StopClone(app, clone); } catch { }
                try { await Task.Run(() => Snapshot.Create(app.Id, clone.Id)); } catch { }
                try { Directory.Delete(Paths.CloneDir(app.Id, clone.Id), true); } catch { }
                try { Directory.CreateDirectory(Paths.CloneHome(app.Id, clone.Id)); } catch { }
                try { if (clone.DeepIsolation) RegClone.ResetCloneRegistry(app.Id, clone.Id); } catch { }
                SourceWatch.Stamp(app, clone);   // 基线推进到当前源版本
                Log.Rollback("ui", $"分身「{clone.Name}」数据已重建并推进源基线", app.Id, clone.Id);
                done++;
            }
            var current = SourceWatch.CaptureVersion(app);
            if (current.Length > 0) app.Version = current;
            _store.Save();
            RefreshAll();
            SetStatus($"已重建 {done} 个分身，源版本基线已更新");
        }
        finally { _busy = false; }
    }

    // ------------------------------------------------------------------ F3 预检查 / Dry Run

    /// <summary>F3：按设置展示 Dry Run 预览；返回 false 表示用户取消了本次启动。</summary>
    private bool ConfirmDryRun(AppEntry app, CloneEntry clone, List<PreflightItem> items)
    {
        if (!Settings.Current.ShowDryRun) return true;

        Launcher.LaunchPlan plan;
        try { plan = Launcher.Describe(app, clone); }
        catch { return true; }   // 预览生成失败不应反过来阻断启动

        using var dlg = new DryRunForm(app, clone, items, plan);
        bool ok = dlg.ShowDialog(DialogOwner()) == DialogResult.OK;
        if (ok && dlg.DontAskAgain)
        {
            Settings.Current.ShowDryRun = false;
            Settings.Current.Save();
            SetStatus("已关闭启动预览（可在设置中重新开启）");
        }
        return ok;
    }

    // ------------------------------------------------------------------ F4 管理中心 / F5 日志

    /// <summary>
    /// 打开分身管理中心（F4）。动作全部以委托回传，本方法仍是编排的唯一归属地 ——
    /// 启动链路带着预检查、Dry Run、观察窗、统计与托盘刷新一整套副作用，不能复制到别的窗体里去。
    /// </summary>
    private void OpenCloneManager()
    {
        using var f = new CloneManagerForm(_store);
        f.LaunchRequested = (app, clone) =>
        {
            if (Launcher.IsRunning(app.Id, clone.Id)) return false;
            LaunchClone(app, clone);
            return true;
        };
        f.StopRequested = async (app, clone) => { await StopClone(app, clone); UpdateRunningStates(); };
        f.RebuildRequested = async (app, clone) => { await RebuildSingle(app, clone); RefreshAll(); };
        f.DeleteRequested = async (app, clone) => { await DeleteClone(app, clone); };
        f.OpenFolderRequested = (app, clone) =>
        {
            Directory.CreateDirectory(Paths.CloneDir(app.Id, clone.Id));
            OpenFolder(Paths.CloneDir(app.Id, clone.Id));
        };
        f.ViewLogRequested = OpenLogViewer;
        f.ExportDiagnosticsRequested = ExportDiagnostics;

        f.ShowDialog(this);
        RefreshAll();
        SetStatus("已关闭分身管理中心");
    }

    /// <summary>打开操作日志查看器（F5）。app/clone 为 null 时展示全量流水。</summary>
    private void OpenLogViewer(AppEntry? app, CloneEntry? clone)
    {
        using var f = new LogViewerForm(app, clone);
        f.ExportDiagnosticsRequested = ExportDiagnostics;
        f.ShowDialog(this);
    }

    /// <summary>导出诊断包（F5）：日志 + 配置，用于排查问题。不含 profiles / snapshots。</summary>
    private void ExportDiagnostics()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出诊断包", Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"AppClone-Diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip", AddExtension = true,
        };
        if (dlg.ShowDialog(DialogOwner()) != DialogResult.OK) return;
        var (ok, msg) = Log.ExportDiagnostics(dlg.FileName);
        MessageBox.Show(DialogOwner(), msg, ok ? "导出诊断包" : "导出失败", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        SetStatus(ok ? "已导出诊断包" : "导出诊断包失败");
    }

    /// <summary>
    /// 重建**单个**分身的数据（管理中心触发）：先存快照 → 清空 → 推进源基线。
    /// 与 <see cref="RebuildForSource"/>（整个应用范围）共享同一套语义，分身身份保持不变。
    /// </summary>
    private async Task RebuildSingle(AppEntry app, CloneEntry clone)
    {
        if (_busy) return;
        if (Settings.Current.ConfirmDestructive &&
            MessageBox.Show(DialogOwner(),
                $"确定重建「{clone.Name}」的数据吗？\n\n" +
                "· 会先自动保存一份快照\n" +
                "· 分身名称、角标颜色、热键与设备身份保持不变\n" +
                "· 分身的登录状态与本地数据将被清空",
                "重建分身数据", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _busy = true;
        try
        {
            Log.Rollback("ui", $"用户确认重建分身数据「{clone.Name}」（先存快照后清空）", app.Id, clone.Id);
            SetStatus($"正在重建 {clone.Name} …");
            try { await StopClone(app, clone); } catch { }
            try { await Task.Run(() => Snapshot.Create(app.Id, clone.Id)); } catch { }
            try { Directory.Delete(Paths.CloneDir(app.Id, clone.Id), true); } catch { }
            try { Directory.CreateDirectory(Paths.CloneHome(app.Id, clone.Id)); } catch { }
            try { if (clone.DeepIsolation) RegClone.ResetCloneRegistry(app.Id, clone.Id); } catch { }
            SourceWatch.Stamp(app, clone);
            var current = SourceWatch.CaptureVersion(app);
            if (current.Length > 0) app.Version = current;
            _store.Save();
            SetStatus($"已重建 {clone.Name} 的数据（源基线已推进）");
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// 弹窗宿主：模态子窗口打开时用子窗口，否则用主窗体。
    /// 否则询问框会挂在被禁用的主窗体上，视觉上被压在管理中心后面，用户以为「点了没反应」。
    /// </summary>
    private IWin32Window DialogOwner()
    {
        var active = Form.ActiveForm;
        if (active != null && !ReferenceEquals(active, this) && active.Visible) return active;
        return this;
    }

    // ------------------------------------------------------------------ 通用弹窗 / 工具

    private string? PromptText(string title, string prompt, string current)
    {
        var dlg = new Form
        {
            Width = 380, Height = 180, Text = title, StartPosition = FormStartPosition.CenterParent,
            BackColor = Ui.BgOf(), Font = Ui.Font(Ui.FBody), FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
        };
        // 独立窗体：内部坐标是 96 DPI 逻辑值，交给 AutoScale 在加载时整树放大（否则高 DPI 下被裁）
        dlg.AutoScaleMode = AutoScaleMode.Dpi;
        dlg.AutoScaleDimensions = new SizeF(96F, 96F);
        dlg.ClientSize = new Size(380, 148);   // 用客户区定尺寸，按钮坐标按客户区推算，任何 DPI 下都不被裁
        var lbl = Ui.Label(prompt, Ui.FBody, false, Ui.TextSecondaryOf());
        lbl.AutoSize = false;
        lbl.Location = new Point(20, 18);
        lbl.Size = new Size(340, 40);
        var tb = new TextBox { Text = current, Location = new Point(20, 56), Width = 340, Font = Ui.Font(10F) };
        var ok = new AppleButton("确定", AppleButton.Style.Filled) { Size = new Size(96, 34), Location = new Point(160, 104) };
        var cancel = new AppleButton("取消", AppleButton.Style.Secondary) { Size = new Size(96, 34), Location = new Point(264, 104) };
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text.Trim(); dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
        dlg.Controls.Add(lbl); dlg.Controls.Add(tb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok; dlg.CancelButton = cancel;
        return dlg.ShowDialog(this) == DialogResult.OK ? (string.IsNullOrWhiteSpace(result) ? null : result) : null;
    }

    private string CountText(string appId) => $"{_store.ClonesOf(appId).Count} 个分身";

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
