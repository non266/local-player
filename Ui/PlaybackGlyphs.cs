using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace 播放器.Ui
{
    /// <summary>播放控件里的五种字形。</summary>
    internal enum PlaybackGlyph
    {
        Play,
        Pause,
        Previous,
        Next,
        Stop
    }

    /// <summary>
    /// 自己画播放控件的字形（三角 / 双竖 / 双三角 / 方块），不用字体图标、也不用位图素材：
    /// DPI 变了、按钮尺寸换一个，形状都还是清楚的。
    /// <para>
    /// 桌面歌词的控制条与音频窗口卡片里的按钮共用这一份画法——两边长得一样，
    /// 以后改形状只改这里（见 docs/1.10.0计划.md 的 F12）。
    /// </para>
    /// </summary>
    internal static class PlaybackGlyphs
    {
        /// <summary>把一个字形画进 <paramref name="box"/>（由调用方给笔刷颜色，含透明度）。</summary>
        public static void Draw(Graphics g, PlaybackGlyph glyph, Rectangle box, Color color)
        {
            if (g == null || box.Width <= 0 || box.Height <= 0) return;

            using var brush = new SolidBrush(color);

            switch (glyph)
            {
                case PlaybackGlyph.Play:
                    FillTriangle(g, brush, box, pointingRight: true);
                    break;

                case PlaybackGlyph.Pause:
                    FillPauseBars(g, brush, box);
                    break;

                case PlaybackGlyph.Previous:
                    FillDoubleTriangle(g, brush, box, pointingRight: false);
                    break;

                case PlaybackGlyph.Next:
                    FillDoubleTriangle(g, brush, box, pointingRight: true);
                    break;

                case PlaybackGlyph.Stop:
                    {
                        var (left, top, right, bottom) = Inset(box, 0.30f);
                        g.FillRectangle(brush, left, top, right - left, bottom - top);
                        break;
                    }
            }
        }

        /// <summary>渲染成一张透明底位图（给 WinForms 按钮的 <c>Image</c> 用）。调用方负责释放。</summary>
        public static Bitmap Render(PlaybackGlyph glyph, Size size, Color color)
        {
            var bitmap = new Bitmap(
                Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);

            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Draw(g, glyph, new Rectangle(Point.Empty, bitmap.Size), color);

            return bitmap;
        }

        private static void FillTriangle(Graphics g, Brush brush, Rectangle box, bool pointingRight)
        {
            var (left, top, right, bottom) = Inset(box, 0.28f);
            var middle = (top + bottom) / 2f;

            g.FillPolygon(brush, pointingRight
                ? new[] { new PointF(left, top), new PointF(right, middle), new PointF(left, bottom) }
                : new[] { new PointF(right, top), new PointF(left, middle), new PointF(right, bottom) });
        }

        private static void FillPauseBars(Graphics g, Brush brush, Rectangle box)
        {
            var (left, top, right, bottom) = Inset(box, 0.30f);

            var width = right - left;
            var bar = width * 0.34f;
            var gap = width * 0.32f;

            g.FillRectangle(brush, left, top, bar, bottom - top);
            g.FillRectangle(brush, left + bar + gap, top, bar, bottom - top);
        }

        /// <summary>上一首 / 下一首的"双三角"（和播放键同一个三角，画两个）。</summary>
        private static void FillDoubleTriangle(Graphics g, Brush brush, Rectangle box, bool pointingRight)
        {
            var (left, top, right, bottom) = Inset(box, 0.26f);
            var middle = (top + bottom) / 2f;

            var half = (right - left) / 2f;
            var gap = half * 0.16f;

            for (var i = 0; i < 2; i++)
            {
                var start = left + i * (half + gap);
                var tip = start + half - gap;

                g.FillPolygon(brush, pointingRight
                    ? new[] { new PointF(start, top), new PointF(tip, middle), new PointF(start, bottom) }
                    : new[] { new PointF(tip, top), new PointF(start, middle), new PointF(tip, bottom) });
            }
        }

        private static (float Left, float Top, float Right, float Bottom) Inset(Rectangle box, float ratio)
        {
            var dx = box.Width * ratio;
            var dy = box.Height * ratio;

            return (box.Left + dx, box.Top + dy, box.Right - dx, box.Bottom - dy);
        }
    }
}
