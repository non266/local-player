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
    internal sealed partial class LyricsView : Control
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
