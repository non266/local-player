// 第 8 项的一部分：字幕外观的纯逻辑（1.23.0）。
//
// 只验"设置怎么归一、内核参数长什么样、签名会不会区分四个值"——真画面在第 13 项量。
// 这样把"逻辑错"和"渲染没生效"分开：红了先看日志是哪一半。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>字幕外观：默认值不生成参数、坏值归一、四个值都进签名、设置往返。</summary>
        private static bool CheckSubtitleAppearanceSettings()
        {
            var defaults = SubtitleAppearance.Default;

            if (!defaults.IsDefault || defaults.ToCoreArguments().Count != 0 || defaults.Describe() != "默认")
            {
                Log(8, "字幕外观：默认值就生成了参数（应为空，保持 libvlc 自己的外观）");
                return false;
            }

            if (defaults.Signature != "||-1|-1")
            {
                Log(8, $"字幕外观：默认签名不对（「{defaults.Signature}」）");
                return false;
            }

            var normalized = SubtitleAppearance.Normalize("XLARGE", "Yellow", 4, 160);

            if (normalized.Size != "xlarge" || normalized.Color != "yellow" ||
                normalized.Outline != 4 || normalized.Background != 160)
            {
                Log(8, "字幕外观：已知值没有归一（大小写不该影响）"
                        + $"（{normalized.Size} / {normalized.Color} / {normalized.Outline} / {normalized.Background}）");
                return false;
            }

            var bogus = SubtitleAppearance.Normalize("huge", "red", 2, 999);

            if (!bogus.IsDefault)
            {
                Log(8, "字幕外观：认不出来的值没有退回默认"
                        + $"（{bogus.Size} / {bogus.Color} / {bogus.Outline} / {bogus.Background}）");
                return false;
            }

            var arguments = normalized.ToCoreArguments();

            var expected = new[]
            {
                "--freetype-rel-fontsize=8",
                "--freetype-color=16776960",
                "--freetype-outline-thickness=4",
                "--freetype-background-opacity=160",
                "--freetype-background-color=0"
            };

            if (!arguments.SequenceEqual(expected))
            {
                Log(8, $"字幕外观：生成的内核参数不对（{string.Join(" ", arguments)}）");
                return false;
            }

            // 四个值都必须进签名：否则"换了颜色 / 描边 / 背景"看起来像没变
            var signatures = new HashSet<string>
            {
                defaults.Signature,
                new SubtitleAppearance { Size = "small" }.Signature,
                new SubtitleAppearance { Color = "cyan" }.Signature,
                new SubtitleAppearance { Outline = 4 }.Signature,
                new SubtitleAppearance { Background = 64 }.Signature
            };

            if (signatures.Count != 5)
            {
                Log(8, "字幕外观：四个值没有全都进签名（换一个值签名不变，就等于没换）");
                return false;
            }

            // 引擎参数：默认一个都不加；给了外观就加；旋转参数不能丢
            if (播放器.Core.PlayerEngine.CoreArguments(ScreenRotation.None)
                .Any(argument => argument.Contains("freetype", StringComparison.Ordinal)))
            {
                Log(8, "字幕外观：默认外观也进了内核参数");
                return false;
            }

            var core = 播放器.Core.PlayerEngine.CoreArguments(ScreenRotation.Clockwise90, normalized);

            if (!core.Contains("--freetype-rel-fontsize=8") || !core.Contains("--freetype-color=16776960"))
            {
                Log(8, $"字幕外观：内核参数里没有外观参数（{string.Join(" ", core)}）");
                return false;
            }

            if (!core.Any(argument => argument.StartsWith("--transform-type=", StringComparison.Ordinal)))
            {
                Log(8, "字幕外观：加了外观参数之后旋转参数丢了");
                return false;
            }

            // 设置往返（含 JSON 键名），坏值读进来要归一
            var folder = Path.Combine(AppContext.BaseDirectory, "subtitle-appearance-settings");
            var file = Path.Combine(folder, "settings.json");

            try
            {
                var settings = new AppSettings();
                normalized.ApplyTo(settings);
                settings.SaveTo(file);

                var loaded = AppSettings.LoadFrom(file);
                var reread = SubtitleAppearance.FromSettings(loaded);

                if (reread.Signature != normalized.Signature)
                {
                    Log(8, $"字幕外观：设置往返之后不一致（{reread.Signature}，期望 {normalized.Signature}）");
                    return false;
                }

                if (!File.ReadAllText(file).Contains("SubtitleFontSize", StringComparison.Ordinal))
                {
                    Log(8, "字幕外观：设置文件里没有新的字段名（JSON 键名不对）");
                    return false;
                }

                var broken = new AppSettings { SubtitleFontSize = "huge", SubtitleColor = "red", SubtitleOutline = 9, SubtitleBackground = 999 };

                if (!SubtitleAppearance.FromSettings(broken).IsDefault)
                {
                    Log(8, "字幕外观：设置里的坏值没有归一成默认");
                    return false;
                }

                Log(8, "字幕外观的逻辑正确（默认不生成参数、坏值归一、四个值都进签名、设置往返正常）");
                return true;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }
    }
}
