using AppClone.Core;

namespace AppClone;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 卸载/一键还原强制规则（R7）：恢复被接管的桌面快捷方式
        if (args.Length >= 1 && string.Equals(args[0], "--restore-all-rules", StringComparison.OrdinalIgnoreCase))
        {
            AutoLaunchRule.RestoreAll();
            return 0;
        }

        // 命令行模式：--launch <appId> <cloneId>  （供桌面快捷方式直接启动分身）
        if (args.Length >= 3 && string.Equals(args[0], "--launch", StringComparison.OrdinalIgnoreCase))
        {
            return LaunchHeadless(args[1], args[2]);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
        {
            try
            {
                Paths.EnsureAll();
                File.AppendAllText(Path.Combine(Paths.DataRoot, "launch.log"),
                    $"[{DateTime.Now:HH:mm:ss}] UI线程异常: {e.Exception}\n");
            }
            catch { }
        };
        Application.Run(new MainForm());
        return 0;
    }

    private static int LaunchHeadless(string appId, string cloneId)
    {
        try
        {
            var store = Store.Load();
            var app = store.FindApp(appId);
            var clone = store.FindClone(appId, cloneId);
            if (app == null || clone == null)
            {
                MessageBox.Show("找不到对应的分身，可能已被删除。", "应用分身",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            var outcome = Launcher.Launch(store, app, clone);
            if (!outcome.Ok)
            {
                Log($"启动失败: {outcome.Message}");
                MessageBox.Show(outcome.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            try { MessageBox.Show(ex.Message, "应用分身", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            return 1;
        }
    }

    private static void Log(string line)
    {
        try
        {
            Paths.EnsureAll();
            File.AppendAllText(Path.Combine(Paths.DataRoot, "launch.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
        }
        catch { }
    }
}
