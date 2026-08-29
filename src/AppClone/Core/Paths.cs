namespace AppClone.Core;

/// <summary>
/// 集中管理所有数据目录的定位。
/// 默认可移植布局：exe 同目录下 data\；可用环境变量 FENSHEN_DATA_DIR 覆盖（用于测试）。
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

    public static string AppProfileDir(string appId) => Path.Combine(ProfilesDir, appId);
    public static string CloneDir(string appId, string cloneId) => Path.Combine(AppProfileDir(appId), cloneId);
    public static string CloneHome(string appId, string cloneId) => Path.Combine(CloneDir(appId, cloneId), "home");

    public static string AppDataRoaming(string appId, string cloneId) => Path.Combine(CloneHome(appId, cloneId), "AppData", "Roaming");
    public static string AppDataLocal(string appId, string cloneId) => Path.Combine(CloneHome(appId, cloneId), "AppData", "Local");
    public static string CloneTemp(string appId, string cloneId) => Path.Combine(CloneHome(appId, cloneId), "Temp");
    public static string CloneUData(string appId, string cloneId) => Path.Combine(CloneDir(appId, cloneId), "udata");
    public static string CloneFFProfile(string appId, string cloneId) => Path.Combine(CloneDir(appId, cloneId), "profile");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(IconsDir);
        Directory.CreateDirectory(ProfilesDir);
        Directory.CreateDirectory(SandboxDir);
    }
}
