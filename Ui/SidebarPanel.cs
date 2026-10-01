using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>侧栏的四个页面。</summary>
    public enum SidebarTab
    {
        /// <summary>歌词。</summary>
        Lyrics = 0,

        /// <summary>专辑封面。</summary>
        Cover = 1,

        /// <summary>媒体信息。</summary>
        Info = 2,

        /// <summary>视频工具（去隔行、音画同步、逐帧）。</summary>
        Video = 3
    }

    /// <summary>
    /// 右侧可折叠的侧栏：顶部是标签条，下面依次是歌词 / 封面 / 媒体信息。
    /// <para>
    /// 三个页面共用同一块区域，一次只显示一个，所以侧栏宽度不会被内容反复撑开，
    /// 主界面的布局始终稳定。
    /// </para>
    /// <para>
    /// 滚轮需要特殊处理：Windows 把 <c>WM_MOUSEWHEEL</c> 投递给<b>焦点窗口</b>而不是
    /// 鼠标下的窗口，歌词页又从来不抢焦点（抢了会打断播放列表搜索框的输入），
    /// 所以这里装一个 <see cref="IMessageFilter"/>：鼠标在侧栏内时把滚动量转交给歌词控件。
    /// </para>
    /// </summary>
    internal sealed class SidebarPanel : Panel
    {
        private readonly SidebarTabs _tabs;
        private readonly Panel _host;
        private readonly LyricsView _lyrics;
        private readonly CoverView _cover;
        private readonly InfoView _info;
        private readonly VideoToolsView _videoTools;
        private readonly WheelForwarder _wheelForwarder;

        private SidebarTab _activeTab = SidebarTab.Lyrics;

        public SidebarPanel()
        {
            Font = UiFonts.Sidebar;

            _tabs = new SidebarTabs { Dock = DockStyle.Top };
            _host = new Panel { Dock = DockStyle.Fill };

            _lyrics = new LyricsView { Dock = DockStyle.Fill };
            _cover = new CoverView { Dock = DockStyle.Fill, Visible = false };
            _info = new InfoView { Dock = DockStyle.Fill, Visible = false };
            _videoTools = new VideoToolsView { Dock = DockStyle.Fill, Visible = false };

            // 先加的在最底层：歌词在最下面，其余依次盖在上面，靠 Visible 切换
            _host.Controls.Add(_lyrics);
            _host.Controls.Add(_cover);
            _host.Controls.Add(_info);
            _host.Controls.Add(_videoTools);

            Controls.Add(_host);
            Controls.Add(_tabs);

            _tabs.TabSelected += (s, index) => ActiveTab = (SidebarTab)index;
            _tabs.CollapseRequested += (s, e) => CollapseRequested?.Invoke(this, EventArgs.Empty);
            _lyrics.SeekRequested += (s, time) => SeekRequested?.Invoke(this, time);

            _wheelForwarder = new WheelForwarder(this);
            Application.AddMessageFilter(_wheelForwarder);
        }

        /// <summary>点了标签条上的折叠按钮。</summary>
        public event EventHandler? CollapseRequested;

        /// <summary>用户点了歌词里的一行，请求跳转到该时间点。</summary>
        public event EventHandler<TimeSpan>? SeekRequested;

        /// <summary>当前页面。</summary>
        public SidebarTab ActiveTab
        {
            get => _activeTab;
            set
            {
                if (_activeTab == value) return;

                _activeTab = value;
                _tabs.SelectedIndex = (int)value;

                _lyrics.Visible = value == SidebarTab.Lyrics;
                _cover.Visible = value == SidebarTab.Cover;
                _info.Visible = value == SidebarTab.Info;
                _videoTools.Visible = value == SidebarTab.Video;
            }
        }

        /// <summary>「画面」页，供主窗体接线。</summary>
        public VideoToolsView VideoTools => _videoTools;

        /// <summary>替换当前曲目的全部内容。</summary>
        public void ShowTrack(
            string? path,
            MediaTags? tags,
            LyricsDocument lyrics,
            IReadOnlyList<(string Name, string Value)> infoRows)
        {
            _lyrics.SetDocument(lyrics);
            _cover.ShowTrack(path, tags);
            _info.SetRows(infoRows);
        }

        /// <summary>只刷新「媒体信息」页，不动歌词与封面（延迟调整会频繁触发）。</summary>
        public void UpdateInfoRows(IReadOnlyList<(string Name, string Value)> rows) => _info.SetRows(rows);

        /// <summary>播放位置推进。</summary>
        public void UpdatePosition(TimeSpan position) => _lyrics.UpdatePosition(position);

        /// <summary>换一份歌词（例如用户调了时间轴偏移之后重新套用）。</summary>
        public void SetLyrics(LyricsDocument? lyrics) => _lyrics.SetDocument(lyrics);

        /// <summary>右上角那行"当前歌词偏移"小字；<c>null</c> = 不显示。</summary>
        public void SetLyricsOffsetHint(string? text) => _lyrics.SetOffsetHint(text);

        /// <summary>歌词页的右键菜单由主窗体提供（歌词偏移就在那儿调）。</summary>
        public ContextMenuStrip? LyricsContextMenu
        {
            get => _lyrics.ContextMenuStrip;
            set => _lyrics.ContextMenuStrip = value;
        }

        /// <summary>换歌词字体族（只影响歌词页，其余各页保持侧栏正文字体）。</summary>
        public void ApplyLyricsFont(string? family) => _lyrics.ApplyFontFamily(family);

        public void ApplyPalette(ThemePalette palette)
        {
            BackColor = palette.PanelBack;
            _host.BackColor = palette.PanelBack;

            _tabs.ApplyPalette(palette);
            _lyrics.ApplyPalette(palette);
            _cover.ApplyPalette(palette);
            _info.ApplyPalette(palette);
            _videoTools.ApplyPalette(palette);
        }

        /// <summary>列表类控件的滚动条要交给系统 API 才能变深色。</summary>
        public void ApplyNativeTheme(bool dark)
        {
            if (!_info.IsHandleCreated) return;

            NativeTheme.ApplyListView(_info.Handle, dark);
            _info.Invalidate();
        }

        /// <summary>把滚轮转交给歌词页；返回 true 表示消息已被消化。</summary>
        private bool TryForwardWheel(IntPtr wParam, IntPtr lParam)
        {
            if (!Visible || !IsHandleCreated) return false;
            if (_activeTab != SidebarTab.Lyrics) return false;

            // lParam 的低字是 x、高字是 y（屏幕坐标）；wParam 的高字才是滚动量。
            var position = (long)lParam;
            var screenPoint = new Point(LowWord(position), HighWord(position));

            if (!ClientRectangle.Contains(PointToClient(screenPoint))) return false;

            var delta = HighWord((long)wParam);
            if (delta == 0) return false;

            _lyrics.ScrollBy(delta);
            return true;
        }

        /// <summary>取出 32 位整数里的低 16 位，并保留符号（屏幕坐标可能是负的）。</summary>
        private static int LowWord(long value) => (short)(value & 0xFFFF);

        /// <summary>取出 32 位整数里的高 16 位，并保留符号。</summary>
        private static int HighWord(long value) => (short)((value >> 16) & 0xFFFF);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Application.RemoveMessageFilter(_wheelForwarder);

            base.Dispose(disposing);
        }

        /// <summary>
        /// 只处理滚轮的消息过滤器。
        /// <para>
        /// 点 <c>WM_MOUSEWHEEL</c> 是 0x020A；这里不去管别的消息，
        /// 而且只有鼠标确实落在侧栏里、当前又停在歌词页时才拦截。
        /// </para>
        /// </summary>
        private sealed class WheelForwarder : IMessageFilter
        {
            private const int WmMouseWheel = 0x020A;

            private readonly SidebarPanel _owner;

            public WheelForwarder(SidebarPanel owner) => _owner = owner;

            public bool PreFilterMessage(ref Message m) =>
                m.Msg == WmMouseWheel && _owner.TryForwardWheel(m.WParam, m.LParam);
        }
    }
}
