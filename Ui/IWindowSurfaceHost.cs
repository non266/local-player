using System;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>主窗口现在长成什么样：视频窗口（画面 + 侧栏 + 播放列表）或音频窗口（一张"正在播放"卡片）。</summary>
    internal enum WindowSurface
    {
        /// <summary>视频窗口：现在的布局，画面 / 侧栏 / 播放列表。</summary>
        Video = 0,

        /// <summary>音频窗口：一张封面卡片，没有工具栏、播放列表与侧栏。</summary>
        Audio = 1,

        /// <summary>迷你模式：只剩一张小卡片（顶置、没有菜单 / 工具栏 / 状态栏 / 传输条）。</summary>
        Mini = 2
    }

    /// <summary>
    /// 窗口模块（视频 / 音频 / 全屏）需要主窗口提供的东西。
    /// <para>
    /// 刻意做成"很窄的一张清单"而不是把 <c>MainForm</c> 整个传进去：模块只能读这几样、
    /// 只能通过这几个方法请求动作，于是"谁在改窗口形状"一眼看得清，模块本身也能脱离
    /// 主窗体单独推演。主窗体在 <c>MainForm.SurfaceHost.cs</c> 里显式实现它。
    /// </para>
    /// <para>
    /// 这里<b>不放引擎</b>：引擎按约定只交给 <c>WindowSurfaceController</c>（它要用真实轨道判形态），
    /// 界面模块只读"有没有媒体 / 在不在播 / 多长"这几个结果。
    /// </para>
    /// </summary>
    internal interface IWindowSurfaceHost
    {
        /// <summary>主窗口本身。</summary>
        Form Window { get; }

        /// <summary>当前设置（窗口几何、要不要分离侧栏 / 播放列表都从这里读）。</summary>
        AppSettings Settings { get; }

        /// <summary>当前配色（卡片与全屏进度条换主题时用）。</summary>
        ThemePalette Palette { get; }

        MenuStrip MainMenuStrip { get; }

        ToolStrip MainToolStrip { get; }

        StatusStrip MainStatusStrip { get; }

        Panel TransportPanel { get; }

        /// <summary>主分隔条：左边视频 / 右边播放列表。</summary>
        SplitContainer MainSplit { get; }

        /// <summary>视频区分隔条：左边画面 / 右边侧栏。</summary>
        SplitContainer VideoSplit { get; }

        /// <summary>画面那一块（卡片盖在它上面，视频 HWND 不销毁）。</summary>
        Control VideoHost { get; }

        Label Placeholder { get; }

        bool HasMedia { get; }

        bool IsPlaying { get; }

        /// <summary>当前这一首的路径（卡片要把封面 / 曲名画出来）；没有时为 <c>null</c>。</summary>
        string? CurrentTrackPath { get; }

        /// <summary>当前这一首的标签（封面在里面）。</summary>
        MediaTags? CurrentTrackTags { get; }

        long PositionMilliseconds { get; }

        long LengthMilliseconds { get; }

        int PlaylistCount { get; }

        /// <summary>把一条命令交给主窗口唯一那个执行入口（卡片上的按钮走这里）。</summary>
        void ExecuteCommand(PlayerCommandId id);

        void SeekTo(long milliseconds);

        void SetStatus(string text);

        /// <summary>全屏时把分离出去的侧栏 / 播放列表窗口收起来，退出时放回去。</summary>
        void SetFloatingWindowsVisible(bool visible);

        /// <summary>全屏是置顶窗口，会把桌面歌词盖住：显式再提一次。</summary>
        void RaiseDesktopLyricsToTop();

        /// <summary>两栏折叠状态改完之后按设置重排一次。</summary>
        void ApplyPanelLayout();

        /// <summary>退出全屏之后把「显示播放列表」菜单的勾选状态同步回设置。</summary>
        void SyncPlaylistMenuChecked();
    }
}
