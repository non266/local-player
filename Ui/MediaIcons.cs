using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace 播放器.Ui
{
    /// <summary>
    /// 一整套工具栏图标。用 GDI+ 现画，不依赖外部图片资源，任意 DPI 下都清晰。
    /// <para>
    /// 之所以做成"一套"而不是静态方法：深色主题下必须换成浅色线条，
    /// 否则深灰图标贴在深色工具栏上等于看不见。换主题时重新生成一套即可。
    /// </para>
    /// </summary>
    internal sealed class MediaIconSet : IDisposable
    {
        private readonly List<Bitmap> _bitmaps = new List<Bitmap>();
        private readonly Color _ink;
        private readonly Color _accent;
        private readonly int _size;

        /// <param name="ink">图标主色。深色主题下必须用浅色，否则看不见。</param>
        /// <param name="accent">强调色，用于播放三角等。</param>
        /// <param name="size">图标边长。图形坐标按 16x16 设计，这里统一缩放。</param>
        public MediaIconSet(Color ink, Color accent, int size = 16)
        {
            _ink = ink;
            _accent = accent;
            _size = Math.Max(8, size);

            OpenFile = Make(DrawOpenFile);
            OpenFolder = Make(DrawOpenFolder);
            Play = Make(DrawPlay);
            Pause = Make(DrawPause);
            Stop = Make(DrawStop);
            Previous = Make(DrawPrevious);
            Next = Make(DrawNext);
            Rewind = Make(DrawRewind);
            Forward = Make(DrawForward);
            Snapshot = Make(DrawSnapshot);
            Playlist = Make(DrawPlaylist);
        }

        public Bitmap OpenFile { get; }

        public Bitmap OpenFolder { get; }

        public Bitmap Play { get; }

        public Bitmap Pause { get; }

        public Bitmap Stop { get; }

        public Bitmap Previous { get; }

        public Bitmap Next { get; }

        public Bitmap Rewind { get; }

        public Bitmap Forward { get; }

        public Bitmap Snapshot { get; }

        public Bitmap Playlist { get; }

        // -----------------------------------------------------------------
        // 图形
        // -----------------------------------------------------------------

        private void DrawOpenFile(Graphics g)
        {
            using (var pen = new Pen(_ink, 1.3f))
            {
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[]
                {
                    new PointF(4f, 2f), new PointF(9.5f, 2f), new PointF(12.5f, 5f),
                    new PointF(12.5f, 14f), new PointF(4f, 14f), new PointF(4f, 2f)
                });
                g.DrawLines(pen, new[] { new PointF(9.5f, 2f), new PointF(9.5f, 5f), new PointF(12.5f, 5f) });
            }
        }

        private void DrawOpenFolder(Graphics g)
        {
            var body = new[]
            {
                new PointF(1.5f, 3.5f), new PointF(6f, 3.5f), new PointF(7.5f, 5.5f),
                new PointF(14.5f, 5.5f), new PointF(14.5f, 13f), new PointF(1.5f, 13f)
            };

            using (var fill = new SolidBrush(Color.FromArgb(247, 201, 72)))
            using (var pen = new Pen(Color.FromArgb(176, 138, 32), 1.1f))
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(body);
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
        }

        private void DrawPlay(Graphics g)
        {
            using (var brush = new SolidBrush(_accent))
            {
                g.FillPolygon(brush, new[]
                {
                    new PointF(4.5f, 2.5f), new PointF(13f, 8f), new PointF(4.5f, 13.5f)
                });
            }
        }

        private void DrawPause(Graphics g)
        {
            using (var brush = new SolidBrush(_ink))
            using (var left = GdiHelpers.RoundedRect(new RectangleF(4f, 2.5f, 3f, 11f), 1f))
            using (var right = GdiHelpers.RoundedRect(new RectangleF(9f, 2.5f, 3f, 11f), 1f))
            {
                g.FillPath(brush, left);
                g.FillPath(brush, right);
            }
        }

        private void DrawStop(Graphics g)
        {
            using (var brush = new SolidBrush(_ink))
            using (var path = GdiHelpers.RoundedRect(new RectangleF(3.5f, 3.5f, 9f, 9f), 1.5f))
            {
                g.FillPath(brush, path);
            }
        }

        private void DrawPrevious(Graphics g)
        {
            using (var brush = new SolidBrush(_ink))
            {
                g.FillRectangle(brush, 2f, 2.5f, 2f, 11f);
                g.FillPolygon(brush, new[]
                {
                    new PointF(14f, 2.5f), new PointF(14f, 13.5f), new PointF(5.5f, 8f)
                });
            }
        }

        private void DrawNext(Graphics g)
        {
            using (var brush = new SolidBrush(_ink))
            {
                g.FillRectangle(brush, 12f, 2.5f, 2f, 11f);
                g.FillPolygon(brush, new[]
                {
                    new PointF(2f, 2.5f), new PointF(2f, 13.5f), new PointF(10.5f, 8f)
                });
            }
        }

        private void DrawRewind(Graphics g)
        {
            using (var brush = new SolidBrush(_ink))
            {
                g.FillPolygon(brush, new[]
                {
                    new PointF(8f, 2.5f), new PointF(8f, 13.5f), new PointF(1.5f, 8f)
                });
                g.FillPolygon(brush, new[]
                {
                    new PointF(14.5f, 2.5f), new PointF(14.5f, 13.5f), new PointF(8f, 8f)
                });
            }
        }

        private void DrawForward(Graphics g)
        {
            using (var brush = new SolidBrush(_ink))
            {
                g.FillPolygon(brush, new[]
                {
                    new PointF(1.5f, 2.5f), new PointF(1.5f, 13.5f), new PointF(8f, 8f)
                });
                g.FillPolygon(brush, new[]
                {
                    new PointF(8f, 2.5f), new PointF(8f, 13.5f), new PointF(14.5f, 8f)
                });
            }
        }

        private void DrawSnapshot(Graphics g)
        {
            using (var pen = new Pen(_ink, 1.3f))
            using (var brush = new SolidBrush(_ink))
            {
                g.DrawRectangle(pen, 1.5f, 5f, 13f, 8.5f);
                g.FillRectangle(brush, 5.5f, 3f, 5f, 2f);
                g.DrawEllipse(pen, 5.5f, 6.8f, 5f, 5f);
            }
        }

        private void DrawPlaylist(Graphics g)
        {
            using (var pen = new Pen(_ink, 1.4f))
            using (var brush = new SolidBrush(_ink))
            {
                for (var i = 0; i < 3; i++)
                {
                    var y = 4f + i * 4f;
                    g.FillRectangle(brush, 2f, y - 0.9f, 1.8f, 1.8f);
                    g.DrawLine(pen, 5.5f, y, 14f, y);
                }
            }
        }

        // -----------------------------------------------------------------
        // 基础设施
        // -----------------------------------------------------------------

        private Bitmap Make(Action<Graphics> draw)
        {
            var bitmap = new Bitmap(_size, _size);
            bitmap.SetResolution(96f, 96f);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(Color.Transparent);

                // 所有图形都是按 16x16 的网格写的，换尺寸时统一缩放。
                if (_size != 16)
                    graphics.ScaleTransform(_size / 16f, _size / 16f);

                draw(graphics);
            }

            _bitmaps.Add(bitmap);
            return bitmap;
        }

        public void Dispose()
        {
            foreach (var bitmap in _bitmaps)
                bitmap.Dispose();

            _bitmaps.Clear();
        }
    }

    /// <summary>程序图标：按强调色生成并缓存（HICON 不受 GC 管理，重复生成会泄漏）。</summary>
    internal static class AppIcons
    {
        private static readonly Dictionary<int, Icon> Cache = new Dictionary<int, Icon>();

        /// <summary>缓存里那些图标的原生句柄，退出时统一销毁。</summary>
        private static readonly List<IntPtr> Handles = new List<IntPtr>();

        public static Icon For(Color accent)
        {
            var key = accent.ToArgb();

            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;

                using (var bitmap = new Bitmap(32, 32))
                {
                    bitmap.SetResolution(96f, 96f);

                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        graphics.Clear(Color.Transparent);

                        using (var back = new SolidBrush(accent))
                        using (var path = GdiHelpers.RoundedRect(new RectangleF(1f, 1f, 30f, 30f), 7f))
                        {
                            graphics.FillPath(back, path);
                        }

                        using (var white = new SolidBrush(Color.White))
                        {
                            graphics.FillPolygon(white, new[]
                            {
                                new PointF(13f, 9f), new PointF(23f, 16f), new PointF(13f, 23f)
                            });
                        }
                    }

                    // GetHicon 生成的是独立副本，Bitmap 释放后依然有效；
                    // 但 Icon.FromHandle 不接管所有权，所以句柄必须自己记下来，
                    // 否则每套主题色都会泄漏一个永不回收的 HICON。
                    var handle = bitmap.GetHicon();
                    Handles.Add(handle);

                    var icon = Icon.FromHandle(handle);
                    Cache[key] = icon;
                    return icon;
                }
            }
        }

        /// <summary>退出时销毁所有缓存图标的原生句柄。可重复调用。</summary>
        public static void ReleaseHandles()
        {
            lock (Cache)
            {
                foreach (var handle in Handles)
                {
                    if (handle != IntPtr.Zero) DestroyIcon(handle);
                }

                Handles.Clear();
                Cache.Clear();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyIcon", SetLastError = true)]
        private static extern bool DestroyIconNative(IntPtr handle);

        private static bool DestroyIcon(IntPtr handle) => DestroyIconNative(handle);
    }
}
