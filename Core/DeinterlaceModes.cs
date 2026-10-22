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

        /// <summary>
        /// 模式表：显示名 ↔ 传给 <c>libvlc_video_set_deinterlace</c> 的值。
        /// <para>
        /// <b>没有「自动」</b>：libvlc 3 的这个接口只接受具体滤镜名，<c>auto</c> 实测等于关闭
        /// （R8 的隔行实测，两次跑都是 4.83 = 基准），所以 1.7.0 把它删掉了——
        /// 菜单上留一个"选了什么也不会发生"的项比没有更糟。真正的自动要内核级
        /// <c>--deinterlace=-1</c>，那是另一件事（见 docs/1.7.0计划.md 的 F9）。
        /// </para>
        /// </summary>
        public static IReadOnlyList<(string Label, string Value)> All { get; } = new[]
        {
            ("关闭", Off),
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

        /// <summary>
        /// 把存下来的值归一成表里认得的那个；空值 / 认不出来的一律返回 <see cref="Off"/>。
        /// <para>
        /// 1.7.0 删掉了 <c>auto</c>（libvlc 3 不认这个模式名，实测等于关闭）。老设置 / 老设置方案 /
        /// 导入的 json 里可能还存着它：读进来就归成"关闭"——它本来就没起作用，所以行为不变，
        /// 不需要专门提示用户"你的设置被改了"。
        /// </para>
        /// </summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Off;

            var trimmed = value!.Trim();

            foreach (var (_, mode) in All)
            {
                if (string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase)) return mode;
            }

            return Off;
        }
    }
}
