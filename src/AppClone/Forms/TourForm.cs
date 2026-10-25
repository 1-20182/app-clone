using System.Drawing.Drawing2D;
using System.Windows.Forms;
using AppClone;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 遮罩式新手教程：半透明黑罩盖住主窗口，当前目标控件处「挖孔」露出原界面，
/// 旁边跟一个说明气泡，逐步骤引导。首次启动自动播放，也可从「更多」菜单或帮助窗口重播。
///
/// 挖孔用 Region（外矩形 + 圆角内矩形，Alternate 填充），孔内事件直接落到主窗口，
/// 孔外点击被遮罩吞掉 —— 不需要额外拦截鼠标。
/// </summary>
public sealed class TourForm : IDisposable
{
    public sealed record Step(Control? Anchor, string Title, string Body);

    private readonly Form _host;
    private readonly IReadOnlyList<Step> _steps;
    private readonly Form _mask;
    private readonly Form _bubble;
    private readonly Label _counter;
    private readonly Label _title;
    private readonly Label _body;
    private readonly AppleButton _btnPrev;
    private readonly AppleButton _btnNext;
    private readonly AppleButton _btnSkip;
    private Rectangle _hole = Rectangle.Empty;
    private int _index;

    /// <summary>教程结束（无论完成还是跳过）后回调，用于落盘标记。</summary>
    public Action? Completed;

    public TourForm(Form host, IReadOnlyList<Step> steps)
    {
        _host = host;
        _steps = steps;

        _mask = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            BackColor = Color.Black,
            Opacity = 0.62,
        };
        _mask.Paint += (_, e) =>
        {
            if (_hole.IsEmpty) return;
            using var pen = new Pen(Ui.ThemedColor(ThemeRole.AccentDark), Math.Max(2F, Ui.S(_mask, 2)));
            using var path = Rounded(_hole, Ui.S(_mask, 12));
            e.Graphics.DrawPath(pen, path);
        };

