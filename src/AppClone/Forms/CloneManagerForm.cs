using System.Reflection;
using System.Windows.Forms;
using AppClone;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 分身管理中心（F4）：把散落在各应用卡片里的分身汇总成一张跨应用总览表。
///
/// 设计要点：本窗体**只负责展示与收集意图**，所有动作（启动 / 停止 / 重建 / 删除）都通过委托
/// 回抛给 <see cref="MainForm"/> 执行。原因是启动链路里带着预检查、Dry Run 预览、启动观察窗、
/// 统计记时、托盘刷新等一整套编排 —— 若在管理窗体里各写一份，两边迟早漂移。
/// 数据装配（该出现哪些行、每列显示什么）则下沉到 <see cref="CloneOverview"/>，那部分可被探针断言。
/// </summary>
public sealed class CloneManagerForm : Form
{
    private readonly Store _store;

    // —— 动作委托：由 MainForm 注入（保持 UI 编排单点）——
    public Func<AppEntry, CloneEntry, bool>? LaunchRequested;
    public Func<AppEntry, CloneEntry, Task>? StopRequested;
    public Func<AppEntry, CloneEntry, Task>? RebuildRequested;
    public Func<AppEntry, CloneEntry, Task>? DeleteRequested;
    public Action<AppEntry, CloneEntry>? OpenFolderRequested;
    public Action<AppEntry, CloneEntry>? ViewLogRequested;
    public Action? ExportDiagnosticsRequested;

    private ListView _list = null!;
    private ComboBox _appFilter = null!;
    private ComboBox _stateFilter = null!;
    private ComboBox _compatFilter = null!;
    private TextBox _search = null!;
    private Label _summary = null!;
    private AppleButton _btnLaunch = null!;
    private AppleButton _btnStop = null!;
    private AppleButton _btnFolder = null!;
    private AppleButton _btnRebuild = null!;
    private AppleButton _btnLog = null!;
    private AppleButton _btnDelete = null!;
    private System.Windows.Forms.Timer _poll = null!;
    private bool _busy;

    private const string SearchHint = "搜索应用 / 分身…";

    public CloneManagerForm(Store store)
    {
        _store = store;
        Text = "分身管理中心";
        Size = new Size(1080, 660);
        MinimumSize = new Size(880, 480);
        StartPosition = FormStartPosition.CenterParent;
        // PerMonitorV2：构造期逻辑像素交给 AutoScale 整树放大（高 DPI 下文本被裁的修复）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = MainForm.TryAppIcon();
        ShowInTaskbar = false;

        BuildFilterBar();
        BuildList();
        BuildActionBar();

        Shown += (_, _) =>
        {
            Ui.RoundWindow(this);
            FadeAnimator.FadeIn(this);
            RefreshList();
        };
        FormClosed += (_, _) => { try { _poll.Stop(); _poll.Dispose(); } catch { } };
    }

    // ------------------------------------------------------------------ 布局

