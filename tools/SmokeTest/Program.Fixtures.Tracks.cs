// 多轨样本（第 13 步 F3 用）：2 音轨 + 2 字幕轨的 mkv。素材归素材，断言在 Program.Video.Tracks.cs。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 合成多轨样本：8 秒蓝画面 + 两条 8 秒 WAV + 两条 8 秒 SRT，混成一个 mkv
        /// （2 音轨 + 2 字幕轨）。路径约定与其它 ffmpeg 素材一致（<c>播放器_TEST_FFMPEG</c>）。
        /// </summary>
        private static bool TryCreateMultiTrackClip(out string path, out string reason)
        {
            path = Path.Combine(AppContext.BaseDirectory, "tracks-sample.mkv");
            reason = string.Empty;

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");

            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                reason = "没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个）";
                return false;
            }

            var first = Path.Combine(AppContext.BaseDirectory, "tracks-a1.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "tracks-a2.wav");
            var subtitle1 = Path.Combine(AppContext.BaseDirectory, "tracks-s1.srt");
            var subtitle2 = Path.Combine(AppContext.BaseDirectory, "tracks-s2.srt");

            WriteWav(first, seconds: 8, frequency: 440);
            WriteWav(second, seconds: 8, frequency: 880);

            var srt = "1" + Environment.NewLine
                      + "00:00:00,000 --> 00:00:04,000" + Environment.NewLine
                      + "First" + Environment.NewLine + Environment.NewLine
                      + "2" + Environment.NewLine
                      + "00:00:04,000 --> 00:00:08,000" + Environment.NewLine
                      + "Second" + Environment.NewLine;

            File.WriteAllText(subtitle1, srt, new System.Text.UTF8Encoding(false));
            File.WriteAllText(subtitle2, srt, new System.Text.UTF8Encoding(false));

            TryDelete(path);

            var arguments = new[]
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi", "-i", "color=c=blue:s=320x240:d=8:r=10",
                "-i", first,
                "-i", second,
                "-i", subtitle1,
                "-i", subtitle2,
                "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:s", "-map", "4:s",
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "96k",
                "-c:s", "srt",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=English",
                "-metadata:s:a:1", "language=jpn", "-metadata:s:a:1", "title=Japanese",
                "-metadata:s:s:0", "language=eng", "-metadata:s:s:0", "title=EnglishSub",
                "-metadata:s:s:1", "language=chi", "-metadata:s:s:1", "title=ChineseSub",
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
                    reason = $"ffmpeg 合成多轨样本失败（退出码 {process.ExitCode}）";
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
                TryDelete(first);
                TryDelete(second);
                TryDelete(subtitle1);
                TryDelete(subtitle2);
            }
        }
    }
}
