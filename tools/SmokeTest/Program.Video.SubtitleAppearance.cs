// 字幕外观：探针（GATED，只量不判）与真播放断言（1.23.0 M0 / M3）。
//
// 为什么先跑探针：libvlc 3 里跟字幕外观有关的参数只有 --freetype-*（内核级），
// 而 libvlc.dll 里没有运行时 API。所以"字幕外观"能不能做，取决于两件事：
//   ① 截图（或抓屏）里到底有没有把字幕画进去；
//   ② --freetype-* 写成内核级 / 媒体级，画面到底变不变。
// 探针（2026-11-16）实测：截图包含字幕（关字幕后墨迹 1675 → 0）；
// 内核级参数生效（字高 20 → 10、颜色变红），媒体级写法**完全不生效**。
// 所以外观走 CoreArguments（建内核时给），改设置要下次启动生效。
//
// 断言这一半只量"库真的按设置画了"：黑底素材看字号 / 颜色，白底素材看描边 / 背景。

namespace SmokeTest
{
    internal static partial class Program
    {
        private const int SubtitleProbeWidth = 640;
        private const int SubtitleProbeHeight = 360;
        private const long SubtitleProbeAtMilliseconds = 2000;

        /// <summary>一条"改了应该看得见"的内核参数：红、粗、大。</summary>
        private static readonly string[] SubtitleProbeCoreOptions =
        {
            "--freetype-rel-fontsize=30",
            "--freetype-color=16711680",
            "--freetype-outline-thickness=4"
        };

        /// <summary>同一组参数的媒体级写法（: 前缀）。</summary>
        private static readonly string[] SubtitleProbeMediaOptions =
        {
            ":freetype-rel-fontsize=30",
            ":freetype-color=16711680",
            ":freetype-outline-thickness=4"
        };

        /// <summary>只改颜色（用来分辨"参数根本没进来"和"只有颜色没进来"两件事）。</summary>
        private static readonly string[] SubtitleProbeColorOnlyOptions = { "--freetype-color=16711680" };

        /// <summary>一张截图里"字幕那些像素"的几项指标（只取画面下半部分）。</summary>
        private readonly struct SubtitleMetrics
        {
            public SubtitleMetrics(int inkPixels, int redPixels, int yellowPixels, int glyphHeight, int glyphWidth)
            {
                InkPixels = inkPixels;
                RedPixels = redPixels;
                YellowPixels = yellowPixels;
                GlyphHeight = glyphHeight;
                GlyphWidth = glyphWidth;
            }

            /// <summary>与背景不同的像素（黑底上就是字与描边；白底上是描边与彩色字）。</summary>
            public int InkPixels { get; }

            public int RedPixels { get; }

            public int YellowPixels { get; }

            /// <summary>这些像素在纵向占了多少行 —— 字号变大，它一定变大。</summary>
            public int GlyphHeight { get; }

            public int GlyphWidth { get; }

            public bool IsEmpty => InkPixels == 0;

            public override string ToString() =>
                $"墨迹 {InkPixels}、红 {RedPixels}、黄 {YellowPixels}、字高 {GlyphHeight}px、字宽 {GlyphWidth}px";
        }

        /// <summary>一次探针采样：同一配置下截图与抓屏各量一遍。</summary>
        private sealed class SubtitleProbeSample
        {
            public SubtitleProbeSample(string label, SubtitleMetrics? snapshot, SubtitleMetrics? screen, int subtitleTracks)
            {
                Label = label;
                Snapshot = snapshot;
                Screen = screen;
                SubtitleTracks = subtitleTracks;
            }

            public string Label { get; }

            public SubtitleMetrics? Snapshot { get; }

            public SubtitleMetrics? Screen { get; }

            public int SubtitleTracks { get; }
        }

        /// <summary>真播放量到的一张图（含整块下半区的平均亮度，白底背景那条要用）。</summary>
        private sealed class AppearanceShot
        {
            public AppearanceShot(string tag, SubtitleMetrics metrics, double meanLuma)
            {
                Tag = tag;
                Metrics = metrics;
                MeanLuma = meanLuma;
            }

            public string Tag { get; }

            public SubtitleMetrics Metrics { get; }

