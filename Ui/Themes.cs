using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.Win32;

namespace 播放器.Ui
{
    /// <summary>内置主题的登记处。</summary>
    public static class Themes
    {
        /// <summary>浅色：保持播放器最早的外观——浅色外壳 + 深色视频区与传送栏。</summary>
        public static ThemePalette Light { get; } = new ThemePalette
        {
            Id = ThemeId.Light,
            DisplayName = "浅色",
            IsDark = false,

            WindowBack = Color.FromArgb(0xF0, 0xF0, 0xF0),
            PanelBack = Color.FromArgb(0xF0, 0xF0, 0xF0),
            SplitterBack = Color.FromArgb(0xF0, 0xF0, 0xF0),

            MenuBack = Color.FromArgb(0xF0, 0xF0, 0xF0),
            MenuFore = Color.FromArgb(0x1F, 0x1F, 0x1F),
            MenuDisabledFore = Color.FromArgb(0xA0, 0xA0, 0xA0),
            MenuHoverBack = Color.FromArgb(0xCC, 0xE8, 0xFF),
            MenuHoverFore = Color.FromArgb(0x1F, 0x1F, 0x1F),
            MenuBorder = Color.FromArgb(0xD8, 0xD8, 0xD8),
            MenuSeparator = Color.FromArgb(0xD0, 0xD0, 0xD0),

            ListBack = Color.White,
            ListFore = Color.FromArgb(0x1F, 0x1F, 0x1F),
            HintFore = Color.FromArgb(0x6B, 0x6B, 0x70),
            PlayingFore = Color.FromArgb(0x00, 0x66, 0xCC),
            ErrorFore = Color.FromArgb(0xB2, 0x22, 0x22),

            VideoBack = Color.FromArgb(0x10, 0x10, 0x12),
            PlaceholderFore = Color.FromArgb(0x8C, 0x8C, 0x94),

            TransportBack = Color.FromArgb(0x20, 0x20, 0x24),
            TransportFore = Color.FromArgb(0xDC, 0xDC, 0xDC),
            TrackBack = Color.FromArgb(0x55, 0x55, 0x5C),
            Accent = Color.FromArgb(0x0A, 0x84, 0xFF),

            IconInk = Color.FromArgb(0x3C, 0x3C, 0x3C)
        };

        /// <summary>深色：整体深色，强调色用亮蓝。</summary>
        public static ThemePalette Dark { get; } = new ThemePalette
        {
            Id = ThemeId.Dark,
            DisplayName = "深色",
            IsDark = true,

            WindowBack = Color.FromArgb(0x1E, 0x1E, 0x1E),
            PanelBack = Color.FromArgb(0x25, 0x25, 0x26),
            SplitterBack = Color.FromArgb(0x1E, 0x1E, 0x1E),

            MenuBack = Color.FromArgb(0x2D, 0x2D, 0x30),
            MenuFore = Color.FromArgb(0xE4, 0xE4, 0xE4),
            MenuDisabledFore = Color.FromArgb(0x6D, 0x6D, 0x6D),
            MenuHoverBack = Color.FromArgb(0x3E, 0x3E, 0x42),
            MenuHoverFore = Color.White,
            MenuBorder = Color.FromArgb(0x3F, 0x3F, 0x46),
            MenuSeparator = Color.FromArgb(0x4A, 0x4A, 0x50),

            ListBack = Color.FromArgb(0x1E, 0x1E, 0x1E),
            ListFore = Color.FromArgb(0xE4, 0xE4, 0xE4),
            HintFore = Color.FromArgb(0x8E, 0x8E, 0x96),
            PlayingFore = Color.FromArgb(0x4C, 0xC2, 0xFF),
            ErrorFore = Color.FromArgb(0xF4, 0x87, 0x71),

            VideoBack = Color.FromArgb(0x10, 0x10, 0x12),
            PlaceholderFore = Color.FromArgb(0x7A, 0x7A, 0x82),

            TransportBack = Color.FromArgb(0x18, 0x18, 0x18),
            TransportFore = Color.FromArgb(0xDC, 0xDC, 0xDC),
            TrackBack = Color.FromArgb(0x4A, 0x4A, 0x50),
            Accent = Color.FromArgb(0x4C, 0xC2, 0xFF),

            IconInk = Color.FromArgb(0xD8, 0xD8, 0xD8)
        };

