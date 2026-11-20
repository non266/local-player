using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 封面字节 → 缩略图位图：只做解码、等比缩放与两条上限，不碰界面、不碰磁盘。
    /// <para>
    /// 故意<b>不裁切、不拉伸</b>：非正方形的封面等比缩进方框、居中、四周透明。
    /// 裁切会把唱片封面上的字切掉，拉伸会把人拉扁——两样都比留一点透明边难看。
    /// </para>
    /// <para>
    /// 上限在进 <see cref="Bitmap"/> 之前就判掉：12 MB 的压缩图可以解出上亿像素，
    /// 300 张挨个解一遍足够把内存吃光。
    /// </para>
    /// </summary>
    internal static class CoverThumbnails
    {
        /// <summary>解码后的像素数上限（4,000×4,000）。</summary>
        internal const long MaxPixels = 4000L * 4000L;

        /// <summary>解码后的估算内存上限（像素 × 4 字节）。</summary>
        internal const long MaxDecodedBytes = 64L * 1024 * 1024;

        /// <summary>缩略图边长允许的范围（逻辑像素在视图侧已按 DPI 缩放）。</summary>
        internal const int MinEdge = 16;
        internal const int MaxEdge = 256;

        /// <summary>边长夹到安全范围：0 / 负数 / 巨大值都不该让 <see cref="Bitmap"/> 抛。</summary>
        internal static int ClampEdge(int edge) => Math.Clamp(edge, MinEdge, MaxEdge);

        /// <summary>这张图允不允许解（像素数与内存两条上限，纯函数，给单测用）。</summary>
        internal static bool AcceptSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;

            var pixels = (long)width * height;

            return pixels <= MaxPixels && pixels * 4 <= MaxDecodedBytes;
        }

        /// <summary>
        /// 把封面字节解成 <paramref name="edge"/>×<paramref name="edge"/> 的缩略图；
        /// 没有封面 / 不是图片 / 超限 / 损坏都返回 <c>null</c>（调用方按"没有封面"处理）。
        /// </summary>
        internal static Bitmap? Decode(byte[]? art, int edge)
        {
            if (art == null || art.Length == 0) return null;

            edge = ClampEdge(edge);

            try
            {
                using var source = Image.FromStream(new MemoryStream(art, writable: false));

                if (!AcceptSize(source.Width, source.Height)) return null;

                var bitmap = new Bitmap(edge, edge, PixelFormat.Format32bppArgb);

                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;

                    var scale = Math.Min(edge / (double)source.Width, edge / (double)source.Height);
                    var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(source.Height * scale));

                    graphics.DrawImage(source, (edge - width) / 2, (edge - height) / 2, width, height);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页缩略图解不开封面", ex);
                return null;
            }
        }
    }
}
