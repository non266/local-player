using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace IconGen
{
    /// <summary>
    /// 生成程序图标 <c>app.ico</c>。
    /// <para>
    /// 画的是和运行时图标（<c>Ui\MediaIcons.cs</c> 的 <c>AppIcons.For</c>）一致的图案：
    /// 圆角方块 + 白色播放三角，强调色用浅色主题的 <c>#0A84FF</c>。
    /// exe 文件本身需要一个真正的 .ico 资源，运行时那套 GDI+ 现画的图标只能用在窗口和托盘上。
    /// </para>
    /// </summary>
    internal static class Program
    {
        /// <summary>浅色主题的强调色，也是默认主题（跟随系统）最常用的那一个。</summary>
        private static readonly Color Accent = Color.FromArgb(0x0A, 0x84, 0xFF);

        /// <summary>
        /// 要打包的尺寸。
        /// <para>
        /// 16~128 用传统 DIB 条目。Vista 以后 Windows 资源管理器确实支持 ICO 内嵌 PNG，
        /// 但 .NET 自己的 <see cref="Icon"/> 读不了 PNG 条目：对它会抛
        /// "Requested range extends past the end of the array"，只能回退到能解析的最大 DIB。
        /// </para>
        /// <para>
        /// 只有 256 那张用 PNG，理由是它占绝对多数体积（DIB 要 270 KB）。
        /// 256 这一档只有资源管理器的"超大图标"视图会用到，走的是 shell 自己的解码，
        /// 而 .NET 的 <c>Icon</c> 本来也拿不到 256（实测请求 256 返回的是 128），
        /// 所以这里用 PNG 不会损失任何 .NET 侧的可用性，文件却从 364 KB 降到约 118 KB。
        /// </para>
        /// </summary>
        private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

        /// <summary>达到这个尺寸就用 PNG 编码。</summary>
        private const int PngThreshold = 256;

        private static int Main(string[] args)
        {
            var outputPath = Path.GetFullPath(
                args.Length > 0
                    ? args[0]
                    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "app.ico"));

            try
            {
                var entries = new List<(int Size, byte[] Data)>();

                foreach (var size in Sizes)
                {
                    using (var bitmap = Render(size))
                    {
                        entries.Add((size, size >= PngThreshold
                            ? EncodePng(bitmap)
                            : EncodeDib(bitmap)));
                    }
                }

                File.WriteAllBytes(outputPath, BuildIco(entries));

                var info = new FileInfo(outputPath);
                Console.WriteLine($"已生成 {outputPath}（{Sizes.Length} 种尺寸，{info.Length / 1024.0:0.#} KB）");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("生成失败：" + ex);
                return 1;
            }
        }

        /// <summary>按尺寸绘制图标。所有坐标都按 32×32 的比例缩放。</summary>
        private static Bitmap Render(int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            bitmap.SetResolution(96f, 96f);

            var scale = size / 32f;

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);

                using (var back = new SolidBrush(Accent))
                using (var path = RoundedRect(new RectangleF(1f * scale, 1f * scale, 30f * scale, 30f * scale), 7f * scale))
                {
                    graphics.FillPath(back, path);
                }

                // 小尺寸下细节会糊成一团，三角形的内缩比例跟着尺寸走。
                var inset = size <= 20 ? 1.5f : 0f;

                using (var white = new SolidBrush(Color.White))
                {
                    graphics.FillPolygon(white, new[]
                    {
                        new PointF((13f + inset) * scale, 9f * scale),
                        new PointF((23f - inset * 0.5f) * scale, 16f * scale),
                        new PointF((13f + inset) * scale, 23f * scale)
                    });
                }
            }

            return bitmap;
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(0.1f, radius * 2);

            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();

            return path;
        }

        private static byte[] EncodePng(Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        /// <summary>
        /// 编码成 ICO 里的 DIB 条目：BITMAPINFOHEADER + 32 位 BGRA 像素（自下而上）+ AND 掩码。
        /// 透明度靠 alpha 通道，AND 掩码全 0 即可（全不透明由 alpha 决定）。
        /// </summary>
        private static byte[] EncodeDib(Bitmap bitmap)
        {
            var size = bitmap.Width;
            var stride = size * 4;
            var maskStride = ((size + 31) / 32) * 4;

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                // BITMAPINFOHEADER
                writer.Write(40);                 // biSize
                writer.Write(size);               // biWidth
                writer.Write(size * 2);           // biHeight：XOR + AND 两张图叠起来
                writer.Write((short)1);           // biPlanes
                writer.Write((short)32);          // biBitCount
                writer.Write(0);                  // biCompression = BI_RGB
                writer.Write(stride * size + maskStride * size);  // biSizeImage
                writer.Write(0);                  // biXPelsPerMeter
                writer.Write(0);                  // biYPelsPerMeter
                writer.Write(0);                  // biClrUsed
                writer.Write(0);                  // biClrImportant

                // XOR 位图：自下而上
                var pixels = new byte[stride * size];
                var data = bitmap.LockBits(
                    new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                try
                {
                    for (var y = 0; y < size; y++)
                    {
                        var sourceRow = IntPtr.Add(data.Scan0, y * data.Stride);
                        System.Runtime.InteropServices.Marshal.Copy(
                            sourceRow, pixels, (size - 1 - y) * stride, stride);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                writer.Write(pixels);

                // AND 掩码：全 0
                writer.Write(new byte[maskStride * size]);

                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>拼出 ICONDIR + ICONDIRENTRY[] + 各条目数据。</summary>
        private static byte[] BuildIco(List<(int Size, byte[] Data)> entries)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((short)0);                    // reserved
                writer.Write((short)1);                    // type = icon
                writer.Write((short)entries.Count);

                var offset = 6 + entries.Count * 16;

                foreach (var (size, data) in entries)
                {
                    writer.Write((byte)(size >= 256 ? 0 : size));   // 256 用 0 表示
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0);                 // 调色板颜色数
                    writer.Write((byte)0);                 // reserved
                    writer.Write((short)1);                // 色彩平面
                    writer.Write((short)32);               // 位深
                    writer.Write(data.Length);
                    writer.Write(offset);

                    offset += data.Length;
                }

                foreach (var (_, data) in entries)
                    writer.Write(data);

                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
