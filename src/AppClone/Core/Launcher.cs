using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AppClone.Core;

namespace AppClone.Core;

public record LaunchOutcome(bool Ok, string Message, uint Pid)
{
    public static LaunchOutcome Fail(string msg) => new(false, msg, 0);
    public static LaunchOutcome Success(uint pid, string msg = "") => new(true, msg, pid);
}

/// <summary>
/// 分身启动器：为每个分身构建独立的“应用环境”后启动目标程序。
///
/// 隔离手段（叠加生效）：
/// 1. 环境变量重定向 —— USERPROFILE / APPDATA / LOCALAPPDATA / TEMP / HOME 全部指向分身目录；
/// 2. 深度隔离 —— 注入 sandbox DLL（注册表重定向 + 互斥体改名）；
/// 3. 浏览器内核应用自动附加 --user-data-dir / -profile（规则表 LaunchRules，R9）。
/// </summary>
public static class Launcher
{
    public class RunningClone
    {
        public required string AppId;
        public required string CloneId;
        public required uint Pid;
        public DateTime StartedAt = DateTime.Now;
        public Process? Process;
        public IntPtr Job;   // 进程树 Job 句柄：Chromium 类应用主进程转后台后仍可准确判断运行中
        public string ExeName = "";
        public string MarkerPath = "";   // 分身目录：命令行探测（--user-data-dir 等）用
        public DateTime MarkerCheckAt = DateTime.MinValue;
        public bool MarkerFound;
    }

    private static readonly List<RunningClone> _running = new();
    private static readonly object _sync = new();
    public static IReadOnlyList<RunningClone> Running
    {
        get { lock (_sync) return _running.ToArray(); }   // A1：返回快照数组，避免跨线程枚举内部 List 抛 InvalidOperationException
    }

    public static bool IsRunning(string appId, string cloneId)
    {
        RunningClone? scanRec = null;

        lock (_sync)
        {
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                var r = _running[i];
                if (r.AppId != appId || r.CloneId != cloneId) continue;

                // 1) Job 跟踪（进程未脱离时最准确）
                bool jobActive = r.Job != IntPtr.Zero && JobActiveCount(r.Job) > 0;
                // 2) 主进程存活
                bool mainAlive = false;
                try { if (r.Process != null && !r.Process.HasExited) mainAlive = true; } catch { }

                if (jobActive || mainAlive) return true;

                // 3) 命令行兜底：仅当 Job 与主进程均判定失败时走（P-3），结果缓存 10s
                if (r.ExeName.Length > 0 && r.MarkerPath.Length > 0)
                {
                    if ((DateTime.Now - r.MarkerCheckAt).TotalSeconds <= 10)
                    {
                        if (r.MarkerFound) return true;
                        CleanupDeadRecord(_running, i);
                    }
                    else
                    {
                        // A3：慢扫描（枚举进程+读内存，可达数百 ms）不在锁内执行；
                        // 先预占缓存时间，防止并发调用方在扫描期间重复触发扫描
                        r.MarkerCheckAt = DateTime.Now;
                        scanRec = r;
                    }
                }
                else
                {
                    CleanupDeadRecord(_running, i);
                }
            }
        }

        if (scanRec == null) return false;

        // A3：出锁执行慢扫描，避免冻结所有并发调用方
        bool markerFound = ScanCommandLineMarker(scanRec.ExeName, scanRec.MarkerPath);

