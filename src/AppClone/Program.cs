using AppClone.Core;

namespace AppClone;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 全局异常兜底（含 UI 线程既有 handler 的增强）
        InstallGlobalExceptionHandlers();

        // 卸载/一键还原强制规则（R7）：恢复被接管的桌面快捷方式
        if (args.Length >= 1 && string.Equals(args[0], "--restore-all-rules", StringComparison.OrdinalIgnoreCase))
        {
            AutoLaunchRule.RestoreAll();
            return 0;
        }

        // 命令行模式：--launch <appId> <cloneId>  （供桌面快捷方式直接启动分身）
        //
        // 【必须在单实例互斥体之前分流】无头启动是一个「短命启动器」：建进程 → 注入 → 退出。
        // 它若与 GUI 抢同一把锁，会出现两类硬伤：
        //   1) 主程序开着时双击分身快捷方式 → 弹「应用分身已在运行」并被拦下，分身根本不启动
        //      （而快捷方式的卖点正是「免开主程序直达」）；
        //   2) 连续双击两个分身的快捷方式 → 后一个同样被拦下，多开直接失效。
        // 更糟的是该拦截走的是 MessageBox：在无人值守 / 脚本 / 计划任务场景下会**永久挂起**。
        // 故无头模式不参与 GUI 单实例保护。
        if (args.Length >= 3 && string.Equals(args[0], "--launch", StringComparison.OrdinalIgnoreCase))
        {
            return LaunchHeadless(args[1], args[2]);
        }

        // 单实例保护：仅 GUI 模式需要，防止用户重复打开主窗口
        using var mutex = new Mutex(true, @"Local\AppClone.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("应用分身已在运行", "应用分身", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        // F5：UI 线程异常 → 结构化崩溃报告（版本 / 最后操作 / 堆栈），而不只是一行文本
        Application.ThreadException += (_, e) => Log.WriteCrashReport("UI线程异常", e.Exception);

        // 启动密码锁（P1）：解锁失败直接退出，避免只挡住窗口、进程仍在后台常驻
        if (AppLock.Enabled)
        {
            using var lockForm = new Forms.LockForm();
            if (lockForm.ShowDialog() != DialogResult.OK) return 3;
        }

        Log.Info("app", $"主界面启动 · 版本 {Log.AppVersion} · 数据目录 {Paths.DataRoot}");
        Application.Run(new MainForm());
        Log.Info("app", "主界面已退出，进程即将结束");
        return 0;
    }

    /// <summary>注册域级兜底：UnhandledException / UnobservedTaskException 统一写结构化崩溃报告（包 try/catch 防再抛）。</summary>
    private static void InstallGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.WriteCrashReport("UnhandledException",
                e.ExceptionObject as Exception,
                e.ExceptionObject as Exception == null ? e.ExceptionObject?.ToString() : null);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.WriteCrashReport("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
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
                LogLine($"无头启动失败：找不到分身 {appId}/{cloneId}");
                MessageBox.Show("找不到对应的分身，可能已被删除。", "应用分身",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }

            // F5：桌面快捷方式直达也进流水，方便回答「我点了图标怎么没反应」
            Log.Info("launch", "无头启动（桌面快捷方式）", app.Id, clone.Id, "headless");

            // F3：无头模式（桌面快捷方式直达）只做「阻断级」预检查，不弹 Dry Run 预览 ——
            // 快捷方式的语义就是一键直达，中途弹窗会破坏体验。
            var preflight = Preflight.Check(app, clone);
            if (Preflight.HasBlocking(preflight))
            {
                var text = Preflight.Format(preflight).Replace('\n', ' ');
                Log.Error("launch", "预检查未通过：" + text, app.Id, clone.Id, "preflight");
                LogLine("预检查未通过: " + text);
                MessageBox.Show(Preflight.Format(preflight), "预检查未通过", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            var outcome = Launcher.Launch(store, app, clone);
            if (!outcome.Ok)
            {
                Log.Error("launch", "启动失败：" + outcome.Message, app.Id, clone.Id, "launch");
                LogLine($"启动失败: {outcome.Message}");
                MessageBox.Show(outcome.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            Log.Info("launch", $"已启动（PID {outcome.Pid}）" +
                (string.IsNullOrEmpty(outcome.Message) ? "" : " · " + outcome.Message), app.Id, clone.Id, "launch");
            // 注意：本进程随即退出，故此处不启用启动观察窗（观察窗需要存活的后台线程）。
            // 桌面快捷方式启动的分身不参与兼容性自学习，经主界面 / 热键启动的才会。
            return 0;
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("无头启动异常", ex, cloneId, appId, cloneId);
            LogLine(ex.ToString());
            try { MessageBox.Show(ex.Message, "应用分身", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            return 1;
        }
    }

    /// <summary>兼容旧版：launch.log 纯文本仍保留写入（README 已注明与 logs\ 的迁移关系）。</summary>
    private static void LogLine(string line)
    {
        try
        {
            Paths.EnsureAll();
            File.AppendAllText(Paths.LegacyLaunchLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
        }
        catch { }
    }
}
