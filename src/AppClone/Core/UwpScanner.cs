using System.Diagnostics;
using System.Text;

namespace AppClone.Core;

/// <summary>
/// UWP / 微软商店应用枚举（R12，Q2 默认灰显）。
/// 经系统 PowerShell 的 Get-AppxPackage（6s 超时，可降级）；产出 Supported=false 的灰显条目。
/// 不写入任何启动参数——UWP 无法注入 DLL，四层隔离失效，仅用于"发现但提示不支持"。
/// </summary>
public static class UwpScanner
{
    private const int PowerShellTimeoutMs = 6000;

    public static async Task<List<DiscoveredApp>> ScanAsync(CancellationToken ct)
    {
        var list = new List<DiscoveredApp>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-AppxPackage | Select-Object -Property Name,InstallLocation | Format-List\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.Unicode,
            };
            using var p = Process.Start(psi);
            if (p == null) return list;

            var read = p.StandardOutput.ReadToEndAsync();
            if (await Task.WhenAny(read, Task.Delay(PowerShellTimeoutMs, ct)) != read)
            {
                try { p.Kill(); } catch { }
                return list;
            }
            var output = await read;
            try { p.WaitForExit(1000); } catch { }

            ParseAppxOutput(output, list);
        }
        catch { }
        return list;
    }

    private static void ParseAppxOutput(string output, List<DiscoveredApp> list)
    {
        string? name = null;
        string? installLoc = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush(ref name, ref installLoc, list);
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var val = line[(colon + 1)..].Trim();
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) name = val;
            else if (key.Equals("InstallLocation", StringComparison.OrdinalIgnoreCase)) installLoc = val;
        }
        Flush(ref name, ref installLoc, list);
    }

    private static void Flush(ref string? name, ref string? installLoc, List<DiscoveredApp> list)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            list.Add(new DiscoveredApp
            {
                Name = name,
                DisplayName = name,
                ExePath = "",                 // UWP 无法直接 CreateProcess 注入，留空
                InstallLocation = installLoc ?? "",
                Source = "uwp",
                Supported = false,            // 灰显「暂不支持分身」
                Publisher = "Microsoft Store",
            });
        }
        name = null;
        installLoc = null;
    }
}
