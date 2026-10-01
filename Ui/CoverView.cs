using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 自绘的专辑封面控件：中间是封面图，下面是曲名 / 艺术家 / 专辑。
    /// <para>
    /// 封面来源依次是：文件内嵌图片（ID3 APIC / FLAC PICTURE / MP4 covr）
    /// → 同目录的 <c>cover.jpg</c> / <c>folder.jpg</c> 等常见命名。
    /// </para>
    /// <para>
    /// 解码时一律先缩到 <see cref="MaxDecodeSide"/> 像素以内：内嵌封面动辄 1~2 MB、
    /// 原始分辨率上千像素，整张留在内存里没有必要，侧栏最宽也就几百像素。
    /// </para>
    /// </summary>
    internal sealed class CoverView : Control
    {
        /// <summary>解码后最长边的上限（像素）。</summary>
        private const int MaxDecodeSide = 1024;

        /// <summary>同目录封面文件的常见命名（不含扩展名，大小写不敏感）。</summary>
        private static readonly string[] SidecarNames =
            { "cover", "folder", "front", "album", "albumart", "artwork", "cd" };

        private static readonly string[] SidecarExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        private Image? _cover;

        /// <summary>只读入口：现在挂着的封面（给冒烟测试用）。</summary>
        internal Image? Cover => _cover;

        private string? _title;
        private string? _artist;
        private string? _album;

        // 字体必须缓存：每次绘制都 new Font 的话，每帧都会漏一个 GDI 对象。
        private Font? _titleFont;
        private Font? _titleFontSource;
        private Font? _iconFont;
        private float _iconFontPixelHeight;

        private Color _placeholderBack = Color.FromArgb(0x28, 0x28, 0x2C);
        private Color _placeholderFore = Color.FromArgb(0x80, 0x80, 0x88);
        private Color _textColor = Color.White;
        private Color _subTextColor = Color.FromArgb(0xB4, 0xB4, 0xBC);
        private Color _borderColor = Color.FromArgb(0x40, 0x40, 0x48);

        public CoverView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
        }

        /// <summary>显示某个文件的封面与文字信息；传 <c>null</c> 清空。</summary>
        public void ShowTrack(string? path, MediaTags? tags)
        {
            _title = FirstNonEmpty(tags?.Title, path == null ? null : Path.GetFileNameWithoutExtension(path));
            _artist = tags?.Artist;
            _album = tags?.Album;

            SetCover(LoadCover(path, tags));
        }

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;
            _placeholderBack = palette.ListBack;
            _placeholderFore = palette.PlaceholderFore;
            _textColor = palette.ListFore;
            _subTextColor = palette.HintFore;
            _borderColor = palette.MenuBorder;
            Invalidate();
        }

        private void SetCover(Image? image)
        {
            if (!ReferenceEquals(_cover, image))
            {
                _cover?.Dispose();
                _cover = image;
            }

            Invalidate();
        }

        private float DpiScale => DeviceDpi / 96f;

        // ---- 绘制 -------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            var padding = (int)Math.Round(12 * DpiScale);
            var available = ClientSize.Width - padding * 2;

            if (available <= 8)
            {
                DrawHint(g, "侧栏太窄，拉宽一点就能看到封面。");
                return;
            }

            // 封面先占一个正方形，剩下的高度留给文字
            var textHeight = MeasureTextBlock(available);
            var maxSquare = ClientSize.Height - padding * 2 - textHeight - (int)Math.Round(10 * DpiScale);
            var side = Math.Max(0, Math.Min(available, maxSquare));

            if (side < 24)
            {
                // 高度不够时只画封面本身，文字牺牲掉
                side = Math.Max(0, Math.Min(available, ClientSize.Height - padding * 2));
                textHeight = 0;
            }

            var square = new Rectangle(
                padding + (available - side) / 2,
                padding,
                side,
                side);

            DrawCover(g, square);

            if (textHeight > 0)
                DrawTextBlock(g, new Rectangle(padding, square.Bottom + (int)Math.Round(10 * DpiScale), available, textHeight));
        }

        private void DrawCover(Graphics g, Rectangle square)
        {
            if (square.Width <= 0 || square.Height <= 0) return;

            var bounds = new RectangleF(square.X, square.Y, square.Width, square.Height);
            using var path = GdiHelpers.RoundedRect(bounds, 8f * DpiScale);

            if (_cover == null)
            {
                using (var back = new SolidBrush(_placeholderBack))
                    g.FillPath(back, path);

                using (var pen = new Pen(_borderColor))
                    g.DrawPath(pen, path);

                // 用音符字符当占位图，比画矢量图标省事且各字体都有
                TextRenderer.DrawText(
                    g, "♪", IconFont(square.Height * 0.34f), square, _placeholderFore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                return;
            }

            // 等比缩放，不裁剪原图
            var scale = Math.Min(square.Width / (float)_cover.Width, square.Height / (float)_cover.Height);
            var width = Math.Max(1, (int)Math.Round(_cover.Width * scale));
            var height = Math.Max(1, (int)Math.Round(_cover.Height * scale));
            var target = new Rectangle(
                square.X + (square.Width - width) / 2,
                square.Y + (square.Height - height) / 2,
                width,
                height);

            var previousClip = g.Clip;
            var previousInterpolation = g.InterpolationMode;

            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_cover, target);
            g.InterpolationMode = previousInterpolation;
            g.Clip = previousClip;

            using (var pen = new Pen(_borderColor))
                g.DrawPath(pen, path);
        }

        /// <summary>占位音符的字体；尺寸变了才重建。</summary>
        private Font IconFont(float pixelHeight)
        {
            if (_iconFont != null && Math.Abs(_iconFontPixelHeight - pixelHeight) < 0.5f) return _iconFont;

            _iconFont?.Dispose();
            _iconFontPixelHeight = pixelHeight;

            var pointSize = Math.Max(8f, pixelHeight * 72f / 96f);

            try
            {
                _iconFont = new Font(Font.FontFamily, pointSize, FontStyle.Regular);
            }
            catch (Exception)
            {
                _iconFont = null;
            }

            return _iconFont ?? Font;
        }

        private int MeasureTextBlock(int width)
        {
            var lines = BuildTextLines();
            if (lines.Length == 0) return 0;

            var total = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var font = i == 0 ? TitleFont : Font;
                var size = TextRenderer.MeasureText(
                    lines[i], font, new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

                total += Math.Max(font.Height, size.Height);
                if (i > 0) total += (int)Math.Round(2 * DpiScale);
            }

            return total;
        }

        private void DrawTextBlock(Graphics g, Rectangle area)
        {
            var lines = BuildTextLines();
            if (lines.Length == 0) return;

            var y = area.Y;

            for (var i = 0; i < lines.Length; i++)
            {
                var font = i == 0 ? TitleFont : Font;
                var width = area.Width;

                var size = TextRenderer.MeasureText(
                    lines[i], font, new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

                var height = Math.Max(font.Height, size.Height);
                if (y + height > area.Bottom) break;

                var color = i == 0 ? _textColor : _subTextColor;

                TextRenderer.DrawText(
                    g, lines[i], font, new Rectangle(area.X, y, width, height), color,
                    TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                y += height + (int)Math.Round(2 * DpiScale);
            }
        }

        private string[] BuildTextLines()
        {
            var lines = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrWhiteSpace(_title)) lines.Add(_title!.Trim());
            if (!string.IsNullOrWhiteSpace(_artist)) lines.Add(_artist!.Trim());
            if (!string.IsNullOrWhiteSpace(_album) &&
                !string.Equals(_album!.Trim(), _title?.Trim(), StringComparison.OrdinalIgnoreCase))
                lines.Add(_album!.Trim());

            return lines.ToArray();
        }

        private void DrawHint(Graphics g, string text)
        {
            TextRenderer.DrawText(
                g, text, Font, ClientRectangle, _subTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        private Font TitleFont
        {
            get
            {
                if (_titleFont != null && ReferenceEquals(_titleFontSource, Font)) return _titleFont;

                _titleFont?.Dispose();
                _titleFontSource = Font;

                try
                {
                    _titleFont = new Font(Font, FontStyle.Bold);
                }
                catch (Exception)
                {
                    _titleFont = null;
                }

                return _titleFont ?? Font;
            }
        }

        // ---- 载入 -------------------------------------------------------------

        private static Image? LoadCover(string? path, MediaTags? tags)
        {
            // 1) 文件内嵌的封面
            if (tags is { HasCoverArt: true })
            {
                var image = Decode(tags.CoverArt!);
                if (image != null) return image;
            }

            // 2) 同目录下常见的封面文件
            var folder = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder)) return null;

            var sidecar = FindSidecar(folder);
            if (sidecar == null) return null;

            try
            {
                return Decode(File.ReadAllBytes(sidecar));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 这个目录里有没有 <c>cover.jpg</c> / <c>folder.jpg</c> 这类现成的封面文件。
        /// <para>
        /// 给"要不要去网上找封面"用：目录里已经有封面了就没必要联网。
        /// 判定规则必须和 <see cref="FindSidecar"/> 一致，所以直接复用它。
        /// </para>
        /// </summary>
        internal static bool HasSidecarCover(string folder) =>
            !string.IsNullOrEmpty(folder) && FindSidecar(folder) != null;

        /// <summary>
        /// 在目录里找常见命名的封面文件。
        /// <para>只枚举一次目录，而不是对着十几种文件名挨个 <c>File.Exists</c>。</para>
        /// </summary>
        private static string? FindSidecar(string folder)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (!SidecarNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                    var extension = Path.GetExtension(file);
                    if (SidecarExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return file;
                }
            }
            catch (Exception ex)
            {
// 目录不可读就当作没有封面
                AppLog.Swallowed("目录不可读就当作没有封面", ex);
            }

            return null;
        }

        private static Image? Decode(byte[] bytes)
        {
            if (bytes.Length == 0) return null;

            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
                return Downscale(source);
            }
            catch (Exception)
            {
                // 数据不是有效图片（或者格式太怪）就当作没有封面
                return null;
            }
        }

        /// <summary>把图片缩到 <see cref="MaxDecodeSide"/> 以内，并转成 32 位预乘 ARGB 便于快速绘制。</summary>
        private static Image? Downscale(Image source)
        {
            var width = source.Width;
            var height = source.Height;

            if (width <= 0 || height <= 0 || width > 20000 || height > 20000) return null;

            var scale = Math.Min(1.0, MaxDecodeSide / (double)Math.Max(width, height));
            var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

            var bitmap = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppPArgb);

            try
            {
                bitmap.SetResolution(96f, 96f);

                using var graphics = Graphics.FromImage(bitmap);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));

                return bitmap;
            }
            catch (Exception)
            {
                bitmap.Dispose();
                return null;
            }
        }

        private static string? FirstNonEmpty(params string?[] candidates)
        {
            foreach (var candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;

            return null;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            Invalidate();
            base.OnFontChanged(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cover?.Dispose();
                _cover = null;

                _titleFont?.Dispose();
                _titleFont = null;

                _iconFont?.Dispose();
                _iconFont = null;
            }

            base.Dispose(disposing);
        }
    }
}
