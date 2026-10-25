using System.Windows.Forms;
using AppClone;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 内置帮助窗口：左侧主题列表，右侧纯文本渲染（标题 / 小标题 / 项目符号 / 段落）。
/// 不依赖网络，也不引 Markdown 解析器 —— 帮助内容由 HelpContent 以约定好的行格式提供。
/// </summary>
public sealed class HelpForm : Form
{
    private readonly ListBox _nav;
    private readonly Panel _content;
    private int _lastWidth;

    /// <summary>请求重播新手教程（由 MainForm 注入）。</summary>
    public Action? ReplayTour;

    public HelpForm(int initialIndex = 0)
    {
        Text = "使用帮助";
        Size = new Size(900, 620);
        MinimumSize = new Size(640, 440);
        StartPosition = FormStartPosition.CenterParent;
        // 构造期用逻辑像素，交给 AutoScale 在加载时整树放大（与其它窗口一致）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = MainForm.TryAppIcon();
        ShowInTaskbar = false;

        var head = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Ui.BgOf(), Padding = new Padding(20, 12, 20, 0) };
        var title = Ui.Label("使用帮助", Ui.FSection, true, Ui.TextPrimaryOf());
        title.Location = new Point(20, 10);
        var hint = Ui.Label("内置文档 · 无需联网，按主题查阅", Ui.FCaption, false, Ui.TextSecondaryOf());
        hint.Location = new Point(20, 38);
        head.Controls.Add(title);
        head.Controls.Add(hint);

        var foot = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Ui.BgOf(), Padding = new Padding(20, 12, 20, 12) };
        var btnTour = new AppleButton("重播新手教程", AppleButton.Style.Secondary, 9F) { Size = new Size(120, 32) };
        btnTour.Click += (_, _) => { ReplayTour?.Invoke(); Close(); };
        var btnClose = new AppleButton("关闭", AppleButton.Style.Filled, 9F) { Size = new Size(84, 32) };
        btnClose.Click += (_, _) => Close();
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.BgOf(),
        };
        btnClose.Margin = new Padding(10, 0, 0, 0);
        btnTour.Margin = new Padding(0);
        actions.Controls.Add(btnClose);
        actions.Controls.Add(btnTour);
        foot.Controls.Add(actions);

        _nav = new ListBox
        {
            Dock = DockStyle.Left, Width = 250, BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false,
            BackColor = Ui.BgOf(), ForeColor = Ui.TextPrimaryOf(), Font = Ui.Font(Ui.FBody),
            ItemHeight = Ui.S(this, 34),
        };
        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (_nav.SelectedIndex >= 0 && _nav.SelectedIndex < HelpContent.All.Length)
                ShowTopic(HelpContent.All[_nav.SelectedIndex]);
        };

        _content = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.CardOf(),
            Padding = new Padding(24, 18, 24, 18),
        };
        // 宽度变化才重排，避免拖动时无意义的反复重建
        _content.Resize += (_, _) =>
        {
            if (Math.Abs(_content.ClientSize.Width - _lastWidth) > 2 && _nav.SelectedIndex >= 0 &&
                _nav.SelectedIndex < HelpContent.All.Length)
                ShowTopic(HelpContent.All[_nav.SelectedIndex]);
        };

        var splitter = new Splitter { Dock = DockStyle.Left, Width = 1, BackColor = Ui.BorderOf(), MinSize = 180, MinExtra = 320 };

        Controls.Add(_content);
        Controls.Add(splitter);
        Controls.Add(_nav);
        Controls.Add(head);
        Controls.Add(foot);

        foreach (var t in HelpContent.All) _nav.Items.Add(t.Title);

        var start = Math.Clamp(initialIndex, 0, HelpContent.All.Length - 1);
        _nav.SelectedIndex = start;

        Shown += (_, _) =>
        {
            Ui.RoundWindow(this);
            _nav.ItemHeight = Ui.S(this, 34);   // ListBox.ItemHeight 不参与 AutoScale，高 DPI 需在句柄就绪后手动换算
            _nav.Focus();
        };
    }

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= HelpContent.All.Length) return;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? Ui.AccentLightOf() : Ui.BgOf()))
            e.Graphics.FillRectangle(bg, e.Bounds);
        var t = HelpContent.All[e.Index];
        // 分类前缀用小字，标题用正文字号 —— 分类重复出现时不至于喧宾夺主
        using var small = Ui.Font(Ui.FChip);
        using var body = Ui.Font(Ui.FBody);
        using var fg = new SolidBrush(sel ? Ui.TextPrimaryOf() : Ui.TextSecondaryOf());
        using var gray = new SolidBrush(Ui.TextSecondaryOf());
        int x = e.Bounds.X + Ui.S(this, 14);
        e.Graphics.DrawString(t.Category, small, gray, x, e.Bounds.Y + Ui.S(this, 3));
        e.Graphics.DrawString(t.Title, body, fg, x, e.Bounds.Y + Ui.S(this, 14));
    }

    private void ShowTopic(HelpContent.Topic topic)
    {
        _content.SuspendLayout();
        foreach (var c in _content.Controls.Cast<Control>().ToList()) c.Dispose();   // 先摘下再释放，避免换主题时句柄泄漏
        _content.Controls.Clear();
        _lastWidth = _content.ClientSize.Width;

        int width = Math.Max(Ui.S(this, 200), _content.ClientSize.Width - _content.Padding.Horizontal - Ui.S(this, 20));
        int y = Ui.S(this, 4);

        y = AddLine(topic.Title, Ui.FTitle, true, Ui.TextPrimaryOf(), y, width, 0);
        y += Ui.S(this, 6);

        foreach (var raw in topic.Body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) { y += Ui.S(this, 8); continue; }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                y += Ui.S(this, 6);
                y = AddLine(line[3..], Ui.FSub, true, Ui.TextPrimaryOf(), y, width, 0);
                continue;
            }
            if (line.StartsWith("· ", StringComparison.Ordinal))
            {
                y = AddLine("•  " + line[2..], Ui.FBody, false, Ui.TextPrimaryOf(), y, width - Ui.S(this, 14), Ui.S(this, 14));
                continue;
            }
            y = AddLine(line, Ui.FBody, false, Ui.TextSecondaryOf(), y, width, 0);
        }

        _content.ResumeLayout();
        _content.AutoScrollPosition = new Point(0, 0);
    }

    /// <summary>放一行自动换行文本并返回下一行的 Y。Label 不能直接测量多行，故用 PreferredHeight 反推。</summary>
    private int AddLine(string text, float size, bool bold, Color color, int y, int width, int indent)
    {
        var lbl = new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(width, 0),
            Font = Ui.Font(size, bold), ForeColor = color, BackColor = Color.Transparent,
            Location = new Point(indent == 0 ? 0 : indent, y),
            Margin = new Padding(0),
        };
        _content.Controls.Add(lbl);
        return y + lbl.PreferredHeight + Ui.S(this, 2);
    }
}
