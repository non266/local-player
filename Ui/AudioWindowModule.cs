using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 音频窗口与迷你模式：两张卡片（<see cref="NowPlayingView"/> 与 <see cref="MiniPlayerView"/>）
    /// 的创建、摆放、换色、推曲目 / 歌词 / 进度。
    /// <para>
    /// 这几件事原来都在 <c>MainForm.WindowMode.cs</c>。现在收在这里，窗体只负责说一句
    /// "进音频窗口 / 进迷你模式 / 换了一首"。
    /// </para>
    /// <para>
    /// 两张卡片都刻意<b>不换控件树</b>：它们只是盖在画面位置上的不透明面板；
    /// <see cref="LibVLCSharp.WinForms.VideoView"/> 在下面继续存在（句柄不变）。
    /// 内部一律算术排版（B9 的教训），并且由面板自己的 <c>SizeChanged</c> +
    /// 界面计时器的 <see cref="Heartbeat"/> 两道保险兜住事件顺序。
    /// </para>
    /// </summary>
    internal sealed class AudioWindowModule
    {
        /// <summary>音频窗口的默认尺寸（逻辑像素；高分屏按比例放大）。</summary>
        private const int LogicalWidth = 420;
        private const int LogicalHeight = 650;

        /// <summary>音频窗口允许缩到的最小尺寸（逻辑像素）。</summary>
        private const int LogicalMinimumWidth = 360;
        private const int LogicalMinimumHeight = 520;

        /// <summary>迷你模式的默认尺寸与最小尺寸（逻辑像素）。</summary>
        private const int LogicalMiniWidth = 440;
        private const int LogicalMiniHeight = 160;
        private const int LogicalMiniMinimumWidth = 380;
        private const int LogicalMiniMinimumHeight = 140;

        /// <summary>迷你窗口第一次出现时离工作区右下角多远（逻辑像素）。</summary>
        private const int LogicalMiniMargin = 24;

        private readonly IWindowSurfaceHost _host;

        private NowPlayingView? _card;
        private MiniPlayerView? _mini;

        public AudioWindowModule(IWindowSurfaceHost host)
        {
            _host = host;
        }

        /// <summary>音频卡片（还没建出来时为 <c>null</c>）；给冒烟测试与命令层推状态用。</summary>
        public NowPlayingView? Card => _card;

        /// <summary>迷你卡片（还没进过迷你模式时为 <c>null</c>）。</summary>
        public MiniPlayerView? Mini => _mini;

        /// <summary>"从迷你还原"（卡片上的按钮或双击）。</summary>
        public event EventHandler? MiniRestoreRequested;

        /// <summary>音频窗口的最小尺寸（按当前 DPI 缩放）。</summary>
        public Size MinimumSize => new Size(
            Math.Max(200, Scaled(LogicalMinimumWidth)),
            Math.Max(200, Scaled(LogicalMinimumHeight)));

        /// <summary>迷你模式的最小尺寸（按当前 DPI 缩放）。</summary>
        public Size MiniMinimumSize => new Size(
            Math.Max(200, Scaled(LogicalMiniMinimumWidth)),
            Math.Max(120, Scaled(LogicalMiniMinimumHeight)));

        // =====================================================================
        // 音频窗口
        // =====================================================================

        /// <summary>进音频窗口：保证卡片在、把它盖到画面上，并按当前曲目 / 状态铺一遍。</summary>
        public void Enter(string? path, MediaTags? tags)
        {
            EnsureCard();

            _card!.ShowTrack(path, tags);
            _card.Visible = true;
            _card.BringToFront();

            ApplyPalette(_host.Palette);
            UpdateTransportState(_host.HasMedia, _host.IsPlaying, _host.PlaylistCount > 0);

            Layout();
        }

        /// <summary>离开音频窗口：卡片收起来（不销毁，回来时不用重建）。</summary>
        public void Exit()
        {
            if (_card != null) _card.Visible = false;
        }

        /// <summary>把卡片摆成"视频面板去掉传输条"的矩形，并让卡片内部重排一次。</summary>
        public void Layout()
        {
            if (_card == null || !_card.Visible) return;

            // 折叠状态下 SplitContainer 不随容器变窄重算 Panel1（实测 Width=581 时 Panel1 仍是 1171）：
            // 折叠切换一次逼它按当前宽度重排。
            var main = _host.MainSplit;

            if (main.Panel2Collapsed && main.Panel1.Width != main.Width)
            {
                main.Panel2Collapsed = false;
                main.Panel2Collapsed = true;
            }

            var panel = _host.VideoSplit.Panel1;
            var width = panel.ClientSize.Width;
            var height = panel.ClientSize.Height - _host.TransportPanel.Height;
            if (width <= 0 || height <= 0) return;

            var bounds = new Rectangle(0, 0, width, Math.Max(0, height));
            if (_card.Bounds != bounds) _card.Bounds = bounds;

            _card.PerformLayout();
        }

        // =====================================================================
        // 迷你模式
        // =====================================================================

        /// <summary>进迷你模式：保证小卡片在，并铺上当前曲目 / 进度 / 状态。</summary>
        public void EnterMini(string? path, MediaTags? tags)
        {
            EnsureMini();

            _mini!.ShowTrack(path, tags);
            _mini.SetProgress(_host.PositionMilliseconds, _host.LengthMilliseconds);
            _mini.Visible = true;
            _mini.BringToFront();

            ApplyPalette(_host.Palette);
            UpdateTransportState(_host.HasMedia, _host.IsPlaying, _host.PlaylistCount > 0);

            LayoutMini();
        }

        /// <summary>离开迷你模式：小卡片收起来（不销毁）。</summary>
        public void ExitMini()
        {
            if (_mini != null) _mini.Visible = false;
        }

        /// <summary>把迷你卡片铺满整个画面区（迷你模式里传输条是藏起来的）。</summary>
        public void LayoutMini()
        {
            if (_mini == null || !_mini.Visible) return;

            var panel = _host.VideoSplit.Panel1;
            var bounds = new Rectangle(0, 0, panel.ClientSize.Width, panel.ClientSize.Height);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            if (_mini.Bounds != bounds) _mini.Bounds = bounds;

            _mini.PerformLayout();
        }

        /// <summary>迷你窗口该在哪儿、多大：优先用上次记住的，否则贴工作区右下角。</summary>
        public Rectangle ResolveMiniBounds()
        {
            var form = _host.Window;
            var settings = _host.Settings;
            var area = Screen.FromControl(form).WorkingArea;
            var minimum = MiniMinimumSize;

            var width = Math.Max(minimum.Width, Scaled(LogicalMiniWidth));
            var height = Math.Max(minimum.Height, Scaled(LogicalMiniHeight));

            var hasSaved = settings.MiniWindowWidth > 0 && settings.MiniWindowHeight > 0;

            if (hasSaved)
            {
                width = Math.Max(minimum.Width, settings.MiniWindowWidth);
                height = Math.Max(minimum.Height, settings.MiniWindowHeight);
            }

            int x;
            int y;

            if (hasSaved)
            {
                x = settings.MiniWindowX;
                y = settings.MiniWindowY;
            }
            else
            {
                var margin = Scaled(LogicalMiniMargin);
                x = area.Right - width - margin;
                y = area.Bottom - height - margin;
            }

            x = Math.Min(Math.Max(x, area.Left), Math.Max(area.Left, area.Right - width));
            y = Math.Min(Math.Max(y, area.Top), Math.Max(area.Top, area.Bottom - height));

            return new Rectangle(x, y, width, height);
        }

        /// <summary>把迷你窗口的位置大小写回设置。</summary>
        public void CaptureMiniGeometry()
        {
            var form = _host.Window;
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            _host.Settings.MiniWindowX = bounds.X;
            _host.Settings.MiniWindowY = bounds.Y;
            _host.Settings.MiniWindowWidth = bounds.Width;
            _host.Settings.MiniWindowHeight = bounds.Height;
        }

        // =====================================================================
        // 两张卡片共用的推送
        // =====================================================================

        /// <summary>换一首（或清空：传 <c>null</c>）。</summary>
        public void ShowTrack(string? path, MediaTags? tags)
        {
            _card?.ShowTrack(path, tags);
            _mini?.ShowTrack(path, tags);
        }

        /// <summary>卡片底部那一行当前歌词；<c>null</c> / 空串 = 不显示（迷你卡片不显示歌词）。</summary>
        public void SetLyricLine(string? text)
        {
            if (_card is { Visible: true }) _card.SetLyricLine(text);
        }

        public void ApplyPalette(ThemePalette palette)
        {
            _card?.ApplyPalette(palette);
            _mini?.ApplyPalette(palette);
        }

        public void UpdateTransportState(bool hasMedia, bool isPlaying, bool hasItems)
        {
            _card?.SetTransportState(hasMedia, isPlaying, hasItems);
            _mini?.SetTransportState(hasMedia, isPlaying, hasItems);
        }

        /// <summary>进度：只有迷你卡片显示进度（音频卡片用的是窗口底部那条传输条）。</summary>
        public void UpdateProgress(long timeMilliseconds, long lengthMilliseconds)
        {
            if (_mini is { Visible: true }) _mini.SetProgress(timeMilliseconds, lengthMilliseconds);
        }

        /// <summary>界面计时器每 200 ms 顺手对一次卡片尺寸（尺寸对的时候只做一次比较）。</summary>
        public void Heartbeat()
        {
            if (_card is { Visible: true }) Layout();
            if (_mini is { Visible: true }) LayoutMini();
        }

        /// <summary>这一首现在该用多大、摆在哪儿（音频窗口）：优先用上次记住的，其次按当前窗口居中。</summary>
        public Rectangle ResolveBounds()
        {
            var form = _host.Window;
            var settings = _host.Settings;
            var area = Screen.FromControl(form).WorkingArea;
            var minimum = MinimumSize;

            var width = Math.Max(minimum.Width, Scaled(LogicalWidth));
            var height = Math.Max(minimum.Height, Scaled(LogicalHeight));

            var hasSaved = settings.AudioWindowWidth > 0 && settings.AudioWindowHeight > 0;

            if (hasSaved)
            {
                width = Math.Max(minimum.Width, settings.AudioWindowWidth);
                height = Math.Max(minimum.Height, settings.AudioWindowHeight);
            }

            var anchor = hasSaved
                ? new Point(settings.AudioWindowX, settings.AudioWindowY)
                : new Point(
                    form.Bounds.X + (form.Bounds.Width - width) / 2,
                    form.Bounds.Y + (form.Bounds.Height - height) / 3);

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

        /// <summary>把音频窗口的位置大小写回设置（只在窗体当时真的是音频窗口时调用）。</summary>
        public void CaptureGeometry()
        {
            var form = _host.Window;
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            _host.Settings.AudioWindowX = bounds.X;
            _host.Settings.AudioWindowY = bounds.Y;
            _host.Settings.AudioWindowWidth = bounds.Width;
            _host.Settings.AudioWindowHeight = bounds.Height;
        }

        public void Dispose()
        {
            _mini?.Dispose();
            _mini = null;

            _card?.Dispose();
            _card = null;
        }

        private void EnsureCard()
        {
            if (_card != null) return;

            // 刻意不用 Dock=Fill：尺寸由 Layout() 显式给，避免"隐藏期间排一次、之后不再重排"
            _card = new NowPlayingView { Visible = false };

            _host.VideoHost.Controls.Add(_card);

            // 盖在画面之上、占位标签之下：占位标签是最前面那个（DragDrop 提示），别把它挡住
            _host.VideoHost.Controls.SetChildIndex(_card, 1);

            // ★ 真正可靠的触发点：面板自己的尺寸变了（它在窗体布局之后才换尺寸，
            //   挂窗体 OnResize 时读到的可能还是旧宽度）
            _host.VideoHost.SizeChanged += (s, e) => Layout();

            // 卡片里的控件条接到主窗口现成的命令上（和工具栏、托盘、任务栏按钮同一个入口）
            _card.Transport.BindCommands(_host.ExecuteCommand);
        }

        private void EnsureMini()
        {
            if (_mini != null) return;

            _mini = new MiniPlayerView { Visible = false };

            _host.VideoHost.Controls.Add(_mini);
            _host.VideoHost.Controls.SetChildIndex(_mini, 1);

            _host.VideoHost.SizeChanged += (s, e) => LayoutMini();

            _mini.BindCommands(_host.ExecuteCommand);
            _mini.SeekRequested += milliseconds => _host.SeekTo(milliseconds);
            _mini.RestoreRequested += (s, e) => MiniRestoreRequested?.Invoke(this, EventArgs.Empty);
        }

        private int Scaled(int logical) => (int)Math.Round(logical * _host.Window.DeviceDpi / 96.0);

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
