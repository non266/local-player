// 检查点 29：网络探测的去重、冷却与有界（1.17.0）。
//
// 1.13.0 把"在不在"丢到后台之后，被放弃的探测只是逻辑作废：那条 Task 照样跑，
// 而离线 SMB 上一次 File.Exists 实测卡 82～93 秒（1.17.0 计划 §2）。连点十几下就攒十几条卡住的线程。
// 这里验五件事，全部用假探测、不碰网络：
//   ① 同一路径重复点只起一条；
//   ② 超时之后冷却期内不再真的去问，并说一句「刚试过」；
//   ③ 冷却过去之后会重新问；
//   ④ 在飞上限 4 条（第 5 条立刻说「太多」）；
//   ⑤ 探测跑在专用线程上（不占线程池的工作线程）。

using System.Threading.Tasks;

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckProbeDedupeAndBounds()
        {
            const string first = @"\\192.0.2.20\share\first.wav";

            try
            {
                // ---- 1～4：同一路径的去重 / 冷却 / 冷却过期 ----
                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(200);

                    var statuses = new List<string>();
                    form.Playback.StatusChanged += (_, text) => statuses.Add(text);

                    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    bool? onPool = null;

                    form.Playback.PathProbe = _ =>
                    {
                        onPool = Thread.CurrentThread.IsThreadPoolThread;
                        return gate.Task;
                    };

                    // 故意把超时放长：前两步只验"有没有起探测"。
                    form.Playback.ProbeTimeout = TimeSpan.FromSeconds(30);

                    form.Playlist.Clear();
                    form.AddPathsForTest(new[] { first });

                    var starts = form.Playback.ProbeStarts;

                    form.Playback.PlayIndex(0, allowFallback: false);

                    if (!PumpUntil(() => onPool.HasValue, 5000))
                    {
                        Log(29, "网络路径没有进入后台探测");
                        return false;
                    }

                    if (onPool == true)
                    {
                        Log(29, "探测跑在线程池上（离线 SMB 卡住时会占着池里的工作线程）");
                        return false;
                    }

                    // 同一路径再点一次：应该复用那条在飞的，不新起。
                    form.Playback.PlayIndex(0, allowFallback: false);

                    if (form.Playback.ProbeStarts != starts + 1)
                    {
                        Log(29, $"同一路径点了两次，起了 {form.Playback.ProbeStarts - starts} 条探测（期望 1 条）");
                        return false;
                    }

                    // 超时：标「超时」、记冷却。
                    form.Playback.ProbeTimeout = TimeSpan.FromMilliseconds(100);
                    Thread.Sleep(150);
                    form.Playback.PumpPendingProbe();

                    if (form.Playback.HasPendingProbe)
                    {
                        Log(29, "探测超时之后还挂着");
                        return false;
                    }

                    if (form.Playlist.Items[0].ErrorMessage != PlaybackCoordinator.NetworkTimeoutMessage)
                    {
                        Log(29, "超时之后条目没有标成「" + PlaybackCoordinator.NetworkTimeoutMessage
                                + "」（现在是「" + form.Playlist.Items[0].ErrorMessage + "」）");
                        return false;
                    }

                    statuses.Clear();
                    form.Playback.PlayIndex(0, allowFallback: false);

                    if (form.Playback.ProbeStarts != starts + 1)
                    {
                        Log(29, $"冷却期内又起了一条探测（一共 {form.Playback.ProbeStarts - starts} 条）");
                        return false;
                    }

                    if (!statuses.Any(text => text.Contains("刚试过", StringComparison.Ordinal)))
                    {
                        Log(29, "冷却期内跳过时没说「刚试过」（状态栏：" + string.Join(" / ", statuses) + "）");
                        return false;
                    }

                    // 冷却过去之后要重新问一次。
                    form.Playback.ProbeCooldown = TimeSpan.FromMilliseconds(50);
                    Thread.Sleep(120);
                    form.Playback.PlayIndex(0, allowFallback: false);

                    if (form.Playback.ProbeStarts != starts + 2)
                    {
                        Log(29, $"冷却过去之后没有重新探测（{form.Playback.ProbeStarts - starts} 条，期望 2 条）");
                        return false;
                    }
                }

                // ---- 5：在飞上限 ----
                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(200);

                    var statuses = new List<string>();
                    form.Playback.StatusChanged += (_, text) => statuses.Add(text);

                    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                    form.Playback.PathProbe = _ => gate.Task;
                    form.Playback.ProbeTimeout = TimeSpan.FromSeconds(30);
                    form.Playback.MaxConcurrentProbes = 4;

                    var paths = new[]
                    {
                        @"\\192.0.2.31\share\p1.wav",
                        @"\\192.0.2.32\share\p2.wav",
                        @"\\192.0.2.33\share\p3.wav",
                        @"\\192.0.2.34\share\p4.wav",
                        @"\\192.0.2.35\share\p5.wav"
                    };

                    form.Playlist.Clear();
                    form.AddPathsForTest(paths);

                    var starts = form.Playback.ProbeStarts;

                    for (var i = 0; i < paths.Length; i++)
                        form.Playback.PlayIndex(i, allowFallback: false);

                    if (form.Playback.ProbeStarts - starts != 4)
                    {
                        Log(29, $"在飞上限没生效：5 条不同路径起了 {form.Playback.ProbeStarts - starts} 条探测（期望 4 条）");
                        return false;
                    }

                    if (form.Playback.ProbeInFlight != 4)
                    {
                        Log(29, $"在飞探测数是 {form.Playback.ProbeInFlight}（期望 4）");
                        return false;
                    }

                    if (!statuses.Any(text => text.Contains("太多", StringComparison.Ordinal)))
                    {
                        Log(29, "超上限的那一条没说「太多」（状态栏：" + string.Join(" / ", statuses) + "）");
                        return false;
                    }
                }

                // ---- 6：真实不可达地址（gated；默认不跑，免得每次冒烟都多等十秒） ----
                // 这里用**真的** File.Exists：本机实测一次要卡 82～93 秒（计划 §2）。
                // 要验的就是"探测卡住的时候界面还在跳、8 秒后如实说超时、冷却期内不再问"。
                if (Environment.GetEnvironmentVariable("播放器_TEST_NETSLOW") is { Length: > 0 })
                {
                    using var slow = new 播放器.MainForm(Array.Empty<string>());
                    slow.Show();
                    PumpMessages(200);

                    // RFC 5737 的 TEST-NET-1：保留给文档用，不可路由。
                    const string unreachable = @"\\192.0.2.1\share\probe.wav";

                    slow.Playlist.Clear();
                    slow.AddPathsForTest(new[] { unreachable });
                    slow.Playback.ProbeTimeout = TimeSpan.FromSeconds(8);

                    var slowStarts = slow.Playback.ProbeStarts;

                    slow.Playback.PlayIndex(0, allowFallback: false);
                    slow.Playback.PlayIndex(0, allowFallback: false);

                    if (slow.Playback.ProbeStarts - slowStarts != 1)
                    {
                        Log(29, $"真实网络：两次点击起了 {slow.Playback.ProbeStarts - slowStarts} 条探测（期望 1 条）");
                        return false;
                    }

                    var ticks = slow.UiTickCount;
                    PumpMessages(1000);

                    if (slow.UiTickCount - ticks < 3)
                    {
                        Log(29, $"真实网络：探测卡住期间界面计时器只跳了 {slow.UiTickCount - ticks} 拍");
                        return false;
                    }

                    if (!PumpUntil(() => slow.Playlist.Items[0].HasError, 20000))
                    {
                        Log(29, "真实网络：8 秒之后条目没有标成超时");
                        return false;
                    }

                    slow.Playback.PlayIndex(0, allowFallback: false);

                    if (slow.Playback.ProbeStarts - slowStarts != 1)
                    {
                        Log(29, "真实网络：冷却期内又起了一条探测（应该直接用「刚试过」跳过）");
                        return false;
                    }

                    Log(29, "真实网络（播放器_TEST_NETSLOW=1）：两次点击只起 1 条探测、界面计时器没停、"
                            + "8 秒后标超时、冷却期内不再问");
                }

                // ---- 7：引擎自己打开不可达网络流，界面到底停没停（gated，只量不判） ----
                // 反馈第 4 条从 1.13.0 一直留到现在：UNC 那一路已经交给后台探测（1.17.0），
                // 但 http / rtsp 是**引擎自己在界面线程上打开**的（PlaybackCoordinator.TryOpen → Open）。
                // libvlc 的 Play 理论上是异步的，这一段的数字就是把它钉住。
                // 变量写成秒数（默认 10）：想看"libvlc 多久才放弃"就设 60。
                if (Environment.GetEnvironmentVariable("播放器_TEST_NETOPEN") is { Length: > 0 } netOpenRaw)
                {
                    var waitSeconds = int.TryParse(netOpenRaw, out var parsedSeconds) && parsedSeconds > 0
                        ? parsedSeconds
                        : 10;

                    using var streamForm = new 播放器.MainForm(Array.Empty<string>());
                    streamForm.Show();
                    PumpMessages(200);

                    foreach (var uri in new[] { "http://192.0.2.1/probe.mp3", "rtsp://192.0.2.1/probe" })
                    {
                        streamForm.Playlist.Clear();
                        streamForm.AddPathsForTest(new[] { uri });

                        var ticksBefore = streamForm.UiTickCount;
                        var watch = System.Diagnostics.Stopwatch.StartNew();

                        streamForm.Playback.PlayIndex(0, allowFallback: false);

                        watch.Stop();
                        PumpMessages(1000);

                        var ticks = streamForm.UiTickCount - ticksBefore;
                        var reachedError = PumpUntil(
                            () => streamForm.Engine.State == 播放器.Core.PlayerState.Error,
                            waitSeconds * 1000);

                        Log(29, $"引擎打开不可达网络流（{uri}）：PlayIndex 用了 {watch.ElapsedMilliseconds} ms、"
                                + $"之后 1 秒界面计时器跳了 {ticks} 拍、"
                                + (reachedError
                                    ? $"{waitSeconds} 秒内变成「播放出错」"
                                    : $"{waitSeconds} 秒内状态还是「{streamForm.Engine.State}」"));
                    }
                }

                Log(29, "探测正常：同一路径只起一条、冷却期内不再问、冷却过去会重问、"
                        + "在飞最多 4 条、跑在专用线程上");
                return true;
            }
            catch (Exception ex)
            {
                Log(29, "探测去重与有界检查失败: " + ex);
                return false;
            }
        }
    }
}
