using System;
using System.Collections.Generic;
using System.Linq;

// 字幕外观（1.23.0）：libvlc 的纯文本字幕（SRT / 内嵌文本轨）的字号、颜色、描边、背景。
//
// 🔴 为什么是"建内核时定死"的：libvlc 3 里这组参数是 --freetype-*，只有实例（内核）级才认；
// libvlc.dll 里根本没有运行时改字幕外观的 API（只有 libvlc_video_set_spu / _spu_delay）。
// 实测（1.23.0 的探针）：
//   · 内核级 --freetype-rel-fontsize / --freetype-color 真的改变画面；
//   · 媒体级（:freetype-…）**一个都不生效**，和 transform 滤镜是同一个结论；
//   · 截图里包含字幕（关字幕后墨迹从 1675 掉到 0），所以效果是可量的。
// 所以外观由 PlayerEngine 构造时读一次设置，改完设置要**下次启动程序**才生效——
// 会话内不新建内核，避免重蹈 M8（内核 / 播放器越攒越多）的坑。
//
// 另一条边界同样重要：ASS / SSA 字幕走 libass、按字幕文件自己的样式画，**不受这里影响**；
// DVD 的图形字幕（SPU 位图）也不受影响。只有纯文本字幕会变。

namespace 播放器.Core
{
    /// <summary>字号预设：libvlc 的 <c>--freetype-rel-fontsize</c> 实测是"画面高度 ÷ 这个值"。</summary>
    public readonly struct SubtitleSizeOption
    {
        public SubtitleSizeOption(string id, string label, int relFontSize)
        {
            Id = id;
            Label = label;
            RelFontSize = relFontSize;
        }

        /// <summary>设置里存的值；空串 = 默认（不传参数，保持 libvlc 自己的字号）。</summary>
        public string Id { get; }

        public string Label { get; }

        public int RelFontSize { get; }
    }

    /// <summary>颜色预设：值是 <c>0xRRGGBB</c>（libvlc 的 <c>--freetype-color</c> 收十进制整数）。</summary>
    public readonly struct SubtitleColorOption
    {
        public SubtitleColorOption(string id, string label, int value)
        {
            Id = id;
            Label = label;
            Value = value;
        }

        public string Id { get; }

        public string Label { get; }

        public int Value { get; }
    }

    /// <summary>描边预设（-1 = 默认，不传参数）。</summary>
    public readonly struct SubtitleOutlineOption
    {
        public SubtitleOutlineOption(int value, string label)
        {
            Value = value;
            Label = label;
        }

        public int Value { get; }

        public string Label { get; }
    }

    /// <summary>背景预设（-1 = 默认，不传参数；否则是不透明度 0~255）。</summary>
    public readonly struct SubtitleBackgroundOption
    {
        public SubtitleBackgroundOption(int value, string label)
        {
            Value = value;
            Label = label;
        }

        public int Value { get; }

        public string Label { get; }
    }

    /// <summary>
    /// 字幕外观的一套取值。<b>默认值 = 一个参数都不生成</b>，保证"没动过"的时候和以前完全一样。
    /// </summary>
    public sealed class SubtitleAppearance
    {
        public static IReadOnlyList<SubtitleSizeOption> Sizes { get; } = new[]
        {
            new SubtitleSizeOption(string.Empty, "默认", 0),
            new SubtitleSizeOption("small", "小", 30),
            new SubtitleSizeOption("large", "大", 12),
            new SubtitleSizeOption("xlarge", "特大", 8)
        };

        public static IReadOnlyList<SubtitleColorOption> Colors { get; } = new[]
        {
            new SubtitleColorOption(string.Empty, "默认", 0),
            new SubtitleColorOption("white", "白", 0xFFFFFF),
            new SubtitleColorOption("yellow", "黄", 0xFFFF00),
            new SubtitleColorOption("cyan", "青", 0x00FFFF),
            new SubtitleColorOption("green", "绿", 0x00FF00)
        };

        public static IReadOnlyList<SubtitleOutlineOption> Outlines { get; } = new[]
        {
            new SubtitleOutlineOption(-1, "默认"),
            new SubtitleOutlineOption(1, "细"),
            new SubtitleOutlineOption(4, "粗")
        };

        public static IReadOnlyList<SubtitleBackgroundOption> Backgrounds { get; } = new[]
        {
            new SubtitleBackgroundOption(-1, "默认"),
            new SubtitleBackgroundOption(64, "淡"),
            new SubtitleBackgroundOption(160, "浓")
        };

        /// <summary>字号预设 id（空 = 默认）。</summary>
        public string Size { get; set; } = string.Empty;

        /// <summary>颜色预设 id（空 = 默认）。</summary>
        public string Color { get; set; } = string.Empty;

        /// <summary>描边厚度（-1 = 默认）。</summary>
        public int Outline { get; set; } = -1;