        lock (_sync)
        {
            // A3：重新定位（记录可能已被并发移除）：优先按对象引用，找不到按 AppId/CloneId 兜底；都没有则丢弃结果
            var cur = _running.FirstOrDefault(x => ReferenceEquals(x, scanRec))
                   ?? _running.FirstOrDefault(x => x.AppId == appId && x.CloneId == cloneId);
            if (cur != null)
            {
                cur.MarkerFound = markerFound;
                if (markerFound) return true;
                // 三项探测均失败 → 记录已死：立即清理，避免残留记录把已停止的分身误判为运行中
                CleanupDeadRecord(_running, _running.IndexOf(cur));
            }
        }
        return false;
    }

    /// <summary>三项探测均失败：释放记录持有的进程对象与 Job 句柄，并从跟踪表移除（A2/A3）。</summary>
    private static void CleanupDeadRecord(List<RunningClone> list, int index)
    {
        if (index < 0 || index >= list.Count) return;
        var r = list[index];
        try { r.Process?.Dispose(); } catch { }
        CloseHandleSafe(r.Job);   // A2：同步关闭 Job 句柄（未设 KILL_ON_JOB_CLOSE，不会误杀进程）
        list.RemoveAt(index);
    }

    public static event Action? StateChanged;

    public static LaunchOutcome Launch(Store store, AppEntry app, CloneEntry clone)
    {
        // P3：冻结在这里拦一道 —— 双击 / 热键 / 托盘 / 崩溃守护重启全部走本方法
        if (clone.Frozen)
            return LaunchOutcome.Fail("该分身已冻结，请先在右键菜单里解冻");

        if (!File.Exists(app.ExePath))
            return LaunchOutcome.Fail($"目标程序不存在：{app.ExePath}");

        // 1. 准备分身数据目录
        var home = Paths.CloneHome(app.Id, clone.Id);
        try
        {
            Directory.CreateDirectory(Paths.AppDataRoaming(app.Id, clone.Id));
            Directory.CreateDirectory(Paths.AppDataLocal(app.Id, clone.Id));
            Directory.CreateDirectory(Paths.AppDataLocalLow(app.Id, clone.Id));
            Directory.CreateDirectory(Paths.CloneTemp(app.Id, clone.Id));
        }
        catch (Exception ex) { return LaunchOutcome.Fail($"创建分身目录失败：{ex.Message}"); }

        // 2. 深度隔离：注册表快照
        string warn = "";
        if (clone.DeepIsolation)
        {
            if (!RegClone.EnsureCloneRegistry(app.Id, clone.Id, out var err))
                warn = $"注册表隔离未就绪（{err}），已降级为标准隔离";
        }

        // 3~4.5 环境变量重定向 + 深度隔离标记 + 设备分身指纹
        // —— 与 Launcher.Describe 共用 BuildEnv，保证 Dry Run 预览与实际启动逐字段一致
        var env = BuildEnv(app, clone);

        // 5. 应用类型自适应参数（规则表 R9，替代硬编码 BuildAutoArgs）
        string extraArgs = BuildAutoArgs(app, clone);
        string args = CombineArgs(app.Args, extraArgs);

        // 6. 创建挂起进程 → 注入 → 恢复
        Injector.LaunchedProcess? lp = null;
        bool injected = false;
        bool succeeded = false;   // A7：launch 失败（返回 Fail）时不加入跟踪记录
        try
        {
            lp = Injector.CreateSuspended(app.ExePath, args, Path.GetDirectoryName(app.ExePath) ?? "", env);
        }
        catch (Exception ex) { return LaunchOutcome.Fail(ex.Message); }

        try
        {
            if (clone.DeepIsolation)
            {
                var dll = PickDll(lp.IsWow64);
                if (dll == null)
                    warn = string.IsNullOrEmpty(warn) ? "缺少注入 DLL，已降级为标准隔离" : warn + "；缺少注入 DLL";
                else
                {
                    try { injected = Injector.InjectDll(lp.Process, dll); }
                    catch (Exception ex)
                    {
                        injected = false;
                        warn = string.IsNullOrEmpty(warn)
                            ? $"深度隔离组件注入失败（{ex.Message}），已降级为标准隔离"
                            : warn + $"；注入失败（{ex.Message}）";
                    }
                }
            }

            var msg = warn;
            if (!injected && clone.DeepIsolation && string.IsNullOrEmpty(warn))
                msg = "深度隔离组件未注入（应用可能仍单实例）";
            succeeded = true;
            return LaunchOutcome.Success(lp.Pid, msg);
        }
        catch (Exception ex)
        {
            try { Injector.Terminate(lp.Process); } catch { }
            return LaunchOutcome.Fail(ex.Message);
        }
        finally
        {
            Injector.Resume(lp.Thread);
            CloseHandleSafe(lp.Thread);

            if (succeeded)   // A7：仅启动成功才加入跟踪记录
            {
                var rec = new RunningClone
                {
                    AppId = app.Id, CloneId = clone.Id, Pid = lp.Pid,
                    ExeName = Path.GetFileNameWithoutExtension(app.ExePath),
                    MarkerPath = Paths.CloneDir(app.Id, clone.Id),
                };
                try { rec.Process = Process.GetProcessById((int)lp.Pid); } catch { }
                rec.Job = CreateTrackingJob(lp.Process);
                lock (_sync) _running.Add(rec);
                Task.Run(async () =>
                {
                    try { await Task.Delay(3000); } catch { }
                    StateChanged?.Invoke();
                });
            }

            // A2①：关闭 Injector 返回的原生 hProcess 句柄（成功路径跟踪 Job 已建立、
            // 失败路径 catch 中已 Terminate），两种情况关闭均安全，防句柄泄漏
            CloseHandleSafe(lp.Process);
        }
    }

    // ---------------------------- 启动预览（F3 Dry Run） ----------------------------

    /// <summary>启动计划快照：Dry Run 展示用，字段与 <see cref="Launch"/> 的实际行为一一对应。</summary>
    public record LaunchPlan(
        Dictionary<string, string?> Env,
        string? DllPath,
        string AutoArgs,
        string RegistrySubKey,
        string DeviceName,
        bool DeepIsolation,
        bool DeviceClone,
        List<string> Steps);

    /// <summary>
    /// 分身专用 Windows 用户名（设备指纹的一部分）。由分身 ID 派生：稳定、各分身不同、只含安全字符。
    /// </summary>
    private static string CloneUserName(CloneEntry clone)
    {
        var id = clone.Id.Replace("-", "");
        return "FS_" + (id.Length <= 8 ? id : id[..8]).ToUpperInvariant();
    }

    /// <summary>
    /// 环境变量重定向 + 深度隔离标记 + 设备指纹的唯一实现。
    /// Launch 与 Describe 共用同一份逻辑，从根上避免「预览说的」与「实际干的」漂移。
    /// 注意：设备分身会在此按需生成并持久化假设备数据（幂等，与首次启动时生成完全等价）。
    /// </summary>
    private static Dictionary<string, string?> BuildEnv(AppEntry app, CloneEntry clone)
    {
        var home = Paths.CloneHome(app.Id, clone.Id);
        var env = new Dictionary<string, string?>
        {
            ["USERPROFILE"] = home,
            ["HOMEDRIVE"] = Path.GetPathRoot(home)?.TrimEnd('\\') ?? "",
            ["HOMEPATH"] = home.Length > 3 ? home[2..] : "\\",
            ["APPDATA"] = Paths.AppDataRoaming(app.Id, clone.Id),
            ["LOCALAPPDATA"] = Paths.AppDataLocal(app.Id, clone.Id),
            ["TEMP"] = Paths.CloneTemp(app.Id, clone.Id),
            ["TMP"] = Paths.CloneTemp(app.Id, clone.Id),
            ["FENSHEN_CLONE"] = $"{app.Name}:{clone.Name}",
            ["HOME"] = home,
        };

        // 深度隔离标记（DLL 据此执行注册表重定向与互斥体改名）
        if (clone.DeepIsolation)
        {
            env["FENSHEN_REG_SUBKEY"] = RegClone.EnvSubKey(app.Id, clone.Id);
            env["FENSHEN_MUTEX_TAG"] = clone.Id[..Math.Min(12, clone.Id.Length)];

            // 已知文件夹（SHGetKnownFolderPath / GetUserProfileDirectory）重定向目标。
            // 只改环境变量不够：Electron / Node 的 app.getPath('appData') 走 shell32 API，
            // 不读 APPDATA 环境变量 —— 这是此前「分身共享同一份登录态」的主要泄漏点。
            env["FENSHEN_CLONE_HOME"] = home;
            env["FENSHEN_ROAMING"] = Paths.AppDataRoaming(app.Id, clone.Id);
            env["FENSHEN_LOCAL"] = Paths.AppDataLocal(app.Id, clone.Id);
            env["FENSHEN_LOCAL_LOW"] = Paths.AppDataLocalLow(app.Id, clone.Id);

            // 任务栏分组：DLL 在目标进程内调用 SetCurrentProcessExplicitAppUserModelID。
            // 快捷方式上的 AppUserModelID 只作用于 .lnk 本身，不会传给被启动的程序（旧实现的实际边界）。
            if (Settings.Current.TaskbarGrouping)
                env["FENSHEN_TASKBAR_GROUP"] = $"AppClone.{app.Id}.{clone.Id}";
        }

        // 设备分身（随机设备名 / MachineGuid / 卷序列号 / 用户名等设备指纹）
        if (clone.DeviceClone)
        {
            var deviceName = RegClone.EnsureDeviceFake(app.Id, clone.Id);
            var userName = CloneUserName(clone);
            env["FENSHEN_DEVICE_FAKE"] = "1";
            env["FENSHEN_DEVICE_NAME"] = deviceName;
            env["FENSHEN_USER_NAME"] = userName;
            env["COMPUTERNAME"] = deviceName;
            // 用户名也是常见指纹：GetUserName → 各分身不同；USERPROFILE 已被上面的 home 覆盖，
            // 应用不会因此拼到不存在的真实用户目录。
            env["USERNAME"] = userName;
            env["USERDOMAIN"] = deviceName;
        }

        // 网络代理：只写标准环境变量，让不同分身走不同出口；不遵守该约定的应用等同于没设
        if (!string.IsNullOrWhiteSpace(clone.Proxy))
        {
            env["HTTP_PROXY"] = clone.Proxy;
            env["HTTPS_PROXY"] = clone.Proxy;
            env["ALL_PROXY"] = clone.Proxy;
            env["http_proxy"] = clone.Proxy;
            env["https_proxy"] = clone.Proxy;
            env["all_proxy"] = clone.Proxy;
        }

        return env;
    }

    /// <summary>
    /// 生成「本次启动将执行什么」的预览（F3）。
    /// 只读：不创建进程、不修改目标程序；仅设备分身数据按需生成（幂等），以便预览显示真实设备名。
    /// </summary>
    public static LaunchPlan Describe(AppEntry app, CloneEntry clone)
    {
        var env = BuildEnv(app, clone);
        bool is64 = Preflight.IsExe64Bit(app.ExePath);
        string dll = Path.Combine(Paths.SandboxDir, is64 ? "inject64.dll" : "inject32.dll");
        bool dllExists = File.Exists(dll);

        string autoArgs = BuildAutoArgs(app, clone);
        string regKey = RegClone.EnvSubKey(app.Id, clone.Id);
        string deviceName = env.TryGetValue("FENSHEN_DEVICE_NAME", out var dn) ? dn ?? "" : "";

        var steps = new List<string>
        {
            $"校验目标程序：{app.ExePath}",
            $"创建 / 复用分身数据目录：{Paths.CloneDir(app.Id, clone.Id)}",
        };
        if (clone.DeepIsolation)
            steps.Add($"确保注册表私有键快照就绪：HKCU\\{regKey}" + (dllExists ? "" : "（组件缺失，将降级为标准隔离）"));
        steps.Add($"重定向 {env.Count} 个环境变量（USERPROFILE / APPDATA / LOCALAPPDATA / TEMP / HOME 等）");
        if (clone.DeepIsolation)
            steps.Add(dllExists
                ? $"注入隔离组件：{dll}（注册表重定向 + 多开互斥体改名）"
                : $"跳过注入：{dll} 不存在，本次为标准隔离");
        if (clone.DeviceClone)
            steps.Add($"伪造设备身份：计算机名 {deviceName}，并重定向 MachineGuid / 卷序列号 / BIOS 序列号");
        steps.Add(autoArgs.Length > 0
            ? $"附加内核专用参数：{autoArgs}"
            : "无内核专用参数（该应用不匹配浏览器内核规则）");
        steps.Add("以挂起态创建进程 → 注入 → 恢复运行，并用 Job Object 跟踪整棵进程树");

        return new LaunchPlan(env, clone.DeepIsolation && dllExists ? dll : null, autoArgs, regKey,
            deviceName, clone.DeepIsolation, clone.DeviceClone, steps);
    }

    // ---------------------------- 进程树跟踪 Job ----------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass,
        out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public uint ThisPeriodTotalUserTime;
        public uint ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    private const int JobObjectBasicAccountingInformation = 1;

    private static IntPtr CreateTrackingJob(IntPtr hProcess)
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            if (!AssignProcessToJobObject(job, hProcess)) { CloseHandle(job); return IntPtr.Zero; }
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    private static int JobActiveCount(IntPtr job)
    {
        if (QueryInformationJobObject(job, JobObjectBasicAccountingInformation,
                out var info, Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(), out _))
            return (int)info.ActiveProcesses;
        return 0;
    }

    // ---------------------------- 命令行兜底探测 ----------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr h, out bool wow64);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr h, int cls, out PROCESS_BASIC_INFORMATION info,
        int size, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_1;
        public IntPtr Reserved2_2;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    private static string? ReadProcessCommandLine(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (NtQueryInformationProcess(h, 0, out var pbi,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            bool wow64 = false;
            try { IsWow64Process(h, out wow64); } catch { }
            IntPtr ppOffset = wow64 ? (IntPtr)0x10 : (IntPtr)0x20;
            IntPtr cmdOffset = wow64 ? (IntPtr)0x40 : (IntPtr)0x70;
            int ptrSize = wow64 ? 4 : 8;

            if (!ReadProcessMemory(h, pbi.PebBaseAddress + ppOffset, out IntPtr pp, ptrSize)) return null;
            if (pp == IntPtr.Zero) return null;

            var us = new byte[ptrSize == 8 ? 16 : 12];
            if (!ReadProcessMemory(h, pp + cmdOffset, us, us.Length)) return null;
            int len = wow64 ? BitConverter.ToUInt16(us, 0) : BitConverter.ToUInt16(us, 0);
            IntPtr strPtr = wow64 ? new IntPtr(BitConverter.ToInt32(us, 4)) : (IntPtr)BitConverter.ToInt64(us, 8);
            if (len <= 0 || strPtr == IntPtr.Zero) return null;

            var data = new byte[len];
            if (!ReadProcessMemory(h, strPtr, data, len)) return null;
            return Encoding.Unicode.GetString(data);
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private static bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size) =>
        ReadProcessMemory(h, addr, buf, (IntPtr)size, out _);

    private static bool ReadProcessMemory(IntPtr h, IntPtr addr, out IntPtr value, int size)
    {
        var buf = new byte[size];
        if (!ReadProcessMemory(h, addr, buf, (IntPtr)size, out _)) { value = IntPtr.Zero; return false; }
        value = size == 8 ? (IntPtr)BitConverter.ToInt64(buf, 0) : new IntPtr(BitConverter.ToInt32(buf, 0));
        return true;
    }

    private static bool ScanCommandLineMarker(string exeName, string marker)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(exeName))
            {
                try
                {
                    string? cl = ReadProcessCommandLine(p.Id);
                    if (cl != null && cl.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 停止分身：按 Pid 强杀整棵进程树。全程异常安全——进程句柄失效/权限不足都不会抛出，
    /// 且无论 kill 结果如何都会移除跟踪记录（否则残留记录会把已停止的分身误判为运行中，阻断删除流程）。
    /// </summary>
    public static bool Stop(string appId, string cloneId)
    {
        RunningClone? rec;
        lock (_sync) rec = _running.FirstOrDefault(r => r.AppId == appId && r.CloneId == cloneId);
        bool ok = true;
        try
        {
            if (rec is { Pid: > 0 })
            {
                using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/PID {rec.Pid} /T /F")
                { CreateNoWindow = true, UseShellExecute = false });
                killer?.WaitForExit(5000);
            }
        }
        catch { ok = false; }
        finally
        {
            if (rec != null)
            {
                try { rec.Process?.Dispose(); } catch { }
                CloseHandleSafe(rec.Job);   // A2②：同步关闭 Job 句柄（未设 KILL_ON_JOB_CLOSE，不会误杀进程）
                lock (_sync) _running.Remove(rec);
            }
        }
        return ok;
    }

    private static string? PickDll(bool isWow64) =>
        isWow64 ? TryFile("inject32.dll") : TryFile("inject64.dll");

    private static string? TryFile(string name)
    {
        var p = Path.Combine(Paths.SandboxDir, name);
        return File.Exists(p) ? p : null;
    }

    /// <summary>R9：以规则表替代硬编码（行为与原 8 种 Chromium+Firefox 完全一致）。</summary>
    private static string BuildAutoArgs(AppEntry app, CloneEntry clone) =>
        LaunchRules.Resolve(app.ExePath, Paths.CloneUData(app.Id, clone.Id), Paths.CloneFFProfile(app.Id, clone.Id));

    private static string CombineArgs(string a, string b)
    {
        a = a?.Trim() ?? ""; b = b?.Trim() ?? "";
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + " " + b;
    }

    private static void CloseHandleSafe(IntPtr h) { try { CloseHandle(h); } catch { } }
}
