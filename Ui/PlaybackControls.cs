using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 音频窗口卡片里的播放控件条：**上一首 / 播放暂停 / 下一首 / 停止**。
    /// <para>
    /// 它只负责"画"与"点"：四个动作由主窗体接到现成的方法上
    /// （<c>TogglePlayPause</c> / <c>PlayPrevious</c> / <c>PlayNext</c> / <c>StopPlayback</c>），
    /// 可用状态与播放/暂停图标由 <see cref="SetTransportState"/> 统一推——
    /// 和工具栏、托盘、桌面歌词是同一份逻辑，不在这里复制一遍。
    /// </para>
    /// <para>
    /// 用真的 <see cref="Button"/> 而不是自绘命中区：能 Tab 到、能 <c>PerformClick</c>、
    /// 冒烟里点得动；字形由 <see cref="PlaybackGlyphs"/> 按 DPI 现画成位图，换主题就重画。
    /// </para>
    /// </summary>
    internal sealed class PlaybackControls : Panel
    {
        /// <summary>按钮边长（逻辑像素）。</summary>
        private const int LogicalButtonSize = 40;

        /// <summary>按钮之间的间距（逻辑像素）。</summary>
        private const int LogicalGap = 12;

        /// <summary>图标边长（逻辑像素）。</summary>
        private const int LogicalIconSize = 20;

        private readonly ToolTip _tips = new ToolTip();
        private readonly Button _previous;
        private readonly Button _playPause;
        private readonly Button _next;
        private readonly Button _stop;

        private bool _playing;
        private Color _iconColor = Color.White;
        private Color _buttonBack = Color.Transparent;
        private Color _hoverBack = Color.Transparent;

        public PlaybackControls()
        {
            DoubleBuffered = true;
            BackColor = Color.Transparent;

            _previous = CreateButton("pcPrevious", "上一首");
            _playPause = CreateButton("pcPlayPause", "播放 / 暂停");
            _next = CreateButton("pcNext", "下一首");
            _stop = CreateButton("pcStop", "停止");

            _previous.Click += (s, e) => PreviousRequested?.Invoke(this, EventArgs.Empty);
            _playPause.Click += (s, e) => PlayPauseRequested?.Invoke(this, EventArgs.Empty);
            _next.Click += (s, e) => NextRequested?.Invoke(this, EventArgs.Empty);
            _stop.Click += (s, e) => StopRequested?.Invoke(this, EventArgs.Empty);
        }

        internal event EventHandler? PlayPauseRequested;

        internal event EventHandler? PreviousRequested;

        internal event EventHandler? NextRequested;

        internal event EventHandler? StopRequested;

        /// <summary>现在画的是不是"暂停"（也就是正在播）；给冒烟测试用。</summary>
        internal bool ShowsPause => _playing;

        /// <summary>播放 / 暂停按钮此刻该画哪个字形（给冒烟测试核对状态与图标是同一份）。</summary>
        internal PlaybackGlyph CurrentGlyph => _playing ? PlaybackGlyph.Pause : PlaybackGlyph.Play;

        internal Button PreviousButton => _previous;

        internal Button PlayPauseButton => _playPause;

        internal Button NextButton => _next;

        internal Button StopButton => _stop;

        internal IReadOnlyList<Button> Buttons => new[] { _previous, _playPause, _next, _stop };

        /// <summary>
        /// 按"有没有媒体 / 在不在播 / 列表空不空"更新可用状态与图标。
        /// <para>规则与工具栏一致：播放看"有媒体或有列表项"、停止看"有媒体"、上一首 / 下一首看"有列表项"。</para>
        /// </summary>
        public void SetTransportState(bool hasMedia, bool isPlaying, bool hasItems)
        {
            _playPause.Enabled = hasMedia || hasItems;
            _stop.Enabled = hasMedia;
            _previous.Enabled = hasItems;
            _next.Enabled = hasItems;

            SetPlaying(isPlaying);
        }

        /// <summary>切换"播放 / 暂停"图标（相等时什么都不做，避免每次计时都重画位图）。</summary>
        public void SetPlaying(bool playing)
        {
            if (_playing == playing) return;

            _playing = playing;
            UpdateIcons();
        }

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;

            _iconColor = palette.ListFore;
            _buttonBack = palette.PanelBack;
            _hoverBack = palette.MenuHoverBack;

            foreach (var button in Buttons)
            {
                button.BackColor = _buttonBack;
                button.FlatAppearance.MouseOverBackColor = _hoverBack;
                button.FlatAppearance.MouseDownBackColor = _hoverBack;
            }

            UpdateIcons();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄有了才有真实的 DeviceDpi（125% 下 40 逻辑像素是 50 物理像素）
            LayoutButtons();
            UpdateIcons();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            LayoutButtons();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tips.Dispose();

                foreach (var button in Buttons)
                {
                    var image = button.Image;
                    button.Image = null;
                    image?.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private Button CreateButton(string name, string tip)
        {
            var button = new Button
            {
                Name = name,
                Text = string.Empty,
                TabStop = true,
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                ImageAlign = ContentAlignment.MiddleCenter,
                BackColor = _buttonBack,
                Cursor = Cursors.Hand
            };

            button.FlatAppearance.BorderSize = 0;

            _tips.SetToolTip(button, tip);
            Controls.Add(button);

            return button;
        }

        private int Scaled(int logical) => (int)Math.Round(logical * DeviceDpi / 96f);

        private void LayoutButtons()
        {
            // 构造函数里一个个加按钮，中途也会触发排版
            if (_previous == null || _playPause == null || _next == null || _stop == null) return;

            var size = Math.Max(16, Scaled(LogicalButtonSize));
            var gap = Math.Max(0, Scaled(LogicalGap));

            var total = size * 4 + gap * 3;
            var x = Math.Max(0, (ClientSize.Width - total) / 2);
            var y = Math.Max(0, (ClientSize.Height - size) / 2);

            foreach (var button in Buttons)
            {
                button.SetBounds(x, y, size, size);
                x += size + gap;
            }
        }

        private void UpdateIcons()
        {
            var side = Math.Max(8, Scaled(LogicalIconSize));

            SetIcon(_previous, PlaybackGlyph.Previous, side);
            SetIcon(_playPause, _playing ? PlaybackGlyph.Pause : PlaybackGlyph.Play, side);
            SetIcon(_next, PlaybackGlyph.Next, side);
            SetIcon(_stop, PlaybackGlyph.Stop, side);
        }

        private void SetIcon(Button button, PlaybackGlyph glyph, int side)
        {
            var image = PlaybackGlyphs.Render(glyph, new Size(side, side), _iconColor);
            var old = button.Image;

            button.Image = image;
            old?.Dispose();
        }
    }
}
