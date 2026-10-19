// 检查点 13 的附加探针：去隔行（M3 / R8）。只在 播放器_TEST_INTERLACED=1 时跑。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 去隔行实测（M3 / R8）
        //
        // 第 13 步原来只验"9 个模式都设得进去"，实际画面效果没有验证过（C11 那条）。
        // 这个探针用 ffmpeg 现场合成一段隔行素材，量两件事：
        //   ① 与「关闭」那一帧的差异（整幅画面，逐像素平均）；
        //   ② 梳状度量——画面中央相邻两行亮度差的平均值。隔行没处理时，运动边缘会
        //      一行亮一行暗，这个数字明显偏大；真正的去隔行会把它压下去。
        //
        // 每个模式都**重新打开一次媒体**再量：libvlc 的去隔行是设在播放器上的，
        // 运行中直接改不一定重建 vout（第一版探针就是在这里量出"八个模式一模一样"，
        // 追下去发现是滤镜链没重建）。所以这里的口径是"先设模式、再载入"，
        // 和程序启动时读设置的路径一致。
        //
        // 它**只量、不当断言**：数字写进 README 与 C11，判据不在冒烟里（合成素材
        // 和真实片源不是一回事，硬把它钉成 20 步之一会变成一个"看心情"的红）。
        // -----------------------------------------------------------------

        /// <summary>固定的取样时刻（毫秒）。选 3 秒：短片 10 秒，前后都留够余量。</summary>
        private const long InterlacedProbeAt = 3000;

        /// <summary>
        /// 设了 <c>播放器_TEST_INTERLACED</c> 才跑；没设就打印原因后跳过
        /// （照 <c>播放器_TEST_VIDEO</c> 的规矩：跳过必须出声）。
        /// </summary>
        private static void RunInterlacedProbeIfRequested()
        {
            var setting = Environment.GetEnvironmentVariable("播放器_TEST_INTERLACED");

            if (string.IsNullOrWhiteSpace(setting))
            {
                Log(13, "去隔行探针：没设 播放器_TEST_INTERLACED，跳过"
                        + "（M3 的实测结论见 README 的「去隔行」一节）");
                return;
            }

            try
            {
                if (!TryCreateInterlacedClip(out var clip, out var reason))
                {
                    Log(13, "去隔行探针跳过：" + reason);
                    return;
                }

                using var form = new Form
                {
                    ClientSize = new Size(720, 440),
                    ShowInTaskbar = false,
                    Text = "冒烟测试 - 去隔行探针"
                };

                using var view = new LibVLCSharp.WinForms.VideoView { Dock = DockStyle.Fill };
                form.Controls.Add(view);
                form.Show();

                using var engine = new PlayerEngine(form);
                view.MediaPlayer = engine.Player;

                var shots = new List<(string Label, string Path, double Comb, bool Live)>();

                foreach (var (label, mode) in DeinterlaceModes.All)
                {
                    if (!engine.SetDeinterlace(mode))
                    {
                        Log(13, $"去隔行探针：{label} 设不进去");
                        continue;
                    }

                    // 先设模式、再载入：让 vout 按新的滤镜链重建（启动时读设置就是这条路）
                    if (!PlayVideo(engine, clip, out var playError))
                    {
                        Log(13, $"去隔行探针：{label} 下短片没播起来（{playError}）");
                        continue;
                    }

                    // 不停：暂停之后 seek 不会重画（vout 只在播放时刷新），截出来的是暂停前那一帧，
                    // 九个模式截到的时刻就不可比了。播放中 seek + 900ms 落地再截，差一帧以内。
                    var at = CaptureFrame(engine, InterlacedProbeAt, "a-" + TagOf(mode));

                    if (at == null)
                    {
                        Log(13, $"去隔行探针：{label} 截不到图");
                        continue;
                    }

                    // 「换没换帧」的对照：同一个模式下再取晚 400ms 的一帧，两帧必须不一样，
                    // 否则说明截图给的是缓存帧，上面那个数字不可信。
                    var later = CaptureFrame(engine, InterlacedProbeAt + 400, "b-" + TagOf(mode));
                    var live = later != null && RegionDifference(at, later) > 0.05;

                    TryDelete(later);

                    shots.Add((label, at, CombMetric(at), live));
                }

                if (shots.Count < 2)
                {
                    Log(13, "去隔行探针：能用的截图少于 2 张，量不出对比");
                    return;
                }

                var baseline = shots[0];   // DeinterlaceModes.All 的第一项就是「关闭」

                Log(13, $"去隔行探针（播放中定位到 {InterlacedProbeAt} ms，素材 {Path.GetFileName(clip)}）："
                        + $"基准「{baseline.Label}」梳状 {baseline.Comb:0.00}");

                foreach (var shot in shots.Skip(1))
                {
                    var difference = RegionDifference(baseline.Path, shot.Path);
                    var change = baseline.Comb > 0 ? (shot.Comb - baseline.Comb) / baseline.Comb * 100 : 0;

                    Log(13, $"去隔行探针：{shot.Label}  梳状 {shot.Comb:0.00}（{change:+0.0;-0.0;0}%）"
                            + $"  与「关闭」差 {difference:0.00}"
                            + (shot.Live ? string.Empty : "  ⚠ 截图没有换帧，这一行不可信"));
                }

                var best = shots.Skip(1).Where(shot => shot.Live).OrderBy(shot => shot.Comb).FirstOrDefault();

                Log(13, best.Label == null
                    ? "去隔行探针结论：能用的数字里没有「换过帧」的，量不出结论"
                    : $"去隔行探针结论：梳状最低的是「{best.Label}」"
                      + $"（{best.Comb:0.00}，关闭 {baseline.Comb:0.00}）；探针只量、不当断言");

                foreach (var shot in shots) TryDelete(shot.Path);

                form.Close();
                PumpMessages(200);
            }
            catch (Exception ex)
            {
                Log(13, "去隔行探针失败: " + ex.Message);
            }
        }

        /// <summary>把某个时刻截成一张 png，返回路径；截不到返回 <c>null</c>。</summary>
        private static string? CaptureFrame(PlayerEngine engine, long milliseconds, string tag)
        {
            engine.SeekTo(milliseconds);
            PumpMessages(900);

            var file = Path.Combine(AppContext.BaseDirectory, "interlaced-" + tag + ".png");

            if (!engine.TakeSnapshot(file) || !File.Exists(file))
            {
                TryDelete(file);
                return null;
            }

            return file;
        }

        /// <summary>模式值转成文件名里能用的短标签（空值 = 关闭）。</summary>
        private static string TagOf(string mode) => string.IsNullOrEmpty(mode) ? "off" : mode;

        /// <summary>
        /// 整幅画面的平均亮度差（0~255），逐像素、按中央窗口取。
        /// <para>
        /// 不用现成的 <c>MeanPixelDifference</c>：它每 4 行才采一行，而隔行的梳状条纹正好
        /// 是"隔行"的——每 4 行采样会系统性地漏掉同一批行，量出来是 0.00（探针第一版踩过）。
        /// </para>
        /// </summary>
        private static double RegionDifference(string pathA, string pathB)
        {
            using var a = new Bitmap(pathA);
            using var b = new Bitmap(pathB);

            if (a.Width != b.Width || a.Height != b.Height) return 255;

            var x0 = a.Width / 6;
            var x1 = a.Width * 5 / 6;
            var y0 = a.Height / 6;
            var y1 = a.Height * 5 / 6;

            double total = 0;
            long count = 0;

            for (var y = y0; y < y1; y += 2)
            {
                for (var x = x0; x < x1; x += 2)
                {
                    var ca = a.GetPixel(x, y);
                    var cb = b.GetPixel(x, y);

                    total += Math.Abs(Luma(ca) - Luma(cb));
                    count++;
                }
            }

            return count == 0 ? 0 : total / count;
        }

        /// <summary>
        /// 梳状度量：画面中央一块里，相邻两行亮度差的平均值。
        /// <para>
        /// 采样窗口固定为 x∈[宽/6, 宽·5/6]、y∈[高/6, 高·5/6]，每 2 列取一次、逐行比较；
        /// 对 640×360 是 426×180 ≈ 7.7 万次比较。定义写死是为了别人能复算同一个数字。
        /// </para>
        /// </summary>
        private static double CombMetric(string file)
        {
            using var bitmap = new Bitmap(file);

            var x0 = bitmap.Width / 6;
            var x1 = bitmap.Width * 5 / 6;
            var y0 = bitmap.Height / 6;
            var y1 = bitmap.Height * 5 / 6;

            double total = 0;
            long count = 0;

            for (var y = y0; y < y1 - 1; y++)
            {
                for (var x = x0; x < x1; x += 2)
                {
                    total += Math.Abs(Luma(bitmap.GetPixel(x, y)) - Luma(bitmap.GetPixel(x, y + 1)));
                    count++;
                }
            }

            return count == 0 ? 0 : total / count;
        }

        private static double Luma(Color colour) => 0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B;

        /// <summary>
        /// 用 ffmpeg 合成一段隔行短片：<c>testsrc2</c> 有运动图案，<c>tinterlace=interleave_top</c>
        /// 把相邻两帧的上下场交织回去，运动边缘就会出现梳状条纹。
        /// 路径约定与四象限素材一致（<c>播放器_TEST_FFMPEG</c>，没设就用本机那把）。
        /// </summary>
        private static bool TryCreateInterlacedClip(out string path, out string reason)
        {
            path = Path.Combine(AppContext.BaseDirectory, "interlaced-sample.mp4");
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

            var arguments = new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi",
                "-i", "testsrc2=size=640x360:rate=25:duration=10",
                "-vf", "tinterlace=interleave_top",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-flags", "+ilme+ildct",
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

                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* 杀不掉就算了 */ }

                    reason = "ffmpeg 60 秒没跑完";
                    return false;
                }

                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    reason = $"ffmpeg 合成隔行短片失败（退出码 {process.ExitCode}）";
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
    }
}
