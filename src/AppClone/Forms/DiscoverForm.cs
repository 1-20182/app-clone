using System.Windows.Forms;
using AppClone;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 「发现本机已安装应用」对话框（R1–R6）。复用 Ui.cs 全部组件。
/// 后台扫描、批量回填、缓存秒开、搜索/分类筛选、复选（Ctrl/Shift 连选）、批量添加。
/// </summary>
public class DiscoverForm : Form
{
    private readonly Store _store;
    private CancellationTokenSource? _cts;

    private FlowLayoutPanel _list = null!;
    private TextBox _search = null!;
    private FlowLayoutPanel _cats = null!;
    private RoundProgressBar _progress = null!;
    private Label _status = null!;
    private CheckBox _chkRecommended = null!;
    private AppleButton _btnSelectAll = null!;
    private ComboBox _cboClone = null!;
    private CheckBox _chkShortcut = null!;
    private AppleButton _btnAdd = null!;
    private AppleButton _btnRescan = null!;
    private Label _empty = null!;

    private List<DiscoveredApp> _merged = new();
    private List<DiscoveredApp> _filtered = new();
    private readonly HashSet<string> _selected = new();
    private readonly List<(DiscoveredApp App, CheckBox Box)> _cards = new();
    private string _category = "全部";
    private string? _anchorKey;
    private bool _suppress;
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 150 };

    public DiscoverForm(Store store)
    {
        _store = store;
        Settings.Load();
        Text = "发现本机已安装的应用";
        Size = new Size(900, 640);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        // PerMonitorV2：构造期逻辑像素交给 AutoScale 整树放大（高 DPI 下文本被裁的修复）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(9.5F);
        Icon = MainForm.TryAppIcon();

        BuildHeader();
        BuildToolbar();
        BuildList();
        BuildFooter();
        BuildEmpty();

        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        Shown += (_, _) => OnShown();
    }

    // ------------------------------------------------------------------ 布局

    private void BuildHeader()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Ui.BgOf() };
        top.Paint += (_, e) =>
        {
            using var b = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, top.Height - 2, top.Width, 2),
                Color.FromArgb(70, Ui.Accent), Color.FromArgb(70, ColorTranslator.FromHtml("#5E5CE6")), 0F);
            e.Graphics.FillRectangle(b, 0, top.Height - 2, top.Width, 2);
        };
        var title = Ui.Label("发现本机已安装的应用", 13F, true, Ui.TextPrimaryOf());
        title.Location = new Point(22, 12);
        _btnRescan = new AppleButton("⟳ 重新扫描", AppleButton.Style.Secondary, 9F)
        { Size = new Size(110, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _btnRescan.Location = new Point(0, 0);
        _btnRescan.Click += (_, _) => Rescan();
        top.Resize += (_, _) => _btnRescan.Location = new Point(top.Width - 110 - 18, 10);
        top.Controls.Add(title);
        top.Controls.Add(_btnRescan);
        _btnRescan.Location = new Point(top.Width - 110 - 18, 10);
        Controls.Add(top);
        top.BringToFront();
    }

    private void BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Ui.BgOf() };

        _search = new TextBox
        {
            Size = new Size(bar.Width - 44, 30),
            Location = new Point(22, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Font = Ui.Font(10F),
            Text = "搜索应用名称或发布者…",
            ForeColor = Ui.TextSecondaryOf(),
        };
        _search.GotFocus += (_, _) => { if (_search.Text == "搜索应用名称或发布者…") { _search.Text = ""; _search.ForeColor = Ui.TextPrimaryOf(); } };
        _search.LostFocus += (_, _) => { if (_search.Text.Length == 0) { _search.Text = "搜索应用名称或发布者…"; _search.ForeColor = Ui.TextSecondaryOf(); } };
        _search.TextChanged += (_, _) => { if (_search.Text != "搜索应用名称或发布者…") _searchTimer.Stop(); _searchTimer.Start(); };

        _cats = new FlowLayoutPanel
        {
            Location = new Point(18, 50),
            Size = new Size(bar.Width - 36, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Ui.BgOf(),
        };
        foreach (var c in new[] { "全部", "浏览器", "社交聊天", "游戏", "开发工具", "云盘", "其它" })
            _cats.Controls.Add(MakeCatChip(c));

        bar.Controls.Add(_search);
        bar.Controls.Add(_cats);
        Controls.Add(bar);
        bar.BringToFront();
    }

    private PillChip MakeCatChip(string name)
    {
        bool on = name == _category;
        var chip = new PillChip(name,
            on ? Ui.AccentLightOf() : Ui.FillGrayOf(),
            on ? Ui.AccentDark : Ui.TextSecondaryOf())
        { Margin = new Padding(0, 0, 8, 0) };
        chip.Click += (_, _) => { _category = name; RefreshCatChips(); ApplyFilter(); };
        return chip;
    }

    private void RefreshCatChips()
    {
        foreach (Control c in _cats.Controls)
            if (c is PillChip chip)
            {
                bool on = chip.Text == _category;
                // 重建配色
                var fresh = new PillChip(chip.Text, on ? Ui.AccentLightOf() : Ui.FillGrayOf(), on ? Ui.AccentDark : Ui.TextSecondaryOf())
                { Margin = chip.Margin, Location = chip.Location };
                var idx = _cats.Controls.GetChildIndex(chip);
                _cats.Controls.Remove(chip);
                fresh.Click += (_, _) => { _category = chip.Text; RefreshCatChips(); ApplyFilter(); };
                _cats.Controls.Add(fresh);
                _cats.Controls.SetChildIndex(fresh, idx);
            }
    }

    private void BuildList()
    {
        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Ui.BgOf(),
            Padding = new Padding(12, 10, 12, 10),
        };
        Controls.Add(_list);
    }

    private void BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = Ui.BgOf() };

        _chkRecommended = new CheckBox
        {
            Text = "仅显示「推荐分身」", AutoSize = true, Font = Ui.Font(9F),
            ForeColor = Ui.TextPrimaryOf(), Location = new Point(24, 12),
        };
        _chkRecommended.CheckedChanged += (_, _) => ApplyFilter();

        _btnSelectAll = new AppleButton("全选", AppleButton.Style.Secondary, 9F)
        { Size = new Size(84, 30), Location = new Point(200, 8) };
        _btnSelectAll.Click += (_, _) => ToggleSelectAll();

        _progress = new RoundProgressBar
        {
            Location = new Point(24, 42),
            Size = new Size(footer.Width - 48, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Indeterminate = true,
            StatusText = "正在扫描本机已安装应用…",
        };
        _status = Ui.Label("已选 0 项", 9F, false, Ui.TextSecondaryOf());
        _status.Location = new Point(24, 70);

        // 底部操作行
        _cboClone = new ComboBox
        {
            Size = new Size(64, 30), DropDownStyle = ComboBoxStyle.DropDownList, Font = Ui.Font(9.5F),
            Location = new Point(footer.Width - 360, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _cboClone.Items.AddRange(new object[] { "1", "2", "3" });
        _cboClone.SelectedIndex = Math.Clamp(Settings.Current.DefaultCloneCount, 1, 3) - 1;

        var lblClone = Ui.Label("为每个应用创建分身：", 9F, false, Ui.TextSecondaryOf());
        lblClone.Location = new Point(footer.Width - 432, 14);
        lblClone.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _chkShortcut = new CheckBox
        {
            Text = "同时生成桌面快捷方式", AutoSize = true, Font = Ui.Font(9F),
            ForeColor = Ui.TextPrimaryOf(), Location = new Point(footer.Width - 200, 14),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        var btnCancel = new AppleButton("取消", AppleButton.Style.Secondary)
        { Size = new Size(96, 34), Location = new Point(footer.Width - 220, 54), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _btnAdd = new AppleButton("添加所选（0）", AppleButton.Style.Filled)
        { Size = new Size(140, 34), Location = new Point(footer.Width - 108, 54), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _btnAdd.Enabled = false;
        _btnAdd.Click += (_, _) => AddSelected();

        footer.Controls.Add(_chkRecommended);
        footer.Controls.Add(_btnSelectAll);
        footer.Controls.Add(_progress);
        footer.Controls.Add(_status);
        footer.Controls.Add(lblClone);
        footer.Controls.Add(_cboClone);
        footer.Controls.Add(_chkShortcut);
        footer.Controls.Add(btnCancel);
        footer.Controls.Add(_btnAdd);
        Controls.Add(footer);
        footer.BringToFront();
    }

    private void BuildEmpty()
    {
        _empty = Ui.Label("正在扫描本机已安装应用…", 11F, false, Ui.TextSecondaryOf());
        _empty.Location = new Point(40, 120);
        _empty.Visible = false;
        Controls.Add(_empty);
        _empty.BringToFront();
    }

    // ------------------------------------------------------------------ 生命周期

    private void OnShown()
    {
        Ui.RoundWindow(this);
        FadeAnimator.FadeIn(this);

        _cts = new CancellationTokenSource();

        // 1) 同步读缓存：命中且指纹未变 → 立即渲染（≤0.3s）
        var cache = DiscoveryCacheIO.Load();
        bool cacheUsed = false;
        if (cache != null && cache.Apps.Count > 0 && !DiscoveryCacheIO.NeedsRescan(cache))
        {
            _merged = AppClassifier.Merge(cache.Apps, _store.AddedExeKeys());
            cacheUsed = true;
            RenderAll();
            _progress.Visible = false;
            _status.Text = $"已加载缓存 · 共 {_merged.Count} 项";
        }

        // 2) 后台全量扫描（缓存命中也静默刷新）；扫描源开关读 Settings.Current（T03/T05 验收）
        var opt = Settings.Current.ToDiscoveryOptions();
        var progress = new Progress<AppDiscovery.ScanProgress>(p =>
        {
            RunOnUi(new Action(() =>
            {
                if (_progress.Visible == false && !cacheUsed) _progress.Visible = true;
                _progress.Indeterminate = true;
                _progress.StatusText = $"正在扫描：{p.SourceName}（{p.SourceIndex + 1}/{p.SourceTotal}）";
            }));
        });

        _ = ScanAndRenderAsync(opt, progress, cacheUsed);
    }

    /// <summary>C8①：后台回调统一走此入口，窗体已销毁 / 句柄未建时静默放弃，避免 BeginInvoke 抛异常。</summary>
    private void RunOnUi(Action action)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(action);
    }

    private async Task ScanAndRenderAsync(AppDiscovery.DiscoveryOptions opt, IProgress<AppDiscovery.ScanProgress> progress, bool cacheUsed)
    {
        var myCts = _cts;   // C8②：识别已被 Rescan 取代 / 已取消的扫描，不再回写 UI
        try
        {
            var result = await AppDiscovery.ScanAsync(myCts!.Token, progress, opt);
            var merged = AppClassifier.Merge(result.Apps, _store.AddedExeKeys());

            // 落盘缓存（含指纹）
            var cache = new DiscoveryCache
            {
                ScannedAt = DateTime.Now,
                MachineId = Environment.MachineName,
                Apps = result.Apps,
                Stamps = DiscoveryCacheIO.CurrentStamps(),
            };
            DiscoveryCacheIO.Save(cache);

            RunOnUi(new Action(() =>
            {
                if (!ReferenceEquals(_cts, myCts)) return;   // 已被新一轮扫描取代，丢弃过期结果
                _merged = merged;
                RenderAll();
                _progress.Visible = false;
                int timeout = result.Status.Count(s => s.Value == AppDiscovery.SourceState.Timeout);
                _status.Text = timeout > 0
                    ? $"扫描完成 · 共 {_merged.Count} 项 · {timeout} 个源超时已跳过"
                    : $"扫描完成 · 共 {_merged.Count} 项";
            }));
        }
        catch (OperationCanceledException)
        {
            RunOnUi(new Action(() =>
            {
                if (!ReferenceEquals(_cts, myCts)) return;   // 新一轮扫描已在跑，不覆盖其进度
                _progress.Visible = false;
                _status.Text = "已取消扫描";
            }));
        }
        catch (Exception ex)
        {
            RunOnUi(new Action(() =>
            {
                if (!ReferenceEquals(_cts, myCts)) return;
                _progress.Visible = false;
                if (_merged.Count == 0)
                {
                    _empty.Visible = true;
                    _empty.Text = $"扫描失败：{ex.Message}";
                    _empty.ForeColor = Ui.DangerOf();
                }
                else _status.Text = "部分源扫描失败，已显示可用结果";
            }));
        }
    }

    public void Rescan()
    {
        // C8②：只 Cancel 不 Dispose —— 旧扫描协程持有 Token，Dispose 会引发 ObjectDisposedException
        if (_cts != null) { try { _cts.Cancel(); } catch { } }
        _cts = new CancellationTokenSource();
        DiscoveryCacheIO.SafeDelete();
        _selected.Clear();
        _merged = new List<DiscoveredApp>();
        RenderAll();
        _progress.Visible = true;
        _progress.Indeterminate = true;
        _progress.StatusText = "正在重新扫描本机已安装应用…";
        var opt = Settings.Current.ToDiscoveryOptions();
        var progress = new Progress<AppDiscovery.ScanProgress>(p => RunOnUi(new Action(() =>
        {
            _progress.StatusText = $"正在扫描：{p.SourceName}（{p.SourceIndex + 1}/{p.SourceTotal}）";
        })));
        _ = ScanAndRenderAsync(opt, progress, false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // C8②：只 Cancel 不 Dispose —— 协程仍持有 Token（Token 属性 / WaitHandle 在 Dispose 后访问会抛异常）
        if (_cts != null) { try { _cts.Cancel(); } catch { } }
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ 渲染

    private void RenderAll()
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string q = _search.Text == "搜索应用名称或发布者…" ? "" : _search.Text.Trim();
        bool recOnly = _chkRecommended.Checked;

        _filtered = _merged.Where(d =>
        {
            if (recOnly && !d.Recommended) return false;
            if (!CategoryMatch(d, _category)) return false;
            if (q.Length > 0)
            {
                bool hit = d.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || d.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || d.Publisher.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!hit) return false;
            }
            return true;
        }).ToList();

        _list.SuspendLayout();
        _list.Controls.Clear();
        _cards.Clear();
        _empty.Visible = _filtered.Count == 0 && _merged.Count > 0 ? false : _merged.Count == 0;
        if (_merged.Count == 0) { _empty.Visible = _progress.Visible == false; }

        foreach (var d in _filtered)
            _list.Controls.Add(MakeCard(d));

        _list.ResumeLayout();
        SyncChecks();
    }

    private bool CategoryMatch(DiscoveredApp d, string cat)
    {
        if (cat == "全部") return true;
        var map = new Dictionary<string, string>
        {
            ["浏览器"] = AppClassifier.CategoryConstants.Browser,
            ["社交聊天"] = AppClassifier.CategoryConstants.Social,
            ["游戏"] = AppClassifier.CategoryConstants.Game,
            ["开发工具"] = AppClassifier.CategoryConstants.Dev,
            ["云盘"] = AppClassifier.CategoryConstants.Cloud,
        };
        if (map.TryGetValue(cat, out var c)) return d.Category == c && d.Supported;
        if (cat == "其它") return d.Supported && (d.Category == AppClassifier.CategoryConstants.Office
            || d.Category == AppClassifier.CategoryConstants.Other);
        return true;
    }

    private AppleCard MakeCard(DiscoveredApp d)
    {
        bool selectable = d.Supported && !d.AlreadyAdded;
        var card = new AppleCard
        {
            Width = Math.Max(560, _list.ClientSize.Width - 24),
            Height = 66,
            Margin = new Padding(2, 3, 2, 3),
            Radius = 14,
            BackColor = Ui.CardOf(),
            DrawBorder = true,
        };

        var chk = new CheckBox
        {
            Size = new Size(18, 18),
            Location = new Point(14, 23),
            Enabled = selectable,
            AutoCheck = true,
        };
        chk.CheckedChanged += (_, _) =>
        {
            if (_suppress) return;
            if (chk.Checked) _selected.Add(KeyOf(d)); else _selected.Remove(KeyOf(d));
            _anchorKey = KeyOf(d);
            UpdateFooter();
        };

        string iconKey = KeyOf(d);
        var icon = new PictureBox
        {
            Size = new Size(32, 32), SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(42, 17), BackColor = Color.Transparent,
        };
        if (d.Supported && !string.IsNullOrEmpty(d.ExePath))
            AppIcons.LoadAsync(iconKey, d.ExePath, 32, img => RunOnUi(new Action(() =>
            {
                if (icon.IsHandleCreated) icon.Image = img;   // RunOnUi 已保证窗体存活
            })));
        else
            icon.Image = MakeUwpPlaceholder();

        var name = Ui.Label(d.Name, 10F, true, d.AlreadyAdded ? Ui.TextSecondaryOf() : Ui.TextPrimaryOf());
        name.Location = new Point(84, 13);

        var meta = Ui.Label(
            $"{(string.IsNullOrEmpty(d.Version) ? "—" : d.Version)}" +
            (string.IsNullOrEmpty(d.Publisher) ? "" : $"  ·  {d.Publisher}"),
            8.5F, false, Ui.TextSecondaryOf());
        meta.Location = new Point(84, 35);

        var path = Ui.Label(d.ExePath, 8F, false, Ui.TextSecondaryOf());
        path.Location = new Point(84, 50);
        path.AutoEllipsis = true;
        path.AutoSize = false;
        path.Size = new Size(card.Width - 220, 16);
        path.Tag = d.ExePath;

        // 右侧标签
        var catChip = new PillChip(
            CategoryLabel(d),
            d.Supported ? Ui.AccentLightOf() : Ui.FillGrayOf(),
            d.Supported ? Ui.AccentDark : Ui.TextSecondaryOf())
        { Location = new Point(card.Width - 110, 13), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        PillChip stateChip;
        if (!d.Supported)
            stateChip = new PillChip("暂不支持分身", Ui.DangerOf(), Ui.DangerOf(), true)
            { Location = new Point(card.Width - 110, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        else if (d.AlreadyAdded)
            stateChip = new PillChip("已添加", Ui.FillGrayOf(), Ui.TextSecondaryOf())
            { Location = new Point(card.Width - 110, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        else
            stateChip = new PillChip(d.Tier, Ui.ChipPurpleOf(), Ui.ChipPurpleTextOf())
            { Location = new Point(card.Width - 110, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };

        card.Controls.Add(chk);
        card.Controls.Add(icon);
        card.Controls.Add(name);
        card.Controls.Add(meta);
        card.Controls.Add(path);
        card.Controls.Add(catChip);
        card.Controls.Add(stateChip);

        // 运行期动态创建（ApplyFilter → MakeCard）：不参与窗体 AutoScale，高 DPI 下自行缩放；
        // 宽度由列表实际宽度决定（物理像素），必须放在缩放之后重设，内部 path 列跟随重算
        if (IsHandleCreated)
        {
            float f = Ui.DpiScaleOf(this);
            if (Math.Abs(f - 1f) > 0.001f) card.Scale(new SizeF(f, f));
        }
        card.Width = Math.Max(Ui.S(this, 560), _list.ClientSize.Width - Ui.S(this, 24));
        path.Width = Math.Max(80, card.Width - Ui.S(this, 220));

        if (selectable)
        {
            card.Click += (_, _) => OnCardClick(d);
            icon.Click += (_, _) => OnCardClick(d);
            name.Click += (_, _) => OnCardClick(d);
            meta.Click += (_, _) => OnCardClick(d);
            path.Click += (_, _) => OnCardClick(d);
        }

        _cards.Add((d, chk));
        return card;
    }

    private static Image? _uwpPlaceholder;
    private static Image MakeUwpPlaceholder()
    {
        if (_uwpPlaceholder != null) return _uwpPlaceholder;
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var b = new SolidBrush(Color.FromArgb(209, 213, 219));
        g.FillEllipse(b, 4, 4, 24, 24);
        _uwpPlaceholder = bmp;
        return bmp;
    }

    private static string CategoryLabel(DiscoveredApp d) => d.Category switch
    {
        AppClassifier.CategoryConstants.Browser => "浏览器",
        AppClassifier.CategoryConstants.Social => "社交聊天",
        AppClassifier.CategoryConstants.Game => "游戏",
        AppClassifier.CategoryConstants.Dev => "开发工具",
        AppClassifier.CategoryConstants.Cloud => "云盘",
        AppClassifier.CategoryConstants.Office => "办公",
        AppClassifier.CategoryConstants.Uwp => "UWP",
        _ => "其它",
    };

    // ------------------------------------------------------------------ 选择

    private static string KeyOf(DiscoveredApp d) =>
        d.Supported ? AppIcons.DiscoverKey(d.ExePath) : "uwp::" + d.Name;

    private void OnCardClick(DiscoveredApp d)
    {
        if (!d.Supported || d.AlreadyAdded) return;
        var key = KeyOf(d);
        bool shift = (Control.ModifierKeys & Keys.Shift) != 0;

        if (shift && _anchorKey != null)
        {
            int a = _filtered.FindIndex(x => KeyOf(x) == _anchorKey);
            int b = _filtered.FindIndex(x => KeyOf(x) == key);
            if (a >= 0 && b >= 0)
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                for (int i = lo; i <= hi; i++)
                {
                    var x = _filtered[i];
                    if (x.Supported && !x.AlreadyAdded) _selected.Add(KeyOf(x));
                }
                SyncChecks();
                return;
            }
        }

        if (_selected.Contains(key)) _selected.Remove(key); else _selected.Add(key);
        _anchorKey = key;
        SyncChecks();
    }

    private void SyncChecks()
    {
        _suppress = true;
        foreach (var (app, box) in _cards)
            box.Checked = _selected.Contains(KeyOf(app)) && app.Supported && !app.AlreadyAdded;
        _suppress = false;
        UpdateFooter();
    }

    private void ToggleSelectAll()
    {
        bool any = _filtered.Any(d => d.Supported && !d.AlreadyAdded && !_selected.Contains(KeyOf(d)));
        _selected.Clear();
        if (any)
            foreach (var d in _filtered)
                if (d.Supported && !d.AlreadyAdded) _selected.Add(KeyOf(d));
        _anchorKey = null;
        SyncChecks();
    }

    private void UpdateFooter()
    {
        int n = _selected.Count;
        _btnAdd.Text = $"添加所选（{n}）";
        _btnAdd.Enabled = n > 0;
        _status.Text = n > 0 ? $"已选 {n} 项" : _status.Text;
    }

    // ------------------------------------------------------------------ 批量添加

    private void AddSelected()
    {
        var picked = _merged.Where(d => _selected.Contains(KeyOf(d)) && d.Supported && !d.AlreadyAdded).ToList();
        if (picked.Count == 0) return;

        int cloneCount = int.Parse((string)_cboClone.SelectedItem!);
        bool makeShortcut = _chkShortcut.Checked;

        // C8①：添加期间禁用相关按钮 + 沙漏光标，阻止「取消 / 重新扫描」与窗体交互引发的后台回调竞态
        _btnAdd.Enabled = false;
        _btnRescan.Enabled = false;
        _btnSelectAll.Enabled = false;
        UseWaitCursor = true;
        _status.Text = $"正在添加 {picked.Count} 个应用…";

        Task.Run(() =>
        {
            bool ok;
            List<(AppEntry App, List<CloneEntry> Clones)> created = new();
            int added = 0;
            try
            {
                added = _store.AddAppsFromDiscovery(picked, cloneCount, out created);
                ok = true;
            }
            catch
            {
                ok = false;   // 失败也要在 UI 线程恢复按钮状态，不静默卡死
            }

            RunOnUi(new Action(() =>
            {
                // 恢复交互状态（若窗体正在关闭，RunOnUi 已保证不投递）
                UseWaitCursor = false;
                _btnRescan.Enabled = true;
                _btnSelectAll.Enabled = true;
                _btnAdd.Enabled = _selected.Count > 0;

                if (!ok)
                {
                    _status.Text = "添加失败，请重试";
                    return;
                }

                if (makeShortcut)
                    foreach (var (app, clones) in created)
                        foreach (var c in clones)
                            CreateShortcut(app, c);

                _status.Text = $"已添加 {added} 个应用";
                DialogResult = DialogResult.OK;
                Close();
            }));
        });
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
                Target = self,
                Arguments = $"--launch {app.Id} {clone.Id}",
                WorkingDirectory = Path.GetDirectoryName(self),
                IconPath = app.ExePath,
                Description = $"启动 {app.Name} · {clone.Name}",
            }.Save(lnk);
        }
        catch { }
    }
}
