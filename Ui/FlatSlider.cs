using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace 播放器.Ui
{
    /// <summary>
    /// 自绘的水平滑块，用来替换原生的 <see cref="TrackBar"/>。
    /// <para>
    /// 换掉它的原因有两个：原生轨道是系统白色，在深色主题下非常扎眼；
    /// 而且 <c>TrackBar.AutoSize</c> 默认为 true，会忽略设计高度（曾经因此盖住音量行）。
    /// 自绘之后轨道、已填充部分、圆点全部跟随主题，高度也完全可控。
    /// </para>
    /// <para>
    /// 对外接口刻意与 <see cref="TrackBar"/> 用到的子集保持一致
    /// （<c>Minimum</c> / <c>Maximum</c> / <c>Value</c> / <c>Scroll</c>），
    /// 这样窗体那边的逻辑不用改。
    /// </para>
    /// </summary>
    internal sealed class FlatSlider : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;
        private bool _hovering;
        private int _hoverValue = -1;
        private bool _keyboardEnabled;
        private int _keyboardStep = 1;
        private ToolTip? _toolTip;

        private Color _trackColor = Color.FromArgb(0x55, 0x55, 0x5C);
        private Color _fillColor = Color.FromArgb(0x0A, 0x84, 0xFF);
        private Color _thumbColor = Color.FromArgb(0x0A, 0x84, 0xFF);
        private int _trackThickness = 4;
        private int _thumbRadius = 7;

        public FlatSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);

            // 默认不抢焦点：传送栏上的进度条 / 音量条要交给窗体统一处理方向键
            // （否则焦点落在滑块上，快进快退就不灵了）。
            // 需要键盘操作的场合（均衡器的各个频段）由调用方打开 KeyboardEnabled。
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Size = DefaultSize;
        }

        /// <summary>用户操作改变了取值（拖动 / 点击 / 键盘）。程序化设置 <see cref="Value"/> 不会触发。</summary>
        public event EventHandler? Scroll;

        /// <summary>
        /// 是否接受键盘操作（方向键 / PageUp / PageDown / Home / End）。
        /// <para>
        /// 打开之后控件会进入 Tab 顺序并可以取得焦点。默认关闭：
        /// 传送栏那两条滑块依赖窗体级的方向键处理。
        /// </para>
        /// </summary>
        [DefaultValue(false)]
        public bool KeyboardEnabled
        {
            get => _keyboardEnabled;
            set
            {
                if (_keyboardEnabled == value) return;

                _keyboardEnabled = value;
                SetStyle(ControlStyles.Selectable, value);
                TabStop = value;

                if (value)
                {
                    AccessibleRole = AccessibleRole.Slider;
                    if (string.IsNullOrEmpty(AccessibleName)) AccessibleName = "滑块";
                }
                else
                {
                    AccessibleRole = AccessibleRole.Default;
                }

                Invalidate();
            }
        }

        /// <summary>键盘单步调整量（方向键一次改变多少）。</summary>
        [DefaultValue(1)]
        public int KeyboardStep
        {
            get => _keyboardStep;
            set => _keyboardStep = Math.Max(1, value);
        }

        [DefaultValue(0)]
        public int Minimum
        {
            get => _minimum;
            set
            {
                if (_minimum == value) return;
                _minimum = value;
                if (_maximum < _minimum) _maximum = _minimum;
                CoerceValue();
                Invalidate();
            }
        }

        [DefaultValue(100)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                var clamped = Math.Max(value, _minimum);
                if (_maximum == clamped) return;
                _maximum = clamped;
                CoerceValue();
                Invalidate();
            }
        }

        [DefaultValue(0)]
        public int Value
        {
            get => _value;
            set
            {
                var clamped = Math.Clamp(value, _minimum, Math.Max(_minimum, _maximum));
                if (_value == clamped) return;
                _value = clamped;
                Invalidate();
            }
        }

        /// <summary>未填充部分的轨道颜色。</summary>
        public Color TrackColor
        {
            get => _trackColor;
            set { _trackColor = value; Invalidate(); }
        }

        /// <summary>已填充部分（已播放进度 / 已选音量）的颜色。</summary>
        public Color FillColor
        {
            get => _fillColor;
            set { _fillColor = value; Invalidate(); }
        }

        public Color ThumbColor
        {
            get => _thumbColor;
            set { _thumbColor = value; Invalidate(); }
        }

        /// <summary>滑块圆点半径。</summary>
        [DefaultValue(7)]
        public int ThumbRadius
        {
            get => _thumbRadius;
            set { _thumbRadius = Math.Max(2, value); Invalidate(); }
        }

        /// <summary>悬停时是否用气泡显示当前位置对应的文字（例如时间）。</summary>
        [DefaultValue(false)]
        public bool ShowHoverTip { get; set; }

        /// <summary>把取值转换成气泡文字。返回空串则不显示。</summary>
        public Func<int, string>? HoverTipFormatter { get; set; }

        protected override Size DefaultSize => new Size(150, 30);

        // -----------------------------------------------------------------
        // 绘制
        // -----------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var background = new SolidBrush(BackColor))
                g.FillRectangle(background, ClientRectangle);

            var radius = EffectiveRadius();
            var left = (float)radius;
            var right = Width - radius;
            if (right <= left) return;

            var centerY = Height / 2f;
            var thickness = Math.Max(2f, Math.Min(_trackThickness, Height / 3f));
            var trackRect = new RectangleF(left, centerY - thickness / 2f, right - left, thickness);

            using (var trackBrush = new SolidBrush(_trackColor))
            using (var path = GdiHelpers.RoundedRect(trackRect, thickness / 2f))
            {
                g.FillPath(trackBrush, path);
            }

            var fraction = GetFraction();
            var thumbX = left + (right - left) * fraction;

            if (thumbX - left > 0.5f)
            {
                var fillRect = new RectangleF(left, trackRect.Top, thumbX - left, thickness);
                using (var fillBrush = new SolidBrush(_fillColor))
                using (var path = GdiHelpers.RoundedRect(fillRect, thickness / 2f))
                {
                    g.FillPath(fillBrush, path);
                }
            }

            var thumbRadius = _dragging || _hovering ? radius + 1f : radius;
            using (var thumbBrush = new SolidBrush(_thumbColor))
            {
                g.FillEllipse(thumbBrush, thumbX - thumbRadius, centerY - thumbRadius, thumbRadius * 2f, thumbRadius * 2f);
            }

            // 键盘操作时标出焦点，否则看不出方向键会作用在哪一条上。
            if (_keyboardEnabled && Focused)
            {
                var focus = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using var pen = new Pen(_thumbColor) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(pen, focus.X, focus.Y, focus.Width, focus.Height);
            }
        }

        // -----------------------------------------------------------------
        // 键盘
        // -----------------------------------------------------------------

        /// <summary>方向键等按键要在本控件内部消化，不能被窗体拿去当快捷键。</summary>
        protected override bool IsInputKey(Keys keyData) => _keyboardEnabled && (keyData & Keys.KeyCode) switch
        {
            Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End
                or Keys.PageUp or Keys.PageDown => true,
            _ => base.IsInputKey(keyData)
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_keyboardEnabled)
            {
                base.OnKeyDown(e);
                return;
            }

            // 用取值范围的十分之一当翻页步长，至少 1。
            var span = Math.Max(_minimum, _maximum) - _minimum;
            var page = Math.Max(1, span / 10);

            var target = e.KeyCode switch
            {
                Keys.Left or Keys.Down => _value - _keyboardStep,
                Keys.Right or Keys.Up => _value + _keyboardStep,
                Keys.PageDown => _value - page,
                Keys.PageUp => _value + page,
                Keys.Home => _minimum,
                Keys.End => Math.Max(_minimum, _maximum),
                _ => int.MinValue
            };

            if (target == int.MinValue)
            {
                base.OnKeyDown(e);
                return;
            }

            e.Handled = true;

            var clamped = Math.Clamp(target, _minimum, Math.Max(_minimum, _maximum));
            if (clamped == _value) return;

            _value = clamped;
            Invalidate();

            // 和鼠标拖动一样走 Scroll：调用方只订阅它，不该再分一套键盘路径。
            Scroll?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            if (_keyboardEnabled) Invalidate();
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            if (_keyboardEnabled) Invalidate();
        }

        // -----------------------------------------------------------------
        // 鼠标
        // -----------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // 先让外部的 MouseDown 处理器执行（主窗体靠它把"用户正在拖动"标志置起来），
            // 再更新取值，这样随后触发的 Scroll 不会被当成播放推进。
            base.OnMouseDown(e);

            if (e.Button != MouseButtons.Left) return;

            _dragging = true;
            Capture = true;
            SetValueFromX(e.X);
            HideTip();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                SetValueFromX(e.X);
            }
            else
            {
                if (!_hovering)
                {
                    _hovering = true;
                    Invalidate();
                }

                UpdateHoverTip(e.X);
            }

            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                Capture = false;
                SetValueFromX(e.X);
            }

            // 外部处理器（主窗体）在这里按最终取值做定位。
            base.OnMouseUp(e);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            // 拖到一半被别处抢走捕获（例如 Alt+Tab）时复位，避免状态卡住。
            if (_dragging && !Capture)
                _dragging = false;

            base.OnMouseCaptureChanged(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovering = false;

            // 只在"真的没有捕获"时才结束拖动：按住拖到控件外面（比如进度条下方）时，
            // 鼠标其实还在捕获中、还会继续把移动消息送进来——以前这里无条件清掉 _dragging，
            // 于是"拖出去之后就不跟手了"。
            if (_dragging && !Capture) _dragging = false;

            HideTip();
            Invalidate();
            base.OnMouseLeave(e);
        }

        // -----------------------------------------------------------------
        // 内部
        // -----------------------------------------------------------------

        private int EffectiveRadius() =>
            Math.Max(3, Math.Min(_thumbRadius, Math.Max(3, Height / 2 - 1)));

        private float GetFraction()
        {
            var span = _maximum - _minimum;
            if (span <= 0) return 0f;
            return Math.Clamp((_value - _minimum) / (float)span, 0f, 1f);
        }

        private int ValueFromX(int x)
        {
            var radius = EffectiveRadius();
            var left = (float)radius;
            var right = Width - radius;
            if (right <= left) return _value;

            var fraction = Math.Clamp((x - left) / (right - left), 0f, 1f);
            var span = _maximum - _minimum;
            return Math.Clamp(_minimum + (int)Math.Round(fraction * span), _minimum, _maximum);
        }

        private void SetValueFromX(int x)
        {
            var next = ValueFromX(x);
            if (next == _value) return;

            _value = next;
            Invalidate();

            // 只有用户操作才触发 Scroll，与 TrackBar 的语义一致，
            // 否则主窗体每 200ms 更新进度都会绕回来。
            Scroll?.Invoke(this, EventArgs.Empty);
        }

        private void CoerceValue()
        {
            var clamped = Math.Clamp(_value, _minimum, Math.Max(_minimum, _maximum));
            if (clamped == _value) return;
            _value = clamped;
        }

        private void UpdateHoverTip(int x)
        {
            if (!ShowHoverTip || HoverTipFormatter == null) return;

            var value = ValueFromX(x);
            if (value == _hoverValue) return;
            _hoverValue = value;

            var text = HoverTipFormatter(value);
            if (string.IsNullOrEmpty(text)) return;

            _toolTip ??= new ToolTip
            {
                ShowAlways = true,
                InitialDelay = 150,
                ReshowDelay = 80,
                AutoPopDelay = 2000
            };

            _toolTip.Show(text, this, x + 10, -6, 1500);
        }

        private void HideTip()
        {
            _hoverValue = -1;
            _toolTip?.Hide(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip?.Dispose();
                _toolTip = null;
            }

            base.Dispose(disposing);
        }
    }
}
