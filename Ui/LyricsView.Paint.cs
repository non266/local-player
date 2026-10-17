using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

// 绘制：高亮判定、同步歌词 / 纯文本 / 占位提示、右上角的偏移提示。

namespace 播放器.Ui
{
    internal sealed partial class LyricsView
    {
        // ---- 绘制 -------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);

            EnsureLayout();

            if (_document.IsEmpty)
            {
                DrawHint(g, "没有找到歌词");
                DrawOffsetHint(g);
                return;
            }

            if (_document.IsSynchronized)
                DrawSynchronized(g);
            else
                DrawPlainText(g);

            DrawOffsetHint(g);
        }

        /// <summary>
        /// 这一行算不算"当前句"。
        /// <para>
        /// 双语歌词里同一个时间戳有两行（原文 + 译文），它们<b>是同一句话</b>，要高亮一起高亮：
        /// 只认一行的话，另一行永远是暗的（以前认的是同时间戳的最后一行，原文那行就永远暗着）。
        /// </para>
        /// </summary>
        private bool IsActiveLine(int index)
        {
            if (index < 0 || _activeIndex < 0) return index == _activeIndex;
            if (index == _activeIndex) return true;

            var lines = _document.Lines;

            return index < lines.Count && _activeIndex < lines.Count &&
                   lines[index].Time == lines[_activeIndex].Time;
        }

        /// <summary>当前被高亮的那几行（双语歌词会是同一时间戳的两行）。给冒烟测试用。</summary>
        internal IReadOnlyList<int> HighlightedIndexes
        {
            get
            {
                var highlighted = new List<int>();

                for (var i = 0; i < _document.Lines.Count; i++)
                {
                    if (IsActiveLine(i)) highlighted.Add(i);
                }

                return highlighted;
            }
        }

        private void DrawSynchronized(Graphics g)
        {
            var lines = _document.Lines;
            var width = Math.Max(1, _layoutWidth);

            for (var i = 0; i < _boxes.Count; i++)
            {
                var box = _boxes[i];
                var y = box.Top - _scroll;

                // 视口外的直接跳过
                if (y > ClientSize.Height || y + box.Height < 0) continue;
                if (box.Index >= lines.Count) continue;

                var text = lines[box.Index].Text;
                if (string.IsNullOrEmpty(text)) text = "♪";

                var isActive = IsActiveLine(i);
                var isHover = i == _hoverIndex;

                var rect = new Rectangle(Padding.Left, (int)Math.Round(y), width, box.Height);

                if (isActive)
                {
                    // 当前行加一层淡淡的强调色底，位置感更明确
                    var highlight = new RectangleF(rect.X + 2, rect.Y + 1, rect.Width - 4, rect.Height - 2);
                    using var brush = new SolidBrush(_activeBackColor);
                    using var path = GdiHelpers.RoundedRect(highlight, 6f * DpiScale);
                    g.FillPath(brush, path);
                }

                var color = isActive ? _activeColor : isHover ? _hoverColor : _idleColor;
                var font = isActive ? ActiveFont : Font;

                TextRenderer.DrawText(g, text, font, rect, color, DrawFlags);
            }
        }

        private void DrawPlainText(Graphics g)
        {
            if (_boxes.Count == 0) return;

            var box = _boxes[0];
            var rect = new Rectangle(
                Padding.Left,
                (int)Math.Round(box.Top - _scroll),
                Math.Max(1, _layoutWidth),
                box.Height);

            TextRenderer.DrawText(
                g, _document.PlainText, Font, rect, _idleColor,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        private void DrawHint(Graphics g, string text)
        {
            TextRenderer.DrawText(
                g, text, Font, ClientRectangle, _hintColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        /// <summary>
        /// 右上角画一行淡淡的"当前歌词偏移"。
        /// <para>
        /// 底下垫一层不透明的背景色：这行字是画在歌词上面的，不垫的话和歌词叠在一起谁也看不清。
        /// 用背景色而不是强调色，是为了让它明显是"角落上的状态"而不是被高亮的歌词。
        /// </para>
        /// </summary>
        private void DrawOffsetHint(Graphics g)
        {
            if (_offsetHint == null) return;

            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

            var size = TextRenderer.MeasureText(g, _offsetHint, Font, new Size(int.MaxValue, int.MaxValue), flags);
            if (size.Width <= 0 || size.Height <= 0) return;

            var pad = (int)Math.Round(4 * DpiScale);
            var margin = (int)Math.Round(3 * DpiScale);

            var chip = new Rectangle(
                Math.Max(0, ClientSize.Width - size.Width - pad * 2 - margin),
                margin,
                size.Width + pad * 2,
                size.Height + pad);

            if (chip.Right <= 0 || chip.Bottom <= 0) return;

            using (var brush = new SolidBrush(BackColor))
                g.FillRectangle(brush, chip);

            TextRenderer.DrawText(
                g, _offsetHint, Font,
                new Rectangle(chip.X + pad, chip.Y + pad / 2, size.Width, size.Height),
                _hintColor, flags);
        }
    }
}
