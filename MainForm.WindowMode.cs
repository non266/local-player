using System;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 主窗口的"窗口形态"入口：菜单、勾选状态、以及"哪些入口在简化形态里点不动"。
    /// <para>
    /// 形态本身（视频 / 音频 / 迷你、各自的几何、进出时收哪些外壳）在
    /// <see cref="WindowSurfaceController"/> 与三个模块里（<see cref="VideoWindowModule"/> /
    /// <see cref="AudioWindowModule"/> / <see cref="FullscreenModule"/>）。这里只剩薄薄一层：
    /// 把菜单项接到控制器、把勾选状态刷对、把"音频窗口里没有播放列表 / 全屏"如实置灰。
    /// </para>
    /// <para>
    /// 判定规则仍在 <see cref="WindowModes"/>（纯函数），"总是音频 / 总是视频"覆盖与
    /// 按真实轨道纠正的调用也从这里转给控制器。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        private ToolStripMenuItem? _menuWindowMode;
        private ToolStripMenuItem? _menuWindowAuto;
        private ToolStripMenuItem? _menuWindowAudio;
        private ToolStripMenuItem? _menuWindowVideo;
        private ToolStripMenuItem? _menuMiniMode;

        /// <summary>当前是不是音频窗口（给冒烟测试用）。</summary>
        internal bool IsAudioWindow => _surfaces.Surface == WindowSurface.Audio;

        /// <summary>当前是不是迷你模式（给冒烟测试用）。</summary>
        internal bool IsMiniWindow => _surfaces.Surface == WindowSurface.Mini;

        /// <summary>音频窗口那一块"正在播放"卡片（没建出来时为 <c>null</c>）；给冒烟测试用。</summary>
        internal NowPlayingView? NowPlaying => _surfaces.AudioCard;

        /// <summary>迷你模式那张小卡片（没进过迷你模式时为 <c>null</c>）；给冒烟测试用。</summary>
        internal MiniPlayerView? MiniPlayer => _surfaces.MiniCard;

        // =====================================================================
        // 菜单与接线
        // =====================================================================
        private void ConfigureWindowModeMenu()
        {
            _menuWindowAuto = new ToolStripMenuItem("自动（按文件）") { Tag = WindowModes.Auto };
            _menuWindowAudio = new ToolStripMenuItem("总是音频窗口") { Tag = WindowModes.Audio };
            _menuWindowVideo = new ToolStripMenuItem("总是视频窗口") { Tag = WindowModes.Video };

            foreach (var item in new[] { _menuWindowAuto, _menuWindowAudio, _menuWindowVideo })
                item.Click += (s, e) => SetWindowMode((string)((ToolStripMenuItem)s!).Tag!);

            _menuWindowMode = new ToolStripMenuItem("窗口模式(&W)")
            {
                Name = "menuViewWindowMode",
                ShortcutKeyDisplayString = "Ctrl+Alt+M"
            };

            _menuWindowMode.DropDownItems.AddRange(new ToolStripItem[]
            {
                _menuWindowAuto, _menuWindowAudio, _menuWindowVideo
            });

            // 紧跟在「全屏」后面：都是"窗口长什么样"的那一类
            var index = menuView.DropDownItems.IndexOf(menuViewFullscreen);
            if (index >= 0)
            {
                menuView.DropDownItems.Insert(index + 1, _menuWindowMode);

                _menuMiniMode = new ToolStripMenuItem("迷你模式(&M)")
                {
                    Name = "menuViewMini",
                    ShortcutKeyDisplayString = "Ctrl+Alt+N"
                };

                menuView.DropDownItems.Insert(index + 2, _menuMiniMode);
            }
            else
            {
                menuView.DropDownItems.Add(_menuWindowMode);

                _menuMiniMode = new ToolStripMenuItem("迷你模式(&M)")
                {
                    Name = "menuViewMini",
                    ShortcutKeyDisplayString = "Ctrl+Alt+N"
                };

                menuView.DropDownItems.Add(_menuMiniMode);
            }

            UpdateWindowModeChecks();
        }

        private void UpdateWindowModeChecks()
        {
            var mode = WindowModes.Normalize(_settings.WindowMode);

            if (_menuWindowAuto != null) _menuWindowAuto.Checked = mode == WindowModes.Auto;
            if (_menuWindowAudio != null) _menuWindowAudio.Checked = mode == WindowModes.Audio;
            if (_menuWindowVideo != null) _menuWindowVideo.Checked = mode == WindowModes.Video;
            if (_menuMiniMode != null) _menuMiniMode.Checked = IsMiniWindow;
        }

        /// <summary>简化形态（音频窗口 / 迷你模式）里那几项"属于大窗口"的入口置灰。</summary>
        private void UpdateWindowModeGating()
        {
            var simplified = IsAudioWindow || IsMiniWindow;

            menuViewPlaylist.Enabled = !simplified;
            if (_menuViewSidebar != null) _menuViewSidebar.Enabled = !simplified;
            if (_menuViewPlaylistFloating != null) _menuViewPlaylistFloating.Enabled = !simplified;
            if (_menuViewSidebarFloating != null) _menuViewSidebarFloating.Enabled = !simplified;

            menuViewFullscreen.Enabled = !simplified;
            tsbTogglePlaylist.Enabled = !simplified;

            // 迷你模式固定置顶：这一项在迷你里点不出效果，置灰并靠勾选状态说明。
            menuViewTopMost.Enabled = !IsMiniWindow;

            UpdateWindowModeChecks();
        }

        /// <summary>「视图 → 窗口模式」：设偏好并立刻套用（测试也走这个入口）。</summary>
        internal void SetWindowMode(string preference)
        {
            _surfaces.SetWindowMode(preference);

            UpdateWindowModeChecks();
            RefreshCommandSurfaces();
        }

        /// <summary>
        /// <c>Ctrl+Alt+M</c>：在当前形态上直接翻一下。
        /// <para>
        /// **它和菜单等价，会把偏好定成翻到的那个形态**——不做"临时切一下、下首歌又跳回去"的第三态，
        /// 那种状态用户记不住（见 docs/1.9.0计划.md 的 F11）。
        /// </para>
        /// </summary>
        internal void ToggleWindowMode()
        {
            _surfaces.ToggleWindowMode();

            UpdateWindowModeChecks();
            RefreshCommandSurfaces();
        }

        /// <summary>进 / 出迷你模式（Ctrl+Alt+N、菜单、卡片上的"还原"都走这里）。</summary>
        internal void ToggleMini()
        {
            _surfaces.ToggleMini();

            UpdateWindowModeChecks();
            RefreshCommandSurfaces();
        }

        // =====================================================================
        // 形态判定：调用点保持不变，实现转给控制器
        // =====================================================================

        /// <summary>按当前偏好、文件与真实轨道决定要不要换形态（见控制器说明）。</summary>
        private void SyncWindowShape(string? path, bool force = false) => _surfaces.SyncForTrack(path, force);

        /// <summary>轨道清单刚出来（或变了）时按真实轨道纠正一次形态。</summary>
        private void SyncWindowShapeFromTracks() => _surfaces.SyncFromTracks();

        /// <summary>启动时先定形态（还没有媒体：只有"总是音频窗口"会直接进卡片）。</summary>
        private void PrepareInitialWindowShape() => _surfaces.PrepareInitial();

        /// <summary>把界面摆成当前形态该有的样子（可重复调用）。</summary>
        private void ApplyWindowShapeLayout() => _surfaces.Apply();

        /// <summary>界面计时器每 200 ms 顺手对一次卡片尺寸（兜底）。</summary>
        private void HealAudioCardLayout() => _surfaces.Heartbeat();
    }
}
