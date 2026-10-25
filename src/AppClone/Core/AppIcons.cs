using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AppClone.Core;

/// <summary>从 exe 提取应用图标，缓存为 PNG 供 UI 显示。增量 1.1.0 增加内存 LRU 缓存 + 异步加载 + 32px 支持。</summary>
public static class AppIcons
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHDefExtractIcon(string iconFile, int iconIndex, uint flags,
        out IntPtr hIconLarge, IntPtr hIconSmall, uint iconSize);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // —— 增量 1.1.0：内存缓存（LRU 上限 200 张，约束 P-7）——
    private const int MaxMemCache = 200;
    private static readonly ConcurrentDictionary<string, Image> _memCache = new();
    private static readonly LinkedList<string> _lru = new();
    private static readonly object _lruLock = new();

    /// <summary>发现页图标缓存键（按规范化 exe 路径，跨次打开稳定）。</summary>
    public static string DiscoverKey(string exePath) => AppClassifier.NormalizeExePath(exePath);

    public static string IconPath(string appId) => Path.Combine(Paths.IconsDir, appId + ".png");

    /// <summary>提取指定尺寸图标到 data\icons\&lt;appId&gt;.png；失败返回 false。默认 48px。</summary>
    public static bool Extract(string appId, string exePath, int size = 48)
    {
        try
        {
            Paths.EnsureAll();
            // bmp 是本方法临时对象：Put 内部已克隆为缓存独占副本，故此处 using 释放安全
            using var bmp = ExtractBitmap(exePath, size) ?? ExtractBitmap(exePath, Math.Max(16, size / 2));
            if (bmp == null) return false;
            bmp.Save(IconPath(appId), ImageFormat.Png);
            Put(appId, bmp);
            return true;
        }
        catch { return false; }
    }

    /// <summary>提取失败重试一次（网络/文件瞬时占用兜底）。</summary>
    public static bool ExtractWithRetry(string appId, string exePath, int size = 48)
    {
        if (Extract(appId, exePath, size)) return true;
        try { Thread.Sleep(60); } catch { }
        return Extract(appId, exePath, size);
    }

    /// <summary>
    /// 渲染指定尺寸的应用图标位图（不落盘、不入缓存）。调用方负责 Dispose。
    /// F6 角标图标（BadgeIcon）与需要自定义底图的场景使用。
    /// </summary>
    public static Bitmap? RenderIcon(string exePath, int size)
    {
        try { return ExtractBitmap(exePath, size); } catch { return null; }
    }

    private static Bitmap? ExtractBitmap(string exePath, int size)
    {
        if (SHDefExtractIcon(exePath, 0, 0, out var hIcon, IntPtr.Zero, (uint)size) == 0 && hIcon != IntPtr.Zero)
        {
            using var icon = Icon.FromHandle(hIcon);
            var bmp = icon.ToBitmap();
            DestroyIcon(hIcon);
            return bmp;
        }
        try
        {
            using var icon2 = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            return icon2?.ToBitmap();
        }
        catch { return null; }
    }

    /// <summary>同步加载（先查内存缓存，再读磁盘 PNG 副本，避免文件被锁）。</summary>
    /// <remarks>返回克隆，调用方可安全 Dispose；缓存对象由缓存独占管理。</remarks>
    public static Image? Load(string appId)
    {
        var hit = GetCached(appId);
        if (hit != null) return hit;
        var p = IconPath(appId);
        if (!File.Exists(p)) return null;
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            // 先解码再用 new Bitmap 脱钩流：流随 using 关闭后 GDI+ 延迟解码不会踩已释放资源
            using var decoded = Image.FromStream(ms);
            Put(appId, decoded);   // Put 内部克隆，decoded 仍由本方法 using 释放
            return GetCached(appId);
        }
        catch { return null; }
    }

    /// <summary>命中缓存返回克隆（调用方可安全 Dispose）；未命中返回 null。</summary>
    public static Image? GetCached(string key) => _memCache.TryGetValue(key, out var i) ? CloneOf(i) : null;

    private static Bitmap? CloneOf(Image img)
    {
        try { return new Bitmap(img); }
        catch { return null; }
    }
    public static void PutCache(string key, Image img) => Put(key, img);

    /// <summary>异步加载：命中内存缓存立即回调；未命中后台提取 32px 仅用于发现页，完成后回调更新单卡。</summary>
    /// <remarks>回调收到的是克隆，调用方可安全 Dispose。</remarks>
    public static void LoadAsync(string key, string exePath, int size, Action<Image?> onReady)
    {
        var hit = GetCached(key);
        if (hit != null) { onReady(hit); return; }
        Task.Run(() =>
        {
            try
            {
                var bmp = ExtractBitmap(exePath, size) ?? ExtractBitmap(exePath, Math.Max(16, size / 2));
                if (bmp == null) { onReady(null); return; }
                Put(key, bmp);   // Put 内部克隆，临时提取对象由本方法释放
                bmp.Dispose();
                onReady(GetCached(key));
            }
            catch { onReady(null); }
        });
    }

    /// <summary>入库：克隆为缓存独占 Bitmap 后入缓存；替换/淘汰时释放的均为缓存自有对象。</summary>
    private static void Put(string key, Image img)
    {
        lock (_lruLock)
        {
            Bitmap copy;
            try { copy = new Bitmap(img); }
            catch { return; }   // 克隆失败则不入缓存，也不影响调用方
            if (_memCache.TryGetValue(key, out var old))
            {
                // old 是缓存独占对象（UI 只持有克隆），可安全释放
                try { old.Dispose(); } catch { }
            }
            _memCache[key] = copy;
            _lru.Remove(key);
            _lru.AddLast(key);
            while (_lru.Count > MaxMemCache)
            {
                var first = _lru.First;
                if (first == null) break;
                _lru.RemoveFirst();
                if (_memCache.TryRemove(first.Value, out var evicted))
                {
                    // 淘汰的是缓存独占对象，外部克隆与它互不关联
                    try { evicted.Dispose(); } catch { }
                }
            }
        }
    }
}
