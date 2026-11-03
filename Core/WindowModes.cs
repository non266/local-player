using System;
using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>窗口形态：视频窗口（画面 + 侧栏 + 播放列表）与音频窗口（简化的"正在播放"卡片）。</summary>
    public enum WindowShape
    {
        /// <summary>视频窗口：现在的布局，画面 / 侧栏 / 播放列表。</summary>
        Video = 0,

        /// <summary>音频窗口：一张封面卡片，没有工具栏、播放列表与侧栏。</summary>
        Audio = 1
    }

    /// <summary>
    /// 「视图 → 窗口模式」的三个选项，以及"这一首该用哪种窗口"的判定规则。
    /// <para>
    /// 单列一张表是因为菜单、启动恢复与切歌时的自动判定都要用它——三处各写一份迟早会写歪，
    /// 而且"哪个文件算音频"必须只有一个答案（<see cref="MediaFormats.IsAudioFile"/>）。
    /// </para>
    /// </summary>
    public static class WindowModes
    {
        /// <summary>按文件自动切（默认）。</summary>
        public const string Auto = "auto";

        /// <summary>总是音频窗口（放视频＝只听声音）。</summary>
        public const string Audio = "audio";

        /// <summary>总是视频窗口。</summary>
        public const string Video = "video";

        /// <summary>三个选项：显示名 ↔ 存进设置的值。</summary>
        public static IReadOnlyList<(string Label, string Value)> All { get; } = new[]
        {
            ("自动（按文件）", Auto),
            ("总是音频窗口", Audio),
            ("总是视频窗口", Video)
        };

        /// <summary>把存下来的值归一成表里认得的那个；空值 / 认不出来的一律 <see cref="Auto"/>。</summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Auto;

            var trimmed = value!.Trim();

            foreach (var (_, mode) in All)
            {
                if (string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase)) return mode;
            }

            return Auto;
        }

        /// <summary>把值翻回显示名；认不出来就原样返回。</summary>
        public static string Describe(string? value)
        {
            var normalized = Normalize(value);

            foreach (var (label, mode) in All)
            {
                if (string.Equals(mode, normalized, StringComparison.Ordinal)) return label;
            }

            return normalized;
        }

        /// <summary>
        /// 按偏好、当前文件与**真实轨道**决定该用哪种窗口。
        /// <para>
        /// 判定顺序：手动固定（总是音频 / 总是视频）最高 → 真实轨道已知就按它 →
        /// 还不知道就退回扩展名 → 连扩展名都没有（串流 / 空路径）按视频窗口。
        /// </para>
        /// <para>
        /// <paramref name="hasVideoTrack"/> 为 <c>null</c> 表示"轨道清单还没解析出来"（刚打开的那一瞬间）。
        /// 真实轨道比扩展名可靠：只有音轨的 <c>.mkv</c>、扩展名是音频却真的带视频轨的文件都靠它纠正；
        /// 扩展名只当解析出来之前的临时猜测（见 docs/1.10.0计划.md 的 F11′）。
        /// </para>
        /// <para>纯函数，不碰引擎、不碰窗体。</para>
        /// </summary>
        public static WindowShape Resolve(string? preference, string? path, bool? hasVideoTrack)
        {
            var mode = Normalize(preference);

            if (mode == Audio) return WindowShape.Audio;
            if (mode == Video) return WindowShape.Video;

            if (hasVideoTrack.HasValue)
                return hasVideoTrack.Value ? WindowShape.Video : WindowShape.Audio;

            return MediaFormats.IsAudioFile(path) ? WindowShape.Audio : WindowShape.Video;
        }

        /// <summary>只看扩展名的旧判定（保留给"轨道还没出来"的调用点与测试）。</summary>
        public static WindowShape Resolve(string? preference, string? path) => Resolve(preference, path, null);
    }
}
