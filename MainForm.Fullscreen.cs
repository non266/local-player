using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 全屏进度条（1.11.0 的 F14）：全屏时浮在画面底部的独立置顶窗口。
    /// <para>
    /// 这里是"什么时候出现 / 什么时候收起 / 拖到某个位置该跳到哪里"，画与摆放都在
    /// <see cref="FullscreenProgressBar"/> 里。为什么不是画面里的控件：见那个类的说明。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        private FullscreenProgressBar? _fullscreenBar;

        /// <summary>上一拍光标在哪：只有真的动过才点亮进度条，静止时不能一直闪。</summary>
        private Point _lastFullscreenPointer;

        /// <summary>测试用：全屏进度条（没进全屏 / 没媒体时为 <c>null</c>）。</summary>
        internal FullscreenProgressBar? FullscreenBar => _fullscreenBar;

        /// <summary>亮出全屏进度条（没有媒体或不在全屏时什么都不做）。</summary>
        private void ShowFullscreenBar()
        {
            if (!_isFullscreen || !_engine.HasMedia) return;

            if (_fullscreenBar == null)
            {
                _fullscreenBar = new FullscreenProgressBar { Owner = this };
                _fullscreenBar.SeekRequested += OnFullscreenBarSeek;
                _fullscreenBar.ApplyPalette(_palette);
            }

            _fullscreenBar.FitTo(Screen.FromControl(this).Bounds, DeviceDpi);
            _fullscreenBar.UpdateProgress(_engine.Time, _engine.Length);
            _fullscreenBar.ShowBar();
        }

        private void HideFullscreenBar() => _fullscreenBar?.HideBar();

        private void UpdateFullscreenBar(long timeMilliseconds, long lengthMilliseconds)
        {
            if (_fullscreenBar == null || !_fullscreenBar.Visible) return;

            _fullscreenBar.UpdateProgress(timeMilliseconds, lengthMilliseconds);
        }

        /// <summary>
        /// 光标动了一下（由 30 ms 的指针轮询调用）。
        /// <para>全屏时视频区收不到 WinForms 的鼠标消息，只能靠轮询；这里只负责"动过才点亮"。</para>
        /// </summary>
        internal void NotifyFullscreenPointer(Point screenPoint)
        {
            if (!_isFullscreen || !_engine.HasMedia) return;

            var screen = Screen.FromControl(this).Bounds;

            if (!screen.Contains(screenPoint))
            {
                HideFullscreenBar();
                return;
            }

            ShowFullscreenBar();
        }

        private void OnFullscreenBarSeek(long milliseconds)
        {
            if (!_engine.HasMedia) return;

            _engine.SeekTo(milliseconds);

            // 松手之后立刻把滑块 / 时间挪到目标位置，不然要等下一拍 200 ms 才跟手。
            UpdateProgress();
        }

        /// <summary>退出全屏 / 关窗时把那条窗口整个放掉（它有自己的计时器）。</summary>
        private void DisposeFullscreenBar()
        {
            HideFullscreenBar();

            if (_fullscreenBar == null) return;

            _fullscreenBar.SeekRequested -= OnFullscreenBarSeek;
            _fullscreenBar.Dispose();
            _fullscreenBar = null;
        }

        /// <summary>
        /// 主窗口不再活动（Alt+Tab、点了别的程序）就把进度条收起来：
        /// 它是置顶窗口，留着会盖在别人家的窗口上。
        /// </summary>
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);

            HideFullscreenBar();
        }
    }
}