    private void BuildFilterBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 88, BackColor = Ui.BgOf(), Padding = new Padding(20, 14, 20, 8) };

        var cap = Ui.Label("全部应用的分身一览（跨应用汇总）", Ui.FSection, true, Ui.TextPrimaryOf());
        cap.Location = new Point(20, 10);
        bar.Controls.Add(cap);

        _appFilter = MakeCombo(240, new Point(20, 42));
        // 必须订阅**具名**处理器：RefreshList 内部靠 `-= OnAppFilterChanged` / `+=` 来临时退订，
        // 若这里订的是匿名 lambda，退订就退不掉，而 RefreshList 重建选项又会改动 SelectedIndex
        // → 再次触发本事件 → 无限递归直到栈溢出（管理中心一打开就闪退）。
        _appFilter.SelectedIndexChanged += OnAppFilterChanged;

        _stateFilter = MakeCombo(110, new Point(272, 42));
        _stateFilter.Items.AddRange(new object[] { "全部状态", "运行中", "未运行" });
        _stateFilter.SelectedIndex = 0;
        _stateFilter.SelectedIndexChanged += (_, _) => RefreshList();

        _compatFilter = MakeCombo(130, new Point(394, 42));
        _compatFilter.Items.AddRange(new object[] { "全部兼容性", "✓ 可用", "⚠ 部分", "✗ 不支持", "— 未测试" });
        _compatFilter.SelectedIndex = 0;
        _compatFilter.SelectedIndexChanged += (_, _) => RefreshList();

        _search = new TextBox
        {
            Size = new Size(220, 28), Location = new Point(536, 44), Font = Ui.Font(Ui.FBody),
            ForeColor = Ui.TextSecondaryOf(), BackColor = Ui.CardOf(), Text = SearchHint,
        };
        _search.GotFocus += (_, _) =>
        {
            if (_search.Text == SearchHint) { _search.Text = ""; _search.ForeColor = Ui.TextPrimaryOf(); }
        };
        _search.LostFocus += (_, _) =>
        {
            if (_search.Text.Length == 0) { _search.Text = SearchHint; _search.ForeColor = Ui.TextSecondaryOf(); }
        };
        _search.TextChanged += (_, _) => { if (_search.Text != SearchHint) RefreshList(); };
        bar.Controls.Add(_search);

        var btnRefresh = new AppleButton("刷新", AppleButton.Style.Secondary, 9F)
        { Size = new Size(72, 28), Location = new Point(770, 44) };
        btnRefresh.Click += (_, _) => RefreshList();
        bar.Controls.Add(btnRefresh);

        bar.Controls.Add(_appFilter);
        bar.Controls.Add(_stateFilter);
        bar.Controls.Add(_compatFilter);
        Controls.Add(bar);
    }

    private static ComboBox MakeCombo(int width, Point at) => new()
    {
        Size = new Size(width, 28), Location = at, DropDownStyle = ComboBoxStyle.DropDownList,
        Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextPrimaryOf(), BackColor = Ui.CardOf(),
    };

    private void BuildList()
    {
        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, GridLines = false, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Ui.CardOf(), ForeColor = Ui.TextPrimaryOf(), Font = Ui.Font(9F),
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        // ListView 默认双缓冲关闭，刷新时会闪；该属性是 protected，用反射打开（WinForms 常规做法）
        try
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(_list, true);
        }
        catch { }

        // 列宽不走控件缩放（ColumnHeader 不是 Control），必须按 DPI 手动换算，
        // 否则 150% 下中文字体放大、列宽不变 → 每一格都被裁
        int S(int v) => Ui.S(this, v);
        _list.Columns.Add("应用", S(150));
        _list.Columns.Add("分身", S(110));
        _list.Columns.Add("隔离", S(90));
        _list.Columns.Add("设备分身", S(74));
        _list.Columns.Add("兼容性", S(88));
        _list.Columns.Add("创建于", S(88));
        _list.Columns.Add("启动次数", S(72));
        _list.Columns.Add("累计时长", S(96));
        _list.Columns.Add("最后启动", S(124));
        _list.Columns.Add("状态", S(66));

        _list.SelectedIndexChanged += (_, _) => UpdateActions();
        _list.DoubleClick += (_, _) => DoLaunch();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoLaunch(); } };

        var menu = new ContextMenuStrip { Font = Ui.Font(Ui.FBody) };
        menu.Items.Add("启动分身", null, (_, _) => DoLaunch());
        menu.Items.Add("结束进程", null, async (_, _) => await DoStop());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) => DoFolder());
        menu.Items.Add("重建分身数据（先存快照）", null, async (_, _) => await DoRebuild());
        menu.Items.Add("查看该分身日志", null, (_, _) => DoViewLog());
        menu.Items.Add(new ToolStripSeparator());
        var del = new ToolStripMenuItem("删除分身") { ForeColor = Ui.DangerOf() };
        del.Click += async (_, _) => await DoDelete();
        menu.Items.Add(del);
        _list.ContextMenuStrip = menu;

        _poll = new System.Windows.Forms.Timer { Interval = 2000 };
        _poll.Tick += (_, _) => UpdateStatusColumn();
        _poll.Start();

        _summary = Ui.Label("", Ui.FCaption, false, Ui.TextSecondaryOf());
        _summary.Dock = DockStyle.Bottom;
        _summary.Height = 22;
        _summary.Padding = new Padding(22, 0, 0, 0);
        _summary.TextAlign = ContentAlignment.MiddleLeft;

        // 停靠顺序：Fill 必须先加入（最后停靠 → 拿剩余空间），Top / Bottom 随后
        Controls.Add(_list);
        Controls.Add(_summary);
    }

    private void BuildActionBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Ui.BgOf() };
        bar.Paint += (_, e) =>
        {
            using var p = new Pen(Ui.BorderOf(), 1F);
            e.Graphics.DrawLine(p, 0, 0, bar.Width, 0);
        };

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.BgOf(),
            Padding = new Padding(20, 12, 0, 12),
        };
        _btnLaunch = new AppleButton("启 动", AppleButton.Style.Filled, 9F) { Size = new Size(86, 34) };
        _btnLaunch.Click += (_, _) => DoLaunch();
        _btnStop = new AppleButton("结束进程", AppleButton.Style.Secondary, 9F) { Size = new Size(96, 34) };
        _btnStop.Click += async (_, _) => await DoStop();
        _btnFolder = new AppleButton("打开目录", AppleButton.Style.Secondary, 9F) { Size = new Size(96, 34) };
        _btnFolder.Click += (_, _) => DoFolder();
        _btnRebuild = new AppleButton("重建数据", AppleButton.Style.Secondary, 9F) { Size = new Size(96, 34) };
        _btnRebuild.Click += async (_, _) => await DoRebuild();
        _btnLog = new AppleButton("查看日志", AppleButton.Style.Secondary, 9F) { Size = new Size(96, 34) };
        _btnLog.Click += (_, _) => DoViewLog();
        foreach (var b in new[] { _btnLaunch, _btnStop, _btnFolder, _btnRebuild, _btnLog })
            b.Margin = new Padding(0, 0, 10, 0);
        left.Controls.Add(_btnLaunch);
        left.Controls.Add(_btnStop);
        left.Controls.Add(_btnFolder);
        left.Controls.Add(_btnRebuild);
        left.Controls.Add(_btnLog);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.BgOf(),
            Padding = new Padding(0, 12, 20, 12),
        };
        var btnClose = new AppleButton("关闭", AppleButton.Style.Secondary, 9F) { Size = new Size(84, 34) };
        btnClose.Click += (_, _) => Close();
        _btnDelete = new AppleButton("删除分身", AppleButton.Style.Ghost, 9F) { Size = new Size(96, 34) };
        _btnDelete.Click += async (_, _) => await DoDelete();
        var btnDiag = new AppleButton("导出诊断包", AppleButton.Style.Secondary, 9F) { Size = new Size(108, 34) };
        btnDiag.Click += (_, _) => ExportDiagnosticsRequested?.Invoke();
        btnClose.Margin = new Padding(10, 0, 0, 0);
        _btnDelete.Margin = new Padding(10, 0, 0, 0);
        right.Controls.Add(btnClose);
        right.Controls.Add(_btnDelete);
        right.Controls.Add(btnDiag);

        bar.Controls.Add(left);
        bar.Controls.Add(right);
        Controls.Add(bar);
    }

    // ------------------------------------------------------------------ 数据装载

    /// <summary>
    /// 重入闸门。本方法会重建「应用」下拉的选项，而重建过程中 SelectedIndex 的变动会回过头
    /// 触发 SelectedIndexChanged —— 一旦某条路径漏了退订，就会无限递归直到栈溢出（打开即闪退）。
    /// 这里兜底：任何重入直接返回，宁可少刷新一次也不能崩。
    /// </summary>
    private bool _refreshing;

    private void RefreshList()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { RefreshListCore(); }
        finally { _refreshing = false; }
    }

    private void RefreshListCore()
    {
        string keep = SelectedRow()?.Key ?? "";

        // 应用下拉：保留当前选择，内容随 Store 变化重建
        string keepApp = _appFilter.SelectedIndex > 0 ? Convert.ToString(_appFilter.SelectedItem) ?? "" : "";
        _appFilter.SelectedIndexChanged -= OnAppFilterChanged;
        _appFilter.Items.Clear();
        _appFilter.Items.Add("全部应用");
        foreach (var a in _store.Model.Apps) _appFilter.Items.Add(a.Name);
        _appFilter.SelectedIndex = Math.Max(0, _appFilter.Items.IndexOf(keepApp));
        _appFilter.SelectedIndexChanged += OnAppFilterChanged;

        _list.BeginUpdate();
        _list.Items.Clear();
        try
        {
            var all = CloneOverview.Build(_store);
            var shown = CloneOverview.Filter(all,
                _appFilter.SelectedIndex > 0 ? Convert.ToString(_appFilter.SelectedItem) : null,
                _stateFilter.SelectedIndex switch
                {
                    1 => RunStateFilter.Running,
                    2 => RunStateFilter.Stopped,
                    _ => RunStateFilter.All,
                },
                _compatFilter.SelectedIndex > 0
                    ? _compatFilter.SelectedIndex switch
                    {
                        1 => CompatLevel.Supported,
                        2 => CompatLevel.Partial,
                        3 => CompatLevel.Unsupported,
                        _ => CompatLevel.Unknown,
                    }
                    : null,
                _search.Text == SearchHint ? null : _search.Text);

            foreach (var row in shown) _list.Items.Add(MakeItem(row));
            _summary.Text = $"共 {all.Count} 个分身（{all.Count(r => r.Running)} 个运行中），当前显示 {shown.Count} 个";
        }
        catch { }
        finally { _list.EndUpdate(); }

        if (keep.Length > 0) RestoreSelection(keep);
        UpdateActions();
    }

    private void OnAppFilterChanged(object? s, EventArgs e) => RefreshList();

    private ListViewItem MakeItem(OverviewRow row)
    {
        var item = new ListViewItem(row.App.Name);
        item.SubItems.Add($"{row.Index}. {row.Clone.Name}");
        item.SubItems.Add(CloneOverview.IsolationText(row.Clone));
        item.SubItems.Add(CloneOverview.DeviceText(row.Clone));
        item.SubItems.Add(CompatibilityDb.Label(row.Compat));
        item.SubItems.Add(row.Clone.CreatedAt.ToString("yyyy-MM-dd"));
        item.SubItems.Add(row.Stats.LaunchCount.ToString());
        item.SubItems.Add(CloneOverview.FormatDuration(row.Stats.CumulativeSeconds));
        item.SubItems.Add(CloneOverview.LastLaunchText(row.Stats.LastLaunchedAt));
        item.SubItems.Add(CloneOverview.RunningText(row.Running));
        item.ForeColor = row.Running ? Ui.OkOf() : Ui.TextPrimaryOf();
        item.Tag = row;
        // 兼容性列与状态列单独着色，其余继承行色
        item.UseItemStyleForSubItems = false;
        item.SubItems[4].ForeColor = CompatColor(row.Compat);
        item.SubItems[9].ForeColor = row.Running ? Ui.OkOf() : Ui.TextSecondaryOf();
        return item;
    }

    private static Color CompatColor(CompatLevel level) => level switch
    {
        CompatLevel.Supported => Ui.OkOf(),
        CompatLevel.Partial => Ui.WarnOf(),
        CompatLevel.Unsupported => Ui.DangerOf(),
        _ => Ui.TextSecondaryOf(),
    };

    /// <summary>只刷新状态列（2 秒轮询），不重建整表 —— 避免用户正在看的时候表格跳动。</summary>
    private void UpdateStatusColumn()
    {
        if (_busy || _list.Items.Count == 0) return;
        try
        {
            _list.BeginUpdate();
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is not OverviewRow row) continue;
                bool now = Launcher.IsRunning(row.App.Id, row.Clone.Id);
                if (now == row.Running) continue;
                item.SubItems[9].Text = CloneOverview.RunningText(now);
                item.SubItems[9].ForeColor = now ? Ui.OkOf() : Ui.TextSecondaryOf();
                item.ForeColor = now ? Ui.OkOf() : Ui.TextPrimaryOf();
            }
            _list.EndUpdate();
            UpdateActions();
        }
        catch { try { _list.EndUpdate(); } catch { } }
    }

    private OverviewRow? SelectedRow() =>
        (_list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null) as OverviewRow;

    private void RestoreSelection(string key)
    {
        foreach (ListViewItem item in _list.Items)
            if (item.Tag is OverviewRow r && r.Key == key) { item.Selected = true; item.EnsureVisible(); return; }
    }

    private void UpdateActions()
    {
        var row = SelectedRow();
        bool has = row != null;
        _btnLaunch.Enabled = has && !row!.Running;
        _btnStop.Enabled = has && row!.Running;
        _btnFolder.Enabled = has;
        _btnRebuild.Enabled = has;
        _btnLog.Enabled = has;
        _btnDelete.Enabled = has;
    }

    // ------------------------------------------------------------------ 动作转发

    private void DoLaunch()
    {
        var row = SelectedRow();
        if (row == null) return;
        if (row.Running)
        {
            MessageBox.Show(this, "该分身正在运行。", "分身管理中心", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        LaunchRequested?.Invoke(row.App, row.Clone);
        UpdateStatusColumn();
    }

    private async Task DoStop()
    {
        var row = SelectedRow();
        if (row == null || !row.Running || StopRequested == null) return;
        await Guarded(() => StopRequested(row.App, row.Clone));
    }

    private void DoFolder()
    {
        var row = SelectedRow();
        if (row == null) return;
        OpenFolderRequested?.Invoke(row.App, row.Clone);
    }

    private async Task DoRebuild()
    {
        var row = SelectedRow();
        if (row == null || RebuildRequested == null) return;
        await Guarded(() => RebuildRequested(row.App, row.Clone));
        RefreshList();
    }

    private async Task DoDelete()
    {
        var row = SelectedRow();
        if (row == null || DeleteRequested == null) return;
        await Guarded(() => DeleteRequested(row.App, row.Clone));
        RefreshList();
    }

    private void DoViewLog()
    {
        var row = SelectedRow();
        if (row == null) return;
        ViewLogRequested?.Invoke(row.App, row.Clone);
    }

    /// <summary>动作执行期间挂忙碌闸并禁用按钮，避免用户在异步流程里连点造成重入。</summary>
    private async Task Guarded(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);
        try { await action(); }
        catch (Exception ex)
        {
            Log.Error("manager", "管理中心动作失败：" + ex.Message, ex: ex);
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
            UpdateActions();
        }
    }

    private void SetButtonsEnabled(bool on)
    {
        foreach (var b in new[] { _btnLaunch, _btnStop, _btnFolder, _btnRebuild, _btnLog, _btnDelete })
            b.Enabled = on;
    }
}
