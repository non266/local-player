using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

// 绘制：画进 32 位位图再整张推给系统（WinForms 自己的绘制不参与）。

namespace 播放器.Ui
{
    internal sealed partial class DesktopLyricsWindow
    {
        /// <summary>内容全部来自位图，WinForms 的绘制一律不参与。</summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        // ---- 绘制（画进位图，再整张推给系统）----------------------------------

        /// <summary>重画当前内容。</summary>
        private void Render()
        {
            if (!IsHandleCreated) return;

            var width = Math.Max(1, ClientSize.Width);
            var height = Math.Max(1, ClientSize.Height);

            var surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            surface.SetResolution(DeviceDpi, DeviceDpi);

            using (var g = Graphics.FromImage(surface))
            {
                g.Clear(Color.Transparent);

                // ⚠ 整块留一层 alpha = 1 的底。
                // 分层窗口的鼠标命中测试是按像素 alpha 来的：alpha 为 0 的地方点击会直接
                // 穿透到下面的程序，于是"只有笔画的像素能抓"——用户把鼠标挪到歌词上想拖它，
                // 十有八九按在字缝里，表现就是"移动不了"。
                // alpha = 1/255 在屏幕上看不出来（4‰ 的变暗），但足够让整块都能被抓住。
                // 锁定时不铺：锁定就是"这个窗口在鼠标眼里不存在"。
                if (!_locked)
                {
                    using var catcher = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
                    g.FillRectangle(catcher, 0, 0, width, height);
                }

                // 不能用 ClearType：逐像素透明时它会在字的边缘留下彩色描边。
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 鼠标停在歌词上（或在拖它）时铺一块背景：不然只有一串悬空的字，
                // 用户看不出"这块地方可以抓"。
                if (_hovering || _dragging) DrawHoverBackground(g, width, height);

                var left = (float)PaddingX;
                var boxWidth = Math.Max(1f, width - PaddingX * 2f);
                var top = (float)PaddingY;

                DrawOutlined(g, _currentText, MainFont, new RectangleF(left, top, boxWidth, _currentHeight + Slice));

                if (_hasNext)
                {
                    var nextTop = top + _currentHeight + LineGap;
                    DrawOutlined(g, _nextText, NextFont, new RectangleF(left, nextTop, boxWidth, _nextHeight + Slice));
                }

                // 控制条在文字下方（锁定或关掉时它自己不画，窗口也已经变矮了）
                DrawControls(g);
            }

            _surface?.Dispose();
            _surface = surface;

            Push();
        }

        /// <summary>鼠标停上来时铺的那块背景。</summary>
        private void DrawHoverBackground(Graphics g, int width, int height)
        {
            var bounds = new RectangleF(0.5f, 0.5f, width - 1f, height - 1f);

            using var path = GdiHelpers.RoundedRect(bounds, Scaled(8));
            using var brush = new SolidBrush(HoverBackColor);
            g.FillPath(brush, path);
        }

        /// <summary>先按一圈深色描边，再把正文盖上去。</summary>
        private void DrawOutlined(Graphics g, string text, Font font, RectangleF box)
        {
            if (string.IsNullOrEmpty(text)) return;

            var radius = OutlineRadius;

            using (var outline = new SolidBrush(OutlineColor))
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;

                        g.DrawString(
                            text, font, outline,
                            new RectangleF(box.X + dx, box.Y + dy, box.Width, box.Height),
                            _format);
                    }
                }
            }

            using var brush = new SolidBrush(MainColor);
            g.DrawString(text, font, brush, box, _format);
        }

        /// <summary>把当前位图整张交给系统（这就是这个窗口的"画面"）。</summary>
        private void Push()
        {
            var surface = _surface;
            if (surface == null || !IsHandleCreated) return;

            var screenDc = GetDC(IntPtr.Zero);
            var memoryDc = CreateCompatibleDC(screenDc);
            var bitmap = IntPtr.Zero;
            var previous = IntPtr.Zero;

            try
            {
                // GetHbitmap(Color.FromArgb(0)) 得到的 HBITMAP 是<b>预乘</b>过的，
                // 正好符合 UpdateLayeredWindow 的要求；直接给位图的原始位会得到发白的光晕。
                bitmap = surface.GetHbitmap(Color.FromArgb(0));
                previous = SelectObject(memoryDc, bitmap);

                var size = new Size(surface.Width, surface.Height);
                var source = new Point(0, 0);
                var destination = new Point(Left, Top);

                var blend = new BlendFunction
                {
                    BlendOp = AcSrcOver,
                    BlendFlags = 0,
                    SourceConstantAlpha = (byte)Math.Round(_opacityPercent * 255 / 100.0),
                    AlphaFormat = AcSrcAlpha
                };

                UpdateLayeredWindow(
                    Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memoryDc, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);

                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
