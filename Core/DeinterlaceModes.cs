using System;
using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>
    /// 去隔行的可选模式。
    /// <para>
    /// 单列一张表是因为菜单和侧栏「画面」页都要用它——两处各写一份迟早会写歪，
    /// 而且显示名与传给 libvlc 的值必须严格对应。
    /// </para>
    /// </summary>
    public static class DeinterlaceModes
    {
        /// <summary>关闭去隔行。</summary>
        public const string Off = "";

        /// <summary>模式表：显示名 ↔ 传给 <c>libvlc_video_set_deinterlace</c> 的值。</summary>
        public static IReadOnlyList<(string Label, string Value)> All { get; } = new[]
        {
            ("关闭", Off),
            ("自动", "auto"),
            ("混合（blend）", "blend"),
            ("Bob", "bob"),
            ("线性（linear）", "linear"),
            ("Yadif", "yadif"),
            ("Yadif 2x", "yadif2x"),
            ("IVTC（胶片转制）", "ivtc"),
            ("荧光（phosphor）", "phosphor")
        };

        /// <summary>把模式值翻回显示名；认不出来就原样返回。</summary>
        public static string Describe(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "关闭";

            foreach (var (label, mode) in All)
                if (string.Equals(mode, value, StringComparison.OrdinalIgnoreCase)) return label;

            return value!;
        }

        /// <summary>两个模式值是否相同（大小写不敏感，空值统一按"关闭"）。</summary>
        public static bool Same(string? left, string? right) =>
            string.Equals(left ?? Off, right ?? Off, StringComparison.OrdinalIgnoreCase);
    }
}
