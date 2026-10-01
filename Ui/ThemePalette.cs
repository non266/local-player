using System.Drawing;

namespace 播放器.Ui
{
    /// <summary>主题标识。持久化到设置文件时按名字保存。</summary>
    public enum ThemeId
    {
        /// <summary>跟随 Windows 的浅色 / 深色设置。</summary>
        Auto,
        Light,
        Dark,
        Oled,
        Vlc
    }

    /// <summary>
    /// 一套完整的配色。界面各处只从这里取色，不直接写死颜色，
    /// 新增主题只需要补一组数据。
    /// </summary>
    public sealed class ThemePalette
    {
        public ThemeId Id { get; init; }

        public string DisplayName { get; init; } = string.Empty;

        /// <summary>是否为深色系。决定是否启用系统深色 API、图标用浅色还是深色。</summary>
        public bool IsDark { get; init; }

        // ---- 窗体与容器 ----

        public Color WindowBack { get; init; }

        /// <summary>播放列表面板区域的底色。</summary>
        public Color PanelBack { get; init; }

        /// <summary>分隔条颜色。</summary>
        public Color SplitterBack { get; init; }

        // ---- 菜单 / 工具栏 / 状态栏 ----

        public Color MenuBack { get; init; }

        public Color MenuFore { get; init; }

        public Color MenuDisabledFore { get; init; }

        public Color MenuHoverBack { get; init; }

        public Color MenuHoverFore { get; init; }

        public Color MenuBorder { get; init; }

        public Color MenuSeparator { get; init; }

        // ---- 播放列表 ----

        public Color ListBack { get; init; }

        public Color ListFore { get; init; }

        /// <summary>
        /// 提示 / 次要文字颜色（"没有找到歌词"、"侧栏太窄"这类）。
        /// <para>
        /// 单独一项而不是借用 <see cref="MenuDisabledFore"/>：那个是<b>禁用菜单项</b>的颜色，
        /// 在浅色主题下是 #A0A0A0 画在 #F0F0F0 上（对比度约 2.3:1），当正文用太淡了。
        /// 这里的取值都按"在 <see cref="PanelBack"/> 上至少 4.5:1"挑过。
        /// </para>
        /// </summary>
        public Color HintFore { get; init; }

        /// <summary>正在播放那一行的文字颜色。</summary>
        public Color PlayingFore { get; init; }

        /// <summary>无法播放那一行的文字颜色。</summary>
        public Color ErrorFore { get; init; }

        // ---- 视频区 ----

        public Color VideoBack { get; init; }

        public Color PlaceholderFore { get; init; }

        // ---- 传送栏 ----

        public Color TransportBack { get; init; }

        public Color TransportFore { get; init; }

        /// <summary>滑块未填充部分的轨道颜色。</summary>
        public Color TrackBack { get; init; }

        /// <summary>强调色：已播放进度、滑块圆点、正在播放高亮。</summary>
        public Color Accent { get; init; }

        // ---- 图标 ----

        /// <summary>工具栏图标的主色。深色主题下要用浅色，否则图标看不见。</summary>
        public Color IconInk { get; init; }
    }
}
