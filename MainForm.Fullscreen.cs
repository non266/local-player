using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 全屏入口的薄层：真正"进 / 出全屏、进度条出现与收起"在
    /// <see cref="Ui.FullscreenModule"/> 里。保留下来的只有测试用的只读属性与
    /// 几个委托方法（冒烟按这些名字断言）。
    /// </summary>
    public partial class MainForm : Form
    {
        /// <summary>测试用：现在是不是全屏（"双击画面"那条判据要它）。</summary>
        internal bool IsFullscreen => _surfaces.IsFullscreen;

        /// <summary>测试用：全屏进度条（没进全屏 / 没媒体时为 <c>null</c>）。</summary>
        internal FullscreenProgressBar? FullscreenBar => _surfaces.FullscreenBar;

        private void ToggleFullscreen() => _surfaces.ToggleFullscreen();

        internal void EnterFullscreen() => _surfaces.EnterFullscreen();

        internal void ExitFullscreen() => _surfaces.ExitFullscreen();

        /// <summary>
        /// 光标动了一下（由 30 ms 的指针轮询调用，测试也直接调它）。
        /// <para>全屏时视频区收不到 WinForms 的鼠标消息，只能靠轮询；"动过才点亮"在模块里。</para>
        /// </summary>
        internal void NotifyFullscreenPointer(Point screenPoint) => _surfaces.Fullscreen.NotifyPointer(screenPoint);

        /// <summary>
        /// 主窗口不再活动（Alt+Tab、点了别的程序）就把进度条收起来：
        /// 它是置顶窗口，留着会盖在别人家的窗口上。
        /// </summary>
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            _surfaces?.OnDeactivate();
        }
    }
}
