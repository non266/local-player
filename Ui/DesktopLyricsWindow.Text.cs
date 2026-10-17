using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

// 文字与排版参数：两行文字、字体缓存、颜色与各处尺度（都按 DPI 放大）。

namespace 播放器.Ui
{
    internal sealed partial class DesktopLyricsWindow
    {
        // ---- 文字 -------------------------------------------------------------

        private static StringFormat CreateFormat()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Near;

            // ⚠ GenericTypographic 里带着 StringFormatFlags.LineLimit，它的意思是
            // "只画完整的行"：矩形高度只要比行高差那么零点几像素，整行就<b>一个字都不画</b>。
            // 一开始就是这么踩的——量出来多高就画多高，结果位图整张全透明。
            format.FormatFlags &= ~StringFormatFlags.LineLimit;

            return format;
        }

        /// <summary>算出"当前句 / 第二行"两段文字，返回它们的拼装（用于判断有没有变化）。</summary>
        private string BuildTexts()
        {
            if (_document.IsSynchronized)
            {
                // LineIndexAt 给的是"同一时间戳那一组"的第一行（双语歌词里是原文）
                var index = _document.LineIndexAt(_position);

                _currentText = index < 0 ? IntroMark : Normalize(_document.Lines[index].Text);

                _nextText = string.Empty;
                _hasNext = false;

                if (_showNextLine && index >= 0)
                {
                    // 双语歌词（同一时间戳的原文 + 译文）：第二行优先放译文——
                    // 那才是"这一句"的另一半。想要"下一句"就把「有翻译时显示翻译」关掉。
                    var companion = _secondLineIsTranslation ? _document.CompanionOf(index) : null;

                    if (companion != null)
                    {
                        _nextText = Normalize(companion.Text);
                        _hasNext = true;
                    }
                    else
                    {
                        // 注意要跳过整组：不能把译文当成"下一句"
                        var next = _document.NextGroupStart(index);

                        if (next >= 0)
                        {
                            _nextText = Normalize(_document.Lines[next].Text);
                            _hasNext = true;
                        }
                    }
                }
            }
            else if (_title != null)
            {
                // 没有可用歌词：主句用同样的记号，曲名放到下一句的位置，
                // 这样"有没有歌词"一眼就能看出来。
                _currentText = IntroMark;
                _nextText = _title;
                _hasNext = _showNextLine;
            }
            else
            {
                _currentText = IdleTitle;
                _nextText = IdleHint;
                _hasNext = _showNextLine;
            }

            return _currentText + "\u0000" + (_hasNext ? _nextText : string.Empty);
        }

        /// <summary>空行在歌词里是"间奏"，显示成音符记号而不是一片空白。</summary>
        private static string Normalize(string text) => string.IsNullOrWhiteSpace(text) ? IntroMark : text.Trim();

        internal Font MainFont
        {
            get
            {
                if (_mainFont != null && _mainFontSize == _fontSize) return _mainFont;

                _mainFont?.Dispose();
                _mainFontSize = _fontSize;
                _mainFont = UiFonts.Resolve(_fontFamily, _fontSize, FontStyle.Bold);
                return _mainFont;
            }
        }

        internal Font NextFont
        {
            get
            {
                if (_nextFont != null && _nextFontSize == _fontSize) return _nextFont;

                _nextFont?.Dispose();
                _nextFontSize = _fontSize;
                _nextFont = UiFonts.Resolve(_fontFamily, _fontSize * NextLineRatio, FontStyle.Regular);
                return _nextFont;
            }
        }

        /// <summary>扔掉两个字体缓存；下一次取值会按当前的字体族与字号重建。</summary>
        private void DisposeFonts()
        {
            _mainFont?.Dispose();
            _mainFont = null;
            _mainFontSize = -1;

            _nextFont?.Dispose();
            _nextFont = null;
            _nextFontSize = -1;
        }

        /// <summary>
        /// 主句颜色。
        /// <para>
        /// 下一句用的是<b>同一个颜色</b>，只靠字号与字重拉开层次，不做减淡处理：
        /// 浅色主题的强调色 #0A84FF 在近黑底上本来只有 5.3:1，
        /// 再乘 0.62 会掉到 2.4:1（按 WCAG 相对亮度算过），下一句就糊了。
        /// </para>
        /// </summary>
        private Color MainColor => _colorChoice switch
        {
            DesktopLyricsColor.White => Color.FromArgb(0xFF, 0xFF, 0xFF),
            DesktopLyricsColor.Yellow => Color.FromArgb(0xFF, 0xD5, 0x2F),
            DesktopLyricsColor.Cyan => Color.FromArgb(0x4C, 0xE0, 0xFF),
            DesktopLyricsColor.Green => Color.FromArgb(0x86, 0xE0, 0x4C),
            DesktopLyricsColor.Pink => Color.FromArgb(0xFF, 0x8A, 0xC8),
            _ => _themeAccent
        };

        /// <summary>
        /// 文字外面那圈深色描边。
        /// <para>
        /// 背景透明之后，字是直接盖在别人的画面上的：白底网页上白字、亮画面上浅色字都会看不见。
        /// 描边是唯一一个"不靠背景板也能保证可读"的办法（字幕做了几十年了）。
        /// </para>
        /// </summary>
        private static Color OutlineColor => Color.FromArgb(190, 0, 0, 0);

        /// <summary>
        /// 鼠标停上来时那块背景的颜色。
        /// <para>
        /// 半透明：它是"鼠标进来了"的提示，同时底下的画面还能透出来一点，
        /// 不至于突然糊住一整块；平时完全不画。
        /// </para>
        /// </summary>
        private static Color HoverBackColor => Color.FromArgb(205, 0x12, 0x12, 0x18);

        private int Scaled(int design) => (int)Math.Round(design * DeviceDpi / 96.0);

        private int OutlineRadius => Math.Max(1, Scaled(2));

        private int PaddingX => OutlineRadius + Scaled(12);

        private int PaddingY => OutlineRadius + Scaled(7);

        private int LineGap => Scaled(6);

        private int MinimumBoxWidth => Scaled(96);

        /// <summary>画文本的矩形比量出来的宽度多留一点，避免"量的时候刚好放得下、画的时候又折行"。</summary>
        private const float WrapSlack = 2f;

        /// <summary>画的那一手再给行高留一点余量，别让浮点误差把整行吃掉（见 <see cref="CreateFormat"/>）。</summary>
        private const float Slice = 2f;

        private int MaximumContentWidth
        {
            get
            {
                var area = Screen.FromPoint(Location).WorkingArea;
                var maximum = (int)(area.Width * MaximumWidthRatio) - PaddingX * 2;
                return Math.Max(Scaled(200), maximum);
            }
        }

        /// <summary>
        /// 量文字用的 Graphics。
        /// <para>
        /// DPI 必须和渲染位图一致：字体按磅算，GDI+ 是按 Graphics 的 DPI 换算成像素的。
        /// 位图默认 96 DPI，125% 显示下量出来的字会比画出来的小一圈。
        /// </para>
        /// </summary>
        private Graphics MeasureGraphics
        {
            get
            {
                if (_measure != null && Math.Abs(_measure.DpiY - DeviceDpi) < 0.5) return _measure;

                _measure?.Dispose();
                _probe?.Dispose();

                _probe = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
                _probe.SetResolution(DeviceDpi, DeviceDpi);
                _measure = Graphics.FromImage(_probe);

                return _measure;
            }
        }

        private SizeF Measure(string text, Font font, int maximumWidth) =>
            MeasureGraphics.MeasureString(text, font, new SizeF(maximumWidth, 10000f), _format);
    }
}
