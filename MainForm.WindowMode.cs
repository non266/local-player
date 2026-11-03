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
        /// 按当前偏好、文件与**真实轨道**决定要不要换形态。
        /// <para><paramref name="path"/> 为 <c>null</c> 且偏好是"自动"时什么都不做：
        /// 没有媒体可判，不猜（否则一按停止，窗口就自己从卡片跳回大窗口）。</para>
        /// <para>真实轨道只在"这份媒体的轨道真的解析出来了"时才拿来判（<see cref="PlayerEngine.HasVideoTrackFor"/>），
        /// 否则退回扩展名——刚打开的那一瞬间 <c>media.Tracks</c> 还是空的，不能拿它当"没有视频"。</para>
        /// </summary>
        private void SyncWindowShape(string? path, bool force = false)
        {
            if (_isFullscreen) return;

            var mode = WindowModes.Normalize(_settings.WindowMode);
            if (path == null && !force && mode == WindowModes.Auto) return;

            var realVideo = _engine.HasVideoTrackFor(path);

            if (WindowModes.Resolve(mode, path, realVideo) == WindowShape.Audio) EnterAudioWindow();
            else ExitAudioWindow();
        }

        /// <summary>
        /// 轨道清单刚出来（或变了）时按**真实轨道**纠正一次形态。
        /// <para>只纠正"自动"模式：手动固定成音频 / 视频窗口是用户的明确选择，轨道不推翻它。</para>
        /// <para>形态本来就对就什么都不做——正常文件（扩展名已经猜对）不会有任何窗口跳动，
        /// 只有"只有音轨的 mkv"这类扩展名猜错的情况才会缩一次（见 docs/1.10.0计划.md 的 F11′）。</para>
        /// </summary>
        private void SyncWindowShapeFromTracks()
        {
            if (_isFullscreen) return;
            if (!_engine.HasMedia) return;
            if (WindowModes.Normalize(_settings.WindowMode) != WindowModes.Auto) return;

            if (!_engine.TryGetHasVideoTrack(out var hasVideo)) return;

            var wanted = hasVideo ? WindowShape.Video : WindowShape.Audio;

            if (wanted == WindowShape.Audio) EnterAudioWindow();
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

            // 卡片是新建的（或刚显示），把当时的配色与状态补一次，再按当前面板尺寸摆好
            _nowPlaying.ApplyPalette(_palette);
            _nowPlaying.SetTransportState(_engine.HasMedia, _engine.IsPlaying, _playlist.Count > 0);
            LayoutAudioCard();
        }

        /// <summary>
        /// 把卡片摆成"视频面板去掉传输条"的矩形，并让卡片内部重排一次。
        /// <para>1.9.0 的封面错位就是少了这一步：卡片与卡片里的 <c>CoverView</c> 只在创建时量过尺寸
        /// （那时窗体还是设计尺寸 1152），之后窗口缩小不会重排，封面按旧宽度居中、被窗口边缘裁掉。
        /// 现在卡片不参加 Dock 布局，尺寸由这里显式给，窗口每次缩放都重算（见 docs/1.10.0计划.md 的 B9）。</para>
        /// <para><b>为什么还要"面板自己的 SizeChanged + 每拍自愈"两道保险</b>：窗体 <c>OnResize</c> 触发时，
        /// <c>SplitContainer</c> 的子面板可能还没换上新的尺寸（它的布局在窗体布局之后才跑），那一下算出来的
        /// 仍是旧宽度；真实机上"启动就播音乐"正好撞上这个顺序（1.10.0 第一次修就是只挂了 OnResize，
        /// 所有者截图里卡片还是 1152 宽）。所以真正可靠的是**面板自己的 SizeChanged**，
        /// 再加界面计时器每 200 ms 对比一次兜底——事件顺序怎么变都不会留下错位的封面。</para>
        /// </summary>
        private void LayoutAudioCard()
        {
            if (!_isAudioWindow || _nowPlaying == null) return;

            // 折叠状态下 SplitContainer 不随容器变窄重算 Panel1（实测 Width=581 时 Panel1 仍是 1171）：
            // 折叠切换一次逼它按当前宽度重排
            if (splitMain.Panel2Collapsed && splitMain.Panel1.Width != splitMain.Width)
            {
                splitMain.Panel2Collapsed = false;
                splitMain.Panel2Collapsed = true;
            }

            var panel = splitVideo.Panel1;
            var width = panel.ClientSize.Width;
            var height = panel.ClientSize.Height - pnlTransport.Height;
            if (width <= 0 || height <= 0) return;

            var bounds = new Rectangle(0, 0, width, Math.Max(0, height));
            if (_nowPlaying.Bounds != bounds) _nowPlaying.Bounds = bounds;

            _nowPlaying.PerformLayout();
        }

        /// <summary>界面计时器每 200ms 顺手对一次卡片尺寸（兜底，尺寸对的时候只做一次比较）。</summary>
        private void HealAudioCardLayout()
        {
            if (_isAudioWindow) LayoutAudioCard();
        }

        /// <summary>音频窗口里窗口尺寸变了（拖边 / DPI 变化 / 最大化）：卡片要跟着重排。</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (_isAudioWindow) LayoutAudioCard();
        }

        private void EnsureNowPlayingView()
        {
            if (_nowPlaying != null) return;

            // 刻意不用 Dock=Fill：尺寸由 LayoutAudioCard() 显式给，避免"隐藏期间排一次、之后不再重排"
            _nowPlaying = new NowPlayingView { Visible = false };

            splitVideo.Panel1.Controls.Add(_nowPlaying);

            // 盖在画面之上、占位标签之下：占位标签是最前面那个（DragDrop 提示），别把它挡住
            splitVideo.Panel1.Controls.SetChildIndex(_nowPlaying, 1);

            // ★ 真正可靠的触发点：面板自己的尺寸变了（它在窗体布局之后才换尺寸，
            //   挂窗体 OnResize 时读到的可能还是旧宽度）
            splitVideo.Panel1.SizeChanged += (s, e) =>
            {
                if (_isAudioWindow) LayoutAudioCard();
            };

            // 卡片里的控件条接到主窗体现成的动作上（和工具栏、托盘、桌面歌词同一个入口）
            var transport = _nowPlaying.Transport;
            transport.PlayPauseRequested += (s, e) => TogglePlayPause();
            transport.PreviousRequested += (s, e) => PlayPrevious();
            transport.NextRequested += (s, e) => PlayNext(true);
            transport.StopRequested += (s, e) => StopPlayback();

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
