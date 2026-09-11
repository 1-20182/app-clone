using System.Text.Json;

namespace AppClone.Core;

/// <summary>
/// 强制规则 R7（默认温和方案：替换桌面快捷方式）。
/// 不修改 App Paths 注册表（零系统副作用），用户双击原图标即进分身环境。
/// 原始快捷方式移动到 &lt;name&gt;.lnk.fenshenbak，记录映射；RestoreAll 一键还原（卸载时调用）。
/// 可选"App Paths 接管"高级模式未实现（需二次确认 + 卸载还原，本期默认不开启）。
/// </summary>
public static class AutoLaunchRule
{
    private static string BackupFile => Path.Combine(Paths.DataRoot, "autorule_backup.json");
    private static readonly object _lock = new();

    public static bool Install(AppEntry app, CloneEntry clone)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string origLnk = Path.Combine(desktop, SafeFileName(app.Name) + ".lnk");
            var backup = LoadBackup();

            // 首次接管：把原快捷方式移动到备份（若尚未被我们接管）
            if (File.Exists(origLnk) && !backup.ContainsKey(origLnk))
            {
                string bak = origLnk + ".fenshenbak";
                if (!File.Exists(bak))
                {
                    File.Move(origLnk, bak);
                    backup[origLnk] = bak;
                    SaveBackup(backup);
                }
            }

            // 写替换型桌面快捷方式：指向 AppClone --launch
            string self = Application.ExecutablePath;
            new AppClone.Shared.NativeLink
            {
                Target = self,
                Arguments = $"--launch {app.Id} {clone.Id}",
                WorkingDirectory = Path.GetDirectoryName(self),
                IconPath = app.ExePath,
                Description = $"以分身方式启动 {app.Name}",
            }.Save(origLnk);
            return true;
        }
        catch { return false; }
    }

    /// <summary>一键还原所有强制规则（卸载 AppClone 时由 --restore-all-rules 调用）。</summary>
    public static void RestoreAll()
    {
        try
        {
            var backup = LoadBackup();
            foreach (var kv in backup)
            {
                try
                {
                    if (File.Exists(kv.Key)) File.Delete(kv.Key);          // 删替换快捷方式
                    if (File.Exists(kv.Value)) File.Move(kv.Value, kv.Key); // 还原原始
                }
                catch { }
            }
            SaveBackup(new Dictionary<string, string>());
        }
        catch { }
    }

    private static Dictionary<string, string> LoadBackup()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(BackupFile))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BackupFile));
                    if (d != null) return d;
                }
            }
            catch { }
            return new Dictionary<string, string>();
        }
    }

    private static void SaveBackup(Dictionary<string, string> map)
    {
        lock (_lock)
        {
            try
            {
                Paths.EnsureAll();
                File.WriteAllText(BackupFile, JsonSerializer.Serialize(map,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "应用" : name;
    }
}
