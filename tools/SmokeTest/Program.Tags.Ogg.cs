// 检查点 9 的 Ogg / APE 部分：Vorbis comment（跨页）、METADATA_BLOCK_PICTURE、APEv1 / APEv2 尾部标签。
//
// 合成样本的形状见 Program.Fixtures.Ogg.cs；这一份只管断言。
// 真实 ffmpeg 生成的文件那一段是 GATED 的（播放器_TEST_OGGPROBE=1），
// 因为"真实 Ogg 页"与"手写页"的差别恰好是这一段最值得验的地方。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckOggAndApeTags(string folder)
        {
            var cover = MakePng();

            if (!CheckOpusTags(folder, cover)) return false;
            if (!CheckVorbisTags(folder)) return false;
            if (!CheckSpeexTags(folder)) return false;
            if (!CheckApeTags(folder, cover)) return false;
            if (!CheckBadTagSamples(folder, cover)) return false;
            if (!CheckRealOggFiles(folder)) return false;

            return true;
        }

        private static bool CheckOpusTags(string folder, byte[] cover)
        {
            const string lyrics = "[00:01.00]奥普斯第一句\n[00:05.00]奥普斯第二句\n";

            // 评论包故意撑到 70 KB：一页最多 255 × 255 字节，所以一定跨页——
            // "只读第一页"的错法会在这里露出来。
            var path = Path.Combine(folder, "sample-opus.opus");
            File.WriteAllBytes(path, BuildOggOpus(
                "奥普斯曲目", "奥普斯歌手", "奥普斯专辑", "2023", "4", lyrics, cover, crossPageComment: true));

            var tags = TagReader.Read(path);

            if (tags.Title != "奥普斯曲目" || tags.Artist != "奥普斯歌手" || tags.Album != "奥普斯专辑")
            {
                Log(9, $"Opus 基本字段不正确：{tags.Title} / {tags.Artist} / {tags.Album}");
                return false;
            }

            if (tags.Year != "2023" || tags.TrackNumber != "4")
            {
                Log(9, $"Opus 年份 / 音轨号不正确：{tags.Year} / {tags.TrackNumber}");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("奥普斯第二句"))
            {
                Log(9, "Opus 跨页评论包里的歌词没有读到");
                return false;
            }

            if (!tags.HasCoverArt || tags.CoverArt!.Length != cover.Length || tags.CoverMimeType != "image/png")
            {
                Log(9, $"Opus 的 METADATA_BLOCK_PICTURE 封面不正确：{tags.CoverArt?.Length} 字节 / {tags.CoverMimeType}");
                return false;
            }

            Log(9, $"Opus 跨页标签正确（评论包跨两页，封面 {tags.CoverArt!.Length} 字节）");
            return true;
        }

        private static bool CheckVorbisTags(string folder)
        {
            var path = Path.Combine(folder, "sample-vorbis.ogg");
            File.WriteAllBytes(path, BuildOggVorbis(
                "沃比斯曲目", "沃比斯歌手", "沃比斯专辑", "2021", "9",
                "[00:02.00]沃比斯一句\n", MakePng()));

            var tags = TagReader.Read(path);

            if (tags.Title != "沃比斯曲目" || tags.Artist != "沃比斯歌手")
            {
                Log(9, $"Vorbis 基本字段不正确：{tags.Title} / {tags.Artist}");
                return false;
            }

            if (tags.Year != "2021" || tags.TrackNumber != "9")
            {
                Log(9, $"Vorbis 年份 / 音轨号不正确：{tags.Year} / {tags.TrackNumber}");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("沃比斯一句"))
            {
                Log(9, "Vorbis 评论包里的歌词没有读到");
                return false;
            }

            if (!tags.HasCoverArt)
            {
                Log(9, "Vorbis 的 METADATA_BLOCK_PICTURE 封面没有读到");
                return false;
            }

            Log(9, "Vorbis 标签正确（framing bit 之后的评论包、封面、歌词）");
            return true;
        }

        private static bool CheckSpeexTags(string folder)
        {
            var path = Path.Combine(folder, "sample-speex.spx");
            File.WriteAllBytes(path, BuildOggSpeex("斯皮克斯曲目", "[00:03.00]斯皮克斯一句\n"));

            var tags = TagReader.Read(path);

            if (tags.Title != "斯皮克斯曲目")
            {
                Log(9, $"Speex 标题不正确：{tags.Title}");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("斯皮克斯一句"))
            {
                Log(9, "Speex 评论包里的歌词没有读到");
                return false;
            }

            Log(9, "Speex 标签正确（无前缀的评论包）");
            return true;
        }

        private static bool CheckApeTags(string folder, byte[] cover)
        {
            const string lyrics = "[00:04.00]猿格式一句\n";

            var v2 = Path.Combine(folder, "sample.ape");
            File.WriteAllBytes(v2, BuildApeFile(
                "猿曲目", "猿歌手", "猿专辑", "2019", "2", lyrics, cover, withHeader: false, version: 2000));

            var tags = TagReader.Read(v2);

            if (tags.Title != "猿曲目" || tags.Artist != "猿歌手" || tags.Album != "猿专辑")
            {
                Log(9, $"APE 基本字段不正确：{tags.Title} / {tags.Artist} / {tags.Album}");
                return false;
            }

            if (tags.Year != "2019" || tags.TrackNumber != "2" || tags.Genre != "电子")
            {
                Log(9, $"APE 年份 / 音轨号 / 流派不正确：{tags.Year} / {tags.TrackNumber} / {tags.Genre}");
                return false;
            }

            if (tags.Comment != "猿格式备注")
            {
                Log(9, $"APE 备注不正确：{tags.Comment}");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("猿格式一句"))
            {
                Log(9, "APE 歌词没有读到");
                return false;
            }

            if (!tags.HasCoverArt || tags.CoverArt!.Length != cover.Length || tags.CoverMimeType != "image/png")
            {
                Log(9, $"APE 的 Cover Art (Front) 不正确：{tags.CoverArt?.Length} 字节 / {tags.CoverMimeType}");
                return false;
            }

            // APEv1 常见"带 header"的形状；标签总长只算"条目 + footer"，起点不能算错
            var v1 = Path.Combine(folder, "sample-v1.ape");
            File.WriteAllBytes(v1, BuildApeFile(
                "老猿曲目", "老猿歌手", null, null, null, null, null, withHeader: true, version: 1000));

            var v1Tags = TagReader.Read(v1);

            if (v1Tags.Title != "老猿曲目" || v1Tags.Artist != "老猿歌手")
            {
                Log(9, $"APEv1（带 header）字段不正确：{v1Tags.Title} / {v1Tags.Artist}");
                return false;
            }

            Log(9, $"APE 标签正确（APEv2 无 header / APEv1 带 header，封面 {tags.CoverArt!.Length} 字节）");
            return true;
        }

        private static bool CheckBadTagSamples(string folder, byte[] cover)
        {
            // ① 坏的 base64 只能丢掉封面，别的字段照读
            var badBase64 = Path.Combine(folder, "bad-picture.opus");
            File.WriteAllBytes(badBase64, BuildOggOpus(
                "坏封面", null, null, null, null, null, null,
                extraEntry: "METADATA_BLOCK_PICTURE=这不是 base64"));

            var badTags = TagReader.Read(badBase64);

            if (badTags.Title != "坏封面")
            {
                Log(9, $"坏 base64 把别的字段也弄丢了（标题「{badTags.Title}」）");
                return false;
            }

            if (badTags.HasCoverArt)
            {
                Log(9, "坏的 base64 封面被当成有效封面收下了");
                return false;
            }

            // ② 截断的 APE footer：认不出标签，安静返回
            var full = BuildApeFile("截断", "截断歌手", null, null, null, null, cover);
            var truncated = new byte[full.Length - 16];
            Array.Copy(full, truncated, truncated.Length);

            var truncatedPath = Path.Combine(folder, "truncated.ape");
            File.WriteAllBytes(truncatedPath, truncated);

            var truncatedTags = TagReader.Read(truncatedPath);

            if (truncatedTags.Title != null)
            {
                Log(9, $"截断的 APE footer 还读出了标题（「{truncatedTags.Title}」）");
                return false;
            }

            // ③ 截断的 Ogg 页：页头声明的段表比文件剩下的还长 → 不抛，警告之外什么都不做
            var ogg = BuildOggVorbis("截断的奥格", null, null, null, null, null, null);
            var cutOgg = new byte[Math.Max(0, ogg.Length - 40)];
            Array.Copy(ogg, cutOgg, cutOgg.Length);

            var cutPath = Path.Combine(folder, "truncated.ogg");
            File.WriteAllBytes(cutPath, cutOgg);

            var cutTags = TagReader.Read(cutPath);

            if (cutTags.Title != null)
            {
                Log(9, $"截断的 Ogg 页还读出了标题（「{cutTags.Title}」）");
                return false;
            }

            Log(9, "坏样本处理正确（坏 base64 只丢封面；截断的 APE footer / Ogg 页安静返回）");
            return true;
        }

        /// <summary>
        /// GATED：用 ffmpeg 现造真实的 .opus / .ogg，验解析器认不认真正的 Ogg 页（带 CRC、多页、真实编码器写的评论包）。
        /// 没设 <c>播放器_TEST_OGGPROBE=1</c>、或本机没有 ffmpeg / 没有编码器时打印原因后跳过。
        /// </summary>
        private static bool CheckRealOggFiles(string folder)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("播放器_TEST_OGGPROBE"), "1", StringComparison.Ordinal))
            {
                Log(9, "真实 Ogg 文件那一段跳过（设 播放器_TEST_OGGPROBE=1 可跑，会调 ffmpeg 现造）");
                return true;
            }

            var ffmpeg = Environment.GetEnvironmentVariable("播放器_TEST_FFMPEG");
            if (string.IsNullOrWhiteSpace(ffmpeg))
                ffmpeg = @"D:\ffmpeg-8.0-full_build\bin\ffmpeg.exe";

            if (!File.Exists(ffmpeg))
            {
                Log(9, "真实 Ogg 文件那一段跳过：没有 ffmpeg（设 播放器_TEST_FFMPEG 指一个）");
                return true;
            }

            // 带中文的元数据走命令行参数字节，不同终端的编码会把样本本身弄坏；
            // 中文已经由手写样本覆盖，这里用 ASCII 验"真实编码器写的评论包结构"。
            if (!TryMakeRealOgg(ffmpeg, folder, "real-opus.opus", "libopus", "RealOpusTrack", "RealOpusArtist",
                    out var opusReason))
            {
                Log(9, "真实 Ogg 文件那一段跳过：" + opusReason);
                return true;
            }

            if (!TryMakeRealOgg(ffmpeg, folder, "real-vorbis.ogg", "libvorbis", "RealVorbisTrack", "RealVorbisArtist",
                    out var vorbisReason))
            {
                Log(9, "真实 Ogg 文件那一段跳过：" + vorbisReason);
                return true;
            }

            var opus = TagReader.Read(Path.Combine(folder, "real-opus.opus"));
            if (opus.Title != "RealOpusTrack" || opus.Artist != "RealOpusArtist")
            {
                Log(9, $"真实 .opus 的标签不正确：{opus.Title} / {opus.Artist}");
                return false;
            }

            var vorbis = TagReader.Read(Path.Combine(folder, "real-vorbis.ogg"));
            if (vorbis.Title != "RealVorbisTrack" || vorbis.Artist != "RealVorbisArtist")
            {
                Log(9, $"真实 .ogg 的标签不正确：{vorbis.Title} / {vorbis.Artist}");
                return false;
            }

            Log(9, "真实 Ogg 文件正确（ffmpeg 现造 .opus / .ogg，标题与歌手都读到了）");
            return true;
        }

        private static bool TryMakeRealOgg(
            string ffmpeg, string folder, string fileName, string encoder, string title, string artist, out string reason)
        {
            reason = string.Empty;
            var output = Path.Combine(folder, fileName);

            try
            {
                var arguments = "-y -hide_banner -loglevel error "
                                + "-f lavfi -i sine=frequency=440:duration=1 "
                                + "-c:a " + encoder + " "
                                + "-metadata title=" + title + " -metadata artist=" + artist + " "
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
                    reason = $"ffmpeg 造 {fileName} 失败（编码器 {encoder}？退出码 {process.ExitCode}）";
                    if (!string.IsNullOrWhiteSpace(error)) reason += "：" + error.Trim();
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
