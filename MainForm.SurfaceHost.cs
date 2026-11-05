using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 主窗口作为"窗口模块宿主"的那一面：把模块需要的东西显式交出去（见
    /// <see cref="IWindowSurfaceHost"/>），以及把控制器挂到窗体生命周期上。
    /// <para>
    /// 做成显式接口实现而不是公开一堆新属性：这些是模块的窄接口，不该变成
    /// <c>MainForm</c> 的公开表面（测试仍然走那几个有名字的 internal 入口）。
    /// </para>
    /// </summary>
    public partial class MainForm : Form, IWindowSurfaceHost
    {
        private WindowSurfaceController _surfaces = null!;

        /// <summary>测试用：窗口形态控制器（音频 / 视频 / 全屏）。</summary>
        internal WindowSurfaceController Surfaces => _surfaces;

        /// <summary>控制器建好之后接上它的"形态变了"通知（菜单门禁 / 命令状态 / 系统媒体控件）。</summary>
        private void AttachWindowSurfaceController()
        {
            _surfaces = new WindowSurfaceController(this, _engine);
            _surfaces.SurfaceChanged += OnWindowSurfaceChanged;
        }

        private void OnWindowSurfaceChanged(object? sender, EventArgs e)
        {
            UpdateWindowModeGating();
            RefreshCommandSurfaces();

            // 音量弹窗里"这是不是视频"要跟着形态走（只听声音时不该说它是视频）
            UpdateSystemMediaControls(_track.Path, _track.Tags);
        }

        /// <summary>窗体尺寸变了（拖边 / DPI 变化 / 最大化）：卡片要跟着重排。</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            _surfaces?.OnFormResize();
        }

        // =====================================================================
        // IWindowSurfaceHost
        // =====================================================================
        Form IWindowSurfaceHost.Window => this;

        AppSettings IWindowSurfaceHost.Settings => _settings;

        ThemePalette IWindowSurfaceHost.Palette => _palette;

        MenuStrip IWindowSurfaceHost.MainMenuStrip => menuStripMain;

        ToolStrip IWindowSurfaceHost.MainToolStrip => toolStripMain;

        StatusStrip IWindowSurfaceHost.MainStatusStrip => statusStripMain;

        Panel IWindowSurfaceHost.TransportPanel => pnlTransport;

        SplitContainer IWindowSurfaceHost.MainSplit => splitMain;

        SplitContainer IWindowSurfaceHost.VideoSplit => splitVideo;

        Control IWindowSurfaceHost.VideoHost => splitVideo.Panel1;

        Label IWindowSurfaceHost.Placeholder => lblPlaceholder;

        bool IWindowSurfaceHost.HasMedia => _engine.HasMedia;

        bool IWindowSurfaceHost.IsPlaying => _engine.IsPlaying;

        string? IWindowSurfaceHost.CurrentTrackPath => _track.Path;

        MediaTags? IWindowSurfaceHost.CurrentTrackTags => _track.Tags;

        long IWindowSurfaceHost.PositionMilliseconds => _engine.Time;

        long IWindowSurfaceHost.LengthMilliseconds => _engine.Length;

        int IWindowSurfaceHost.PlaylistCount => _playlist.Count;

        void IWindowSurfaceHost.ExecuteCommand(PlayerCommandId id) => ExecuteCommand(id);

        void IWindowSurfaceHost.SeekTo(long milliseconds)
        {
            if (!_engine.HasMedia) return;

            _engine.SeekTo(milliseconds);

            // 松手之后立刻把滑块 / 时间挪到目标位置，不然要等下一拍 200 ms 才跟手。
            UpdateProgress();
        }

        void IWindowSurfaceHost.SetStatus(string text) => SetStatus(text);

        void IWindowSurfaceHost.SetFloatingWindowsVisible(bool visible) => SetFloatingWindowsVisible(visible);

        void IWindowSurfaceHost.RaiseDesktopLyricsToTop() => _desktopLyrics?.BringToTop();

        void IWindowSurfaceHost.ApplyPanelLayout() => ApplyPanelLayout();

        void IWindowSurfaceHost.SyncPlaylistMenuChecked() => menuViewPlaylist.Checked = _settings.ShowPlaylist;

        /// <summary>这块矩形还在不在任何一块屏幕的工作区里（拔副屏 / 改分辨率之后要判断）。</summary>
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
