using System;
using System.Globalization;

namespace 播放器.Core
{
    /// <summary>
    /// 歌词时间轴的手动偏移：歌词和声音对不上时，整体提前或延后。
    /// <para>
    /// 符号与 LRC 的 <c>[offset:]</c> 完全一致：<b>正值 = 歌词提前显示</b>
    /// （每一行都比原时间轴早这么多出现），实现上是「时间戳 − 偏移」。
    /// 界面上永远把方向和数字一起写出来（"提前 1.5 秒"），不让用户去猜正负号。
    /// </para>
    /// <para>
    /// 这里只做数值上的事（步长、范围、格式、解析），不碰界面也不碰文件：
    /// 偏移是<b>按文件</b>记的，存哪儿由 <see cref="PlaybackHistory"/> 决定。
    /// </para>
    /// </summary>
    public static class LyricsOffset
    {
        /// <summary>按一下加减多少毫秒。</summary>
        public const int StepMilliseconds = 500;

        /// <summary>能调到的极限（正负都是它）。再大就不像微调了，多半是拿错了歌词。</summary>
        public const int LimitMilliseconds = 30_000;

        /// <summary>把偏移钳进允许范围。</summary>
        public static long Clamp(long milliseconds) =>
            Math.Clamp(milliseconds, -LimitMilliseconds, LimitMilliseconds);

        /// <summary>顶到极限了没有（用来提示"已经到头了"）。</summary>
        public static bool IsAtLimit(long milliseconds) =>
            Math.Abs(Clamp(milliseconds)) >= LimitMilliseconds;

        /// <summary>
        /// 按一档加减。<paramref name="direction"/> 为正表示"歌词提前"，为负表示"歌词延后"。
        /// </summary>
        public static long Step(long current, int direction) =>
            Clamp(current + Math.Sign(direction) * StepMilliseconds);

        /// <summary>
        /// 描述偏移方向和大小：<c>"提前 1.5 秒"</c> / <c>"延后 0.5 秒"</c> / <c>"不偏移"</c>。
        /// <para>刻意不带"歌词"两个字——调用方那边已经有"歌词偏移"这个标签了。</para>
        /// </summary>
        public static string Describe(long milliseconds)
        {
            var clamped = Clamp(milliseconds);
            if (clamped == 0) return "不偏移";

            var seconds = FormatSeconds(Math.Abs(clamped) / 1000.0);

            return clamped > 0 ? $"提前 {seconds} 秒" : $"延后 {seconds} 秒";
        }

        /// <summary>很短的说法，给角落上的小字用：<c>"+1.5s"</c> / <c>"-0.5s"</c>；不偏移时返回空串。</summary>
        public static string DescribeShort(long milliseconds)
        {
            var clamped = Clamp(milliseconds);
            if (clamped == 0) return string.Empty;

            var sign = clamped > 0 ? "+" : "−";
            return sign + FormatSeconds(Math.Abs(clamped) / 1000.0) + "s";
        }

        /// <summary>秒数去掉多余的零：0.5 / 1.5 / 30（不写 0.500）。</summary>
        private static string FormatSeconds(double seconds) =>
            seconds.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// 解析手输入的偏移。认识这几种写法：<c>0.5</c>（秒）、<c>-1.5</c>、<c>+2</c>、
        /// <c>750ms</c>、<c>0.5s</c>、<c>500毫秒</c>、<c>1.5秒</c>。
        /// </summary>
        public static bool TryParse(string? text, out long milliseconds, out string error)
        {
            milliseconds = 0;
            error = string.Empty;

            var value = (text ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                error = "请输入一个秒数，例如 0.5、-1.5、750ms。";
                return false;
            }

            // 默认按秒算；写 ms / 毫秒 就按毫秒
            var multiplier = 1000.0;

            if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1.0;
                value = value.Substring(0, value.Length - 2).Trim();
            }
            else if (value.EndsWith("毫秒", StringComparison.Ordinal))
            {
                multiplier = 1.0;
                value = value.Substring(0, value.Length - 2).Trim();
            }
            else if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase) ||
                     value.EndsWith("秒", StringComparison.Ordinal))
            {
                value = value.Substring(0, value.Length - 1).Trim();
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                double.IsNaN(number) || double.IsInfinity(number))
            {
                error = "看不懂这个数字，请输入例如 0.5、-1.5、750ms。";
                return false;
            }

            var total = number * multiplier;

            // 超范围直接说清楚，而不是悄悄钳到边界——那样用户会以为自己输错了别的地方
            if (Math.Abs(total) > LimitMilliseconds + 0.5)
            {
                error = $"偏移最多 ±{LimitMilliseconds / 1000} 秒。";
                return false;
            }

            milliseconds = (long)Math.Round(total);
            return true;
        }
    }
}
