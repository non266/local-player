using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 音频窗口里的"正在播放"卡片：封面 + 曲名 / 艺术家 / 专辑（这三行由 <see cref="CoverView"/> 画）
    /// + 一行当前歌词 + 一排播放控件（<see cref="PlaybackControls"/>）。
    /// <para>
    /// 刻意复用 <see cref="CoverView"/> 而不是再画一套：封面的取法（内嵌图 → 同目录 cover.jpg）、
    /// 解码尺寸上限、圆角与边框、主题配色都与侧栏封面共用一份代码，两边不会长得不一样。
    /// </para>
    /// <para>
    /// <b>内部一律用算术排版，不靠 <c>Dock</c></b>：1.9.0 的封面错位就是因为卡片（以及卡片里的
    /// 封面控件）在窗体还没恢复几何、或卡片还隐藏着的时候把尺寸算死了，之后窗口变窄也不重排，
    /// 封面按旧宽度居中、被窗口边缘裁掉（见 docs/1.10.0计划.md 的 B9）。
    /// 现在 <see cref="OnLayout"/> 每次都按当前的 <c>ClientSize</c> 重算，
    /// <see cref="OnVisibleChanged"/> 时再强制排一次。
    /// </para>
    /// <para>
    /// 它只是<b>盖在画面位置上的一块不透明面板</b>：<c>VideoView</c> 在下面继续存在（句柄不变），
    /// 传输条仍在视频区底部，所以进度 / 音量 / 拖动一条代码都不用改。
    /// </para>
    /// </summary>
    internal sealed class NowPlayingView : Panel
    {
        /// <summary>歌词那一行的高度（逻辑像素）。</summary>
        private const int LyricLogicalHeight = 40;

        /// <summary>播放控件条的高度（逻辑像素）。</summary>
        private const int ControlsLogicalHeight = 52;

        private readonly CoverView _cover;
        private readonly Label _lyric;
        private readonly PlaybackControls _controls;

        public NowPlayingView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Font = UiFonts.Resolve(11f, FontStyle.Regular);

            _lyric = new Label
            {
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                UseMnemonic = false,
                Padding = new Padding(16, 0, 16, 0),
                Font = UiFonts.Resolve(11f, FontStyle.Regular)
            };

            // 卡片比侧栏宽得多：标题用大一号的字，站在两米外也看得清
            _cover = new CoverView { Font = UiFonts.Resolve(12f, FontStyle.Regular) };

            _controls = new PlaybackControls();

            Controls.Add(_cover);
            Controls.Add(_lyric);
            Controls.Add(_controls);

            LayoutChildren();
        }

        /// <summary>换一首（或清空：传 <c>null</c>）。<paramref name="pending"/> = 标签还在读（1.19.0）。</summary>
        public void ShowTrack(string? path, MediaTags? tags, bool pending = false) =>
            _cover.ShowTrack(path, tags, pending);

        /// <summary>底部那一行当前歌词；<c>null</c> / 空串 = 不显示（纯文本歌词与没有歌词都走这里）。</summary>
        public void SetLyricLine(string? text)
        {
            var value = text ?? string.Empty;
            if (string.Equals(_lyric.Text, value, StringComparison.Ordinal)) return;

            _lyric.Text = value;
        }

        /// <summary>播放控件条（主窗体接线用）。</summary>
        internal PlaybackControls Transport => _controls;

        /// <summary>封面控件现在占的矩形（给冒烟测试断言"跟着卡片重排"）。</summary>
        internal Rectangle CoverBounds => _cover.Bounds;

        /// <summary>歌词那一行现在写着什么（给冒烟测试用）。</summary>
        internal string LyricText => _lyric.Text;

        /// <summary>只读入口：现在挂着的封面（给冒烟测试用）。</summary>
        internal Image? Cover => _cover.Cover;

        /// <summary>把"有没有媒体 / 在不在播 / 列表空不空"推给控件条。</summary>
        public void SetTransportState(bool hasMedia, bool isPlaying, bool hasItems) =>
            _controls.SetTransportState(hasMedia, isPlaying, hasItems);

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;

            _lyric.BackColor = palette.PanelBack;
            _lyric.ForeColor = palette.Accent;

            _controls.ApplyPalette(palette);
            _cover.ApplyPalette(palette);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄建好之后才有真实的 DeviceDpi（125% 下 40 逻辑像素是 50 物理像素）
            LayoutChildren();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);

            // 卡片自己的尺寸变了（窗口缩放 / DPI 变化）：里面的三块必须跟着重算
            LayoutChildren();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);

            // 隐藏期间父容器可能已经改过尺寸；再显示时补一次，别用旧宽度画
            if (!Visible) return;

            LayoutChildren();
            PerformLayout();
            Invalidate(true);
        }

        private void LayoutChildren()
        {
            // 构造函数里设 Font 会触发一次 OnLayout，那时子控件还没建出来
            if (_cover == null || _lyric == null || _controls == null) return;

            var width = ClientSize.Width;
            var height = ClientSize.Height;
            if (width <= 0 || height <= 0) return;

            var scale = DeviceDpi / 96f;
            var lyricHeight = Math.Max(20, (int)Math.Round(LyricLogicalHeight * scale));
            var controlsHeight = Math.Max(0, (int)Math.Round(ControlsLogicalHeight * scale));

            // 从底部往上推：控件条贴底、歌词在它上面、剩下的全给封面
            var controlsTop = Math.Max(0, height - controlsHeight);
            var lyricTop = Math.Max(0, controlsTop - lyricHeight);

            _controls.SetBounds(0, controlsTop, width, Math.Max(0, height - controlsTop));
            _lyric.SetBounds(0, lyricTop, width, Math.Max(0, controlsTop - lyricTop));
            _cover.SetBounds(0, 0, width, lyricTop);
        }
    }
}
