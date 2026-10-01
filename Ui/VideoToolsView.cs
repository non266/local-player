using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>侧栏「视频」页里与延迟有关的一步操作。</summary>
    internal sealed class DelayStepEventArgs : EventArgs
    {
        public DelayStepEventArgs(bool audio, int direction)
        {
            Audio = audio;
            Direction = direction;
        }

        /// <summary><c>true</c> 表示音频延迟，<c>false</c> 表示字幕延迟。</summary>
        public bool Audio { get; }

        /// <summary>+1 或 −1。</summary>
        public int Direction { get; }
    }

    /// <summary>
    /// 侧栏「视频」页：去隔行、音画/字幕延迟微调、逐帧步进。
    /// <para>
    /// 这些调节都是"看着画面边调边看效果"的，放在侧栏比埋进菜单更容易发现；
    /// 常用的那几个同时也留了快捷键与菜单项。
    /// </para>
    /// <para>
    /// 注意这里<b>没有</b>亮度/对比度那类画面调整：libvlc 3.x 的 <c>adjust</c> 滤镜
    /// 在本项目里怎么调都不影响输出画面（媒体选项、滤镜链参数、实例级选项、
    /// 关掉硬解、换视频输出都试过，画面像素完全不变），所以没有做——
    /// 与其放几个不起作用的滑块，不如不做。
    /// </para>
    /// </summary>
    internal sealed class VideoToolsView : Panel
    {
        private const int RowHeight = 36;
        private const int LabelWidth = 62;
        private const int SidePadding = 10;

        private readonly Button _deinterlaceButton = new Button();
        private readonly ContextMenuStrip _deinterlaceMenu = new ContextMenuStrip();

        private readonly Label _audioDelayValue = new Label();
        private readonly Label _subtitleDelayValue = new Label();
        private readonly Button _resetDelaysButton = new Button();

        private readonly Button _previousFrameButton = new Button();
        private readonly Button _nextFrameButton = new Button();
        private readonly Button _timeCodeButton = new Button();
        private readonly Label _hint = new Label();

        private string _deinterlace = string.Empty;

        public VideoToolsView()
        {
            Font = UiFonts.Sidebar;

            // 不开 AutoScroll：这一页只有六七行，最矮的侧栏也放得下；
            // 开了反而会因为构造时的宽度还没确定而冒出一条多余的横向滚动条。
            AutoScroll = false;

            BuildLayout();
        }

        /// <summary>去隔行模式变了。</summary>
        public event EventHandler<string>? DeinterlaceChanged;

        /// <summary>请求走一帧。</summary>
        public event EventHandler<bool>? FrameStepRequested;

        /// <summary>请求按时间码跳转。</summary>
        public event EventHandler? TimeCodeRequested;

        /// <summary>延迟的加减按钮被按下。</summary>
        public event EventHandler<DelayStepEventArgs>? DelayStepRequested;

        /// <summary>请求把两个延迟都归零。</summary>
        public event EventHandler? ResetDelaysRequested;

        // ---- 对外接口 ---------------------------------------------------------

        /// <summary>外部（快捷键或菜单）改了延迟之后同步显示。</summary>
        public void SetDelays(long audioMilliseconds, long subtitleMilliseconds)
        {
            _audioDelayValue.Text = TimeFormatter.FormatMilliseconds(audioMilliseconds);
            _subtitleDelayValue.Text = TimeFormatter.FormatMilliseconds(subtitleMilliseconds);
        }

        /// <summary>由外部设置当前去隔行模式（例如启动时从设置恢复）。</summary>
        public void SetDeinterlaceMode(string? mode)
        {
            _deinterlace = mode ?? string.Empty;
            UpdateDeinterlaceButton();
        }

        /// <summary>是否有正在播放的媒体（没有就禁用逐帧与延迟）。</summary>
        public void SetPlaybackAvailable(bool available)
        {
            _previousFrameButton.Enabled = available;
            _nextFrameButton.Enabled = available;
            _timeCodeButton.Enabled = available;
            _resetDelaysButton.Enabled = available;

            _hint.Text = available ? string.Empty : "播放视频后可用";
        }

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;
            ForeColor = palette.ListFore;

            foreach (Control control in Controls)
            {
                switch (control)
                {
                    case Button button:
                        button.FlatStyle = FlatStyle.Flat;
                        button.BackColor = palette.MenuBack;
                        button.ForeColor = palette.MenuFore;
                        button.FlatAppearance.BorderColor = palette.MenuBorder;
                        break;

                    case Label label:
                        label.ForeColor = palette.ListFore;
                        break;
                }
            }

            _deinterlaceMenu.Renderer = new ThemedToolStripRenderer(palette);
            _deinterlaceMenu.BackColor = palette.MenuBack;
            _deinterlaceMenu.ForeColor = palette.MenuFore;

            _hint.ForeColor = palette.MenuDisabledFore;
            Invalidate();
        }

        // ---- 布局 -------------------------------------------------------------

        private float DpiScale => DeviceDpi / 96f;

        private int Scaled(int value) => (int)Math.Round(value * DpiScale);

        private void BuildLayout()
        {
            var top = Scaled(10);

            BuildDeinterlaceRow(top);
            top += Scaled(RowHeight);

            top += Scaled(8);
            BuildDelayRow("音频延迟", top, _audioDelayValue, audio: true);
            top += Scaled(RowHeight);

            BuildDelayRow("字幕延迟", top, _subtitleDelayValue, audio: false);
            top += Scaled(RowHeight);

            _resetDelaysButton.Text = "延迟归零";
            _resetDelaysButton.Size = new Size(Scaled(92), Scaled(26));
            _resetDelaysButton.Location = new Point(Scaled(SidePadding + LabelWidth), top);
            _resetDelaysButton.Click += (s, e) => ResetDelaysRequested?.Invoke(this, EventArgs.Empty);
            Controls.Add(_resetDelaysButton);
            top += Scaled(RowHeight);

            top += Scaled(8);
            BuildFrameStepRow(top);

            SetDelays(0, 0);
        }

        private void BuildDeinterlaceRow(int top)
        {
            Controls.Add(new Label
            {
                Text = "去隔行",
                Location = new Point(Scaled(SidePadding), top + Scaled(8)),
                Size = new Size(Scaled(LabelWidth), Scaled(20)),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            });

            _deinterlaceButton.Size = new Size(Scaled(150), Scaled(26));
            _deinterlaceButton.Location = new Point(Scaled(SidePadding + LabelWidth), top);
            _deinterlaceButton.Click += (s, e) =>
                _deinterlaceMenu.Show(_deinterlaceButton, new Point(0, _deinterlaceButton.Height));
            Controls.Add(_deinterlaceButton);

            _deinterlaceMenu.ShowImageMargin = false;
            _deinterlaceMenu.ShowCheckMargin = true;

            foreach (var (label, value) in DeinterlaceModes.All)
            {
                var item = new ToolStripMenuItem(label) { Tag = value };
                item.Click += (s, e) => SetDeinterlace(value);
                _deinterlaceMenu.Items.Add(item);
            }

            UpdateDeinterlaceButton();
        }

        private void BuildDelayRow(string caption, int top, Label valueLabel, bool audio)
        {
            Controls.Add(new Label
            {
                Text = caption,
                Location = new Point(Scaled(SidePadding), top + Scaled(8)),
                Size = new Size(Scaled(LabelWidth), Scaled(20)),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            });

            var minus = new Button
            {
                Text = "−",
                Size = new Size(Scaled(28), Scaled(26)),
                Location = new Point(Scaled(SidePadding + LabelWidth), top)
            };
            minus.Click += (s, e) => DelayStepRequested?.Invoke(this, new DelayStepEventArgs(audio, -1));
            Controls.Add(minus);

            valueLabel.Location = new Point(Scaled(SidePadding + LabelWidth) + Scaled(30), top + Scaled(8));
            valueLabel.Size = new Size(Scaled(72), Scaled(20));
            valueLabel.TextAlign = ContentAlignment.MiddleCenter;
            valueLabel.AutoSize = false;
            Controls.Add(valueLabel);

            var plus = new Button
            {
                Text = "+",
                Size = new Size(Scaled(28), Scaled(26)),
                Location = new Point(Scaled(SidePadding + LabelWidth) + Scaled(104), top)
            };
            plus.Click += (s, e) => DelayStepRequested?.Invoke(this, new DelayStepEventArgs(audio, 1));
            Controls.Add(plus);
        }

        private void BuildFrameStepRow(int top)
        {
            Controls.Add(new Label
            {
                Text = "逐帧",
                Location = new Point(Scaled(SidePadding), top + Scaled(8)),
                Size = new Size(Scaled(LabelWidth), Scaled(20)),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false
            });

            _previousFrameButton.Text = "◀ 上一帧";
            _previousFrameButton.Size = new Size(Scaled(86), Scaled(26));
            _previousFrameButton.Location = new Point(Scaled(SidePadding + LabelWidth), top);
            _previousFrameButton.Click += (s, e) => FrameStepRequested?.Invoke(this, false);
            Controls.Add(_previousFrameButton);

            _nextFrameButton.Text = "下一帧 ▶";
            _nextFrameButton.Size = new Size(Scaled(86), Scaled(26));
            _nextFrameButton.Location = new Point(Scaled(SidePadding + LabelWidth) + Scaled(92), top);
            _nextFrameButton.Click += (s, e) => FrameStepRequested?.Invoke(this, true);
            Controls.Add(_nextFrameButton);

            top += Scaled(RowHeight);

            _timeCodeButton.Text = "跳转到时间码…";
            _timeCodeButton.Size = new Size(Scaled(140), Scaled(26));
            _timeCodeButton.Location = new Point(Scaled(SidePadding + LabelWidth), top);
            _timeCodeButton.Click += (s, e) => TimeCodeRequested?.Invoke(this, EventArgs.Empty);
            Controls.Add(_timeCodeButton);

            top += Scaled(RowHeight);

            _hint.Text = "播放视频后可用";
            _hint.Location = new Point(Scaled(SidePadding), top);
            _hint.Size = new Size(Scaled(200), Scaled(20));
            _hint.AutoSize = false;
            Controls.Add(_hint);
        }

        // ---- 交互 -------------------------------------------------------------

        private void SetDeinterlace(string value)
        {
            _deinterlace = value;
            UpdateDeinterlaceButton();
            DeinterlaceChanged?.Invoke(this, value);
        }

        private void UpdateDeinterlaceButton()
        {
            _deinterlaceButton.Text = DeinterlaceModes.Describe(_deinterlace) + " ▾";

            foreach (ToolStripItem item in _deinterlaceMenu.Items)
            {
                if (item is ToolStripMenuItem menuItem)
                    menuItem.Checked = DeinterlaceModes.Same(menuItem.Tag as string, _deinterlace);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _deinterlaceMenu.Dispose();

            base.Dispose(disposing);
        }
    }
}
