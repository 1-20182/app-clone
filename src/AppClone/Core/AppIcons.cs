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
    public static Image? Load(string appId)
    {
        if (_memCache.TryGetValue(appId, out var hit)) return hit;
        var p = IconPath(appId);
        if (!File.Exists(p)) return null;
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;
            var img = Image.FromStream(ms);
            Put(appId, img);
            return img;
        }
        catch { return null; }
    }

    public static Image? GetCached(string key) => _memCache.TryGetValue(key, out var i) ? i : null;
    public static void PutCache(string key, Image img) => Put(key, img);

    /// <summary>异步加载：命中内存缓存立即回调；未命中后台提取 32px 仅用于发现页，完成后回调更新单卡。</summary>
    public static void LoadAsync(string key, string exePath, int size, Action<Image?> onReady)
    {
        if (_memCache.TryGetValue(key, out var hit)) { onReady(hit); return; }
        Task.Run(() =>
        {
            try
            {
                var bmp = ExtractBitmap(exePath, size) ?? ExtractBitmap(exePath, Math.Max(16, size / 2));
                if (bmp == null) { onReady(null); return; }
                Put(key, bmp);
                onReady(bmp);
            }
            catch { onReady(null); }
        });
    }

    private static void Put(string key, Image img)
    {
        lock (_lruLock)
        {
            if (_memCache.TryGetValue(key, out var old) && !ReferenceEquals(old, img))
            {
                try { old.Dispose(); } catch { }
            }
            _memCache[key] = img;
            _lru.Remove(key);
            _lru.AddLast(key);
            while (_lru.Count > MaxMemCache)
            {
                var first = _lru.First;
                if (first == null) break;
                _lru.RemoveFirst();
                if (_memCache.TryRemove(first.Value, out var evicted))
                {
                    try { evicted.Dispose(); } catch { }
                }
            }
        }
    }
}
