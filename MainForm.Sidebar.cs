using System;
using System.Windows.Forms;
using 播放器.Core;

// 侧栏的装配与布局：三入口菜单（视图 / 右键 / 工具栏）、显示与隐藏、宽度与分隔条、分离之后的表现。

namespace 播放器
{
    /// <summary>
    /// 侧栏的装配与布局：显示 / 隐藏、宽度与分隔条、以及挂在「视图」菜单上的那几项。
    /// <para>
    /// 挂在「播放」菜单下的三组功能（均衡器、音频输出设备、章节跳转）分别在
    /// <c>MainForm.Equalizer.cs</c> / <c>MainForm.AudioDevice.cs</c> / <c>MainForm.Chapters.cs</c>；
    /// 曲目元数据与「媒体信息」页在 <c>MainForm.Metadata.cs</c>。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private const int LogicalDefaultSidebarWidth = 300;

        private ToolStripMenuItem? _menuViewSidebar;

        private ToolStripMenuItem? _menuViewSidebarFloating;

        private ToolStripMenuItem? _menuViewPlaylistFloating;

        private ToolStripMenuItem? _menuMinimizeToTray;

        /// <summary>
        /// 当前这一首的会话状态：路径 / 标签 / 歌词 / 歌词偏移 / 标签版本号。
        /// <para>切歌就是换一份会话，界面各处只读它，不再各自持有一份。</para>
        /// </summary>
        private readonly TrackLyricsSession _track = new TrackLyricsSession();

        /// <summary>
        /// 只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。
        /// <para>
        /// 测试原来靠反射去读 <c>_currentLyrics</c> / <c>_currentTags</c> /
        /// <c>_lyricsOffsetMilliseconds</c>，内部字段一改名测试就红；改成读这个入口之后，
        /// 会话内部怎么调整都不再牵动测试。测试只读，不调这里的写方法。
        /// </para>
        /// </summary>
        internal TrackLyricsSession Track => _track;

        // =====================================================================
        // 初始化
        // =====================================================================
        private void ConfigureMetadataMenus()
        {
            ConfigureSidebarMenus();
            ConfigureDesktopLyricsMenu();
            ConfigureOnlineLookupMenu();
            ConfigureLyricsOffsetMenu();
            ConfigureEqualizerMenu();
            ConfigureAudioDeviceMenu();
            ConfigureChapterMenu();
            ConfigureVideoMenus();
            ConfigurePlaylistLibraryMenu();
            ConfigureLogMenu();
        }

        private void ConfigureSidebarMenus()
        {
            _menuViewSidebar = new ToolStripMenuItem("显示侧栏")
            {
                CheckOnClick = true,
                Checked = true
            };
            _menuViewSidebar.Click += (s, e) => ApplySidebarVisibility(_menuViewSidebar.Checked);

            // 紧跟各自的显示开关，成对出现更好找
            var index = menuView.DropDownItems.IndexOf(menuViewPlaylist);
            if (index < 0)
            {
                menuView.DropDownItems.Add(_menuViewSidebar);
            }
            else
            {
                menuView.DropDownItems.Insert(index + 1, _menuViewSidebar);

                _menuViewPlaylistFloating = new ToolStripMenuItem("播放列表分离为独立窗口") { CheckOnClick = true };
                _menuViewPlaylistFloating.Click += (s, e) =>
                    SetPlaylistFloating(_menuViewPlaylistFloating.Checked);
                menuView.DropDownItems.Insert(index + 1, _menuViewPlaylistFloating);
            }

            _menuViewSidebarFloating = new ToolStripMenuItem("侧栏分离为悬浮窗") { CheckOnClick = true };
            _menuViewSidebarFloating.Click += (s, e) => SetSidebarFloating(_menuViewSidebarFloating.Checked);

            var sidebarIndex = menuView.DropDownItems.IndexOf(_menuViewSidebar);
            if (sidebarIndex < 0)
                menuView.DropDownItems.Add(_menuViewSidebarFloating);
            else
                menuView.DropDownItems.Insert(sidebarIndex + 1, _menuViewSidebarFloating);

            menuView.DropDownItems.Add(new ToolStripSeparator());

            _menuMinimizeToTray = new ToolStripMenuItem("关闭窗口时缩到托盘") { CheckOnClick = true };
            _menuMinimizeToTray.Click += (s, e) => _settings.MinimizeToTray = _menuMinimizeToTray.Checked;
            menuView.DropDownItems.Add(_menuMinimizeToTray);

            sidebarPanel.CollapseRequested += (s, e) => ApplySidebarVisibility(false);
            sidebarPanel.SeekRequested += (s, time) => SeekToLyricLine(time);
        }

