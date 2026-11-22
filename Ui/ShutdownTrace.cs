using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace 播放器.Ui
{
    /// <summary>
    /// 收尾过程的分步追踪（1.29.0）：关窗时每做一段就记一行，卡住时看门狗把这份"走到哪一步"
    /// 写进日志。
    /// <para>
    /// 为什么需要它：音频窗口关闭那一处从 1.18.0 起偶发 <b>Application Hang</b>（退出码
    /// `0xCFFFFFFF`），现场只有"最后一条成功日志"——日志停在「按真实轨道判形态正常」之后，
    /// 但那之后收尾还有十几段。没有分步记录，下一轮还是只能猜是"放引擎"还是"放系统媒体控件"。
    /// </para>
    /// <para>
    /// 只记名字与相对耗时，<b>不碰窗口、不碰引擎、不写磁盘</b>：它要在收尾的每一步之间被调用，
    /// 自己不能再引入任何阻塞。环形容量 48，一次收尾最多十几步，留足余量。
    /// </para>
    /// </summary>
    internal static class ShutdownTrace
    {
        private const int Capacity = 48;

        private static readonly object Gate = new object();
        private static readonly List<string> Steps = new List<string>(Capacity);
        private static long _startTicks;

        /// <summary>开始一次新的收尾追踪（清掉上一次的）。</summary>
        public static void Begin()
        {
            lock (Gate)
            {
                Steps.Clear();
                _startTicks = Stopwatch.GetTimestamp();
            }
        }

        /// <summary>记一步。名字是给日志看的短句（「摘画面输出」「放引擎（有界 2 秒）」）。</summary>
        public static void Step(string name)
        {
            lock (Gate)
            {
                if (Steps.Count >= Capacity) Steps.RemoveAt(0);

                var elapsed = _startTicks == 0
                    ? 0
                    : (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;

                Steps.Add($"{name}（+{elapsed:0} ms）");
            }
        }

        /// <summary>当前（或最后一次）收尾走过的步骤；没跑过收尾时是空表。</summary>
        public static IReadOnlyList<string> LastSteps
        {
            get
            {
                lock (Gate) return Steps.ToArray();
            }
        }

        /// <summary>给看门狗 / 日志用的一行：`刷新历史 → 停计时器 → …`。</summary>
        public static string Dump()
        {
            lock (Gate) return Steps.Count == 0 ? string.Empty : string.Join(" → ", Steps);
        }

        /// <summary>测试用：清空（免得一次收尾的记录串到下一个用例里）。</summary>
        internal static void ResetForTest()
        {
            lock (Gate)
            {
                Steps.Clear();
                _startTicks = 0;
            }
        }
    }
}
