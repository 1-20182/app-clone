using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AppClone.Core;

/// <summary>
/// 游戏平台库扫描（R13，可开关）：Steam（libraryfolders.vdf v1 + appmanifest_*.acf）、
/// Epic（Manifests\*.item JSON）。作为 AppDiscovery 的可选源。纯文件解析，零依赖。
/// </summary>
public static class GameLibrary
{
    private static readonly Regex KvLine = new(@"""(?<k>[^""]+)""\s*""(?<v>[^""]*)""", RegexOptions.Compiled);

    public static async Task<List<DiscoveredApp>> ScanSteam(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var list = new List<DiscoveredApp>();
            try
            {
                using var steamKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                    .OpenSubKey(@"Software\Valve\Steam");
                var steamExe = steamKey?.GetValue("SteamExe") as string;
                if (string.IsNullOrEmpty(steamExe)) return list;
                var steamDir = Path.GetDirectoryName(steamExe);
                if (steamDir == null) return list;

                var libs = new List<string> { steamDir };
                var vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf)) libs.AddRange(ParseLibraryFolders(vdf));

                foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (ct.IsCancellationRequested) break;
                    var common = Path.Combine(lib, "steamapps", "common");
                    var appDir = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(appDir)) continue;
                    foreach (var acf in SafeEnumFiles(appDir, "appmanifest_*.acf"))
                    {
                        if (ct.IsCancellationRequested) break;
                        var (name, instDir) = ParseAppManifest(acf);
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(instDir)) continue;
                        var gameDir = Path.Combine(common, instDir);
                        if (!Directory.Exists(gameDir)) continue;
                        var exe = Directory.GetFiles(gameDir, "*.exe", SearchOption.AllDirectories)
                            .FirstOrDefault(x =>
                            {
                                var fn = Path.GetFileName(x).ToLowerInvariant();
                                return !fn.Contains("crashhandler") && !fn.Contains("redist")
                                    && !fn.Contains("unityplayer") && !fn.Contains("installscript")
                                    && !fn.Contains("_commonredist");
                            });
                        if (exe != null && File.Exists(exe))
                            list.Add(new DiscoveredApp
                            {
                                Name = name, DisplayName = name, ExePath = exe, IconPath = exe,
                                Source = "steam", Publisher = "Steam",
                            });
                    }
                }
            }
            catch { }
            return list;
        }, ct);
    }

    public static async Task<List<DiscoveredApp>> ScanEpic(CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var list = new List<DiscoveredApp>();
            try
            {
                var manifests = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (!Directory.Exists(manifests)) return list;
                foreach (var file in Directory.GetFiles(manifests, "*.item"))
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var root = doc.RootElement;
                        string? name = GetStr(root, "DisplayName")
                            ?? GetStr(root, "LaunchCommand")
                            ?? Path.GetFileNameWithoutExtension(file);
                        string? installLoc = GetStr(root, "InstallLocation");
                        string? launchExe = GetStr(root, "LaunchExecutable");
                        if (string.IsNullOrEmpty(installLoc) || string.IsNullOrEmpty(launchExe)) continue;
                        var exe = Path.Combine(installLoc, launchExe);
                        if (File.Exists(exe))
                            list.Add(new DiscoveredApp
                            {
                                Name = name, DisplayName = name, ExePath = exe, IconPath = exe,
                                Source = "epic", Publisher = "Epic Games",
                            });
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }, ct);
    }

    private static IEnumerable<string> ParseLibraryFolders(string vdf)
    {
        foreach (var line in File.ReadLines(vdf))
        {
            var m = KvLine.Match(line);
            if (!m.Success) continue;
            var key = m.Groups["k"].Value;
            var val = m.Groups["v"].Value;
            // v1：键为整数序号，值为库路径
            if (key.All(char.IsDigit) && !string.IsNullOrWhiteSpace(val) && Directory.Exists(val))
                yield return val;
        }
    }

    private static (string Name, string InstDir) ParseAppManifest(string acf)
    {
        string name = "", inst = "";
        foreach (var line in File.ReadLines(acf))
        {
            var m = KvLine.Match(line);
            if (!m.Success) continue;
            var k = m.Groups["k"].Value;
            var v = m.Groups["v"].Value;
            if (k == "name") name = v;
            else if (k == "installdir") inst = v;
        }
        return (name, inst);
    }

    private static IEnumerable<string> SafeEnumFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static string? GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
