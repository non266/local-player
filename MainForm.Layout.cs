using System;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 播放列表面板的显示与隐藏、窗口置顶。
    /// <para>全屏进出与形态切换已经搬进 <see cref="Ui.WindowSurfaceController"/>；
    /// 这里只剩这两项与布局直接相关的开关。</para>
    /// </summary>
    public partial class MainForm : Form
    {
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

            // 音频窗口 / 迷你模式里没有播放列表这一栏（菜单项是灰的，这里只是兜底）：
            // 只记设置，不动布局——否则一按就把它从卡片里挤出来
            if (IsAudioWindow || IsMiniWindow) return;

            if (IsFullscreen) return;

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
    }
}