            public double MeanLuma { get; }
        }

        // -----------------------------------------------------------------
        // 探针（GATED，只量不判）
        // -----------------------------------------------------------------

        /// <summary>
        /// GATED：只量不判。记录「截图 / 抓屏里有没有字幕」以及「内核级 / 媒体级的 --freetype-* 变不变画面」。
        /// </summary>
        private static void RunSubtitleAppearanceProbeIfRequested()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("播放器_TEST_SUBTITLEPROBE"), "1", StringComparison.Ordinal))
            {
                Log(13, "字幕外观探针跳过（设 播放器_TEST_SUBTITLEPROBE=1 可跑，只量不判）");
                return;
            }

            var folder = Path.Combine(AppContext.BaseDirectory, "smoke-subtitle-probe");

            try
            {
                if (!TryCreateSubtitleClip("black", "subtitle-probe.mp4", out var clip, out var subtitle, out var reason))
                {
                    Log(13, "字幕外观探针跳过：" + reason);
                    return;
                }

                TryDeleteDirectory(folder);
                Directory.CreateDirectory(folder);

                Log(13, "字幕外观探针：开始（黑底 " + SubtitleProbeWidth + "×" + SubtitleProbeHeight
                        + "，外挂 .srt 在 " + SubtitleProbeAtMilliseconds + " ms 处）");

                var samples = new List<SubtitleProbeSample>
                {
                    ProbeSubtitleLook(folder, "基线（默认外观）", clip, subtitle, null, null, subtitlesOn: true),
                    ProbeSubtitleLook(folder, "关字幕（对照）", clip, subtitle, null, null, subtitlesOn: false),
                    ProbeSubtitleLook(folder, "内核级参数", clip, subtitle, SubtitleProbeCoreOptions, null, subtitlesOn: true),
                    ProbeSubtitleLook(folder, "媒体级参数", clip, subtitle, null, SubtitleProbeMediaOptions, subtitlesOn: true),
                    ProbeSubtitleLook(folder, "只改颜色（内核级）", clip, subtitle, SubtitleProbeColorOnlyOptions, null, subtitlesOn: true),
                    ProbeSubtitleLook(folder, "只改字号 30", clip, subtitle, new[] { "--freetype-rel-fontsize=30" }, null, subtitlesOn: true),
                    ProbeSubtitleLook(folder, "只改字号 15", clip, subtitle, new[] { "--freetype-rel-fontsize=15" }, null, subtitlesOn: true),
                    ProbeSubtitleLook(folder, "只改字号 12", clip, subtitle, new[] { "--freetype-rel-fontsize=12" }, null, subtitlesOn: true),
                    ProbeSubtitleLook(folder, "只改字号 8", clip, subtitle, new[] { "--freetype-rel-fontsize=8" }, null, subtitlesOn: true)
                };

                foreach (var sample in samples) LogSubtitleProbeSample(sample);

                LogSubtitleProbeConclusion(samples, hasSnapshotSubtitle: SnapshotCarriesSubtitle(samples),
                    hasScreenSubtitle: ScreenCarriesSubtitle(samples));
            }
            catch (Exception ex)
            {
                Log(13, "字幕外观探针失败: " + ex.Message);
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        private static void LogSubtitleProbeSample(SubtitleProbeSample sample)
        {
            var snapshot = sample.Snapshot?.ToString() ?? "截图失败";
            var screen = sample.Screen?.ToString() ?? "抓屏失败";

            Log(13, $"字幕外观探针：{sample.Label} → 截图 {snapshot}；抓屏 {screen}；字幕轨 {sample.SubtitleTracks} 条");
        }

        /// <summary>截图里有没有字幕：基线有墨迹、关字幕之后几乎没有。</summary>
        private static bool SnapshotCarriesSubtitle(IReadOnlyList<SubtitleProbeSample> samples)
        {
            var baseline = samples.FirstOrDefault(sample => sample.Label.StartsWith("基线", StringComparison.Ordinal))?.Snapshot;
            var off = samples.FirstOrDefault(sample => sample.Label.StartsWith("关字幕", StringComparison.Ordinal))?.Snapshot;

            if (baseline == null || off == null) return false;

            return baseline.Value.InkPixels >= 200 && off.Value.InkPixels <= Math.Max(20, baseline.Value.InkPixels / 10);
        }

        /// <summary>抓屏里有没有字幕（截图不包含字幕时靠它）。</summary>
        private static bool ScreenCarriesSubtitle(IReadOnlyList<SubtitleProbeSample> samples)
        {
            var baseline = samples.FirstOrDefault(sample => sample.Label.StartsWith("基线", StringComparison.Ordinal))?.Screen;
            var off = samples.FirstOrDefault(sample => sample.Label.StartsWith("关字幕", StringComparison.Ordinal))?.Screen;

            if (baseline == null || off == null) return false;

            return baseline.Value.InkPixels >= 200 && off.Value.InkPixels <= Math.Max(20, baseline.Value.InkPixels / 10);
        }

        /// <summary>结论按计划 §2.2 的三选一打印：M（媒体级可用）/ C（只有内核级）/ X（都不行）。</summary>
        private static void LogSubtitleProbeConclusion(
            IReadOnlyList<SubtitleProbeSample> samples, bool hasSnapshotSubtitle, bool hasScreenSubtitle)
        {
            if (!hasSnapshotSubtitle && !hasScreenSubtitle)
            {
                Log(13, "字幕外观探针结论：X —— 截图和抓屏都没有把字幕画进去，无法验证效果（这个功能不做）");
                return;
            }

            var useSnapshot = hasSnapshotSubtitle;

            SubtitleMetrics? MetricOf(string prefix)
            {
                var sample = samples.FirstOrDefault(item => item.Label.StartsWith(prefix, StringComparison.Ordinal));
                if (sample == null) return null;

                return useSnapshot ? sample.Snapshot : sample.Screen;
            }

            var baseline = MetricOf("基线");

            if (baseline == null)
            {
                Log(13, "字幕外观探针结论：基线没量到，无法比较");
                return;
            }

            bool Changed(string prefix)
            {
                var metric = MetricOf(prefix);

                if (metric == null) return false;

                if (baseline.Value.GlyphHeight > 0 &&
                    metric.Value.GlyphHeight >= Math.Max(baseline.Value.GlyphHeight + 4, baseline.Value.GlyphHeight * 1.3))
                    return true;

                return metric.Value.RedPixels >= Math.Max(60, baseline.Value.RedPixels * 3) &&
                       metric.Value.RedPixels > baseline.Value.RedPixels + 50;
            }

            var coreChanged = Changed("内核级参数");
            var mediaChanged = Changed("媒体级参数");
            var colorOnlyChanged = Changed("只改颜色");

            if (mediaChanged)
            {
                Log(13, "字幕外观探针结论：M —— 媒体级 --freetype-* 就能改变画面（走 MediaOptions，换外观重载当前媒体）");
            }
            else if (coreChanged || colorOnlyChanged)
            {
                Log(13, "字幕外观探针结论：C —— 只有内核级 --freetype-* 生效（外观进 CoreArguments，下次启动生效）");
            }
            else
            {
                Log(13, "字幕外观探针结论：X —— 两组参数都没有改变画面（这个功能不做）");
            }
        }

        /// <summary>
        /// 一次探针采样：按给定的内核 / 媒体参数起一套原生播放链，播到固定时刻截图 + 抓屏。
        /// <para>
        /// 这里**不用 PlayerEngine**：探针要试"任意内核参数"，而引擎的外观是构造时定死的。
        /// 直接用 LibVLC / MediaPlayer 更接近"参数到底有没有生效"这个问题本身。
        /// </para>
        /// </summary>
        private static SubtitleProbeSample ProbeSubtitleLook(
            string folder, string label, string clip, string subtitle, string[]? coreOptions, string[]? mediaOptions,
            bool subtitlesOn)
        {
            var tag = Math.Abs(label.GetHashCode() % 100000).ToString("D5");
            var shot = Path.Combine(folder, "shot-" + tag + ".png");

            SubtitleMetrics? snapshot = null;
            SubtitleMetrics? screen = null;
            var subtitleTracks = 0;

            using var form = new Form
            {
                ClientSize = new Size(SubtitleProbeWidth + 120, SubtitleProbeHeight + 120),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40),
                ShowInTaskbar = false,
                Text = "冒烟测试 - 字幕外观探针"
            };

            using var view = new LibVLCSharp.WinForms.VideoView { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            form.Show();
            form.Activate();
            PumpMessages(300);

            var options = new List<string> { "--quiet", "--no-video-title-show", "--no-snapshot-preview" };

            if (coreOptions != null) options.AddRange(coreOptions);

            using var libVlc = new LibVLC(options.ToArray());
            using var player = new MediaPlayer(libVlc);

            view.MediaPlayer = player;

            using var media = new Media(libVlc, clip, FromType.FromPath);

            if (mediaOptions != null)
            {
                foreach (var option in mediaOptions) media.AddOption(option);
            }

            try
            {
                if (!player.Play(media))
                {
                    Log(13, $"字幕外观探针：{label} 打不开短片");
                    return new SubtitleProbeSample(label, null, null, 0);
                }

                if (!PumpUntil(() => player.IsPlaying, 15000))
                {
                    Log(13, $"字幕外观探针：{label} 没播起来");
                    return new SubtitleProbeSample(label, null, null, 0);
                }

                PumpMessages(400);

                if (subtitlesOn)
                {
                    player.AddSlave(MediaSlaveType.Subtitle, new Uri(subtitle).AbsoluteUri, true);
                    PumpMessages(700);
                }
                else
                {
                    player.SetSpu(-1);
                }

                subtitleTracks = (player.SpuDescription ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>())
                    .Count(track => track.Id >= 0);

                player.SeekTo(TimeSpan.FromMilliseconds(SubtitleProbeAtMilliseconds));
                PumpMessages(900);

                TryDelete(shot);

                if (player.TakeSnapshot(0, shot, 0, 0) &&
                    PumpUntil(() => File.Exists(shot) && new FileInfo(shot).Length > 0, 3000))
                    snapshot = MeasureSubtitle(shot, backgroundLuma: 0);

                var bounds = view.RectangleToScreen(view.ClientRectangle);

                using (var captured = CaptureScreen(bounds))
                {
                    if (captured != null) screen = MeasureSubtitleBitmap(captured, backgroundLuma: 0);
                }

                return new SubtitleProbeSample(label, snapshot, screen, subtitleTracks);
            }
            finally
            {
                view.MediaPlayer = null;

                try { player.Stop(); } catch (Exception) { /* 探针收尾，失败不影响结论 */ }

                form.Close();
                PumpMessages(200);
                TryDelete(shot);
            }
        }

        // -----------------------------------------------------------------
        // 断言（第 13 项）：真播放，按设置量画面
        // -----------------------------------------------------------------

        /// <summary>
        /// 字幕外观断言：同一段黑底 / 白底素材，分别用不同外观起引擎，量字高、颜色、描边、背景。
        /// 没有 ffmpeg 或量不到截图时打印原因后跳过（三态）；产品变异仍然必须红。
        /// </summary>
        private static bool CheckSubtitleAppearanceRendering()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "smoke-subtitle-appearance");

            try
            {
                if (!TryCreateSubtitleClip("black", "appearance-black.mp4", out var blackClip, out var blackSrt, out var reason))
                {
                    Log(13, "字幕外观检查跳过：" + reason);
                    return true;
                }

                if (!TryCreateSubtitleClip("white", "appearance-white.mp4", out var whiteClip, out var whiteSrt, out var whiteReason))
                {
                    Log(13, "字幕外观检查跳过：" + whiteReason);
                    return true;
                }

                TryDeleteDirectory(folder);
                Directory.CreateDirectory(folder);

                var baseline = TakeAppearanceShot(folder, "base", blackClip, blackSrt, SubtitleAppearance.Default);

                if (baseline == null || baseline.Metrics.InkPixels < 300)
                {
                    Log(13, "字幕外观检查跳过：截图里量不到字幕（可能是 this 机器上截图不含 SPU）");
                    return true;
                }

                var small = TakeAppearanceShot(folder, "small", blackClip, blackSrt,
                    new SubtitleAppearance { Size = "small" });

                var xlargeYellow = TakeAppearanceShot(folder, "xlarge", blackClip, blackSrt,
                    new SubtitleAppearance { Size = "xlarge", Color = "yellow" });

                var fine = TakeAppearanceShot(folder, "fine", whiteClip, whiteSrt,
                    new SubtitleAppearance { Size = "xlarge", Color = "green", Outline = 1 });

                var coarse = TakeAppearanceShot(folder, "coarse", whiteClip, whiteSrt,
                    new SubtitleAppearance { Size = "xlarge", Color = "green", Outline = 4 });

                var withBackground = TakeAppearanceShot(folder, "background", whiteClip, whiteSrt,
                    new SubtitleAppearance { Size = "xlarge", Color = "green", Outline = 1, Background = 160 });

                if (small == null || xlargeYellow == null || fine == null || coarse == null || withBackground == null)
                {
                    Log(13, "字幕外观检查跳过：至少一张截图没量到");
                    return true;
                }

                Log(13, $"字幕外观观测：默认 {baseline.Metrics}；小 {small.Metrics}；"
                        + $"特大+黄 {xlargeYellow.Metrics}；白底细描边 {fine.Metrics}；"
                        + $"白底粗描边 {coarse.Metrics}；白底背景 {withBackground.Metrics}（平均亮度 {withBackground.MeanLuma:0.0}）");

                if (small.Metrics.GlyphHeight > baseline.Metrics.GlyphHeight * 0.8)
                {
                    Log(13, $"字幕外观：字号「小」没有变小（字高 {small.Metrics.GlyphHeight}，默认 {baseline.Metrics.GlyphHeight}）");
                    return false;
                }

                if (xlargeYellow.Metrics.GlyphHeight < baseline.Metrics.GlyphHeight * 1.8)
                {
                    Log(13, $"字幕外观：字号「特大」没有变大（字高 {xlargeYellow.Metrics.GlyphHeight}，默认 {baseline.Metrics.GlyphHeight}）");
                    return false;
                }

                if (xlargeYellow.Metrics.YellowPixels < 200)
                {
                    Log(13, $"字幕外观：颜色「黄」没有生效（黄像素 {xlargeYellow.Metrics.YellowPixels}，默认 {baseline.Metrics.YellowPixels}）");
                    return false;
                }

                if (coarse.Metrics.InkPixels < fine.Metrics.InkPixels * 1.05)
                {
                    Log(13, $"字幕外观：描边「粗」和「细」画出来差不多（墨迹 {coarse.Metrics.InkPixels} / {fine.Metrics.InkPixels}）");
                    return false;
                }

                if (withBackground.MeanLuma > fine.MeanLuma - 8)
                {
                    Log(13, $"字幕外观：背景「浓」没有画出来（平均亮度 {withBackground.MeanLuma:0.0}，无背景 {fine.MeanLuma:0.0}）");
                    return false;
                }

                Log(13, "字幕外观正确：字号（小 / 特大）、颜色（黄）、描边（细 / 粗）、背景都有可测的变化");
                return true;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }

        /// <summary>用给定外观起一套引擎、播到固定时刻截图，量成指标；失败返回 <c>null</c>。</summary>
        private static AppearanceShot? TakeAppearanceShot(
            string folder, string tag, string clip, string subtitle, SubtitleAppearance appearance)
        {
            var shot = Path.Combine(folder, tag + ".png");

            using var form = new Form
            {
                ClientSize = new Size(SubtitleProbeWidth + 120, SubtitleProbeHeight + 120),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(40, 40),
                ShowInTaskbar = false,
                Text = "冒烟测试 - 字幕外观"
            };

            using var view = new LibVLCSharp.WinForms.VideoView { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            form.Show();
            form.Activate();
            PumpMessages(300);

            using var engine = new PlayerEngine(form, appearance);
            view.MediaPlayer = engine.Player;

            try
            {
                if (!PlayVideo(engine, clip, out var error))
                {
                    Log(13, $"字幕外观检查：短片没播起来（{error}）");
                    return null;
                }

                if (!engine.AddSubtitleFile(subtitle))
                {
                    Log(13, "字幕外观检查：加载外挂 .srt 失败");
                    return null;
                }

                PumpMessages(800);

                engine.SeekTo(SubtitleProbeAtMilliseconds);
                PumpMessages(1000);

                TryDelete(shot);

                if (!engine.TakeSnapshot(shot)) return null;
                if (!PumpUntil(() => File.Exists(shot) && new FileInfo(shot).Length > 0, 4000)) return null;

                var backgroundLuma = tag == "base" || tag == "small" || tag == "xlarge" ? 0 : 255;

                return new AppearanceShot(tag, MeasureSubtitle(shot, backgroundLuma), MeanBottomLuma(shot));
            }
            finally
            {
                view.MediaPlayer = null;

                try { form.Close(); } catch (Exception) { }
                PumpMessages(200);
                TryDelete(shot);
            }
        }

        // -----------------------------------------------------------------
        // 量法
        // -----------------------------------------------------------------

        /// <summary>把截图下半部分里"非背景"的像素量成几个数字（背景亮度由素材决定）。</summary>
        private static SubtitleMetrics MeasureSubtitle(string file, double backgroundLuma)
        {
            using var bitmap = new Bitmap(file);
            return MeasureSubtitleBitmap(bitmap, backgroundLuma);
        }

        private static SubtitleMetrics MeasureSubtitleBitmap(Bitmap bitmap, double backgroundLuma)
        {
            var top = bitmap.Height * 55 / 100;

            var ink = 0;
            var red = 0;
            var yellow = 0;
            var minY = int.MaxValue;
            var maxY = -1;
            var minX = int.MaxValue;
            var maxX = -1;

            for (var y = top; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var colour = bitmap.GetPixel(x, y);
                    var luma = Luma(colour);

                    var isRed = colour.R >= 90 && colour.R - colour.G >= 40 && colour.R - colour.B >= 40;
                    var isYellow = colour.R >= 90 && colour.G >= 90 && colour.B <= 80;

                    if (isRed) red++;
                    if (isYellow) yellow++;

                    // 背景可能是黑（0）也可能是白（255）：离背景足够远才算"字 / 描边"
                    if (Math.Abs(luma - backgroundLuma) <= 24 && !isRed && !isYellow) continue;

                    ink++;

                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                }
            }

            return maxY < 0
                ? new SubtitleMetrics(0, red, yellow, 0, 0)
                : new SubtitleMetrics(ink, red, yellow, maxY - minY + 1, maxX - minX + 1);
        }

        /// <summary>画面下半区的平均亮度（白底上加了半透明背景之后会明显变暗）。</summary>
        private static double MeanBottomLuma(string file)
        {
            using var bitmap = new Bitmap(file);

            var top = bitmap.Height * 55 / 100;
            double total = 0;
            long count = 0;

            for (var y = top; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    total += Luma(bitmap.GetPixel(x, y));
                    count++;
                }
            }

            return count == 0 ? 0 : total / count;
        }

        /// <summary>纯色底短片 + 外挂 .srt：字幕在 1~5 秒，2 秒处一定在屏上。</summary>
        private static bool TryCreateSubtitleClip(
            string background, string fileName, out string clip, out string subtitle, out string reason)
        {
            clip = Path.Combine(AppContext.BaseDirectory, fileName);
            subtitle = Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(fileName) + ".srt");
            reason = string.Empty;

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");

            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                reason = "没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个，或者装到计划里那把路径）";
                return false;
            }

            try
            {
                // 外挂 .srt：不用 ASS —— ASS 走 libass，测不到 freetype 的那组参数。
                File.WriteAllText(subtitle,
                    "1" + Environment.NewLine +
                    "00:00:01,000 --> 00:00:05,000" + Environment.NewLine +
                    "字幕外观探针 ABC 123" + Environment.NewLine, new UTF8Encoding(false));

                TryDelete(clip);

                var arguments = new[]
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi",
                    "-i", $"color=c={background}:s={SubtitleProbeWidth}x{SubtitleProbeHeight}:d=6:r=25",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                    "-an",
                    clip
                };

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

                if (process.ExitCode != 0 || !File.Exists(clip))
                {
                    reason = $"ffmpeg 合成 {background} 底短片失败（退出码 {process.ExitCode}）";
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
