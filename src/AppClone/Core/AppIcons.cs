using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AppClone.Core;

/// <summary>从 exe 提取应用图标，缓存为 PNG 供 UI 显示。</summary>
public static class AppIcons
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHDefExtractIcon(string iconFile, int iconIndex, uint flags,
        out IntPtr hIconLarge, IntPtr hIconSmall, uint iconSize);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static string IconPath(string appId) => Path.Combine(Paths.IconsDir, appId + ".png");

    /// <summary>提取 48px 图标到 data\icons\<appId>.png；失败返回 false。</summary>
    public static bool Extract(string appId, string exePath)
    {
        try
        {
            Paths.EnsureAll();
            using var bmp = ExtractBitmap(exePath, 48) ?? ExtractBitmap(exePath, 32);
            if (bmp == null) return false;
            bmp.Save(IconPath(appId), ImageFormat.Png);
            return true;
        }
        catch { return false; }
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

    public static Image? Load(string appId)
    {
        var p = IconPath(appId);
        if (!File.Exists(p)) return null;
        try
        {
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read);
            return Image.FromStream(fs);   // 副本，避免文件被锁
        }
        catch { return null; }
    }
}
