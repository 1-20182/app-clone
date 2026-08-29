using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AppClone.Core;

/// <summary>
/// 低层进程创建与 DLL 注入：
/// 1. 以 CREATE_SUSPENDED 创建目标进程（携带自定义环境块）；
/// 2. 向挂起的进程注入 sandbox DLL（LoadLibraryW 远程线程）；
/// 3. 恢复主线程运行。
/// </summary>
public static class Injector
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, IntPtr size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBase, byte[] buffer, IntPtr size, out IntPtr written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr attr, IntPtr stackSize,
        IntPtr lpStartAddress, IntPtr lpParameter, uint flags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(IntPtr hThread, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    private const uint PROCESS_CREATE_THREAD = 0x0002;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 4;
    private const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    public record LaunchedProcess(uint Pid, IntPtr Process, IntPtr Thread, bool IsWow64);

    /// <summary>创建挂起的目标进程并写入自定义环境块。</summary>
    public static LaunchedProcess CreateSuspended(string exePath, string arguments, string workingDir,
        IReadOnlyDictionary<string, string?> envOverrides)
    {
        var env = BuildEnvironmentBlock(envOverrides);

        var si = new STARTUPINFOW();
        si.cb = (uint)Marshal.SizeOf<STARTUPINFOW>();
        string cmd = $"\"{exePath}\"{(string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments)}";

        IntPtr envPtr = Marshal.AllocHGlobal(env.Length * 2);
        try
        {
            Marshal.Copy(env.ToCharArray(), 0, envPtr, env.Length);
            var ok = CreateProcessW(exePath, cmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, envPtr,
                string.IsNullOrWhiteSpace(workingDir) ? null : workingDir, ref si, out var pi);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法启动进程: {cmd}");
            IsWow64Process(pi.hProcess, out bool wow64);
            return new LaunchedProcess(pi.dwProcessId, pi.hProcess, pi.hThread, wow64);
        }
        finally
        {
            Marshal.FreeHGlobal(envPtr);
        }
    }

    /// <summary>向挂起进程注入 DLL 并等待加载完成。失败返回 false（不影响进程继续运行）。</summary>
    public static bool InjectDll(IntPtr hProcess, string dllPath)
    {
        byte[] dllPathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        var remote = VirtualAllocEx(hProcess, IntPtr.Zero, dllPathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero) return false;
        try
        {
            if (!WriteProcessMemory(hProcess, remote, dllPathBytes, dllPathBytes.Length, out _)) return false;

            var loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero) return false;

            var hThread = CreateRemoteThread(hProcess, IntPtr.Zero, IntPtr.Zero, loadLibrary, remote, 0, out _);
            if (hThread == IntPtr.Zero) return false;
            try
            {
                // 等待 DLL 完成加载（含注册表重定向与 Hook 安装），超时不视为致命
                WaitForSingleObject(hThread, 8000);
                GetExitCodeThread(hThread, out uint exitCode);
                return exitCode != 0 && exitCode != 0xFFFFFFFF;
            }
            finally { CloseHandle(hThread); }
        }
        finally
        {
            VirtualFreeEx(hProcess, remote, IntPtr.Zero, MEM_RELEASE);
        }
    }

    public static void Resume(IntPtr hThread) => ResumeThread(hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint exitCode);

    /// <summary>终止一个尚在挂起状态的子进程（启动流程中途失败时清理用）。</summary>
    public static void Terminate(IntPtr hProcess) => TerminateProcess(hProcess, 1);

    /// <summary>构造完整环境块字符串（UTF-16, "k=v\0" 序列 + 结束 \0）。</summary>
    private static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> overrides)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (object? k in Environment.GetEnvironmentVariables().Keys)
            dict[(string)k] = Environment.GetEnvironmentVariable((string)k);
        foreach (var (k, v) in overrides)
        {
            if (v == null) dict.Remove(k);
            else dict[k] = v;
        }
        var sb = new StringBuilder();
        foreach (var (k, v) in dict) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }
}
