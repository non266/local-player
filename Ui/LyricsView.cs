using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 自绘歌词控件。
    /// <para>
    /// 带时间轴（LRC / SYLT）时高亮当前行并平滑滚动到视野偏上的位置，
    /// 点击某一行可以直接跳到那一句；纯文本歌词则整段换行显示，可用滚轮浏览。
    /// </para>
    /// <para>
    /// 换行后的行高只在「文档 / 宽度 / 字体」变化时重算并缓存，
    /// 每帧绘制只做一次平移，所以滚动是流畅的。
    /// </para>
    /// </summary>
    internal sealed class LyricsView : Control
    {
        /// <summary>滚动动画的刷新间隔（毫秒）。</summary>
        private const int AnimationInterval = 15;

        /// <summary>当前行停在控件高度的这个比例处——放在偏上位置，能同时看到下一句。</summary>
        private const float FocusRatio = 0.35f;

        /// <summary>缓动系数：每帧走完剩余距离的这个比例。</summary>
        private const float EaseFactor = 0.22f;

        /// <summary>用户滚轮之后，暂停自动跟随的时间（毫秒）。</summary>
        private const int UserScrollHoldMs = 4000;

        private const TextFormatFlags WrapFlags =
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

        private const TextFormatFlags DrawFlags =
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter;

        /// <summary>一行的布局结果（换行之后的位置与高度）。</summary>
        private sealed class LineBox
        {
            public int Index;
            public int Top;
            public int Height;
            public TimeSpan Time;
        }

        private readonly List<LineBox> _boxes = new List<LineBox>();
        private readonly Timer _animation;

        /// <summary>
        /// 悬停时显示"这一句对应的时间点"的气泡。
        /// <para>只在带时间轴的歌词上弹；纯文本歌词没有时间戳，弹出来只会是 00:00:00。</para>
        /// </summary>
        private readonly ToolTip _timeTip = new ToolTip
        {
            ShowAlways = true,
            InitialDelay = 240,
            ReshowDelay = 60,
            AutoPopDelay = 5000,
            UseAnimation = true,
            UseFading = true
        };

        /// <summary>气泡当前对应的是哪一行；-1 表示没有气泡。</summary>
        private int _tipIndex = -1;

        private LyricsDocument _document = LyricsDocument.Empty;

        /// <summary>右上角那行偏移提示；<c>null</c> = 不显示（偏移为 0 时）。</summary>
        private string? _offsetHint;

        /// <summary>只读入口：现在显示的偏移提示（给冒烟测试用）。</summary>
        internal string? OffsetHint => _offsetHint;
        private bool _layoutDirty = true;
        private int _layoutWidth = -1;
        private int _contentHeight;

        private Font? _activeFont;
        private Font? _activeFontSource;

        /// <summary>用户指定字体族时自己建的那个字体（换的时候要释放）。</summary>
        private Font? _explicitFont;

        private float _scroll;
        private float _target;
        private int _activeIndex = -1;
        private int _hoverIndex = -1;
        private long _lastUserScrollTick;
        private bool _hovering;

        private Color _idleColor = Color.FromArgb(0xB4, 0xB4, 0xBC);
        private Color _activeColor = Color.FromArgb(0x4C, 0xC2, 0xFF);
        private Color _hoverColor = Color.White;
        private Color _hintColor = Color.FromArgb(0x80, 0x80, 0x88);
        private Color _activeBackColor = Color.FromArgb(0x28, 0x4C, 0xC2, 0xFF);

        public LyricsView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);

            _animation = new Timer { Interval = AnimationInterval };
            _animation.Tick += OnAnimationTick;
        }

        /// <summary>用户点击了带时间轴的某一行，参数是要跳转到的时间点。</summary>
        public event EventHandler<TimeSpan>? SeekRequested;

        /// <summary>当前显示的歌词文档。</summary>
        public LyricsDocument Document => _document;

        /// <summary>换成另一份歌词。</summary>
        public void SetDocument(LyricsDocument? document)
        {
            _document = document ?? LyricsDocument.Empty;
            _layoutDirty = true;
            _activeIndex = -1;
            _hoverIndex = -1;
            _scroll = 0f;
            _target = 0f;
            _lastUserScrollTick = 0;
            _animation.Stop();
            HideTimeTip();
            Invalidate();
        }

        /// <summary>
        /// 右上角那行"当前歌词偏移"（例如 <c>+1.5s</c>）；<c>null</c> 表示不显示。
        /// <para>
        /// 偏移是上一次调完就留在那儿的，过几天再看歌词已经想不起来自己动过它，
        /// 于是"歌词怎么还是对不上"就变成了一个说不清的问题。这里常驻一行小字（很淡），
        /// 只要不为 0 就一直在。
        /// </para>
        /// </summary>
        public void SetOffsetHint(string? text)
        {
            var hint = string.IsNullOrWhiteSpace(text) ? null : text;

            if (string.Equals(hint, _offsetHint, StringComparison.Ordinal)) return;

            _offsetHint = hint;
            Invalidate();
        }

        /// <summary>播放位置推进；跨越到新的一句时开始滚动。</summary>
        public void UpdatePosition(TimeSpan position)
        {
            if (_document.IsSynchronized)
            {
                var index = _document.LineIndexAt(position);
                if (index == _activeIndex) return;

                _activeIndex = index;
                _target = FocusTargetFor(index);
                StartAnimation();
                Invalidate();
            }
        }

        /// <summary>滚轮滚动（由侧栏的消息过滤器转发过来，见 <see cref="SidebarPanel"/>）。</summary>
        public void ScrollBy(int delta)
        {
            if (_document.IsEmpty) return;

            EnsureLayout();

            _lastUserScrollTick = Environment.TickCount64;
            _animation.Stop();

            _target = Math.Clamp(_target - delta, 0f, MaxScroll);
            _scroll = _target;      // 滚轮直接跟手，不做缓动

            // 滚动之后光标底下换了一行，之前那个气泡已经对不上了。
            HideTimeTip();

            Invalidate();
        }

        /// <summary>是否正处于"用户手动滚动"的暂停跟随状态。</summary>
        private bool IsUserScrolling =>
            _lastUserScrollTick != 0 && Environment.TickCount64 - _lastUserScrollTick < UserScrollHoldMs;

        // ---- 绘制 -------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            EnsureLayout();

            if (_document.IsEmpty)
            {
                DrawHint(g, "没有找到歌词");
                DrawOffsetHint(g);
                return;
            }

            if (_document.IsSynchronized)
                DrawSynchronized(g);
            else
                DrawPlainText(g);

            DrawOffsetHint(g);
        }

        private void DrawSynchronized(Graphics g)
        {
            var lines = _document.Lines;
            var width = Math.Max(1, _layoutWidth);

            for (var i = 0; i < _boxes.Count; i++)
            {
                var box = _boxes[i];
                var y = box.Top - _scroll;

                // 视口外的直接跳过
                if (y > ClientSize.Height || y + box.Height < 0) continue;
                if (box.Index >= lines.Count) continue;

                var text = lines[box.Index].Text;
                if (string.IsNullOrEmpty(text)) text = "♪";

                var isActive = i == _activeIndex;
                var isHover = i == _hoverIndex;

                var rect = new Rectangle(Padding.Left, (int)Math.Round(y), width, box.Height);

                if (isActive)
                {
                    // 当前行加一层淡淡的强调色底，位置感更明确
                    var highlight = new RectangleF(rect.X + 2, rect.Y + 1, rect.Width - 4, rect.Height - 2);
                    using var brush = new SolidBrush(_activeBackColor);
                    using var path = GdiHelpers.RoundedRect(highlight, 6f * DpiScale);
                    g.FillPath(brush, path);
                }

                var color = isActive ? _activeColor : isHover ? _hoverColor : _idleColor;
                var font = isActive ? ActiveFont : Font;

                TextRenderer.DrawText(g, text, font, rect, color, DrawFlags);
            }
        }

        private void DrawPlainText(Graphics g)
        {
            if (_boxes.Count == 0) return;

            var box = _boxes[0];
            var rect = new Rectangle(
                Padding.Left,
                (int)Math.Round(box.Top - _scroll),
                Math.Max(1, _layoutWidth),
                box.Height);

            TextRenderer.DrawText(
                g, _document.PlainText, Font, rect, _idleColor,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        private void DrawHint(Graphics g, string text)
        {
            TextRenderer.DrawText(
                g, text, Font, ClientRectangle, _hintColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        /// <summary>
        /// 右上角画一行淡淡的"当前歌词偏移"。
        /// <para>
        /// 底下垫一层不透明的背景色：这行字是画在歌词上面的，不垫的话和歌词叠在一起谁也看不清。
        /// 用背景色而不是强调色，是为了让它明显是"角落上的状态"而不是被高亮的歌词。
        /// </para>
        /// </summary>
        private void DrawOffsetHint(Graphics g)
        {
            if (_offsetHint == null) return;

            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

            var size = TextRenderer.MeasureText(g, _offsetHint, Font, new Size(int.MaxValue, int.MaxValue), flags);
            if (size.Width <= 0 || size.Height <= 0) return;

            var pad = (int)Math.Round(4 * DpiScale);
            var margin = (int)Math.Round(3 * DpiScale);

            var chip = new Rectangle(
                Math.Max(0, ClientSize.Width - size.Width - pad * 2 - margin),
                margin,
                size.Width + pad * 2,
                size.Height + pad);

            if (chip.Right <= 0 || chip.Bottom <= 0) return;

            using (var brush = new SolidBrush(BackColor))
                g.FillRectangle(brush, chip);

            TextRenderer.DrawText(
                g, _offsetHint, Font,
                new Rectangle(chip.X + pad, chip.Y + pad / 2, size.Width, size.Height),
                _hintColor, flags);
        }

        // ---- 布局 -------------------------------------------------------------

        private float DpiScale => DeviceDpi / 96f;

        private int LineGap => (int)Math.Round(10 * DpiScale);

        private float MaxScroll
        {
            get
            {
                var overflow = _contentHeight - ClientSize.Height;
                return overflow > 0 ? overflow : 0f;
            }
        }

        internal void EnsureLayout()
        {
            var width = ClientSize.Width - Padding.Horizontal;
            if (width <= 0)
            {
                _boxes.Clear();
                _contentHeight = 0;
                return;
            }

            if (!_layoutDirty && _layoutWidth == width) return;

            _boxes.Clear();
            _layoutWidth = width;
            _layoutDirty = false;

            var font = Font;
            var top = Padding.Top;

            if (_document.IsSynchronized)
            {
                var lines = _document.Lines;

                for (var i = 0; i < lines.Count; i++)
                {
                    var text = string.IsNullOrEmpty(lines[i].Text) ? "♪" : lines[i].Text;
                    var height = MeasureWrapped(text, font, width);

                    _boxes.Add(new LineBox { Index = i, Top = top, Height = height, Time = lines[i].Time });
                    top += height + LineGap;
                }
            }
            else if (!_document.IsEmpty)
            {
                var height = MeasureWrapped(_document.PlainText, font, width);
                _boxes.Add(new LineBox { Index = 0, Top = top, Height = height });
                top += height;
            }

            _contentHeight = top + Padding.Bottom;

            // 布局变了以后原来的滚动位置可能越界
            _target = Math.Clamp(_target, 0f, MaxScroll);
            _scroll = Math.Clamp(_scroll, 0f, MaxScroll);
        }

        private int MeasureWrapped(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return font.Height;

            var size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), WrapFlags);
            return Math.Max(font.Height, size.Height);
        }

        private float FocusTargetFor(int index)
        {
            if (index < 0 || index >= _boxes.Count) return 0f;

            var box = _boxes[index];
            var desired = box.Top + box.Height / 2f - ClientSize.Height * FocusRatio;
            return Math.Clamp(desired, 0f, MaxScroll);
        }

        // ---- 滚动动画 ---------------------------------------------------------

        private void StartAnimation()
        {
            if (!_animation.Enabled) _animation.Start();
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            EnsureLayout();

            // 用户手动滚动期间不抢位置；松手（或过了保持时间）后自动回到当前行
            if (_document.IsSynchronized && !IsUserScrolling)
                _target = FocusTargetFor(_activeIndex);

            var difference = _target - _scroll;

            if (Math.Abs(difference) < 0.5f)
            {
                _scroll = _target;
                _animation.Stop();
                Invalidate();
                return;
            }

            _scroll += difference * EaseFactor;
            Invalidate();
        }

        // ---- 交互 -------------------------------------------------------------

        /// <summary>把控件坐标换算成行下标；不在任何一行上时返回 -1。</summary>
        private int IndexAt(int y)
        {
            if (_boxes.Count == 0) return -1;

            var position = y + _scroll;

            for (var i = 0; i < _boxes.Count; i++)
            {
                var box = _boxes[i];
                if (position >= box.Top && position <= box.Top + box.Height) return i;
            }

            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_hovering)
            {
                _hovering = true;
                _lastUserScrollTick = 0;    // 鼠标回到歌词上，恢复跟随
            }

            var index = _document.IsSynchronized ? IndexAt(e.Y) : -1;

            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }

            UpdateTimeTip(index, e.Location);

            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovering = false;

            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }

            HideTimeTip();

            Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        /// <summary>
        /// 鼠标停在一行上时，在光标旁边弹出这一句对应的时间点。
        /// <para>
        /// 只在<b>换行</b>时才重弹：同一行里跟着鼠标移动会让气泡不停闪，
        /// 而且气泡本身会挡住歌词。滚轮滚动、换歌词、移出控件都会把它收掉。
        /// </para>
        /// </summary>
        private void UpdateTimeTip(int index, Point location)
        {
            var text = TimeTipTextFor(index);

            if (text == null)
            {
                HideTimeTip();
                return;
            }

            if (index == _tipIndex) return;

            _tipIndex = index;

            // 先 Hide 再 Show：不这样在同一个控件上换文字时气泡位置不会更新。
            _timeTip.Hide(this);

            var offsetX = (int)Math.Round(14 * DpiScale);
            var offsetY = (int)Math.Round(20 * DpiScale);

            _timeTip.Show(text, this, location.X + offsetX, location.Y + offsetY, 5000);
        }

        private void HideTimeTip()
        {
            if (_tipIndex == -1) return;

            _tipIndex = -1;
            _timeTip.Hide(this);
        }

        /// <summary>
        /// 某一行悬停时要显示的文字；没有时间可显示时返回 <c>null</c>。
        /// <para>纯文本歌词（没有时间轴）与无效行都返回 <c>null</c>。</para>
        /// </summary>
        internal string? TimeTipTextFor(int index)
        {
            if (!_document.IsSynchronized) return null;
            if (index < 0 || index >= _boxes.Count) return null;

            return TimeFormatter.FormatWithHours(_boxes[index].Time);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _document.IsSynchronized)
            {
                var index = IndexAt(e.Y);
                if (index >= 0 && index < _boxes.Count)
                    SeekRequested?.Invoke(this, _boxes[index].Time);
            }

            base.OnMouseClick(e);
        }

        /// <summary>
        /// 换歌词字体族（字号与字重不变）。<paramref name="family"/> 为空表示回到默认字体。
        /// <para>
        /// 换字体必须重排：行高、折行位置、滚动位置全都跟着变，
        /// 赋值 <see cref="Control.Font"/> 会触发 <see cref="OnFontChanged"/> 去置脏。
        /// </para>
        /// <para>
        /// 自己 new 的字体自己释放：<c>Control.Font</c> 被显式赋值之后，
        /// WinForms 不会替你回收它（它只回收自己算出来的那个）。
        /// </para>
        /// </summary>
        public void ApplyFontFamily(string? family)
        {
            var target = UiFonts.Resolve(family, Font.Size, Font.Style);

            if (string.Equals(Font.FontFamily.Name, target.FontFamily.Name, StringComparison.OrdinalIgnoreCase))
            {
                target.Dispose();
                return;
            }

            var previous = _explicitFont;
            _explicitFont = target;
            Font = target;

            previous?.Dispose();
        }

        /// <summary>配色变化时调用。</summary>
        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;
            _idleColor = palette.ListFore;
            _activeColor = palette.Accent;

            // 悬停色必须和 _idleColor 不同，否则"鼠标移上去这一行会变色"的提示等于没有。
            // 之前这里和 idle 都赋成了 palette.ListFore，字段初始值（Color.White）本来是有
            // 区分的，ApplyPalette 一跑就被永久拉平——功能还在（光标是手型、点了能跳转），
            // 但看不出哪一行可以点。用 PlayingFore：四套主题里它都是"在 PanelBack 上看得清"
            // 的强调文字色，语义上也就是"这一项被高亮了"。
            _hoverColor = palette.PlayingFore;
            _hintColor = palette.HintFore;
            _activeBackColor = Color.FromArgb(38, palette.Accent);
            Invalidate();
        }

        private Font ActiveFont
        {
            get
            {
                if (_activeFont != null && ReferenceEquals(_activeFontSource, Font)) return _activeFont;

                _activeFont?.Dispose();
                _activeFontSource = Font;

                try
                {
                    _activeFont = new Font(Font, FontStyle.Bold);
                }
                catch (Exception)
                {
                    // 字体族不支持加粗时退回原字体
                    _activeFont = null;
                }

                return _activeFont ?? Font;
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            _layoutDirty = true;
            base.OnFontChanged(e);
        }

        protected override void OnResize(EventArgs e)
        {
            _layoutDirty = true;
            base.OnResize(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animation.Stop();
                _animation.Dispose();
                _activeFont?.Dispose();
                _activeFont = null;
                _explicitFont?.Dispose();
                _explicitFont = null;
                _timeTip.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
