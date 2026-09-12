using System.Drawing.Drawing2D;
using AppClone.Core;

namespace AppClone;

/// <summary>
/// 苹果风格 UI 基建：配色、圆角图形、带动画的按钮与卡片。
/// 所有交互动画采用逐帧插值（约 60fps 定时器 + 指数趋近），过渡平滑自然。
/// 增量 1.1.0 新增：RoundProgressBar、CloneColors、深色主题（Ui.Theme + 主题感知访问器）。
///
/// 主题约束（共享知识 14）：所有控件填充与 Parent.BackColor 必须走 Ui.*Of() 主题访问器，
/// 禁止硬编码浅色常量 Ui.Bg / Ui.Card 等（否则深色主题会漏）。
/// </summary>
internal static class Ui
{
    // ---- Apple 浅色系统色板（Light 基准值，UI.*Of() 在浅色时返回这些）----
    public static readonly Color Bg = ColorTranslator.FromHtml("#F5F5F7");       // 苹果浅灰背景
    public static readonly Color Card = Color.White;
    public static readonly Color Accent = ColorTranslator.FromHtml("#0A84FF");   // systemBlue
    public static readonly Color AccentDark = ColorTranslator.FromHtml("#0071E3");
    public static readonly Color AccentLight = ColorTranslator.FromHtml("#E8F1FF");
    public static readonly Color ChipPurple = ColorTranslator.FromHtml("#F3E8FF");
    public static readonly Color ChipPurpleText = ColorTranslator.FromHtml("#8E44EC");
    public static readonly Color TextPrimary = ColorTranslator.FromHtml("#1D1D1F");
    public static readonly Color TextSecondary = ColorTranslator.FromHtml("#86868B");
    public static readonly Color Border = ColorTranslator.FromHtml("#E8E8ED");
    public static readonly Color Ok = ColorTranslator.FromHtml("#34C759");       // systemGreen
    public static readonly Color Danger = ColorTranslator.FromHtml("#FF3B30");   // systemRed
    public static readonly Color FillGray = ColorTranslator.FromHtml("#E9E9EB"); // 次级按钮
    public static readonly Color FillGrayHover = ColorTranslator.FromHtml("#DCDCE1");

    // ---- 深色色板（T18：Ui.Theme = Dark 时由访问器返回）----
    public static readonly Color DarkBg = ColorTranslator.FromHtml("#1C1C1E");
    public static readonly Color DarkCard = ColorTranslator.FromHtml("#2C2C2E");
    public static readonly Color DarkTextPrimary = ColorTranslator.FromHtml("#F5F5F7");
    public static readonly Color DarkTextSecondary = ColorTranslator.FromHtml("#98989D");
    public static readonly Color DarkBorder = ColorTranslator.FromHtml("#38383A");
    public static readonly Color DarkFillGray = ColorTranslator.FromHtml("#3A3A3C");
    public static readonly Color DarkFillGrayHover = ColorTranslator.FromHtml("#48484A");
    public static readonly Color DarkOk = ColorTranslator.FromHtml("#30D158");
    public static readonly Color DarkDanger = ColorTranslator.FromHtml("#FF453A");
    public static readonly Color DarkAccentLight = ColorTranslator.FromHtml("#0A3A66");
    public static readonly Color DarkChipPurple = ColorTranslator.FromHtml("#2A1F3D");
    public static readonly Color DarkChipPurpleText = ColorTranslator.FromHtml("#BF5AF2");
    // 注：Accent / AccentDark 深浅主题一致（systemBlue 在深色背景同样清晰），不单独定义深色副本。

    // ---- 主题选择器：桥接 AppTheme 与 ThemeManager（共享知识 14）----
    public static AppTheme Theme
    {
        get => ThemeManager.Current == Core.Theme.Dark ? AppTheme.Dark : AppTheme.Light;
        set => ThemeManager.Set(value == AppTheme.Dark ? Core.Theme.Dark : Core.Theme.Light);
    }

