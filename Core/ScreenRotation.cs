using System;
using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>
    /// 画面旋转 / 翻转（<b>按文件记住</b>）。
    /// <para>
    /// 它是 libvlc 的 <c>transform</c> 视频滤镜（<b>媒体级</b>选项），所以改它要重载媒体才生效；
    /// 而且<b>只转画面本身</b>：窗口比例、字幕、桌面歌词都不跟着转
    /// （竖屏手机视频转到能看，靠的就是这个）。
    /// </para>
    /// </summary>
    public enum ScreenRotation
    {
        /// <summary>不旋转（默认）。</summary>
        None = 0,

        /// <summary>顺时针 90°。</summary>
        Clockwise90,

        /// <summary>180°（上下颠倒）。</summary>
        UpsideDown,

        /// <summary>逆时针 90°。</summary>
        CounterClockwise90,

        /// <summary>水平翻转（左右镜像）。</summary>
        FlipHorizontal,

        /// <summary>垂直翻转（上下镜像）。</summary>
        FlipVertical
    }

    /// <summary>
    /// <see cref="ScreenRotation"/> 的取值表：菜单文案 ↔ libvlc 的 <c>transform-type</c> 取值，
    /// 以及"存进 history.json 的那串字"怎么读回来。
    /// <para>
    /// 单列一处是因为菜单、按文件恢复、纯函数断言三处都要用同一份对应关系；
    /// 而 libvlc 这边收的是<b>字符串</b>取值（<c>90</c> / <c>180</c> / <c>270</c> /
    /// <c>hflip</c> / <c>vflip</c>），写错一个字母它只会安静地什么都不做。
    /// </para>
    /// </summary>
    public static class ScreenRotations
    {
        /// <summary>libvlc 那个滤镜模块的名字。</summary>
        public const string VideoFilter = "transform";

        /// <summary>滤镜的选项名（值由 <see cref="TransformType"/> 给）。</summary>
        public const string TransformTypeOption = "transform-type";

        /// <summary>取值表：界面顺序，也就是菜单里的顺序。</summary>
        public static IReadOnlyList<(string Label, ScreenRotation Value)> All { get; } = new[]
        {
            ("不旋转", ScreenRotation.None),
            ("顺时针 90°", ScreenRotation.Clockwise90),
            ("180°（上下颠倒）", ScreenRotation.UpsideDown),
            ("逆时针 90°", ScreenRotation.CounterClockwise90),
            ("水平翻转（左右镜像）", ScreenRotation.FlipHorizontal),
            ("垂直翻转（上下镜像）", ScreenRotation.FlipVertical)
        };

        /// <summary>
        /// 传给 libvlc 的 <c>transform-type</c> 取值；<see cref="ScreenRotation.None"/> 时为 <c>null</c>
        /// （那时候<b>连滤镜都不挂</b>：挂了再设 0 也是"旋转 90°"）。
        /// </summary>
        public static string? TransformType(ScreenRotation rotation) => rotation switch
        {
            ScreenRotation.Clockwise90 => "90",
            ScreenRotation.UpsideDown => "180",
            ScreenRotation.CounterClockwise90 => "270",
            ScreenRotation.FlipHorizontal => "hflip",
            ScreenRotation.FlipVertical => "vflip",
            _ => null
        };

        /// <summary>显示名；认不出来的值按"不旋转"。</summary>
        public static string Describe(ScreenRotation rotation)
        {
            foreach (var (label, value) in All)
                if (value == rotation) return label;

            return "不旋转";
        }

        /// <summary>存进 <c>history.json</c> 的那串字（枚举名，读得懂也稳定）。</summary>
        public static string ToStorage(ScreenRotation rotation) => rotation.ToString();

        /// <summary>
        /// 从 <c>history.json</c> 读回来。
        /// <para>
        /// 认三种写法：枚举名（我们现在写的）、libvlc 的取值（<c>90</c> / <c>hflip</c>，
        /// 手改过文件也认）、以及数字。认不出来一律当<b>不旋转</b>——
        /// 老记录里没有这个字段，读出来就是空串，正好落在这里。
        /// </para>
        /// </summary>
        public static ScreenRotation Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ScreenRotation.None;

            var text = value.Trim();

            if (Enum.TryParse<ScreenRotation>(text, ignoreCase: true, out var parsed) &&
                Enum.IsDefined(typeof(ScreenRotation), parsed))
            {
                return parsed;
            }

            return text.ToLowerInvariant() switch
            {
                "90" => ScreenRotation.Clockwise90,
                "180" => ScreenRotation.UpsideDown,
                "270" => ScreenRotation.CounterClockwise90,
                "hflip" => ScreenRotation.FlipHorizontal,
                "vflip" => ScreenRotation.FlipVertical,
                _ => ScreenRotation.None
            };
        }
    }
}
