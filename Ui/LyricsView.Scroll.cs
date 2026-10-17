using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

// 滚动动画：15 ms 一拍的缓动，以及用户滚轮之后的暂停跟随。

namespace 播放器.Ui
{
    internal sealed partial class LyricsView
    {
        // ---- 滚动动画 ---------------------------------------------------------

        private void StartAnimation()
        {
            if (!_animation.Enabled) _animation.Start();
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            EnsureLayout();

            // 用户手动滚动期间不抢位置；松手（或过了保持时间）后自动回到当前行
            if (_document.IsSynchronized && !IsUserScrolling)
                _target = FocusTargetFor(_activeIndex);

            var difference = _target - _scroll;

            if (Math.Abs(difference) < 0.5f)
            {
                _scroll = _target;
                _animation.Stop();
                Invalidate();
                return;
            }

            _scroll += difference * EaseFactor;
            Invalidate();
        }
    }
}
