using System;

namespace 播放器.Core
{
    /// <summary>
    /// A-B 循环的纯逻辑：设点校验、越界判断、写给人看的那句话。
    /// <para>
    /// 抽成纯函数是为了"能直接被断言"。越界判断的<b>边界</b>（引擎时间正好落在 B 点那一拍
    /// 算不算越界）在真实引擎上验不干脆——<c>Time</c> 是浮动的，泵到某个时刻再看它落在哪，
    /// 永远碰不到"正好等于 B"。判据写在 <see cref="ShouldWrap"/> 上，边界才是确定的。
    /// </para>
    /// </summary>
    public static class AbLoop
    {
        /// <summary>
        /// 把 A 点设在 <paramref name="valueMs"/>：能设返回 <c>null</c>，不能设返回一句原因。
        /// <para>A 点必须<b>严格早于</b> B 点；越界时如实拒绝，不偷偷挪成"B 点前一点"。</para>
        /// </summary>
        public static string? RejectStart(long valueMs, long? endMs)
        {
            if (valueMs < 0) return "A 点不能是负数";

            if (endMs.HasValue && valueMs >= endMs.Value)
                return $"A 点 {Describe(valueMs)} 不早于 B 点 {Describe(endMs.Value)}，这次没有设";

            return null;
        }

        /// <summary>
        /// 把 B 点设在 <paramref name="valueMs"/>：能设返回 <c>null</c>，不能设返回一句原因。
        /// <para>B 点必须<b>严格晚于</b> A 点。</para>
        /// </summary>
        public static string? RejectEnd(long valueMs, long? startMs)
        {
            if (valueMs <= 0) return "B 点在开头（0 秒），再往后放一点";

            if (startMs.HasValue && valueMs <= startMs.Value)
                return $"B 点 {Describe(valueMs)} 不晚于 A 点 {Describe(startMs.Value)}，这次没有设";

            return null;
        }

        /// <summary>
        /// 引擎时间到了这儿就该跳回 A 点：<b>含"正好等于 B"</b>。
        /// </summary>
        /// <remarks>
        /// 用 <c>&gt;=</c> 而不是 <c>&gt;</c>：界面计时器 200 ms 一拍，正好落在 B 的那一拍
        /// 用 <c>&gt;</c> 会把它放过去，等于多放一整个周期——听感上就是"循环点比设的松"。
        /// </remarks>
        public static bool ShouldWrap(long timeMs, long? startMs, long? endMs) =>
            startMs.HasValue && endMs.HasValue && timeMs >= endMs.Value;

        /// <summary>菜单与状态栏里那句"当前 A-B 循环"。</summary>
        public static string Describe(long? startMs, long? endMs)
        {
            var start = startMs.HasValue ? Describe(startMs.Value) : "未设";
            var end = endMs.HasValue ? Describe(endMs.Value) : "未设";

            return $"A {start} → B {end}";
        }

        /// <summary>
        /// 精确到 0.1 秒的时间点。
        /// <para>秒级精度不够用：设 A 点时看不出"设在 1.0 秒"和"设在 1.9 秒"的区别。</para>
        /// </summary>
        public static string Describe(long milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(milliseconds);
            if (time < TimeSpan.Zero) time = TimeSpan.Zero;

            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";
        }
    }
}