    /// <summary>主题感知访问器：统一委托 ThemeManager.Color（单一来源，保证全应用配色一致）。</summary>
    public static Color BgOf() => ThemeManager.Color(ThemeRole.Bg);
    public static Color CardOf() => ThemeManager.Color(ThemeRole.Card);
    public static Color TextPrimaryOf() => ThemeManager.Color(ThemeRole.TextPrimary);
    public static Color TextSecondaryOf() => ThemeManager.Color(ThemeRole.TextSecondary);
    public static Color BorderOf() => ThemeManager.Color(ThemeRole.Border);
    public static Color FillGrayOf() => ThemeManager.Color(ThemeRole.FillGray);
    public static Color FillGrayHoverOf() => ThemeManager.Color(ThemeRole.FillGrayHover);
    public static Color OkOf() => ThemeManager.Color(ThemeRole.Ok);
    public static Color DangerOf() => ThemeManager.Color(ThemeRole.Danger);
    public static Color AccentLightOf() => ThemeManager.Color(ThemeRole.AccentLight);
    public static Color ChipPurpleOf() => ThemeManager.Color(ThemeRole.ChipPurple);
    public static Color ChipPurpleTextOf() => ThemeManager.Color(ThemeRole.ChipPurpleText);
    /// <summary>悬停微亮色（随主题切换）。</summary>
    public static Color HoverTintOf() =>
        Theme == AppTheme.Dark ? ColorTranslator.FromHtml("#3A3A3C") : ColorTranslator.FromHtml("#EEF1F6");

    public const string FontName = "Microsoft YaHei UI";

    // —— 字体层级（增量 1.2.0）：统一字号，避免散落魔法数 ——
    public const float FTitle = 16F;
    public const float FSub = 13F;
    public const float FSection = 12F;
    public const float FCardTitle = 10.5F;
    public const float FBody = 9.5F;
    public const float FCaption = 8.5F;
    public const float FChip = 8F;

    /// <summary>按当前主题取颜色（统一入口，禁止新增代码硬编码浅色常量）。</summary>
    public static Color ThemedColor(ThemeRole role) => ThemeManager.Color(role);

    /// <summary>
    /// 递归应用主题：重设非自绘控件底色并触发重绘。
    /// 自绘控件（AppleButton/PillChip/RoundProgressBar/AppleCard）自行处理，这里跳过其底色。
    /// </summary>
    public static void ApplyTheme(Control root)
    {
        if (root is null) return;
        bool selfDrawn = root is AppleButton or PillChip or RoundProgressBar or AppleCard;
        if (!selfDrawn)
        {
            try { root.BackColor = ThemedColor(ThemeRole.Bg); } catch { }
        }
        try { root.Invalidate(); } catch { }
        foreach (Control child in root.Controls)
            ApplyTheme(child);
    }

    public static Font Font(float size, bool bold = false) =>
        new(FontName, size, bold ? FontStyle.Bold : FontStyle.Regular);

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Win11 圆角窗口 + 使窗体圆形化更柔和。</summary>
    public static void RoundWindow(IWin32Window form)
    {
        try
        {
            int pref = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(form.Handle, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref pref, 4);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>标准文字标签（主题感知前景色）。</summary>
    public static Label Label(string text, float size = 9.5F, bool bold = false, Color? color = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = Font(size, bold),
            ForeColor = color ?? TextPrimaryOf(),
            BackColor = Color.Transparent,
        };
    }
}

/// <summary>分身角标 6 色调色板（蓝/绿/橙/粉/紫/灰）。CloneEntry.Color 存十六进制串，""=默认蓝。</summary>
internal static class CloneColors
{
    public const string Default = "#0A84FF";
    public const string Green = "#34C759";
    public const string Orange = "#FF9500";
    public const string Pink = "#FF2D55";
    public const string Purple = "#AF52DE";
    public const string Gray = "#8E8E93";

