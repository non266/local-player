using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 桌面歌词窗口下面那条控制条：播放 / 暂停、上一首、下一首、停止。
    /// <para>
    /// 它的形状是被这个窗口自身的三条规矩逼出来的，不是随手画的：
    /// </para>
    /// <list type="bullet">
    ///   <item><b>锁定 = 鼠标穿透</b>（<c>WS_EX_TRANSPARENT</c> + 命中测试返回透明）：
    ///     锁定时它<b>完全不画</b>，窗口也跟着变矮——画出来却点不动比没有更糟。</item>
    ///   <item><b>窗口不抢焦点</b>（<c>NOACTIVATE</c>）：鼠标点得动，但键盘操作不了，
    ///     所以这里不给任何快捷键。</item>
    ///   <item><b>窗口跟着文字伸缩</b>：控制条<b>常驻</b>（永远占那一行，尺寸恒定、不抖），
    ///     只是鼠标没停在这块上时画得很淡，停上来才清楚。</item>
    /// </list>
    /// <para>
    /// ⚠ 这个窗口是逐像素透明的分层窗口，<b>放不了子控件</b>：按钮是自己画 + 自己命中测试。
    /// 画和命中用的是同一份布局（<see cref="ControlBounds"/>），不会各自跑偏。
    /// </para>
    /// </summary>
    internal sealed partial class DesktopLyricsWindow
    {
        /// <summary>控制条上的四个按钮；枚举值就是从左到右的顺序。</summary>
        internal enum LyricsControl
        {
            PlayPause = 0,
            Previous = 1,
            Next = 2,
            Stop = 3
        }

        private const int LyricsControlCount = 4;

        /// <summary>某个按钮被点到了。主窗体把它接到真正的播放命令上（和托盘菜单同一批）。</summary>
        internal event Action<LyricsControl>? ControlClicked;

        private bool _showControls = true;
        private bool _playing;

        /// <summary>鼠标当前停在哪一个按钮上（-1 = 不在任何按钮上）。</summary>
        private int _hoveredControl = -1;

        /// <summary>鼠标在哪一个按钮上按下了（-1 = 没有）。</summary>
        private int _pressedControl = -1;

        /// <summary>
        /// 这一次"松开"要不要吞掉。
        /// <para>
        /// 双击按钮时：WinForms 收到 <c>WM_LBUTTONDBLCLK</c> 会再抛一次 MouseDown，
        /// 于是那一下会变成"按了两次"（对播放/暂停等于白按，对下一首就是跳两首）。
        /// 双击本身已经是一次操作了，所以第二下不再算。
        /// </para>
        /// </summary>
        private bool _suppressNextClick;

        /// <summary>
        /// 最近一次"按下"落在客户区的哪个点。
        /// <para>
        /// 双击事件里要判断"这一点在不在按钮上"：<b>不能</b>去问实时光标位置——
        /// 光标可能在按下与双击之间被挪走（真人动鼠标、或者别的程序），那样判断就会翻脸。
        /// 双击的位置本来就是"按下时那个点"，记下来最准。
        /// </para>
        /// </summary>
        private Point _lastMousePoint = new Point(-1, -1);

        /// <summary>是否显示控制条（设置项，默认显示）。</summary>
        public bool ShowControls
        {
            get => _showControls;
            set
            {
                if (_showControls == value) return;

                _showControls = value;
                _hoveredControl = -1;
                _pressedControl = -1;
                ApplyContent();
            }
        }

        /// <summary>
        /// 当前是不是正在播放——决定第一个按钮画"播放"还是"暂停"。
        /// <para>由主窗体在刷新歌词位置的同一条路上推过来（和位置一样是每帧同步的状态）。</para>
        /// </summary>
        public bool Playing
        {
            get => _playing;
            set
            {
                if (_playing == value) return;

                _playing = value;
                Render();
            }
        }

        /// <summary>控制条此刻该占的高度；不显示或锁定时是 0（窗口会跟着变矮）。</summary>
        private int ControlsTotalHeight => ControlsVisible ? LineGap + ControlSize : 0;

        /// <summary>控制条需要的最小宽度（窗口太窄时把它撑开，免得按钮被裁掉）。</summary>
        private int ControlsTotalWidth =>
            LyricsControlCount * ControlSize + (LyricsControlCount - 1) * ControlGap;

        private bool ControlsVisible => _showControls && !_locked;

        private int ControlSize => Scaled(24);
        private int ControlGap => Scaled(10);

        /// <summary>两行文字加起来的高度（控制条跟在它下面）。</summary>
        private float ContentHeight => _currentHeight + (_hasNext ? LineGap + _nextHeight : 0);

        /// <summary>
        /// 文字底部的 y 坐标（含上内边距）。
        /// <para>控制条必须从这下面开始——它比"只有文字时窗口的高度"要靠上一点，
        /// 因为文字下面本来还有一段下内边距。给冒烟测试当基准用。</para>
        /// </summary>
        internal int ContentBottom => (int)Math.Ceiling(PaddingY + ContentHeight);

        /// <summary>
        /// 四个按钮的矩形（客户区坐标）。不显示（或锁定）时是空的——画与命中测试都用这一份。
        /// </summary>
        internal IReadOnlyList<Rectangle> ControlBounds
        {
            get
            {
                var bounds = new List<Rectangle>();
                if (!ControlsVisible) return bounds;

                var left = (ClientSize.Width - ControlsTotalWidth) / 2;
                var top = (int)Math.Ceiling(PaddingY + ContentHeight + LineGap);

                for (var i = 0; i < LyricsControlCount; i++)
                {
                    bounds.Add(new Rectangle(left + i * (ControlSize + ControlGap), top, ControlSize, ControlSize));
                }

                return bounds;
            }
        }

        /// <summary>
        /// 客户区坐标 <paramref name="point"/> 落在哪个按钮上；不在按钮上（或根本没显示）返回 <c>-1</c>。
        /// </summary>
        internal int ControlIndexAt(Point point)
        {
            var bounds = ControlBounds;

            for (var i = 0; i < bounds.Count; i++)
            {
                if (bounds[i].Contains(point)) return i;
            }

            return -1;
        }

        // ---- 鼠标：按下 / 松开 / 悬停 ------------------------------------------

        /// <summary>
        /// 按下了：落在按钮上就"按住"它并返回 <c>true</c>——这时<b>不要</b>开始拖动。
        /// <para>拖动是桌面歌词唯一的移动方式，不能被按钮吃掉；反过来点在按钮上也不该顺带把窗口拖走。</para>
        /// </summary>
        private bool TryPressControl(Point point)
        {
            // 新的一次按下：上一次双击留下的账已经了结
            _suppressNextClick = false;

            var index = ControlIndexAt(point);
            if (index < 0) return false;

            _pressedControl = index;
            Render();
            return true;
        }

        /// <summary>松开了：按下的和松开的是同一个按钮才算点中（和普通按钮的手感一致）。</summary>
        private void ReleaseControl(Point point)
        {
            var pressed = _pressedControl;
            _pressedControl = -1;

            if (pressed < 0) return;

            if (_suppressNextClick)
            {
                // 刚才那一下是双击的第二下：双击已经算过一次操作了，这里不再算。
                _suppressNextClick = false;
                Render();
                return;
            }

            var index = ControlIndexAt(point);
            Render();

            if (index == pressed) ControlClicked?.Invoke((LyricsControl)pressed);
        }

        /// <summary>更新"鼠标停在哪一个按钮上"（悬停计时器与鼠标移动都会调它）。</summary>
        private void UpdateHoveredControl(Point point)
        {
            var index = ControlsVisible ? ControlIndexAt(point) : -1;
            if (index == _hoveredControl) return;

            _hoveredControl = index;
            Render();
        }

        /// <summary>鼠标离开这块（或锁定、被拖动）时把按钮高亮收掉。</summary>
        private void ClearHoveredControl()
        {
            _pressedControl = -1;

            if (_hoveredControl < 0) return;

            _hoveredControl = -1;
            Render();
        }

        /// <summary>
        /// 双击歌词 = 播放 / 暂停（不占版面，也不需要额外的按钮）。
        /// <para>
        /// 双击落在按钮上时不是"再触发一次播放/暂停"，而是把这颗按钮的第二下吞掉
        /// （见 <see cref="_suppressNextClick"/>）：双击在按钮上应当只算一次点击。
        /// </para>
        /// </summary>
        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);

            if (_locked) return;

            if (ControlIndexAt(_lastMousePoint) >= 0)
            {
                _suppressNextClick = true;
                return;
            }

            ControlClicked?.Invoke(LyricsControl.PlayPause);
        }

        // ---- 绘制 -------------------------------------------------------------

        /// <summary>把控制条画进位图（由 <c>Render</c> 在文字之后调用）。</summary>
        private void DrawControls(Graphics g)
        {
            var bounds = ControlBounds;
            if (bounds.Count == 0) return;

            // 鼠标没停在这块上时画得很淡：常驻但不抢戏（尺寸恒定，所以不会因为悬停而抖）
            var color = _hovering || _dragging ? MainColor : Color.FromArgb(80, MainColor);

            for (var i = 0; i < bounds.Count; i++)
            {
                if (i == _pressedControl) FillControlBackground(g, bounds[i], 110);
                else if (i == _hoveredControl) FillControlBackground(g, bounds[i], 64);

                DrawControlGlyph(g, (LyricsControl)i, bounds[i], color);
            }
        }

        private void FillControlBackground(Graphics g, Rectangle box, int alpha)
        {
            using var path = GdiHelpers.RoundedRect(new RectangleF(box.X, box.Y, box.Width, box.Height), Scaled(6));
            using var brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));

            g.FillPath(brush, path);
        }

        /// <summary>按钮里的图形：自己画形状而不是用字体图标，DPI 变了也不会糊。</summary>
        private void DrawControlGlyph(Graphics g, LyricsControl control, Rectangle box, Color color)
        {
            using var brush = new SolidBrush(color);

            switch (control)
            {
                case LyricsControl.PlayPause when _playing:
                    DrawPauseBars(g, brush, box);
                    break;

                case LyricsControl.PlayPause:
                    DrawTriangle(g, brush, box, pointingRight: true);
                    break;

                case LyricsControl.Previous:
                    DrawDoubleTriangle(g, brush, box, pointingRight: false);
                    break;

                case LyricsControl.Next:
                    DrawDoubleTriangle(g, brush, box, pointingRight: true);
                    break;

                case LyricsControl.Stop:
                    {
                        var (left, top, right, bottom) = Inset(box, 0.30f);
                        g.FillRectangle(brush, left, top, right - left, bottom - top);
                        break;
                    }
            }
        }

        private void DrawTriangle(Graphics g, Brush brush, Rectangle box, bool pointingRight)
        {
            var (left, top, right, bottom) = Inset(box, 0.28f);
            var middle = (top + bottom) / 2f;

            g.FillPolygon(brush, pointingRight
                ? new[] { new PointF(left, top), new PointF(right, middle), new PointF(left, bottom) }
                : new[] { new PointF(right, top), new PointF(left, middle), new PointF(right, bottom) });
        }

        private void DrawPauseBars(Graphics g, Brush brush, Rectangle box)
        {
            var (left, top, right, bottom) = Inset(box, 0.30f);

            var width = (right - left);
            var bar = width * 0.34f;
            var gap = width * 0.32f;

            g.FillRectangle(brush, left, top, bar, bottom - top);
            g.FillRectangle(brush, left + bar + gap, top, bar, bottom - top);
        }

        /// <summary>上一首 / 下一首的"双三角"（和播放键同一个三角，画两个）。</summary>
        private void DrawDoubleTriangle(Graphics g, Brush brush, Rectangle box, bool pointingRight)
        {
            var (left, top, right, bottom) = Inset(box, 0.26f);
            var middle = (top + bottom) / 2f;

            var half = (right - left) / 2f;
            var gap = half * 0.16f;

            for (var i = 0; i < 2; i++)
            {
                var start = left + i * (half + gap);
                var tip = start + half - gap;

                g.FillPolygon(brush, pointingRight
                    ? new[] { new PointF(start, top), new PointF(tip, middle), new PointF(start, bottom) }
                    : new[] { new PointF(tip, top), new PointF(start, middle), new PointF(tip, bottom) });
            }
        }

        private static (float Left, float Top, float Right, float Bottom) Inset(Rectangle box, float ratio)
        {
            var dx = box.Width * ratio;
            var dy = box.Height * ratio;

            return (box.Left + dx, box.Top + dy, box.Right - dx, box.Bottom - dy);
        }
    }
}
