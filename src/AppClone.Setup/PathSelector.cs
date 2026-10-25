namespace AppCloneSetup;

/// <summary>控件边界的快照，供无 GUI 的布局探针断言（本环境不启动窗口）。</summary>
public readonly record struct SelectorBounds(Rectangle Label, Rectangle Box, Rectangle Browse, Rectangle Quick, Rectangle Hint);

/// <summary>
/// 安装位置选择行：文本框 + 「浏览…」+「快速定位 ▾」+ 实时校验提示。
///
/// <para><b>为什么子控件全部手算布局、不用 Anchor/Dock：</b>
/// 本控件在构造时宿主面板只有 200×100，右锚定的间距会被算成负数；
/// 等页面被 Dock 撑到 640 宽时，文本框会按那个负间距一路撑到 860px —— 直接糊住右侧按钮，
/// 用户点「浏览」其实点到的是文本框。这里改为在 OnResize 里对每一列 SetBounds，
/// 尺寸完全由算术决定，并可脱离窗口逐宽度断言。</para>
/// </summary>
public sealed class PathSelector : Panel
{
    /// <summary>路径行建议高度（标题 22 + 输入行 30 + 间距 6 + 提示 42）。</summary>
    public const int PreferredHeight = 100;

    private const int Gap = 8;
    private const int BrowseWidth = 84;
    private const int QuickWidth = 100;
    private const int BoxHeight = 30;
    private const int RowTop = 22;

    /// <summary>文本列的最小宽度（再窄就挤不下完整路径了）。</summary>
    private const int MinBoxWidth = 120;

    /// <summary>本控件低于此宽度就会把按钮挤出可视区 —— 宿主必须保证更宽。</summary>
    public const int MinUsableWidth = MinBoxWidth + Gap + BrowseWidth + Gap + QuickWidth;

    private static readonly Color WarnColor = ColorTranslator.FromHtml("#D0423C");
    private static readonly Color InfoColor = ColorTranslator.FromHtml("#1D7F3A");

    private readonly Label _label;
    private readonly TextBox _box;
    private readonly AppleButton _browse;
    private readonly AppleButton _quick;
    private readonly Label _hint;

    /// <summary>路径文本变化。</summary>
    public event EventHandler? PathChanged;

    /// <summary>用户在路径框里按回车（等同点「开始安装」）。</summary>
    public event EventHandler? PathSubmitted;

    public string AppName { get; set; } = "应用分身";
    public string ExeName { get; set; } = "AppClone.exe";
    public string DefaultPath { get; set; } = "";
    public string LastPath { get; set; } = "";
    public string SizeHint { get; set; } = "约 100 MB";

    /// <summary>当前选中的安装路径。</summary>
    public string SelectedPath
    {
        get => _box.Text.Trim();
        set => SetPath(value);
    }

    /// <summary>当前那行提示文字（探针用它断言校验逻辑）。</summary>
    public string HintText => _hint.Text;

    /// <summary>输入框是否接受资源管理器拖入的文件夹。</summary>
    public bool AcceptsDrop => _box.AllowDrop;

