using System.Windows.Forms;

namespace AppClone.Core;

/// <summary>
/// 托盘封装：NotifyIcon + 右键菜单（打开 / 新建分身 / 最近启动 / 退出）。
/// 悬浮文字由 SetRunningCount 更新（可显示运行数）；图标由构造传入（调用方用 TryAppIcon 兜底，允许为 null）。
/// </summary>
public sealed class TrayContext : IDisposable
{
    public event Action? OpenRequested;
    public event Action? NewRequested;
    public event Action? ExitRequested;
    /// <summary>增量 1.4.0（F4）：托盘直接打开分身管理中心。</summary>
    public event Action? ManagerRequested;
    /// <summary>增量 1.4.0（F5）：托盘直接打开操作日志。</summary>
    public event Action? LogsRequested;

    private readonly Icon _icon;   // 必须在构造赋值（避免 CS0191）
    private readonly NotifyIcon _notify;

    public TrayContext(Icon icon)
    {
        _icon = icon;   // 允许为 null：调用方用 TryAppIcon 兜底，null 时 NotifyIcon 用系统默认图标
        _notify = new NotifyIcon
        {
            Icon = _icon,
            Text = "应用分身",
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>构建右键菜单。recent 为最近启动项（最多 recentN 条），点击打开主窗口。</summary>
    public void BuildMenu(IEnumerable<(string appName, string cloneName, string appId, string cloneId)> recent, int recentN = 5)
    {
        // A8：释放旧菜单实例，避免每次重建泄漏一个 ContextMenuStrip
        if (_notify.ContextMenuStrip != null)
        {
            var old = _notify.ContextMenuStrip;
            _notify.ContextMenuStrip = null;
            old.Dispose();
        }

        var cms = new ContextMenuStrip();
        cms.Items.Add(new ToolStripMenuItem("打开应用分身", null, (_, _) => OpenRequested?.Invoke()));
        cms.Items.Add(new ToolStripMenuItem("新建分身", null, (_, _) => NewRequested?.Invoke()));
        cms.Items.Add(new ToolStripMenuItem("分身管理中心", null, (_, _) => ManagerRequested?.Invoke()));
        cms.Items.Add(new ToolStripMenuItem("操作日志", null, (_, _) => LogsRequested?.Invoke()));

        var items = (recent ?? System.Array.Empty<(string, string, string, string)>()).Take(recentN).ToArray();
        if (items.Length > 0)
        {
            cms.Items.Add(new ToolStripSeparator());
            foreach (var r in items)
            {
                var label = string.IsNullOrEmpty(r.cloneName) ? r.appName : $"{r.appName} · {r.cloneName}";
                cms.Items.Add(new ToolStripMenuItem(label, null, (_, _) => OpenRequested?.Invoke()));
            }
        }

        cms.Items.Add(new ToolStripSeparator());
        cms.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke()));
        _notify.ContextMenuStrip = cms;
    }

    /// <summary>更新悬浮文字：showCount 时显示 "应用分身 · 运行 N/总数"，否则纯 "应用分身"。</summary>
    public void SetRunningCount(int running, int total, bool showCount)
    {
        _notify.Text = showCount
            ? $"应用分身 · 运行 {running}/{total}"
            : "应用分身";
    }

    public void Dispose()
    {
        try { _notify.Visible = false; _notify.Dispose(); } catch { }
    }
}
