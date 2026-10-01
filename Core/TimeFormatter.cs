using System;
using System.Globalization;

namespace 播放器.Core
{
    /// <summary>
    /// 时间格式化工具，用于进度条两侧与播放列表的时长列。
    /// </summary>
    public static class TimeFormatter
    {
        /// <summary>
        /// 格式化为 <c>mm:ss</c> 或 <c>h:mm:ss</c>（不足一小时时省略小时位）。
        /// </summary>
        public static string Format(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;

            var totalHours = (int)time.TotalHours;
            return totalHours > 0
                ? $"{totalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{time.Minutes:00}:{time.Seconds:00}";
        }

        /// <summary>
        /// 格式化为 <c>hh:mm:ss</c>（始终带小时位），用于进度显示避免宽度跳动。
        /// </summary>
        public static string FormatWithHours(TimeSpan time)
        {
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
        }

        /// <summary>
        /// 解析用户输入的时间码。<c>90</c>（秒）、<c>1:30</c>、<c>00:12:34.5</c> 都认。
        /// <para>从右往左数：最后一段是秒（可带小数），依次往前是分、时。</para>
        /// </summary>
        public static bool TryParse(string? text, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var parts = text.Trim().Split(':');
            if (parts.Length is < 1 or > 3) return false;

            // 用固定区域性解析，免得在别处被当成"1,5"这种本地化小数写法
            if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return false;

            var minutes = 0d;
            var hours = 0d;

            if (parts.Length >= 2 &&
                !double.TryParse(parts[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes))
                return false;

            if (parts.Length >= 3 &&
                !double.TryParse(parts[^3], NumberStyles.Float, CultureInfo.InvariantCulture, out hours))
                return false;

            if (seconds < 0 || minutes < 0 || hours < 0) return false;
            if (seconds >= 60 || minutes >= 60) return false;

            result = TimeSpan.FromSeconds(hours * 3600 + minutes * 60 + seconds);
            return true;
        }

        /// <summary>
        /// 把毫秒偏移格式化成 <c>+100 ms</c> / <c>0 ms</c> / <c>−250 ms</c>，
        /// 供音画与字幕延迟显示使用。
        /// </summary>
        public static string FormatMilliseconds(long milliseconds) =>
            milliseconds switch
            {
                0 => "0 ms",
                > 0 => $"+{milliseconds} ms",
                _ => $"−{Math.Abs(milliseconds)} ms"
            };
    }
}
