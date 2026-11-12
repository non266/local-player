using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 自绘的专辑封面控件：中间是封面图，下面是曲名 / 艺术家 / 专辑。
    /// <para>
    /// 封面来源依次是：文件内嵌图片（ID3 APIC / FLAC PICTURE / MP4 covr）
    /// → 同目录的 <c>cover.jpg</c> / <c>folder.jpg</c> 等常见命名
    /// （找哪个文件由 <see cref="SidecarCover"/> 定，系统媒体控件那边用的是同一份）。
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

        private Image? _cover;

        /// <summary>只读入口：现在挂着的封面（给冒烟测试用）。</summary>
        internal Image? Cover => _cover;

        /// <summary>标签还在读时那一行提示（侧栏封面、音频卡片、信息页共用同一句）。</summary>
        internal const string PendingText = "正在读取标签…";

        private string? _title;
        private string? _artist;
        private string? _album;

        /// <summary>只读入口：现在画着的三行文字（给冒烟测试判"占位换没换掉"）。</summary>
        internal string? TitleText => _title;
        internal string? ArtistText => _artist;
        internal string? AlbumText => _album;

        // 字体必须缓存：每次绘制都 new Font 的话，每帧都会漏一个 GDI 对象。
        private Font? _titleFont;
        private Font? _titleFontSource;
        private Font? _iconFont;
        private float _iconFontPixelHeight;

        // ---- 绘制缓存（1.18.0）-------------------------------------------------
        // 反馈里的原话是"同一帧里 MeasureText 算了两遍"：量一遍算布局、画的时候又量一遍。
        // 现在排版结果只算一次并缓存（宽度 / 文案 / 字体 / DPI 都不变就一直用），
        // 绘制直接取缓存里的行高。圆角路径与两支笔也按"尺寸 / 颜色"缓存。

        /// <summary>一次排版的结果：每行文案与高度。</summary>
        private sealed class TextBlockLayout
        {
            internal static readonly TextBlockLayout Empty = new();

            internal string[] Lines = Array.Empty<string>();
            internal int[] Heights = Array.Empty<int>();
            internal int Total;
        }

        private TextBlockLayout? _textLayout;
        private int _textLayoutWidth = -1;
        private float _textLayoutDpi;
        private string? _textLayoutTitle;
        private string? _textLayoutArtist;
        private string? _textLayoutAlbum;
        private Font? _textLayoutFont;
        private Font? _textLayoutTitleFont;

        private GraphicsPath? _squarePath;
        private Rectangle _squarePathBounds;
        private float _squarePathRadius = -1f;

        /// <summary>上一次建路径用的矩形与半径（冒烟测试失败时打出来定位用）。</summary>
        internal Rectangle LastPathSquare { get; private set; }

        internal float LastPathRadius { get; private set; }

        private SolidBrush? _placeholderBackBrush;
        private SolidBrush? _placeholderForeBrush;
        private Pen? _borderPen;
        private Color _borderPenColor;
        private Color _placeholderBackColor;

        /// <summary>排版时量了几次字（冒烟测试用：一次绘制应该只有一次）。</summary>
        internal int TextMeasureCount { get; private set; }

        /// <summary>圆角路径建了几次（冒烟测试用：尺寸不变就该一直是 1）。</summary>
        internal int PathBuildCount { get; private set; }

        /// <summary>
        /// 把两个计数清零（冒烟测试用）。
        /// <para>
        /// 为什么要这个：`DrawToBitmap` 会先把句柄建出来，那时 `DeviceDpi` 还没定，
        /// 画的那一帧与之后尺寸 / DPI 都不同——不剔掉预热帧，计数就会多出一遍。
        /// </para>
        /// </summary>
        internal void ResetPaintCounters()
        {
            TextMeasureCount = 0;
            PathBuildCount = 0;
        }

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

        /// <summary>
        /// 封面下面要不要再画标题 / 艺术家 / 专辑三行。
        /// <para>迷你模式的缩略图只要封面：那个方框本来就只有一百多像素，三行文字会把封面
        /// 挤成一个很小的点（所有者截图里的"错位"）。关掉之后封面占满可画区域。</para>
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool ShowText { get; set; } = true;

        /// <summary>
        /// 显示某个文件的封面与文字信息；传 <c>null</c> 清空。
        /// </summary>
        /// <param name="pending">
        /// 标签还在读（1.19.0）：标题先显示文件名、副标题写 <see cref="PendingText"/>、封面留空，
        /// 而且<b>不去找外挂封面</b>——占位要的是"把上一首那张摘掉"，不是切歌时枚举目录。
        /// </param>
        public void ShowTrack(string? path, MediaTags? tags, bool pending = false)
        {
            _title = FirstNonEmpty(tags?.Title, path == null ? null : Path.GetFileNameWithoutExtension(path));

            if (pending)
            {
                _artist = PendingText;
                _album = null;
                SetCover(null);
                return;
            }

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

            var layout = MeasureLayout();

            DrawCover(g, layout.Square);

            if (layout.TextHeight > 0)
            {
                DrawTextBlock(
                    g,
                    new Rectangle(
                        padding,
                        layout.Square.Bottom + (int)Math.Round(10 * DpiScale),
                        available,
                        layout.TextHeight));
            }
        }

        /// <summary>
        /// 封面现在画在哪（按当前 <c>ClientSize</c> 现算）。
        /// <para>给冒烟测试用：音频窗口那张卡片要断言"封面在卡片里水平居中"，
        /// 靠它比靠像素比色稳。</para>
        /// </summary>
        internal Rectangle CoverRectangle
        {
            get
            {
                var padding = (int)Math.Round(12 * DpiScale);
                if (ClientSize.Width - padding * 2 <= 8) return Rectangle.Empty;

                return MeasureLayout().Square;
            }
        }

        /// <summary>封面方框与文字块高度：封面先占一个正方形，剩下的高度留给文字。</summary>
        private (Rectangle Square, int TextHeight) MeasureLayout()
        {
            var padding = (int)Math.Round(12 * DpiScale);
            var available = ClientSize.Width - padding * 2;

            // 只画封面的模式（迷你缩略图）不占文字块的高度
            var textHeight = ShowText ? BuildTextLayout(available).Total : 0;
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

            return (square, textHeight);
        }

        /// <summary>
        /// 封面那个圆角方框的路径。**按尺寸缓存**：尺寸 / 半径 / DPI 不变就一直复用，
        /// 变的时候才重建并释放旧的（每帧 new 一个 GraphicsPath 是白花的）。
        /// </summary>
        private GraphicsPath GetSquarePath(Rectangle square, float radius)
        {
            if (_squarePath != null && _squarePathBounds == square && Math.Abs(_squarePathRadius - radius) < 0.01f)
                return _squarePath;

            _squarePath?.Dispose();
            _squarePath = GdiHelpers.RoundedRect(
                new RectangleF(square.X, square.Y, square.Width, square.Height), radius);
            _squarePathBounds = square;
            _squarePathRadius = radius;
            LastPathSquare = square;
            LastPathRadius = radius;
            PathBuildCount++;

            return _squarePath;
        }

        private SolidBrush GetPlaceholderBackBrush()
        {
            if (_placeholderBackBrush != null && _placeholderBackColor == _placeholderBack)
                return _placeholderBackBrush;

            _placeholderBackBrush?.Dispose();
            _placeholderBackBrush = new SolidBrush(_placeholderBack);
            _placeholderBackColor = _placeholderBack;
            return _placeholderBackBrush;
        }

        private SolidBrush GetPlaceholderForeBrush()
        {
            return _placeholderForeBrush ??= new SolidBrush(_placeholderFore);
        }

        private Pen GetBorderPen()
        {
            if (_borderPen != null && _borderPenColor == _borderColor) return _borderPen;

            _borderPen?.Dispose();
            _borderPen = new Pen(_borderColor);
            _borderPenColor = _borderColor;
            return _borderPen;
        }

        private void DrawCover(Graphics g, Rectangle square)
        {
            if (square.Width <= 0 || square.Height <= 0) return;

            var path = GetSquarePath(square, 8f * DpiScale);

            if (_cover == null)
            {
                g.FillPath(GetPlaceholderBackBrush(), path);
                g.DrawPath(GetBorderPen(), path);

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

            // ⚠ Region 要自己释放：Graphics.Clip 的 getter 每次都 new 一个 Region。
            using var previousClip = g.Clip;
            var previousInterpolation = g.InterpolationMode;

            g.SetClip(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_cover, target);
            g.InterpolationMode = previousInterpolation;
            g.Clip = previousClip;

            g.DrawPath(GetBorderPen(), path);
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

        /// <summary>
        /// 排版一次并缓存：每行文案 + 每行高度 + 总高。
        /// <para>
        /// 缓存键是「宽度 / DPI / 三行文案 / 字体」——**这些不变就一次都不再量**。
        /// 以前这里是两处各量一遍（算布局一处、绘制一处），同一帧里同样的字量两次。
        /// </para>
        /// </summary>
        private TextBlockLayout BuildTextLayout(int width)
        {
            var titleFont = TitleFont;

            if (_textLayout != null &&
                _textLayoutWidth == width &&
                Math.Abs(_textLayoutDpi - DpiScale) < 0.001f &&
                ReferenceEquals(_textLayoutFont, Font) &&
                ReferenceEquals(_textLayoutTitleFont, titleFont) &&
                string.Equals(_textLayoutTitle, _title, StringComparison.Ordinal) &&
                string.Equals(_textLayoutArtist, _artist, StringComparison.Ordinal) &&
                string.Equals(_textLayoutAlbum, _album, StringComparison.Ordinal))
            {
                return _textLayout;
            }

            var layout = new TextBlockLayout();
            var lines = BuildTextLines();

            if (lines.Length == 0)
            {
                _textLayout = layout;
                _textLayoutWidth = width;
                _textLayoutDpi = DpiScale;
                _textLayoutFont = Font;
                _textLayoutTitleFont = titleFont;
                _textLayoutTitle = _title;
                _textLayoutArtist = _artist;
                _textLayoutAlbum = _album;
                return layout;
            }

            layout.Lines = lines;
            layout.Heights = new int[lines.Length];

            var total = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var font = i == 0 ? titleFont : Font;

                TextMeasureCount++;

                var size = TextRenderer.MeasureText(
                    lines[i], font, new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

                var height = Math.Max(font.Height, size.Height);
                layout.Heights[i] = height;

                total += height;
                if (i > 0) total += (int)Math.Round(2 * DpiScale);
            }

            layout.Total = total;

            _textLayout = layout;
            _textLayoutWidth = width;
            _textLayoutDpi = DpiScale;
            _textLayoutFont = Font;
            _textLayoutTitleFont = titleFont;
            _textLayoutTitle = _title;
            _textLayoutArtist = _artist;
            _textLayoutAlbum = _album;

            return layout;
        }

        private void DrawTextBlock(Graphics g, Rectangle area)
        {
            var layout = BuildTextLayout(area.Width);
            if (layout.Lines.Length == 0) return;

            var titleFont = TitleFont;
            var y = area.Y;

            for (var i = 0; i < layout.Lines.Length; i++)
            {
                var font = i == 0 ? titleFont : Font;
                var height = layout.Heights[i];

                if (y + height > area.Bottom) break;

                var color = i == 0 ? _textColor : _subTextColor;

                TextRenderer.DrawText(
                    g, layout.Lines[i], font, new Rectangle(area.X, y, area.Width, height), color,
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

            var sidecar = SidecarCover.FindInFolder(folder);
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
        /// 判定与 <see cref="SidecarCover.FindInFolder"/> 是同一份（连优先序也同一份），
        /// 所以不会出现"这里说有、那里说没有"。
        /// </para>
        /// </summary>
        internal static bool HasSidecarCover(string folder) => SidecarCover.HasInFolder(folder);

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

                _squarePath?.Dispose();
                _squarePath = null;

                _placeholderBackBrush?.Dispose();
                _placeholderBackBrush = null;

                _placeholderForeBrush?.Dispose();
                _placeholderForeBrush = null;

                _borderPen?.Dispose();
                _borderPen = null;
            }

            base.Dispose(disposing);
        }
    }
}
