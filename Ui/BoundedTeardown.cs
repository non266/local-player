using System;
using System.Diagnostics;
using System.Threading;

namespace 播放器.Ui
{
    /// <summary>
    /// 有界收尾（1.29.0）：把一段可能卡住的释放动作交给后台线程，界面只泵等一个上界。
    /// <para>
    /// 为什么是"上界"而不是修根因：关窗挂起（Application Hang / `0xCFFFFFFF`）从 1.18.0 起
    /// 偶发，形状是"卡在 libvlc 的收尾里"，本机没有 cdb / windbg、也拿不到稳定的复现夹具。
    /// 但<b>"卡住"和"让用户看见程序无响应"是两件事</b>——用户按下关闭之后，界面不该陪着原生
    /// 线程一起被 Windows 判成挂起。所以这里只做两件事：把释放挪到后台、给界面一个时间上界；
    /// 超时就把这件事记下来，窗体照常关（后台线程是 background，进程退出时由系统回收）。
    /// </para>
    /// <para>
    /// ⚠ 上界内完成时行为与以前完全一样（同步放完再往下走）；只有超时才走"放弃等待"那条路。
    /// 泵消息用的是 <see cref="System.Windows.Forms.Application.DoEvents"/>：等待期间界面照样
    /// 响应，不会触发 Windows 的"无响应"判定。
    /// </para>
    /// </summary>
    internal static class BoundedTeardown
    {
        /// <summary>
        /// 在后台执行 <paramref name="action"/>，最多等 <paramref name="limit"/>。
        /// 返回 <c>true</c> = 在限内完成；<c>false</c> = 超时（已经调过 <paramref name="onTimeout"/>）。
        /// <para>
        /// ⚠ <b>等待期间刻意不泵消息</b>。1.29.0 的第一版是"一边等一边 <c>DoEvents</c>，界面照样响应"，
        /// 结果在冒烟里当场原生崩了（`0xC0000005`，崩在 <c>libvlc_audio_get_delay</c>）：
        /// 后台线程正在释放引擎，泵进来的消息里却有引擎刚投递的"状态变了"回调，
        /// 那个回调正要去读一个已经在拆的 libvlc 对象——**用已释放的原生对象**。
        /// 关窗最多静默 2 秒（Windows 判"无响应"要 5 秒以上），比"拿消息泵和释放赛跑"划算得多。
        /// </para>
        /// </summary>
        public static bool Run(Action action, TimeSpan limit, Action<TimeSpan>? onTimeout)
        {
            var done = new ManualResetEventSlim(false);
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            })
            {
                IsBackground = true,
                Name = "shutdown-teardown"
            };

            thread.Start();

            var watch = Stopwatch.StartNew();
            var completed = done.Wait(limit);

            if (completed)
            {
                if (failure != null)
                    Core.AppLog.Swallowed("收尾时后台释放抛了异常", failure);

                return true;
            }

            onTimeout?.Invoke(watch.Elapsed);
            return false;
        }

        /// <summary>
        /// 给冒烟用的"慢收尾"注入：环境变量 <c>播放器_TEST_SLOWTEARDOWN</c> 是毫秒数，
        /// 大于 0 时在真正的释放之前先睡这么久，用来验证"上界到了就放弃等待"。
        /// </summary>
        public static int SlowTeardownMilliseconds
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("播放器_TEST_SLOWTEARDOWN");
                return int.TryParse(raw, out var value) && value > 0 ? value : 0;
            }
        }
    }
}
