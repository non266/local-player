using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 文件浏览器的小图标：文件夹 / 磁盘 / 视频 / 音频 / 字幕 / 播放列表 / 其他。
    /// <para>
    /// 与 <see cref="MediaIconSet"/> 同一种做法：按主题色现画，不依赖图片资源，
    /// 深色主题下线条自动变浅；DPI 变了就整组重建。
    /// </para>
    /// </summary>
    internal sealed class FileIconSet : IDisposable
    {
        private readonly List<Bitmap> _bitmaps = new List<Bitmap>();
        private readonly Color _ink;
        private readonly Color _accent;
        private readonly Color _muted;

        internal FileIconSet(ThemePalette palette, int size)
        {
            _ink = palette.IconInk;
            _accent = palette.Accent;
            _muted = palette.HintFore;

            var side = Math.Max(12, size);

            Images = new ImageList
            {
                ColorDepth = ColorDepth.Depth32Bit,
                ImageSize = new Size(side, side),
                TransparentColor = Color.Transparent
            };

            // 顺序与 FileBrowserKind 无关，用 IndexOf 显式映射，避免以后加种类时错位。
            Add(DrawFolder);
            Add(DrawDrive);
            Add(DrawVideo);
            Add(DrawAudio);
            Add(DrawSubtitle);
            Add(DrawPlaylist);
            Add(DrawOther);
        }

        internal ImageList Images { get; }

        internal int IndexOf(FileBrowserKind kind) => kind switch
        {
            FileBrowserKind.Folder => 0,
            FileBrowserKind.Drive => 1,
            FileBrowserKind.Video => 2,
            FileBrowserKind.Audio => 3,
            FileBrowserKind.Subtitle => 4,
            FileBrowserKind.Playlist => 5,
            _ => 6
        };

        private void Add(Action<Graphics> draw)
        {
            var side = Images.ImageSize.Width;
            var bitmap = new Bitmap(side, side);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // 所有图形按 16×16 设计，这里统一缩放。
                graphics.ScaleTransform(side / 16f, side / 16f);
                draw(graphics);
            }

            _bitmaps.Add(bitmap);
            Images.Images.Add(bitmap);
        }

        private void DrawFolder(Graphics g)
        {
            var body = new[]
            {
                new PointF(1.5f, 3.5f), new PointF(6f, 3.5f), new PointF(7.5f, 5.5f),
                new PointF(14.5f, 5.5f), new PointF(14.5f, 13f), new PointF(1.5f, 13f)
            };

            using var fill = new SolidBrush(Color.FromArgb(247, 201, 72));
            using var pen = new Pen(Color.FromArgb(176, 138, 32), 1.1f);
            using var path = new GraphicsPath();
            path.AddPolygon(body);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        private void DrawDrive(Graphics g)
        {
            using var fill = new SolidBrush(Color.FromArgb(72, 72, 80));
            using var pen = new Pen(_ink, 1.1f);
            using var body = GdiHelpers.RoundedRect(new RectangleF(1.5f, 4f, 13f, 8f), 1.5f);

            g.FillPath(fill, body);
            g.DrawPath(pen, body);
            g.DrawLine(pen, 1.5f, 9f, 14.5f, 9f);

            using var light = new SolidBrush(_accent);
            g.FillEllipse(light, 11.5f, 10.5f, 1.6f, 1.6f);
        }

        private void DrawVideo(Graphics g)
        {
            using var pen = new Pen(_ink, 1.2f);
            using var body = GdiHelpers.RoundedRect(new RectangleF(1.5f, 3f, 13f, 10f), 1.6f);

            g.DrawPath(pen, body);

            using var fill = new SolidBrush(_accent);
            g.FillPolygon(fill, new[]
            {
                new PointF(6.5f, 5.5f), new PointF(11f, 8f), new PointF(6.5f, 10.5f)
            });
        }

        private void DrawAudio(Graphics g)
        {
            using var pen = new Pen(_ink, 1.3f) { LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(_accent);

            g.DrawLine(pen, 10.5f, 2.5f, 10.5f, 11f);
            g.DrawLine(pen, 10.5f, 2.5f, 5.5f, 4f);
            g.DrawLine(pen, 5.5f, 4f, 5.5f, 12f);

            g.FillEllipse(fill, 2.5f, 10.5f, 3.6f, 2.8f);
            g.FillEllipse(fill, 7.5f, 9.5f, 3.6f, 2.8f);
        }

        private void DrawSubtitle(Graphics g)
        {
            using var pen = new Pen(_ink, 1.1f);
            using var page = GdiHelpers.RoundedRect(new RectangleF(2f, 2f, 12f, 12f), 1.4f);

            g.DrawPath(pen, page);
            g.DrawLine(pen, 4.5f, 6f, 11.5f, 6f);
            g.DrawLine(pen, 4.5f, 8.5f, 11.5f, 8.5f);

            using var fill = new SolidBrush(_accent);
            g.FillRectangle(fill, 4.5f, 10.5f, 5f, 1.4f);
        }

        private void DrawPlaylist(Graphics g)
        {
            using var pen = new Pen(_ink, 1.2f);
            g.DrawLine(pen, 1.5f, 4f, 10.5f, 4f);
            g.DrawLine(pen, 1.5f, 7.5f, 10.5f, 7.5f);
            g.DrawLine(pen, 1.5f, 11f, 7.5f, 11f);

            using var fill = new SolidBrush(_accent);
            g.FillPolygon(fill, new[]
            {
                new PointF(10f, 9f), new PointF(14.5f, 11.5f), new PointF(10f, 14f)
            });
        }

        private void DrawOther(Graphics g)
        {
            var page = new[]
            {
                new PointF(4f, 1.5f), new PointF(10f, 1.5f), new PointF(12.5f, 4f),
                new PointF(12.5f, 14.5f), new PointF(4f, 14.5f)
            };

            using var fill = new SolidBrush(Color.FromArgb(60, 60, 68));
            using var pen = new Pen(_ink, 1.1f) { LineJoin = LineJoin.Round };

            g.FillPolygon(fill, page);
            g.DrawPolygon(pen, page);
            g.DrawLine(pen, 10f, 1.5f, 10f, 4f);
            g.DrawLine(pen, 10f, 4f, 12.5f, 4f);

            using var line = new Pen(_muted, 1f);
            g.DrawLine(line, 6f, 7.5f, 10.5f, 7.5f);
            g.DrawLine(line, 6f, 10f, 10.5f, 10f);
        }

        public void Dispose()
        {
            Images.Dispose();

            foreach (var bitmap in _bitmaps)
                bitmap.Dispose();

            _bitmaps.Clear();
        }
    }
}