        /// <summary>OLED 纯黑：大面积纯黑，适合 OLED 屏幕，也更沉浸。</summary>
        public static ThemePalette Oled { get; } = new ThemePalette
        {
            Id = ThemeId.Oled,
            DisplayName = "OLED 纯黑",
            IsDark = true,

            WindowBack = Color.Black,
            PanelBack = Color.Black,
            SplitterBack = Color.FromArgb(0x0A, 0x0A, 0x0A),

            MenuBack = Color.FromArgb(0x0A, 0x0A, 0x0A),
            MenuFore = Color.FromArgb(0xEC, 0xEC, 0xEC),
            MenuDisabledFore = Color.FromArgb(0x5A, 0x5A, 0x5A),
            MenuHoverBack = Color.FromArgb(0x24, 0x24, 0x24),
            MenuHoverFore = Color.White,
            MenuBorder = Color.FromArgb(0x1E, 0x1E, 0x1E),
            MenuSeparator = Color.FromArgb(0x2A, 0x2A, 0x2A),

            ListBack = Color.Black,
            ListFore = Color.FromArgb(0xEC, 0xEC, 0xEC),
            HintFore = Color.FromArgb(0x82, 0x82, 0x8A),
            PlayingFore = Color.FromArgb(0x4C, 0xC2, 0xFF),
            ErrorFore = Color.FromArgb(0xFF, 0x7B, 0x72),

            VideoBack = Color.Black,
            PlaceholderFore = Color.FromArgb(0x6E, 0x6E, 0x6E),

            TransportBack = Color.Black,
            TransportFore = Color.FromArgb(0xE0, 0xE0, 0xE0),
            TrackBack = Color.FromArgb(0x3A, 0x3A, 0x3A),
            Accent = Color.FromArgb(0x4C, 0xC2, 0xFF),

            IconInk = Color.FromArgb(0xE8, 0xE8, 0xE8)
        };

        /// <summary>VLC 橙：深灰底 + VLC 招牌橙作为强调色。</summary>
        public static ThemePalette Vlc { get; } = new ThemePalette
        {
            Id = ThemeId.Vlc,
            DisplayName = "VLC 橙",
            IsDark = true,

            WindowBack = Color.FromArgb(0x2B, 0x2B, 0x2B),
            PanelBack = Color.FromArgb(0x2B, 0x2B, 0x2B),
            SplitterBack = Color.FromArgb(0x2B, 0x2B, 0x2B),

            MenuBack = Color.FromArgb(0x3A, 0x3A, 0x3A),
            MenuFore = Color.FromArgb(0xED, 0xED, 0xED),
            MenuDisabledFore = Color.FromArgb(0x77, 0x77, 0x77),
            MenuHoverBack = Color.FromArgb(0xFF, 0x88, 0x00),
            MenuHoverFore = Color.FromArgb(0x1A, 0x1A, 0x1A),
            MenuBorder = Color.FromArgb(0x4A, 0x4A, 0x4A),
            MenuSeparator = Color.FromArgb(0x55, 0x55, 0x55),

            ListBack = Color.FromArgb(0x26, 0x26, 0x26),
            ListFore = Color.FromArgb(0xED, 0xED, 0xED),
            HintFore = Color.FromArgb(0x9A, 0x9A, 0x9A),
            PlayingFore = Color.FromArgb(0xFF, 0xA0, 0x33),
            ErrorFore = Color.FromArgb(0xFF, 0x6B, 0x6B),

            VideoBack = Color.FromArgb(0x14, 0x14, 0x14),
            PlaceholderFore = Color.FromArgb(0x8A, 0x8A, 0x8A),

            TransportBack = Color.FromArgb(0x1A, 0x1A, 0x1A),
            TransportFore = Color.FromArgb(0xE8, 0xE8, 0xE8),
            TrackBack = Color.FromArgb(0x4A, 0x4A, 0x4A),
            Accent = Color.FromArgb(0xFF, 0x88, 0x00),

            IconInk = Color.FromArgb(0xE0, 0xE0, 0xE0)
        };

        /// <summary>菜单里展示的顺序。</summary>
        public static IReadOnlyList<ThemeId> All { get; } = new[]
        {
            ThemeId.Auto, ThemeId.Light, ThemeId.Dark, ThemeId.Oled, ThemeId.Vlc
        };

        /// <summary>把主题标识解析成实际配色；<see cref="ThemeId.Auto"/> 交给系统设置决定。</summary>
        public static ThemePalette Resolve(ThemeId id) => id switch
        {
            ThemeId.Light => Light,
            ThemeId.Dark => Dark,
            ThemeId.Oled => Oled,
            ThemeId.Vlc => Vlc,
            _ => IsSystemUsingDarkMode() ? Dark : Light
        };

        /// <summary>菜单里显示的名字。</summary>
        public static string DisplayName(ThemeId id) => id switch
        {
            ThemeId.Light => Light.DisplayName,
            ThemeId.Dark => Dark.DisplayName,
            ThemeId.Oled => Oled.DisplayName,
            ThemeId.Vlc => Vlc.DisplayName,
            _ => "跟随系统"
        };

        /// <summary>
        /// 读取 Windows 的"应用模式"设置。
        /// <c>AppsUseLightTheme</c> 为 0 表示系统正在使用深色模式。
        /// </summary>
        public static bool IsSystemUsingDarkMode()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                           @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
                }
            }
            catch (Exception)
            {
                // 读不到就当作浅色，不影响使用。
                return false;
            }
        }
    }
}
