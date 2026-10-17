using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

// 交互：命中哪一行、悬停时间气泡、点击跳转。

namespace 播放器.Ui
{
    internal sealed partial class LyricsView
    {
        // ---- 交互 -------------------------------------------------------------

        /// <summary>把控件坐标换算成行下标；不在任何一行上时返回 -1。</summary>
        private int IndexAt(int y)
        {
            if (_boxes.Count == 0) return -1;

            var position = y + _scroll;

            for (var i = 0; i < _boxes.Count; i++)
            {
                var box = _boxes[i];
                if (position >= box.Top && position <= box.Top + box.Height) return i;
            }

            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_hovering)
            {
                _hovering = true;
                _lastUserScrollTick = 0;    // 鼠标回到歌词上，恢复跟随
            }

            var index = _document.IsSynchronized ? IndexAt(e.Y) : -1;

            if (index != _hoverIndex)
            {
                _hoverIndex = index;
                Invalidate();
            }

            UpdateTimeTip(index, e.Location);

            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovering = false;

            if (_hoverIndex != -1)
            {
                _hoverIndex = -1;
                Invalidate();
            }

            HideTimeTip();

            Cursor = Cursors.Default;
            base.OnMouseLeave(e);
        }

        /// <summary>
        /// 鼠标停在一行上时，在光标旁边弹出这一句对应的时间点。
        /// <para>
        /// 只在<b>换行</b>时才重弹：同一行里跟着鼠标移动会让气泡不停闪，
        /// 而且气泡本身会挡住歌词。滚轮滚动、换歌词、移出控件都会把它收掉。
        /// </para>
        /// </summary>
        private void UpdateTimeTip(int index, Point location)
        {
            var text = TimeTipTextFor(index);

            if (text == null)
            {
                HideTimeTip();
                return;
            }

            if (index == _tipIndex) return;

            _tipIndex = index;

            // 先 Hide 再 Show：不这样在同一个控件上换文字时气泡位置不会更新。
            _timeTip.Hide(this);

            var offsetX = (int)Math.Round(14 * DpiScale);
            var offsetY = (int)Math.Round(20 * DpiScale);

            _timeTip.Show(text, this, location.X + offsetX, location.Y + offsetY, 5000);
        }

        private void HideTimeTip()
        {
            if (_tipIndex == -1) return;

            _tipIndex = -1;
            _timeTip.Hide(this);
        }

        /// <summary>
        /// 某一行悬停时要显示的文字；没有时间可显示时返回 <c>null</c>。
        /// <para>纯文本歌词（没有时间轴）与无效行都返回 <c>null</c>。</para>
        /// </summary>
        internal string? TimeTipTextFor(int index)
        {
            if (!_document.IsSynchronized) return null;
            if (index < 0 || index >= _boxes.Count) return null;

            return TimeFormatter.FormatWithHours(_boxes[index].Time);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _document.IsSynchronized)
            {
                var index = IndexAt(e.Y);
                if (index >= 0 && index < _boxes.Count)
                    SeekRequested?.Invoke(this, _boxes[index].Time);
            }

            base.OnMouseClick(e);
        }
    }
}
