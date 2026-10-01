using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 侧栏与播放列表的「脱离主窗口」功能。
    /// <para>
    /// 两者都能停靠在主窗口里，也都能搬进一个独立的窗口单独摆放（侧栏的悬浮窗归主窗口所有，
    /// 所以总会浮在主窗口之上，可以叠在画面上或拖到第二块屏幕）。
    /// </para>
    /// <para>
    /// 实现上就是<b>把控件换个父容器</b>：WinForms 里 <c>Controls.Add</c> 会自动把它从旧父级摘下来，
    /// 停靠方式、主题、事件订阅都不受影响，所以"分离/归位"只是搬一次家。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private const int FloatingWindowMinimumWidth = 220;
        private const int FloatingWindowMinimumHeight = 160;

        /// <summary>拖到离主窗口多近就贴上去（设计像素）。</summary>
        private const int SnapAttachDistance = 18;

        /// <summary>
        /// 已经贴住之后，要拖开多远才脱离（设计像素）。
        /// <para>比"贴上去"的距离大：两个阈值相同的话，拖到临界点会反复贴上又掉下来。</para>
        /// </summary>
        private const int SnapReleaseDistance = 40;

        /// <summary>沿贴合方向上至少要有这么多重叠，才算"挨着这条边"（设计像素）。</summary>
        private const int SnapMinimumOverlap = 32;

        /// <summary>两个窗口贴在同一条边上时间隔多少（设计像素）。</summary>
        private const int SnapStackGap = 6;

        /// <summary>四条边，用于挑最近的一条。</summary>
        private static readonly WindowSnapEdge[] AllSnapEdges =
        {
            WindowSnapEdge.Left, WindowSnapEdge.Right, WindowSnapEdge.Top, WindowSnapEdge.Bottom
        };

        private Form? _sidebarWindow;
        private Form? _playlistWindow;

        /// <summary>只读入口：分离出去的侧栏窗口（给冒烟测试用）。</summary>
        internal Form? SidebarWindow => _sidebarWindow;

        private WindowSnapEdge _sidebarSnap = WindowSnapEdge.None;

        /// <summary>只读入口：侧栏悬浮窗贴在哪条边上（给冒烟测试用）。</summary>
        internal WindowSnapEdge SidebarSnap => _sidebarSnap;
        private WindowSnapEdge _playlistSnap = WindowSnapEdge.None;
        private int _sidebarSnapOffset;
        private int _playlistSnapOffset;

        /// <summary>正在由程序摆放悬浮窗，忽略随之而来的 Move 通知（否则会自己触发自己）。</summary>
        private bool _adjustingFloating;

        // =====================================================================
        // 侧栏
        // =====================================================================

        /// <summary>分离 / 归位侧栏。</summary>
        private void SetSidebarFloating(bool floating)
        {
            _settings.SidebarFloating = floating;

            if (_menuViewSidebarFloating != null && _menuViewSidebarFloating.Checked != floating)
                _menuViewSidebarFloating.Checked = floating;

            if (floating) DetachSidebar();
            else DockSidebar();

            // 分离之后"显示侧栏"控制的是那个窗口
            ApplySidebarVisibility(_settings.ShowSidebar);
            SetStatus(floating ? "侧栏已分离为悬浮窗" : "侧栏已归位");
        }

        private void DetachSidebar()
        {
            if (_sidebarWindow != null) return;

            splitVideo.Panel2.Controls.Remove(sidebarPanel);
            splitVideo.Panel2Collapsed = true;

            _sidebarWindow = CreateFloatingWindow(
                "播放器 · 侧栏",
                _settings.SidebarWindowX,
                _settings.SidebarWindowY,
                _settings.SidebarWindowWidth,
                _settings.SidebarWindowHeight,
                new Size(320, 460));

            _sidebarWindow.FormClosing += OnSidebarWindowClosing;
            _sidebarWindow.Controls.Add(sidebarPanel);

            _sidebarSnap = _settings.SidebarSnap;
            _sidebarSnapOffset = _settings.SidebarSnapOffset;
            AttachSnapHandlers(_sidebarWindow);

            if (_settings.ShowSidebar) _sidebarWindow.Show(this);

            ApplyFloatingWindowTheme(_sidebarWindow);

            // 位置要在显示之后才最终确定：如果上次是吸附状态，
            // 这里按主窗口当前的位置重新贴回去。
            LayoutAttachedWindows();
        }

        private void DockSidebar()
        {
            var window = _sidebarWindow;
            if (window == null) return;

            RememberFloatingBounds(
                window,
                (x, y, w, h) =>
                {
                    _settings.SidebarWindowX = x;
                    _settings.SidebarWindowY = y;
                    _settings.SidebarWindowWidth = w;
                    _settings.SidebarWindowHeight = h;
                },
                edge => _settings.SidebarSnap = edge,
                _sidebarSnap);

            // 归位之后就没有"悬浮窗"了，吸附状态一并清掉。
            ForgetSnap(isSidebar: true);

            // 先断开引用与事件，避免 Dispose 时的 FormClosing 又绕回来
            _sidebarWindow = null;
            window.FormClosing -= OnSidebarWindowClosing;
            DetachSnapHandlers(window);

            window.Controls.Remove(sidebarPanel);
            window.Hide();
            window.Dispose();

            splitVideo.Panel2.Controls.Add(sidebarPanel);
            splitVideo.Panel2Collapsed = !_settings.ShowSidebar;

            ApplyPanelLayout();
        }

        private void OnSidebarWindowClosing(object? sender, FormClosingEventArgs e)
        {
            // 只有用户自己点 ✕ 才归位；主窗口关闭时（FormOwnerClosing）要放行
            if (e.CloseReason != CloseReason.UserClosing) return;

            e.Cancel = true;
            SetSidebarFloating(false);
        }

        // =====================================================================
        // 播放列表
        // =====================================================================

        /// <summary>分离 / 归位播放列表。</summary>
        private void SetPlaylistFloating(bool floating)
        {
            _settings.PlaylistFloating = floating;

            if (_menuViewPlaylistFloating != null && _menuViewPlaylistFloating.Checked != floating)
                _menuViewPlaylistFloating.Checked = floating;

            if (floating) DetachPlaylist();
            else DockPlaylist();

            ApplyPlaylistVisibility(_settings.ShowPlaylist);
            SetStatus(floating ? "播放列表已分离为独立窗口" : "播放列表已归位");
        }

        private void DetachPlaylist()
        {
            if (_playlistWindow != null) return;

            splitMain.Panel2.Controls.Remove(playlistHost);
            splitMain.Panel2Collapsed = true;

            _playlistWindow = CreateFloatingWindow(
                "播放器 · 播放列表",
                _settings.PlaylistWindowX,
                _settings.PlaylistWindowY,
                _settings.PlaylistWindowWidth,
                _settings.PlaylistWindowHeight,
                new Size(360, 520));

            _playlistWindow.FormClosing += OnPlaylistWindowClosing;
            _playlistWindow.Controls.Add(playlistHost);

            _playlistSnap = _settings.PlaylistSnap;
            _playlistSnapOffset = _settings.PlaylistSnapOffset;
            AttachSnapHandlers(_playlistWindow);

            if (_settings.ShowPlaylist) _playlistWindow.Show(this);

            ApplyFloatingWindowTheme(_playlistWindow);
            LayoutAttachedWindows();
        }

        private void DockPlaylist()
        {
            var window = _playlistWindow;
            if (window == null) return;

            RememberFloatingBounds(
                window,
                (x, y, w, h) =>
                {
                    _settings.PlaylistWindowX = x;
                    _settings.PlaylistWindowY = y;
                    _settings.PlaylistWindowWidth = w;
                    _settings.PlaylistWindowHeight = h;
                },
                edge => _settings.PlaylistSnap = edge,
                _playlistSnap);

            ForgetSnap(isSidebar: false);

            _playlistWindow = null;
            window.FormClosing -= OnPlaylistWindowClosing;
            DetachSnapHandlers(window);

            window.Controls.Remove(playlistHost);
            window.Hide();
            window.Dispose();

            splitMain.Panel2.Controls.Add(playlistHost);
            splitMain.Panel2Collapsed = !_settings.ShowPlaylist;

            ApplyPanelLayout();
            AdjustPlaylistColumns();
        }

        private void OnPlaylistWindowClosing(object? sender, FormClosingEventArgs e)
        {
            if (e.CloseReason != CloseReason.UserClosing) return;

            e.Cancel = true;
            SetPlaylistFloating(false);
        }

        // =====================================================================
        // 公共部分
        // =====================================================================

        private Form CreateFloatingWindow(
            string title, int x, int y, int width, int height, Size defaultSize)
        {
            var window = new Form
            {
                Text = title,
                Owner = this,
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                StartPosition = FormStartPosition.Manual,
                MinimumSize = new Size(FloatingWindowMinimumWidth, FloatingWindowMinimumHeight),
                Font = UiFonts.Sidebar,
                ClientSize = width > 0 && height > 0
                    ? new Size(width, height)
                    : defaultSize
            };

            // 位置：有保存过就照搬（但要确认还在屏幕上），否则贴在主窗口右侧
            var bounds = new Rectangle(x, y, window.Width, window.Height);

            if (width > 0 && height > 0 && IsVisibleOnAnyScreen(bounds))
                window.Location = new Point(x, y);
            else
                window.Location = SuggestFloatingLocation(window.Size);

            return window;
        }

        /// <summary>
        /// 第一次分离时给个不挡住主窗口的位置：贴主窗口右边，放不下就放左上角。
        /// <para>
        /// 留的间距（28）刻意比吸附距离（18）大：否则一分离就立刻被判成"贴在边上"，
        /// 用户还没动手就看到"已吸附"。想要吸附的话往主窗口方向拖一下即可。
        /// </para>
        /// </summary>
        private Point SuggestFloatingLocation(Size size)
        {
            var area = Screen.FromControl(this).WorkingArea;
            var gap = SnapPixels(28);

            var x = Right + gap;
            if (x + size.Width > area.Right) x = Math.Max(area.Left, area.Right - size.Width - gap);

            var y = Math.Max(area.Top, Top + 40);
            if (y + size.Height > area.Bottom) y = Math.Max(area.Top, area.Bottom - size.Height - gap);

            return new Point(x, y);
        }

        /// <summary>记录悬浮窗的位置与大小（用 RestoreBounds 避免最大化时的怪尺寸）。</summary>
        private static void SaveFloatingBounds(Form window, Action<int, int, int, int> assign)
        {
            var bounds = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            assign(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        /// <summary>
        /// 记录悬浮窗的几何信息与吸附状态。
        /// <para>
        /// 吸附状态下窗口的位置是<b>算出来</b>的（跟着主窗口走），所以坐标没意义，
        /// 真正要记的是"贴在哪条边、沿边偏了多少"；不吸附时才记坐标与大小。
        /// </para>
        /// </summary>
        private static void RememberFloatingBounds(
            Form window,
            Action<int, int, int, int> assignBounds,
            Action<WindowSnapEdge> assignEdge,
            WindowSnapEdge edge)
        {
            assignEdge(edge);

            // 吸附时也要记大小：用户可能把它拖宽了，重贴的时候得用这个宽度。
            SaveFloatingBounds(window, assignBounds);
        }

        // =====================================================================
        // 吸附到主窗口边上
        //
        // 行为：把悬浮窗拖到主窗口某条边附近（沿边方向有足够重叠）就自动贴齐，
        // 贴住之后<b>跟着主窗口一起移动 / 缩放</b>；拖开超过脱离距离就恢复自由。
        // 四条边都支持，按距离自动挑最近的一条。
        // =====================================================================

        private int SnapPixels(int design) => (int)Math.Round(design * DeviceDpi / 96.0);

        private void AttachSnapHandlers(Form window)
        {
            // Move 与 Resize 都要接：贴着右边时把窗口拖宽，它的左边界要往回走才能保持贴齐。
            window.Move += OnFloatingWindowGeometryChanged;
            window.Resize += OnFloatingWindowGeometryChanged;

            // 侧栏被隐藏再显示（"显示侧栏"开关）之后也要重新贴回主窗口边上，
            // 否则它会停在隐藏前那个已经过期的位置上。
            window.VisibleChanged += OnFloatingWindowVisibleChanged;
        }

        private void DetachSnapHandlers(Form window)
        {
            window.Move -= OnFloatingWindowGeometryChanged;
            window.Resize -= OnFloatingWindowGeometryChanged;
            window.VisibleChanged -= OnFloatingWindowVisibleChanged;
        }

        private void OnFloatingWindowVisibleChanged(object? sender, EventArgs e)
        {
            if (sender is Form { Visible: true }) LayoutAttachedWindows();
        }

        private bool IsSidebarWindow(Form window) => ReferenceEquals(window, _sidebarWindow);

        private void ForgetSnap(bool isSidebar)
        {
            if (isSidebar)
            {
                _sidebarSnap = WindowSnapEdge.None;
                _sidebarSnapOffset = 0;
                _settings.SidebarSnap = WindowSnapEdge.None;
                _settings.SidebarSnapOffset = 0;
            }
            else
            {
                _playlistSnap = WindowSnapEdge.None;
                _playlistSnapOffset = 0;
                _settings.PlaylistSnap = WindowSnapEdge.None;
                _settings.PlaylistSnapOffset = 0;
            }
        }

        /// <summary>
        /// 用户在拖悬浮窗（或改它的大小）。重新判断该不该吸附，并立刻贴齐。
        /// </summary>
        private void OnFloatingWindowGeometryChanged(object? sender, EventArgs e)
        {
            if (_adjustingFloating || sender is not Form window) return;
            if (_isFullscreen || !window.Visible) return;
            if (window.WindowState != FormWindowState.Normal) return;

            var isSidebar = IsSidebarWindow(window);
            var current = isSidebar ? _sidebarSnap : _playlistSnap;
            var next = ResolveSnapEdge(window, current);

            if (next != WindowSnapEdge.None)
            {
                // 用户可能是沿着贴合边在滑：先按当前位置更新偏移，
                // 否则它会一直弹回最初贴上去的那个位置。
                var offset = OffsetFor(Bounds, window.Bounds, next);

                if (isSidebar) _sidebarSnapOffset = offset;
                else _playlistSnapOffset = offset;
            }

            if (next != current)
            {
                if (isSidebar) _sidebarSnap = next;
                else _playlistSnap = next;

                SetStatus(next == WindowSnapEdge.None
                    ? (isSidebar ? "侧栏已脱离主窗口" : "播放列表已脱离主窗口")
                    : (isSidebar ? "侧栏" : "播放列表") + "已吸附到主窗口" + SnapEdgeName(next));
            }

            if (next == WindowSnapEdge.None) return;

            _adjustingFloating = true;
            try
            {
                LayoutAttachedWindowsCore();
            }
            finally
            {
                _adjustingFloating = false;
            }
        }

        /// <summary>主窗口移动 / 缩放后，把所有吸附中的悬浮窗带过去。</summary>
        private void LayoutAttachedWindows()
        {
            if (_adjustingFloating || _isFullscreen) return;
            if (WindowState == FormWindowState.Minimized) return;

            var hasAttached = false;
            foreach (var edge in new[] { _sidebarSnap, _playlistSnap })
            {
                if (edge != WindowSnapEdge.None)
                {
                    hasAttached = true;
                    break;
                }
            }

            if (!hasAttached) return;

            _adjustingFloating = true;
            try
            {
                LayoutAttachedWindowsCore();
            }
            finally
            {
                _adjustingFloating = false;
            }
        }

        /// <summary>按当前吸附状态摆放两个悬浮窗。调用方负责置 <see cref="_adjustingFloating"/>。</summary>
        private void LayoutAttachedWindowsCore()
        {
            ApplySnapPosition(_sidebarWindow, _sidebarSnap, _sidebarSnapOffset, _playlistWindow, _playlistSnap);
            ApplySnapPosition(_playlistWindow, _playlistSnap, _playlistSnapOffset, _sidebarWindow, _sidebarSnap);
        }

        /// <summary>判断应该吸附到哪条边；没有合适的就返回 <see cref="WindowSnapEdge.None"/>。</summary>
        private WindowSnapEdge ResolveSnapEdge(Form window, WindowSnapEdge current)
        {
            var main = Bounds;
            var target = window.Bounds;

            // 已经贴住时用"脱离距离"，否则用"吸附距离"。
            var limit = current == WindowSnapEdge.None
                ? SnapPixels(SnapAttachDistance)
                : SnapPixels(SnapReleaseDistance);

            var minimumOverlap = SnapPixels(SnapMinimumOverlap);

            var best = WindowSnapEdge.None;
            var bestGap = int.MaxValue;

            foreach (var edge in AllSnapEdges)
            {
                if (!TryMeasureSnapGap(main, target, edge, minimumOverlap, out var gap)) continue;
                if (gap > limit || gap >= bestGap) continue;

                bestGap = gap;
                best = edge;
            }

            return best;
        }

        /// <summary>
        /// 量出窗口离某条边的距离（窗口在边的哪一侧都取绝对值），
        /// 同时要求沿这条边方向有足够重叠——否则会出现"隔着老远斜着贴到角上"。
        /// </summary>
        private static bool TryMeasureSnapGap(
            Rectangle main, Rectangle window, WindowSnapEdge edge, int minimumOverlap, out int gap)
        {
            gap = 0;

            switch (edge)
            {
                case WindowSnapEdge.Left:
                case WindowSnapEdge.Right:
                {
                    var overlap = Math.Min(main.Bottom, window.Bottom) - Math.Max(main.Top, window.Top);
                    if (overlap < minimumOverlap) return false;

                    gap = edge == WindowSnapEdge.Left
                        ? Math.Abs(window.Right - main.Left)
                        : Math.Abs(window.Left - main.Right);
                    return true;
                }

                case WindowSnapEdge.Top:
                case WindowSnapEdge.Bottom:
                {
                    var overlap = Math.Min(main.Right, window.Right) - Math.Max(main.Left, window.Left);
                    if (overlap < minimumOverlap) return false;

                    gap = edge == WindowSnapEdge.Top
                        ? Math.Abs(window.Bottom - main.Top)
                        : Math.Abs(window.Top - main.Bottom);
                    return true;
                }

                default:
                    return false;
            }
        }

        /// <summary>贴在某条边上时，沿这条边相对主窗口左上角的偏移。</summary>
        private static int OffsetFor(Rectangle main, Rectangle window, WindowSnapEdge edge) =>
            edge is WindowSnapEdge.Left or WindowSnapEdge.Right
                ? window.Top - main.Top
                : window.Left - main.Left;

        private static string SnapEdgeName(WindowSnapEdge edge) => edge switch
        {
            WindowSnapEdge.Left => "左侧",
            WindowSnapEdge.Right => "右侧",
            WindowSnapEdge.Top => "上方",
            WindowSnapEdge.Bottom => "下方",
            _ => string.Empty
        };

        /// <summary>把一个吸附中的悬浮窗摆到位。</summary>
        private void ApplySnapPosition(
            Form? window,
            WindowSnapEdge edge,
            int offset,
            Form? other,
            WindowSnapEdge otherEdge)
        {
            if (window == null || edge == WindowSnapEdge.None || !window.Visible) return;
            if (window.WindowState != FormWindowState.Normal) return;

            var main = Bounds;
            var size = window.Size;
            var vertical = edge is WindowSnapEdge.Left or WindowSnapEdge.Right;

            int x;
            int y;

            switch (edge)
            {
                case WindowSnapEdge.Left:
                    x = main.Left - size.Width;
                    y = main.Top + offset;
                    break;

                case WindowSnapEdge.Right:
                    x = main.Right;
                    y = main.Top + offset;
                    break;

                case WindowSnapEdge.Top:
                    x = main.Left + offset;
                    y = main.Top - size.Height;
                    break;

                default:
                    x = main.Left + offset;
                    y = main.Bottom;
                    break;
            }

            // 另一个窗口也贴在同一条边上时，沿边让开，别叠在一起。
            if (other != null && otherEdge == edge && other.Visible)
            {
                var otherBounds = other.Bounds;
                var span = vertical ? size.Height : size.Width;
                var otherStart = vertical ? otherBounds.Top : otherBounds.Left;
                var otherSpan = vertical ? otherBounds.Height : otherBounds.Width;
                var along = vertical ? y : x;

                if (along < otherStart + otherSpan && otherStart < along + span)
                {
                    var gap = SnapPixels(SnapStackGap);

                    // 往"离另一个窗口更远"的那一侧让，尽量少挪动。
                    along = along >= otherStart
                        ? otherStart + otherSpan + gap
                        : otherStart - span - gap;
                }

                if (vertical) y = along;
                else x = along;
            }

            var target = ClampToWorkingArea(new Point(x, y), size);

            if (window.Location == target) return;

            window.Location = target;
        }

        /// <summary>
        /// 把位置钳进某块屏幕的工作区。
        /// <para>
        /// 主窗口最大化时，"贴在外侧"的位置会整个落到屏幕外。这时<b>不把窗口藏起来</b>，
        /// 而是钳进工作区当浮层——窗口突然消失比位置不完美更让人困惑。
        /// 吸附关系仍然记着，主窗口一还原它就回到外侧。
        /// </para>
        /// </summary>
        private Point ClampToWorkingArea(Point location, Size size)
        {
            var probe = new Rectangle(location, size);
            var screen = Screen.FromRectangle(probe) ?? Screen.FromControl(this);
            var area = screen.WorkingArea;

            var x = Math.Max(Math.Min(location.X, area.Right - size.Width), area.Left);
            var y = Math.Max(Math.Min(location.Y, area.Bottom - size.Height), area.Top);

            return new Point(x, y);
        }

        private void ApplyFloatingWindowTheme(Form window)
        {
            window.BackColor = _palette.PanelBack;
            window.ForeColor = _palette.ListFore;

            if (window.IsHandleCreated)
                NativeTheme.ApplyTitleBar(window.Handle, _palette.IsDark);
        }

        /// <summary>启动时按设置恢复"分离"状态；要在主窗口已经显示之后调用。</summary>
        private void RestoreFloatingWindows()
        {
            if (_settings.SidebarFloating) SetSidebarFloating(true);
            if (_settings.PlaylistFloating) SetPlaylistFloating(true);
        }

        /// <summary>主题变化时同步悬浮窗。</summary>
        private void ApplyThemeToFloatingWindows()
        {
            if (_sidebarWindow != null) ApplyFloatingWindowTheme(_sidebarWindow);
            if (_playlistWindow != null) ApplyFloatingWindowTheme(_playlistWindow);
        }

        /// <summary>
        /// 退出时也要把悬浮窗的位置大小与吸附状态记下来。
        /// <para>只在"归位"时记录是不够的——用户完全可能一直让它们飘着然后直接退出。</para>
        /// </summary>
        private void SaveFloatingWindowBounds()
        {
            if (_sidebarWindow != null)
            {
                RememberFloatingBounds(
                    _sidebarWindow,
                    (x, y, w, h) =>
                    {
                        _settings.SidebarWindowX = x;
                        _settings.SidebarWindowY = y;
                        _settings.SidebarWindowWidth = w;
                        _settings.SidebarWindowHeight = h;
                    },
                    edge => _settings.SidebarSnap = edge,
                    _sidebarSnap);

                // 吸附时的偏移也得存，否则下次启动会贴在边上但位置滑回 0。
                _settings.SidebarSnapOffset = _sidebarSnapOffset;
            }

            if (_playlistWindow != null)
            {
                RememberFloatingBounds(
                    _playlistWindow,
                    (x, y, w, h) =>
                    {
                        _settings.PlaylistWindowX = x;
                        _settings.PlaylistWindowY = y;
                        _settings.PlaylistWindowWidth = w;
                        _settings.PlaylistWindowHeight = h;
                    },
                    edge => _settings.PlaylistSnap = edge,
                    _playlistSnap);

                _settings.PlaylistSnapOffset = _playlistSnapOffset;
            }
        }

        /// <summary>全屏时把悬浮窗收起来，退出全屏再按设置放回去。</summary>
        private void SetFloatingWindowsVisible(bool visible)
        {
            if (!visible)
            {
                _sidebarWindow?.Hide();
                _playlistWindow?.Hide();
                return;
            }

            if (_sidebarWindow != null && _settings.ShowSidebar) _sidebarWindow.Show();
            if (_playlistWindow != null && _settings.ShowPlaylist) _playlistWindow.Show();

            // 全屏期间主窗口的尺寸整个变了，放回来之后要重新贴齐。
            LayoutAttachedWindows();
        }
    }
}