        _bubble = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96F, 96F),
            BackColor = Ui.CardOf(),
            Padding = new Padding(0),
        };
        _bubble.Paint += (_, e) =>
        {
            using var pen = new Pen(Ui.BorderOf());
            e.Graphics.DrawRectangle(pen, 0, 0, _bubble.Width - 1, _bubble.Height - 1);
        };
        _bubble.ClientSize = new Size(420, 212);
        _bubble.KeyPreview = true;
        _bubble.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Finish(); }
            else if (e.KeyCode == Keys.Right) { e.Handled = true; Go(_index + 1); }
            else if (e.KeyCode == Keys.Left) { e.Handled = true; Go(_index - 1); }
        };

        _counter = Ui.Label("", Ui.FChip, false, Ui.TextSecondaryOf());
        _counter.Location = new Point(20, 14);
        _title = Ui.Label("", Ui.FSub, true, Ui.TextPrimaryOf());
        _title.Location = new Point(20, 36);
        // 正文固定宽度、按换行后的实际高度撑高气泡：
        // AutoSize + MaximumSize 在 Label 上不保证重新换行（长句会按一行宽度画出去被窗口裁掉）
        _body = new Label
        {
            Text = "", AutoSize = false, Size = new Size(380, 96),
            Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextSecondaryOf(),
            BackColor = Color.Transparent, Location = new Point(20, 66),
        };
        _btnPrev = new AppleButton("上一步", AppleButton.Style.Secondary, 9F) { Size = new Size(88, 32) };
        _btnPrev.Click += (_, _) => Go(_index - 1);
        var btnSkip = new AppleButton("跳过", AppleButton.Style.Ghost, 9F) { Size = new Size(72, 32) };
        _btnSkip = btnSkip;
        btnSkip.Click += (_, _) => Finish();
        _btnNext = new AppleButton("下一步", AppleButton.Style.Filled, 9F) { Size = new Size(112, 32) };
        _btnNext.Click += (_, _) => Go(_index + 1);

        _bubble.Controls.Add(_counter);
        _bubble.Controls.Add(_title);
        _bubble.Controls.Add(_body);
        _bubble.Controls.Add(btnSkip);
        _bubble.Controls.Add(_btnPrev);
        _bubble.Controls.Add(_btnNext);

        _host.Move += OnHostChanged;
        _host.Resize += OnHostChanged;
        _host.FormClosing += OnHostClosing;
    }

    /// <summary>显示教程并从第一步开始（非阻塞）。</summary>
    public void Start()
    {
        _mask.Show(_host);
        _bubble.Show(_host);
        _index = 0;
        Apply();
        _bubble.Activate();
    }

    private void OnHostChanged(object? sender, EventArgs e) => Relayout();

    private void OnHostClosing(object? sender, FormClosingEventArgs e) => Close();

    private void Apply()
    {
        var step = _steps[Math.Clamp(_index, 0, _steps.Count - 1)];
        _counter.Text = $"第 {_index + 1} / {_steps.Count} 步";
        _title.Text = step.Title;
        // 气泡自身没有随 DPI 缩放（尺寸就是物理像素），因此正文宽度按真实 ClientWidth 算，
        // 用 TextRenderer 预量换行后的高度（Label 只负责画，不再依赖它自测）
        int bodyW = Math.Max(160, _bubble.ClientSize.Width - 40);
        _body.Size = new Size(bodyW, Math.Max(24,
            TextRenderer.MeasureText(step.Body, _body.Font, new Size(bodyW, int.MaxValue),
                TextFormatFlags.WordBreak).Height + 4));
        _body.Text = step.Body;
        _btnPrev.Enabled = _index > 0;
        _btnNext.Text = _index >= _steps.Count - 1 ? "完成" : "下一步";
        Relayout();
    }

    private void Go(int index)
    {
        if (index >= _steps.Count) { Finish(); return; }
        if (index < 0) return;
        _index = index;
        Apply();
    }

    private void Relayout()
    {
        if (_mask.IsDisposed || _bubble.IsDisposed || _host.IsDisposed) return;
        try
        {
            // 正文自动撑高气泡，按钮跟着内容走（每步文字长短不同，固定高度会裁掉后半段）
            int y = _body.Bounds.Bottom + 16;
            int x = _bubble.ClientSize.Width - 20;
            var buttons = new[] { _btnSkip, _btnPrev, _btnNext };
            for (int i = buttons.Length - 1; i >= 0; i--)
            {
                var btn = buttons[i];
                x -= btn.Width + 8;
                btn.Location = new Point(x, y);
            }
            int clientH = y + 32 + 16;
            if (_bubble.ClientSize.Height != clientH)
                _bubble.ClientSize = new Size(_bubble.ClientSize.Width, clientH);

            _mask.Bounds = _host.Bounds;
            var anchor = _steps[Math.Clamp(_index, 0, _steps.Count - 1)].Anchor;
            if (anchor == null || !anchor.IsHandleCreated || !anchor.Visible)
            {
                SetMaskHole(null);
                CenterBubble();
                return;
            }

            int pad = Ui.S(_mask, 8);
            var r = anchor.RectangleToScreen(anchor.ClientRectangle);
            var hole = new Rectangle(r.X - _mask.Left - pad, r.Y - _mask.Top - pad, r.Width + pad * 2, r.Height + pad * 2);
            // 目标可能被其它窗口/滚动区挡住，孔位至少要和遮罩本体重叠才值得挖
            if (!hole.IntersectsWith(new Rectangle(Point.Empty, _mask.ClientSize)))
            {
                SetMaskHole(null);
                CenterBubble();
                return;
            }

            SetMaskHole(hole);
            PositionBubble(hole);
        }
        catch { }
    }

    /// <summary>套用挖孔区域：外矩形减去圆角内矩形；旧 Region 必须显式 Dispose，否则每步漏一个 GDI 对象。</summary>
    private void SetMaskHole(Rectangle? hole)
    {
        var old = _mask.Region;
        if (hole is null || hole.Value.IsEmpty)
        {
            _hole = Rectangle.Empty;
            _mask.Region = null;
        }
        else
        {
            var region = new Region(new Rectangle(Point.Empty, _mask.ClientSize));
            using (var path = Rounded(hole.Value, Ui.S(_mask, 12))) region.Exclude(path);
            _hole = hole.Value;
            _mask.Region = region;
        }
        old?.Dispose();
        _mask.Invalidate();
    }

    private void CenterBubble()
    {
        var wa = Screen.FromControl(_host).WorkingArea;
        _bubble.Location = new Point(
            wa.Left + (wa.Width - _bubble.Width) / 2,
            wa.Top + (wa.Height - _bubble.Height) / 2);
    }

    /// <summary>气泡优先放在孔的右下方；下方放不下就放上方，横向夹到工作区内。</summary>
    private void PositionBubble(Rectangle hole)
    {
        var wa = Screen.FromControl(_host).WorkingArea;
        int gap = Ui.S(_bubble, 12);
        int x = hole.Right - _bubble.Width + Ui.S(_bubble, 20);
        int y = hole.Bottom + gap;
        if (y + _bubble.Height > wa.Bottom - gap)
        {
            y = hole.Top - _bubble.Height - gap;
            if (y < wa.Top + gap) y = wa.Top + (wa.Height - _bubble.Height) / 2;
        }
        x = Math.Clamp(x, wa.Left + gap, Math.Max(wa.Left + gap, wa.Right - _bubble.Width - gap));
        _bubble.Location = new Point(x, y);
    }

    private void Finish()
    {
        Settings.Current.OnboardingDone = true;
        Settings.Current.Save();
        Close();
    }

    public void Close()
    {
        try { _host.Move -= OnHostChanged; } catch { }
        try { _host.Resize -= OnHostChanged; } catch { }
        try { _host.FormClosing -= OnHostClosing; } catch { }
        try { _bubble.Close(); } catch { }
        try { _mask.Close(); } catch { }
        Dispose();
    }

    public void Dispose()
    {
        try { _bubble.Dispose(); } catch { }
        try { _mask.Dispose(); } catch { }
        var cb = Completed;
        Completed = null;
        try { cb?.Invoke(); } catch { }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
