using System.Diagnostics;
using System.Drawing.Drawing2D;
using AppClone.Core;

namespace AppClone;

/// <summary>
/// 主界面：左侧应用列表，右侧所选应用的分身卡片。苹果风格视觉与动画。
/// </summary>
public class MainForm : Form
{
    private readonly Store _store;

    private FlowLayoutPanel _appFlow = null!;
    private Label _appEmpty = null!;
    private Label _rightTitle = null!;
    private Label _rightPath = null!;
    private PictureBox _rightIcon = null!;
    private AppleButton _btnNewClone = null!;
    private FlowLayoutPanel _cloneFlow = null!;
    private Label _cloneEmpty = null!;
    private Label _rightPlaceholder = null!;
    private Label _statusText = null!;

    private string? _selectedAppId;
    private readonly List<(string app, string clone, Label dot, Label text, AppleCard card)> _statusLabels = new();
    private readonly List<(string app, Label count)> _appCountLabels = new();

    private string CountText(AppEntry app) => $"{_store.ClonesOf(app.Id).Count} 个分身";
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _pulse;
    private float _pulsePhase;
    private bool _reallyClosing;

    public MainForm()
    {
        _store = Store.Load();

        Text = "应用分身";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1120, 740);
        MinimumSize = new Size(960, 640);
        BackColor = Ui.Bg;
        Font = Ui.Font(9.5F);
        Icon = TryOwnIcon();

        BuildTopBar();
        BuildSidebar();
        BuildRightPane();
        BuildStatusBar();
        SetStatus($"数据目录 {Paths.DataRoot} · 已加载 {_store.Model.Apps.Count} 个应用");

        if (_store.Model.Apps.Count > 0)
            SelectApp(_store.Model.Apps[0].Id);
        else
            RefreshRightPane();
        RefreshAppList();

        _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        _timer.Tick += (_, _) => UpdateRunningStates();
        _timer.Start();

        _pulse = new System.Windows.Forms.Timer { Interval = 60 };
        _pulse.Tick += (_, _) => { _pulsePhase += 0.09f; UpdatePulse(); };
        _pulse.Start();

