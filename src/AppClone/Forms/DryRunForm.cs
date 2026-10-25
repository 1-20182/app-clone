using System.Text;
using AppClone.Core;

namespace AppClone.Forms;

/// <summary>
/// 启动预览（F3 Dry Run）：把「本次启动到底会做什么」摊开给用户看，降低误操作。
/// 布局全部走 Dock 自动锚定 + 只读多行文本框（自带双向滚动、可复制），任何窗口尺寸下都不会重叠。
/// </summary>
public sealed class DryRunForm : Form
{
    private readonly CheckBox _dontAsk;

    /// <summary>用户勾选了「以后不再显示」。</summary>
    public bool DontAskAgain => _dontAsk.Checked;

    public DryRunForm(AppEntry app, CloneEntry clone, List<PreflightItem> items, Launcher.LaunchPlan plan)
    {
        Text = "启动预览（Dry Run）";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(720, 620);
        MinimumSize = new Size(560, 420);
        // PerMonitorV2：构造期逻辑像素交给 AutoScale 整树放大（高 DPI 下文本被裁的修复）
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        BackColor = Ui.BgOf();
        Font = Ui.Font(Ui.FBody);
        ShowInTaskbar = false;
        MaximizeBox = false;

        // —— 头部 ——
        var head = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Ui.BgOf(), Padding = new Padding(20, 12, 20, 0) };
        var title = Ui.Label($"即将启动：{app.Name} · {clone.Name}", Ui.FTitle, true, Ui.TextPrimaryOf());
        title.AutoSize = false;            // AutoSize 标签高度滞后量出，副标题固定 y=40 会被压住 —— 改定高定宽
        title.AutoEllipsis = true;
        title.Location = new Point(20, 12);
        title.Size = new Size(300, 32);
        var sub = Ui.Label("以下是本次启动将执行的操作与隔离配置，确认无误后再继续。", Ui.FCaption, false, Ui.TextSecondaryOf());
        sub.AutoSize = false;
        sub.AutoEllipsis = true;
        sub.Location = new Point(20, 50);
        sub.Size = new Size(300, 20);
        head.Controls.Add(title);
        head.Controls.Add(sub);
        head.Resize += (_, _) =>
        {
            int w = Math.Max(120, head.ClientSize.Width - 40);
            title.Width = w; sub.Width = w;
        };

        // —— 正文：只读文本框，滚动与复制由系统提供 ——
        var body = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
            ScrollBars = ScrollBars.Both, BorderStyle = BorderStyle.None,
            BackColor = Ui.CardOf(), ForeColor = Ui.TextPrimaryOf(),
            Font = new Font("Consolas", 9.5F), Text = BuildText(app, clone, items, plan),
        };
        var bodyWrap = new Panel { Dock = DockStyle.Fill, BackColor = Ui.CardOf(), Padding = new Padding(16, 12, 16, 12) };
        bodyWrap.Controls.Add(body);

        // —— 底部：勾选框 + 按钮（RightToLeft 流向，永不重叠）——
        var foot = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Ui.BgOf(), Padding = new Padding(20, 12, 20, 12) };
        _dontAsk = new CheckBox
        {
            Text = "以后不再显示（可在设置中重新开启）", AutoSize = true,
            Font = Ui.Font(Ui.FBody), ForeColor = Ui.TextSecondaryOf(), Location = new Point(20, 20),
        };

        var btnOk = new AppleButton("确认启动", AppleButton.Style.Filled) { Size = new Size(104, 34) };
        var btnCancel = new AppleButton("取消", AppleButton.Style.Secondary) { Size = new Size(84, 34) };
        btnOk.Margin = Padding.Empty;      // 默认 3px 外边距会把按钮挤出 34px 的停靠高度，底部越界 3px
        btnOk.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent,
            Padding = new Padding(0),
        };
        btnCancel.Margin = new Padding(8, 0, 0, 0);
        actions.Controls.Add(btnOk);
        actions.Controls.Add(btnCancel);

        foot.Controls.Add(_dontAsk);
        foot.Controls.Add(actions);

        // Dock=Fill 必须先加入；Top / Bottom 后加入 → 各占一边
        Controls.Add(bodyWrap);
        Controls.Add(head);
        Controls.Add(foot);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private static string BuildText(AppEntry app, CloneEntry clone, List<PreflightItem> items, Launcher.LaunchPlan plan)
    {
        var sb = new StringBuilder();

        // 预检查
        sb.AppendLine("【启动前预检查】");
        if (items.Count == 0)
        {
            sb.AppendLine("  全部通过，未发现问题");
        }
        else
        {
            foreach (var i in items)
            {
                string tag = i.Blocking ? "阻断" : i.Level switch
                {
                    PreflightSeverity.Error => "错误",
                    PreflightSeverity.Warn => "警告",
                    _ => "提示",
                };
                sb.Append("  [").Append(tag).Append("] ").Append(i.Title);
                if (i.Detail.Length > 0) sb.Append(" —— ").Append(i.Detail);
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        // 目标
        sb.AppendLine("【目标】");
        sb.AppendLine($"  应用      : {app.Name}");
        sb.AppendLine($"  程序      : {app.ExePath}");
        sb.AppendLine($"  分身      : {clone.Name}");
        sb.AppendLine($"  数据目录  : {Paths.CloneDir(app.Id, clone.Id)}");
        sb.AppendLine($"  隔离模式  : {(clone.DeepIsolation ? "深度隔离（注册表重定向 + 多开解锁）" : "标准隔离（仅环境变量 + 数据目录）")}");
        sb.AppendLine($"  设备分身  : {(clone.DeviceClone ? $"开（{plan.DeviceName}）" : "关")}");
        sb.AppendLine();

        // 步骤
        sb.AppendLine("【将执行的操作】");
        for (int i = 0; i < plan.Steps.Count; i++)
            sb.Append("  ").Append(i + 1).Append(". ").AppendLine(plan.Steps[i]);
        sb.AppendLine();

        // 环境变量
        sb.AppendLine("【环境变量重定向】");
        foreach (var kv in plan.Env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append("  ").Append(kv.Key.PadRight(20)).Append(" = ").AppendLine(kv.Value ?? "");
        sb.AppendLine();

        // 注册表与注入
        sb.AppendLine("【注册表与注入】");
        sb.AppendLine($"  私有键    : HKCU\\{plan.RegistrySubKey}");
        sb.AppendLine($"  注入组件  : {plan.DllPath ?? "（无：标准隔离或组件缺失）"}");
        if (plan.AutoArgs.Length > 0)
            sb.AppendLine($"  附加参数  : {plan.AutoArgs}");
        sb.AppendLine();
        sb.AppendLine("说明：真实 HKCU / HKLM 不会被修改；分身的注册表读写全部落在上述私有键内。");

        return sb.ToString();
    }
}
