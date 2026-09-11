using System.Text.RegularExpressions;

namespace AppClone.Core;

/// <summary>
/// 识别层（全静态纯函数）：路径规范化、去重合并、分类词典打分、推荐三档标签。
/// 共享知识：路径规范化唯一实现 NormalizedExePath，发现去重 / 已添加比对 / 强制规则比对均复用。
/// </summary>
public static class AppClassifier
{
    // —— 分类常量（共享知识 3）——
    public static class CategoryConstants
    {
        public const string Browser = "browser";
        public const string Social = "social";
        public const string Game = "game";
        public const string Dev = "dev";
        public const string Cloud = "cloud";
        public const string Office = "office";
        public const string Other = "other";
        public const string Uwp = "uwp";
    }

    public const string TierRecommended = "推荐分身";
    public const string TierTry = "可尝试";
    public const string TierSkip = "不建议";

    // —— 分类词典（PRD 第 2 节；命中即加分并置顶）——
    private static readonly (string Category, string[] Keywords)[] CategoryRules =
    {
        (CategoryConstants.Browser, new[] { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
            "360se", "chromium", "qqbrowser", "百分浏览器", "浏览器", "dragon", "edge" }),
        (CategoryConstants.Social, new[] { "wechat", "微信", "qq", "telegram", "discord", "slack", "钉钉",
            "飞书", "企业微信", "whatsapp", "line", "signal", "im", "tim", "微信" }),
        (CategoryConstants.Game, new[] { "steam", "epic", "epicgames", "wegame", "原神", "米哈游", "riot",
            "lol", "battle.net", "minecraft", "游戏", "网易云游戏", "wegame" }),
        (CategoryConstants.Dev, new[] { "vscode", "code", "cursor", "trae", "jetbrains", "postman",
            "github desktop", "开发", "android studio", "pycharm", "webstorm", "idea" }),
        (CategoryConstants.Cloud, new[] { "百度网盘", "阿里云盘", "onedrive", "dropbox", "google drive",
            "坚果云", "云盘", "115" }),
        (CategoryConstants.Office, new[] { "outlook", "thunderbird", "foxmail", "teams", "zoom", "notion",
            "obsidian", "office", "wps", "word", "excel" }),
    };

    // —— 排除规则：名称含以下关键词的应用（运行时/卸载器/驱动/安全软件）——
    private static readonly string[] ExcludeNameKeywords =
    {
        "卸载", "uninstall", "help", "帮助", "readme", "文档", "sdk", "runtime",
        "redistributable", "driver", "update", "补丁", "更新",
    };

    // —— 微软运行时发布者黑名单（PRD 第 2 节）——
    private static readonly string[] BlacklistedPublishers =
    {
        "microsoft visual c++", "visual c++", "microsoft .net", ".net runtime", "microsoft .net runtime",
        "windows driver", "microsoft windows sdk", "microsoft silverlight", "microsoft vc", "redist",
        "intel", "realtek", "nvidia", "amd", // 驱动/运行时厂商
    };

    // —— 源优先级（共享知识 10）：值越小优先级越高 ——
    private static int SourcePriority(string source) => source switch
    {
        "uninstall" => 0,
        "apppaths" => 1,
        "startmenu" => 2,
        "steam" => 3,
        "epic" => 4,
        "uwp" => 5,
        "winget" => 6,
        "manual" => 7,
        _ => 8,
    };

