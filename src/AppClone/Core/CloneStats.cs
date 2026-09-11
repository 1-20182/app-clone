using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AppClone.Core;

/// <summary>单分身运行统计：启动次数、累计秒数、最后启动时间。</summary>
public class StatsEntry
{
    public int LaunchCount;
    public long CumulativeSeconds;
    public DateTime LastLaunchedAt;
}

/// <summary>
/// 运行统计（内存计数 + 落盘 data/stats.json）。
/// 与 apps.json 分离；线程安全（lock 保护字典）；累计若干次或退出前调用 Save / FlushIfNeeded 落盘。
/// </summary>
public static class CloneStats
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, StatsEntry> _stats = new();
    private static bool _loaded;
    private static bool _dirty;
    private static int _pending;

    private const int FlushThreshold = 20;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Key(string appId, string cloneId) => $"{appId}|{cloneId}";

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(Paths.StatsFile))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, StatsEntry>>(
                    File.ReadAllText(Paths.StatsFile), JsonOpts);
                if (dict != null)
                    foreach (var kv in dict) _stats[kv.Key] = kv.Value;
            }
        }
        catch { }
    }

    public static void RecordLaunch(string appId, string cloneId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var k = Key(appId, cloneId);
            if (!_stats.TryGetValue(k, out var e)) { e = new StatsEntry(); _stats[k] = e; }
            e.LaunchCount++;
            e.LastLaunchedAt = DateTime.Now;
            _dirty = true;
            _pending++;
        }
    }

    public static void RecordStop(string appId, string cloneId, TimeSpan dur)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var k = Key(appId, cloneId);
            if (!_stats.TryGetValue(k, out var e)) { e = new StatsEntry(); _stats[k] = e; }
            e.CumulativeSeconds += (long)dur.TotalSeconds;
            _dirty = true;
            _pending++;
        }
    }

    public static StatsEntry Get(string appId, string cloneId)
    {
        lock (_lock)
        {
            EnsureLoaded();
            var k = Key(appId, cloneId);
            return _stats.TryGetValue(k, out var e) ? e : new StatsEntry();
        }
    }

    public static void Save()
    {
        lock (_lock)
        {
            EnsureLoaded();
            Paths.EnsureAll();
            try { File.WriteAllText(Paths.StatsFile, JsonSerializer.Serialize(_stats, JsonOpts)); } catch { }
            _dirty = false;
            _pending = 0;
        }
    }

    public static void FlushIfNeeded()
    {
        lock (_lock)
        {
            if (_dirty && _pending >= FlushThreshold) Save();
        }
    }
}
