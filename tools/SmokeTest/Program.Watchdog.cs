// 看门狗（1.22.0 C）：任何一步卡住超过阈值 → 写一份迷你转储 + 留一行日志 + 以退出码 3 结束。
//
// 为什么要有它：1.21.0 验收时第 13 步「音轨记忆」挂死 15 分钟——界面线程不响应、CPU 不动、
// 没有任何对话框，只能眼看着它卡着（最后是手动杀进程）。要查这种挂死，最缺的其实是
// "卡在哪一段"与"卡住那一刻的现场"，而不是一次 15 分钟的白等。
//
// 心跳的设计要点：<see cref="PumpMessages"/> 每圈、<see cref="Journal"/> 每行都刷新一次时间戳。
// 所以**合法但很久不泵消息**的操作（ffmpeg 等待 ≤60 秒、Media.Parse ≤15 秒）不会误触；
// 只有"界面线程被真正卡死、连消息泵都不转了"才会到阈值。
//
// 阈值默认 180 秒，可用 `播放器_TEST_WATCHDOG`（秒）改，0 关掉。
//
// 如实说明：本机没有 cdb / dotnet-dump，转储**当场分析不了**。它保证的是"不再白等、
// 现场留下来、能拿到别的机器分析"，与 1.20.0 的崩溃取证是同一条口径。

