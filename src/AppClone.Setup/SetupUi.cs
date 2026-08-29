using System.Drawing.Drawing2D;

namespace AppCloneSetup;

/// <summary>安装程序的苹果风格 UI 基建（自包含，不依赖主程序工程）。</summary>
internal static class Ui
{
    public static readonly Color Bg = ColorTranslator.FromHtml("#F5F5F7");
    public static readonly Color Card = Color.White;
    public static readonly Color Accent = ColorTranslator.FromHtml("#0A84FF");
    public static readonly Color AccentDark = ColorTranslator.FromHtml("#0071E3");
    public static readonly Color TextPrimary = ColorTranslator.FromHtml("#1D1D1F");
    public static readonly Color TextSecondary = ColorTranslator.FromHtml("#86868B");
    public static readonly Color FillGray = ColorTranslator.FromHtml("#E9E9EB");
    public static readonly Color FillGrayHover = ColorTranslator.FromHtml("#DCDCE1");
    public static readonly Color Ok = ColorTranslator.FromHtml("#34C759");

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

    public static void RoundWindow(IWin32Window form)
    {
        try
        {
            int pref = 2;
            DwmSetWindowAttribute(form.Handle, 33, ref pref, 4);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>苹果风格按钮（胶囊形，悬停/按下颜色过渡动画），继承 Button 保留无障碍支持。</summary>
internal sealed class AppleButton : Button
{
    private static readonly System.Windows.Forms.Timer _clock = new() { Interval = 16 };
    private static readonly List<AppleButton> _animating = new();

    private readonly bool _filled;
    private float _hover, _press;
    private bool _tracked, _down;

    public AppleButton(string text, bool filled = true, float fontSize = 10F)
    {
        _filled = filled;
        Text = text;
        Font = Ui.Font(fontSize, filled);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        TabStop = false;
        UseVisualStyleBackColor = false;
        Height = 38;
    }

    private void TickFrame()
    {
        float th = _tracked && Enabled ? 1f : 0f;
        float tp = _down && Enabled ? 1f : 0f;
        _hover += (th - _hover) * 0.30f;
        _press += (tp - _press) * 0.35f;
        if (Math.Abs(_hover - th) < 0.01f && Math.Abs(_press - tp) < 0.01f)
        {
            _hover = th; _press = tp;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        // 必须先清空客户区：双缓冲缓冲区内容未定义，不清理会出现黑色方块
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? Ui.Bg);

        using var path = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);

        Color fill = _filled
            ? Ui.Lerp(Ui.Lerp(Ui.Accent, Ui.AccentDark, _hover), ColorTranslator.FromHtml("#005BB8"), _press)
            : Ui.Lerp(Ui.Lerp(Ui.FillGray, Ui.FillGrayHover, _hover), ColorTranslator.FromHtml("#C9C9CF"), _press);
        using (var b = new SolidBrush(fill))
            g.FillPath(b, path);

        var tc = !_filled && !Enabled ? Ui.TextSecondary : (_filled ? Color.White : Ui.TextPrimary);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), tc,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>圆角进度条（苹果风格：灰色轨道 + 蓝色胶囊进度）。</summary>
internal sealed class RoundedProgressBar : Control
{
    private int _value;

    public int Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    public RoundedProgressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 10;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Ui.Bg);

        using var track = Ui.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
        using (var b = new SolidBrush(Ui.FillGray))
            g.FillPath(b, track);

        int w = (int)((Width - 1) * _value / 100.0);
        if (w > Height / 2)
        {
            using var fillPath = Ui.Round(new Rectangle(0, 0, w, Height - 1), Height / 2);
            using var fb = new LinearGradientBrush(new Rectangle(0, 0, w, Height),
                Ui.Accent, ColorTranslator.FromHtml("#5E5CE6"), 0F);
            g.FillPath(fb, fillPath);
        }
        else if (w > 0)
        {
            using var fb = new SolidBrush(Ui.Accent);
            g.FillEllipse(fb, 0, 0, Math.Max(Height - 1, w), Height - 1);
        }
    }
}

/// <summary>面板滑动切换动画（苹果式页面过渡）。</summary>
internal static class SlideAnimator
{
    public static void SlideTo(Control container, Control next)
    {
        foreach (Control c in container.Controls) { if (c != next) c.Visible = false; }
        next.Visible = true;
        next.BringToFront();

        int target = next.Left;
        int offset = 46;
        next.Left = target + offset;
        var t = new System.Windows.Forms.Timer { Interval = 10 };
        t.Tick += (_, _) =>
        {
            offset = (int)(offset * 0.62);
            next.Left = target + offset;
            if (offset <= 0)
            {
                next.Left = target;
                t.Stop(); t.Dispose();
            }
        };
        t.Start();
    }
}
