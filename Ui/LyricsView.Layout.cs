using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

// 布局：折行与行高缓存、最大滚动量、当前句的目标滚动位置。

namespace 播放器.Ui
{
    internal sealed partial class LyricsView
    {
        // ---- 布局 -------------------------------------------------------------

        private float DpiScale => DeviceDpi / 96f;

        private int LineGap => (int)Math.Round(10 * DpiScale);

        private float MaxScroll
        {
            get
            {
                var overflow = _contentHeight - ClientSize.Height;
                return overflow > 0 ? overflow : 0f;
            }
        }

        internal void EnsureLayout()
        {
            var width = ClientSize.Width - Padding.Horizontal;
            if (width <= 0)
            {
                _boxes.Clear();
                _contentHeight = 0;
                return;
            }

            if (!_layoutDirty && _layoutWidth == width) return;

            _boxes.Clear();
            _layoutWidth = width;
            _layoutDirty = false;

            var font = Font;
            var top = Padding.Top;

            if (_document.IsSynchronized)
            {
                var lines = _document.Lines;

                for (var i = 0; i < lines.Count; i++)
                {
                    var text = string.IsNullOrEmpty(lines[i].Text) ? "♪" : lines[i].Text;
                    var height = MeasureWrapped(text, font, width);

                    _boxes.Add(new LineBox { Index = i, Top = top, Height = height, Time = lines[i].Time });
                    top += height + LineGap;
                }
            }
            else if (!_document.IsEmpty)
            {
                var height = MeasureWrapped(_document.PlainText, font, width);
                _boxes.Add(new LineBox { Index = 0, Top = top, Height = height });
                top += height;
            }

            _contentHeight = top + Padding.Bottom;

            // 布局变了以后原来的滚动位置可能越界
            _target = Math.Clamp(_target, 0f, MaxScroll);
            _scroll = Math.Clamp(_scroll, 0f, MaxScroll);
        }

        private int MeasureWrapped(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return font.Height;

            var size = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), WrapFlags);
            return Math.Max(font.Height, size.Height);
        }

        private float FocusTargetFor(int index)
        {
            if (index < 0 || index >= _boxes.Count) return 0f;

            var box = _boxes[index];
            var desired = box.Top + box.Height / 2f - ClientSize.Height * FocusRatio;
            return Math.Clamp(desired, 0f, MaxScroll);
        }
    }
}