        Ui.RoundWindow(this);
        FadeAnimator.FadeIn(this);
    }

    private static Icon? TryOwnIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return null; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            FadeAnimator.FadeOutThen(this, () => { _reallyClosing = true; Close(); });
            return;
        }
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------------ UI 搭建

    private void BuildTopBar()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Ui.Bg };
        top.Paint += (_, e) =>
        {
            // 顶栏底部的品牌渐变发丝线
            using var b = new LinearGradientBrush(
                new Rectangle(0, top.Height - 2, top.Width, 2),
                Color.FromArgb(70, Ui.Accent), Color.FromArgb(70, ColorTranslator.FromHtml("#5E5CE6")), 0F);
            e.Graphics.FillRectangle(b, 0, top.Height - 2, top.Width, 2);
        };

        var iconBox = new PictureBox
        {
            Size = new Size(30, 30),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(22, 15),
            Image = TryOwnIcon()?.ToBitmap(),
        };

        var title = new Label
        {
            Text = "应用分身",
            AutoSize = true,
            Font = Ui.Font(14F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            Location = new Point(60, 13),
        };
        var sub = new Label
        {
            Text = "数据隔离 · 沙盒多开",
            AutoSize = true,
            Font = Ui.Font(8F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            Location = new Point(62, 40),
        };

        var btnData = new AppleButton("打开数据目录", AppleButton.Style.Secondary, 9F)
        { Size = new Size(112, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnData.Location = new Point(0, 0);
        btnData.Click += (_, _) => OpenFolder(Paths.DataRoot);

        var btnAdd = new AppleButton("＋ 添加应用", AppleButton.Style.Filled)
        { Size = new Size(116, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        btnAdd.Click += (_, _) => AddAppFlow();

        top.Controls.Add(iconBox);
        top.Controls.Add(title);
        top.Controls.Add(sub);
        top.Controls.Add(btnData);
        top.Controls.Add(btnAdd);
        top.Resize += (_, _) =>
        {
            btnAdd.Location = new Point(top.Width - 116 - 24, 17);
            btnData.Location = new Point(top.Width - 116 - 112 - 12 - 24, 17);
        };
        top.Controls.Add(new Panel()); // 触发一次布局
        btnAdd.Location = new Point(top.Width - 116 - 24, 17);
        btnData.Location = new Point(top.Width - 116 - 112 - 12 - 24, 17);

        Controls.Add(top);
        top.BringToFront();
    }

    private void BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 272, BackColor = Ui.Bg };

        var cap = Ui.Label("我的应用", 9F, true, Ui.TextSecondary);
        cap.Location = new Point(20, 16);
        sidebar.Controls.Add(cap);

        _appFlow = new FlowLayoutPanel
        {
            Location = new Point(12, 44),
            Size = new Size(sidebar.Width - 24, sidebar.Height - 56),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Ui.Bg,
            Padding = new Padding(0),
        };
        sidebar.Controls.Add(_appFlow);

        _appEmpty = new Label
        {
            Text = "还没有应用\n\n点击右上角「添加应用」\n开始创建第一个分身",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            Size = new Size(sidebar.Width - 40, 140),
            Location = new Point(20, 100),
            TextAlign = ContentAlignment.TopLeft,
        };
        sidebar.Controls.Add(_appEmpty);

        Controls.Add(sidebar);
        sidebar.BringToFront();
    }

    private void BuildRightPane()
    {
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding(16, 12, 20, 8) };

        _rightPlaceholder = new Label
        {
            Text = "←  选择左侧应用，管理它的分身",
            Font = Ui.Font(12F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        _rightPlaceholder.Location = new Point(40, 70);

        _rightIcon = new PictureBox { Size = new Size(52, 52), SizeMode = PictureBoxSizeMode.Zoom, Location = new Point(12, 10) };
        _rightTitle = new Label
        {
            AutoSize = true,
            Font = Ui.Font(15F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            Location = new Point(78, 8),
        };
        _rightPath = new Label
        {
            AutoSize = false,
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            Location = new Point(80, 40),
            AutoEllipsis = true,
            Size = new Size(560, 20),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _btnNewClone = new AppleButton("＋ 新建分身", AppleButton.Style.Filled)
        { Size = new Size(112, 36), Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _btnNewClone.Click += async (_, _) => await NewCloneFlow();

        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Ui.Bg };
        header.Controls.Add(_rightIcon);
        header.Controls.Add(_rightTitle);
        header.Controls.Add(_rightPath);
        header.Controls.Add(_btnNewClone);
        header.Resize += (_, _) => _btnNewClone.Location = new Point(header.Width - 112 - 4, 14);

        _cloneFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Ui.Bg,
            Padding = new Padding(0, 8, 0, 0),
        };
        _cloneFlow.Resize += (_, _) => StretchCloneCards();

        _cloneEmpty = new Label
        {
            Text = "该应用还没有分身\n\n点击右上角「＋ 新建分身」创建",
            Font = Ui.Font(10F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = false,
            Size = new Size(400, 100),
            Location = new Point(24, 40),
        };

        right.Controls.Add(_cloneFlow);
        right.Controls.Add(header);
        right.Controls.Add(_rightPlaceholder);
        _rightPlaceholder.BringToFront();

        Controls.Add(right);
        right.BringToFront();
    }

    private void BuildStatusBar()
    {
        var status = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Ui.Bg };
        _statusText = new Label
        {
            Text = "就绪",
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(24, 9),
        };
        status.Controls.Add(_statusText);
        Controls.Add(status);
        status.BringToFront();
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusText.Parent!.Invalidate();
    }

    // ------------------------------------------------------------------ 数据流

    private AppEntry? SelectedApp() =>
        _selectedAppId == null ? null : _store.FindApp(_selectedAppId);

    private async void AddAppFlow()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择要分身的应用",
            Filter = "应用程序 (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var app = _store.AddApp(dlg.FileName);
        SetStatus($"已添加 {app.Name}，正在提取图标…");
        await Task.Run(() => AppIcons.Extract(app.Id, app.ExePath));
        _selectedAppId = app.Id;
        RefreshAppList();
        RefreshRightPane();
        SetStatus($"已添加 {app.Name}");
    }

    private async Task NewCloneFlow()
    {
        var app = SelectedApp();
        if (app == null) return;

        SetStatus("正在创建分身…");
        var clone = _store.AddClone(app.Id);

        // 预热注册表快照与设备分身数据
        if (clone.DeepIsolation)
        {
            var ok = await Task.Run(() => RegClone.EnsureCloneRegistry(app.Id, clone.Id, out _));
            if (!ok) SetStatus("注册表快照初始化失败，首次启动时会自动重试");
        }
        if (clone.DeviceClone)
        {
            await Task.Run(() => RegClone.EnsureDeviceFake(app.Id, clone.Id));
        }

        RefreshRightPane();
        SetStatus($"已创建 {clone.Name}，可以启动了");
    }

    // ------------------------------------------------------------------ 列表渲染

    private void RefreshAppList()
    {
        _appFlow.SuspendLayout();
        _appFlow.Controls.Clear();
        foreach (var app in _store.Model.Apps)
            _appFlow.Controls.Add(MakeAppCard(app));
        _appFlow.ResumeLayout();
        _appEmpty.Visible = _store.Model.Apps.Count == 0;
    }

    private Panel MakeAppCard(AppEntry app)
    {
        bool selected = app.Id == _selectedAppId;
        var card = new AppleCard
        {
            Width = 240,
            Height = 66,
            Margin = new Padding(4, 4, 4, 4),
            Cursor = Cursors.Hand,
            Radius = 13,
            BackColor = Ui.Card,
            DrawBorder = true,
        };
        card.BackColorOverride = () => app.Id == _selectedAppId ? Ui.AccentLight : Ui.Card;
        card.SetHoverTint(Color.FromArgb(238, 241, 246));
        if (selected) card.DisableHoverAnimation();

        var icon = new PictureBox
        {
            Size = new Size(40, 40),
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(13, 13),
            BackColor = Color.Transparent,
            Image = AppIcons.Load(app.Id),
        };
        if (icon.Image == null && File.Exists(app.ExePath))
        {
            // 图标缺失（如导入的配置）时后台补提取
            var appId = app.Id;
            Task.Run(() => AppIcons.Extract(appId, app.ExePath)).ContinueWith(_ =>
            {
                try { BeginInvoke(new Action(() => { if (!IsDisposed) RefreshAppList(); })); } catch { }
            });
        }
        var name = new Label
        {
            Text = app.Name,
            AutoSize = true,
            Font = Ui.Font(10F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            Location = new Point(64, 13),
        };
        var count = new Label
        {
            Text = CountText(app),
            AutoSize = true,
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            Location = new Point(66, 38),
        };
        count.Paint += (_, e) =>
        {
            // 运行中数量以绿色强调
            int running = _store.ClonesOf(app.Id).Count(c => Launcher.IsRunning(app.Id, c.Id));
            if (running > 0)
            {
                using var b = new SolidBrush(Ui.Ok);
                e.Graphics.DrawString($" · {running} 运行中", count.Font, b, count.Width + 2, 0);
            }
        };
        _appCountLabels.Add((app.Id, count));

        card.Controls.Add(icon);
        card.Controls.Add(name);
        card.Controls.Add(count);

        void Select(object? s, EventArgs e) => SelectApp(app.Id);
        card.Click += Select;
        icon.Click += Select;
        name.Click += Select;
        count.Click += Select;

        var menu = new ContextMenuStrip { Font = Ui.Font(9.5F) };
        menu.Items.Add("打开程序所在目录", null, (_, _) => OpenFolder(Path.GetDirectoryName(app.ExePath) ?? ""));
        var remove = new ToolStripMenuItem("移除应用（连同全部分身数据）") { ForeColor = Ui.Danger };
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
        _cloneFlow.Visible = has;
        _cloneEmpty.Visible = false;

        if (app == null)
        {
            _rightTitle.Text = "";
            _rightPath.Text = "";
            _rightIcon.Image = null;
            _cloneFlow.Controls.Clear();
            return;
        }

        _rightIcon.Image?.Dispose();
        _rightIcon.Image = AppIcons.Load(app.Id) ?? MakeFallbackIcon();
        if (_rightIcon.Image == MakeFallbackIconStatic() && File.Exists(app.ExePath))
        {
            // 头像位置后台补提取图标
            var appId = app.Id;
            Task.Run(() => AppIcons.Extract(appId, app.ExePath)).ContinueWith(_ =>
            {
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (!IsDisposed && _selectedAppId == appId)
                        {
                            _rightIcon.Image?.Dispose();
                            _rightIcon.Image = AppIcons.Load(appId) ?? MakeFallbackIcon();
                        }
                    }));
                }
                catch { }
            });
        }
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

    private static Image? MakeFallbackIconStatic() => _fallback;

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
            Width = 620,
            Height = 92,
            Margin = new Padding(2, 7, 2, 7),
            Radius = 16,
            BackColor = Ui.Card,
            DrawBorder = true,
            Shadow = true,
            AccentBar = running ? Ui.Ok : null,
        };

        // 分身头像：渐变圆 + 序号
        var avatar = new Label
        {
            Size = new Size(38, 38),
            Text = index.ToString(),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Ui.Font(12F, true),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Location = new Point(20, 27),
        };
        avatar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var gPath = Ui.Round(new Rectangle(0, 0, 38, 38), 13);
            using var b = running
                ? new LinearGradientBrush(avatar.ClientRectangle,
                    Ui.Accent, ColorTranslator.FromHtml("#5E5CE6"), 60F)
                : new LinearGradientBrush(avatar.ClientRectangle,
                    ColorTranslator.FromHtml("#C7CBD4"), ColorTranslator.FromHtml("#AAB0BC"), 60F);
            e.Graphics.FillPath(b, gPath);
        };
        avatar.Resize += (_, _) => avatar.Invalidate();
        if (running) avatar.Invalidate();

        var name = new Label
        {
            Text = clone.Name,
            AutoSize = true,
            Font = Ui.Font(10.5F, true),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            Location = new Point(72, 16),
        };

        var dot = new Label
        {
            Text = "●",
            AutoSize = true,
            Font = new Font("Segoe UI", 10F),
            ForeColor = running ? Ui.Ok : Color.FromArgb(210, 210, 215),
            Location = new Point(74, 50),
            BackColor = Color.Transparent,
            Tag = running,
        };
        var state = new Label
        {
            Text = running ? "运行中" : "未运行",
            AutoSize = true,
            Font = Ui.Font(8.5F),
            ForeColor = running ? Ui.Ok : Ui.TextSecondary,
            Location = new Point(90, 51),
            BackColor = Color.Transparent,
        };

        int chipX = 150;
        card.Controls.Add(new PillChip(clone.DeepIsolation ? "深度隔离" : "标准隔离",
            clone.DeepIsolation ? Ui.AccentLight : Color.FromArgb(243, 244, 246),
            clone.DeepIsolation ? Ui.Accent : Ui.TextSecondary)
        { Location = new Point(chipX, 49) });
        if (clone.DeviceClone)
            card.Controls.Add(new PillChip("设备分身", Ui.ChipPurple, Ui.ChipPurpleText)
            { Location = new Point(chipX + 76, 49) });

        var created = new Label
        {
            Text = $"创建于 {clone.CreatedAt:yyyy-MM-dd}",
            AutoSize = true,
            Font = Ui.Font(8F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            Location = new Point(chipX + (clone.DeviceClone ? 158 : 84), 51),
        };

        var btnLaunch = new AppleButton(running ? "再次启动" : "启 动", AppleButton.Style.Filled)
        { Size = new Size(96, 36) };
        btnLaunch.Click += (_, _) => LaunchClone(app, clone);

        var btnMore = new AppleButton("管理  ▾", AppleButton.Style.Secondary, 9F)
        { Size = new Size(84, 36) };
        btnMore.Click += (_, _) => ShowCloneMenu(btnMore, app, clone);

        card.Controls.Add(avatar);
        card.Controls.Add(name);
        card.Controls.Add(dot);
        card.Controls.Add(state);
        card.Controls.Add(created);
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

    private PillChip MakeChip(int x, string text, Color bg, Color fg)
    {
        return new PillChip(text, bg, fg) { Location = new Point(x, 48) };
    }

    private void UpdateRunningStates()
    {
        foreach (var (appId, cloneId, _, text, _) in _statusLabels)
        {
            bool running = Launcher.IsRunning(appId, cloneId);
            text.Text = running ? "运行中" : "未运行";
            text.ForeColor = running ? Ui.Ok : Ui.TextSecondary;
            text.Tag = running;
        }
        foreach (var (appId, count) in _appCountLabels)
            count.Invalidate();
        UpdatePulse();
    }

    private void UpdatePulse()
    {
        // 运行中的状态点做呼吸动画（绿色明暗渐变）
        float k = 0.5f + 0.5f * MathF.Sin(_pulsePhase);
        foreach (var (_, _, dot, text, card) in _statusLabels)
        {
            if (dot.Tag is true)
            {
                var baseColor = Ui.Ok;
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
        SetStatus(string.IsNullOrEmpty(outcome.Message)
            ? $"已启动 {app.Name} · {clone.Name}"
            : $"{app.Name} · {clone.Name}：{outcome.Message}");
        RefreshRightPane();
    }

    private void ShowCloneMenu(Control anchor, AppEntry app, CloneEntry clone)
    {
        var menu = new ContextMenuStrip { Font = Ui.Font(9.5F) };

        menu.Items.Add("启动分身", null, (_, _) => LaunchClone(app, clone));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开数据目录", null, (_, _) =>
        {
            Directory.CreateDirectory(Paths.CloneDir(app.Id, clone.Id));
            OpenFolder(Paths.CloneDir(app.Id, clone.Id));
        });
        menu.Items.Add("创建桌面快捷方式", null, (_, _) => CreateShortcut(app, clone));

        var deep = new ToolStripMenuItem("深度隔离（注册表 + 多开解锁）")
        {
            Checked = clone.DeepIsolation,
            ToolTipText = "关闭后仅使用环境变量隔离数据",
        };
        deep.Click += (_, _) =>
        {
            clone.DeepIsolation = !clone.DeepIsolation;
            _store.Save();
            RefreshRightPane();
        };
        menu.Items.Add(deep);

        var dev = new ToolStripMenuItem("设备分身（随机设备名 / 设备ID）")
        {
            Checked = clone.DeviceClone,
            ToolTipText = "为该分身生成独立的设备指纹，绕过“同一设备”限制",
        };
        dev.Click += (_, _) =>
        {
            clone.DeviceClone = !clone.DeviceClone;
            _store.Save();
            RefreshRightPane();
            SetStatus(clone.DeviceClone
                ? "已开启设备分身，下次启动生效（同一分身设备身份保持稳定）"
                : "已关闭设备分身，下次启动生效");
        };
        menu.Items.Add(dev);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("结束进程", null, (_, _) =>
        {
            Launcher.Stop(app.Id, clone.Id);
            SetStatus("已请求结束分身进程");
        });

        var reset = menu.Items.Add("重置分身数据");
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"确定重置「{clone.Name}」吗？\n该分身的全部数据与登录状态将被清空并重新初始化。",
                    "重置分身", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            SetStatus("正在重置分身数据…");
            Launcher.Stop(app.Id, clone.Id);
            try { Directory.Delete(Paths.CloneDir(app.Id, clone.Id), true); } catch { }
            Directory.CreateDirectory(Paths.CloneHome(app.Id, clone.Id));
            if (clone.DeepIsolation)
                RegClone.ResetCloneRegistry(app.Id, clone.Id);
            SetStatus("分身数据已重置");
        };

        var del = new ToolStripMenuItem("删除分身") { ForeColor = Ui.Danger };
        del.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"确定删除「{clone.Name}」吗？\n该分身的全部数据将被永久删除。",
                    "删除分身", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Launcher.Stop(app.Id, clone.Id);
            _store.RemoveClone(app.Id, clone.Id);
            RefreshAppList();
            RefreshRightPane();
            SetStatus("分身已删除");
        };
        menu.Items.Add(del);

        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void CreateShortcut(AppEntry app, CloneEntry clone)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnk = Path.Combine(desktop, $"{app.Name} - {clone.Name}.lnk");
            string self = Application.ExecutablePath;

            // 原生 IShellLink 写入（见 Shared/Shortcuts.cs）；
            // 快捷方式带 --launch 参数，双击图标直接启动该分身
            new AppClone.Shared.NativeLink
            {
                Target = self,
                Arguments = $"--launch {app.Id} {clone.Id}",
                WorkingDirectory = Path.GetDirectoryName(self),
                IconPath = app.ExePath,
                Description = $"启动 {app.Name} · {clone.Name}",
            }.Save(lnk);

            SetStatus($"已在桌面创建快捷方式：{app.Name} - {clone.Name}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "创建快捷方式失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }
}
