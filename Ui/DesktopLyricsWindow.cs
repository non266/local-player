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
    internal sealed class DesktopLyricsWindow : Form
    {
        /// <summary>下一句相对主句的字号比例。</summary>
        private const float NextLineRatio = 0.62f;

        /// <summary>取景框最多占工作区宽度的多少。</summary>
        private const double MaximumWidthRatio = 0.9;

        /// <summary>当前句还没开始时（前奏）显示的记号，和侧栏歌词页保持一致。</summary>
        private const string IntroMark = "♪";

        private const string IdleTitle = "桌面歌词";
        private const string IdleHint = "（未在播放）";

        // ---- 原生常量 ---------------------------------------------------------

        private const int GwlExStyle = -20;
        private const int WsExLayered = 0x00080000;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        private const int WmNcHitTest = 0x0084;
        private const int HtTransparent = -1;

        /// <summary>窗口被激活时（<c>WM_ACTIVATE</c>）。</summary>
        private const int WmActivate = 0x0006;

        /// <summary><c>WM_ACTIVATE</c> 的 wParam：不是点击引起的激活（要还前台）。</summary>
        private const int WaActive = 1;

        /// <summary>鼠标在窗口里移动（只有落在"字"的像素上才会收到）。</summary>
        private const int WmMouseMove = 0x0200;

        /// <summary>检查"鼠标还在不在歌词上"的间隔（毫秒）。</summary>
        private const int HoverCheckInterval = 120;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;

        private static readonly IntPtr HwndTopMost = new IntPtr(-1);

        private const int UlwAlpha = 0x00000002;
        private const byte AcSrcOver = 0x00;
        private const byte AcSrcAlpha = 0x01;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLong64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLong64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hWnd,
            IntPtr hdcDst,
            ref Point pptDst,
            ref Size psize,
            IntPtr hdcSrc,
            ref Point pptSrc,
            int crKey,
            ref BlendFunction pblend,
            int dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

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

        /// <summary>
        /// 用户往歌词窗口上拖了字体文件。
        /// <para>
        /// 窗口自己不认得怎么导入（那要写进数据目录、还要刷菜单和两处歌词），
        /// 所以只把路径抛给主窗体，由它走"和菜单导入完全相同"的那条路。
        /// </para>
        /// <para>⚠ 锁定时窗口是鼠标穿透的（<c>WS_EX_TRANSPARENT</c>），拖不进来——这是预期的。</para>
        /// </summary>
        public event Action<string[]>? FontFilesDropped;

        private static string[] FontFilesIn(IDataObject? data)
        {
            if (data == null || !data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();

            if (data.GetData(DataFormats.FileDrop) is not string[] paths) return Array.Empty<string>();

            return paths.Where(Core.FontLibrary.IsSupported).ToArray();
        }

        private void OnFontDragEnter(object? sender, DragEventArgs e) =>
            e.Effect = FontFilesIn(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnFontDragDrop(object? sender, DragEventArgs e)
        {
            var paths = FontFilesIn(e.Data);
            if (paths.Length == 0) return;

            FontFilesDropped?.Invoke(paths);
        }

        /// <summary>用 Show() 显示时不激活自己：全屏看视频时弹出来绝不能把焦点抢走。</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;

                // LAYERED 是逐像素透明（UpdateLayeredWindow）的前提；
                // TOOLWINDOW：不出现在 Alt+Tab 里（ShowInTaskbar=false 只管任务栏）；
                // NOACTIVATE：点了也不激活，不打断下面那个程序。
                parameters.ExStyle |= WsExLayered | WsExToolWindow | WsExNoActivate;

                return parameters;
            }
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

        /// <summary>
        /// 摆到保存过的位置。
        /// <para>
        /// 摆完一定要再钳一次：上次退出到现在，显示器可能被拔掉、分辨率或缩放可能变了，
        /// 而这是一个没有边框的窗口，跑到可视区外面就等于消失了。
        /// </para>
        /// </summary>
        public void MoveTo(Point location)
        {
            Location = location;
            ClampToWorkingArea();
        }

        /// <summary>
        /// 把窗口摆到所在屏幕的下方居中——第一次开启时的默认位置。
        /// <para>第一次开时窗口还没摆过，位置是 (0,0)，所以这里拿到的是主屏幕。</para>
        /// </summary>
        public void CenterOnScreen()
        {
            var area = Screen.FromControl(this).WorkingArea;

            Location = new Point(
                area.Left + (area.Width - Width) / 2,
                area.Bottom - Height - (int)Math.Round(area.Height * 0.1));

            ClampToWorkingArea();
        }

        /// <summary>
        /// 显示窗口，并保证不夺走前台。
        /// <para>
        /// 只靠 <c>WS_EX_NOACTIVATE</c> + <see cref="ShowWithoutActivation"/> <b>不够</b>：
        /// 本机实测这个窗口被显示出来的时候，前台窗口常常就变成了它，
        /// 而"全屏看视频时弹出歌词"最不能接受的就是把焦点抢走——
        /// 抢走之后全屏视频收不到键盘、游戏里的按键也会丢。
        /// </para>
        /// <para>
        /// 拦截放在 <see cref="WndProc"/> 里（收到激活消息就还给窗口构造时记下的那个窗口）。
        /// 不能靠时序：实测 <c>Show()</c> 返回之后激活还可能补上来，
        /// 在 <c>Show()</c> 后面顺着写一句 <c>SetForegroundWindow</c> 是拦不住的。
        /// </para>
        /// </summary>
        public void ShowWithoutTakingFocus()
        {
            Show();
            BringToTop();
            GiveBackForeground(force: false);

            // 激活可能比 Show() 晚一步生效（实测确实如此），所以等消息循环转一圈再核对一次。
            // 少了这一手，就只能在"Show() 返回那一瞬间"判断有没有被抢，晚了就漏掉。
            try
            {
                BeginInvoke(new Action(() => GiveBackForeground(force: false)));
            }
            catch (Exception ex)
            {
// 窗口正在销毁，忽略
                AppLog.Swallowed("窗口正在销毁，忽略", ex);
            }
        }

        /// <summary>如果前台被我们抢走了，就还给显示之前那个窗口。</summary>
        /// <param name="force">
        /// <c>true</c> 表示"我正在被激活，无条件还回去"：在 <c>WM_ACTIVATE</c> 处理中
        /// <see cref="GetForegroundWindow"/> 可能还没更新，靠它判断会漏。
        /// <c>false</c> 是兜底路径，只在确实被抢走时才还。
        /// </param>
        private void GiveBackForeground(bool force)
        {
            var target = _focusReturnTarget;

            if (target == IntPtr.Zero || target == Handle) return;
            if (!IsHandleCreated || IsDisposed) return;
            if (!force && GetForegroundWindow() != Handle) return;

            SetForegroundWindow(target);
        }

        /// <summary>
        /// 把它重新提到所有置顶窗口之上。
        /// <para>
        /// 主窗口进全屏时会自己变成置顶窗口，两个置顶窗口之间是按激活顺序排的，
        /// 后置顶的主窗口会盖在上面——桌面歌词这时就看不见了。
        /// 所以进全屏、以及主窗口切"窗口置顶"之后，都要显式再提一次。
        /// </para>
        /// <para><c>SWP_NOACTIVATE</c> 不能省：提层级的同时绝不能把焦点从视频上抢走。</para>
        /// </summary>
        public void BringToTop()
        {
            if (!IsHandleCreated) return;

            SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        // ---- 原生样式 ---------------------------------------------------------

        private static IntPtr ReadExtendedStyle(IntPtr handle) =>
            IntPtr.Size == 8
                ? GetWindowLong64(handle, GwlExStyle)
                : (IntPtr)GetWindowLong32(handle, GwlExStyle);

        private static void WriteExtendedStyle(IntPtr handle, IntPtr style)
        {
            if (IntPtr.Size == 8) SetWindowLong64(handle, GwlExStyle, style);
            else SetWindowLong32(handle, GwlExStyle, style.ToInt32());
        }

        /// <summary>
        /// 锁定状态落到窗口样式上。
        /// <para>
        /// 背景透明之后，<b>透明的地方本来就已经点得到了</b>（分层窗口按 alpha 做命中测试），
        /// 所以这里要管的是"连字也不让点"：锁定时把整个窗口对鼠标透明。
        /// </para>
        /// <para>
        /// 两条路一起走：
        /// <list type="number">
        ///   <item><c>WS_EX_TRANSPARENT</c>：系统在做命中测试时会跳过这个窗口，
        ///   点击直接落到下面的程序上（跨进程有效），这是主要机制。</item>
        ///   <item><c>WM_NCHITTEST</c> 回 <c>HTTRANSPARENT</c>：见 <see cref="WndProc"/>，
        ///   作为兜底——文档里这条路只对<b>同一个线程</b>的窗口继续往下传。</item>
        /// </list>
        /// </para>
        /// </summary>
        private void ApplyMouseTransparency()
        {
            if (!IsHandleCreated) return;

            var style = ReadExtendedStyle(Handle).ToInt64();

            style |= WsExLayered;

            if (_locked) style |= WsExTransparent;
            else style &= ~(long)WsExTransparent;

            WriteExtendedStyle(Handle, new IntPtr(style));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            ApplyMouseTransparency();
            ApplyContent();
        }

        protected override void WndProc(ref Message m)
        {
            // 锁定时连非客户区命中测试都直接让路。
            if (_locked && m.Msg == WmNcHitTest)
            {
                m.Result = (IntPtr)HtTransparent;
                return;
            }

            // 鼠标消息只会在"有像素"的地方送到这个窗口，所以它只能当个"立刻反应"的加速器，
            // 真正判断在不在歌词上的是 UpdateHover（计时器里按窗口矩形算）。
            if (m.Msg == WmMouseMove) UpdateHover();

            base.WndProc(ref m);

            // 有人把前台切到我们身上了：立刻还回去。
            // 这个窗口的意义是"只是显示"，任何时候都不该成为用户正在操作的那个窗口。
            // 有人把前台切到我们身上了：把"不是用户点出来的那种"还回去。
            // ⚠ 这里必须区分 wParam：
            //   WA_ACTIVE(1)      —— Show() 之后系统补上来的激活，要还回去（不然弹歌词会抢焦点）
            //   WA_CLICKACTIVE(2) —— 用户<b>点</b>出来的激活，绝不能还！
            // 实测：连点击引起的激活也一并还回去的话，那次左键"按下"会被整个取消，
            // 窗口从此点不动、拖不动（消息、抬起都到，唯独按下不来）。
            if (m.Msg == WmActivate && m.WParam.ToInt64() == WaActive)
                GiveBackForeground(force: true);
        }

        /// <summary>
        /// 鼠标进到歌词上了：给一块背景显示出来。
        /// <para>
        /// 背景是全透明的，用户平时只看到一串悬空的字，看不出哪里可以抓；
        /// 所以鼠标停上来要铺一块背景。
        /// </para>
        /// </summary>
        private void BeginHover()
        {
            if (!_hovering)
            {
                _hovering = true;
                Render();
            }
        }

        /// <summary>
        /// 按真实光标位置更新"鼠标在不在歌词上"。
        /// <para>
        /// ⚠ 这里<b>不能</b>等系统的鼠标消息：命中区域是逐像素的，
        /// 背景透明时光标只要落在字之间的空隙上，窗口根本收不到任何消息——
        /// 于是"把鼠标移到歌词上"这件事永远没人知道，只有正正好戳中某个笔画才有反应，
        /// 用户的感觉就是"移动不了"。所以由一个计时器按窗口矩形主动问光标在哪。
        /// </para>
        /// <para>
        /// 同理，判断"离开"也不能靠 <c>WM_MOUSELEAVE</c>：逐像素命中测试下，
        /// 光标一落到透明像素上系统就认为"已经离开窗口"，那块背景会刚出现就被收掉。
        /// </para>
        /// </summary>
        private void UpdateHover()
        {
            // 锁定的意思是"这个窗口在鼠标眼里不存在"，那就不该有任何提示
            if (_locked)
            {
                EndHover();
                return;
            }

            // 拖动过程中光标可能被拖到窗口外面，但这时背景要留着（用户正抓着它）
            if (_dragging) return;

            // 按"窗口矩形"判断而不是按像素：矩形之内都算还在歌词上，
            // 这样两块文字之间的空隙、以及字的描边之外那点边距，都算在里面。
            var inside = ClientRectangle.Contains(PointToClient(Cursor.Position));

            if (inside == _hovering) return;

            _hovering = inside;
            Render();
        }

        /// <summary>光标已经不在歌词上了：把背景收掉。</summary>
        private void EndHover()
        {
            if (!_hovering) return;

            _hovering = false;
            Render();
        }

        private void OnHoverTimerTick(object? sender, EventArgs e) => UpdateHover();

        /// <summary>窗口显示 / 隐藏时开关轮询：藏起来的时候没必要一直问光标在哪。</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (Visible)
            {
                if (!_hoverTimer.Enabled) _hoverTimer.Start();
                UpdateHover();
            }
            else
            {
                _hoverTimer.Stop();
                EndHover();
            }
        }

        /// <summary>内容全部来自位图，WinForms 的绘制一律不参与。</summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

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

        /// <summary>算出"当前句 / 下一句"两段文字，返回它们的拼装（用于判断有没有变化）。</summary>
        private string BuildTexts()
        {
            if (_document.IsSynchronized)
            {
                var index = _document.LineIndexAt(_position);

                _currentText = index < 0 ? IntroMark : Normalize(_document.Lines[index].Text);

                _nextText = string.Empty;
                _hasNext = _showNextLine && index + 1 < _document.Lines.Count;

                if (_hasNext) _nextText = Normalize(_document.Lines[index + 1].Text);
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

        /// <summary>重新排版：算出两行文字、量出需要的客户区大小、按需伸缩窗口，然后重画。</summary>
        private void ApplyContent()
        {
            BuildTexts();

            var maximum = MaximumContentWidth;

            var current = Measure(_currentText, MainFont, maximum);
            var next = _hasNext ? Measure(_nextText, NextFont, maximum) : SizeF.Empty;

            _currentHeight = current.Height;
            _nextHeight = next.Height;

            var contentWidth = Math.Max(current.Width, next.Width);
            var contentHeight = _currentHeight + (_hasNext ? LineGap + _nextHeight : 0);

            var width = Math.Max(MinimumBoxWidth, (int)Math.Ceiling(contentWidth + PaddingX * 2 + WrapSlack));
            var height = Math.Max(1, (int)Math.Ceiling(contentHeight + PaddingY * 2));

            if (ClientSize.Width != width || ClientSize.Height != height)
                ResizeKeepingCenter(new Size(width, height));

            Render();
        }

        /// <summary>
        /// 改宽度时保持水平中心不动。
        /// <para>歌词换一句就变宽变窄，如果锚在左边，窗口会左右乱跳；锚在中心看起来才是"在屏幕那个位置"。 </para>
        /// </summary>
        private void ResizeKeepingCenter(Size size)
        {
            var center = Left + Width / 2;

            ClientSize = size;
            Location = new Point(center - Width / 2, Top);

            ClampToWorkingArea();
        }

        /// <summary>把窗口整体挪回工作区内——边框都没了，拖出去就再也找不回来。</summary>
        private void ClampToWorkingArea()
        {
            var probe = new Rectangle(Location, Size);
            var area = Screen.FromRectangle(probe).WorkingArea;

            Location = new Point(
                Math.Max(Math.Min(Left, area.Right - Width), area.Left),
                Math.Max(Math.Min(Top, area.Bottom - Height), area.Top));
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

        // ---- 拖动 -------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (_locked || e.Button != MouseButtons.Left) return;

            _dragging = true;
            _dragOrigin = PointToScreen(e.Location);
            _dragWindowOrigin = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (!_dragging) return;

            // 用"窗口当初的位置 + 鼠标位移"来算，而不是累加每次的增量：
            // 累加会把每一次取整误差留下来，拖久了窗口会和光标脱节。
            var now = PointToScreen(e.Location);

            Location = new Point(
                _dragWindowOrigin.X + now.X - _dragOrigin.X,
                _dragWindowOrigin.Y + now.Y - _dragOrigin.Y);

            ClampToWorkingArea();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);

            // 拖出去再松手时，光标已经不在窗口里，别等下一个计时器节拍
            UpdateHover();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hoverTimer.Stop();
                _hoverTimer.Dispose();

                DisposeFonts();

                _surface?.Dispose();
                _surface = null;

                _measure?.Dispose();
                _measure = null;
                _probe?.Dispose();
                _probe = null;

                _format.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
