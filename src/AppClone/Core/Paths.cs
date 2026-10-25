using System.IO;

namespace AppClone.Core;

/// <summary>
/// 集中管理所有数据目录的定位。
/// 默认可移植布局：exe 同目录下 data\；可用环境变量 FENSHEN_DATA_DIR 覆盖（用于测试）。
/// 增量 1.1.0 新增：cache / snapshots 目录与若干文件访问器（不改变既有行为）。
/// </summary>
public static class Paths
{
    public static readonly string DataRoot =
        Environment.GetEnvironmentVariable("FENSHEN_DATA_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(AppContext.BaseDirectory, "data");

    public static string StoreFile => Path.Combine(DataRoot, "apps.json");
    public static string IconsDir => Path.Combine(DataRoot, "icons");
    public static string ProfilesDir => Path.Combine(DataRoot, "profiles");
    public static string SandboxDir => Path.Combine(AppContext.BaseDirectory, "sandbox");

    // —— 增量 1.1.0 新增目录 / 文件访问器（不影响既有项）——
    public static string CacheDir => Path.Combine(DataRoot, "cache");
    public static string SnapshotsDir => Path.Combine(DataRoot, "snapshots");
    public static string LaunchRulesFile => Path.Combine(DataRoot, "launch_rules.json");
    public static string CategoriesFile => Path.Combine(DataRoot, "categories.json");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string StatsFile => Path.Combine(DataRoot, "stats.json");
    public static string DiscoveryCacheFile => Path.Combine(CacheDir, "discovered.json");

    // —— 增量 1.3.0 新增：兼容性数据库（F1）——
    public static string CompatFile => Path.Combine(DataRoot, "compat.json");

    // —— 增量 1.4.0 新增：分级日志与崩溃报告（F5）——
    /// <summary>结构化日志目录：data\logs\（ops-*.jsonl 操作流水 + crash-*.json 崩溃报告）。</summary>
    public static string LogsDir => Path.Combine(DataRoot, "logs");

    /// <summary>兼容旧版本的纯文本日志（保留写入，逐步被 logs\ 取代）。</summary>
    public static string LegacyLaunchLog => Path.Combine(DataRoot, "launch.log");
    public static string LegacyCrashLog => Path.Combine(DataRoot, "crash.log");

    public static string AppProfileDir(string appId) => Path.Combine(ProfilesDir, appId);
    public static string CloneDir(string appId, string cloneId) => Path.Combine(AppProfileDir(appId), cloneId);
    public static string CloneHome(string appId, string cloneId) => Path.Combine(CloneDir(appId, cloneId), "home");

    public static string AppDataRoaming(string appId, string cloneId) => Path.Combine(CloneHome(appId, cloneId), "AppData", "Roaming");
    public static string AppDataLocal(string appId, string cloneId) => Path.Combine(CloneHome(appId, cloneId), "AppData", "Local");
    /// <summary>Electron / 现代应用常用 LocalLow（SHGetKnownFolderPath FOLDERID_LocalAppDataLow）。</summary>
    public static string AppDataLocalLow(string appId, string cloneId) => Path.Combine(CloneHome(appId, cloneId), "AppData", "LocalLow");
    public static string CloneTemp(string appId, string cloneId) => Path.Combine(CloneHome(appId, cloneId), "Temp");
    public static string CloneUData(string appId, string cloneId) => Path.Combine(CloneDir(appId, cloneId), "udata");
    public static string CloneFFProfile(string appId, string cloneId) => Path.Combine(CloneDir(appId, cloneId), "profile");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(IconsDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(SandboxDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(SnapshotsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
