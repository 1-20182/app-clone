using System.Drawing.Drawing2D;

namespace AppClone;

/// <summary>
/// 苹果风格 UI 基建：配色、圆角图形、带动画的按钮与卡片。
/// 所有交互动画采用逐帧插值（约 60fps 定时器 + 指数趋近），过渡平滑自然。
/// </summary>
internal static class Ui
{
    // ---- Apple 系统色板 ----
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

    public const string FontName = "Microsoft YaHei UI";

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

    /// <summary>标准文字标签。</summary>
    public static Label Label(string text, float size = 9.5F, bool bold = false, Color? color = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = Font(size, bold),
            ForeColor = color ?? TextPrimary,
            BackColor = Color.Transparent,
        };
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
    }

    private Color CurrentFill()
    {
        Color a = _style switch
        {
            Style.Filled => Ui.Accent,
            Style.Secondary => Ui.FillGray,
            _ => Color.Transparent,
        };
        Color b = _style switch
        {
            Style.Filled => Ui.AccentDark,
            Style.Secondary => Ui.FillGrayHover,
            _ => Color.FromArgb(15, 0, 0, 0),
        };
        var c = Ui.Lerp(a, b, _hover);
        if (_style == Style.Ghost)
            c = Color.FromArgb((int)(20 * _hover), Ui.Accent);
        return Ui.Lerp(c, _style == Style.Filled ? ColorTranslator.FromHtml("#005BB8") : ColorTranslator.FromHtml("#C9C9CF"), _press);
    }

    private Color CurrentText()
    {
        if (!Enabled) return _style == Style.Filled ? Color.White : Ui.TextSecondary;
        return _style switch
        {
            Style.Filled => Color.White,
            Style.Secondary => Ui.TextPrimary,
            _ => Ui.Accent,
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
        // 完全自绘，不调用基类（避免系统按钮外观）。
        // 必须先清空整个客户区：双缓冲缓冲区内容未定义，不清理会出现黑色/花屏方块
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Ui.Bg);

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
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Ui.Card);

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

/// <summary>
/// 苹果风格圆角卡片：白色圆角面板，悬停时背景平滑过渡，可附加描边。
/// 对子控件做整体悬停跟踪（鼠标进入卡片任意位置即触发）。
/// </summary>
internal sealed class AppleCard : Panel
{
    private float _hover;
    private bool _tracked;
    private Color _hoverTint = Color.FromArgb(246, 246, 248);
    private bool _animateHover = true;
    private static readonly System.Windows.Forms.Timer _clock = new() { Interval = 16 };
    private static readonly List<AppleCard> _animating = new();

    public int Radius { get; set; } = 14;
    public bool DrawBorder { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public Color? AccentBar { get; set; }              // 左侧强调条（如运行中状态）
    public Func<Color>? BackColorOverride;             // 选中态等外部控制

    public AppleCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Ui.Card;
    }

    public void SetHoverTint(Color c) => _hoverTint = c;
    public void DisableHoverAnimation() => _animateHover = false;

    private void HookHover(Control c)
    {
        c.MouseEnter += (_, _) => { _tracked = true; StartAnim(); };
        c.MouseLeave += (_, _) =>
        {
            // 鼠标可能只是移动到子控件，延迟一帧确认真正离开
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
        Color b = BackColorOverride?.Invoke() ?? Ui.Card;
        return _animateHover ? Ui.Lerp(b, _hoverTint, _hover * 0.9f) : b;
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
        g.Clear(Parent?.BackColor ?? Ui.Bg);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        // 多层内嵌软阴影（自阴影不越界，层叠列表不受影响）
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
            using (var p = new Pen(Ui.Border, 1f))
                g.DrawPath(p, path);

        if (AccentBar is Color bar)
        {
            using var bp = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Radius);
            g.SetClip(bp);
            using var b = new SolidBrush(bar);
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