    public static readonly string[] All = { Default, Green, Orange, Pink, Purple, Gray };

    public static Color Resolve(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return ColorTranslator.FromHtml(Default);
        try { return ColorTranslator.FromHtml(hex); }
        catch { return ColorTranslator.FromHtml(Default); }
    }
}

/// <summary>
/// 苹果风格按钮：胶囊形（或自定义圆角），悬停/按下颜色平滑过渡动画。
/// Filled = 蓝底白字主按钮；Secondary = 浅灰底；Ghost = 无底色文字按钮。
/// 继承自 Button 以保留原生无障碍 Invoke（自动化点击）支持。
/// </summary>
internal sealed class AppleButton : Button
{
    public enum Style { Filled, Secondary, Ghost }

    private static readonly System.Windows.Forms.Timer _clock = new() { Interval = 16 };
    private static readonly List<AppleButton> _animating = new();

    private readonly Style _style;
    private float _hover;   // 0..1
    private float _press;   // 0..1
    private bool _tracked, _down;

    public int Radius { get; set; } = -1;   // -1 = 胶囊形

    public AppleButton(string text, Style style = Style.Filled, float fontSize = 9.5F)
    {
        _style = style;
        Text = text;
        Font = Ui.Font(fontSize, style == Style.Filled);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = false;
        UseVisualStyleBackColor = false;
        ThemeManager.ThemeChanged += () => Invalidate();
    }

    private Color CurrentFill()
    {
        Color a = _style switch
        {
            Style.Filled => Ui.ThemedColor(ThemeRole.Accent),
            Style.Secondary => Ui.ThemedColor(ThemeRole.FillGray),
            _ => Color.Transparent,
        };
        Color b = _style switch
        {
            Style.Filled => Ui.ThemedColor(ThemeRole.AccentDark),
            Style.Secondary => Ui.ThemedColor(ThemeRole.FillGrayHover),
            _ => Color.FromArgb(15, 0, 0, 0),
        };
        var c = Ui.Lerp(a, b, _hover);
        if (_style == Style.Ghost)
            c = Color.FromArgb((int)(20 * _hover), Ui.ThemedColor(ThemeRole.Accent));
        return Ui.Lerp(c, _style == Style.Filled ? ColorTranslator.FromHtml("#005BB8") : ColorTranslator.FromHtml("#C9C9CF"), _press);
    }

    private Color CurrentText()
    {
        if (!Enabled) return _style == Style.Filled ? Color.White : Ui.ThemedColor(ThemeRole.TextSecondary);
        return _style switch
        {
            Style.Filled => Color.White,
            Style.Secondary => Ui.ThemedColor(ThemeRole.TextPrimary),
            _ => Ui.ThemedColor(ThemeRole.Accent),
        };
    }

    private void TickFrame()
    {
        float targetHover = _tracked && Enabled ? 1f : 0f;
        float targetPress = _down && Enabled ? 1f : 0f;
        _hover += (targetHover - _hover) * 0.30f;
        _press += (targetPress - _press) * 0.35f;
        if (Math.Abs(_hover - targetHover) < 0.01f && Math.Abs(_press - targetPress) < 0.01f)
        {
            _hover = targetHover; _press = targetPress;
            _animating.Remove(this);
            if (_animating.Count == 0) _clock.Stop();
        }
        Invalidate();
    }

