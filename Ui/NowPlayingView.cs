using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 音频窗口里的"正在播放"卡片：封面 + 曲名 / 艺术家 / 专辑（这三行由 <see cref="CoverView"/> 画）
    /// + 底部一行当前歌词。
    /// <para>
    /// 刻意复用 <see cref="CoverView"/> 而不是再画一套：封面的取法（内嵌图 → 同目录 cover.jpg）、
    /// 解码尺寸上限、圆角与边框、主题配色都与侧栏封面共用一份代码，两边不会长得不一样。
    /// </para>
    /// <para>
    /// 它只是<b>盖在画面位置上的一块不透明面板</b>：`VideoView` 在下面继续存在（句柄不变），
    /// 传输条仍在视频区底部，所以进度 / 音量 / 拖动一条代码都不用改（见 docs/1.9.0计划.md 的 F11）。
    /// </para>
    /// </summary>
    internal sealed class NowPlayingView : Panel
    {
        /// <summary>歌词那一行的高度（逻辑像素）。</summary>
        private const int LyricLogicalHeight = 40;

        private readonly CoverView _cover;
        private readonly Label _lyric;

        public NowPlayingView()
        {
            DoubleBuffered = true;
            Font = UiFonts.Resolve(11f, FontStyle.Regular);

            _lyric = new Label
            {
                Dock = DockStyle.Bottom,
                TextAlign = ContentAlignment.MiddleCenter,
                AutoEllipsis = true,
                UseMnemonic = false,
                Padding = new Padding(16, 0, 16, 0),
                Font = UiFonts.Resolve(11f, FontStyle.Regular)
            };

            // 卡片比侧栏宽得多：标题用大一号的字，站在两米外也看得清
            _cover = new CoverView
            {
                Dock = DockStyle.Fill,
                Font = UiFonts.Resolve(12f, FontStyle.Regular)
            };

            Controls.Add(_cover);
            Controls.Add(_lyric);

            LayoutLyric();
        }

        /// <summary>换一首（或清空：传 <c>null</c>）。</summary>
        public void ShowTrack(string? path, MediaTags? tags) => _cover.ShowTrack(path, tags);

        /// <summary>底部那一行当前歌词；<c>null</c> / 空串 = 不显示（纯文本歌词与没有歌词都走这里）。</summary>
        public void SetLyricLine(string? text)
        {
            var value = text ?? string.Empty;
            if (string.Equals(_lyric.Text, value, StringComparison.Ordinal)) return;

            _lyric.Text = value;
        }

        /// <summary>只读入口：现在挂着的封面（给冒烟测试用）。</summary>
        internal Image? Cover => _cover.Cover;

        /// <summary>只读入口：歌词那一行现在写着什么（给冒烟测试用）。</summary>
        internal string LyricText => _lyric.Text;

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;
            _lyric.BackColor = palette.PanelBack;
            _lyric.ForeColor = palette.Accent;
            _cover.ApplyPalette(palette);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            // 句柄建好之后才有真实的 DeviceDpi（125% 下 40 逻辑像素是 50 物理像素）
            LayoutLyric();
        }

        private void LayoutLyric()
        {
            var height = Math.Max(24, (int)Math.Round(LyricLogicalHeight * DeviceDpi / 96f));
            if (_lyric.Height != height) _lyric.Height = height;
        }
    }
}
