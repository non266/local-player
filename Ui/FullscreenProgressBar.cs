using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 全屏时浮在画面底部的一条进度条（1.11.0 的 F14）。
    /// <para>
    /// <b>为什么是独立窗口，而不是画面里的一个控件</b>：视频是 libvlc 自己的子窗口，
    /// WinForms 控件盖不到它上面（1.8.0 做画面右键菜单时实测过，见 MainForm.VideoMenu.cs）。
    /// 独立窗口还顺带保证了一件更重要的：<b>视频区尺寸始终不变</b>——把进度条塞进主窗口
    /// 或显示 / 收起传输条，都会让 libvlc 重排一次输出，画面会跳一下。
    /// </para>
    /// <para>
    /// 行为：鼠标动就出现，2.5 秒没动静就收起；拖动过程中与鼠标悬停在它上面时不收；
    /// 松手才定位（和主界面那条进度条同一个手感）。它<b>不抢焦点</b>、不进任务栏 / Alt+Tab。
    /// </para>
    /// </summary>
    internal sealed class FullscreenProgressBar : Form
    {
        /// <summary>进度条的取值刻度（和主界面那条进度条一样：0～1000）。</summary>
        internal const int Resolution = 1000;

        /// <summary>多久没动静就收起（毫秒）。</summary>
        private const int IdleMilliseconds = 2500;

        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;

        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private static readonly IntPtr HwndTopMost = new IntPtr(-1);

        private readonly Label _current;
        private readonly Label _total;
        private readonly FlatSlider _slider;
        private readonly Timer _idle;

        private long _length;
        private bool _dragging;
        private bool _hovering;
        private long _lastActivityTicks;
        private float _scale = 1f;
        private Font? _textFont;

        /// <summary>松手要跳到的位置（毫秒）。由主窗体接过去调 <c>_engine.SeekTo</c>。</summary>
        public event Action<long>? SeekRequested;

        public FullscreenProgressBar()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 24, 28);

            _current = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "00:00:00",
                BackColor = Color.Transparent,
                ForeColor = Color.Gainsboro
            };

            _total = new Label
            {
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Text = "--:--:--",
                BackColor = Color.Transparent,
                ForeColor = Color.Gainsboro
            };

            _slider = new FlatSlider
            {
                Minimum = 0,
                Maximum = Resolution,
                Value = 0
            };

            // 拖动时保持可见：松手前不能被空闲计时器收走。
            _slider.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _dragging = true;
                NoteActivity();
            };

            _slider.Scroll += (s, e) =>
            {
                NoteActivity();

                if (_length > 0)
                    _current.Text = Format(SliderMilliseconds);
            };

            _slider.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (!_dragging) return;

                _dragging = false;
                CommitSeek();
            };

            Controls.Add(_current);
            Controls.Add(_total);
            Controls.Add(_slider);

            AttachHover(this);
            AttachHover(_current);
            AttachHover(_total);
            AttachHover(_slider);

            _idle = new Timer { Interval = 200 };
            _idle.Tick += (s, e) => TickIdle();
        }

        /// <summary>不激活自己（全屏看片时弹出的东西绝不能把焦点带走）。</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;

                // TOOLWINDOW：不进 Alt+Tab；NOACTIVATE：点到它也不激活，键盘还留给主窗口。
                parameters.ExStyle |= WsExNoActivate | WsExToolWindow;

                return parameters;
            }
        }

        /// <summary>按当前主题上色。</summary>
        public void ApplyPalette(ThemePalette palette)
        {
            if (palette == null) return;

            BackColor = palette.TransportBack;

            foreach (var label in new[] { _current, _total })
                label.ForeColor = palette.TransportFore;

            _slider.BackColor = palette.TransportBack;
            _slider.TrackColor = palette.TrackBack;
            _slider.FillColor = palette.Accent;
            _slider.ThumbColor = palette.Accent;
            _slider.Invalidate();

            Invalidate();
        }

        /// <summary>
        /// 摆到屏幕底部：宽度最多占屏幕的 92%（两侧至少留 4%），高度固定、随 DPI 放大。
        /// </summary>
        public void FitTo(Rectangle screenBounds, int dpi)
        {
            var scale = Math.Max(1f, dpi / 96f);

            var height = (int)Math.Round(56 * scale);
            var margin = Math.Max(1, (int)Math.Round(28 * scale));
            var side = Math.Max((int)Math.Round(48 * scale), (int)Math.Round(screenBounds.Width * 0.04));

            var width = Math.Max((int)Math.Round(320 * scale), screenBounds.Width - side * 2);
            width = Math.Min(width, screenBounds.Width);

            var bounds = new Rectangle(
                screenBounds.X + (screenBounds.Width - width) / 2,
                screenBounds.Bottom - margin - height,
                width,
                height);

            Bounds = bounds;

            ApplyScale(scale);
            ApplyRoundedRegion(scale);
            LayoutChildren(scale);
        }

        /// <summary>推一次播放进度。</summary>
        public void UpdateProgress(long timeMilliseconds, long lengthMilliseconds)
        {
            _length = lengthMilliseconds > 0 ? lengthMilliseconds : 0;

            _total.Text = _length > 0 ? Format(_length) : "--:--:--";

            if (_dragging) return;

            _slider.Value = _length > 0
                ? (int)Math.Clamp(timeMilliseconds * Resolution / _length, 0, Resolution)
                : 0;

            _current.Text = Format(Math.Max(0, timeMilliseconds));
        }

        /// <summary>亮出来（并重新开始空闲计时）。</summary>
        public void ShowBar()
        {
            NoteActivity();

            if (!Visible)
            {
                Show();
                BringToTopWithoutActivating();
            }

            if (!_idle.Enabled) _idle.Start();
        }

        /// <summary>收起。</summary>
        public void HideBar()
        {
            _idle.Stop();

            if (Visible) Hide();
        }

        /// <summary>鼠标或拖动又动了：把"多久没动静"重新计时。</summary>
        public void NoteActivity()
        {
            _lastActivityTicks = Environment.TickCount64;
        }

        /// <summary>当前滑块对应的毫秒数（长度未知时为 0）。</summary>
        internal long SliderMilliseconds =>
            _length > 0 ? (long)(_length * (_slider.Value / (double)Resolution)) : 0;

        /// <summary>测试用：状态是否与界面一致。</summary>
        internal FlatSlider Slider => _slider;

        internal bool SliderDragging => _dragging;

        /// <summary>测试用：走一遍"松手定位"那条路（和鼠标抬起完全相同）。</summary>
        internal void CommitSeek()
        {
            if (_length <= 0) return;

            NoteActivity();
            SeekRequested?.Invoke(SliderMilliseconds);
        }

        /// <summary>测试用：把空闲计时推到超时，再走一遍计时器逻辑。</summary>
        internal void ForceIdleForTest()
        {
            _hovering = false;
            _dragging = false;
            _lastActivityTicks = 0;
            TickIdle();
        }

        private void TickIdle()
        {
            if (!Visible) return;

            // 拖动中 / 鼠标悬停在它上面：保持可见，并把空闲时间往后推。
            if (_dragging || _hovering)
            {
                NoteActivity();
                return;
            }

            if (Environment.TickCount64 - _lastActivityTicks >= IdleMilliseconds)
                HideBar();
        }

        private void AttachHover(Control control)
        {
            control.MouseEnter += (s, e) => _hovering = true;
            control.MouseLeave += (s, e) => _hovering = false;
        }

        /// <summary>浮到最上层但不激活。主窗口全屏时是置顶窗口，这里再显式提一次才稳。</summary>
        private void BringToTopWithoutActivating()
        {
            if (!IsHandleCreated) return;

            SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        private void ApplyScale(float scale)
        {
            if (Math.Abs(scale - _scale) < 0.01f && _textFont != null) return;

            _scale = scale;

            var old = _textFont;
            _textFont = new Font("Segoe UI", 9.5f * scale, FontStyle.Regular, GraphicsUnit.Point);

            _current.Font = _textFont;
            _total.Font = _textFont;

            old?.Dispose();
        }

        private void ApplyRoundedRegion(float scale)
        {
            if (Width <= 0 || Height <= 0) return;

            using var path = GdiHelpers.RoundedRect(
                new RectangleF(0, 0, Width, Height), 10f * scale);

            var old = Region;
            Region = new Region(path);
            old?.Dispose();
        }

        private void LayoutChildren(float scale)
        {
            if (Width <= 0 || Height <= 0) return;

            var pad = (int)Math.Round(18 * scale);
            var labelWidth = (int)Math.Round(82 * scale);
            var gap = (int)Math.Round(10 * scale);
            var sliderHeight = Math.Max(8, (int)Math.Round(28 * scale));

            _current.Bounds = new Rectangle(pad, 0, labelWidth, ClientSize.Height);
            _total.Bounds = new Rectangle(
                ClientSize.Width - pad - labelWidth, 0, labelWidth, ClientSize.Height);

            var sliderLeft = pad + labelWidth + gap;
            var sliderWidth = ClientSize.Width - sliderLeft - pad - labelWidth - gap;

            _slider.Bounds = new Rectangle(
                sliderLeft, (ClientSize.Height - sliderHeight) / 2, Math.Max(1, sliderWidth), sliderHeight);
        }

        private static string Format(long milliseconds) =>
            TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _idle.Stop();
                _idle.Dispose();
                _textFont?.Dispose();
                _textFont = null;
            }

            base.Dispose(disposing);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}