        /// <summary>背景不透明度（-1 = 默认）。</summary>
        public int Background { get; set; } = -1;

        /// <summary>一个参数都不传的那一套。</summary>
        public static SubtitleAppearance Default => new SubtitleAppearance();

        /// <summary>四个值都是默认：构造内核时不加任何参数。</summary>
        public bool IsDefault =>
            Size.Length == 0 && Color.Length == 0 && Outline < 0 && Background < 0;

        /// <summary>字号预设对应的相对字号；默认返回 0。</summary>
        /// <remarks>
        /// 实测语义是<b>分母</b>（画面高度 ÷ 这个值）：30 → 约 12 像素、12 → 约 30、8 → 约 45
        /// （640×360 素材上的包围盒高度；默认 0 时由 libvlc 自己算，约 20 像素）。
        /// 所以数字越小字越大——见 1.23.0 计划 §10.1 的探针数字。
        /// </remarks>
        public int RelFontSize =>
            Sizes.FirstOrDefault(option => string.Equals(option.Id, Size, StringComparison.Ordinal)).RelFontSize;

        /// <summary>颜色预设对应的 <c>0xRRGGBB</c>；默认返回 0。</summary>
        public int ColorValue =>
            Colors.FirstOrDefault(option => string.Equals(option.Id, Color, StringComparison.Ordinal)).Value;

        /// <summary>给状态栏 / 对话框看的一句话；全默认时是「默认」。</summary>
        public string Describe()
        {
            if (IsDefault) return "默认";

            var parts = new List<string>();

            if (Size.Length > 0) parts.Add("字号 " + SizeLabel(Size));
            if (Color.Length > 0) parts.Add("颜色 " + ColorLabel(Color));
            if (Outline >= 0) parts.Add("描边 " + OutlineLabel(Outline));
            if (Background >= 0) parts.Add("背景 " + BackgroundLabel(Background));

            return string.Join(" · ", parts);
        }

        /// <summary>
        /// 归一：认不出来的值一律退回默认（设置被手改过、方案文件来自旧版本时都走这里）。
        /// </summary>
        public static SubtitleAppearance Normalize(string? size, string? color, int outline, int background) =>
            new SubtitleAppearance
            {
                Size = MatchId(Sizes.Select(option => option.Id), size),
                Color = MatchId(Colors.Select(option => option.Id), color),
                Outline = Outlines.Any(option => option.Value == outline) ? outline : -1,
                Background = Backgrounds.Any(option => option.Value == background) ? background : -1
            };

        /// <summary>从设置里读一套（读完就归一）。</summary>
        public static SubtitleAppearance FromSettings(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            return Normalize(
                settings.SubtitleFontSize,
                settings.SubtitleColor,
                settings.SubtitleOutline,
                settings.SubtitleBackground);
        }

        /// <summary>写回设置（值已经归一过，界面直接存）。</summary>
        public void ApplyTo(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            settings.SubtitleFontSize = Size;
            settings.SubtitleColor = Color;
            settings.SubtitleOutline = Outline;
            settings.SubtitleBackground = Background;
        }

        /// <summary>
        /// 内核参数：只对非默认项生成 <c>--freetype-*</c>。空表 == 保持 libvlc 自己的外观。
        /// </summary>
        public IReadOnlyList<string> ToCoreArguments()
        {
            var arguments = new List<string>();

            if (Size.Length > 0) arguments.Add("--freetype-rel-fontsize=" + RelFontSize);
            if (Color.Length > 0) arguments.Add("--freetype-color=" + ColorValue);
            if (Outline >= 0) arguments.Add("--freetype-outline-thickness=" + Outline);

            if (Background >= 0)
            {
                arguments.Add("--freetype-background-opacity=" + Background);
                arguments.Add("--freetype-background-color=0");
            }

            return arguments;
        }

        /// <summary>稳定的签名（日志 / 断言 / 比较用；全默认是 <c>||-1|-1</c>）。</summary>
        public string Signature => Size + "|" + Color + "|" + Outline + "|" + Background;

        public static string SizeLabel(string id) =>
            Sizes.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal)).Label ?? "默认";

        public static string ColorLabel(string id) =>
            Colors.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal)).Label ?? "默认";

        public static string OutlineLabel(int value) =>
            Outlines.FirstOrDefault(option => option.Value == value).Label ?? "默认";

        public static string BackgroundLabel(int value) =>
            Backgrounds.FirstOrDefault(option => option.Value == value).Label ?? "默认";

        private static string MatchId(IEnumerable<string> ids, string? value)
        {
            var text = (value ?? string.Empty).Trim();

            if (text.Length == 0) return string.Empty;

            foreach (var id in ids)
            {
                if (string.Equals(id, text, StringComparison.OrdinalIgnoreCase)) return id;
            }

            return string.Empty;
        }
    }
}
