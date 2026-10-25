using System.Diagnostics;

namespace AppClone.Core;

/// <summary>
/// 启动观察窗（F1 自学习的执行者）：
/// 启动后监视该进程一个观察窗时长，据此推断应用是否真的能在分身里跑起来。
///
/// 判定规则（保守优先，宁可少报失败也不误伤）：
///   · 观察窗结束进程仍存活 → 记为成功；
///   · 进程已退出但退出码为 0 → 记为成功；
///   · 进程已退出但 <see cref="Launcher.IsRunning"/> 仍认为在运行（Chromium 类应用会
///     启动后交出主进程、由子进程接管）→ 记为成功；
///   · 其余非 0 退出 → 记为失败（⚠ 部分；连续 3 次转 ✗ 不支持）。
///
/// 用户主动「结束进程」会先经 <see cref="MarkUserStopped"/> 打标，观察窗消费该标记后直接跳过判定，
/// 避免把正常结束误判为兼容性失败。
/// </summary>
public static class LaunchWatch
{
    /// <summary>观察窗时长：太短抓不到早期崩溃，太长会让状态反馈显得迟钝。</summary>
    public const int WatchMs = 6000;

    private const int MaxPendingStops = 512;

    private static readonly object _lock = new();
    private static readonly HashSet<string> _userStopped = new();

    /// <summary>
    /// P2 崩溃守护信号：观察窗判定「启动后早退且非 0 退出」时触发（线程池线程，订阅方自行编组回 UI）。
    /// 只在启动期崩溃时触发，正常运行中的应用被用户关掉不会误报。
    /// </summary>
    public static event Action<string, string, int>? Crashed;

    /// <summary>标记「这个分身的结束是用户主动发起的」，观察窗据此跳过失败判定。</summary>
    public static void MarkUserStopped(string appId, string cloneId)
    {
        lock (_lock)
        {
            if (_userStopped.Count >= MaxPendingStops) _userStopped.Clear();
            _userStopped.Add(Key(appId, cloneId));
        }
    }

    /// <summary>启动观察窗（不阻塞调用方；全程异常安全）。</summary>
    public static void Watch(string appId, string cloneId, string? exePath, uint pid)
    {
        if (string.IsNullOrWhiteSpace(exePath) || pid == 0) return;

        Task.Run(() =>
        {
            // 句柄必须在进程还活着时打开：退出后 pid 可能被复用。
            // 之所以不只用 Process.ExitCode：对 GetProcessById 得到的对象，进程退出后它会抛
            // InvalidOperationException，而这里把它当成 0 就等于把崩溃误判为启动成功 ——
            // 实测崩溃守护信号永远发不出去，自学习也永远不会记录失败。
            IntPtr h = IntPtr.Zero;
            try { h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid); } catch { }

            Process? p = null;
            try { p = Process.GetProcessById((int)pid); }
            catch { CloseQuietly(h); return; }   // 进程已消失：无从判定，交给后续启动观察
            try
            {
                bool exited = p.WaitForExit(WatchMs);

                bool userStopped;
                lock (_lock) userStopped = _userStopped.Remove(Key(appId, cloneId));
                if (userStopped) return;

                if (!exited)
                {
                    CompatibilityDb.Report(exePath, true);
                    return;
                }

                int code = ExitCodeOf(p, h);

                if (code == 0) { CompatibilityDb.Report(exePath, true); return; }

                // 主进程退出但整棵树仍在（Chromium 类交付子进程）→ 视为启动成功
                try
                {
                    if (Launcher.IsRunning(appId, cloneId)) { CompatibilityDb.Report(exePath, true); return; }
                }
                catch { }

                // 先发守护信号再写兼容性库：兼容库写盘失败不能把崩溃信号一起吞掉
                try { Crashed?.Invoke(appId, cloneId, code); } catch { }
                CompatibilityDb.Report(exePath, false, $"启动后 {WatchMs / 1000} 秒内异常退出（退出码 {code}）");
            }
            catch { }
            finally { try { p.Dispose(); } catch { } CloseQuietly(h); }
        });
    }

    private static string Key(string appId, string cloneId) => appId + "|" + cloneId;

    // ------------------------------------------------------------------ 退出码

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SYNCHRONIZE = 0x00100000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 取已退出进程的退出码。优先问内核（可靠），拿不到再退回 Process.ExitCode。
    /// ponytail: OpenProcess 对提权进程会 AccessDenied，此时仍按 0（启动成功）处理 —— 宁可漏报不误伤；
    /// 要覆盖提权应用需要以服务/高权限代理观察，属另一量级改造。
    /// </summary>
    private static int ExitCodeOf(Process p, IntPtr handle)
    {
        if (handle != IntPtr.Zero && GetExitCodeProcess(handle, out uint raw)) return unchecked((int)raw);
        try { return p.ExitCode; } catch { return 0; }
    }

    private static void CloseQuietly(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        try { CloseHandle(handle); } catch { }
    }
}
