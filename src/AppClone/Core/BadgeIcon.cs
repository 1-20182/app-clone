using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace AppClone.Core;

/// <summary>
/// 角标图标（F6）：把分身序号画在应用图标右下角，输出成多尺寸 .ico，供桌面快捷方式使用。
///
/// 为什么手写 ICO 而不引第三方库：本项目坚持零 PackageReference。ICO 容器格式本身极简单
/// （一个 6 字节文件头 + N 个 16 字节目录项 + 各自的图像数据），自己写反而比引依赖更可控。
///
/// 尺寸与编码策略：
///   16 / 32 / 48 → BMP(DIB) 内嵌。这是最保守的格式，任何 Windows 版本、任何提取路径（含旧 API）都认；
///   256          → PNG 内嵌。256 的 BMP 单张就要 256 KB，用 PNG 压到几 KB，且 Vista 以后系统一律支持。
///
/// 小尺寸下（16px）数字根本挤不下，此时只画一个纯色圆点 —— 宁可给「这是另一个分身」的视觉线索，
/// 也不要画一团看不清的糊字。
/// </summary>
public static class BadgeIcon
{
    /// <summary>输出尺寸：覆盖任务栏(16/32)、资源管理器中等图标(48)、大图标/详细信息(256)。</summary>
    private static readonly int[] Sizes = { 16, 32, 48, 256 };

    /// <summary>小于该直径的圆点不画数字（画了也认不出，反而变糊）。</summary>
    private const int MinDiameterForText = 12;

    /// <summary>单个输出文件上限保护：避免异常放大导致写出巨大文件。</summary>
    private const long MaxFileBytes = 4L * 1024 * 1024;

    public static string IconPath(string cloneId) =>
        Path.Combine(Paths.IconsDir, "badge-" + cloneId + ".ico");

    /// <summary>
    /// 确保某分身的角标图标存在并可用，返回其路径（失败返回 null，调用方应回退到原 exe 图标）。
    /// 若已有比源程序更新的缓存则直接复用，避免每次创建快捷方式都重绘一遍。
    /// </summary>
    public static string? Ensure(string cloneId, int index, string? colorHex, string exePath, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(cloneId) || string.IsNullOrWhiteSpace(exePath)) return null;
        var path = IconPath(cloneId);

        try
        {
            if (!force && File.Exists(path))
            {
                var icoAt = File.GetLastWriteTimeUtc(path);
                var exeAt = File.GetLastWriteTimeUtc(exePath);
                if (icoAt >= exeAt) return path;
            }
        }
        catch { }

