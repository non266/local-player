using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace 播放器
{
    /// <summary>
    /// 画面右键菜单（1.8.0）：字幕轨道 / 音轨 / 播放控制 / 全屏。
    /// <para>
    /// 为什么要在计时器里问系统"键按着没有"：视频区域整个交给 libvlc 的子窗口了，鼠标消息
    /// 根本到不了 WinForms（实测：画面中心那个窗口句柄不是 VideoView 的，VideoView 的
    /// MouseUp / DoubleClick 一次都不触发）。所以右键只能自己看键的状态，在"按下 → 抬起"
    /// 时弹菜单——全屏时那条菜单栏是收起来的，右键是唯一能选字幕 / 音轨的入口。
    /// </para>
    /// <para>
    /// 顺带把"双击画面进 / 退全屏"也接在这上面：原来那句
    /// <c>videoView.DoubleClick</c> 从来没触发过（同样的原因），属于死代码。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        private const int VkLeftButton = 0x01;
        private const int VkRightButton = 0x02;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr window);

        /// <summary>系统的双击间隔（控制面板里能调，默认 500 ms）。</summary>
        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        private ContextMenuStrip? _videoMenu;
        private ToolStripMenuItem? _videoPlayPause;
        private ToolStripMenuItem? _videoStop;
        private ToolStripMenuItem? _videoSubtitle;
        private ToolStripMenuItem? _videoAudio;
        private ToolStripMenuItem? _videoFullscreen;

        /// <summary>30 ms 一次：200 ms 的界面计时器会漏掉又快又短的点击。</summary>
        private System.Windows.Forms.Timer? _videoPointerTimer;

        private bool _rightButtonWasDown;
        private bool _rightButtonArmed;

        private bool _leftButtonWasDown;
        private long _lastLeftClickTicks;

        /// <summary>测试用：画面右键菜单。</summary>
        internal ContextMenuStrip? VideoMenu => _videoMenu;

        /// <summary>测试用：右键菜单里的「字幕轨道」。</summary>
        internal ToolStripMenuItem? VideoSubtitleMenu => _videoSubtitle;

        /// <summary>测试用：右键菜单里的「音轨」。</summary>
        internal ToolStripMenuItem? VideoAudioMenu => _videoAudio;

        private void ConfigureVideoMenu()
        {
            _videoMenu = new ContextMenuStrip { ShowImageMargin = false };

            _videoPlayPause = new ToolStripMenuItem("播放");
            _videoPlayPause.Click += (s, e) => TogglePlayPause();

            _videoStop = new ToolStripMenuItem("停止");
            _videoStop.Click += (s, e) => StopPlayback();

            _videoSubtitle = new ToolStripMenuItem("字幕轨道");
            _videoSubtitle.DropDownOpening += (s, e) => FillSubtitleTrackMenu(_videoSubtitle.DropDownItems);

            _videoAudio = new ToolStripMenuItem("音轨");
            _videoAudio.DropDownOpening += (s, e) => FillAudioTrackMenu(_videoAudio.DropDownItems);

            _videoFullscreen = new ToolStripMenuItem("全屏");
            _videoFullscreen.Click += (s, e) => ToggleFullscreen();

            _videoMenu.Items.AddRange(new ToolStripItem[]
            {
                _videoPlayPause,
                _videoStop,
                new ToolStripSeparator(),
                _videoSubtitle,
                _videoAudio,
                new ToolStripSeparator(),
                _videoFullscreen
            });

            _videoMenu.Opening += (s, e) => RefreshVideoMenu();

            _videoPointerTimer = new System.Windows.Forms.Timer { Interval = 30 };
            _videoPointerTimer.Tick += (s, e) => PollVideoPointer();
            _videoPointerTimer.Start();
        }

        /// <summary>
        /// 每 30 ms 看一次左右键：右键"按下 → 抬起"弹菜单，左键两下快连点切换全屏。
        /// <para>抬起才弹右键菜单：按下就弹的话，紧接着的那次"抬起"会立刻把菜单关掉。</para>
        /// </summary>
        private void PollVideoPointer()
        {
            PollRightClick();
            PollLeftDoubleClick();
        }

        private void PollRightClick()
        {
            if (_videoMenu == null) return;

            var down = IsKeyDown(VkRightButton);

            if (down && !_rightButtonWasDown)
            {
                // 按下这一刻窗口可能还没被这次点击激活，所以这里只看位置；
                // "是不是活动窗口"留到抬起、真正要弹菜单的时候再判。
                _rightButtonArmed = CursorOverVideo();
            }
            else if (!down && _rightButtonWasDown && _rightButtonArmed)
            {
                _rightButtonArmed = false;

                if (CursorOverVideo() && CanShowVideoMenu())
                    ShowVideoMenu(Cursor.Position);
            }
            else if (!down)
            {
                _rightButtonArmed = false;
            }

            _rightButtonWasDown = down;
        }

        private void PollLeftDoubleClick()
        {
            var down = IsKeyDown(VkLeftButton);

            if (!down && _leftButtonWasDown)
            {
                // 没有媒体时画面那块是占位标签，它自己的双击是"打开文件"——
                // 这里不能顺手把窗口切进全屏。窗口不在前台时也不动手：轮询是全局的，
                // 在别的程序里连点两下不该把播放器切进全屏。
                if (_engine.HasMedia && Form.ActiveForm == this && CursorOverVideo())
                {
                    var now = Environment.TickCount64;

                    if (_lastLeftClickTicks != 0 && now - _lastLeftClickTicks <= GetDoubleClickTime())
                    {
                        _lastLeftClickTicks = 0;
                        ToggleFullscreen();
                    }
                    else
                    {
                        _lastLeftClickTicks = now;
                    }
                }
                else
                {
                    _lastLeftClickTicks = 0;
                }
            }

            _leftButtonWasDown = down;
        }

        private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        /// <summary>
        /// 光标是不是**真的**在画面那块上：先看矩形，再问系统"这个点上是哪个窗口"，
        /// 顺着父窗口找到最外层——必须是我们那个 <see cref="VideoView"/> 或它的子窗口（libvlc 的
        /// 画面窗口）。只判矩形的话，浮在画面上的桌面歌词 / 侧栏悬浮窗、甚至别的程序窗口，
        /// 都会被当成"在画面上双击"。
        /// </summary>
        private bool CursorOverVideo()
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return false;

            return IsScreenPointOnVideo(Cursor.Position);
        }

        /// <summary>测试用：这个屏幕坐标是不是落在画面（视频窗口）上。</summary>
        internal bool IsScreenPointOnVideo(Point position)
        {
            if (!videoView.RectangleToScreen(videoView.ClientRectangle).Contains(position)) return false;

            var window = WindowFromPoint(position);

            while (window != IntPtr.Zero)
            {
                if (window == videoView.Handle) return true;

                window = GetParent(window);
            }

            return false;
        }

        /// <summary>
        /// 只在自己的窗口是活动窗口、而且真的有媒体时动手：轮询是全局的，不然在别的程序里右键
        /// 也会冒出这个菜单、双击也会把播放器切进全屏；空窗口时右键也不该弹这个（没有轨道可选）。
        /// </summary>
        private bool CanShowVideoMenu() =>
            _engine.HasMedia && Form.ActiveForm == this && (_videoMenu == null || !_videoMenu.Visible);

        /// <summary>在指定屏幕坐标弹出画面菜单（测试直接调这个，避开合成鼠标）。</summary>
        internal void ShowVideoMenu(Point screenPoint)
        {
            if (_videoMenu == null) return;

            RefreshVideoMenu();
            _videoMenu.Show(screenPoint);
        }

        /// <summary>刷新菜单上的文字与两个子菜单（轨道清单会随播放状态变）。</summary>
        internal void RefreshVideoMenu()
        {
            if (_videoMenu == null) return;

            var hasMedia = _engine.HasMedia;

            if (_videoPlayPause != null)
            {
                _videoPlayPause.Text = hasMedia && _engine.IsPlaying ? "暂停" : "播放";
                _videoPlayPause.Enabled = hasMedia;
            }

            if (_videoStop != null) _videoStop.Enabled = hasMedia;
            if (_videoFullscreen != null) _videoFullscreen.Text = _isFullscreen ? "退出全屏" : "全屏";

            FillSubtitleTrackMenu(_videoSubtitle!.DropDownItems);
            FillAudioTrackMenu(_videoAudio!.DropDownItems);
        }

        /// <summary>关窗 / 只 Dispose 时都要停掉轮询并放掉菜单（和界面计时器一个规矩）。</summary>
        private void DisposeVideoMenu()
        {
            if (_videoPointerTimer != null)
            {
                _videoPointerTimer.Stop();
                _videoPointerTimer.Dispose();
                _videoPointerTimer = null;
            }

            _videoMenu?.Dispose();
            _videoMenu = null;
        }
    }
}
