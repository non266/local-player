using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>窗口形态：全屏进出、播放列表面板的显示与隐藏、窗口置顶。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 全屏
        // =====================================================================
        private void ToggleFullscreen()
        {
            if (_isFullscreen)
                ExitFullscreen();
            else
                EnterFullscreen();
        }

        internal void EnterFullscreen()
        {
            if (_isFullscreen) return;

            _fullscreenState = new FullscreenState
            {
                BorderStyle = FormBorderStyle,
                WindowState = WindowState,
                Bounds = Bounds,
                TopMost = TopMost,
                MenuVisible = menuStripMain.Visible,
                ToolVisible = toolStripMain.Visible,
                StatusVisible = statusStripMain.Visible,
                TransportVisible = pnlTransport.Visible,
                SplitterDistance = splitMain.SplitterDistance,
                VideoSplitterDistance = splitVideo.SplitterDistance
            };

            _isFullscreen = true;

            menuStripMain.Visible = false;
            toolStripMain.Visible = false;
            statusStripMain.Visible = false;
            pnlTransport.Visible = false;
            splitMain.Panel2Collapsed = true;
            splitVideo.Panel2Collapsed = true;

            // 分离出去的窗口全屏时也要收起来，否则会浮在满屏画面上
            SetFloatingWindowsVisible(false);

            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            TopMost = true;

            // 全屏的主窗口也是置顶窗口，会把桌面歌词盖住（同为置顶时按激活顺序排）。
            // 显式再提一次，且不激活自己——否则视频刚播起来就被抢走焦点。
            _desktopLyrics?.BringToTop();

            SetStatus("已进入全屏，按 Esc 或 F11 退出");
        }

        internal void ExitFullscreen()
        {
            if (!_isFullscreen) return;

            _isFullscreen = false;
            var state = _fullscreenState;
            _fullscreenState = null;

            WindowState = FormWindowState.Normal;

            if (state != null)
            {
                FormBorderStyle = state.BorderStyle;
                Bounds = state.Bounds;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
            }

            // 恢复两栏折叠状态与两条分隔条，再把分离出去的窗口放回来
            splitMain.Panel2Collapsed = !_settings.ShowPlaylist || _settings.PlaylistFloating;
            splitVideo.Panel2Collapsed = !_settings.ShowSidebar || _settings.SidebarFloating;

            if (state != null)
            {
                try
                {
                    if (!splitMain.Panel2Collapsed) splitMain.SplitterDistance = state.SplitterDistance;
                    if (!splitVideo.Panel2Collapsed) splitVideo.SplitterDistance = state.VideoSplitterDistance;
                }
                catch (Exception ex)
                {
// 窗口尺寸变化后原距离可能非法，保留默认值即可。
                    AppLog.Swallowed("窗口尺寸变化后原距离可能非法，保留默认值即可。", ex);
                }
            }

            menuStripMain.Visible = state?.MenuVisible ?? true;
            toolStripMain.Visible = state?.ToolVisible ?? true;
            statusStripMain.Visible = state?.StatusVisible ?? true;
            pnlTransport.Visible = state?.TransportVisible ?? true;
            TopMost = state?.TopMost ?? false;

            if (state != null && state.WindowState == FormWindowState.Maximized)
                WindowState = FormWindowState.Maximized;

            SetFloatingWindowsVisible(true);
            ApplyPanelLayout();

            menuViewPlaylist.Checked = _settings.ShowPlaylist;
            SetStatus("已退出全屏");
        }

        private void OnShowPlaylistChanged(object? sender, EventArgs e)
        {
            if (_suspendUiEvents) return;
            ApplyPlaylistVisibility(menuViewPlaylist.Checked);
        }

        private void ApplyPlaylistVisibility(bool visible)
        {
            _settings.ShowPlaylist = visible;

            if (menuViewPlaylist.Checked != visible)
            {
                _suspendUiEvents = true;
                menuViewPlaylist.Checked = visible;
                _suspendUiEvents = false;
            }

            // 播放列表被分离出去时，"显示播放列表"控制的是那个独立窗口
            if (_settings.PlaylistFloating)
            {
                if (_playlistWindow != null)
                {
                    if (visible) _playlistWindow.Show();
                    else _playlistWindow.Hide();
                }

                return;
            }

            if (_isFullscreen) return;

            splitMain.Panel2Collapsed = !visible;

            if (!visible) return;

            ApplyPanelLayout();
            AdjustPlaylistColumns();
        }

        private void OnTopMostChanged(object? sender, EventArgs e)
        {
            if (_suspendUiEvents) return;

            TopMost = menuViewTopMost.Checked;
            _settings.AlwaysOnTop = menuViewTopMost.Checked;

            // 主窗口变成置顶之后会盖在桌面歌词上面，同样要把它提回来。
            if (TopMost) _desktopLyrics?.BringToTop();
        }

        /// <summary>进入全屏前的窗口状态快照。</summary>
        private sealed class FullscreenState
        {
            public FormBorderStyle BorderStyle { get; set; }

            public FormWindowState WindowState { get; set; }

            public Rectangle Bounds { get; set; }

            public bool TopMost { get; set; }

            // 这四个在 ExitFullscreen 里用来还原外壳的显示状态。
            public bool MenuVisible { get; set; }

            public bool ToolVisible { get; set; }

            public bool StatusVisible { get; set; }

            public bool TransportVisible { get; set; }

            // 两栏的折叠状态不在这里记：退出全屏时按 _settings.ShowPlaylist / ShowSidebar
            // 和"是否已分离"重新算（见 ExitFullscreen），比直接回放快照更准。
            public int SplitterDistance { get; set; }

            public int VideoSplitterDistance { get; set; }
        }
    }
}
