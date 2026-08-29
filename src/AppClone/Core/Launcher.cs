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
///   绝大多数应用（Electron / Chromium / Firefox / 主流 IM）的登录数据与运行数据都落在这里。
/// 2. 深度隔离 —— 注入 sandbox DLL：
///    a) RegOverridePredefKey 将 HKCU 重定向到分身私有键（登录凭据、配置项隔离）；
///    b) IAT Hook 改名命名互斥体/信号量，绕过单实例限制，实现真正多开。
/// 3. 浏览器内核应用自动附加 --user-data-dir / -profile，走官方多实例通道。
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
    public static IReadOnlyList<RunningClone> Running => _running;

    public static bool IsRunning(string appId, string cloneId)
    {
        foreach (var r in _running)
        {
            if (r.AppId != appId || r.CloneId != cloneId) continue;

            // 1) Job 跟踪（进程未脱离时最准确）
            if (r.Job != IntPtr.Zero && JobActiveCount(r.Job) > 0) return true;

            // 2) 主进程存活
            try { if (r.Process != null && !r.Process.HasExited) return true; } catch { }

            // 3) 命令行兜底：浏览器类应用会把控制权交给脱离 Job 的后台进程，
            //    但其命令行仍带有分身目录标记（--user-data-dir / -profile 等）
            if (r.ExeName.Length > 0 && r.MarkerPath.Length > 0)
            {
                if ((DateTime.Now - r.MarkerCheckAt).TotalSeconds > 5)
                {
                    r.MarkerCheckAt = DateTime.Now;
                    r.MarkerFound = ScanCommandLineMarker(r.ExeName, r.MarkerPath);
                }
                if (r.MarkerFound) return true;
            }
        }
        return false;
    }

    public static event Action? StateChanged;

    public static LaunchOutcome Launch(Store store, AppEntry app, CloneEntry clone)
    {
        if (!File.Exists(app.ExePath))
            return LaunchOutcome.Fail($"目标程序不存在：{app.ExePath}");

        // 1. 准备分身数据目录
        var home = Paths.CloneHome(app.Id, clone.Id);
        try
        {
            Directory.CreateDirectory(Paths.AppDataRoaming(app.Id, clone.Id));
            Directory.CreateDirectory(Paths.AppDataLocal(app.Id, clone.Id));
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

        // 3. 环境变量重定向
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
        };
        // HOME 主要影响从 Unix 移植的应用（git/node 等）
        env["HOME"] = home;

        // 4. 深度隔离标记（DLL 据此执行注册表重定向与互斥体改名）
        if (clone.DeepIsolation)
        {
            env["FENSHEN_REG_SUBKEY"] = RegClone.EnvSubKey(app.Id, clone.Id);
            env["FENSHEN_MUTEX_TAG"] = clone.Id[..Math.Min(12, clone.Id.Length)];
        }

        // 4.5 设备分身（随机设备名 / MachineGuid / 卷序列号等设备指纹）
        if (clone.DeviceClone)
        {
            var deviceName = RegClone.EnsureDeviceFake(app.Id, clone.Id);
            env["FENSHEN_DEVICE_FAKE"] = "1";
            env["FENSHEN_DEVICE_NAME"] = deviceName;
            env["COMPUTERNAME"] = deviceName;
        }

        // 5. 应用类型自适应参数（官方多实例通道）
        string extraArgs = BuildAutoArgs(app, clone);
        string args = CombineArgs(app.Args, extraArgs);

        // 6. 创建挂起进程 → 注入 → 恢复
        Injector.LaunchedProcess? lp = null;
        bool injected = false;
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
                        // 注入失败不应影响分身启动：降级并继续
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
            return LaunchOutcome.Success(lp.Pid, msg);
        }
        catch (Exception ex)
        {
            // 启动流程中途失败：清掉挂起的子进程，避免残留僵尸
            try { Injector.Terminate(lp.Process); } catch { }
            return LaunchOutcome.Fail(ex.Message);
        }
        finally
        {
            Injector.Resume(lp.Thread);
            CloseHandleSafe(lp.Thread);

            var rec = new RunningClone
            {
                AppId = app.Id,
                CloneId = clone.Id,
                Pid = lp.Pid,
                ExeName = Path.GetFileNameWithoutExtension(app.ExePath),
                MarkerPath = Paths.CloneDir(app.Id, clone.Id),
            };
            try { rec.Process = Process.GetProcessById((int)lp.Pid); } catch { }
            rec.Job = CreateTrackingJob(lp.Process);
            _running.Add(rec);
            Task.Run(async () =>
            {
                try { await Task.Delay(3000); } catch { }
                StateChanged?.Invoke();
            });
        }
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

    /// <summary>把分身主进程纳入跟踪 Job（不设 kill 限制，仅用于运行状态统计）。</summary>
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
                out var info, System.Runtime.InteropServices.Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(),
                out _))
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

    /// <summary>读取目标进程的完整命令行（经 PEB，同用户进程无需特权）。</summary>
    private static string? ReadProcessCommandLine(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (NtQueryInformationProcess(h, 0, out var pbi,
                    System.Runtime.InteropServices.Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            bool wow64 = false;
            try { IsWow64Process(h, out wow64); } catch { }
            // x64: PEB+0x20=ProcessParameters, 参数块+0x70=CommandLine；x86: 0x10 / 0x40
            IntPtr ppOffset = wow64 ? (IntPtr)0x10 : (IntPtr)0x20;
            IntPtr cmdOffset = wow64 ? (IntPtr)0x40 : (IntPtr)0x70;
            int ptrSize = wow64 ? 4 : 8;

            if (!ReadProcessMemory(h, pbi.PebBaseAddress + ppOffset, out IntPtr pp, ptrSize)) return null;
            if (pp == IntPtr.Zero) return null;

            var us = new byte[ptrSize == 8 ? 16 : 12];
            if (!ReadProcessMemory(h, pp + cmdOffset, us, us.Length)) return null;
            int len = wow64
                ? BitConverter.ToUInt16(us, 0)
                : BitConverter.ToUInt16(us, 0);
            IntPtr strPtr = wow64
                ? new IntPtr(BitConverter.ToInt32(us, 4))
                : (IntPtr)BitConverter.ToInt64(us, 8);
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

    /// <summary>检查指定映像名进程中是否有命令行包含分身目录标记的（5 秒内共享缓存结果放调用方）。</summary>
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

    /// <summary>结束分身主进程（不追溯子进程树）。</summary>
    public static bool Stop(string appId, string cloneId)
    {
        var rec = _running.FirstOrDefault(r => r.AppId == appId && r.CloneId == cloneId);
        if (rec?.Process == null || rec.Process.HasExited) return false;
        try
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/PID {rec.Pid} /T /F")
            { CreateNoWindow = true, UseShellExecute = false });
            killer?.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }

    private static string? PickDll(bool isWow64) =>
        isWow64 ? TryFile("inject32.dll") : TryFile("inject64.dll");

    private static string? TryFile(string name)
    {
        var p = Path.Combine(Paths.SandboxDir, name);
        return File.Exists(p) ? p : null;
    }

    private static string BuildAutoArgs(AppEntry app, CloneEntry clone)
    {
        var name = Path.GetFileName(app.ExePath).ToLowerInvariant();
        var appId = app.Id; var cloneId = clone.Id;

        if (name is "chrome.exe" or "msedge.exe" or "brave.exe" or "opera.exe" or "vivaldi.exe"
            or "chromium.exe" or "dragon.exe" or "360se.exe")
            return $"--user-data-dir=\"{Paths.CloneUData(appId, cloneId)}\" --no-first-run";

        if (name == "firefox.exe")
            return $"-profile \"{Paths.CloneFFProfile(appId, cloneId)}\" -no-remote";

        return "";
    }

    private static string CombineArgs(string a, string b)
    {
        a = a?.Trim() ?? ""; b = b?.Trim() ?? "";
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + " " + b;
    }

    private static void CloseHandleSafe(IntPtr h) { try { CloseHandle(h); } catch { } }
}
