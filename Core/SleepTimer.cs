using System;

namespace 播放器.Core
{
    /// <summary>
    /// 睡眠定时器：到点就**暂停**（不是退出程序），或者"听完这一首就停"。
    /// <para>
    /// 抽成纯逻辑（时钟从外面传进来）是为了能被直接断言：到点判定、剩余时间、
    /// "听完这一首"那种不靠时间到期——这些都不该靠"泵几秒钟看它停没停"来验。
    /// </para>
    /// <para>
    /// 时钟用单调的 <c>Environment.TickCount64</c>（毫秒），不用墙上时间：
    /// 用户改系统时间不该让定时器提前或延后。
    /// </para>
    /// </summary>
    public sealed class SleepTimer
    {
        private SleepTimer(bool endOfTrack, long dueAtMilliseconds)
        {
            IsEndOfTrack = endOfTrack;
            DueAtMilliseconds = dueAtMilliseconds;
        }

        /// <summary>"听完这一首就停"：不看到期时间，等当前这一首自然放完。</summary>
        public bool IsEndOfTrack { get; }

        /// <summary>按时间到点的那一种：到点的时刻（单调时钟毫秒）。"听完这一首"时无意义。</summary>
        public long DueAtMilliseconds { get; }

        /// <summary>听完这一首就停。</summary>
        public static SleepTimer EndOfTrack() => new SleepTimer(true, 0);

        /// <summary>从现在起 <paramref name="delay"/> 之后停。</summary>
        /// <param name="nowMilliseconds">当前单调时钟（<c>Environment.TickCount64</c>）。</param>
        public static SleepTimer After(long nowMilliseconds, TimeSpan delay)
        {
            var milliseconds = (long)Math.Max(0, delay.TotalMilliseconds);

            return new SleepTimer(false, nowMilliseconds + milliseconds);
        }

        /// <summary>还剩多少毫秒（到点之后是 0）。</summary>
        public long RemainingMilliseconds(long nowMilliseconds) =>
            IsEndOfTrack ? 0 : Math.Max(0, DueAtMilliseconds - nowMilliseconds);

        /// <summary>到点了吗（"听完这一首"那种不看时间，永远返回 false）。</summary>
        public bool IsDue(long nowMilliseconds) =>
            !IsEndOfTrack && nowMilliseconds >= DueAtMilliseconds;

        /// <summary>菜单第一行那句"当前是什么状态"。</summary>
        public string Describe(long nowMilliseconds) =>
            IsEndOfTrack ? "听完这一首就停" : DescribeRemaining(RemainingMilliseconds(nowMilliseconds));

        /// <summary>把剩余毫秒数写成一句人话（向上取整到秒：显示"还剩 1 秒"却过 0.2 秒就到点会显得很怪）。</summary>
        public static string DescribeRemaining(long remainingMilliseconds)
        {
            var seconds = Math.Max(0, (int)Math.Ceiling(remainingMilliseconds / 1000.0));

            if (seconds <= 0) return "马上就到";

            var minutes = seconds / 60;
            var rest = seconds % 60;

            if (minutes <= 0) return $"还剩 {rest} 秒";
            if (rest == 0) return $"还剩 {minutes} 分钟";

            return $"还剩 {minutes} 分 {rest} 秒";
        }
    }
}
