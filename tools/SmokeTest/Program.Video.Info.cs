// 检查点 13 的「媒体信息」部分（1.22.0 B）：
// 视频 / 音频 / 字幕轨那一行要写出分辨率、显示比例、帧率、码率、语言中文名等细节。
//
// 两段：
//   ① 纯逻辑（不依赖引擎、不依赖 ffmpeg）：MediaDescriptions 的边界；
//   ② 真播放：ffmpeg 现造 640×480 / 25 fps / SAR 4:3（显示 16:9）/ H.264 + AAC + 字幕 的片段，
//      播放后读「媒体信息」页那几行。没有 ffmpeg 时打印原因后跳过（照第 13 步的规矩）。
//
// `播放器_TEST_VIDEOPROBE=1` 时把每条轨道 libvlc 报上来的原始字段与最终行都打出来（只量不判）。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckMediaInfoRows()
        {
            if (!CheckMediaDescriptions()) return false;

            if (!TryCreateInfoClip(out var clip, out var reason))
            {
                Log(13, "媒体信息检查：" + reason + "（跳过）");
                return true;
            }

            try
            {
                using var form = new 播放器.MainForm(new[] { clip });
                form.Show();

                if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.GetTracks().Count >= 3, 20000))
                {
                    Log(13, "媒体信息检查：等不到「1 视频 + 1 音频 + 1 字幕」的轨道清单"
                            + $"（轨道 {form.Engine.GetTracks().Count} 条，HasMedia={form.Engine.HasMedia}）");
                    return false;
                }

                PumpMessages(600);      // 轨道清单在开始播放之后才填满，多给几拍

                var rows = ((播放器.MainForm)form).BuildInfoRows(clip, SessionOf(form).Tags);

                if (string.Equals(Environment.GetEnvironmentVariable("播放器_TEST_VIDEOPROBE"), "1", StringComparison.Ordinal))
                {
                    DumpTrackDetails(form.Engine);
                    Log(13, "媒体信息行：" + string.Join(" | ", rows.Select(r => r.Name + "=" + r.Value)));
                }

                var video = RowValue(rows, "视频轨 1");
                var audio = RowValue(rows, "音轨 1");
                var subtitle = RowValue(rows, "字幕轨 1");

                if (video == null)
                {
                    Log(13, "媒体信息检查：没有「视频轨 1」这一行（" + string.Join(" / ", rows.Select(r => r.Name)) + "）");
                    return false;
                }

                if (!video.Contains("640×480", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：视频分辨率不对（「{video}」，期望含 640×480）");
                    return false;
                }

                // 1.22.0 之前 DescribeCodec 传的是轨道 id，查不到就退回四字符码；而四字符码又从高到低取，
                // 于是这一列一直显示成「462h」这种反过来的东西。现在必须是真正的编码名。
                if (!video.Contains("H264", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：视频编码没有写成「H264 - MPEG-4 AVC」（「{video}」）");
                    return false;
                }

                if (video.Contains("462h", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：视频编码还是兜底的四字符码（反了）（「{video}」）");
                    return false;
                }

                if (!video.Contains("显示 16:9", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：SAR 4:3 + 640×480 应该写成「显示 16:9」（「{video}」）");
                    return false;
                }

                if (!video.Contains("25", StringComparison.Ordinal) || !video.Contains("fps", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：视频帧率没写出来（「{video}」，期望含 25 fps）");
                    return false;
                }

                // 这个样本上 libvlc 报的 profile / level 是 -1（读不到），所以这一行不该出现「@」
                if (video.Contains('@'))
                {
                    Log(13, $"媒体信息：读不到 profile / level 时不该写出来（「{video}」）");
                    return false;
                }

                if (audio == null)
                {
                    Log(13, "媒体信息检查：没有「音轨 1」这一行");
                    return false;
                }

                if (!audio.Contains("AAC", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：音轨编码没有写成「MPEG AAC Audio」（「{audio}」）");
                    return false;
                }

                if (!audio.Contains("Hz", StringComparison.Ordinal) || !audio.Contains("声道", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：音轨的采样率 / 声道没写出来（「{audio}」）");
                    return false;
                }

                if (!audio.Contains("英语", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：音轨的 eng 没有写成「英语」（「{audio}」）");
                    return false;
                }

                if (subtitle == null)
                {
                    Log(13, "媒体信息检查：没有「字幕轨 1」这一行");
                    return false;
                }

                if (!subtitle.Contains("中文", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：字幕轨的 chi 没有写成「中文」（「{subtitle}」）");
                    return false;
                }

                // 轨道自己的码率 libvlc 3 不填（实测 0），整条文件的平均码率要写出来
                var overall = RowValue(rows, "总码率");

                if (overall == null || !overall.Contains("kbps", StringComparison.Ordinal))
                {
                    Log(13, $"媒体信息：文件总码率没写出来（「{overall ?? "（没有这一行）"}」）");
                    return false;
                }

                foreach (var row in rows)
                {
                    if (row.Value.Contains("und", StringComparison.Ordinal))
                    {
                        Log(13, $"媒体信息：把「未标注语言」的 und 也写出来了（{row.Name}={row.Value}）");
                        return false;
                    }

                    if (row.Value.Contains("0 kbps", StringComparison.Ordinal))
                    {
                        Log(13, $"媒体信息：码率为 0 时写出了「0 kbps」（{row.Name}={row.Value}）");
                        return false;
                    }
                }

                Log(13, $"媒体信息正确（视频「{video}」/ 音轨「{audio}」/ 字幕「{subtitle}」）");
                return true;
            }
            finally
            {
                TryDelete(clip);
            }
        }

        private static string? RowValue(IReadOnlyList<(string Name, string Value)> rows, string name)
        {
            foreach (var row in rows)
                if (row.Name == name) return row.Value;

            return null;
        }

        /// <summary>纯逻辑：比例、码率、语言、H.264 的 profile / level、方位与投影的边界。</summary>
        private static bool CheckMediaDescriptions()
        {
            void Fail(string message) => Log(13, "媒体信息检查：" + message);

            if (MediaDescriptions.Language("und") != null ||
                MediaDescriptions.Language(null) != null ||
                MediaDescriptions.Language("eng") != "英语" ||
                MediaDescriptions.Language("CHI") != "中文" ||
                MediaDescriptions.Language("xyz") != "xyz")
            {
                Fail($"语言码映射不正确（und={MediaDescriptions.Language("und")}、eng={MediaDescriptions.Language("eng")}、"
                     + $"CHI={MediaDescriptions.Language("CHI")}、xyz={MediaDescriptions.Language("xyz")}）");
                return false;
            }

            if (MediaDescriptions.Bitrate(0) != null ||
                MediaDescriptions.Bitrate(999) != null ||
                MediaDescriptions.Bitrate(128_000) != "128 kbps" ||
                MediaDescriptions.Bitrate(1_530_000) != "1.53 Mbps")
            {
                Fail($"码率换算不正确（0={MediaDescriptions.Bitrate(0)}、999={MediaDescriptions.Bitrate(999)}、"
                     + $"128000={MediaDescriptions.Bitrate(128_000)}、1530000={MediaDescriptions.Bitrate(1_530_000)}）");
                return false;
            }

            if (MediaDescriptions.AspectRatio(640, 480, 4, 3) != "16:9" ||
                MediaDescriptions.AspectRatio(640, 480, 1, 1) != "4:3" ||
                MediaDescriptions.AspectRatio(1920, 1080, 1, 1) != "16:9" ||
                MediaDescriptions.AspectRatio(100, 100, 1, 1) != null ||
                MediaDescriptions.AspectRatio(0, 0, 1, 1) != null ||
                MediaDescriptions.AspectRatio(1920, 804, 1, 1) != "2.39:1")
            {
                Fail("显示比例换算不正确（"
                     + $"640×480 SAR4:3={MediaDescriptions.AspectRatio(640, 480, 4, 3)}、"
                     + $"640×480={MediaDescriptions.AspectRatio(640, 480, 1, 1)}、"
                     + $"1920×1080={MediaDescriptions.AspectRatio(1920, 1080, 1, 1)}、"
                     + $"100×100={MediaDescriptions.AspectRatio(100, 100, 1, 1)}、"
                     + $"1920×804={MediaDescriptions.AspectRatio(1920, 804, 1, 1)}）");
                return false;
            }

            const uint h264 = ('h' << 24) | ('2' << 16) | ('6' << 8) | '4';
            const uint hevc = ('h' << 24) | ('v' << 16) | ('c' << 8) | '1';

            if (MediaDescriptions.ProfileLevel(h264, 100, 41) != "High@4.1" ||
                MediaDescriptions.ProfileLevel(h264, 0, 41) != null ||
                MediaDescriptions.ProfileLevel(h264, 100, 0) != null ||
                MediaDescriptions.ProfileLevel(h264, 100, 99) != null ||
                MediaDescriptions.ProfileLevel(hevc, 100, 41) != null ||
                MediaDescriptions.ProfileLevel(h264, 77, 31) != "Main@3.1")
            {
                Fail("H.264 的 profile / level 翻译不正确（"
                     + $"100/41={MediaDescriptions.ProfileLevel(h264, 100, 41)}、"
                     + $"0/41={MediaDescriptions.ProfileLevel(h264, 0, 41)}、"
                     + $"100/0={MediaDescriptions.ProfileLevel(h264, 100, 0)}、"
                     + $"100/99={MediaDescriptions.ProfileLevel(h264, 100, 99)}、"
                     + $"HEVC 100/41={MediaDescriptions.ProfileLevel(hevc, 100, 41)}、"
                     + $"77/31={MediaDescriptions.ProfileLevel(h264, 77, 31)}）");
                return false;
            }

            if (MediaDescriptions.Orientation(VideoOrientation.TopLeft) != null ||
                MediaDescriptions.Orientation(VideoOrientation.BottomRight) != "旋转 180°" ||
                MediaDescriptions.Projection(VideoProjection.Rectangular) != null ||
                MediaDescriptions.Projection(VideoProjection.Equirectangular) != "360° 全景")
            {
                Fail("方位 / 投影的写法不正确（"
                     + $"TopLeft={MediaDescriptions.Orientation(VideoOrientation.TopLeft)}、"
                     + $"BottomRight={MediaDescriptions.Orientation(VideoOrientation.BottomRight)}、"
                     + $"Rectangular={MediaDescriptions.Projection(VideoProjection.Rectangular)}、"
                     + $"Equirectangular={MediaDescriptions.Projection(VideoProjection.Equirectangular)}）");
                return false;
            }

            // 兜底的四字符码：VLC 的 fourcc 第一个字符在最低字节，从低到高取才是正序
            var h264Text = 播放器.MainForm.DescribeFourCc(0x34363268);
            var avc1Text = 播放器.MainForm.DescribeFourCc(0x31637661);

            if (h264Text != "h264" || avc1Text != "avc1")
            {
                Fail($"四字符码还原不正确（0x34363268 → 「{h264Text}」、0x31637661 → 「{avc1Text}」，期望 h264 / avc1）");
                return false;
            }

            return true;
        }

        /// <summary>只给探针用：把 libvlc 报上来的原始字段打出来，判断哪些字段真的有值。</summary>
        private static void DumpTrackDetails(PlayerEngine engine)
        {
            foreach (var track in engine.GetTracks())
            {
                var line = new StringBuilder();
                line.Append(track.TrackType).Append(" id=").Append(track.Id)
                    .Append(" codec=0x").Append(track.Codec.ToString("X8"))
                    .Append(" fourcc=0x").Append(track.OriginalFourcc.ToString("X8"))
                    .Append(" profile=").Append(track.Profile)
                    .Append(" level=").Append(track.Level)
                    .Append(" bitrate=").Append(track.Bitrate)
                    .Append(" lang=").Append(track.Language ?? "-")
                    .Append(" desc=").Append(track.Description ?? "-");

                if (track.TrackType == TrackType.Video)
                {
                    var video = track.Data.Video;
                    line.Append(" size=").Append(video.Width).Append('x').Append(video.Height)
                        .Append(" sar=").Append(video.SarNum).Append(':').Append(video.SarDen)
                        .Append(" fps=").Append(video.FrameRateNum).Append('/').Append(video.FrameRateDen)
                        .Append(" orient=").Append(video.Orientation)
                        .Append(" proj=").Append(video.Projection);
                }
                else if (track.TrackType == TrackType.Audio)
                {
                    var audio = track.Data.Audio;
                    line.Append(" rate=").Append(audio.Rate).Append(" ch=").Append(audio.Channels);
                }

                Log(13, "轨道探针：" + line);
            }
        }

        /// <summary>
        /// 合成「媒体信息」样本：640×480 / 25 fps / 像素宽高比 4:3（显示 16:9）的 H.264 画面
        /// + 一条 eng 的 AAC 音轨 + 一条 chi 的字幕轨，3 秒。
        /// </summary>
        private static bool TryCreateInfoClip(out string path, out string reason)
        {
            path = Path.Combine(AppContext.BaseDirectory, "info-sample.mp4");
            reason = string.Empty;

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");
            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                reason = "没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个）";
                return false;
            }

            var subtitle = Path.Combine(AppContext.BaseDirectory, "info-sub.srt");
            File.WriteAllText(subtitle,
                "1" + Environment.NewLine +
                "00:00:00,000 --> 00:00:03,000" + Environment.NewLine +
                "Info" + Environment.NewLine,
                new UTF8Encoding(false));

            TryDelete(path);

            var arguments = new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=blue:s=640x480:d=3:r=25",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-i", subtitle,
                "-map", "0:v", "-map", "1:a", "-map", "2:s",
                "-vf", "setsar=4/3",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-b:v", "400k",
                "-c:a", "aac", "-b:a", "96k",
                "-c:s", "mov_text",
                "-metadata:s:a:0", "language=eng",
                "-metadata:s:s:0", "language=chi",
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
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                    reason = "ffmpeg 60 秒没跑完";
                    return false;
                }

                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    reason = $"ffmpeg 合成媒体信息样本失败（退出码 {process.ExitCode}）";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "调 ffmpeg 失败：" + ex.Message;
                return false;
            }
            finally
            {
                TryDelete(subtitle);
            }
        }
    }
}
