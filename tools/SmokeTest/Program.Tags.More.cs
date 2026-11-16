// 第 9 项的一部分：ASF（WMA/ASF/WMV）、Ogg-FLAC、Theora 的标签（1.23.0）。
//
// 样本全是手写字节（形状见 Program.Fixtures.Asf.cs / Program.Fixtures.Ogg.cs）；
// 真实容器那一段是 GATED 的（播放器_TEST_TAGPROBE=1），因为"真实编码器写的头"
// 与"手写头"的差别恰好是这一段最值得验的地方。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckMoreTags(string folder)
        {
            var cover = MakePng();

            if (!CheckAsfTags(folder, cover)) return false;
            if (!CheckAsfBadSamples(folder, cover)) return false;
            if (!CheckOggFlacTags(folder, cover)) return false;
            if (!CheckTheoraTags(folder)) return false;
            if (!CheckRealThirdBatchFiles(folder)) return false;

            return true;
        }

        private static bool CheckAsfTags(string folder, byte[] cover)
        {
            const string lyrics = "[00:06.00]ASF 第一句\n";

            var path = Path.Combine(folder, "sample.wma");
            File.WriteAllBytes(path, BuildAsfFile(
                "ASF 曲目", "ASF 歌手", "ASF 备注", "ASF 专辑", "2022", 3, "爵士", lyrics, cover));

            var tags = TagReader.Read(path);

            if (tags.Title != "ASF 曲目" || tags.Artist != "ASF 歌手" || tags.Album != "ASF 专辑")
            {
                Log(9, $"ASF 基本字段不正确：{tags.Title} / {tags.Artist} / {tags.Album}");
                return false;
            }

            if (tags.Year != "2022" || tags.TrackNumber != "3" || tags.Genre != "爵士")
            {
                Log(9, $"ASF 年份 / 音轨号 / 流派不正确：{tags.Year} / {tags.TrackNumber} / {tags.Genre}");
                return false;
            }

            if (tags.Comment != "ASF 备注")
            {
                Log(9, $"ASF 备注不正确：{tags.Comment}");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("ASF 第一句", StringComparison.Ordinal))
            {
                Log(9, "ASF 歌词没有读到");
                return false;
            }

            if (!tags.HasCoverArt || tags.CoverArt!.Length != cover.Length || tags.CoverMimeType != "image/png")
            {
                Log(9, $"ASF 封面不正确：{tags.CoverArt?.Length} 字节 / {tags.CoverMimeType}");
                return false;
            }

            // 只比长度 / MIME 抓不住"起点算错"这类坏法：字节要一模一样
            if (!tags.CoverArt!.SequenceEqual(cover))
            {
                Log(9, "ASF 封面字节与样本不一致（起点 / 前缀算错了）");
                return false;
            }

            // 扩展名骗人（.bin）也要按魔数认出来
            var disguised = Path.Combine(folder, "asf-disguised.bin");
            File.WriteAllBytes(disguised, BuildAsfFile("伪装曲目", null, null, null, null, null, null, null, null));

            if (TagReader.Read(disguised).Title != "伪装曲目")
            {
                Log(9, "ASF 的扩展名骗人时没有按文件头认出来");
                return false;
            }

            Log(9, $"ASF 标签正确（Content Description + Extended Content Description，封面 {tags.CoverArt!.Length} 字节）");
            return true;
        }

        private static bool CheckAsfBadSamples(string folder, byte[] cover)
        {
            // ① 对象长度撒谎：必须停，不能照着读到文件尾
            var lying = Path.Combine(folder, "asf-lying.wma");
            File.WriteAllBytes(lying, BuildAsfFile(
                "撒谎曲目", "撒谎歌手", null, null, null, null, null, null, null, lieAboutObjectSize: true));

            var lyingTags = TagReader.Read(lying);

            if (lyingTags.Title != null)
            {
                Log(9, $"ASF 头长度撒谎时还读出了标题（「{lyingTags.Title}」）");
                return false;
            }

            // ② WM/Picture 的数据长度离谱：只丢封面，别的字段照读
            var badPicture = Path.Combine(folder, "asf-bad-picture.wma");
            File.WriteAllBytes(badPicture, BuildAsfFile(
                "坏封面曲目", "坏封面歌手", null, null, null, null, null, null, cover,
                badPictureLength: 1_000_000));

            var badTags = TagReader.Read(badPicture);

            if (badTags.Title != "坏封面曲目")
            {
                Log(9, $"ASF 的 WM/Picture 长度离谱时把别的字段也弄丢了（标题「{badTags.Title}」）");
                return false;
            }

            if (badTags.HasCoverArt)
            {
                Log(9, "ASF 的 WM/Picture 长度离谱时还把坏封面收下了");
                return false;
            }

            // ③ 截断到头的中间（连第一个头对象都装不下）：安静返回
            var full = BuildAsfFile("截断", "截断歌手", null, null, null, null, null, null, null);
            var truncated = new byte[Math.Min(70, full.Length)];
            Array.Copy(full, truncated, truncated.Length);
            File.WriteAllBytes(Path.Combine(folder, "asf-truncated.wma"), truncated);

            if (TagReader.Read(Path.Combine(folder, "asf-truncated.wma")).Title != null)
            {
                Log(9, "截断的 ASF 还读出了标题");
                return false;
            }

            Log(9, "ASF 坏样本处理正确（头长度撒谎 / 封面长度离谱 / 截断都安静停住）");
            return true;
        }

        private static bool CheckOggFlacTags(string folder, byte[] cover)
        {
            var path = Path.Combine(folder, "sample-flac.oga");
            File.WriteAllBytes(path, BuildOggFlac(
                "FLAC 曲目", "FLAC 歌手", "FLAC 专辑", "2021", "5", "[00:02.50]FLAC 一句\n", cover));

            var tags = TagReader.Read(path);

            if (tags.Title != "FLAC 曲目" || tags.Artist != "FLAC 歌手" || tags.Album != "FLAC 专辑")
            {
                Log(9, $"Ogg-FLAC 基本字段不正确：{tags.Title} / {tags.Artist} / {tags.Album}");
                return false;
            }

            if (tags.Year != "2021" || tags.TrackNumber != "5")
            {
                Log(9, $"Ogg-FLAC 年份 / 音轨号不正确：{tags.Year} / {tags.TrackNumber}");
                return false;
            }

            if (tags.SampleRate != 44100 || tags.Channels != 2 || tags.BitsPerSample != 16)
            {
                Log(9, $"Ogg-FLAC 的 STREAMINFO 不正确：{tags.SampleRate} / {tags.Channels} / {tags.BitsPerSample}");
                return false;
            }

            if (tags.DurationMilliseconds is < 2900 or > 3100)
            {
                Log(9, $"Ogg-FLAC 的时长不正确：{tags.DurationMilliseconds} ms");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("FLAC 一句", StringComparison.Ordinal))
            {
                Log(9, "Ogg-FLAC 评论块里的歌词没有读到");
                return false;
            }

            if (!tags.HasCoverArt || tags.CoverArt!.Length != cover.Length || tags.CoverMimeType != "image/png")
            {
                Log(9, $"Ogg-FLAC 的 PICTURE 块不正确：{tags.CoverArt?.Length} 字节 / {tags.CoverMimeType}");
                return false;
            }

            if (!tags.CoverArt!.SequenceEqual(cover))
            {
                Log(9, "Ogg-FLAC 的 PICTURE 块字节与样本不一致（块头 / 起点算错了）");
                return false;
            }

            Log(9, $"Ogg-FLAC 标签正确（STREAMINFO + 评论块 + PICTURE 块，封面 {tags.CoverArt!.Length} 字节）");
            return true;
        }

        private static bool CheckTheoraTags(string folder)
        {
            var path = Path.Combine(folder, "sample-theora.ogv");
            File.WriteAllBytes(path, BuildOggTheora("西奥拉曲目", "西奥拉歌手", "[00:03.00]西奥拉一句\n"));

            var tags = TagReader.Read(path);

            if (tags.Title != "西奥拉曲目" || tags.Artist != "西奥拉歌手")
            {
                Log(9, $"Theora 基本字段不正确：{tags.Title} / {tags.Artist}");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("西奥拉一句", StringComparison.Ordinal))
            {
                Log(9, "Theora 评论包里的歌词没有读到");
                return false;
            }

            Log(9, "Theora 标签正确（0x81 theora 之后的 Vorbis comment）");
            return true;
        }

        /// <summary>
        /// GATED：用 ffmpeg 现造真实 .wma / .ogv / .oga(flac)，验真实编码器写的容器头。
        /// 没设 <c>播放器_TEST_TAGPROBE=1</c>、或本机缺某个编码器时打印原因后跳过（不是失败）。
        /// </summary>
        private static bool CheckRealThirdBatchFiles(string folder)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("播放器_TEST_TAGPROBE"), "1", StringComparison.Ordinal))
            {
                Log(9, "真实 WMA / Theora / Ogg-FLAC 那一段跳过（设 播放器_TEST_TAGPROBE=1 可跑，会调 ffmpeg 现造）");
                return true;
            }

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");
            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                Log(9, "真实 WMA / Theora / Ogg-FLAC 那一段跳过：没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个）");
                return true;
            }

            // 中文元数据走命令行参数会被终端编码弄坏；中文已由手写样本覆盖，这里用 ASCII 验结构。
            var made = 0;

            if (!TryMakeRealTaggedFile(ffmpeg, folder, "real.wma",
                    "-f lavfi -i sine=frequency=440:duration=1 -c:a wmav2",
                    "RealWmaTrack", out var wmaReason))
            {
                Log(9, "真实 .wma 跳过：" + wmaReason);
            }
            else
            {
                var wma = TagReader.Read(Path.Combine(folder, "real.wma"));

                if (wma.Title != "RealWmaTrack")
                {
                    Log(9, $"真实 .wma 的标题不正确：{wma.Title}");
                    return false;
                }

                made++;
            }

            if (!TryMakeRealTaggedFile(ffmpeg, folder, "real.ogv",
                    "-f lavfi -i testsrc2=size=320x240:rate=10:duration=1 -c:v libtheora",
                    "RealTheoraTrack", out var theoraReason))
            {
                Log(9, "真实 .ogv 跳过：" + theoraReason);
            }
            else
            {
                var theora = TagReader.Read(Path.Combine(folder, "real.ogv"));

                if (theora.Title != "RealTheoraTrack")
                {
                    Log(9, $"真实 .ogv 的标题不正确：{theora.Title}");
                    return false;
                }

                made++;
            }

            if (!TryMakeRealTaggedFile(ffmpeg, folder, "real-flac.oga",
                    "-f lavfi -i sine=frequency=440:duration=1 -c:a flac -strict -2",
                    "RealFlacTrack", out var flacReason))
            {
                Log(9, "真实 Ogg-FLAC 跳过：" + flacReason);
            }
            else
            {
                var flac = TagReader.Read(Path.Combine(folder, "real-flac.oga"));

                if (flac.Title != "RealFlacTrack")
                {
                    Log(9, $"真实 Ogg-FLAC 的标题不正确：{flac.Title}");
                    return false;
                }

                made++;
            }

            Log(9, $"真实容器文件那一段完成（成功 {made} 个；跳过的不算失败，原因见上）");
            return true;
        }

        private static bool TryMakeRealTaggedFile(
            string ffmpeg, string folder, string fileName, string inputArguments, string title, out string reason)
        {
            reason = string.Empty;
            var output = Path.Combine(folder, fileName);

            try
            {
                var arguments = "-y -hide_banner -loglevel error " + inputArguments + " "
                                + "-metadata title=" + title + " -metadata artist=RealArtist "
                                + '"' + output + '"';

                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });

                if (process == null)
                {
                    reason = "ffmpeg 起不来";
                    return false;
                }

                var error = process.StandardError.ReadToEnd();

                if (!process.WaitForExit(30000))
                {
                    try { process.Kill(); } catch (Exception) { }
                    reason = "ffmpeg 30 秒没跑完";
                    return false;
                }

                if (process.ExitCode != 0 || !File.Exists(output))
                {
                    reason = $"ffmpeg 造 {fileName} 失败（退出码 {process.ExitCode}）"
                             + (string.IsNullOrWhiteSpace(error) ? string.Empty : "：" + error.Trim());
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
