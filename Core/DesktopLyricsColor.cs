using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>
    /// 桌面歌词的文字配色。
    /// <para>
    /// 用枚举而不是直接存一个 ARGB 整数：设置文件是纯文本，用户可能手改，
    /// 存名字的话改错了只是退回默认色，存数字改错了会得到一块看不见的文字。
    /// </para>
    /// </summary>
    public enum DesktopLyricsColor
    {
        /// <summary>跟随当前主题的强调色。</summary>
        Theme = 0,

        White = 1,
        Yellow = 2,
        Cyan = 3,
        Green = 4,
        Pink = 5
    }

    /// <summary>桌面歌词配色的登记处。</summary>
    public static class DesktopLyricsColors
    {
        /// <summary>菜单里展示的顺序。</summary>
        public static IReadOnlyList<DesktopLyricsColor> All { get; } = new[]
        {
            DesktopLyricsColor.Theme,
            DesktopLyricsColor.White,
            DesktopLyricsColor.Yellow,
            DesktopLyricsColor.Cyan,
            DesktopLyricsColor.Green,
            DesktopLyricsColor.Pink
        };

        /// <summary>菜单里显示的名字。</summary>
        public static string DisplayName(DesktopLyricsColor color) => color switch
        {
            DesktopLyricsColor.White => "白色",
            DesktopLyricsColor.Yellow => "暖黄",
            DesktopLyricsColor.Cyan => "青色",
            DesktopLyricsColor.Green => "绿色",
            DesktopLyricsColor.Pink => "粉色",
            _ => "跟随主题"
        };
    }
}
