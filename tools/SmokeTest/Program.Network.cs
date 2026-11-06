// 检查点 25：网络位置播放（1.13.0）。
//
// 离线 NAS / SMB 路径上的 File.Exists 会等满超时（几十秒），而播放是在界面线程上发起的。
// 这一轮把"在不在"丢到后台：PlayIndex 立刻返回，探测回来再决定打开 / 标缺 / 顺延。
// 这里用不可达的 TEST-NET 地址 + 可控的假探测，绝不碰真实网络。

using System.Threading;
using System.Threading.Tasks;

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckNetworkPathPlayback()
        {
            var local = Path.Combine(AppContext.BaseDirectory, "net-local.wav");

            // RFC 5737 的 TEST-NET-1 / TEST-NET-2：保留给文档用，不可路由。
            // 测试里所有探测都由假委托接管，这两个地址一次都不会真的被访问。
            const string offline = @"\\192.0.2.1\share\offline.wav";
            const string late = @"\\192.0.2.2\share\late.wav";

            WriteWav(local, seconds: 5, frequency: 520);

            try
            {
                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(200);

                // ---- 1) 判"网络位置"不碰磁盘；IsOpenable 也不该在这里下"不存在"的结论 ----
                if (!MediaFormats.IsNetworkPath(offline))
                {
                    Log(25, "UNC 路径没有被认成网络位置");
                    return false;
                }

                if (!MediaFormats.IsOpenable(offline))
                {
                    Log(25, "网络路径被 IsOpenable 直接判成打不开——它还没探测就下了结论");
                    return false;
                }

                // ---- 2) 探测没回来之前：PlayIndex 立刻返回，且不打开媒体 ----
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.Playback.PathProbe = _ => gate.Task;

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { offline, local });

                var watch = System.Diagnostics.Stopwatch.StartNew();
                form.Playback.PlayIndex(0);
                watch.Stop();

                if (watch.ElapsedMilliseconds > 1000)
                {
                    Log(25, $"点网络条目时界面线程被同步检查卡住了（{watch.ElapsedMilliseconds} ms）");
                    return false;
                }

                if (!form.Playback.HasPendingProbe)
                {
                    Log(25, "网络路径没有进入后台探测（界面线程还在同步查盘）");
                    return false;
                }

                if (form.Engine.HasMedia)
                {
                    Log(25, "探测还没回来就已经打开了媒体——等于没等结果");
                    return false;
                }

                // ---- 3) 探测结果"不存在"：标出来、保留条目、顺延到本地那一项 ----
                gate.SetResult(false);
                form.Playback.PumpPendingProbe();

                if (!PumpUntil(
                        () => form.Engine.HasMedia &&
                              string.Equals(form.Engine.CurrentPath, local, StringComparison.OrdinalIgnoreCase),
                        15000))
                {
                    Log(25, "网络项探测失败后没有顺延到本地项（停在 "
                            + $"「{form.Engine.CurrentPath}」）");
                    return false;
                }

                if (form.Playlist.Count != 2)
                {
                    Log(25, $"网络项探测失败后被从列表里删掉了（现在 {form.Playlist.Count} 项）");
                    return false;
                }

                if (!form.Playlist.Items[0].HasError ||
                    form.Playlist.Items[0].ErrorMessage != PlaybackCoordinator.MissingFileMessage)
                {
                    Log(25, "网络项探测失败后没有标成「" + PlaybackCoordinator.MissingFileMessage + "」（现在是「"
                            + form.Playlist.Items[0].ErrorMessage + "」）");
                    return false;
                }

                // ---- 4) 迟到的结果不能覆盖用户后来点的曲目 ----
                var lateGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.Playback.PathProbe = _ => lateGate.Task;

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { late, local });

                form.Playback.PlayIndex(0);
                form.Playback.PlayIndex(1);

                if (form.Playback.HasPendingProbe)
                {
                    Log(25, "换了目标之后，旧的网络探测还挂着（迟到的结果迟早会回来抢播放）");
                    return false;
                }

                if (!PumpUntil(
                        () => string.Equals(form.Engine.CurrentPath, local, StringComparison.OrdinalIgnoreCase),
                        15000))
                {
                    Log(25, "改点本地项之后没有播起来（迟到的探测那段没法验）");
                    return false;
                }

                lateGate.SetResult(true);
                form.Playback.PumpPendingProbe();
                PumpMessages(400);

                if (!string.Equals(form.Engine.CurrentPath, local, StringComparison.OrdinalIgnoreCase))
                {
                    Log(25, "迟到的网络探测结果把当前曲目换掉了（现在是「"
                            + form.Engine.CurrentPath + "」）");
                    return false;
                }

                // ---- 5) 探测超时：只标超时、保留条目，不判"不存在"、也不打开 ----
                var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.Playback.PathProbe = _ => never.Task;
                form.Playback.ProbeTimeout = TimeSpan.FromMilliseconds(150);

                form.Playlist.Clear();
                form.AddPathsForTest(new[] { late });

                form.Playback.PlayIndex(0);
                Thread.Sleep(220);
                form.Playback.PumpPendingProbe();

                if (form.Playback.HasPendingProbe)
                {
                    Log(25, "探测超时之后还挂着（没有放弃等待）");
                    return false;
                }

                if (form.Playlist.Count != 1)
                {
                    Log(25, $"探测超时后条目被删掉了（现在 {form.Playlist.Count} 项）");
                    return false;
                }

                if (form.Playlist.Items[0].ErrorMessage != PlaybackCoordinator.NetworkTimeoutMessage)
                {
                    Log(25, "探测超时没有单独标记（现在「" + form.Playlist.Items[0].ErrorMessage + "」）");
                    return false;
                }

                if (form.Engine.HasMedia)
                {
                    Log(25, "探测超时后仍然尝试打开了（应该先跳过、下次再试）");
                    return false;
                }

                if (!form.StatusText.Contains("超时", StringComparison.Ordinal))
                {
                    Log(25, "探测超时后状态栏没说超时（现在「" + form.StatusText + "」）");
                    return false;
                }

                Log(25, "网络位置正常：后台探测不卡界面；离线保留并标缺、顺延；迟到结果不覆盖；超时先跳过");
                return true;
            }
            catch (Exception ex)
            {
                Log(25, "网络位置检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(local);
            }
        }
    }
}
