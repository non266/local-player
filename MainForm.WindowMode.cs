using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 主窗口的"窗口形态"部分：<b>音频窗口</b>（简化的"正在播放"卡片）与<b>视频窗口</b>
    /// （画面 + 侧栏 + 播放列表）。
    /// <para>
    /// 判定规则在 <see cref="WindowModes"/>（纯函数）：默认按文件扩展名自动切，"总是音频 / 总是视频"
    /// 可以覆盖。两种窗口<b>各记各的位置与大小</b>（<c>WindowX…</c> 与 <c>AudioWindowX…</c>），
    /// 来回切多少次都不互相污染。
    /// </para>
    /// <para>
    /// 实现上刻意<b>不换控件树</b>：音频窗口只是把一块不透明的 <see cref="NowPlayingView"/> 盖在画面位置上，
    /// 传输条仍在原地 —— 进度 / 音量 / 主题 / 冒烟找 <c>trackSeek</c> 的那几条都不用改。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>音频窗口的默认尺寸（逻辑像素；高分屏按比例放大）。</summary>
        private const int LogicalAudioWidth = 420;
        private const int LogicalAudioHeight = 650;

        /// <summary>音频窗口允许缩到的最小尺寸（逻辑像素）。</summary>
        private const int LogicalAudioMinimumWidth = 360;
        private const int LogicalAudioMinimumHeight = 520;

        /// <summary>视频窗口的设计尺寸，只在"从没保存过几何"时拿来兜底。</summary>
        private const int LogicalVideoWidth = 1152;
        private const int LogicalVideoHeight = 700;

        private NowPlayingView? _nowPlaying;

        private ToolStripMenuItem? _menuWindowMode;
        private ToolStripMenuItem? _menuWindowAuto;
        private ToolStripMenuItem? _menuWindowAudio;
        private ToolStripMenuItem? _menuWindowVideo;

        private bool _isAudioWindow;

        // 进音频窗口之前视频窗口的样子：退出时按这个原样回去。
        private Rectangle _videoBoundsBeforeAudio;
        private Size _videoMinimumBeforeAudio;
        private bool _videoMaximizedBeforeAudio;
        private bool _toolStripVisibleBeforeAudio = true;

        /// <summary>当前是不是音频窗口（给冒烟测试用）。</summary>
        internal bool IsAudioWindow => _isAudioWindow;

        /// <summary>音频窗口那一块"正在播放"卡片（没建出来时为 <c>null</c>）；给冒烟测试用。</summary>
        internal NowPlayingView? NowPlaying => _nowPlaying;

        private int Scaled(int logical) => (int)Math.Round(logical * UiScale);

        private Size ScaledAudioMinimum =>
            new Size(Math.Max(200, Scaled(LogicalAudioMinimumWidth)), Math.Max(200, Scaled(LogicalAudioMinimumHeight)));

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
                ShortcutKeyDisplayString = "Ctrl+Alt+M"
            };

            _menuWindowMode.DropDownItems.AddRange(new ToolStripItem[]
            {
                _menuWindowAuto, _menuWindowAudio, _menuWindowVideo
            });

            // 紧跟在「全屏」后面：都是"窗口长什么样"的那一类
            var index = menuView.DropDownItems.IndexOf(menuViewFullscreen);
            if (index >= 0) menuView.DropDownItems.Insert(index + 1, _menuWindowMode);
            else menuView.DropDownItems.Add(_menuWindowMode);

            UpdateWindowModeChecks();
        }

        private void UpdateWindowModeChecks()
        {
            var mode = WindowModes.Normalize(_settings.WindowMode);

            if (_menuWindowAuto != null) _menuWindowAuto.Checked = mode == WindowModes.Auto;
            if (_menuWindowAudio != null) _menuWindowAudio.Checked = mode == WindowModes.Audio;
            if (_menuWindowVideo != null) _menuWindowVideo.Checked = mode == WindowModes.Video;
        }

        /// <summary>音频窗口里那几项"属于视频窗口"的入口置灰，并说明为什么（状态栏由调用方写）。</summary>
        private void UpdateWindowModeGating()
        {
            var audio = _isAudioWindow;

            menuViewPlaylist.Enabled = !audio;
            if (_menuViewSidebar != null) _menuViewSidebar.Enabled = !audio;
            if (_menuViewPlaylistFloating != null) _menuViewPlaylistFloating.Enabled = !audio;
            if (_menuViewSidebarFloating != null) _menuViewSidebarFloating.Enabled = !audio;

            menuViewFullscreen.Enabled = !audio;
            tsbTogglePlaylist.Enabled = !audio;

            UpdateWindowModeChecks();
        }

        /// <summary>「视图 → 窗口模式」：设偏好并立刻套用（测试也走这个入口）。</summary>
        internal void SetWindowMode(string preference)
        {
            _settings.WindowMode = WindowModes.Normalize(preference);

            UpdateWindowModeChecks();
            SyncWindowShape(_track.Path, force: true);

            var mode = _settings.WindowMode;

            SetStatus(mode == WindowModes.Auto
                ? "窗口模式：自动（按文件类型切换）"
                : mode == WindowModes.Audio
                    ? "窗口模式：总是音频窗口（视图 → 窗口模式 可改回自动）"
                    : "窗口模式：总是视频窗口（视图 → 窗口模式 可改回自动）");
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
            if (_isFullscreen)
            {
                SetStatus("先退出全屏，再切换音频 / 视频窗口");
                return;
            }

            SetWindowMode(_isAudioWindow ? WindowModes.Video : WindowModes.Audio);
        }

        // =====================================================================
        // 形态切换
        // =====================================================================

        /// <summary>
        /// 按当前偏好与文件决定要不要换形态。
        /// <para><paramref name="path"/> 为 <c>null</c> 且偏好是"自动"时什么都不做：
        /// 没有媒体可判，不猜（否则一按停止，窗口就自己从卡片跳回大窗口）。</para>
        /// </summary>
        private void SyncWindowShape(string? path, bool force = false)
        {
            if (_isFullscreen) return;

            var mode = WindowModes.Normalize(_settings.WindowMode);
            if (path == null && !force && mode == WindowModes.Auto) return;

            if (WindowModes.Resolve(mode, path) == WindowShape.Audio) EnterAudioWindow();
            else ExitAudioWindow();
        }

        /// <summary>启动时先定形态（还没有媒体：只有"总是音频窗口"会直接进卡片）。</summary>
        private void PrepareInitialWindowShape()
        {
            if (_videoMinimumBeforeAudio.IsEmpty) _videoMinimumBeforeAudio = MinimumSize;

            _isAudioWindow = false;

            if (WindowModes.Resolve(_settings.WindowMode, null) != WindowShape.Audio) return;

            _isAudioWindow = true;
            _videoMaximizedBeforeAudio = _settings.WindowMaximized;
            _toolStripVisibleBeforeAudio = true;

            EnsureNowPlayingView();
            MinimumSize = ScaledAudioMinimum;
        }

        private void EnterAudioWindow()
        {
            if (_isAudioWindow) return;

            if (_videoMinimumBeforeAudio.IsEmpty) _videoMinimumBeforeAudio = MinimumSize;

            _isAudioWindow = true;

            // 记下视频窗口的样子：退出时按这个回来（最大化状态也记）
            _videoBoundsBeforeAudio = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            _videoMaximizedBeforeAudio = WindowState == FormWindowState.Maximized;
            _toolStripVisibleBeforeAudio = toolStripMain.Visible;

            // 先换最小尺寸再赋 Bounds：否则 420 宽会被视频窗口的 860 夹回去
            WindowState = FormWindowState.Normal;
            MinimumSize = ScaledAudioMinimum;
            Bounds = ResolveAudioBounds();

            ApplyWindowShapeLayout();

            // 音量弹窗里"这是不是视频"要跟着形态走（只听声音时不该说它是视频）
            UpdateSystemMediaControls(_track.Path, _track.Tags);
        }

        private void ExitAudioWindow()
        {
            if (!_isAudioWindow) return;

            _isAudioWindow = false;

            MinimumSize = _videoMinimumBeforeAudio.IsEmpty ? ScaledVideoMinimum() : _videoMinimumBeforeAudio;
            WindowState = FormWindowState.Normal;

            if (_videoBoundsBeforeAudio.Width > 0 && _videoBoundsBeforeAudio.Height > 0)
                Bounds = _videoBoundsBeforeAudio;

            toolStripMain.Visible = _toolStripVisibleBeforeAudio;

            // 两栏按设置重新折叠（和退出全屏时同一套算法）
            splitMain.Panel2Collapsed = !_settings.ShowPlaylist || _settings.PlaylistFloating;
            splitVideo.Panel2Collapsed = !_settings.ShowSidebar || _settings.SidebarFloating;

            if (_nowPlaying != null) _nowPlaying.Visible = false;
            lblPlaceholder.Visible = !_engine.HasMedia;

            ApplyPanelLayout();

            if (_videoMaximizedBeforeAudio) WindowState = FormWindowState.Maximized;

            UpdateWindowModeGating();

            UpdateSystemMediaControls(_track.Path, _track.Tags);
        }

        /// <summary>把界面摆成当前形态该有的样子（可重复调用）。</summary>
        private void ApplyWindowShapeLayout()
        {
            if (!_isAudioWindow)
            {
                UpdateWindowModeGating();
                return;
            }

            EnsureNowPlayingView();

            _nowPlaying!.ShowTrack(_track.Path, _track.Tags);
            _nowPlaying.Visible = true;
            _nowPlaying.BringToFront();

            lblPlaceholder.Visible = false;
            toolStripMain.Visible = false;

            splitMain.Panel2Collapsed = true;
            splitVideo.Panel2Collapsed = true;

            UpdateWindowModeGating();

            // 卡片是新建的（或刚显示），把当时的配色补一次
            _nowPlaying.ApplyPalette(_palette);
        }

        private void EnsureNowPlayingView()
        {
            if (_nowPlaying != null) return;

            _nowPlaying = new NowPlayingView { Dock = DockStyle.Fill, Visible = false };

            splitVideo.Panel1.Controls.Add(_nowPlaying);

            // 插在画面之上、占位标签之下：停靠按"列表倒序"处理，
            // 这样它会填满传输条以上的区域，又不会盖住空闲时的拖放提示。
            splitVideo.Panel1.Controls.SetChildIndex(_nowPlaying, 1);

            _nowPlaying.ShowTrack(_track.Path, _track.Tags);
            _nowPlaying.ApplyPalette(_palette);
        }

        /// <summary>音频窗口该用多大、摆在哪儿：优先用上次记住的，其次按当前窗口居中。</summary>
        private Rectangle ResolveAudioBounds()
        {
            var area = Screen.FromControl(this).WorkingArea;
            var min = ScaledAudioMinimum;

            var width = Math.Max(min.Width, Scaled(LogicalAudioWidth));
            var height = Math.Max(min.Height, Scaled(LogicalAudioHeight));

            var hasSaved = _settings.AudioWindowWidth > 0 && _settings.AudioWindowHeight > 0;

            if (hasSaved)
            {
                width = Math.Max(min.Width, _settings.AudioWindowWidth);
                height = Math.Max(min.Height, _settings.AudioWindowHeight);
            }

            var anchor = hasSaved
                ? new Point(_settings.AudioWindowX, _settings.AudioWindowY)
                : new Point(Bounds.X + (Bounds.Width - width) / 2, Bounds.Y + (Bounds.Height - height) / 3);

            var bounds = new Rectangle(anchor.X, anchor.Y, width, height);

            if (!IsVisibleOnAnyScreen(bounds))
            {
                // 上次那块屏幕不在了（拔掉副屏 / 换了分辨率）：回到主屏正中
                bounds.X = area.X + Math.Max(0, (area.Width - width) / 2);
                bounds.Y = area.Y + Math.Max(0, (area.Height - height) / 2);
                return bounds;
            }

            bounds.X = Math.Min(Math.Max(bounds.X, area.X), Math.Max(area.X, area.Right - width));
            bounds.Y = Math.Min(Math.Max(bounds.Y, area.Y), Math.Max(area.Y, area.Bottom - height));

            return bounds;
        }

        /// <summary>视频窗口的设计尺寸（从没保存过几何时兜底用）。</summary>
        private Size ScaledVideoMinimum() => new Size(Scaled(LogicalVideoWidth), Scaled(LogicalVideoHeight));

        /// <summary>视频窗口"本该在哪儿"：退出音频窗口时回到这里（没保存过就居中设计尺寸）。</summary>
        private Rectangle ResolveVideoBounds()
        {
            if (_settings.WindowWidth > 0 && _settings.WindowHeight > 0)
            {
                var min = _videoMinimumBeforeAudio.IsEmpty ? ScaledVideoMinimum() : _videoMinimumBeforeAudio;

                return new Rectangle(
                    _settings.WindowX,
                    _settings.WindowY,
                    Math.Max(_settings.WindowWidth, min.Width),
                    Math.Max(_settings.WindowHeight, min.Height));
            }

            var size = ScaledVideoMinimum();
            var area = Screen.FromControl(this).WorkingArea;

            return new Rectangle(
                area.X + Math.Max(0, (area.Width - size.Width) / 2),
                area.Y + Math.Max(0, (area.Height - size.Height) / 2),
                size.Width,
                size.Height);
        }
    }
}
