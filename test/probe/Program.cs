using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using AppClone.Core;

namespace F14Probe;

/// <summary>
/// F1–F6 逻辑回归探针：直接引用编译产物 AppClone.dll，逐项断言新代码的真实行为。
///
/// 安全约定：数据目录被重定向到本工程输出目录下的 tmp\（环境变量 FENSHEN_DATA_DIR），
/// 探针过程绝不读写用户真实的应用分身数据目录，也不启动任何被杀进程 / GUI 窗口。
/// 退出码 0 表示全部通过。
/// </summary>
internal static class Program
{
    /// <summary>输出缓冲。刻意不叫 Log —— 那会遮蔽 AppClone.Core.Log。</summary>
    private static readonly StringBuilder Out = new();
    private static int _pass, _fail, _skip;

    private static void T(string name, bool ok, string detail = "")
    {
        if (ok) _pass++; else _fail++;
        Out.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "   :: " + detail : ""));
    }

    private static void Skip(string name, string why = "")
    {
        _skip++;
        Out.AppendLine("SKIP  " + name + (why.Length > 0 ? "   :: " + why : ""));
    }

    private static void Head(string title) => Out.AppendLine().AppendLine("—— " + title + " ——");

    private static int Main()
    {
        string tmp = Path.Combine(AppContext.BaseDirectory, "tmp");
        try { Directory.CreateDirectory(tmp); } catch { }
        Environment.SetEnvironmentVariable("FENSHEN_DATA_DIR", tmp);

        // ---------- Paths ----------
        Head("Paths");
        T("Paths 遵循 FENSHEN_DATA_DIR", Paths.DataRoot == tmp, Paths.DataRoot);
        Paths.EnsureAll();
        T("compat.json 位于数据目录内", Paths.CompatFile == Path.Combine(tmp, "compat.json"), Paths.CompatFile);
        T("logs 目录位于数据目录内（F5）", Paths.LogsDir == Path.Combine(tmp, "logs"), Paths.LogsDir);
        T("logs 目录已随 EnsureAll 创建", Directory.Exists(Paths.LogsDir));

        // ---------- Settings 新字段默认值 ----------
        Head("Settings 默认值");
        T("Settings.ShowDryRun 默认开启", Settings.Current.ShowDryRun);
        T("Settings.MinFreeDiskGB 默认 2", Settings.Current.MinFreeDiskGB == 2, Settings.Current.MinFreeDiskGB.ToString());
        T("Settings.WriteOpsLog 默认开启", Settings.Current.WriteOpsLog);
        T("Settings.MinLogLevel 默认 Info", Settings.Current.MinLogLevel == LogLevel.Info, Settings.Current.MinLogLevel.ToString());
        T("Settings.BadgeShortcutIcon 默认开启", Settings.Current.BadgeShortcutIcon);
        T("Settings.TaskbarGrouping 默认关闭（实验性）", !Settings.Current.TaskbarGrouping);

        // ---------- Preflight ----------
        Head("Preflight（F3）");
        string sys64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
        if (File.Exists(sys64)) T("64 位 exe 判定为 64 位", Preflight.IsExe64Bit(sys64), sys64);
        else Skip("64 位 exe 判定", "未找到 System32\\notepad.exe");

        string sys32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "notepad.exe");
        if (File.Exists(sys32) && !string.Equals(sys32, sys64, StringComparison.OrdinalIgnoreCase))
            T("32 位 exe 判定为 32 位", !Preflight.IsExe64Bit(sys32), sys32);
        else Skip("32 位 exe 判定", "未找到不同的 SysWOW64\\notepad.exe");

        T("路径不存在时保守按 64 位处理", Preflight.IsExe64Bit(Path.Combine(tmp, "nope.exe")));

        var missing = new AppEntry { Name = "缺程序", ExePath = Path.Combine(tmp, "nope.exe") };
        var missItems = Preflight.Check(missing, null);
        T("目标程序缺失被判为阻断", Preflight.HasBlocking(missItems), Preflight.Format(missItems).Replace("\n", " | "));
        T("Preflight.Format 产出可读文本", Preflight.Format(missItems).Contains("目标程序不存在"));

        // ---------- CompatibilityDb ----------
        Head("CompatibilityDb（F1）");
        string edge = Path.Combine(tmp, "msedge.exe");
        File.WriteAllText(edge, "x");
        var browser = new AppEntry { Name = "Edge", ExePath = edge, Category = AppClassifier.CategoryConstants.Browser };
        T("浏览器内核 → 内置推断 Supported",
            CompatibilityDb.Resolve(browser, out var nBrowser) == CompatLevel.Supported, nBrowser);

        var uwp = new AppEntry { Name = "商店应用", ExePath = "", Category = AppClassifier.CategoryConstants.Uwp };
        T("UWP → 内置推断 Unsupported",
            CompatibilityDb.Resolve(uwp, out var nUwp) == CompatLevel.Unsupported, nUwp);

        string fake = Path.Combine(tmp, "wechat.exe");
        File.WriteAllText(fake, "x");
        var social = new AppEntry { Name = "微信", ExePath = fake, Category = AppClassifier.CategoryConstants.Social };

        T("未实测应用初始为 Unknown",
            CompatibilityDb.Resolve(social, out var n0) == CompatLevel.Unknown, n0);

        CompatibilityDb.Report(fake, true);
        T("启动成功后自学习为 Supported",
            CompatibilityDb.Resolve(social, out var n1) == CompatLevel.Supported, n1);

        CompatibilityDb.Report(fake, false, "测试用失败");
        T("单次失败降为 Partial",
            CompatibilityDb.Resolve(social, out var n2) == CompatLevel.Partial, n2);

        CompatibilityDb.Report(fake, false, "测试用失败");
        CompatibilityDb.Report(fake, false, "测试用失败");
        T("连续 3 次失败转 Unsupported",
            CompatibilityDb.Resolve(social, out var n3) == CompatLevel.Unsupported, n3);

        CompatibilityDb.SetManual(fake, CompatLevel.Supported, "用户确认可用");
        CompatibilityDb.Report(fake, false, "自学习不应覆盖用户标记");
        T("用户标记不被自学习覆盖",
            CompatibilityDb.Resolve(social, out var n4) == CompatLevel.Supported, n4);

        T("compat.json 已落盘", File.Exists(Paths.CompatFile));
        string json = File.Exists(Paths.CompatFile) ? File.ReadAllText(Paths.CompatFile) : "";
        T("枚举以可读字符串持久化", json.Contains("\"Supported\"") && json.Contains("\"user\""),
            json.Replace("\r", "").Replace("\n", " ").Trim());

        CompatibilityDb.Reset(fake);
        T("Reset 后回到内置推断",
            CompatibilityDb.Resolve(social, out _) == CompatLevel.Unknown);

        T("四态文案完整",
            CompatibilityDb.Label(CompatLevel.Supported) == "✓ 可用" &&
            CompatibilityDb.Label(CompatLevel.Partial) == "⚠ 部分" &&
            CompatibilityDb.Label(CompatLevel.Unsupported) == "✗ 不支持" &&
            CompatibilityDb.Label(CompatLevel.Unknown) == "— 未测试");

        // ---------- SourceWatch ----------
        Head("SourceWatch（F2）");
        string src = Path.Combine(tmp, "app.exe");
        File.WriteAllText(src, "v1");
        var sApp = new AppEntry { Name = "源应用", ExePath = src };
        var clone = new CloneEntry { Name = "分身 1" };

        T("新分身初始无基线", !SourceWatch.HasBaseline(clone));
        SourceWatch.Stamp(sApp, clone);
        T("打标后具备基线", SourceWatch.HasBaseline(clone), clone.SourceExeMtimeAtCreate);

        T("基线未变 → None",
            SourceWatch.Check(sApp, new[] { clone }, out _, out _, out _) == SourceChangeKind.None);

        System.Threading.Thread.Sleep(1100);
        File.WriteAllText(src, "v2-内容更长");
        var k1 = SourceWatch.Check(sApp, new[] { clone }, out _, out _, out var r1);
        T("源文件被替换 → ExeReplaced", k1 == SourceChangeKind.ExeReplaced, k1 + " / " + r1);

        File.Delete(src);
        var k2 = SourceWatch.Check(sApp, new[] { clone }, out _, out _, out var r2);
        T("源路径丢失 → PathMissing", k2 == SourceChangeKind.PathMissing, k2 + " / " + r2);

        // 恢复源文件后再验证「无基线」分支：Check 会先判路径存在性，用例顺序错了会命中 PathMissing
        File.WriteAllText(src, "v1");
        T("无基线的老数据 → Unknown（不误报升级）",
            SourceWatch.Check(sApp, new[] { new CloneEntry() }, out _, out _, out _) == SourceChangeKind.Unknown);

        // ---------- Store 向后兼容 ----------
        Head("数据向后兼容");
        string legacyStore =
            "{ \"apps\": [ { \"Id\": \"a1\", \"Name\": \"旧应用\", \"ExePath\": \"" +
            src.Replace("\\", "\\\\") +
            "\", \"AddedAt\": \"2026-01-01T00:00:00\" } ], \"clones\": { \"a1\": [ { \"Id\": \"c1\", \"Name\": \"分身 1\", \"CreatedAt\": \"2026-01-01T00:00:00\" } ] } }";
        File.WriteAllText(Paths.StoreFile, legacyStore);
        var store = Store.Load();
        var oldApp = store.FindApp("a1");
        var oldClone = oldApp != null ? store.FindClone("a1", "c1") : null;
        T("老 apps.json 载入不丢应用/分身", oldApp != null && oldClone != null,
            oldApp == null ? "app=null" : (oldClone == null ? "clone=null" : oldClone.Name));
        T("老数据新字段取默认值",
            oldClone != null && oldClone.SourceVersionAtCreate == "" && oldClone.SourceExeSizeAtCreate == 0);
        T("老数据隔离开关保持默认 true",
            oldClone != null && oldClone.DeepIsolation && oldClone.DeviceClone);

        // ---------- Launcher.Describe ----------
        Head("Launcher.Describe（F3 Dry Run 数据源）");
        var plain = new CloneEntry { Name = "分身 A", DeepIsolation = false, DeviceClone = false };
        var plan = Launcher.Describe(sApp, plain);
        T("Describe 环境变量 ≥ 9 项", plan.Env.Count >= 9, "count=" + plan.Env.Count);
        T("USERPROFILE 指向分身 home",
            (plan.Env.TryGetValue("USERPROFILE", out var up) ? up ?? "" : "").Replace("/", "\\").EndsWith("home"),
            plan.Env.TryGetValue("USERPROFILE", out var up2) ? up2 ?? "" : "");
        T("标准隔离不产出注入组件", plan.DllPath == null && !plan.DeepIsolation);
        T("Describe 步骤含进程树说明",
            plan.Steps.Count >= 4 && plan.Steps.Any(s => s.Contains("进程树")), "steps=" + plan.Steps.Count);

        var deep = new CloneEntry { Name = "分身 B", DeepIsolation = true, DeviceClone = false };
        var plan2 = Launcher.Describe(sApp, deep);
        T("深度隔离 → 写入 FENSHEN_REG_SUBKEY", plan2.Env.ContainsKey("FENSHEN_REG_SUBKEY"), plan2.RegistrySubKey);
        T("深度隔离 → 步骤含注入项", plan2.Steps.Any(s => s.Contains("注入")), string.Join(" / ", plan2.Steps));
        T("MUTEX_TAG 为 cloneId 前 12 位",
            plan2.Env.TryGetValue("FENSHEN_MUTEX_TAG", out var mt) && mt == deep.Id[..Math.Min(12, deep.Id.Length)],
            plan2.Env.TryGetValue("FENSHEN_MUTEX_TAG", out var mt2) ? mt2 ?? "" : "");
        T("不匹配内核规则 → 无附加参数", plan2.AutoArgs.Length == 0, "autoArgs=[" + plan2.AutoArgs + "]");

        // —— 隔离完整性：Electron/Node 直接问 shell32/userenv 的场景（环境变量盖不住） ——
        Head("隔离完整性（已知文件夹 / 用户名 / 设备指纹）");
        T("深度隔离 → 注入分身 home",
            plan2.Env.TryGetValue("FENSHEN_CLONE_HOME", out var ch) && ch == Paths.CloneHome(sApp.Id, deep.Id),
            plan2.Env.TryGetValue("FENSHEN_CLONE_HOME", out var ch2) ? ch2 ?? "" : "missing");
        T("深度隔离 → Roaming/Local/LocalLow 三项齐备",
            plan2.Env.TryGetValue("FENSHEN_ROAMING", out var rr) && rr == Paths.AppDataRoaming(sApp.Id, deep.Id)
            && plan2.Env.TryGetValue("FENSHEN_LOCAL", out var lo) && lo == Paths.AppDataLocal(sApp.Id, deep.Id)
            && plan2.Env.TryGetValue("FENSHEN_LOCAL_LOW", out var llw) && llw == Paths.AppDataLocalLow(sApp.Id, deep.Id));
        T("LocalLow 目录布局 = home\\AppData\\LocalLow",
            Paths.AppDataLocalLow("a", "b").Replace("/", "\\").EndsWith("home\\AppData\\LocalLow"),
            Paths.AppDataLocalLow("a", "b"));
        T("标准隔离不写已知文件夹重定向（守旧行为）",
            !plan.Env.ContainsKey("FENSHEN_CLONE_HOME") && !plan.Env.ContainsKey("FENSHEN_LOCAL_LOW"));

        var devA = new CloneEntry { Name = "分身 C", DeepIsolation = true, DeviceClone = true };
        var plan3 = Launcher.Describe(sApp, devA);
        T("设备分身 → USERNAME 与 FENSHEN_USER_NAME 同为 FS_ 前缀",
            plan3.Env.TryGetValue("USERNAME", out var un) && un is not null && un.StartsWith("FS_")
            && plan3.Env.TryGetValue("FENSHEN_USER_NAME", out var fu) && fu == un,
            plan3.Env.TryGetValue("USERNAME", out var un2) ? un2 ?? "" : "missing");
        T("设备分身 → COMPUTERNAME 与 FENSHEN_DEVICE_NAME 一致",
            plan3.Env.TryGetValue("COMPUTERNAME", out var cn) && cn == plan3.Env["FENSHEN_DEVICE_NAME"],
            plan3.Env.TryGetValue("COMPUTERNAME", out var cn2) ? cn2 ?? "" : "missing");
        var devB = new CloneEntry { Name = "分身 D", DeepIsolation = true, DeviceClone = true };
        var plan4 = Launcher.Describe(sApp, devB);
        T("不同分身假用户名不同、同分身稳定",
            plan3.Env["USERNAME"] != plan4.Env["USERNAME"]
            && Launcher.Describe(sApp, devA).Env["USERNAME"] == plan3.Env["USERNAME"],
            plan3.Env["USERNAME"] + " / " + plan4.Env["USERNAME"]);

        // ---------- F4 CloneOverview ----------
        Head("F4 克隆管理中心数据装配");
        string overviewStore =
            "{ \"apps\": [" +
            "  { \"Id\": \"a1\", \"Name\": \"Edge\", \"ExePath\": \"" + edge.Replace("\\", "\\\\") + "\", \"Publisher\": \"Microsoft\", \"Category\": \"browser\" }," +
            "  { \"Id\": \"a2\", \"Name\": \"微信\", \"ExePath\": \"" + fake.Replace("\\", "\\\\") + "\", \"Publisher\": \"Tencent\", \"Category\": \"social\" }" +
            "], \"clones\": {" +
            "  \"a1\": [ { \"Id\": \"c1\", \"Name\": \"分身 1\" }, { \"Id\": \"c2\", \"Name\": \"分身 2\" } ]," +
            "  \"a2\": [ { \"Id\": \"c3\", \"Name\": \"工作号\" } ] } }";
        File.WriteAllText(Paths.StoreFile, overviewStore);
        var ovStore = Store.Load();
        var rows = CloneOverview.Build(ovStore);

        T("管理中心行数 = 全部应用的分身总和", rows.Count == 3, "count=" + rows.Count);
        T("序号在应用内从 1 重新计数",
            rows.Count == 3 && rows[0].Index == 1 && rows[1].Index == 2 && rows[2].Index == 1,
            rows.Count == 3 ? $"{rows[0].Index}/{rows[1].Index}/{rows[2].Index}" : "n/a");
        T("行键为 appId|cloneId", rows.Count == 3 && rows[0].Key == "a1|c1", rows.Count == 3 ? rows[0].Key : "n/a");
        T("浏览器应用行 → 兼容性 Supported",
            rows.Count == 3 && rows[0].Compat == CompatLevel.Supported, rows.Count == 3 ? rows[0].Compat.ToString() : "n/a");
        T("未实测应用行 → 兼容性 Unknown",
            rows.Count == 3 && rows[2].Compat == CompatLevel.Unknown, rows.Count == 3 ? rows[2].Compat.ToString() : "n/a");

        T("按应用名筛选只留该应用", CloneOverview.Filter(rows, appName: "Edge").Count == 2);
        T("按关键词筛选命中分身名", CloneOverview.Filter(rows, query: "工作").Count == 1);
        T("按关键词筛选命中发布者", CloneOverview.Filter(rows, query: "tencent").Count == 1);
        T("按兼容性筛选", CloneOverview.Filter(rows, compat: CompatLevel.Supported).Count == 2);
        T("按运行状态筛选（无运行分身时）",
            CloneOverview.Filter(rows, state: RunStateFilter.Running).Count == 0
            && CloneOverview.Filter(rows, state: RunStateFilter.Stopped).Count == 3);
        T("空条件返回全量", CloneOverview.Filter(rows).Count == 3);
        T("列文案统一出口",
            CloneOverview.IsolationText(new CloneEntry()) == "深度隔离"
            && CloneOverview.IsolationText(new CloneEntry { DeepIsolation = false }) == "标准隔离"
            && CloneOverview.DeviceText(new CloneEntry()) == "开"
            && CloneOverview.RunningText(true) == "运行中");
        T("时长格式化",
            CloneOverview.FormatDuration(0) == "—"
            && CloneOverview.FormatDuration(45) == "45 秒"
            && CloneOverview.FormatDuration(150) == "2 分 30 秒"
            && CloneOverview.FormatDuration(11700) == "3 小时 15 分",
            $"{CloneOverview.FormatDuration(0)} / {CloneOverview.FormatDuration(45)} / {CloneOverview.FormatDuration(150)} / {CloneOverview.FormatDuration(11700)}");
        T("最后启动占位符", CloneOverview.LastLaunchText(default) == "—");

        // ---------- F5 日志 / 崩溃报告 / 诊断包 ----------
        Head("F5 日志分级与崩溃报告");
        Settings.Current.WriteOpsLog = true;
        Settings.Current.MinLogLevel = LogLevel.Info;

        T("日志保留天数 == 7", Log.KeepDays == 7, Log.KeepDays.ToString());
        T("流水文件名含当日日期",
            Path.GetFileName(Log.OpsFile) == $"ops-{DateTime.Now:yyyyMMdd}.jsonl", Log.OpsFile);

        int before = Log.Tail(5000).Count;
        Log.Info("probe", "流水写入自检", "a1", "c1");
        var lines = Log.Tail(5000);
        T("Log.Info 写入当日流水", lines.Count == before + 1, $"{before} → {lines.Count}");

        string last = lines.Count > 0 ? lines[^1] : "";
        bool parsedOk = false;
        try
        {
            using var doc = JsonDocument.Parse(last);
            var root = doc.RootElement;
            parsedOk = root.GetProperty("level").GetString() == "Info"
                    && root.GetProperty("area").GetString() == "probe"
                    && root.GetProperty("message").GetString() == "流水写入自检"
                    && root.GetProperty("appId").GetString() == "a1"
                    && root.GetProperty("cloneId").GetString() == "c1"
                    && (root.GetProperty("ts").GetString() ?? "").Length >= 19;
        }
        catch { }
        T("流水行是合法 JSON 且字段齐全", parsedOk, last);

        Log.Rollback("probe", "回滚留痕自检", "a1", "c1");
        var rollLines = Log.Tail(5000);
        T("Log.Rollback 标注 step=rollback",
            rollLines.Count > 0 && rollLines[^1].Contains("\"step\":\"rollback\"")
            && rollLines[^1].Contains("回滚留痕自检"),
            rollLines.Count > 0 ? rollLines[^1] : "");

        Settings.Current.MinLogLevel = LogLevel.Error;
        int opsBeforeWarn = Log.Tail(5000).Count;
        Log.Warn("probe", "这条低于最低级别，不应落盘");
        T("低于最低级别的日志不落盘", Log.Tail(5000).Count == opsBeforeWarn);
        Log.Error("probe", "这条达到最低级别，应落盘");
        T("达到级别的日志落盘", Log.Tail(5000).Count == opsBeforeWarn + 1);
        Settings.Current.MinLogLevel = LogLevel.Info;

        Settings.Current.WriteOpsLog = false;
        int opsBeforeOff = Log.Tail(5000).Count;
        Log.Error("probe", "总开关关闭时不应落盘");
        T("总开关关闭后完全不写流水", Log.Tail(5000).Count == opsBeforeOff);
        T("总开关关闭仍更新内存中的最后操作", Log.LastOp.Contains("总开关关闭时不应落盘"));
        Settings.Current.WriteOpsLog = true;

        var crashPath = Log.WriteCrashReport("探针自检", new InvalidOperationException("模拟崩溃"), "探针附加上下文", "a1", "c1");
        T("崩溃报告已写出", crashPath != null && File.Exists(crashPath), crashPath ?? "null");
        T("崩溃报告落在 data\\logs 目录",
            crashPath != null && crashPath.StartsWith(Paths.LogsDir, StringComparison.OrdinalIgnoreCase));
        string crashText = crashPath != null && File.Exists(crashPath) ? File.ReadAllText(crashPath) : "";
        T("崩溃报告含版本/最后操作/堆栈",
            crashText.Contains("\"appVersion\"") && crashText.Contains("\"lastOp\"")
            && crashText.Contains("模拟崩溃") && crashText.Contains("\"stack\"")
            && crashText.Contains("探针附加上下文"));
        T("兼容：旧 crash.log 仍追加摘要",
            File.Exists(Paths.LegacyCrashLog) && File.ReadAllText(Paths.LegacyCrashLog).Contains("探针自检"));

        string diagZip = Path.Combine(tmp, "diag.zip");
        var (diagOk, diagMsg) = Log.ExportDiagnostics(diagZip);
        T("导出诊断包成功", diagOk && File.Exists(diagZip), diagMsg.Replace("\n", " "));
        try
        {
            using var za = ZipFile.OpenRead(diagZip);
            var names = za.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            T("诊断包含 summary.txt", names.Contains("summary.txt"), string.Join(", ", names.Take(8)));
            T("诊断包含当日流水", names.Any(n => n.StartsWith("logs/ops-")), string.Join(", ", names.Take(8)));
            T("诊断包含 apps.json", names.Contains("apps.json"));
            T("诊断包不含 profiles / snapshots（用户真实数据）",
                !names.Any(n => n.Contains("profiles", StringComparison.OrdinalIgnoreCase)
                             || n.Contains("snapshot", StringComparison.OrdinalIgnoreCase)));
            string sum = "";
            var entry = za.GetEntry("summary.txt");
            if (entry != null) { using var sr = new StreamReader(entry.Open()); sum = sr.ReadToEnd(); }
            T("概要含版本与保留天数说明",
                sum.Contains("日志保留") && sum.Contains("不包含") && sum.Contains("程序版本"),
                sum.Split('\n').FirstOrDefault() ?? "");
        }
        catch (Exception ex) { T("诊断包结构可解析", false, ex.Message); }

        // ---------- F6 角标图标 ----------
        Head("F6 角标图标（ICO 容器）");
        string cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmdExe)) cmdExe = sys64;
        if (File.Exists(cmdExe))
        {
            string ico = Path.Combine(Paths.IconsDir, "badge-probe.ico");
            bool built = BadgeIcon.Build(ico, cmdExe, 3, ColorTranslator.FromHtml("#FF9500"));
            T("Build 生成多尺寸 ICO", built && File.Exists(ico), ico);

            byte[] bytes = File.Exists(ico) ? File.ReadAllBytes(ico) : Array.Empty<byte>();
            T("ICO 文件头正确（reserved=0 / type=1 / 4 帧）",
                bytes.Length > 6 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0
                && BitConverter.ToUInt16(bytes, 4) == 4,
                bytes.Length > 6 ? $"count={BitConverter.ToUInt16(bytes, 4)}" : "empty");

            bool entriesOk = bytes.Length >= 6 + 4 * 16;
            var sizes = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 4 && entriesOk; i++)
            {
                int at = 6 + i * 16;
                if (at + 16 > bytes.Length) { entriesOk = false; break; }
                int w = bytes[at] == 0 ? 256 : bytes[at];
                int h = bytes[at + 1] == 0 ? 256 : bytes[at + 1];
                int size = BitConverter.ToInt32(bytes, at + 8);
                int off = BitConverter.ToInt32(bytes, at + 12);
                sizes.Add(w);
                if (w != h) entriesOk = false;
                if (size <= 0 || off <= 0 || off + size > bytes.Length) entriesOk = false;
            }
            T("4 个目录项的尺寸/偏移/长度自洽", entriesOk, string.Join(",", sizes));

            // 256 帧用 PNG、小尺寸用 BMP(DIB)：各自验证魔数与头部
            int off256 = -1, off16 = -1;
            for (int i = 0; i < 4; i++)
            {
                int at = 6 + i * 16;
                int w = bytes[at] == 0 ? 256 : bytes[at];
                int off = BitConverter.ToInt32(bytes, at + 12);
                if (w == 256) off256 = off;
                if (w == 16) off16 = off;
            }
            T("256 帧为 PNG 内嵌",
                off256 > 0 && bytes.Length > off256 + 8 && bytes[off256] == 0x89 && bytes[off256 + 1] == 0x50
                && bytes[off256 + 2] == 0x4E && bytes[off256 + 3] == 0x47);
            T("16 帧为 BMP(DIB) 内嵌（biSize=40 / 高度写两倍）",
                off16 > 0 && bytes.Length > off16 + 12 && BitConverter.ToInt32(bytes, off16) == 40
                && BitConverter.ToInt32(bytes, off16 + 8) == 32,
                off16 > 0 ? $"biSize={BitConverter.ToInt32(bytes, off16)} biHeight={BitConverter.ToInt32(bytes, off16 + 8)}" : "n/a");

            // 最硬的验证：交给系统 Icon 解析器 —— 系统读不出来，快捷方式图标就是个白块
            try
            {
                using var icon16 = new Icon(ico, 16, 16);
                using var icon32 = new Icon(ico, 32, 32);
                T("系统 Icon 解析器可读且取到 16/32 尺寸",
                    icon16.Width == 16 && icon32.Width == 32,
                    $"{icon16.Width}x{icon16.Height} / {icon32.Width}x{icon32.Height}");
            }
            catch (Exception ex) { T("系统 Icon 解析器可读", false, ex.Message); }

            // 序号膨胀：两位数走「9+」收敛分支，不应抛异常
            string icoBig = Path.Combine(Paths.IconsDir, "badge-probe-big.ico");
            T("序号 > 9 走收敛分支且不崩",
                BadgeIcon.Build(icoBig, cmdExe, 42, ColorTranslator.FromHtml("#0A84FF")) && File.Exists(icoBig));

            // 缓存复用：源程序未变时第二次不应重绘
            var p1 = BadgeIcon.Ensure("probe-cache", 1, "", cmdExe);
            var t1 = p1 != null ? File.GetLastWriteTimeUtc(p1) : DateTime.MinValue;
            System.Threading.Thread.Sleep(1100);
            var p2 = BadgeIcon.Ensure("probe-cache", 1, "", cmdExe);
            var t2 = p2 != null ? File.GetLastWriteTimeUtc(p2) : DateTime.MinValue;
            T("Ensure 命中缓存不重复绘制", p1 != null && p1 == p2 && t1 == t2, $"{t1:HH:mm:ss.fff} / {t2:HH:mm:ss.fff}");
            T("图标路径落在 data\\icons",
                p1 != null && p1.StartsWith(Paths.IconsDir, StringComparison.OrdinalIgnoreCase), p1 ?? "null");

            var missingIcon = BadgeIcon.Ensure("probe-missing", 1, "", Path.Combine(tmp, "nope.exe"));
            T("源程序不存在时返回 null（调用方回退原图标）", missingIcon == null);
        }
        else
        {
            Skip("F6 角标图标", "找不到可用于提取图标的系统 exe");
        }

        // ---------- P1 启动密码锁 ----------
        Head("P1 启动密码锁");
        {
            AppLock.Set("");   // 探针目录可能残留上次运行的数据，先归零再断言
            T("默认未启用启动锁", !AppLock.Enabled);
            T("未启用时任意密码都放行", AppLock.Verify("whatever"));

            AppLock.Set("probe-1234");
            T("设置后启用", AppLock.Enabled);
            T("正确密码通过", AppLock.Verify("probe-1234"));
            T("错误密码拒绝", !AppLock.Verify("probe-12345"));
            T("空密码拒绝", !AppLock.Verify(""));
            T("密码不以明文落盘",
                !File.ReadAllText(Paths.SettingsFile).Contains("probe-1234", StringComparison.Ordinal));
            T("哈希串为 salt:hash 两段",
                Settings.Current.PasswordHash.Split(':').Length == 2);
            // 同一密码两次设置应得到不同哈希（随机盐）
            var first = Settings.Current.PasswordHash;
            AppLock.Set("probe-1234");
            T("随机盐：同密码二次设置哈希不同", Settings.Current.PasswordHash != first);
            T("二次设置后仍能校验", AppLock.Verify("probe-1234"));

            AppLock.Set("");
            T("空串关闭启动锁", !AppLock.Enabled);
        }

        // ---------- P1 整包导出 / 导入 ----------
        Head("P1 数据包导出 / 导入");
        {
            var pStore = Store.Load();
            var pApp = new AppEntry { Name = "probe-pack-app", ExePath = @"C:\probe\none.exe" };
            var pClone = new CloneEntry { Name = "pack-clone" };
            pStore.Model.Apps.Add(pApp);
            pStore.Model.Clones[pApp.Id] = new List<CloneEntry> { pClone };
            pStore.Save();

            var srcDir = Paths.CloneDir(pApp.Id, pClone.Id);
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "marker.txt"), "pack-data");

            var zip = Path.Combine(Paths.DataRoot, "probe-pack.zip");
            if (File.Exists(zip)) File.Delete(zip);
            var (ea, ec, bytes) = ConfigIo.ExportPackage(zip, pStore, new[] { pApp.Id });
            T("导出恰好 1 应用 1 分身", ea == 1 && ec == 1);
            T("导出写入了分身数据", bytes > 0 && File.Exists(zip));

            var pStore2 = Store.Load();
            int pBefore = pStore2.Model.Apps.Count;
            var (ia, ic, pErr) = ConfigIo.ImportPackage(zip, pStore2);
            T("整包导入无错误", pErr.Length == 0);
            T("整包导入新增应用与分身", ia == 1 && ic == 1 && pStore2.Model.Apps.Count == pBefore + 1);

            var pImported = pStore2.Model.Apps[^1];
            var pImportedClone = pStore2.Model.Clones[pImported.Id][0];
            T("导入后应用 ID 重新生成", pImported.Id != pApp.Id);
            T("分身数据目录已解压",
                File.Exists(Path.Combine(Paths.CloneDir(pImported.Id, pImportedClone.Id), "marker.txt")));

            // 回归：ExportAll 写的是数组，旧 Import 只认单对象 —— 这里必须能读回
            var pJson = Path.Combine(Paths.DataRoot, "probe-pack.json");
            ConfigIo.ExportAll(pJson, pStore);
            var (ja, _, pJerr) = ConfigIo.Import(pJson, pStore2);
            T("数组形态 JSON 可导入（旧 bug 回归）", pJerr.Length == 0 && ja >= 1);

            File.Delete(zip);
            File.Delete(pJson);
        }

        // ---------- P1 快捷方式伪装 ----------
        Head("P1 快捷方式伪装");
        {
            T("预设包含计算器 / 记事本 / 资源管理器", Disguise.All.Length == 3);
            T("未知 Key 取不到预设", Disguise.Get("nope") == null);
            T("取消伪装返回 null", Disguise.IconPath("") == null);

            var ico = Disguise.IconPath("notepad");
            T("生成伪装图标 .ico", ico != null && File.Exists(ico) && new FileInfo(ico).Length > 0);
            T("伪装图标可复用缓存", ico != null && Disguise.IconPath("notepad") == ico);
        }

        // ---------- P2 崩溃守护信号 ----------
        Head("P2 崩溃守护信号");
        {
            int fired = 0, code = -1;
            Action<string, string, int> handler = (_, _, c) => { fired++; code = c; };
            LaunchWatch.Crashed += handler;
            // 一个约 1 秒后以退出码 3 结束的进程：观察窗应判定「早退崩溃」并发出信号
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul & exit 3")
            { UseShellExecute = false, CreateNoWindow = true };
            var proc = System.Diagnostics.Process.Start(psi)!;
            LaunchWatch.Watch("guard-app", "guard-clone", @"C:\probe\none.exe", (uint)proc.Id);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (fired == 0 && sw.ElapsedMilliseconds < LaunchWatch.WatchMs + 6000)
                System.Threading.Thread.Sleep(120);
            LaunchWatch.Crashed -= handler;
            T("启动早退（非 0 退出）→ 触发崩溃守护信号", fired == 1 && code == 3,
                $"fired={fired} code={code} waited={sw.ElapsedMilliseconds}ms");
        }

        // ---------- P2 窗口位置记忆 ----------
        Head("P2 窗口位置记忆");
        {
            var ws = Settings.Current;
            ws.WindowX = 120; ws.WindowY = 80; ws.WindowW = 1280; ws.WindowH = 900; ws.WindowMaximized = true;
            ws.Save();
            // Settings.Load 是「只加载一次」的，回读交给反序列化，验证字段真的落到了磁盘
            var reread = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile))!;
            T("窗口边界四值 + 最大化状态可持久化",
                reread.WindowX == 120 && reread.WindowY == 80 && reread.WindowW == 1280 &&
                reread.WindowH == 900 && reread.WindowMaximized);

            // 复位，避免污染后续用例
            ws.WindowX = 0; ws.WindowY = 0; ws.WindowW = 0; ws.WindowH = 0; ws.WindowMaximized = false;
            ws.Save();
            var reread2 = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile))!;
            T("复位后 W/H=0 表示从未记录（回退默认居中）",
                reread2.WindowW == 0 && reread2.WindowH == 0 && !reread2.WindowMaximized);
        }

        // ---------- P3 冻结分身 ----------
        Head("P3 冻结分身");
        {
            var fStore = new Store();
            var fApp = new AppEntry { Name = "冻结应用", ExePath = src };   // src 是上面存在过的临时 exe
            var fFrozen = new CloneEntry { Name = "冻结分身", Frozen = true };
            var frozenOut = Launcher.Launch(fStore, fApp, fFrozen);
            T("冻结后拒绝启动（不落目录、不起进程）",
                !frozenOut.Ok && frozenOut.Message.Contains("冻结"), frozenOut.Message);

            var fOpen = new CloneEntry { Name = "正常分身" };
            T("Frozen 默认关闭（老数据解冻）", !fOpen.Frozen);

            // P3：网络代理 → 标准代理环境变量（Describe 与实际启动共用 BuildEnv）
            var proxyPlan = Launcher.Describe(fApp, new CloneEntry { Name = "代理分身", Proxy = "http://127.0.0.1:7890" });
            T("设置代理后注入 HTTP_PROXY / HTTPS_PROXY",
                proxyPlan.Env.TryGetValue("HTTPS_PROXY", out var px) && px == "http://127.0.0.1:7890",
                proxyPlan.Env.TryGetValue("HTTPS_PROXY", out var px2) ? px2 ?? "null" : "缺失");
            var plainPlan = Launcher.Describe(fApp, new CloneEntry { Name = "普通分身" });
            T("未设代理时不注入代理变量", !plainPlan.Env.ContainsKey("HTTPS_PROXY"));
        }

        // ---------- P3 新手教程 / 内置帮助 ----------
        Head("P3 新手教程 / 内置帮助");
        {
            var topics = HelpContent.All;
            T("内置帮助主题数 ≥ 10", topics.Length >= 10, topics.Length.ToString());
            T("每篇标题/正文/分类均非空",
                topics.All(t => t.Title.Trim().Length > 0 && t.Body.Trim().Length > 0 && t.Category.Trim().Length > 0));
            T("帮助标题不重复（导航不会出现同名项）",
                topics.Select(t => t.Title).Distinct().Count() == topics.Length);
            T("图文版式标记合法（只有 ## / · 两种前缀，且都带空格）",
                topics.SelectMany(t => t.Body.Replace("\r\n", "\n").Split('\n'))
                      .Where(l => l.StartsWith("#", StringComparison.Ordinal) || l.StartsWith("·", StringComparison.Ordinal))
                      .All(l => l.StartsWith("## ", StringComparison.Ordinal) || l.StartsWith("· ", StringComparison.Ordinal)));
            Settings.Current.Save();   // 确保上面各用例已落盘，再验证「旧文件无字段 → 默认 false」
            T("首次启动判定默认未完成（老配置也能自动播放一次）",
                !JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile))!.OnboardingDone,
                "旧 settings.json 无该字段时应反序列化为 false");

            var hs = Settings.Current;
            hs.OnboardingDone = true;
            hs.Save();
            var hsRead = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile))!;
            T("OnboardingDone=true 已落盘（跳过/完成后不再自动弹）", hsRead.OnboardingDone);

            hs.OnboardingDone = false;
            hs.Save();
            T("OnboardingDone 复位可再次触发教程",
                !JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile))!.OnboardingDone);
        }

        // ---------- P4 分身随软件启动 / 每天定时 ----------
        Head("P4 分身随软件启动 / 每天定时启动");
        {
            TimeSpan? P(string? s) => AutoLaunchPlan.ParseTime(s);
            T("HH:mm 合法解析", P("08:30") == new TimeSpan(8, 30, 0));
            T("00:00 与 23:59 边界可解析",
                P("00:00") == TimeSpan.Zero && P("23:59") == new TimeSpan(23, 59, 0));
            T("非法格式一律拒绝",
                P("8:30") is null && P("24:00") is null && P("08:60") is null &&
                P("") is null && P(null) is null && P("abcde") is null);

            var day = new DateTime(2026, 10, 22);
            T("未到点不触发", !AutoLaunchPlan.ShouldFire("08:30", day.AddHours(8).AddMinutes(29), null));
            T("到点触发", AutoLaunchPlan.ShouldFire("08:30", day.AddHours(8).AddMinutes(30), null));
            T("当天已触发过不重复",
                !AutoLaunchPlan.ShouldFire("08:30", day.AddHours(12), day.AddHours(8).AddMinutes(30)));
            T("次日同一时刻重新触发",
                AutoLaunchPlan.ShouldFire("08:30", day.AddDays(1).AddHours(8).AddMinutes(31), day.AddHours(8).AddMinutes(30)));
            T("错过 6 小时内补启", AutoLaunchPlan.ShouldFire("08:30", day.AddHours(14).AddMinutes(29), null));
            T("错过超过 6 小时不补启", !AutoLaunchPlan.ShouldFire("08:30", day.AddHours(14).AddMinutes(31), null));
            T("空 / 非法时间永不触发",
                !AutoLaunchPlan.ShouldFire("", day.AddHours(12), null) &&
                !AutoLaunchPlan.ShouldFire("25:00", day.AddHours(12), null));
            T("菜单显示值格式化", AutoLaunchPlan.Describe("08:30") == "08:30" && AutoLaunchPlan.Describe("bad") == "");

            var back = JsonSerializer.Deserialize<CloneEntry>(
                JsonSerializer.Serialize(new CloneEntry { AutoStart = true, AutoStartAt = "08:30" }))!;
            T("AutoStart / AutoStartAt 序列化往返", back.AutoStart && back.AutoStartAt == "08:30");
            var legacy = JsonSerializer.Deserialize<CloneEntry>("{}")!;
            T("旧 apps.json 无新字段 → 默认不自启、不定时", !legacy.AutoStart && legacy.AutoStartAt == "");

            // 任务栏分组：真正的 AppUserModelID 只能由注入 DLL 在目标进程内设置
            var probeApp = new AppEntry
            { Id = "probeapp", Name = "测试", ExePath = @"C:\Windows\System32\notepad.exe" };
            bool oldTb = Settings.Current.TaskbarGrouping;
            try
            {
                Settings.Current.TaskbarGrouping = true;
                var on = Launcher.Describe(probeApp, new CloneEntry { DeepIsolation = true });
                T("任务栏分组：开启 + 深度隔离 → 注入 AppUserModelID",
                    on.Env.TryGetValue("FENSHEN_TASKBAR_GROUP", out var id) &&
                    id != null && id.StartsWith("AppClone."));

                Settings.Current.TaskbarGrouping = false;
                var off = Launcher.Describe(probeApp, new CloneEntry { DeepIsolation = true });
                T("任务栏分组：关闭 → 不注入", !off.Env.ContainsKey("FENSHEN_TASKBAR_GROUP"));

                Settings.Current.TaskbarGrouping = true;
                var shallow = Launcher.Describe(probeApp, new CloneEntry { DeepIsolation = false });
                T("任务栏分组：未开深度隔离 → 不注入（DLL 未加载）",
                    !shallow.Env.ContainsKey("FENSHEN_TASKBAR_GROUP"));
            }
            finally
            {
                Settings.Current.TaskbarGrouping = oldTb;
            }
        }

        // ---------- 输出 ----------
        Out.AppendLine();
        Out.AppendLine($"结果：PASS={_pass}  FAIL={_fail}  SKIP={_skip}");
        Out.AppendLine($"临时数据目录（可安全删除）：{tmp}");
        Console.OutputEncoding = Encoding.UTF8;
        Console.Write(Out.ToString());
        return _fail == 0 ? 0 : 1;
    }
}
