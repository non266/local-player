using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 视频窗口：画面 + 侧栏 + 播放列表那一套外壳，以及它自己的几何。
    /// <para>
    /// 视频窗口是"基础形态"——另外两种形态都是从它出发、再收起一部分外壳得到的。
    /// 这里只放两件容易写歪的事：<b>它该在哪儿 / 多大</b>，以及<b>从简化形态回来时怎么还原</b>。
    /// 具体控件仍归主窗口（设计器建的），模块只按清单拨开关。
    /// </para>
    /// </summary>
    internal sealed class VideoWindowModule
    {
        /// <summary>视频窗口的设计尺寸，只在"从没保存过几何"时拿来兜底。</summary>
        private const int LogicalWidth = 1152;
        private const int LogicalHeight = 700;

        private readonly IWindowSurfaceHost _host;

        public VideoWindowModule(IWindowSurfaceHost host)
        {
            _host = host;
        }

        /// <summary>视频窗口的默认最小尺寸（从没保存过、也没进过音频窗口时兜底）。</summary>
        public Size DefaultMinimum => new Size(Scaled(LogicalWidth), Scaled(LogicalHeight));

        /// <summary>视频窗口"本该在哪儿"：没保存过就居中设计尺寸。</summary>
        public Rectangle ResolveBounds(Size minimum)
        {
            var settings = _host.Settings;
            var form = _host.Window;

            if (settings.WindowWidth > 0 && settings.WindowHeight > 0)
            {
                var min = minimum.IsEmpty ? DefaultMinimum : minimum;

                return new Rectangle(
                    settings.WindowX,
                    settings.WindowY,
                    Math.Max(settings.WindowWidth, min.Width),
                    Math.Max(settings.WindowHeight, min.Height));
            }

            var size = DefaultMinimum;
            var area = Screen.FromControl(form).WorkingArea;

            return new Rectangle(
                area.X + Math.Max(0, (area.Width - size.Width) / 2),
                area.Y + Math.Max(0, (area.Height - size.Height) / 2),
                size.Width,
                size.Height);
        }

        /// <summary>
        /// 从音频 / 迷你形态回到视频窗口：把简化形态收起来的那几件还原回去。
        /// <para>两栏按设置重新折叠（和退出全屏时同一套算法），再让面板重排一次。</para>
        /// </summary>
        public void RestoreChrome(bool toolStripVisible)
        {
            var settings = _host.Settings;

            _host.MainToolStrip.Visible = toolStripVisible;

            _host.MainSplit.Panel2Collapsed = !settings.ShowPlaylist || settings.PlaylistFloating;
            _host.VideoSplit.Panel2Collapsed = !settings.ShowSidebar || settings.SidebarFloating;

            _host.Placeholder.Visible = !_host.HasMedia;

            _host.ApplyPanelLayout();
        }

        /// <summary>把视频窗口的位置大小写回设置（最大化时记 RestoreBounds，不记满屏尺寸）。</summary>
        public void CaptureGeometry()
        {
            var form = _host.Window;
            var settings = _host.Settings;
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            settings.WindowMaximized = form.WindowState == FormWindowState.Maximized;
            settings.WindowX = bounds.X;
            settings.WindowY = bounds.Y;
            settings.WindowWidth = bounds.Width;
            settings.WindowHeight = bounds.Height;
        }

        private int Scaled(int logical) => (int)Math.Round(logical * _host.Window.DeviceDpi / 96.0);
    }
}
