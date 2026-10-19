// 检查点 13：视频功能与逐帧。需要 播放器_TEST_VIDEO 指定真实视频，否则跳过。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 13) 视频功能（需要真实视频，通过环境变量指定，否则跳过）
        //
        // 画面调整、去隔行、延迟、逐帧这些都必须落到真正的视频输出上才有意义——
        // 用 WAV 或者假数据是验不出来的，所以这一步要求外部给一个视频文件。
        // -----------------------------------------------------------------

        private static bool TestVideoFeatures()
        {
            // 1.3.0 B-2：画面旋转 / 翻转（素材自己用 ffmpeg 合成，所以不依赖 播放器_TEST_VIDEO）
            if (!CheckVideoRotation()) return false;

            var video = Environment.GetEnvironmentVariable("播放器_TEST_VIDEO");

            if (string.IsNullOrWhiteSpace(video) || !File.Exists(video))
            {
                Log(13, "未指定 播放器_TEST_VIDEO（或文件不存在），跳过视频功能检查");
                return true;
            }

            try
            {
                using var form = new Form
                {
                    ClientSize = new Size(720, 440),
                    ShowInTaskbar = false,
                    Text = "冒烟测试 - 视频功能"
                };

                using var view = new LibVLCSharp.WinForms.VideoView { Dock = DockStyle.Fill };
                form.Controls.Add(view);
                form.Show();

                using var engine = new PlayerEngine(form);
                view.MediaPlayer = engine.Player;

                if (!PlayVideo(engine, video, out var error))
                {
                    Log(13, "播放视频失败：" + error);
                    return false;
                }

                var fps = engine.FrameRate;
                if (fps <= 1)
                {
                    Log(13, $"没有读到视频帧率（{fps}），逐帧功能没法工作");
                    return false;
                }

                // ---- 音画 / 字幕延迟（界面上是毫秒，libvlc 收的是微秒）----
                if (!engine.SetAudioDelayMilliseconds(150) ||
                    Math.Abs(engine.AudioDelayMilliseconds - 150) > 30)
                {
                    Log(13, $"音频延迟往返不正确：设 150，读回 {engine.AudioDelayMilliseconds}");
                    return false;
                }

                if (!engine.SetSubtitleDelayMilliseconds(-250) ||
                    Math.Abs(engine.SubtitleDelayMilliseconds + 250) > 30)
                {
                    Log(13, $"字幕延迟往返不正确：设 −250，读回 {engine.SubtitleDelayMilliseconds}");
                    return false;
                }

                // ---- 去隔行：每个模式都要能设进去且不抛 ----
                foreach (var (label, mode) in DeinterlaceModes.All)
                {
                    if (!engine.SetDeinterlace(mode))
                    {
                        Log(13, $"设置去隔行「{label}」失败");
                        return false;
                    }

                    PumpMessages(120);
                }

                // ---- 对照实验：先确认截图抓到的是实时画面 ----
                // 如果播放中连拍两张都一模一样，那说明 TakeSnapshot 给的是缓存帧，
                // 后面用像素判断"有没有换帧"就完全不成立，必须先排除这种情况。
                var liveA = Path.Combine(AppContext.BaseDirectory, "shot-live-a.png");
                var liveB = Path.Combine(AppContext.BaseDirectory, "shot-live-b.png");

                engine.TakeSnapshot(liveA);
                PumpMessages(700);
                engine.TakeSnapshot(liveB);

                var liveDiff = MeanPixelDifference(liveA, liveB);

                // 阈值放到 0.05：静态画面下相邻帧的差异本来就很微小（实测有个视频 700ms 才 0.9），
                // 而"完全没换帧"时像素差是精确的 0.00，两者区分得很清楚。
                if (liveDiff < 0.05)
                {
                    Log(13, $"对照失败：播放中两次快照的像素差只有 {liveDiff:0.00}，"
                            + "说明截图不是实时画面，无法据此判断逐帧是否生效");
                    return false;
                }

                Log(13, $"对照：播放中两次快照的像素差 {liveDiff:0.00}（截图是实时画面）");
                // ---- 逐帧：libvlc 的 NextFrame 只在暂停状态下有效 ----
                engine.Pause();

                // 暂停是异步生效的：没停稳就调 NextFrame，会被静默丢掉。
                var pauseDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < pauseDeadline && engine.IsPlaying)
                    PumpMessages(100);

                if (engine.IsPlaying)
                {
                    Log(13, "暂停一直没生效，无法测试逐帧");
                    return false;
                }

                PumpMessages(300);

                // ⚠ 这里有两个坑，都是实测出来的，不绕开就一定会误报：
                //
                // 1) **暂停时画面上显示的并不是"时钟所指的那一帧"。** 暂停后连拍两张（间隔 700ms）
                //    像素差是 0.00，说明画面本身是稳的；但再 seek 到时钟所指的同一时刻，
                //    画面却变了（本机实测差 11 左右）——vout 比时钟滞后约 100~200ms。
                //    所以"暂停时截的那一张"不能当作"上一帧应该回到的那一帧"。
                //
                // 2) **本测试视频是白板讲解，相邻帧常常几乎一样**（实测一帧只差 0.00~0.54），
                //    一次走一帧根本分辨不出"走没走"，用 0.05 当阈值等于在量重绘噪声。
                //
                // 因此这里的做法是：先用一次"前进 + 后退"把基准落到 seek 落点上，
                // 再一次走 5 帧——前进时画面必须有明显变化，后退时**必须像素级回到基准**
                // （seek 落点是可复现的，本机实测同一个时刻来回 seek 的像素差为 0.00）。
                var reference = Path.Combine(AppContext.BaseDirectory, "shot-frame-base.png");
                var steppedShot = Path.Combine(AppContext.BaseDirectory, "shot-frame-forward.png");
                var restoredShot = Path.Combine(AppContext.BaseDirectory, "shot-frame-back.png");

                // 基准：走一个来回，让 base 那一帧真正落到画面上
                StepFrames(engine, forward: true, count: 1);
                StepFrames(engine, forward: false, count: 1);

                if (!engine.TakeSnapshot(reference))
                {
                    Log(13, "暂停时截图失败");
                    return false;
                }

                StepFrames(engine, forward: true, count: FramesPerStepRun);
                engine.TakeSnapshot(steppedShot);

                StepFrames(engine, forward: false, count: FramesPerStepRun);
                engine.TakeSnapshot(restoredShot);

                var forwardDiff = MeanPixelDifference(reference, steppedShot);
                var backDiff = MeanPixelDifference(steppedShot, restoredShot);
                var restoreDiff = MeanPixelDifference(reference, restoredShot);

                if (forwardDiff < 0.5)
                {
                    Log(13, $"下一帧没有换画面（前进 {FramesPerStepRun} 帧之后与基准的像素差只有 {forwardDiff:0.00}，"
                            + $"fps={fps:0.###}，帧长={engine.FrameDurationMilliseconds:0.#}ms）");
                    return false;
                }

                if (restoreDiff >= 0.05)
                {
                    Log(13, $"上一帧没有退回原来那一帧（后退 {FramesPerStepRun} 帧之后与基准的像素差还有 "
                            + $"{restoreDiff:0.00}；这一步画面确实动了 {backDiff:0.00}）");
                    return false;
                }

                // 连按（两次之间不留静置）不能丢定位：逐帧每次发的都是"基准 + 第几帧"这样的绝对时刻，
                // 最后那一次定位落到哪儿，画面就该在哪儿。实测 5 连按与慢速走 5 帧落在同一帧（差 0.00）。
                for (var i = 0; i < FramesPerStepRun; i++) engine.StepFrame(true);

                PumpMessages(1500);

                var rapidShot = Path.Combine(AppContext.BaseDirectory, "shot-frame-rapid.png");
                engine.TakeSnapshot(rapidShot);

                var rapidDiff = MeanPixelDifference(steppedShot, rapidShot);

                if (rapidDiff >= 0.05)
                {
                    Log(13, $"连按 {FramesPerStepRun} 次之后没有停在「前进 {FramesPerStepRun} 帧」那一帧上"
                            + $"（与慢速前进那一帧差 {rapidDiff:0.00}，与基准差 "
                            + $"{MeanPixelDifference(reference, rapidShot):0.00}）");
                    return false;
                }

                Log(13, $"视频功能正常：{fps:0.###} fps（帧长 {engine.FrameDurationMilliseconds:0.#} ms）、"
                        + $"去隔行 {DeinterlaceModes.All.Count} 个模式可设、延迟往返正确、"
                        + $"逐帧换帧正常（前进 {FramesPerStepRun} 帧差 {forwardDiff:0.0}，"
                        + $"后退 {FramesPerStepRun} 帧回到基准的差 {restoreDiff:0.00}，"
                        + $"连按 {FramesPerStepRun} 次落点差 {rapidDiff:0.00}）");

                form.Close();
                PumpMessages(200);
                return true;
            }
            catch (Exception ex)
            {
                Log(13, "视频功能检查失败: " + ex);
                return false;
            }
        }



        /// <summary>
        /// 一次"逐帧"检查里连续走几帧。
        /// <para>
        /// 不能用 1 帧：本项目用的测试视频是白板讲解，相邻两帧常常一模一样
        /// （实测像素差 0.00~0.54），走一帧根本分辨不出"走没走"，
        /// 那样的断言实际上是在量 vout 的重绘噪声。走 5 帧（约 166ms）之后
        /// 画面差异稳定在 10 上下，和噪声拉开两个数量级。
        /// </para>
        /// </summary>
        private const int FramesPerStepRun = 5;

        /// <summary>
        /// 两次逐帧之间要留的静置时间。
        /// <para>
        /// 逐帧是靠 seek 实现的（libvlc 的 <c>NextFrame()</c> 在本项目里完全不生效），
        /// 而"定位到某时刻"要解码并重新送显，实测要几百毫秒才落地。
        /// 太短的话下一次定位会落在上一次还没排空的窗口里。
        /// </para>
        /// </summary>
        private const int SettleAfterStepMs = 600;

        /// <summary>连续走若干帧，每帧之间留出落地时间。</summary>
        private static void StepFrames(PlayerEngine engine, bool forward, int count)
        {
            for (var i = 0; i < count; i++)
            {
                engine.StepFrame(forward);
                PumpMessages(SettleAfterStepMs);
            }
        }

        // -----------------------------------------------------------------
        // B-2（1.3.0）：画面旋转 / 翻转
        //
        // 判据用"四个象限的颜色按预期搬家"，不是"像素差很大"那种弱判据：
        // 旋转 180° 和水平翻转都会让整幅画面变得很不一样，弱判据分不出它们谁是谁。
        // 素材由 ffmpeg 现场合成（一次性，不入库）：四个象限红 / 绿 / 蓝 / 黄，
        // 没有 ffmpeg 就照 播放器_TEST_VIDEO 的先例打印原因后跳过。
        // -----------------------------------------------------------------

        /// <summary>四象限素材：宽高、以及画上去的四个颜色。</summary>
        private const int QuadrantWidth = 320;

        private const int QuadrantHeight = 240;










        /// <summary>截一帧并读四个象限；截不到返回 <c>null</c>。</summary>
        private static string[]? SnapshotQuadrants(PlayerEngine engine, string tag)
        {
            var shot = Path.Combine(AppContext.BaseDirectory, tag + ".png");

            try
            {
                if (!engine.TakeSnapshot(shot) || !File.Exists(shot)) return null;

                using var bitmap = new Bitmap(shot);
                return ReadQuadrants(bitmap);
            }
            finally
            {
                TryDelete(shot);
            }
        }

        /// <summary>
        /// 读四个象限的中心颜色，各自归到 红 / 绿 / 蓝 / 黄 里最像的那一个。
        /// <para>
        /// 取一小块的平均值而不是单个像素：视频经过 YUV 与压缩，单个像素可能偏得离谱。
        /// </para>
        /// </summary>
        private static string[] ReadQuadrants(Bitmap bitmap)
        {
            var names = new[] { "左上", "右上", "左下", "右下" };
            var colours = new string[4];

            for (var i = 0; i < 4; i++)
            {
                var x = (i % 2 == 0 ? bitmap.Width / 4 : bitmap.Width * 3 / 4);
                var y = (i < 2 ? bitmap.Height / 4 : bitmap.Height * 3 / 4);

                long r = 0, g = 0, b = 0;
                var count = 0;

                for (var dy = -5; dy <= 5; dy++)
                {
                    for (var dx = -5; dx <= 5; dx++)
                    {
                        var px = Math.Clamp(x + dx, 0, bitmap.Width - 1);
                        var py = Math.Clamp(y + dy, 0, bitmap.Height - 1);

                        var colour = bitmap.GetPixel(px, py);
                        r += colour.R;
                        g += colour.G;
                        b += colour.B;
                        count++;
                    }
                }

                colours[i] = ClassifyColour((int)(r / count), (int)(g / count), (int)(b / count));
            }

            return colours;
        }

        /// <summary>把平均颜色归到四象限用的那四个颜色里最像的一个（认不出来返回 <c>"?"</c>）。</summary>
        private static string ClassifyColour(int r, int g, int b)
        {
            const int Margin = 60;

            if (r > b + Margin && g > b + Margin) return "黄";
            if (r > g + Margin && r > b + Margin) return "红";
            if (g > r + Margin && g > b + Margin) return "绿";
            if (b > r + Margin && b > g + Margin) return "蓝";

            return $"?({r},{g},{b})";
        }

        /// <summary>
        /// 用 ffmpeg 现场合成一段四象限短片（左上红、右上绿、左下蓝、右下黄）。
        /// <para>
        /// 路径来自 <c>播放器_TEST_FFMPEG</c>，没设就用本机那把（见 docs/1.3.0计划.md 的假设）；
        /// 两个都没有时返回 false 并给出理由（照 <c>播放器_TEST_VIDEO</c> 的规矩：跳过要出声）。
        /// </para>
        /// <para>
        /// 刻意<b>不重定向</b>子进程的输出：冒烟测试可能跑在受限环境里，
        /// 管道那头创建不出来会直接抛异常，而这里根本不需要读它的输出（日志级别压到 error）。
        /// </para>
        /// </summary>
        private static bool TryCreateQuadrantClip(out string path, out string reason)
        {
            path = Path.Combine(AppContext.BaseDirectory, "rotate-quadrants.mp4");
            reason = string.Empty;

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");

            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                reason = "没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个，或者装到计划里那把路径）";
                return false;
            }

            TryDelete(path);

            var filter = string.Join(",", new[]
            {
                $"drawbox=x=0:y=0:w={QuadrantWidth / 2}:h={QuadrantHeight / 2}:c=red@1:t=fill",
                $"drawbox=x={QuadrantWidth / 2}:y=0:w={QuadrantWidth / 2}:h={QuadrantHeight / 2}:c=green@1:t=fill",
                $"drawbox=x=0:y={QuadrantHeight / 2}:w={QuadrantWidth / 2}:h={QuadrantHeight / 2}:c=blue@1:t=fill",
                $"drawbox=x={QuadrantWidth / 2}:y={QuadrantHeight / 2}:w={QuadrantWidth / 2}:h={QuadrantHeight / 2}:c=yellow@1:t=fill",
                "format=yuv420p"
            });

            var arguments = new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi",
                "-i", $"color=c=black:s={QuadrantWidth}x{QuadrantHeight}:d=4:r=10",
                "-vf", filter,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                path
            };

            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = string.Join(" ", arguments.Select(QuoteArgument))
                });

                if (process == null)
                {
                    reason = "ffmpeg 起不来";
                    return false;
                }

                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* 杀不掉就算了 */ }

                    reason = "ffmpeg 30 秒没跑完";
                    return false;
                }

                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    reason = $"ffmpeg 合成四象限短片失败（退出码 {process.ExitCode}）";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "调 ffmpeg 失败：" + ex.Message;
                return false;
            }
        }

        /// <summary>命令行的路径参数要带引号（Program Files 那种空格路径）。</summary>
        private static string QuoteArgument(string argument) =>
            argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;

        /// <summary>打开视频并等到真正开始播放。</summary>
        private static bool PlayVideo(PlayerEngine engine, string video, out string error)
        {
            error = string.Empty;

            var playing = new ManualResetEventSlim(false);
            var failed = new ManualResetEventSlim(false);

            // ⚠ 订阅底层 MediaPlayer 的事件，而不是 PlayerEngine 的：
            // 后者会把事件派发回 UI 线程，而等待期间主线程被占住，消息循环停了就永远收不到，
            // 表现为"一直没进入 Playing"。这里改成边等边泵消息。
            EventHandler<EventArgs> onPlaying = (s, e) => playing.Set();
            EventHandler<EventArgs> onError = (s, e) => failed.Set();

            engine.Player.Playing += onPlaying;
            engine.Player.EncounteredError += onError;

            try
            {
                engine.Open(video);

                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline && !playing.IsSet && !failed.IsSet)
                    PumpMessages(200);

                if (failed.IsSet) { error = "播放报错（格式不支持或文件损坏）"; return false; }
                if (!playing.IsSet) { error = "25 秒内没有进入 Playing 状态"; return false; }

                PumpMessages(500);
                return true;
            }
            finally
            {
                engine.Player.Playing -= onPlaying;
                engine.Player.EncounteredError -= onError;
            }
        }
    }
}
