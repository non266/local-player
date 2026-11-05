using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 窗口形态的总管：视频 / 音频 / 迷你三种形态之间的切换、各自的几何，以及全屏。
    /// 原来这一整套在 <c>MainForm.WindowMode.cs</c>（438 行）+
    /// <c>MainForm.Layout.cs</c> + <c>MainForm.Fullscreen.cs</c> 里，与窗体字段互相咬合。
    /// <para>
    /// 判定规则仍在 <see cref="WindowModes"/>（纯函数）：默认按文件扩展名自动切，
    /// "总是音频 / 总是视频"可以覆盖；真实轨道出来之后按轨道再纠正一次。
    /// 每种形态<b>各记各的位置与大小</b>，来回切多少次都不互相污染。
    /// </para>
    /// <para>
    /// 形态变化后由 <see cref="SurfaceChanged"/> 通知主窗体（菜单门禁、命令层状态、
    /// 系统媒体控件里"这是不是视频"都要跟着变）。模块自己不碰菜单项。
    /// </para>
    /// </summary>
    internal sealed class WindowSurfaceController : IDisposable
    {
        private readonly IWindowSurfaceHost _host;

        private readonly PlayerEngine _engine;

        // 进音频窗口之前视频窗口的样子：退出时按这个原样回去。
        private Rectangle _videoBoundsBeforeAudio;
        private Size _videoMinimumBeforeAudio;
        private bool _videoMaximizedBeforeAudio;
        private bool _toolStripVisibleBeforeAudio = true;

        // 进迷你模式之前的样子：退出时按这个原样回去（迷你可以从视频或音频进来）。
        private WindowSurface _surfaceBeforeMini = WindowSurface.Video;
        private Rectangle _boundsBeforeMini;
        private Size _minimumBeforeMini;
        private bool _topMostBeforeMini;
        private bool _maximizeBoxBeforeMini;
        private bool _toolStripVisibleBeforeMini = true;

        public WindowSurfaceController(IWindowSurfaceHost host, PlayerEngine engine)
        {
            _host = host;
            _engine = engine;

            Video = new VideoWindowModule(host);
            Audio = new AudioWindowModule(host);
            Fullscreen = new FullscreenModule(host);

            Audio.MiniRestoreRequested += (s, e) => ToggleMini();
        }

        /// <summary>形态变了（菜单门禁 / 命令可用性 / 系统媒体控件要跟着刷）。</summary>
        public event EventHandler? SurfaceChanged;

        public WindowSurface Surface { get; private set; } = WindowSurface.Video;

        public VideoWindowModule Video { get; }

        public AudioWindowModule Audio { get; }

        public FullscreenModule Fullscreen { get; }

        public NowPlayingView? AudioCard => Audio.Card;

        public MiniPlayerView? MiniCard => Audio.Mini;

        public FullscreenProgressBar? FullscreenBar => Fullscreen.Bar;

        public bool IsFullscreen => Fullscreen.IsFullscreen;

        /// <summary>
        /// 启动时先定形态（还没有媒体：只有"总是音频窗口"会直接进卡片；
        /// 上次退出时是迷你模式的话，再套一层迷你）。
        /// <para>这里只定形态与最小尺寸；具体窗口位置交给 <see cref="RestorePlacement"/>，
        /// 它决定用哪一套几何（<c>WindowX…</c> / <c>AudioWindowX…</c> / <c>MiniWindowX…</c>）。</para>
        /// </summary>
        public void PrepareInitial()
        {
            var form = _host.Window;

            if (_videoMinimumBeforeAudio.IsEmpty) _videoMinimumBeforeAudio = form.MinimumSize;

            Surface = WindowSurface.Video;

            if (WindowModes.Resolve(_host.Settings.WindowMode, null) == WindowShape.Audio)
            {
                Surface = WindowSurface.Audio;
                _videoMaximizedBeforeAudio = _host.Settings.WindowMaximized;
                _toolStripVisibleBeforeAudio = true;
                form.MinimumSize = Audio.MinimumSize;
            }

            if (!_host.Settings.MiniMode) return;

            // 上次退出时是迷你模式：基底形态仍按偏好算好，退出迷你时回到它。
            _surfaceBeforeMini = Surface;
            _toolStripVisibleBeforeMini = Surface != WindowSurface.Audio;
            Surface = WindowSurface.Mini;
            form.MinimumSize = Audio.MiniMinimumSize;
        }

        /// <summary>按当前形态恢复窗口位置与大小；进过简化形态的顺手记下"原来该在哪儿"。</summary>
        public void RestorePlacement()
        {
            var form = _host.Window;

            if (Surface == WindowSurface.Mini)
            {
                _videoBoundsBeforeAudio = Video.ResolveBounds(_videoMinimumBeforeAudio);

                _boundsBeforeMini = _surfaceBeforeMini == WindowSurface.Audio
                    ? Audio.ResolveBounds()
                    : _videoBoundsBeforeAudio;

                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = Audio.ResolveMiniBounds();
                return;
            }

            // 音频窗口：用它自己那套几何；顺手把"视频窗口本该在哪儿"记下来，退出时回到那里
            if (Surface == WindowSurface.Audio)
            {
                _videoBoundsBeforeAudio = Video.ResolveBounds(_videoMinimumBeforeAudio);

                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = Audio.ResolveBounds();
                return;
            }

            // 没有保存过位置，或者保存的尺寸小到不合理：沿用设计尺寸并居中。
            if (_host.Settings.WindowWidth <= 0 || _host.Settings.WindowHeight <= 0)
            {
                form.StartPosition = FormStartPosition.CenterScreen;
                return;
            }

            var width = Math.Max(_host.Settings.WindowWidth, form.MinimumSize.Width);
            var height = Math.Max(_host.Settings.WindowHeight, form.MinimumSize.Height);
            var bounds = new Rectangle(_host.Settings.WindowX, _host.Settings.WindowY, width, height);

            if (!IsVisibleOnAnyScreen(bounds))
            {
                form.StartPosition = FormStartPosition.CenterScreen;
            }
            else
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = bounds;
            }

            if (_host.Settings.WindowMaximized)
                form.WindowState = FormWindowState.Maximized;
        }

        /// <summary>把界面摆成当前形态该有的样子（可重复调用）。</summary>
        public void Apply()
        {
            if (Surface == WindowSurface.Mini)
            {
                ApplyMiniChrome();
                Audio.EnterMini(_host.CurrentTrackPath, _host.CurrentTrackTags);
            }
            else if (Surface == WindowSurface.Audio)
            {
                _host.MainToolStrip.Visible = false;
                _host.MainSplit.Panel2Collapsed = true;
                _host.VideoSplit.Panel2Collapsed = true;
                _host.Placeholder.Visible = false;

                Audio.Enter(_host.CurrentTrackPath, _host.CurrentTrackTags);
            }

            SurfaceChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 按当前偏好、文件与<b>真实轨道</b>决定要不要换形态。
        /// <para><paramref name="path"/> 为 <c>null</c> 且偏好是"自动"时什么都不做：
        /// 没有媒体可判，不猜（否则一按停止，窗口就自己从卡片跳回大窗口）。</para>
        /// <para>迷你模式是用户的明确选择：切歌不推翻它（<see cref="SyncForTrack"/> 直接返回）。</para>
        /// </summary>
        public void SyncForTrack(string? path, bool force = false)
        {
            if (IsFullscreen) return;
            if (Surface == WindowSurface.Mini) return;

            var mode = WindowModes.Normalize(_host.Settings.WindowMode);
            if (path == null && !force && mode == WindowModes.Auto) return;

            var realVideo = _engine.HasVideoTrackFor(path);

            if (WindowModes.Resolve(mode, path, realVideo) == WindowShape.Audio) EnterAudio();
            else ExitAudio();
        }

        /// <summary>
        /// 轨道清单刚出来（或变了）时按真实轨道纠正一次形态。
        /// <para>只纠正"自动"模式：手动固定成音频 / 视频窗口是用户的明确选择，轨道不推翻它；
        /// 迷你模式同理（用户自己选的形态）。</para>
        /// </summary>
        public void SyncFromTracks()
        {
            if (IsFullscreen) return;
            if (Surface == WindowSurface.Mini) return;
            if (!_engine.HasMedia) return;
            if (WindowModes.Normalize(_host.Settings.WindowMode) != WindowModes.Auto) return;

            if (!_engine.TryGetHasVideoTrack(out var hasVideo)) return;

            if (hasVideo) ExitAudio();
            else EnterAudio();
        }

        /// <summary>「视图 → 窗口模式」：设偏好并立刻套用（测试也走这个入口）。</summary>
        public void SetWindowMode(string preference)
        {
            // 在迷你模式里改窗口模式 = 先退出迷你，再按新偏好落到音频 / 视频。
            if (Surface == WindowSurface.Mini) ExitMini();

            _host.Settings.WindowMode = WindowModes.Normalize(preference);

            SyncForTrack(_host.CurrentTrackPath, force: true);

            var mode = _host.Settings.WindowMode;

            _host.SetStatus(mode == WindowModes.Auto
                ? "窗口模式：自动（按文件类型切换）"
                : mode == WindowModes.Audio
                    ? "窗口模式：总是音频窗口（视图 → 窗口模式 可改回自动）"
                    : "窗口模式：总是视频窗口（视图 → 窗口模式 可改回自动）");
        }

        /// <summary>
        /// 在当前形态上直接翻一下（与菜单等价，会把偏好定成翻到的那个形态）。
        /// </summary>
        public void ToggleWindowMode()
        {
            if (IsFullscreen)
            {
                _host.SetStatus("先退出全屏，再切换音频 / 视频窗口");
                return;
            }

            if (Surface == WindowSurface.Mini)
            {
                var target = _surfaceBeforeMini == WindowSurface.Audio ? WindowModes.Video : WindowModes.Audio;
                SetWindowMode(target);
                return;
            }

            SetWindowMode(Surface == WindowSurface.Audio ? WindowModes.Video : WindowModes.Audio);
        }

        /// <summary>进 / 出迷你模式（Ctrl+Alt+N、菜单、卡片上的"还原"都走这里）。</summary>
        public void ToggleMini()
        {
            if (IsFullscreen)
            {
                _host.SetStatus("先退出全屏，再进入迷你模式");
                return;
            }

            if (Surface == WindowSurface.Mini) ExitMini();
            else EnterMini();
        }

        public void ToggleFullscreen()
        {
            if (IsFullscreen) ExitFullscreen();
            else EnterFullscreen();
        }

        public void EnterFullscreen()
        {
            if (IsFullscreen) return;

            // 音频窗口是一张卡片、迷你模式只有一张小卡：都没有"满屏画面"可言
            if (Surface == WindowSurface.Audio)
            {
                _host.SetStatus("音频窗口没有全屏；按 Ctrl+Alt+M 切回视频窗口再按 F11");
                return;
            }

            if (Surface == WindowSurface.Mini)
            {
                _host.SetStatus("迷你模式没有全屏；按 Ctrl+Alt+N 还原窗口");
                return;
            }

            Fullscreen.Enter();
        }

        public void ExitFullscreen() => Fullscreen.Exit();

        /// <summary>窗体尺寸变了（拖边 / DPI 变化 / 最大化）：卡片要跟着重排。</summary>
        public void OnFormResize() => Audio.Layout();

        /// <summary>界面计时器每 200 ms 顺手对一次卡片尺寸（兜底，尺寸对的时候只做一次比较）。</summary>
        public void Heartbeat() => Audio.Heartbeat();

        public void UpdateTransportState(bool hasMedia, bool isPlaying, bool hasItems) =>
            Audio.UpdateTransportState(hasMedia, isPlaying, hasItems);

        /// <summary>把同一次算出来的时间 / 总时长推给迷你卡片与全屏进度条。</summary>
        public void UpdateProgress(long timeMilliseconds, long lengthMilliseconds)
        {
            Audio.UpdateProgress(timeMilliseconds, lengthMilliseconds);
            Fullscreen.UpdateProgress(timeMilliseconds, lengthMilliseconds);
        }

        public void PollPointer(Point screenPoint) => Fullscreen.PollPointer(screenPoint);

        public void OnDeactivate() => Fullscreen.OnDeactivate();

        public void ApplyPalette()
        {
            Audio.ApplyPalette(_host.Palette);
            Fullscreen.ApplyPalette(_host.Palette);
        }

        /// <summary>退出时按当前形态把几何写回设置（视频 / 音频 / 迷你各记各的）。</summary>
        public void CaptureGeometry()
        {
            if (IsFullscreen) return;

            switch (Surface)
            {
                case WindowSurface.Mini:
                    Audio.CaptureMiniGeometry();
                    break;

                case WindowSurface.Audio:
                    Audio.CaptureGeometry();
                    break;

                default:
                    Video.CaptureGeometry();
                    break;
            }
        }

        public void Dispose()
        {
            Audio.Dispose();
            Fullscreen.Dispose();
        }

        private void EnterAudio()
        {
            if (Surface == WindowSurface.Audio) return;

            var form = _host.Window;

            if (_videoMinimumBeforeAudio.IsEmpty) _videoMinimumBeforeAudio = form.MinimumSize;

            Surface = WindowSurface.Audio;

            // 记下视频窗口的样子：退出时按这个回来（最大化状态也记）
            _videoBoundsBeforeAudio = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            _videoMaximizedBeforeAudio = form.WindowState == FormWindowState.Maximized;
            _toolStripVisibleBeforeAudio = _host.MainToolStrip.Visible;

            // 先换最小尺寸再赋 Bounds：否则 420 宽会被视频窗口的 860 夹回去
            form.WindowState = FormWindowState.Normal;
            form.MinimumSize = Audio.MinimumSize;
            form.Bounds = Audio.ResolveBounds();

            Apply();
        }

        private void ExitAudio()
        {
            if (Surface != WindowSurface.Audio) return;

            var form = _host.Window;

            Surface = WindowSurface.Video;

            form.MinimumSize = _videoMinimumBeforeAudio.IsEmpty ? Video.DefaultMinimum : _videoMinimumBeforeAudio;
            form.WindowState = FormWindowState.Normal;

            if (_videoBoundsBeforeAudio.Width > 0 && _videoBoundsBeforeAudio.Height > 0)
                form.Bounds = _videoBoundsBeforeAudio;

            Audio.Exit();
            Video.RestoreChrome(_toolStripVisibleBeforeAudio);

            if (_videoMaximizedBeforeAudio) form.WindowState = FormWindowState.Maximized;

            SurfaceChanged?.Invoke(this, EventArgs.Empty);
        }

        private void EnterMini()
        {
            var form = _host.Window;

            _surfaceBeforeMini = Surface;
            _boundsBeforeMini = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            _minimumBeforeMini = form.MinimumSize;
            _topMostBeforeMini = form.TopMost;
            _maximizeBoxBeforeMini = form.MaximizeBox;
            _toolStripVisibleBeforeMini = _host.MainToolStrip.Visible;

            Surface = WindowSurface.Mini;

            form.WindowState = FormWindowState.Normal;
            form.MinimumSize = Audio.MiniMinimumSize;
            form.MaximizeBox = false;
            form.Bounds = Audio.ResolveMiniBounds();
            form.TopMost = true;

            // 音频卡片与迷你卡片都挂在同一个面板上：进迷你时把音频那张收起来。
            Audio.Exit();

            Apply();
        }

        private void ExitMini()
        {
            if (Surface != WindowSurface.Mini) return;

            var form = _host.Window;
            var back = _surfaceBeforeMini;

            Surface = back;

            form.MinimumSize = _minimumBeforeMini.IsEmpty ? Audio.MinimumSize : _minimumBeforeMini;
            form.MaximizeBox = _maximizeBoxBeforeMini;
            form.TopMost = _topMostBeforeMini;
            form.WindowState = FormWindowState.Normal;

            if (_boundsBeforeMini.Width > 0 && _boundsBeforeMini.Height > 0)
                form.Bounds = _boundsBeforeMini;

            Audio.ExitMini();

            _host.MainMenuStrip.Visible = true;
            _host.MainStatusStrip.Visible = true;
            _host.TransportPanel.Visible = true;

            if (back == WindowSurface.Audio)
            {
                _host.MainToolStrip.Visible = false;
                _host.MainSplit.Panel2Collapsed = true;
                _host.VideoSplit.Panel2Collapsed = true;
                _host.Placeholder.Visible = false;

                Audio.Enter(_host.CurrentTrackPath, _host.CurrentTrackTags);
            }
            else
            {
                Video.RestoreChrome(_toolStripVisibleBeforeMini);
            }

            SurfaceChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyMiniChrome()
        {
            _host.MainMenuStrip.Visible = false;
            _host.MainToolStrip.Visible = false;
            _host.MainStatusStrip.Visible = false;
            _host.TransportPanel.Visible = false;
            _host.MainSplit.Panel2Collapsed = true;
            _host.VideoSplit.Panel2Collapsed = true;
            _host.Placeholder.Visible = false;

            _host.Window.TopMost = true;
            _host.Window.MaximizeBox = false;
        }

        private static bool IsVisibleOnAnyScreen(Rectangle bounds)
        {
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.IntersectsWith(bounds)) return true;
            }

            return false;
        }
    }
}
