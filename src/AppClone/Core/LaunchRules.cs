using System.Text.Json;

namespace AppClone.Core;

/// <summary>启动参数规则：内置默认 = 原 8 种 Chromium + Firefox 行为完全一致；用户规则可扩展。</summary>
public class LaunchRule
{
    public string Id { get; set; } = "";
    public string Family { get; set; } = "None";      // Chromium | Firefox | Electron | None
    public List<string> Match { get; set; } = new();           // 精确 exe 名（小写）
    public List<string> MatchContains { get; set; } = new();   // 名称包含（小写）
    public string Args { get; set; } = "";                     // 模板，占位 {udata}/{ffprofile}
}

/// <summary>
/// 启动参数规则表（R9，替代 Launcher.BuildAutoArgs 硬编码）。
/// 解析占位：{udata}=Paths.CloneUData(app,clone)，{ffprofile}=Paths.CloneFFProfile(app,clone)。
/// Electron/CEF：命中 MatchContains 且目录下存在 resources/*.asar → 追加 --user-data-dir。
/// </summary>
public static class LaunchRules
{
    /// <summary>内置默认（= 原 BuildAutoArgs 8 种行为，回归红线）。</summary>
    public static readonly List<LaunchRule> BuiltIn = new()
    {
        new LaunchRule
        {
            Id = "chromium", Family = "Chromium",
            Match = new() { "chrome.exe", "msedge.exe", "brave.exe", "opera.exe",
                            "vivaldi.exe", "chromium.exe", "dragon.exe", "360se.exe" },
            Args = "--user-data-dir=\"{udata}\" --no-first-run",
        },
        new LaunchRule
        {
            Id = "firefox", Family = "Firefox",
            Match = new() { "firefox.exe" },
            Args = "-profile \"{ffprofile}\" -no-remote",
        },
        // Electron/CEF：探测 resources/*.asar 后附加 --user-data-dir（追加能力，不影响内置 8 种）
        new LaunchRule
        {
            Id = "electron", Family = "Electron",
            MatchContains = new() { "electron" },
            Args = "",
        },
    };

    private static List<LaunchRule>? _user;
    private static readonly object _lock = new();

    /// <summary>解析给定 exe 的分身启动参数；无匹配返回空字符串。</summary>
    public static string Resolve(string exePath, string udata, string ffprofile)
    {
        var name = Path.GetFileName(exePath).ToLowerInvariant();
        var dir = Path.GetDirectoryName(exePath) ?? "";
        var rules = AllRules();

        foreach (var r in rules)
            if (r.Match.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return Apply(r, udata, ffprofile, dir);

        foreach (var r in rules)
            if (r.MatchContains.Any(m =>
                    name.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                    exePath.ToLowerInvariant().Contains(m, StringComparison.OrdinalIgnoreCase)))
                return Apply(r, udata, ffprofile, dir);

        return "";
    }

    private static string Apply(LaunchRule r, string udata, string ffprofile, string dir)
    {
        var args = r.Args
            .Replace("{udata}", udata, StringComparison.Ordinal)
            .Replace("{ffprofile}", ffprofile, StringComparison.Ordinal);

        if (r.Family == "Electron")
        {
            try
            {
                var res = Path.Combine(dir, "resources");
                if (Directory.Exists(res) && Directory.GetFiles(res, "*.asar").Length > 0)
                    args = string.IsNullOrEmpty(args)
                        ? $"--user-data-dir=\"{udata}\""
                        : args + $" --user-data-dir=\"{udata}\"";
            }
            catch { }
        }
        return args;
    }

    private static List<LaunchRule> AllRules()
    {
        var list = new List<LaunchRule>();
        list.AddRange(UserRules());   // 用户规则优先（可覆盖内置）
        list.AddRange(BuiltIn);
        return list;
    }

    private static List<LaunchRule> UserRules()
    {
        lock (_lock)
        {
            if (_user != null) return _user;
            _user = new List<LaunchRule>();
            try
            {
                if (File.Exists(Paths.LaunchRulesFile))
                {
                    var arr = JsonSerializer.Deserialize<List<LaunchRule>>(File.ReadAllText(Paths.LaunchRulesFile));
                    if (arr != null) _user = arr;
                }
            }
            catch { _user = new List<LaunchRule>(); }
            return _user;
        }
    }
}
