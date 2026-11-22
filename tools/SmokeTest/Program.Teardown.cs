// 关窗收尾探针与默认断言（1.18.0 排 0xCFFFFFFF 起步，1.29.0 扩成"逐轮计时 + 有界收尾"）。
//
// 复现的是第 11 项检查里出问题的那条路：先放"只有音轨的 mkv"进音频卡片，再把只有视频轨的
// mkv 塞进同一个窗体（窗口形态要从卡片切回三栏），然后立刻 停引擎 → 泵 250ms → Close → Dispose。
// 全量冒烟里这一条偶发把进程打挂（Windows 事件日志记的是 Application Hang，日志停在
// "按真实轨道判形态正常"那一行），单跑又正常——所以做成"同一进程里连做 N 次"的压测。
//
// 1.29.0：两处用法共用 <see cref="RunCloseRounds"/>——
//   ① 默认冒烟第 30 步「关窗收尾」：6 轮，逐轮断言关窗 ≤ 2500 ms、追踪顺序对、资源增长有界；
//   ② GATED 探针 `播放器_TEST_TEARDOWN=N`：N 轮，先量基线、修后再量，门槛写在探针里。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>关窗耗时的门槛（毫秒）。</summary>
        private const int CloseBudgetMilliseconds = 2500;

        /// <summary>关窗那一段允许的线程 / 句柄增量（每轮）。</summary>
        private const int CloseThreadBudget = 5;

        private const int CloseHandleBudget = 30;

        /// <summary>一轮压测的结果。</summary>
        private sealed class CloseRound
        {
            public int Round;
            public long CloseMilliseconds;

            /// <summary>关窗那一段自己的资源增量（创建窗体 / 开引擎不算）。</summary>
            public long ThreadDelta;
            public long HandleDelta;

            /// <summary>从第一轮之前算起的累计增量（只打印，不判定）。</summary>
            public long CumulativeThread;
            public long CumulativeHandle;

            /// <summary>这一次收尾是不是"超时后放弃等待"（正常路径必须为 false）。</summary>
            public bool TimedOut;

            /// <summary>放引擎之前，画面输出真的摘掉了吗（1.18.0 那条坑的硬证据）。</summary>
            public bool Detached;

            public string? Failure;
        }

        /// <summary>GATED 探针入口：`播放器_TEST_TEARDOWN=N` 时在全部步骤之前跑 N 轮关窗压测。</summary>
        private static bool RunTeardownStress()
        {
            var raw = Environment.GetEnvironmentVariable("播放器_TEST_TEARDOWN");

            if (raw is not ("1" or "true") && !(int.TryParse(raw, out var parsed) && parsed > 0))
            {
                Log(35, "关窗压测：没设 播放器_TEST_TEARDOWN，跳过（设成轮数，例如 20）");
                return true;
            }

            var rounds = 20;
            if (int.TryParse(raw, out var value) && value > 0) rounds = value;

            var results = RunCloseRounds(rounds, out var reason);

            if (results == null)
            {
                Log(35, "关窗压测：跳过——" + reason);
                return true;
            }

            var closeTimes = results.Select(round => round.CloseMilliseconds).OrderBy(ms => ms).ToList();
            var failures = results.Where(round => round.Failure != null).ToList();

            foreach (var round in results)
            {
                Log(35, $"关窗压测 {round.Round}/{rounds}：关窗 {round.CloseMilliseconds} ms、"
                        + $"线程 {round.ThreadDelta:+#;-#;0}、句柄 {round.HandleDelta:+#;-#;0}"
                        + (round.Failure == null ? string.Empty : "；" + round.Failure));
            }

            var p50 = closeTimes[closeTimes.Count / 2];
            var p95 = closeTimes[Math.Min(closeTimes.Count - 1, (int)Math.Ceiling(closeTimes.Count * 0.95) - 1)];
            var max = closeTimes[^1];
            var maxThreadDelta = results.Max(round => round.ThreadDelta);
            var maxHandleDelta = results.Max(round => round.HandleDelta);
            var cumulativeThread = results.Max(round => round.CumulativeThread);
            var cumulativeHandle = results.Max(round => round.CumulativeHandle);

            Log(35, $"关窗压测汇总：{rounds} 轮，关窗 P50 {p50} ms / P95 {p95} ms / 最大 {max} ms；"
                    + $"关窗那一段线程最多 +{maxThreadDelta}、句柄最多 +{maxHandleDelta}；"
                    + $"（累计 +{cumulativeThread} / +{cumulativeHandle}，含建窗体开引擎，只记录不判定）"
                    + $"失败 {failures.Count} 轮");

            if (failures.Count > 0)
            {
                Log(35, "关窗压测：有轮次失败（见上面每轮的原文）");
                return false;
            }

            if (max > CloseBudgetMilliseconds)
            {
                Log(35, $"关窗压测：最慢一轮 {max} ms 超过门槛 {CloseBudgetMilliseconds} ms");
                return false;
            }

            if (maxThreadDelta > CloseThreadBudget || maxHandleDelta > CloseHandleBudget)
            {
                Log(35, $"关窗压测：关窗那一段的资源增长越界（线程 +{maxThreadDelta} / 句柄 +{maxHandleDelta}，"
                        + $"门槛是 +{CloseThreadBudget} / +{CloseHandleBudget}）");
                return false;
            }

            Log(35, "关窗压测通过：没有挂起、没有崩，关窗耗时与关窗那一段的资源增长都在门槛内");
            return true;
        }

        /// <summary>
        /// 连做 <paramref name="rounds"/> 轮"音频卡片 → 视频 → 关窗"，每轮记关窗毫秒与资源增量。
        /// <para>返回 <c>null</c> 表示环境不成立（没有 ffmpeg / 造不出样本），第二轮起才有意义。</para>
        /// </summary>
        private static List<CloseRound>? RunCloseRounds(int rounds, out string reason)
        {
            reason = string.Empty;
            var results = new List<CloseRound>();

            if (!TryCreateAudioOnlyMkv(out var audioOnly, out reason)) return null;

            if (!TryCreateTinyVideo(out var video, out reason))
            {
                TryDelete(audioOnly);
                return null;
            }

            // 窗口形态必须跟着"真实轨道"走：全量冒烟默认钉成「总是视频窗口」（见 Program.Main），
            // 那样只有音轨的 mkv 永远进不了音频卡片，这条压测就变成了"三栏 → 三栏"的空跑。
            // 照第 11 项的写法：先改设置、再构造窗体，收尾时把原设置写回去。
            var defaults = AppSettings.Load();
            var setup = AppSettings.Load();
            setup.WindowMode = WindowModes.Auto;
            setup.Save();

            try
            {
                long threadsBefore;
                long handlesBefore;

                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    threadsBefore = process.Threads.Count;
                    handlesBefore = process.HandleCount;
                }

                for (var round = 1; round <= rounds; round++)
                {
                    播放器.MainForm form = null!;
                    var result = new CloseRound { Round = round };

                    try
                    {
                        form = new 播放器.MainForm(Array.Empty<string>());
                        form.Show();
                        PumpMessages(150);

                        form.Engine.Open(audioOnly);
                        form.Engine.Play();

                        if (!PumpUntil(() => form.Engine.GetTracks().Count > 0, 15000))
                        {
                            result.Failure = "只有音轨的 mkv 没解析出轨道";
                            results.Add(result);
                            break;
                        }

                        if (!PumpUntil(() => form.IsAudioWindow, 8000))
                        {
                            result.Failure = "只有音轨的 mkv 没进音频卡片";
                            results.Add(result);
                            break;
                        }

                        form.Engine.Open(video);
                        form.Engine.Play();

                        if (!PumpUntil(() => form.Engine.GetTracks().Count > 0, 15000))
                        {
                            result.Failure = "视频 mkv 没解析出轨道";
                            results.Add(result);
                            break;
                        }

                        if (!PumpUntil(() => !form.IsAudioWindow, 8000))
                        {
                            result.Failure = "真的带视频轨的 mkv 没切回三栏";
                            results.Add(result);
                            break;
                        }
                    }
                    finally
                    {
                        if (form != null)
                        {
                            long threadsBeforeClose;
                            long handlesBeforeClose;

                            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                            {
                                threadsBeforeClose = process.Threads.Count;
                                handlesBeforeClose = process.HandleCount;
                            }

                            // 和冒烟里那一模一样的收尾顺序
                            form.Engine.Stop();
                            PumpMessages(250);

                            var watch = System.Diagnostics.Stopwatch.StartNew();
                            form.Close();
                            result.CloseMilliseconds = watch.ElapsedMilliseconds;
                            watch.Stop();
                            form.Dispose();

                            result.TimedOut = form.LastShutdownTimedOut;
                            result.Detached = form.VideoOutputDetachedBeforeTeardown;

                            // 只看"关窗这一段"的资源增量：创建窗体 / 开引擎那部分本来就会攒
                            // （1.5.0 记过：每个 libvlc 实例自带一整套线程），混进来就量不出收尾本身。
                            using (var process = System.Diagnostics.Process.GetCurrentProcess())
                            {
                                result.ThreadDelta = process.Threads.Count - threadsBeforeClose;
                                result.HandleDelta = process.HandleCount - handlesBeforeClose;
                            }
                        }
                    }

                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        result.CumulativeThread = process.Threads.Count - threadsBefore;
                        result.CumulativeHandle = process.HandleCount - handlesBefore;
                    }

                    results.Add(result);

                    Journal($"关窗压测 {round}/{rounds} 完成（关窗 {result.CloseMilliseconds} ms）");
                }
            }
            finally
            {
                defaults.Save();
                TryDelete(audioOnly);
                TryDelete(video);
            }

            return results;
        }

        /// <summary>
        /// 第 30 个运行步骤「关窗收尾」= 第 35 项检查（1.29.0）。
        /// <para>
        /// 验的是"产品不再被收尾挂住"这条机制，而不是"libvlc 那个卡点消失了"：
        /// ① 6 轮真实关窗（音频卡片 → 视频 → 关窗）逐轮 ≤ 2500 ms、不超时；
        /// ② 收尾步骤顺序对（先摘画面输出、再放引擎、最后收尾完成）；
        /// ③ 关窗那一段的资源增长有界；
        /// ④ 注入 4 秒慢收尾：上界到点就放弃等待（关窗 ≤ 3500 ms、追踪里有「收尾超时」）。
        /// </para>
        /// </summary>
        private static bool TestCloseTeardown()
        {
            const int rounds = 6;

            var results = RunCloseRounds(rounds, out var reason);

            if (results == null)
            {
                Log(35, "关窗收尾：跳过——" + reason);
                return true;
            }

            if (results.Count < rounds)
            {
                var failed = results.FirstOrDefault(round => round.Failure != null);
                Log(35, $"关窗收尾：只跑完 {results.Count}/{rounds} 轮就停了"
                        + (failed?.Failure == null ? string.Empty : "——" + failed.Failure));
                return false;
            }

            foreach (var round in results)
            {
                if (round.Failure != null)
                {
                    Log(35, $"关窗收尾：第 {round.Round} 轮失败——{round.Failure}");
                    return false;
                }

                if (round.TimedOut)
                {
                    Log(35, $"关窗收尾：第 {round.Round} 轮正常关窗却走了超时放弃（关窗 "
                            + $"{round.CloseMilliseconds} ms）");
                    return false;
                }

                if (!round.Detached)
                {
                    Log(35, $"关窗收尾：第 {round.Round} 轮放引擎之前没有摘掉画面输出"
                            + "（1.18.0 那条坑会回来：vout 线程还攥着窗体句柄）");
                    return false;
                }

                if (round.CloseMilliseconds > CloseBudgetMilliseconds)
                {
                    Log(35, $"关窗收尾：第 {round.Round} 轮关窗用了 {round.CloseMilliseconds} ms"
                            + $"（门槛 {CloseBudgetMilliseconds} ms）");
                    return false;
                }

                if (round.ThreadDelta > CloseThreadBudget || round.HandleDelta > CloseHandleBudget)
                {
                    Log(35, $"关窗收尾：第 {round.Round} 轮关窗那一段涨了 {round.ThreadDelta} 个线程 / "
                            + $"{round.HandleDelta} 个句柄（门槛 +{CloseThreadBudget} / +{CloseHandleBudget}）");
                    return false;
                }
            }

            var trace = 播放器.Ui.ShutdownTrace.LastSteps;
            var history = TraceIndexOf(trace, "刷新历史");
            var detach = TraceIndexOf(trace, "摘画面输出");
            var engine = TraceIndexOf(trace, "放引擎（有界 2 秒）");
            var finished = TraceIndexOf(trace, "收尾完成");

            if (history != 0)
            {
                Log(35, "关窗收尾：追踪里第一条不是「刷新历史」（" + string.Join(" → ", trace) + "）");
                return false;
            }

            if (detach < 0 || engine < 0 || finished < 0)
            {
                Log(35, "关窗收尾：追踪里少了关键步骤（" + string.Join(" → ", trace) + "）");
                return false;
            }

            if (detach > engine)
            {
                Log(35, "关窗收尾：先放了引擎、后摘画面输出（顺序反了，1.18.0 那条坑会回来）");
                return false;
            }

            if (engine > finished)
            {
                Log(35, "关窗收尾：追踪里「放引擎」排在「收尾完成」之后（顺序乱了）");
                return false;
            }

            if (!CheckSlowTeardownBound()) return false;

            var p50 = results.Select(round => round.CloseMilliseconds).OrderBy(ms => ms).ToList()[results.Count / 2];
            var max = results.Max(round => round.CloseMilliseconds);

            Log(35, "关窗收尾正常：6 轮真实关窗 P50 " + p50 + " ms / 最大 " + max + " ms，"
                    + "没有一轮超时；收尾顺序是「刷新历史 → … → 摘画面输出 → 放引擎（有界 2 秒） → … → 收尾完成」，"
                    + "4 秒的慢收尾在 2 秒上界处被放弃等待、界面照常关掉");
            return true;
        }

        /// <summary>
        /// 慢收尾注入：4 秒的释放必须在 2 秒上界处放弃等待——界面照常关，追踪里留一行。
        /// </summary>
        private static bool CheckSlowTeardownBound()
        {
            var previous = Environment.GetEnvironmentVariable("播放器_TEST_SLOWTEARDOWN");
            Environment.SetEnvironmentVariable("播放器_TEST_SLOWTEARDOWN", "4000");

            try
            {
                播放器.MainForm form = null!;

                try
                {
                    form = new 播放器.MainForm(Array.Empty<string>());
                    form.Show();
                    PumpMessages(200);

                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    form.Close();
                    watch.Stop();

                    if (!form.LastShutdownTimedOut)
                    {
                        Log(35, "慢收尾：4 秒的释放居然在上界内就放完了，没有按 2 秒放弃等待");
                        return false;
                    }

                    if (!form.VideoOutputDetachedBeforeTeardown)
                    {
                        Log(35, "慢收尾：放引擎之前没有摘掉画面输出（1.18.0 那条坑会回来）");
                        return false;
                    }

                    if (watch.ElapsedMilliseconds > 3500)
                    {
                        Log(35, $"慢收尾：上界是 2 秒，关窗却用了 {watch.ElapsedMilliseconds} ms");
                        return false;
                    }

                    if (watch.ElapsedMilliseconds < 1500)
                    {
                        Log(35, $"慢收尾：上界没生效——关窗只用 {watch.ElapsedMilliseconds} ms 就放弃了"
                                + "（应该等满 2 秒左右）");
                        return false;
                    }

                    if (TraceIndexOf(form.CloseTrace, "收尾超时") < 0)
                    {
                        Log(35, "慢收尾：追踪里没有「收尾超时」这一行（旁边没有证据说明它放弃了等待）");
                        return false;
                    }

                    Log(35, $"慢收尾正常：4 秒的释放等了 {watch.ElapsedMilliseconds} ms、到 2 秒上界后放弃，"
                            + "界面照常关掉");
                }
                finally
                {
                    form?.Dispose();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("播放器_TEST_SLOWTEARDOWN", previous);
            }

            return true;
        }

        /// <summary>在收尾追踪里找一步（按前缀匹配：「收尾超时（2000 ms 仍未放完）」这种带数字的也算）。</summary>
        private static int TraceIndexOf(IReadOnlyList<string> steps, string name)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                if (steps[i].StartsWith(name, StringComparison.Ordinal)) return i;
            }

            return -1;
        }
    }
}