    private void StartAnim()
    {
        if (!_animating.Contains(this)) _animating.Add(this);
        if (!_clock.Enabled) { _clock.Tick += (_, _) => { foreach (var b in _animating.ToArray()) b.TickFrame(); }; _clock.Start(); }
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _tracked = true; StartAnim(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _tracked = false; _down = false; StartAnim(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; StartAnim(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; StartAnim(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Ui.ThemedColor(ThemeRole.Bg));

        int radius = Radius >= 0 ? Radius : Height / 2;
        using var path = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), radius);
        var fill = CurrentFill();
        if (fill.A > 0)
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }

        Color tc = CurrentText();
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>圆角胶囊徽章（如"深度隔离 / 设备分身"），自绘保证圆角与配色统一。</summary>
internal sealed class PillChip : Control
{
    private readonly Color _fill;
    private readonly Color _text;
    private readonly bool _outline;

    public PillChip(string text, Color fill, Color textColor, bool outline = false, float fontSize = 8F)
    {
        _fill = fill; _text = textColor; _outline = outline;
        Text = text;
        Font = Ui.Font(fontSize);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        using var g = CreateGraphics();
        var size = TextRenderer.MeasureText(text, Font);
        Size = new Size(size.Width + 18, 22);
        ThemeManager.ThemeChanged += () => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Ui.ThemedColor(ThemeRole.Card));

        using var path = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
        using (var b = new SolidBrush(_fill))
            g.FillPath(b, path);
        if (_outline)
            using (var p = new Pen(_text, 1F))
                g.DrawPath(p, path);

        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), _text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>自绘圆角进度条（底色 FillGray、进度 Accent、高 4 圆角 2）+ 状态文案。用于发现页扫描进度。</summary>
internal sealed class RoundProgressBar : Control
{
    private int _maximum = 100;
    private int _value;
    private string _status = "";
    private bool _indeterminate;

    public int Maximum { get => _maximum; set { _maximum = Math.Max(1, value); Invalidate(); } }
    public int Value { get => _value; set { _value = Math.Clamp(value, 0, _maximum); Invalidate(); } }
    public string StatusText { get => _status; set { _status = value ?? ""; Invalidate(); } }
    public bool Indeterminate { get => _indeterminate; set { _indeterminate = value; Invalidate(); } }

    public RoundProgressBar()
    {
        Height = 22;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        ThemeManager.ThemeChanged += () => Invalidate();
    }

    public void SetProgress(int value, string text) { Value = value; StatusText = text; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Ui.ThemedColor(ThemeRole.Bg));

        int barH = 4, barY = 2;
        using (var track = Ui.Round(new Rectangle(0, barY, Width - 1, barH), 2))
        using (var tb = new SolidBrush(Ui.ThemedColor(ThemeRole.FillGray)))
            g.FillPath(tb, track);

        int pct = _indeterminate ? 55 : (int)(100.0 * _value / _maximum);
        if (pct > 0)
        {
            int w = Math.Max(8, (int)((Width - 2) * pct / 100.0));
            using var fill = Ui.Round(new Rectangle(0, barY, w, barH), 2);
            using var fb = new SolidBrush(Ui.ThemedColor(ThemeRole.Accent));
            g.FillPath(fb, fill);
        }

        if (_status.Length > 0)
            TextRenderer.DrawText(g, _status, Ui.Font(Ui.FCaption), new Rectangle(0, barY + barH + 4, Width, 14),
                Ui.ThemedColor(ThemeRole.TextSecondary), TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// 苹果风格圆角卡片：白色圆角面板，悬停时背景平滑过渡，可附加描边。
/// 对子控件做整体悬停跟踪（鼠标进入卡片任意位置即触发）。
/// </summary>
internal sealed class AppleCard : Panel
{
    private float _hover;
    private bool _tracked;
    private bool _animateHover = true;
    private static readonly System.Windows.Forms.Timer _clock = new() { Interval = 16 };
    private static readonly List<AppleCard> _animating = new();

    public int Radius { get; set; } = 14;
    public bool DrawBorder { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public Color? AccentBar { get; set; }              // 左侧强调条（如运行中状态）
    public Func<Color>? BackColorOverride { get; set; } // 选中态等外部控制

    /// <summary>关闭悬停过渡动画（选中态卡片用，避免与选中高亮冲突）。</summary>
    public void DisableHoverAnimation() { _animateHover = false; Invalidate(); }

    public AppleCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.ThemedColor(ThemeRole.Card);
        ThemeManager.ThemeChanged += () => Invalidate();
    }

    private void HookHover(Control c)
    {
        c.MouseEnter += (_, _) => { _tracked = true; StartAnim(); };
        c.MouseLeave += (_, _) =>
        {
            BeginInvoke(new Action(() =>
            {
                if (!ClientRectangle.Contains(PointToClient(Cursor.Position)))
                { _tracked = false; StartAnim(); }
            }));
        };
        c.ControlAdded += (_, e2) => { if (e2.Control != null) HookHover(e2.Control); };
        foreach (Control child in c.Controls)
            if (child != null) HookHover(child);
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        HookHover(e.Control);
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        UpdateRegion();
        foreach (Control child in Controls) HookHover(child);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        try { Region?.Dispose(); } catch { }
        Region = new Region(Ui.Round(new Rectangle(0, 0, Width, Height), Radius));
    }

    private Color CurrentBack()
    {
        Color b = BackColorOverride?.Invoke() ?? Ui.ThemedColor(ThemeRole.Card);
        return _animateHover ? Ui.Lerp(b, Ui.HoverTintOf(), _hover * 0.9f) : b;
    }

    private void TickFrame()
    {
        float target = _tracked ? 1f : 0f;
        _hover += (target - _hover) * 0.30f;
        if (Math.Abs(_hover - target) < 0.01f)
        {
            _hover = target;
            _animating.Remove(this);
            if (_animating.Count == 0) _clock.Stop();
        }
        Invalidate();
    }

    private void StartAnim()
    {
        if (!_animating.Contains(this)) _animating.Add(this);
        if (!_clock.Enabled) { _clock.Tick += (_, _) => { foreach (var c in _animating.ToArray()) c.TickFrame(); }; _clock.Start(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Ui.ThemedColor(ThemeRole.Bg));

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        if (Shadow)
        {
            for (int i = 3; i >= 1; i--)
            {
                int a = 8 + i * 5;
                using var sp = Ui.Round(new Rectangle(i * 2, i * 2 + 2, Width - i * 4 - 1, Height - i * 4 - 3), Radius);
                using var sb = new SolidBrush(Color.FromArgb(Math.Min(a, _hover > 0.5f ? 44 : 30), 15, 23, 42));
                g.FillPath(sb, sp);
            }
        }

        using var path = Ui.Round(rect, Radius);
        using (var b = new SolidBrush(CurrentBack()))
            g.FillPath(b, path);
        if (DrawBorder)
            using (var p = new Pen(Ui.ThemedColor(ThemeRole.Border), 1f))
                g.DrawPath(p, path);

        if (AccentBar.HasValue)
        {
            using var bp = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            g.SetClip(bp);
            using var b = new SolidBrush(Ui.ThemedColor(ThemeRole.Ok));
            g.FillRectangle(b, 0, 8, 4, Height - 16);
            g.ResetClip();
        }
    }
}

/// <summary>窗体淡入 / 淡出动画。</summary>
internal static class FadeAnimator
{
    public static void FadeIn(Form form, int steps = 14)
    {
        form.Opacity = 0;
        var t = new System.Windows.Forms.Timer { Interval = 12 };
        int i = 0;
        t.Tick += (_, _) =>
        {
            i++;
            form.Opacity = Math.Min(1.0, i / (double)steps);
            if (form.Opacity >= 1.0) { form.Opacity = 1; t.Stop(); t.Dispose(); }
        };
        t.Start();
    }

    public static void FadeOutThen(Form form, Action then)
    {
        var t = new System.Windows.Forms.Timer { Interval = 10 };
        t.Tick += (_, _) =>
        {
            form.Opacity -= 0.10;
            if (form.Opacity <= 0.05)
            {
                t.Stop(); t.Dispose();
                form.Opacity = 0;
                then();
            }
        };
        t.Start();
    }
}
