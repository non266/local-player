using System;
using System.Globalization;
using LibVLCSharp.Shared;

// 「媒体信息」页里那些要算一下才给人看的细节。
//
// 全是纯函数：输入是 libvlc 报上来的数字，输出是界面上的文字。放这里而不是塞在 MainForm 里，
// 是因为它们最容易写错（比例、码率单位、H.264 的 profile / level 编号），
// 单独留一份就能直接断言边界，不必为了验一个除法去起一个窗口。
//
// ⚠ 一条硬规矩：**读不到就返回 null，界面就不显示这一项**。libvlc 3 对不少封装
// 不填 Profile / Level / Description（尤其 .mkv 里的某些编码），拿 0 当"Baseline@1.0"写出来
// 比不写更糟——用户会以为那是真的。

namespace 播放器.Core
{
    internal static class MediaDescriptions
    {
        /// <summary>ISO 639-2/1 的常见语言码 → 中文名；认不出的原样返回，<c>und</c>（未标注）返回 null。</summary>
        public static string? Language(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;

            var trimmed = code.Trim();
            if (trimmed.Equals("und", StringComparison.OrdinalIgnoreCase)) return null;

            return trimmed.ToLowerInvariant() switch
            {
                "eng" or "en" => "英语",
                "chi" or "zho" or "zh" => "中文",
                "jpn" or "ja" => "日语",
                "kor" or "ko" => "韩语",
                "fra" or "fre" or "fr" => "法语",
                "deu" or "ger" or "de" => "德语",
                "spa" or "es" => "西班牙语",
                "ita" or "it" => "意大利语",
                "rus" or "ru" => "俄语",
                _ => trimmed
            };
        }

        /// <summary>
        /// H.264 的 <c>High@4.1</c> 写法。
        /// <para>
        /// 只对 H.264 做这个翻译：libvlc 给的 profile / level 是<b>编码器自己的编号</b>，
        /// 换一种编码含义就完全不同（HEVC 的 profile 是 1/2/3，不是 66/77/100），
        /// 硬套一张表会把别的编码写成错的。认不出就返回 null、界面不显示。
        /// </para>
        /// </summary>
        public static string? ProfileLevel(uint codecFourCc, int profile, int level)
        {
            if (profile <= 0 || level <= 0) return null;
            if (!IsH264(codecFourCc)) return null;

            var profileName = profile switch
            {
                66 => "Baseline",
                77 => "Main",
                88 => "Extended",
                100 => "High",
                110 => "High10",
                122 => "High422",
                244 => "High444",
                _ => null
            };

            var levelName = level switch
            {
                9 => "1b",
                10 => "1.0",
                11 => "1.1",
                12 => "1.2",
                13 => "1.3",
                20 => "2.0",
                21 => "2.1",
                22 => "2.2",
                30 => "3.0",
                31 => "3.1",
                32 => "3.2",
                40 => "4.0",
                41 => "4.1",
                42 => "4.2",
                50 => "5.0",
                51 => "5.1",
                52 => "5.2",
                60 => "6.0",
                61 => "6.1",
                62 => "6.2",
                _ => null
            };

            if (profileName == null || levelName == null) return null;

            return profileName + "@" + levelName;
        }

        /// <summary>
        /// 显示宽高比（像素宽高比 SAR 换算成显示比例 DAR）。
        /// <para>
        /// 方像素（1:1）返回 null——绝大多数文件都是方像素，写一句「显示 1:1」纯属噪音。
        /// 能约成小整数的给「16:9」，约不动的给「2.37:1」。
        /// </para>
        /// </summary>
        public static string? AspectRatio(uint width, uint height, uint sarNum, uint sarDen)
        {
            if (width == 0 || height == 0) return null;

            if (sarNum == 0 || sarDen == 0)
            {
                sarNum = 1;
                sarDen = 1;
            }

            var displayWidth = (long)width * sarNum;
            var displayHeight = (long)height * sarDen;

            if (displayWidth == displayHeight) return null;

            var divisor = GreatestCommonDivisor(displayWidth, displayHeight);
            var numerator = displayWidth / divisor;
            var denominator = displayHeight / divisor;

            if (numerator <= 64 && denominator <= 64) return numerator + ":" + denominator;

            return (displayWidth / (double)displayHeight).ToString("0.##", CultureInfo.InvariantCulture) + ":1";
        }

        /// <summary>码率：不到 1 Mbps 用整数 kbps，超过就写 Mbps（两位小数）；0 或负数返回 null。</summary>
        public static string? Bitrate(long bitsPerSecond)
        {
            if (bitsPerSecond < 1000) return null;

            if (bitsPerSecond < 1_000_000) return (bitsPerSecond / 1000) + " kbps";

            return (bitsPerSecond / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps";
        }

        /// <summary>画面方位：正常（<c>TopLeft</c>）返回 null，其余给出人话。</summary>
        public static string? Orientation(VideoOrientation orientation) => orientation switch
        {
            VideoOrientation.TopLeft => null,
            VideoOrientation.TopRight => "水平翻转",
            VideoOrientation.BottomLeft => "垂直翻转",
            VideoOrientation.BottomRight => "旋转 180°",
            VideoOrientation.LeftTop => "顺时针 90°",
            VideoOrientation.RightTop => "顺时针 90° · 翻转",
            VideoOrientation.LeftBottom => "逆时针 90° · 翻转",
            VideoOrientation.RightBottom => "逆时针 90°",
            _ => null
        };

        /// <summary>投影方式：普通平面返回 null，全景 / 立方体才写。</summary>
        public static string? Projection(VideoProjection projection) => projection switch
        {
            VideoProjection.Equirectangular => "360° 全景",
            VideoProjection.CubemapLayoutStandard => "360° 立方体贴图",
            _ => null
        };

        private static bool IsH264(uint fourCc) =>
            fourCc is H264FourCc or Avc1FourCc or H264UpperFourCc;

        private const uint H264FourCc = ('h' << 24) | ('2' << 16) | ('6' << 8) | '4';
        private const uint H264UpperFourCc = ('H' << 24) | ('2' << 16) | ('6' << 8) | '4';
        private const uint Avc1FourCc = ('a' << 24) | ('v' << 16) | ('c' << 8) | '1';

        private static long GreatestCommonDivisor(long a, long b)
        {
            a = Math.Abs(a);
            b = Math.Abs(b);

            while (b != 0)
            {
                var remainder = a % b;
                a = b;
                b = remainder;
            }

            return a == 0 ? 1 : a;
        }
    }
}