        /// <summary>显示 / 隐藏侧栏。全屏时不改布局，只记设置。</summary>
        private void ApplySidebarVisibility(bool visible)
        {
            _settings.ShowSidebar = visible;

            if (_menuViewSidebar != null && _menuViewSidebar.Checked != visible)
                _menuViewSidebar.Checked = visible;

            // 侧栏被分离出去时，"显示侧栏"控制的是那个悬浮窗
            if (_settings.SidebarFloating)
            {
                if (_sidebarWindow != null)
                {
                    if (visible) _sidebarWindow.Show();
                    else _sidebarWindow.Hide();
                }

                return;
            }

            if (_isFullscreen) return;

            splitVideo.Panel2Collapsed = !visible;

            ApplyPanelLayout();
        }

        /// <summary>逻辑像素 → 物理像素的比例（125% 显示下是 1.25）。</summary>
        private float UiScale => DeviceDpi / 96f;

        private int ScaledDefaultSidebarWidth => (int)Math.Round(LogicalDefaultSidebarWidth * UiScale);

        private int ScaledDefaultPlaylistWidth => (int)Math.Round(DefaultPlaylistPanelWidth * UiScale);

        /// <summary>期望的侧栏宽度（物理像素）。</summary>
        private int ResolveSidebarWidth() =>
            _settings.SidebarWidth > 0 ? _settings.SidebarWidth : ScaledDefaultSidebarWidth;

        /// <summary>侧栏当前停靠时的实际宽度（用于保存）。</summary>
        private int SidebarDockedWidth
        {
            get
            {
                if (splitVideo.Panel2Collapsed) return ResolveSidebarWidth();

                var width = splitVideo.Width - splitVideo.SplitterDistance - splitVideo.SplitterWidth;
                return width > 0 ? width : ResolveSidebarWidth();
            }
        }

        /// <summary>
        /// 重新分配「视频 | 侧栏」与「播放列表」的宽度。
        /// <para>
        /// 侧栏和播放列表现在分属两个不同的分隔条（侧栏跟着视频，播放列表独立），
        /// 所以两者互不影响：关掉播放列表不会再连带隐藏侧栏，宽度也各算各的。
        /// </para>
        /// </summary>
        private void ApplyPanelLayout()
        {
            var total = splitMain.Width;
            if (total <= 0) return;

            var sidebarFull = _settings.ShowSidebar && !_settings.SidebarFloating
                ? ResolveSidebarWidth() + splitVideo.SplitterWidth
                : 0;

            if (!splitMain.Panel2Collapsed)
            {
                var playlistMinimum = Math.Max(1, splitMain.Panel2MinSize);

                var defaultVideo = Math.Max(
                    splitVideo.Panel1MinSize,
                    total - sidebarFull - ScaledDefaultPlaylistWidth - splitMain.SplitterWidth);

                var desiredPanel1 = (_settings.SplitterDistance > 0 ? _settings.SplitterDistance : defaultVideo)
                                    + sidebarFull;

                var minimumPanel1 = Math.Max(1, splitMain.Panel1MinSize);
                var maximumPanel1 = total - playlistMinimum - splitMain.SplitterWidth;

                if (maximumPanel1 > minimumPanel1)
                {
                    try
                    {
                        splitMain.SplitterDistance = Math.Clamp(desiredPanel1, minimumPanel1, maximumPanel1);
                    }
                    catch (Exception ex)
                    {
// 忽略非法距离
                        AppLog.Swallowed("忽略非法距离", ex);
                    }
                }
            }

            ApplySidebarWidth();
        }

        /// <summary>在视频区内部摆放侧栏：视频占左边，侧栏固定宽度靠右。</summary>
        private void ApplySidebarWidth()
        {
            if (_settings.SidebarFloating)
            {
                if (!splitVideo.Panel2Collapsed) splitVideo.Panel2Collapsed = true;
                return;
            }

            splitVideo.Panel2Collapsed = !_settings.ShowSidebar;
            if (splitVideo.Panel2Collapsed) return;

            var total = splitVideo.Width;
            if (total <= 0) return;

            var minimumVideo = Math.Max(1, splitVideo.Panel1MinSize);
            var desired = total - ResolveSidebarWidth() - splitVideo.SplitterWidth;

            if (desired < minimumVideo) desired = minimumVideo;

            try
            {
                splitVideo.SplitterDistance = desired;
            }
            catch (Exception ex)
            {
// 忽略非法距离
                AppLog.Swallowed("忽略非法距离", ex);
            }
        }

        private void SeekToLyricLine(TimeSpan time)
        {
            if (!_engine.HasMedia) return;

            _engine.SeekTo((long)time.TotalMilliseconds);
            SetStatus("已跳转到 " + TimeFormatter.FormatWithHours(time));
        }
    }
}