    public PathSelector()
    {
        DoubleBuffered = true;
        BackColor = Ui.Bg;
        // 注意：这里不再提前设 Height —— 高缩放下 S(100)=150 会触发 OnResize → LayoutChildren，
        // 而此时 _label/_box 还未创建，直接 NRE（真机 150% 启动即崩、96 DPI 探针看不见）。
        // 高度改到字段全部就绪后（Controls.AddRange 之后）再赋值。

        _label = new Label
        {
            Text = "安装位置",
            Font = Ui.Font(9.5F),
            ForeColor = Ui.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _box = new TextBox
        {
            Font = Ui.Font(9.5F),
            BorderStyle = BorderStyle.FixedSingle,
            AllowDrop = true,
            ShortcutsEnabled = true,
            // 单行 TextBox 默认 AutoSize=true，会把高度按字体算成 20 出头，
            // 和同一行的 30px 按钮对不齐 —— 关掉，让 RowTop/BoxHeight 说了算。
            AutoSize = false,
        };
        _box.TextChanged += (_, _) => { Validate(); PathChanged?.Invoke(this, EventArgs.Empty); };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;   // 别让回车在文本框里发出「叮」的一声
                PathSubmitted?.Invoke(this, EventArgs.Empty);
            }
        };
        _box.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        _box.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } dropped) return;
            string path = dropped[0];
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            if (Directory.Exists(path)) SetPath(path);
        };

        _browse = new AppleButton("浏览…", filled: false, 9.5F);
        _browse.Click += (_, _) => Browse();

        _quick = new AppleButton("快速定位 ▾", filled: false, 9.5F);
        _quick.Click += (_, _) => ShowQuickMenu();

        _hint = new Label
        {
            Font = Ui.Font(8.5F),
            ForeColor = Ui.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = false,
        };

        Controls.AddRange(new Control[] { _label, _box, _browse, _quick, _hint });
        Height = PreferredHeightScaled;   // DPI 感知：高缩放下整行加高，提示文字不再被裁（必须在字段就绪后）

        LayoutChildren();
        Validate();
    }

    // ---------------------------------------------------------------- 布局

    // —— DPI 感知（v1.4.1）：上方常量是 96 DPI 逻辑值，实际布局一律经 S() 换算成物理像素；
    // 96 DPI 下恒等，探针断言不受影响。MinUsableWidth / PreferredHeight 常量保留给探针与逻辑判断使用。 ——

    /// <summary>按当前 DPI 换算逻辑像素（实例级，随控件所在屏变化）。</summary>
    private int S(int v) => Ui.S(this, v);

    /// <summary>路径行当前 DPI 下的建议高度。</summary>
    public int PreferredHeightScaled => S(PreferredHeight);

    /// <summary>本控件当前 DPI 下可用的最小宽度（再窄按钮就会被挤出可视区）。</summary>
    public int MinUsableWidthScaled => S(MinBoxWidth) + S(Gap) * 2 + S(BrowseWidth) + S(QuickWidth);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutChildren();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        // 守卫：OnResize/OnLayout 可能在构造期字段就绪前到达（设 Height 即触发），必须容忍早到调用
        if (_label is null || _box is null || _browse is null || _quick is null || _hint is null) return;

        int w = Math.Max(0, ClientSize.Width);
        int gap = S(Gap), browseW = S(BrowseWidth), quickW = S(QuickWidth);
        int boxH = S(BoxHeight), rowTop = S(RowTop), minBox = S(MinBoxWidth);

        int boxW = Math.Max(minBox, w - browseW - quickW - gap * 2);

        _label.SetBounds(0, 0, w, rowTop - S(4));
        _box.SetBounds(0, rowTop, boxW, boxH);
        _browse.SetBounds(boxW + gap, rowTop, browseW, boxH);
        _quick.SetBounds(boxW + gap + browseW + gap, rowTop, quickW, boxH);
        int hintTop = rowTop + boxH + S(6);
        _hint.SetBounds(0, hintTop, w, Math.Max(S(20), Height - hintTop));
    }

    /// <summary>取一段边界快照（给布局探针用）。</summary>
    public SelectorBounds BoundsSnapshot() =>
        new(_label.Bounds, _box.Bounds, _browse.Bounds, _quick.Bounds, _hint.Bounds);

    // ---------------------------------------------------------------- 行为

    private void SetPath(string? path)
    {
        _box.Text = path ?? "";   // 文本真的变了会走 TextChanged，由它发 PathChanged
        try { _box.SelectionStart = _box.TextLength; } catch { }

        // 文本与当前值相同时 TextBox 不发 TextChanged，但目录可能刚被建出来 / 刚装上一个版本，
        // 提示必须跟着重算 —— 否则用户点了「浏览」选中同一个已有目录，看到的还是「将新建」。
        Validate();
    }

    private void Browse()
    {
        string? picked = FolderPicker.Pick(
            FindForm(),
            _box.Text,
            "选择安装位置（可直接在地址栏粘贴路径后回车）",
            "选择此文件夹");

        if (!string.IsNullOrWhiteSpace(picked)) SetPath(picked);
    }

    private void ShowQuickMenu()
    {
        var items = QuickLocations();
        if (items.Count == 0) return;

        var menu = new ContextMenuStrip
        {
            Font = Ui.Font(9.5F),
            ShowImageMargin = false,
            ShowCheckMargin = false,
        };
        foreach (var (text, path) in items)
        {
            if (text == "-") { menu.Items.Add(new ToolStripSeparator()); continue; }
            var item = new ToolStripMenuItem(text);
            string target = path;
            item.Click += (_, _) => SetPath(target);
            menu.Items.Add(item);
        }
        menu.Show(_quick, new Point(0, _quick.Height + 4));
    }

    /// <summary>
    /// 「快速定位」的下拉项。文本为 "-" 表示分隔线。
    /// 抽成 public 是为了让探针能在无 GUI 环境下断言候选位置都是真实、可用的。
    /// </summary>
    public List<(string Text, string Path)> QuickLocations()
    {
        var list = new List<(string, string)>();

        if (!string.IsNullOrWhiteSpace(DefaultPath))
            list.Add(($"默认位置   {Compact(DefaultPath)}", DefaultPath));

        if (!string.IsNullOrWhiteSpace(LastPath)
            && !Same(LastPath, DefaultPath)
            && !Same(LastPath, SelectedPath)
            && Directory.Exists(LastPath))
            list.Add(($"上次使用   {Compact(LastPath)}", LastPath));

        list.Add(("-", ""));

        AddSpecial("桌面", Environment.SpecialFolder.DesktopDirectory, list);
        AddSpecial("文档", Environment.SpecialFolder.MyDocuments, list);
        try
        {
            string programs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            if (Directory.Exists(programs)) list.Add(($"用户程序目录   {Compact(programs)}", programs));
        }
        catch { }

        var drives = FixedDrives();
        if (drives.Count > 0)
        {
            list.Add(("-", ""));
            foreach (var (root, note) in drives)
                list.Add(($"{root}  →  {root}{AppName}    （{note}）", Path.Combine(root, AppName)));
        }

        return list;
    }

    private static void AddSpecial(string name, Environment.SpecialFolder folder, List<(string, string)> list)
    {
        try
        {
            string p = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                list.Add(($"{name}   {Compact(p)}", p));
        }
        catch { }
    }

    private static List<(string Root, string Note)> FixedDrives()
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                    string kind = d.DriveType == DriveType.Fixed ? "本地磁盘" : "可移动磁盘";
                    list.Add((d.RootDirectory.FullName, $"{kind}，可用 {Free(d.AvailableFreeSpace)}"));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    // ---------------------------------------------------------------- 校验

    private void Validate()
    {
        var (text, color) = Evaluate(_box.Text.Trim());
        _hint.Text = text;
        _hint.ForeColor = color;
    }

    /// <summary>给当前路径一句人话提示 + 严重程度对应的颜色。</summary>
    private (string Text, Color Color) Evaluate(string path)
    {
        if (path.Length == 0)
            return ("请填入手动路径，或点「浏览…」/「快速定位 ▾」选择。", Ui.TextSecondary);

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return ("路径含非法字符，请重新选择。", WarnColor);

        if (!Path.IsPathRooted(path))
            return ($"请填写完整路径，例如 D:\\{AppName}", WarnColor);

        string root = Path.GetPathRoot(path) ?? "";
        if (root.Length == 0 || !Directory.Exists(root))
            return ($"盘符 {root} 不存在或未就绪。", WarnColor);

        bool isRootDir = string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        if (isRootDir)
            return ($"不建议装到盘符根目录 —— 可用「快速定位 ▾」里的 {root}{AppName}。", WarnColor);

        if (NeedsAdmin(path))
            return ("该位置需要管理员权限，写入可能失败，建议改用默认位置。", WarnColor);

        try
        {
            if (File.Exists(Path.Combine(path, ExeName)))
                return ($"此处已有「{AppName}」，将覆盖程序文件（分身数据与登录状态不受影响）。", Ui.TextSecondary);
        }
        catch { }

        return (Directory.Exists(path)
            ? "目录已存在，将直接安装到此位置。"
            : $"将新建该目录并安装（{SizeHint}）。", InfoColor);
    }

    /// <summary>目标是否落在受保护目录（需要提权才能写）。</summary>
    private static bool NeedsAdmin(string path)
    {
        try
        {
            string p = path.TrimEnd('\\') + "\\";
            foreach (var f in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.Windows,
            })
            {
                string baseDir = Environment.GetFolderPath(f);
                if (string.IsNullOrWhiteSpace(baseDir)) continue;
                if (p.StartsWith(baseDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    // ---------------------------------------------------------------- 小工具

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string Compact(string p) => p.Length <= 34 ? p : "…" + p[^33..];

    private static string Free(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        double gb = bytes / 1024.0 / 1024 / 1024;
        if (gb >= 100) return $"{gb:F0} GB";
        if (gb >= 1) return $"{gb:F1} GB";
        return $"{bytes / 1024.0 / 1024:F0} MB";
    }
}