    /// <summary>路径规范化唯一实现（共享知识 1）：去引号 → 展开环境变量 → 绝对路径 → 小写。</summary>
    public static string NormalizeExePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try
        {
            p = p.Trim().Trim('"', '\'', ' ', '\t');
            p = Environment.ExpandEnvironmentVariables(p);
            p = Path.GetFullPath(p);
            return p.ToLowerInvariant();
        }
        catch
        {
            return p.Trim().ToLowerInvariant();
        }
    }

    /// <summary>名称归一化（共享知识 2）：去版本号 / 架构词 / 空白，转小写。</summary>
    public static string NormalizeName(string n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "";
        n = n.ToLowerInvariant();
        n = Regex.Replace(n, @"\d+(\.\d+)+", " ");
        n = n.Replace("(x64)", "").Replace("64-bit", "").Replace("(x86)", "").Replace("32-bit", "");
        n = new string(n.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return n.Trim();
    }

    /// <summary>单条分类：返回 (Category, Recommended, Tier)。</summary>
    public static (string Category, bool Recommended, string Tier) Classify(DiscoveredApp d)
    {
        if (!d.Supported) return (CategoryConstants.Uwp, false, TierSkip);

        string nameKey = NormalizeName(d.DisplayName.Length > 0 ? d.DisplayName : d.Name);
        string exeKey = Path.GetFileNameWithoutExtension(d.ExePath).ToLowerInvariant();

        string? category = null;
        foreach (var rule in CategoryRules)
        {
            foreach (var kw in rule.Keywords)
            {
                if (nameKey.Contains(kw, StringComparison.Ordinal) ||
                    exeKey.Contains(kw, StringComparison.Ordinal))
                {
                    category = rule.Category;
                    break;
                }
            }
            if (category != null) break;
        }

        if (category == null) category = CategoryConstants.Other;

        bool excluded = IsExcludedByName(d.DisplayName) || IsExcludedByName(d.Name) ||
                        IsBlacklistedPublisher(d.Publisher);

        if (excluded)
            return (category, false, TierSkip);
        if (category != CategoryConstants.Other)
            return (category, true, TierRecommended);
        return (category, false, TierTry);
    }

    /// <summary>
    /// 去重合并 + 分类打分。主键 exe 规范化；UWP 用名称键（无 exe）。
    /// 返回按 推荐度 → 已添加沉底 → 名称 排序的列表。
    /// </summary>
    public static List<DiscoveredApp> Merge(IEnumerable<DiscoveredApp> raw, HashSet<string> addedSet)
    {
        var byKey = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in raw)
        {
            if (d.Excluded) continue;                       // 注册表噪音已在扫描层标记，直接丢弃
            bool uwp = !d.Supported;
            string key = uwp
                ? "uwp::" + (d.Name + "|" + d.DisplayName)
                : NormalizeExePath(d.ExePath);
            if (string.IsNullOrEmpty(key)) continue;

            if (byKey.TryGetValue(key, out var existing))
                MergeInto(existing, d);
            else
                byKey[key] = d;
        }

        var list = byKey.Values.ToList();
        foreach (var d in list)
        {
            var (cat, rec, tier) = Classify(d);
            d.Category = cat;
            d.Recommended = rec;
            d.Tier = tier;
            d.AlreadyAdded = !uwp && addedSet.Contains(NormalizeExePath(d.ExePath));
        }

        list.Sort((a, b) =>
        {
            int r = b.Recommended.CompareTo(a.Recommended);     // 推荐优先
            if (r != 0) return r;
            int aa = a.AlreadyAdded.CompareTo(b.AlreadyAdded);  // 已添加沉底
            if (aa != 0) return aa;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
        });
        return list;
    }

    /// <summary>高优先级源保留，低优先级源补全空缺字段。</summary>
    private static void MergeInto(DiscoveredApp existing, DiscoveredApp incoming)
    {
        if (SourcePriority(incoming.Source) < SourcePriority(existing.Source))
            existing.Source = incoming.Source;
        if (string.IsNullOrEmpty(existing.DisplayName) && !string.IsNullOrEmpty(incoming.DisplayName))
            existing.DisplayName = incoming.DisplayName;
        if (string.IsNullOrEmpty(existing.Name) && !string.IsNullOrEmpty(incoming.Name))
            existing.Name = incoming.Name;
        if (string.IsNullOrEmpty(existing.Publisher) && !string.IsNullOrEmpty(incoming.Publisher))
            existing.Publisher = incoming.Publisher;
        if (string.IsNullOrEmpty(existing.Version) && !string.IsNullOrEmpty(incoming.Version))
            existing.Version = incoming.Version;
        if (string.IsNullOrEmpty(existing.InstallLocation) && !string.IsNullOrEmpty(incoming.InstallLocation))
            existing.InstallLocation = incoming.InstallLocation;
        if (string.IsNullOrEmpty(existing.IconPath) && !string.IsNullOrEmpty(incoming.IconPath))
            existing.IconPath = incoming.IconPath;
        if (string.IsNullOrEmpty(existing.ExePath) && !string.IsNullOrEmpty(incoming.ExePath))
            existing.ExePath = incoming.ExePath;
    }

    public static bool IsExcludedByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var n = name.ToLowerInvariant();
        return ExcludeNameKeywords.Any(k => n.Contains(k));
    }

    public static bool IsBlacklistedPublisher(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return false;
        var p = publisher.ToLowerInvariant();
        return BlacklistedPublishers.Any(b => p.Contains(b));
    }
}
