using System.Windows.Forms;
using AppClone;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>R10 快照管理：列出某分身的全部快照，支持一键恢复与删除。</summary>
public class SnapshotsForm : Form
{
    private readonly AppEntry _app;
    private readonly CloneEntry _clone;
    private readonly FlowLayoutPanel _list = null!;
    private readonly Label _empty = null!;

    public SnapshotsForm(AppEntry app, CloneEntry clone)
    {
        _app = app;
        _clone = clone;
        Text = $"快照管理 · {app.Name} - {clone.Name}";
        Size = new Size(460, 380);
        MinimumSize = new Size(380, 280);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Ui.Bg;
        Font = Ui.Font(9.5F);
        Icon = MainForm.TryAppIcon();

        var header = Ui.Label("该分身的快照（纯目录复制，不压缩）", 9.5F, true, Ui.TextPrimary)
        { Location = new Point(20, 16), AutoSize = true };

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Ui.Bg, Padding = new Padding(12, 8, 12, 8),
            Location = new Point(0, 44), Size = new Size(460, 280),
        };

        _empty = Ui.Label("还没有快照。\n在分身菜单选择「重置分身数据（先存快照）」即可创建。",
            9.5F, false, Ui.TextSecondary) { Location = new Point(24, 70), AutoSize = true, Visible = false };

        var btnNew = new AppleButton("＋ 立即创建快照", AppleButton.Style.Filled, 9F)
        { Size = new Size(130, 32), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Location = new Point(20, Height - 50) };
        btnNew.Click += (_, _) => CreateNow();

        Controls.Add(header);
        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(btnNew);
        Shown += (_, _) => { Ui.RoundWindow(this); RefreshList(); };
    }

    private void RefreshList()
    {
        _list.SuspendLayout();
        _list.Controls.Clear();
        var stamps = Snapshot.ListStamps(_app.Id, _clone.Id);
        _empty.Visible = stamps.Count == 0;
        foreach (var s in stamps)
            _list.Controls.Add(MakeCard(s));
        _list.ResumeLayout();
    }

    private Panel MakeCard(string stamp)
    {
        var card = new AppleCard
        {
            Width = _list.ClientSize.Width - 24, Height = 56, Margin = new Padding(2, 4, 2, 4),
            Radius = 12, BackColor = Ui.Card, DrawBorder = true,
        };
        var name = Ui.Label(stamp.Replace('_', ' '), 10F, true, Ui.TextPrimary) { Location = new Point(16, 10) };
        var hint = Ui.Label("点击「恢复」将用该快照覆盖当前分身数据", 8.5F, false, Ui.TextSecondary)
        { Location = new Point(16, 32) };

        var btnRestore = new AppleButton("恢复", AppleButton.Style.Filled, 9F)
        { Size = new Size(84, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(card.Width - 184, 12) };
        btnRestore.Click += (_, _) => Restore(stamp);

        var btnDel = new AppleButton("删除", AppleButton.Style.Secondary, 9F)
        { Size = new Size(84, 32), Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(card.Width - 92, 12) };
        btnDel.Click += (_, _) => Delete(stamp);

        card.Controls.Add(name);
        card.Controls.Add(hint);
        card.Controls.Add(btnRestore);
        card.Controls.Add(btnDel);
        return card;
    }

    private void CreateNow()
    {
        var (ok, msg, _) = Snapshot.Create(_app.Id, _clone.Id);
        MessageBox.Show(this, msg, ok ? "创建快照" : "创建快照失败", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        if (ok) RefreshList();
    }

    private void Restore(string stamp)
    {
        if (MessageBox.Show(this, $"确定恢复到快照 {stamp.Replace('_', ' ')} 吗？\n当前分身数据将被覆盖。",
                "恢复快照", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Launcher.Stop(_app.Id, _clone.Id);
        var (ok, msg) = Snapshot.Restore(_app.Id, _clone.Id, stamp);
        MessageBox.Show(this, msg, ok ? "恢复快照" : "恢复失败", MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        if (ok) Close();
    }

    private void Delete(string stamp)
    {
        if (MessageBox.Show(this, $"确定删除快照 {stamp.Replace('_', ' ')} 吗？", "删除快照",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (Snapshot.Delete(_app.Id, _clone.Id, stamp)) RefreshList();
    }
}
