using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 桌面歌词窗口：一个无边框、置顶、<b>不抢焦点</b>、<b>背景完全透明</b>的小窗口，
    /// 跟着播放进度显示当前这一句。
    /// <para>
    /// 它是纯展示控件：配色、字号、不透明度、锁定状态都由 <see cref="MainForm"/> 从设置里读出来推给它，
    /// 右键菜单也是外面挂上来的（<see cref="Form.ContextMenuStrip"/>），
    /// 这样"设置"只有一份来源，不做双向同步。
    /// </para>
    /// <para>
    /// 背景透明不是靠 <c>Form.Opacity</c>：那个只能整窗均匀变淡，背景仍然是一块颜色。
    /// 这里走 <c>UpdateLayeredWindow</c> + 32 位 ARGB 位图做<b>逐像素</b>透明——
    /// 只有字是有像素的，其余地方完全不占画面，点击也直接穿透到下面的程序。
    /// 代价是内容不能再用 <c>OnPaint</c> 画，必须自己画进位图再推给系统。
    /// </para>
    /// </summary>
    internal sealed partial class DesktopLyricsWindow : Form
    {
        /// <summary>下一句相对主句的字号比例。</summary>
        private const float NextLineRatio = 0.62f;

        /// <summary>取景框最多占工作区宽度的多少。</summary>
        private const double MaximumWidthRatio = 0.9;

        /// <summary>当前句还没开始时（前奏）显示的记号，和侧栏歌词页保持一致。</summary>
        private const string IntroMark = "♪";

        private const string IdleTitle = "桌面歌词";
        private const string IdleHint = "（未在播放）";

        // ---- 文字与布局 -------------------------------------------------------

        /// <summary>
        /// 度量与绘制共用同一套排版参数。
        /// <para>
        /// <see cref="StringFormat.GenericTypographic"/> 不能省：默认排版会在文字四周
        /// 各加约 1/6 字的空白，量出来的尺寸和画出来的对不上，自动宽度就会永远差一点。
        /// </para>
        /// </summary>
        private readonly StringFormat _format = CreateFormat();

        private LyricsDocument _document = LyricsDocument.Empty;

        // ---- 给冒烟测试的只读入口（主工程里有 InternalsVisibleTo("SmokeTest")）----
        internal LyricsDocument Document => _document;

        internal Color ThemeAccent => _themeAccent;

        internal bool Hovering => _hovering;

        /// <summary>显示这个窗口时会不会抢焦点（<c>ShowWithoutActivation</c> 的只读镜像）。</summary>
        internal bool StealsFocusOnShow => !ShowWithoutActivation;
        private TimeSpan _position;
        private string? _title;

        private string _currentText = IdleTitle;
        private string _nextText = IdleHint;
        private bool _hasNext = true;

        private float _currentHeight;
        private float _nextHeight;

        private Font? _mainFont;
        private int _mainFontSize = -1;
        private Font? _nextFont;
        private int _nextFontSize = -1;

        /// <summary>歌词字体族；null 表示用内置默认字体。</summary>
        private string? _fontFamily;

        private int _fontSize = DesktopLyricsDefaults.FontSize;
        private int _opacityPercent = DesktopLyricsDefaults.Opacity;
        private bool _locked;
        private bool _showNextLine = true;

        /// <summary>第二行优先显示译文（双语歌词），而不是下一句。</summary>
        private bool _secondLineIsTranslation = true;
        private DesktopLyricsColor _colorChoice = DesktopLyricsColor.Theme;
        private Color _themeAccent = Color.FromArgb(0x4C, 0xC2, 0xFF);

        /// <summary>正在显示的那张 32 位 ARGB 位图（透明背景就靠它）。</summary>
        private Bitmap? _surface;

        /// <summary>量文字用的 Graphics：必须有，而且 DPI 要和渲染时一致。</summary>
        private Bitmap? _probe;
        private Graphics? _measure;

        private bool _dragging;
        private Point _dragOrigin;
        private Point _dragWindowOrigin;

        /// <summary>
        /// 用户拖动过之后为 true：内容引起的尺寸变化以水平中心为锚（见 <see cref="ResizeKeepingCenter"/>）。
        /// 没拖过时锚左上角——那时位置来自设置，不该被内容变化挪走。
        /// </summary>
        private bool _anchoredByContent;

        /// <summary>鼠标是不是正停在歌词上（停住时给一块背景，否则用户看不出该抓哪儿）。</summary>
        private bool _hovering;

        /// <summary>盯着"鼠标还在不在歌词上"的计时器（离开时把背景收掉）。</summary>
        private readonly Timer _hoverTimer;

        /// <summary>显示之前的前台窗口；一旦自己被激活就把前台还给它。</summary>
        private IntPtr _focusReturnTarget;

        public DesktopLyricsWindow()
        {
            // 建窗<b>之前</b>的前台窗口：之后这个窗口任何时候被激活，都要把前台还给它。
            // 必须在这里取——等到 Show() 的时候已经晚了，实测光是把句柄建出来
            // （Screen.FromControl 之类）就有可能把前台切到它身上。
            _focusReturnTarget = GetForegroundWindow();

            // 尺寸完全由 ApplyContent 算出来，这里不能让 WinForms 的自动缩放再插一脚。
            AutoScaleMode = AutoScaleMode.None;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            Text = "桌面歌词";
            AccessibleName = "桌面歌词";
            AccessibleDescription = "显示当前播放曲目的歌词，可以拖动与锁定";

            // 抓着字就能拖（背景是透明的，没有别的地方可抓）
            Cursor = Cursors.SizeAll;

            // 内容全部来自位图，WinForms 自己那一套绘制一律关掉，
            // 否则它会往一个"根本没有背景"的窗口上刷底色。
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, false);

            // 跨屏拖动时 padding 的缩放要跟着变
            DpiChanged += (s, e) => ApplyContent();

            _hoverTimer = new Timer { Interval = HoverCheckInterval };
            _hoverTimer.Tick += OnHoverTimerTick;

            // 把 .ttf / .otf 直接拖到歌词上就能换字体（比"菜单 → 导入 → 文件对话框"省事）
            AllowDrop = true;
            DragEnter += OnFontDragEnter;
            DragOver += OnFontDragEnter;
            DragDrop += OnFontDragDrop;
        }

        // ---- 对外属性（设置只有一份来源，这里只负责落地）-----------------------

        /// <summary>主句字号（磅）。</summary>
        public int FontSize
        {
            get => _fontSize;
            set
            {
                var clamped = Math.Clamp(value, DesktopLyricsDefaults.MinimumFontSize, DesktopLyricsDefaults.MaximumFontSize);
                if (_fontSize == clamped) return;

                _fontSize = clamped;
                ApplyContent();
            }
        }

        /// <summary>
        /// 文字浓度（百分比）。
        /// <para>
        /// 背景已经是透明的了，这一项调的是<b>文字本身</b>的浓度：
        /// 它是整窗统一的 alpha，叠在字的逐像素 alpha 之上。
        /// </para>
        /// </summary>
        public int OpacityPercent
        {
            get => _opacityPercent;
            set
            {
                var clamped = Math.Clamp(value, DesktopLyricsDefaults.MinimumOpacity, DesktopLyricsDefaults.MaximumOpacity);
                if (_opacityPercent == clamped) return;

                _opacityPercent = clamped;
                Push();
            }
        }

        /// <summary>锁定：整个窗口对鼠标透明，点击落到下面的程序上。</summary>
        public bool Locked
        {
            get => _locked;
            set
            {
                if (_locked == value) return;

                _locked = value;
                ApplyMouseTransparency();

                // 锁上之后收不到任何鼠标消息，那块背景就该收掉
                if (_locked) EndHover();

                if (_locked && _dragging) _dragging = false;

                // 控制条只在不锁定时出现：锁定要把那一行收掉、窗口跟着变矮
                ClearHoveredControl();
                ApplyContent();
            }
        }

        /// <summary>是否显示下一句。</summary>
        public bool ShowNextLine
        {
            get => _showNextLine;
            set
            {
                if (_showNextLine == value) return;

                _showNextLine = value;
                ApplyContent();
            }
        }

        /// <summary>
        /// 第二行在有翻译时显示翻译（双语歌词：同一时间戳的原文 + 译文），而不是下一句。
        /// </summary>
        public bool SecondLineIsTranslation
        {
            get => _secondLineIsTranslation;
            set
            {
                if (_secondLineIsTranslation == value) return;

                _secondLineIsTranslation = value;
                ApplyContent();
            }
        }

        /// <summary>
        /// 歌词字体族；空表示用内置默认字体。
        /// <para>字体族不可用（没装、名字被手改坏）时由 <see cref="UiFonts.Resolve"/> 退回默认，不抛异常。</para>
        /// </summary>
        public string? FontFamilyName
        {
            get => _fontFamily;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (string.Equals(_fontFamily, normalized, StringComparison.Ordinal)) return;

                _fontFamily = normalized;

                // 两个字号缓存都是按"字体族 + 字号"算出来的，换族必须让它们重建
                DisposeFonts();
                ApplyContent();
            }
        }

        /// <summary>文字配色。</summary>
        public DesktopLyricsColor ColorChoice
        {
            get => _colorChoice;
            set
            {
                if (_colorChoice == value) return;

                _colorChoice = value;
                Render();
            }
        }

        /// <summary>当前正在显示的这一句（只读视图，冒烟测试据此断言换句）。</summary>
        public string CurrentText => _currentText;

        /// <summary>当前实际用的文字颜色（外观对话框拿它画预览）。</summary>
        internal Color TextColor => MainColor;

        /// <summary>当前显示的下一句；没有下一句时为空串。</summary>
        public string NextText => _hasNext ? _nextText : string.Empty;

        /// <summary>正在显示的那张位图（冒烟测试靠它验"背景是不是真的透明、字有没有画上去"）。</summary>
        internal Bitmap? RenderedSurface => _surface;

        /// <summary>换一套主题色（只有配色选"跟随主题"时才看得见）。</summary>
        public void SetPalette(ThemePalette palette)
        {
            _themeAccent = palette.Accent;
            Render();
        }

        /// <summary>换曲目：曲名用于在没有歌词时兜底显示。</summary>
        public void SetTrack(string? title, LyricsDocument? lyrics)
        {
            _title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

            // 纯文本歌词没有时间轴，在桌面歌词这种"一次只看一句"的窗口里没法定位，
            // 所以当作"没有歌词"处理，退回显示曲名。
            _document = lyrics is { IsSynchronized: true } ? lyrics : LyricsDocument.Empty;
            _position = TimeSpan.Zero;

            ApplyContent();
        }

        /// <summary>播放位置推进；只有跨到新的一句时才重新排版。</summary>
        public void UpdatePosition(TimeSpan position)
        {
            _position = position;

            if (!_document.IsSynchronized) return;

            // 先算一遍文字，文字没变就不用重新度量（每 200ms 会调到这里一次）。
            var before = _currentText + "\u0000" + (_hasNext ? _nextText : string.Empty);
            var after = BuildTexts();

            if (before == after) return;

            ApplyContent();
        }
    }
}
