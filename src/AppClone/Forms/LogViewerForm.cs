using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;
using AppClone;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 操作日志查看器（F5）：把 data\logs\ops-*.jsonl 解析成可读表格，
/// 默认只显示当前分身相关的记录（日志是全应用共享一份流水，不筛会淹掉用户真正想看的那几行）。
/// </summary>
public sealed class LogViewerForm : Form
{
    private readonly AppEntry? _app;
    private readonly CloneEntry? _clone;
    private readonly ListView _list = null!;
    private readonly CheckBox _onlyThis;
    private readonly Label _hint = null!;

    public Action? ExportDiagnosticsRequested;

    public LogViewerForm(AppEntry? app, CloneEntry? clone)
    {
        _app = app;
        _clone = clone;

        Text = app == null ? "操作日志" : $"操作日志 · {app.Name} - {clone?.Name}";
        Size = new Size(940, 600);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterParent;
        // PerMonitorV2：构造期逻辑像素交给 AutoScale 整树放大（高 DPI 下文本被裁的修复）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        Icon = MainForm.TryAppIcon();
        ShowInTaskbar = false;

        var head = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Ui.BgOf(), Padding = new Padding(20, 12, 20, 0) };
        var title = Ui.Label("操作流水（data\\logs\\ops-*.jsonl）", Ui.FSection, true, Ui.TextPrimaryOf());
        title.Location = new Point(20, 10);
        _hint = Ui.Label("", Ui.FCaption, false, Ui.TextSecondaryOf());
        _hint.AutoSize = false;            // 内容含完整日志文件路径，AutoSize 会右溢出窗口
        _hint.AutoEllipsis = true;
        _hint.Location = new Point(20, 36);
        _hint.Size = new Size(300, 18);
        _hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        head.Controls.Add(title);
        head.Controls.Add(_hint);
        // 头部是 Dock=Top，宽度随窗体走；这里同步提示行宽度（锚定差值以真实宽度为基准）
        head.Resize += (_, _) => { _hint.Width = head.ClientSize.Width - 40; };
        _hint.Width = Math.Max(120, head.ClientSize.Width - 40);

        _onlyThis = new CheckBox
        {
            Text = clone == null ? "只看当前应用" : "只看该分身", AutoSize = true,
            Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextPrimaryOf(),
            Checked = clone != null, Location = new Point(20, 12),
        };
        _onlyThis.CheckedChanged += (_, _) => Reload();

        var btnRefresh = new AppleButton("刷新", AppleButton.Style.Secondary, 9F) { Size = new Size(72, 32) };
        btnRefresh.Click += (_, _) => Reload();
        var btnOpen = new AppleButton("打开日志目录", AppleButton.Style.Secondary, 9F) { Size = new Size(112, 32) };
        btnOpen.Click += (_, _) => OpenFolder(Paths.LogsDir);
        var btnDiag = new AppleButton("导出诊断包", AppleButton.Style.Secondary, 9F) { Size = new Size(108, 32) };
        btnDiag.Click += (_, _) => ExportDiagnosticsRequested?.Invoke();
        var btnClose = new AppleButton("关闭", AppleButton.Style.Filled, 9F) { Size = new Size(84, 32) };
        btnClose.Click += (_, _) => Close();

        var foot = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Ui.BgOf(), Padding = new Padding(20, 12, 20, 12) };
        _onlyThis.Location = new Point(20, 18);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Ui.BgOf(),
        };
        btnClose.Margin = new Padding(10, 0, 0, 0);
        btnDiag.Margin = new Padding(10, 0, 0, 0);
        btnOpen.Margin = new Padding(10, 0, 0, 0);
        btnRefresh.Margin = new Padding(0);
        actions.Controls.Add(btnClose);
        actions.Controls.Add(btnDiag);
        actions.Controls.Add(btnOpen);
        actions.Controls.Add(btnRefresh);
        foot.Controls.Add(_onlyThis);
        foot.Controls.Add(actions);

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, GridLines = false, BorderStyle = BorderStyle.FixedSingle,
            BackColor = Ui.CardOf(), ForeColor = Ui.TextPrimaryOf(), Font = Ui.Font(9F),
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        // 列宽不走控件缩放（ColumnHeader 不是 Control），按 DPI 手动换算
        int S(int v) => Ui.S(this, v);
        _list.Columns.Add("时间", S(150));
        _list.Columns.Add("级别", S(62));
        _list.Columns.Add("区域", S(90));
        _list.Columns.Add("说明", S(600));

        // 停靠顺序：Fill 先加入
        Controls.Add(_list);
        Controls.Add(head);
        Controls.Add(foot);

        Shown += (_, _) => { Ui.RoundWindow(this); Reload(); };
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        int total = 0, shown = 0;
        try
        {
            foreach (var line in Log.Tail(500))
            {
                total++;
                var row = Parse(line);
                if (_onlyThis.Checked)
                {
                    if (_clone != null && row.CloneId != _clone.Id) continue;
                    if (_clone == null && _app != null && row.AppId != _app.Id) continue;
                }
                var item = new ListViewItem(row.Ts);
                item.SubItems.Add(row.Level);
                item.SubItems.Add(row.Area);
                item.SubItems.Add(row.Message);
                item.ForeColor = row.Level switch
                {
                    "Error" => Ui.DangerOf(),
                    "Warn" => Ui.WarnOf(),
                    _ => Ui.TextPrimaryOf(),
                };
                item.UseItemStyleForSubItems = true;
                if (row.Step == "rollback")
                {
                    // 回滚 / 破坏性操作单独标色，翻日志时一眼能扫到「什么时候动过用户数据」
                    item.SubItems[3].Text = "[回滚留痕] " + item.SubItems[3].Text;
                    item.SubItems[3].ForeColor = Ui.DangerOf();
                    item.UseItemStyleForSubItems = false;
                }
                _list.Items.Add(item);
                shown++;
            }
        }
        catch { }
        finally { _list.EndUpdate(); }

        _hint.Text = $"日志保留 {Log.KeepDays} 天 · 今日共 {total} 条，显示 {shown} 条 · 文件：{Log.OpsFile}";
    }

    private sealed record ParsedRow(string Ts, string Level, string Area, string Message, string? AppId, string? CloneId, string? Step);

    /// <summary>解析一行流水。解析失败时把原文塞进「说明」列 —— 宁可难看，也不能让日志消失。</summary>
    private static ParsedRow Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string Get(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            string? GetOrNull(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
            return new ParsedRow(Get("ts"), Get("level"), Get("area"), Get("message"),
                GetOrNull("appId"), GetOrNull("cloneId"), GetOrNull("step"));
        }
        catch
        {
            return new ParsedRow("", "?", "", line, null, null, null);
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