using System.Diagnostics;

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>默认阈值：180 秒。比现有最长的合法阻塞（ffmpeg 60 秒）留了 3 倍余量。</summary>
        private const long DefaultWatchdogMilliseconds = 180_000;

        private const int WatchdogPollMilliseconds = 5000;

        /// <summary>迷你转储的类型：MiniDumpNormal | MiniDumpWithThreadInfo。</summary>
        private const uint MiniDumpNormalWithThreadInfo = 0x00000000 | 0x00001000;

        private static long _watchdogThresholdMilliseconds = DefaultWatchdogMilliseconds;
        private static long _watchdogHeartbeatTicks;
        private static volatile string _watchdogStep = "启动";
        private static Thread? _watchdogThread;
        private static volatile bool _watchdogStopped = true;

        /// <summary>开始看门。阈值读 <c>播放器_TEST_WATCHDOG</c>（秒），0 表示关掉。</summary>
        private static void StartWatchdog()
        {
            var raw = Environment.GetEnvironmentVariable("播放器_TEST_WATCHDOG");

            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out var seconds))
            {
                if (seconds <= 0)
                {
                    Journal("看门狗：按 播放器_TEST_WATCHDOG=" + raw + " 关掉（不检查挂死）");
                    return;
                }

                _watchdogThresholdMilliseconds = seconds * 1000L;
            }

            _watchdogHeartbeatTicks = Stopwatch.GetTimestamp();
            _watchdogStopped = false;

            _watchdogThread = new Thread(WatchdogLoop)
            {
                IsBackground = true,
                Name = "smoke-watchdog"
            };

            _watchdogThread.Start();

            Journal($"看门狗：任何一步连续 {_watchdogThresholdMilliseconds / 1000} 秒没有心跳就写迷你转储、"
                    + "以退出码 3 结束（阈值用 播放器_TEST_WATCHDOG 改，0 关掉）");
        }

        private static void StopWatchdog()
        {
            _watchdogStopped = true;

            try { _watchdogThread?.Join(1000); }
            catch (Exception) { /* 线程已经退出，不影响结论 */ }

            _watchdogThread = null;
        }

        /// <summary>当前跑到哪一步（看门狗触发时写进日志）。</summary>
        private static void SetWatchdogStep(string name)
        {
            _watchdogStep = name;
            WatchdogHeartbeat();
        }

        /// <summary>刷新心跳：消息泵每转一圈、日志每写一行都算"我还活着"。</summary>
        internal static void WatchdogHeartbeat() =>
            Interlocked.Exchange(ref _watchdogHeartbeatTicks, Stopwatch.GetTimestamp());

        private static void WatchdogLoop()
        {
            while (!_watchdogStopped)
            {
                Thread.Sleep(WatchdogPollMilliseconds);

                if (_watchdogStopped) return;

                var last = Interlocked.Read(ref _watchdogHeartbeatTicks);
                var elapsed = (Stopwatch.GetTimestamp() - last) * 1000.0 / Stopwatch.Frequency;

                if (elapsed < _watchdogThresholdMilliseconds) continue;

                var step = _watchdogStep;
                var seconds = (long)(elapsed / 1000);

                // 先把标记写进日志（写日志很快），再写转储：即使写转储失败/很慢，
                // "卡在哪一步"这条信息也已经落到磁盘上了。
                Journal("疑似挂死：第「" + step + "」步，最后一条日志之后已 " + seconds
                        + " 秒没有心跳（阈值 " + (_watchdogThresholdMilliseconds / 1000) + " 秒）"
                        + "；正在写转储……");

                var dump = TryWriteHangDump();

                var message = "疑似挂死：第「" + step + "」步，最后一条日志之后已 " + seconds
                              + " 秒没有心跳（阈值 " + (_watchdogThresholdMilliseconds / 1000) + " 秒）"
                              + (dump == null
                                  ? "；写转储失败"
                                  : "；转储：" + dump + "（" + new FileInfo(dump).Length + " 字节）");

                Console.WriteLine();
                Console.WriteLine("!! " + message);
                Journal(message);
                Console.Out.Flush();

                // 用 Exit 而不是 FailFast：FailFast 会让 WER 再记一次崩溃、在 CrashDumps 里多一份
                // 与本问题无关的转储（1.20.0 的取证会被它污染）。退出码 3 是"冒烟挂死"的专用记号。
                Environment.Exit(3);
            }
        }

        /// <summary>给自己写一份迷你转储；失败返回 <c>null</c>（没有转储也要照样报红，不能悄悄放过）。</summary>
        private static string? TryWriteHangDump()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "smoke-hang-" + Environment.ProcessId + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".dmp");

            try
            {
                using var process = Process.GetCurrentProcess();
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

                var written = MiniDumpWriteDump(
                    process.Handle, (uint)process.Id, file.SafeFileHandle.DangerousGetHandle(),
                    MiniDumpNormalWithThreadInfo, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                if (written) return path;

                file.Close();
                TryDelete(path);
                return null;
            }
            catch (Exception)
            {
                TryDelete(path);
                return null;
            }
        }

        /// <summary>某个时刻之后生成的看门狗转储（自检用；没有返回 <c>null</c>）。</summary>
        private static string? NewestHangDump(DateTime? since = null)
        {
            try
            {
                var folder = Path.GetTempPath();

                return Directory.GetFiles(folder, "smoke-hang-*.dmp")
                    .Select(file => new FileInfo(file))
                    .Where(info => since == null || info.LastWriteTimeUtc >= since.Value.ToUniversalTime())
                    .OrderByDescending(info => info.LastWriteTimeUtc)
                    .Select(info => info.FullName)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// GATED：验看门狗真的拦得住挂死——起一个 `--watchdog-selftest` 子进程（阈值压到 2 秒、故意睡 60 秒），
        /// 断言它<b>以退出码 3 结束</b>、<b>写了一份转储</b>、<b>自己的日志里有「疑似挂死」</b>。
        /// <para>
        /// 为什么要自检：一个"阈值比天还大"或"从来没被启动"的看门狗，看起来一切正常，
        /// 真挂死时照样白等——和 1.21.0 三态判定那几条自检是同一个道理。
        /// </para>
        /// </summary>
        private static bool CheckWatchdogSelfTest()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("播放器_TEST_WATCHDOG_SELFTEST"), "1", StringComparison.Ordinal))
            {
                Log(19, "看门狗自检：没设 播放器_TEST_WATCHDOG_SELFTEST=1，跳过"
                        + "（这一段会真的起一个子进程、把自己挂死 10 秒再拦下来）");
                return true;
            }

            var appHost = Path.Combine(AppContext.BaseDirectory, "SmokeTest.exe");

            if (!File.Exists(appHost))
            {
                Log(19, "看门狗自检：找不到 SmokeTest.exe（用 dotnet 跑的时候它也在输出目录里），跳过");
                return true;
            }

            var started = DateTime.UtcNow;

            try
            {
                using var child = Process.Start(new ProcessStartInfo(appHost, "--watchdog-selftest")
                {
                    UseShellExecute = false
                });

                if (child == null)
                {
                    Log(19, "看门狗自检：子进程起不来");
                    return false;
                }

                var exited = child.WaitForExit(20000);
                var exitCode = exited ? child.ExitCode : -1;

                if (!exited)
                {
                    try { child.Kill(entireProcessTree: true); } catch (Exception) { }
                }

                if (!exited || exitCode != 3)
                {
                    Log(19, "看门狗自检：子进程没有以退出码 3 结束"
                            + $"（{(exited ? "退出码 " + exitCode : "20 秒都没退出")}）——挂死没被拦下来");
                    return false;
                }

                var childJournal = Path.Combine(Path.GetTempPath(), "smoke-watchdog-selftest-" + child.Id + ".txt");
                var dump = NewestHangDump(started);
                var logged = false;
                long dumpSize = 0;

                try
                {
                    if (File.Exists(childJournal))
                        logged = File.ReadAllText(childJournal).Contains("疑似挂死", StringComparison.Ordinal);
                }
                catch (Exception)
                {
                    // 读不出来就按"没记下"处理，下面会红
                }

                if (dump != null) dumpSize = new FileInfo(dump).Length;

                // 清掉自检产物：它是"故意挂死"造出来的，不该留在临时目录里冒充真实现场
                TryDelete(childJournal);
                if (dump != null) TryDelete(dump);

                if (!logged)
                {
                    Log(19, "看门狗自检：子进程退出了，但它自己的日志里没有「疑似挂死」这一行");
                    return false;
                }

                if (dump == null)
                {
                    Log(19, "看门狗自检：子进程退出了，但没有写转储");
                    return false;
                }

                Log(19, $"看门狗自检正确：假挂死（阈值 2 秒）被子进程自己拦下，退出码 3、"
                        + $"转储 {dumpSize} 字节、日志写明「疑似挂死」");
                return true;
            }
            catch (Exception ex)
            {
                Log(19, "看门狗自检：起子进程失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// GATED：`--watchdog-selftest` 子进程自己会走这里——故意睡 10 秒，看门狗压到 2 秒，
        /// 必须被拦下来（退出码 3 + 一份转储 + 日志里有「疑似挂死」）。
        /// 自检子进程用自己的一份日志文件，不碰父进程的 smoke-journal.txt。
        /// </summary>
        private static int RunWatchdogSelfTest()
        {
            JournalPathOverride = Path.Combine(
                Path.GetTempPath(), "smoke-watchdog-selftest-" + Environment.ProcessId + ".txt");

            ResetJournal();

            Environment.SetEnvironmentVariable("播放器_TEST_WATCHDOG", "2");

            StartWatchdog();
            SetWatchdogStep("看门狗自检（故意挂死 60 秒）");

            Journal("看门狗自检：故意睡 60 秒，阈值 2 秒，应该被拦下来");

            Console.WriteLine("看门狗自检：故意挂死 60 秒……");
            Thread.Sleep(60_000);

            Console.WriteLine("看门狗自检失败：睡了 60 秒也没有被拦下来");
            return 4;
        }
    }
}