        var color = CloneColors.Resolve(colorHex);
        return Build(path, exePath, index, color) ? path : null;
    }

    /// <summary>
    /// 绘制并写出多尺寸 ICO（可独立调用的纯函数，便于回归探针断言）。
    /// 返回 false 表示底座图标取不到或写盘失败 —— 两种情况都应静默回退，不该阻断用户操作。
    /// </summary>
    public static bool Build(string icoPath, string sourceExePath, int index, Color badgeColor)
    {
        try
        {
            Paths.EnsureAll();

            // 底座：优先 256 大图（缩放质量最好），取不到再退到系统标准提取
            Bitmap? baseIcon = AppIcons.RenderIcon(sourceExePath, 256)
                               ?? AppIcons.RenderIcon(sourceExePath, 48);
            if (baseIcon == null) return false;

            var frames = new List<(int Size, byte[] Data)>();
            using (baseIcon)
            {
                foreach (int size in Sizes)
                {
                    using var frame = RenderFrame(baseIcon, size, index, badgeColor);
                    if (frame == null) continue;
                    byte[] data = size >= 256 ? EncodePng(frame) : EncodeDib(frame);
                    if (data.Length == 0) continue;
                    frames.Add((size, data));
                }
            }
            if (frames.Count == 0) return false;

            var dir = Path.GetDirectoryName(Path.GetFullPath(icoPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteIco(icoPath, frames);
            return File.Exists(icoPath) && new FileInfo(icoPath).Length <= MaxFileBytes;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ 绘制

    /// <summary>渲染单帧：底座图标缩放到目标尺寸 + 右下角角标。</summary>
    private static Bitmap? RenderFrame(Image source, int size, int index, Color badgeColor)
    {
        if (size <= 0) return null;
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        g.DrawImage(source, new Rectangle(0, 0, size, size));

        // 伪装图标（Disguise）复用同一条渲染管线，但只保留底座、不画角标
        if (index <= 0 && badgeColor.IsEmpty) return bmp;

        // 角标几何：直径取 56%，贴着右下角；16px 下约 9px，够看一眼形状
        int d = Math.Max(6, (int)Math.Round(size * 0.56));
        int margin = size >= 32 ? 1 : 0;
        int x = size - d - margin;
        int y = size - d - margin;
        var circle = new Rectangle(x, y, d, d);

        // 外描边让角标在任何底色上都立得住（深色底用浅描边，浅色底用白描边）
        float ringW = Math.Max(1f, size / 16f);
        using (var ring = new Pen(Color.FromArgb(235, 255, 255, 255), ringW))
            g.DrawEllipse(ring, x, y, d, d);

        using (var fill = new SolidBrush(badgeColor))
            g.FillEllipse(fill, x, y, d, d);
        using (var edge = new Pen(Color.FromArgb(60, 0, 0, 0), Math.Max(1f, size / 32f)))
            g.DrawEllipse(edge, x, y, d, d);

        if (d >= MinDiameterForText)
        {
            string text = NumberText(index);
            float fontSize = text.Length > 1 ? d * 0.52f : d * 0.66f;
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap,
            };
            // 两字符（10+）在小圆点里极易糊，缩小一档并只画一次，不做描边堆叠
            using (var shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.DrawString(text, font, shadow, new RectangleF(circle.X, circle.Y + 1, circle.Width, circle.Height), fmt);
            using var white = new SolidBrush(Color.White);
            g.DrawString(text, font, white, new RectangleF(circle.X, circle.Y, circle.Width, circle.Height), fmt);
        }

        return bmp;
    }

    /// <summary>序号文案：1–9 直接用数字，10 以上收敛为「9+」（小圆点里两位数是灾难）。</summary>
    private static string NumberText(int index)
    {
        if (index <= 0) return "•";
        return index <= 9 ? index.ToString() : "9+";
    }

    // ------------------------------------------------------------------ 编码

    private static byte[] EncodePng(Bitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// 编码为 ICO 内嵌的 BMP(DIB)：BITMAPINFOHEADER（高度写两倍，含掩码区）+ 32 位 BGRA 像素（自底向上）+ AND 掩码。
    /// </summary>
    private static byte[] EncodeDib(Bitmap bmp)
    {
        try
        {
            int w = bmp.Width, h = bmp.Height;
            int maskStride = ((w + 31) / 32) * 4;          // AND 掩码每行按 4 字节对齐
            int pixelBytes = w * h * 4;
            int maskBytes = maskStride * h;

            using var ms = new MemoryStream(40 + pixelBytes + maskBytes);
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // BITMAPINFOHEADER
            bw.Write(40);              // biSize
            bw.Write(w);               // biWidth
            bw.Write(h * 2);           // biHeight = 图像高 + 掩码高
            bw.Write((short)1);        // biPlanes
            bw.Write((short)32);       // biBitCount
            bw.Write(0);               // biCompression = BI_RGB
            bw.Write(pixelBytes);      // biSizeImage
            bw.Write(0); bw.Write(0);  // 分辨率
            bw.Write(0); bw.Write(0);  // 调色板

            // 像素：自底向上、BGRA
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    bw.Write(c.B);
                    bw.Write(c.G);
                    bw.Write(c.R);
                    bw.Write(c.A);
                }

            // AND 掩码：32 位图靠 alpha 通道，掩码全 0 即可（不额外抠洞）
            bw.Write(new byte[maskBytes]);
            bw.Flush();
            return ms.ToArray();
        }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>拼装 ICO 容器：6 字节头 + N×16 字节目录 + 图像数据。</summary>
    private static void WriteIco(string path, List<(int Size, byte[] Data)> frames)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, Encoding.UTF8);

        bw.Write((short)0);                 // idReserved
        bw.Write((short)1);                 // idType = ICO
        bw.Write((short)frames.Count);      // idCount

        int offset = 6 + frames.Count * 16;
        foreach (var (size, data) in frames)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));   // bWidth（256 写 0）
            bw.Write((byte)(size >= 256 ? 0 : size));   // bHeight
            bw.Write((byte)0);                          // bColorCount
            bw.Write((byte)0);                          // bReserved
            bw.Write((short)1);                         // wPlanes
            bw.Write((short)32);                        // wBitCount
            bw.Write(data.Length);                      // dwBytesInRes
            bw.Write(offset);                           // dwImageOffset
            offset += data.Length;
        }
        foreach (var (_, data) in frames) bw.Write(data);
        bw.Flush();
    }
}
