using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 全屏：进 / 出时的外壳快照与还原，以及全屏底部那条独立进度条的生命周期。
    /// <para>
    /// 这里管两件以前分在两个文件里的事：<c>MainForm.Layout.cs</c> 的进出全屏
    /// （菜单 / 工具栏 / 状态栏 / 传输条全收、两栏折叠、窗口铺满屏幕、置顶），
    /// 和 <c>MainForm.Fullscreen.cs</c> 的进度条（出现 / 更新 / 收起 / 释放）。
    /// 主窗口只留"翻一下"的入口与测试用的只读属性。
    /// </para>
    /// <para>
    /// 进度条为什么是独立窗口：画面是 libvlc 自己的子窗口，WinForms 控件盖不上去；
    /// 把传输条显示出来又会把视频区挤矮、libvlc 每次都要重排、画面跳一下。
    /// 所以它是一条<b>置顶、不激活</b>的小窗口，视频区尺寸永远不变。
    /// </para>
    /// </summary>
    internal sealed class FullscreenModule
    {
        private readonly IWindowSurfaceHost _host;

        private FullscreenState? _state;

        private bool _isFullscreen;

        private FullscreenProgressBar? _bar;

        /// <summary>上一拍光标在哪：只有真的动过才点亮进度条，静止时不能一直闪。</summary>
        private Point _lastPointer;

        public FullscreenModule(IWindowSurfaceHost host)
        {
            _host = host;
        }

        public bool IsFullscreen => _isFullscreen;

        /// <summary>全屏进度条（没进全屏 / 没媒体时为 <c>null</c>）。</summary>
        public FullscreenProgressBar? Bar => _bar;

        /// <summary>进入全屏。调用方保证当前不是音频 / 迷你形态（那两种没有"满屏画面"可言）。</summary>
        public void Enter()
        {
            if (_isFullscreen) return;

            var form = _host.Window;

            _state = new FullscreenState
            {
                BorderStyle = form.FormBorderStyle,
                WindowState = form.WindowState,
                Bounds = form.Bounds,
                TopMost = form.TopMost,
                MenuVisible = _host.MainMenuStrip.Visible,
                ToolVisible = _host.MainToolStrip.Visible,
                StatusVisible = _host.MainStatusStrip.Visible,
                TransportVisible = _host.TransportPanel.Visible,
                SplitterDistance = _host.MainSplit.SplitterDistance,
                VideoSplitterDistance = _host.VideoSplit.SplitterDistance
            };

            _isFullscreen = true;

            _host.MainMenuStrip.Visible = false;
            _host.MainToolStrip.Visible = false;
            _host.MainStatusStrip.Visible = false;
            _host.TransportPanel.Visible = false;
            _host.MainSplit.Panel2Collapsed = true;
            _host.VideoSplit.Panel2Collapsed = true;

            // 分离出去的窗口全屏时也要收起来，否则会浮在满屏画面上
            _host.SetFloatingWindowsVisible(false);

            form.WindowState = FormWindowState.Normal;
            form.FormBorderStyle = FormBorderStyle.None;
            form.Bounds = Screen.FromControl(form).Bounds;
            form.TopMost = true;

            // 全屏的主窗口也是置顶窗口，会把桌面歌词盖住（同为置顶时按激活顺序排）。
            // 显式再提一次，且不激活自己——否则视频刚播起来就被抢走焦点。
            _host.RaiseDesktopLyricsToTop();

            // 进全屏先把进度条亮一次，用户才知道"鼠标一动底下有东西"。
            ShowBar();

            _host.SetStatus("已进入全屏，按 Esc 或 F11 退出");
        }

        public void Exit()
        {
            if (!_isFullscreen) return;

            var form = _host.Window;
            var settings = _host.Settings;
            var state = _state;

            _isFullscreen = false;
            _state = null;

            // 进度条是独立窗口：退出全屏就整个放掉，别让它留一条置顶的条在桌面上。
            DisposeBar();

            form.WindowState = FormWindowState.Normal;

            if (state != null)
            {
                form.FormBorderStyle = state.BorderStyle;
                form.Bounds = state.Bounds;
            }
            else
            {
                form.FormBorderStyle = FormBorderStyle.Sizable;
            }

            // 恢复两栏折叠状态与两条分隔条，再把分离出去的窗口放回来
            _host.MainSplit.Panel2Collapsed = !settings.ShowPlaylist || settings.PlaylistFloating;
            _host.VideoSplit.Panel2Collapsed = !settings.ShowSidebar || settings.SidebarFloating;

            if (state != null)
            {
                try
                {
                    if (!_host.MainSplit.Panel2Collapsed)
                        _host.MainSplit.SplitterDistance = state.SplitterDistance;

                    if (!_host.VideoSplit.Panel2Collapsed)
                        _host.VideoSplit.SplitterDistance = state.VideoSplitterDistance;
                }
                catch (Exception ex)
                {
                    // 窗口尺寸变化后原距离可能非法，保留默认值即可。
                    AppLog.Swallowed("窗口尺寸变化后原距离可能非法，保留默认值即可。", ex);
                }
            }

            _host.MainMenuStrip.Visible = state?.MenuVisible ?? true;
            _host.MainToolStrip.Visible = state?.ToolVisible ?? true;
            _host.MainStatusStrip.Visible = state?.StatusVisible ?? true;
            _host.TransportPanel.Visible = state?.TransportVisible ?? true;

            // 退出全屏之后窗口可能不再是置顶（原来就是置顶的才还原成置顶）
            if (state != null) form.TopMost = state.TopMost;

            if (state != null && state.WindowState == FormWindowState.Maximized)
                form.WindowState = FormWindowState.Maximized;

            _host.SetFloatingWindowsVisible(true);
            _host.ApplyPanelLayout();
            _host.SyncPlaylistMenuChecked();

            _host.SetStatus("已退出全屏");
        }

        /// <summary>30 ms 轮询里的一拍：光标真的动过才点亮（收起由进度条自己的空闲计时器负责）。</summary>
        public void PollPointer(Point screenPoint)
        {
            if (!_isFullscreen || !_host.HasMedia) return;
            if (Form.ActiveForm != _host.Window) return;

            if (screenPoint == _lastPointer) return;

            _lastPointer = screenPoint;
            NotifyPointer(screenPoint);
        }

        /// <summary>光标动了一下：在屏幕里就亮出来，跑到屏幕外就收起（测试直接调这个）。</summary>
        public void NotifyPointer(Point screenPoint)
        {
            if (!_isFullscreen || !_host.HasMedia) return;

            var screen = Screen.FromControl(_host.Window).Bounds;

            if (!screen.Contains(screenPoint))
            {
                HideBar();
                return;
            }

            ShowBar();
        }

        /// <summary>界面计时器每一拍把同一次算出来的时间 / 总时长推给进度条。</summary>
        public void UpdateProgress(long timeMilliseconds, long lengthMilliseconds)
        {
            if (_bar == null || !_bar.Visible) return;

            _bar.UpdateProgress(timeMilliseconds, lengthMilliseconds);
        }

        /// <summary>换主题时进度条跟着换色。</summary>
        public void ApplyPalette(ThemePalette palette) => _bar?.ApplyPalette(palette);

        /// <summary>主窗口不再活动（Alt+Tab、点了别的程序）就收起：置顶窗口不能盖着别人家。</summary>
        public void OnDeactivate() => HideBar();

        public void Dispose()
        {
            DisposeBar();
        }

        /// <summary>亮出进度条（没有媒体或不在全屏时什么都不做）。</summary>
        public void ShowBar()
        {
            if (!_isFullscreen || !_host.HasMedia) return;

            var form = _host.Window;

            if (_bar == null)
            {
                _bar = new FullscreenProgressBar { Owner = form };
                _bar.SeekRequested += OnSeekRequested;
                _bar.ApplyPalette(_host.Palette);
            }

            _bar.FitTo(Screen.FromControl(form).Bounds, form.DeviceDpi);
            _bar.UpdateProgress(_host.PositionMilliseconds, _host.LengthMilliseconds);
            _bar.ShowBar();
        }

        public void HideBar() => _bar?.HideBar();

        private void OnSeekRequested(long milliseconds)
        {
            if (!_host.HasMedia) return;

            _host.SeekTo(milliseconds);
        }

        private void DisposeBar()
        {
            HideBar();

            if (_bar == null) return;

            _bar.SeekRequested -= OnSeekRequested;
            _bar.Dispose();
            _bar = null;
        }

        /// <summary>进入全屏前的窗口状态快照。</summary>
        private sealed class FullscreenState
        {
            public FormBorderStyle BorderStyle { get; set; }

            public FormWindowState WindowState { get; set; }

            public Rectangle Bounds { get; set; }

            public bool TopMost { get; set; }

            // 这四个在 Exit 里用来还原外壳的显示状态。
            public bool MenuVisible { get; set; }

            public bool ToolVisible { get; set; }

            public bool StatusVisible { get; set; }

            public bool TransportVisible { get; set; }

            // 两栏的折叠状态不在这里记：退出全屏时按 Settings.ShowPlaylist / ShowSidebar
            // 和"是否已分离"重新算，比直接回放快照更准。
            public int SplitterDistance { get; set; }

            public int VideoSplitterDistance { get; set; }
        }
    }
}
