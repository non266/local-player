using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 迷你模式那张小卡片：封面缩略图 + 曲名 / 艺术家 + 一条进度 + 播放控件 + "还原"。
    /// <para>
    /// 与音频窗口的 <see cref="NowPlayingView"/> 是同一套路（盖在画面位置上的不透明面板，
    /// 视频 HWND 不销毁），但排版完全不同：窗口只有 440×160 左右，所以封面缩到边上、
    /// 文字压成两行、进度条与控件挤在同一列。所以没有硬套那个类，另画一份。
    /// </para>
    /// <para>
    /// 控件条与进度都接到命令层 / 模块：按钮走 <see cref="BindCommands"/>，进度松手才
    /// 抛 <see cref="SeekRequested"/>（拖动过程中不回写，否则滑块会跟播放时间打架）。
    /// 内部一律按当前尺寸算术排版（B9 的教训）。
    /// </para>
    /// </summary>
    internal sealed class MiniPlayerView : Panel
    {
        private const int LogicalPadding = 12;
        private const int LogicalGap = 12;
        private const int LogicalTitleHeight = 24;
        private const int LogicalArtistHeight = 18;
        private const int LogicalProgressHeight = 22;
        private const int LogicalTimeHeight = 16;
        private const int LogicalControlsHeight = 44;

        /// <summary>
        /// 进度 / 时间 / 控件这一组离卡片底边留多少（逻辑像素）。
        /// <para>刻意比四周的 <see cref="LogicalPadding"/> 小：这一组是"贴底操作区"，
        /// 留 12 会显得整体被顶起来，离底一条缝更像一张小卡片。</para>
        /// </summary>
        private const int LogicalBottomInset = 4;

        private const int LogicalRestoreWidth = 56;
        private const int LogicalRestoreHeight = 24;
        private const int LogicalCoverMax = 132;
        private const int LogicalCoverMin = 64;

        /// <summary>进度条内部精度（与主进度条一致）。</summary>
        private const int Resolution = 1000;

        private readonly CoverView _cover;
        private readonly Label _title;
        private readonly Label _artist;
        private readonly FlatSlider _progress;
        private readonly Label _position;
        private readonly Label _duration;
        private readonly PlaybackControls _controls;
        private readonly Button _restore;

        private long _length;
        private bool _dragging;

        public MiniPlayerView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Font = UiFonts.Resolve(10f, FontStyle.Regular);

            _cover = new CoverView();
            _title = CreateLabel(ContentAlignment.MiddleLeft, UiFonts.Resolve(11f, FontStyle.Bold));
            _artist = CreateLabel(ContentAlignment.MiddleLeft, UiFonts.Resolve(9.5f, FontStyle.Regular));

            _progress = new FlatSlider { Minimum = 0, Maximum = Resolution, Value = 0 };
            _position = CreateLabel(ContentAlignment.MiddleLeft, UiFonts.Resolve(9f, FontStyle.Regular));
            _duration = CreateLabel(ContentAlignment.MiddleRight, UiFonts.Resolve(9f, FontStyle.Regular));

            _controls = new PlaybackControls();

            _restore = new Button
            {
                Name = "miniRestore",
                Text = "还原",
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                TabStop = true,
                Cursor = Cursors.Hand
            };
            _restore.FlatAppearance.BorderSize = 0;
            _restore.Click += (s, e) => RestoreRequested?.Invoke(this, EventArgs.Empty);

            _progress.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _dragging = true;
            };

            _progress.Scroll += (s, e) =>
            {
                if (_length <= 0) return;
                _position.Text = Format(SliderMilliseconds);
            };

            _progress.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (!_dragging) return;

                _dragging = false;
                CommitSeek();
            };

            // 卡片空白处 / 封面 / 文字双击都能还原：小按钮之外再给一个更顺手的手势。
            foreach (var control in new Control[] { this, _cover, _title, _artist })
                control.DoubleClick += (s, e) => RestoreRequested?.Invoke(this, EventArgs.Empty);

            Controls.Add(_cover);
            Controls.Add(_title);
            Controls.Add(_artist);
            Controls.Add(_progress);
            Controls.Add(_position);
            Controls.Add(_duration);
            Controls.Add(_controls);
            Controls.Add(_restore);

            LayoutChildren();
        }

        /// <summary>"还原成上一个窗口形态"（按钮或双击卡片）。</summary>
        public event EventHandler? RestoreRequested;

        /// <summary>进度松手：要跳到这个位置（毫秒）。</summary>
        public event Action<long>? SeekRequested;

        /// <summary>播放控件条（模块用它接命令）。</summary>
        internal PlaybackControls Transport => _controls;

        /// <summary>进度条（给冒烟测试用）。</summary>
        internal FlatSlider Progress => _progress;

        /// <summary>封面控件现在占的矩形（给冒烟测试断言排版）。</summary>
        internal Rectangle CoverBounds => _cover.Bounds;

        internal string TitleText => _title.Text;

        internal string ArtistText => _artist.Text;

        internal string PositionText => _position.Text;

        internal string DurationText => _duration.Text;

        /// <summary>把"有没有媒体 / 在不在播 / 列表空不空"推给控件条。</summary>
        public void SetTransportState(bool hasMedia, bool isPlaying, bool hasItems) =>
            _controls.SetTransportState(hasMedia, isPlaying, hasItems);

        /// <summary>接命令层（与菜单 / 快捷键 / 任务栏按钮同一个入口）。</summary>
        public void BindCommands(Action<PlayerCommandId> execute) => _controls.BindCommands(execute);

        /// <summary>换一首（或清空：传 <c>null</c>）。</summary>
        public void ShowTrack(string? path, MediaTags? tags)
        {
            _cover.ShowTrack(path, tags);

            var title = FirstNonEmpty(tags?.Title, path == null ? null : System.IO.Path.GetFileNameWithoutExtension(path));
            _title.Text = string.IsNullOrWhiteSpace(title) ? "没有在播放" : title;

            var parts = new[] { tags?.Artist, tags?.Album }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            _artist.Text = parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }

        /// <summary>更新进度与两个时间标签（拖动中不覆盖，避免和用户手里的滑块打架）。</summary>
        public void SetProgress(long timeMilliseconds, long lengthMilliseconds)
        {
            _length = Math.Max(0, lengthMilliseconds);

            _duration.Text = _length > 0
                ? TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(_length))
                : "--:--:--";

            if (_dragging) return;

            if (_length > 0)
            {
                var value = (int)Math.Clamp(timeMilliseconds * Resolution / _length, 0, Resolution);
                if (_progress.Value != value) _progress.Value = value;
            }
            else if (_progress.Value != 0)
            {
                _progress.Value = 0;
            }

            _position.Text = Format(timeMilliseconds);
        }

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;

            _title.ForeColor = palette.ListFore;
            _artist.ForeColor = palette.HintFore;
            _position.ForeColor = palette.HintFore;
            _duration.ForeColor = palette.HintFore;

            _progress.TrackColor = palette.TrackBack;
            _progress.FillColor = palette.Accent;
            _progress.ThumbColor = palette.Accent;

            _restore.BackColor = palette.PanelBack;
            _restore.ForeColor = palette.HintFore;
            _restore.FlatAppearance.MouseOverBackColor = palette.MenuHoverBack;
            _restore.FlatAppearance.MouseDownBackColor = palette.MenuHoverBack;

            _controls.ApplyPalette(palette);
            _cover.ApplyPalette(palette);
        }

        /// <summary>测试用：等价于"拖到某个位置松手"。</summary>
        internal void CommitSeekForTest() => CommitSeek();

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LayoutChildren();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            LayoutChildren();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            if (!Visible) return;

            LayoutChildren();
            PerformLayout();
            Invalidate(true);
        }

        private static Label CreateLabel(ContentAlignment align, Font font) => new Label
        {
            TextAlign = align,
            AutoEllipsis = true,
            UseMnemonic = false,
            AutoSize = false,
            Font = font
        };

        private void LayoutChildren()
        {
            // 构造函数里设 Font 会触发一次 OnLayout，那时子控件还没建出来
            if (_cover == null || _title == null || _artist == null || _progress == null ||
                _position == null || _duration == null || _controls == null || _restore == null)
                return;

            var width = ClientSize.Width;
            var height = ClientSize.Height;
            if (width <= 0 || height <= 0) return;

            var padding = Scaled(LogicalPadding);
            var gap = Scaled(LogicalGap);

            // 封面：高度决定边长（留出上下 padding），再按卡片宽度压一层
            var coverSide = Math.Clamp(height - padding * 2, Scaled(LogicalCoverMin), Scaled(LogicalCoverMax));
            coverSide = Math.Max(0, Math.Min(coverSide, width / 3));
            _cover.SetBounds(padding, Math.Max(0, (height - coverSide) / 2), coverSide, coverSide);

            var restoreWidth = Scaled(LogicalRestoreWidth);
            var restoreHeight = Scaled(LogicalRestoreHeight);
            _restore.SetBounds(
                Math.Max(padding, width - padding - restoreWidth), padding, restoreWidth, restoreHeight);

            var rightX = padding + coverSide + gap;
            var rightWidth = Math.Max(0, width - rightX - padding);

            var controlsHeight = Scaled(LogicalControlsHeight);
            var bottomInset = Scaled(LogicalBottomInset);
            var controlsTop = Math.Max(0, height - bottomInset - controlsHeight);
            _controls.SetBounds(rightX, controlsTop, rightWidth, controlsHeight);

            var timeHeight = Scaled(LogicalTimeHeight);
            var progressHeight = Scaled(LogicalProgressHeight);
            var timeTop = Math.Max(0, controlsTop - timeHeight);
            var progressTop = Math.Max(0, timeTop - progressHeight);

            var timeWidth = Math.Min(rightWidth, Scaled(64));
            _position.SetBounds(rightX, timeTop, timeWidth, timeHeight);
            _duration.SetBounds(
                Math.Max(rightX, rightX + rightWidth - timeWidth), timeTop, timeWidth, timeHeight);

            _progress.SetBounds(rightX, progressTop, rightWidth, progressHeight);

            // 标题 / 艺术家的可用高度是"顶部到进度条"；窗口压到最小高度时先牺牲艺术家那一行。
            var textWidth = Math.Max(0, rightWidth - (restoreWidth + Scaled(8)));
            var textTop = padding;
            var textBottom = Math.Max(textTop, progressTop - Scaled(2));
            var titleHeight = Math.Min(Scaled(LogicalTitleHeight), Math.Max(0, textBottom - textTop));
            var artistHeight = Math.Max(0, textBottom - textTop - titleHeight);

            _title.SetBounds(rightX, textTop, textWidth, titleHeight);
            _artist.SetBounds(rightX, textTop + titleHeight, textWidth, artistHeight);
        }

        private int Scaled(int logical) => (int)Math.Round(logical * DeviceDpi / 96f);

        private long SliderMilliseconds => _length > 0 ? (long)(_length * (_progress.Value / (double)Resolution)) : 0;

        private void CommitSeek()
        {
            if (_length <= 0) return;

            SeekRequested?.Invoke(SliderMilliseconds);
        }

        private static string Format(long milliseconds) =>
            TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));

        private static string? FirstNonEmpty(string? first, string? second) =>
            string.IsNullOrWhiteSpace(first) ? second : first;
    }
}
