using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 主窗口：经典 WinForms 布局（菜单 + 工具栏 + 视频区 + 播放列表 + 状态栏）。
    /// <para>播放控制集中在 <see cref="PlayerEngine"/>，播放列表模型在 <see cref="Playlist"/>，
    /// 本类负责把两者接到界面上。</para>
    /// <para>
    /// 这个类按职责拆在多个文件里（<c>MainForm.*.cs</c>）：启动与设置、播放控制、字幕与轨道、
    /// 文件与截图、窗口形态、界面计时、引擎事件、系统集成、快捷键、外壳（状态栏 / 帮助 / 退出）。
    /// 这份只管字段与构造。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        private const int SeekBarResolution = 1000;

        private const long SeekStepMilliseconds = 5000;

        private const int MaxConsecutiveErrors = 5;

        private const int DefaultPlaylistPanelWidth = 320;

        /// <summary>任务栏缩略图按钮的图标尺寸。取大一点，高 DPI 下缩放更清晰。</summary>
        private const int TaskbarIconSize = 32;

        /// <summary>找不到文件时的固定提示。用常量比较，避免每次顺延都重复标记、重复刷列表。</summary>
        private const string MissingFileMessage = "文件不存在或无法访问";

        private readonly string[] _startupArgs;

        /// <summary>上次退出时的播放列表（原样恢复出来的那份，交给后台核对可用性）。</summary>
        private IReadOnlyList<string> _restoredSessionFiles = Array.Empty<string>();

        /// <summary>
        /// 当前这套设置。
        /// <para>
        /// <b>不是 readonly</b>：载入设置方案时要整份换成"当前设置 + 方案里选中的那几组"
        /// 合并出来的新对象（见 <c>MainForm.SettingsProfiles.cs</c>）。
        /// 全工程只有这一个地方持有设置对象——别的类都只读它的字段值，所以换掉它不会留下旧引用。
        /// </para>
        /// </summary>
        private AppSettings _settings;

        /// <summary>
        /// 只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// <para>
        /// 测试要摆各种设置场景（开 / 关在线获取、换服务地址、损坏的 JSON……），
        /// 以前靠反射挖 <c>_settings</c>。现在是同一个对象，只是不用再挖。
        /// </para>
        /// </summary>
        internal AppSettings Settings => _settings;

        /// <summary>
        /// 只读入口：引擎（给冒烟测试读"现在到底在播哪儿"，例如拖动进度条时不许被 seek）。
        /// </summary>
        internal PlayerEngine Engine => _engine;

        private readonly PlayerEngine _engine;

        private readonly Playlist _playlist = new Playlist();

        /// <summary>
        /// 只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// <para>测试要摆"列表里有哪些项"的场景，以前靠反射挖 <c>_playlist</c>；
        /// 模型本身（<see cref="Playlist"/>）没打算搬家，那就别再让测试挖字段。</para>
        /// </summary>
        internal Playlist Playlist => _playlist;

        /// <summary>
        /// 时长扫描器。
        /// <para>
        /// <b>不是 readonly</b>：它拿着一个 libvlc 内核，而换画面旋转会换内核
        /// （见 <see cref="OnEngineCoreRebuilt"/>），那时候要重建一个。
        /// </para>
        /// </summary>
        private DurationScanner _scanner;

        private readonly System.Windows.Forms.Timer _uiTimer;

        private readonly System.Windows.Forms.Timer _skipTimer;

        private readonly Random _random = new Random();

        private readonly Font _playingItemFont;

        private TaskbarThumbnailButtons? _taskbarButtons;

        /// <summary>
        /// 真正注册成功的全局媒体键 id。
        /// <para>
        /// 记的是<b>具体哪几个</b>而不是个数：媒体键是独占资源，缺的那几个要说得出名字
        /// （见 <see cref="Ui.MediaKeys.DescribeMissing"/>）。
        /// </para>
        /// </summary>
        private IReadOnlyList<int> _registeredMediaKeys = Array.Empty<int>();

        /// <summary>媒体键没注册全时的说明（启动时并进 <c>ReportLoadWarnings</c> 一起显示）。</summary>
        private string? _mediaKeyWarning;

        /// <summary>只读入口：媒体键的提示与结果（给冒烟测试用）。</summary>
        internal string? MediaKeyWarning => _mediaKeyWarning;

        internal IReadOnlyList<int> RegisteredMediaKeys => _registeredMediaKeys;

        // ---- 帮助菜单里与诊断有关的三项 --------------------------------------
        private ToolStripMenuItem? _menuViewLog;

        private ToolStripMenuItem? _menuOpenLogFile;

        private ToolStripMenuItem? _menuLogEnabled;

        // ---- 给冒烟测试的只读入口（主工程里有 InternalsVisibleTo("SmokeTest")）----
        // 测试以前靠反射按名字挖这些成员，一改名断言就静默空转（真踩过）。
        // 换成有名字的入口之后，改名由编译器管。
        internal ToolStripMenuItem? MenuViewLog => _menuViewLog;

        internal ToolStripMenuItem? MenuOpenLogFile => _menuOpenLogFile;

        internal ToolStripMenuItem? MenuLogEnabled => _menuLogEnabled;

        /// <summary>状态栏那一行字（测试报失败时连同它一起说出来）。</summary>
        internal string StatusText => lblStatus.Text ?? string.Empty;

        /// <summary>
        /// 界面计时器还在不在跑（给冒烟测试用）。
        /// <para>
        /// 它每 200 ms 读一次引擎、每 20 秒把这份实例的历史落一次盘；
        /// <b>只 Dispose 没 Close</b> 的表单要是没把它停掉，就会变成一个一直写旧数据的僵尸
        /// （见 <c>ShutdownForm</c> 的说明）。
        /// </para>
        /// </summary>
        internal bool UiTimerRunning => _uiTimer.Enabled;

        /// <summary>
        /// 系统媒体控件（音量弹窗 / 锁屏里的「正在播放」）。
        /// <para>拿不到时为 <c>null</c>（老系统 / 被禁）——那是安静降级，界面上什么都不说。</para>
        /// </summary>
        internal SmtcSession? SystemMediaControls => _smtc;

        /// <summary>桌面歌词解锁热键（Ctrl+Alt+D）是否注册上了。</summary>
        private bool _desktopLyricsHotKeyRegistered;

        private FullscreenState? _fullscreenState;

        private bool _isFullscreen;

        private bool _userSeeking;

        private bool _suppressSeekEvent;

        private bool _suspendUiEvents;

        private bool _disposed;

        /// <summary>收尾是否已经跑过（关窗与 Dispose 都会来，只能收一次）。</summary>
        private bool _shutdown;

        /// <summary>系统媒体控件会话；拿不到时为 <c>null</c>。</summary>
        private SmtcSession? _smtc;

        /// <summary>界面计时器的计数：每 5 拍（1 秒）把播放进度推给系统媒体控件一次。</summary>
        private int _smtcTick;

        private int _consecutiveErrors;

        public MainForm(string[]? startupArgs, SingleInstance? singleInstance = null)
        {
            InitializeComponent();

            _startupArgs = startupArgs ?? Array.Empty<string>();
            _settings = AppSettings.Load();

            // 「启动时恢复上次播放列表」默认关：关掉时这一次启动算一次全新会话——
            // 列表 / 当前项 / "当前歌单"关联都在这里清成空（清法见 AppSettings.ForgetSession）。
            // 放在这里是因为后面所有代码（歌单关联、RestoreSession、标题）都直接读这几项，
            // 清一次就都对了，不必在每个读取点各判一次。
            if (!_settings.RestoreLastPlaylist) _settings.ForgetSession();

            AttachSingleInstance(singleInstance);

            _engine = new PlayerEngine(this);
            videoView.MediaPlayer = _engine.Player;

            // 换画面旋转时引擎会换内核（libvlc 3 的 transform 只能在实例级设）：
            // 窗口与时长扫描都要重新接上，播放器状态也要重新铺一遍。
            _engine.CoreRebuilt += (s, e) => OnEngineCoreRebuilt();

            _scanner = CreateScanner();

            _uiTimer = new System.Windows.Forms.Timer { Interval = 200 };
            _uiTimer.Tick += OnUiTimerTick;

            // 播放失败时延后一点再跳到下一个，避免在 VLC 回调里立刻切媒体。
            _skipTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _skipTimer.Tick += OnSkipTimerTick;

            _playingItemFont = new Font(listViewPlaylist.Font, FontStyle.Bold);

            ConfigureToolStrips();
            ConfigureMetadataMenus();
            ConfigureSettingsProfileMenu();

            // 主题要在接线之前应用：它同时决定配色与工具栏图标（深色主题需要浅色图标）。
            ApplyTheme(_settings.Theme);
            _themeReady = true;

            WireEvents();
            ApplySettings();
            InitializeTray();

            // 恢复会话时列表会被填上/重排，那不是"用户刚改了歌单"，
            // 所以这一段不标记「未保存」（见 MarkPlaylistModified）。
            // 命令行/拖进来的文件发生在这一段之外，正常标记。
            _playlists.SuppressModified = true;
            try
            {
                RestoreSession();
            }
            finally
            {
                _playlists.SuppressModified = false;
            }

            HandleStartupFiles();

            UpdateTransportState();
            UpdateWindowTitle();

            // 空着启动时把那句"歌单在哪儿"说出来：默认就是这个状态，
            // 不说的话用户看着空列表只会以为列表丢了。
            SetStatus(_settings.RestoreLastPlaylist
                ? "就绪"
                : "就绪（本次是空列表，可在「文件 → 播放列表」里载入歌单）");

            ReportLoadWarnings();
            _uiTimer.Start();
        }
    }
}
