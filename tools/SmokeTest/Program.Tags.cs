// 检查点 9：标签与歌词解析。样本全部手工合成，不依赖任何外部媒体文件。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 9) 标签与歌词解析
        //
        // 样本全部按各格式规范手工合成，因此这项检查不依赖任何外部媒体文件，
        // 覆盖：FLAC（Vorbis comment + PICTURE + STREAMINFO）、
        // ID3v2.3 / v2.4（含 GBK、UTF-16、USLT、TXXX、SYLT、APIC）、ID3v1、MP4 ilst。
        // -----------------------------------------------------------------

        private static bool TestTagAndLyrics()
        {
            // 主工程里的 GBK 代码页是惰性注册的，测试自己造 GBK 字节前先注册好
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var folder = Path.Combine(AppContext.BaseDirectory, "smoke-tags");
            TryDeleteDirectory(folder);
            Directory.CreateDirectory(folder);

            if (!CheckFlacTags(folder)) return false;
            if (!CheckId3v2Tags(folder)) return false;
            if (!CheckSynchronizedLyricsFrame(folder)) return false;
            if (!CheckId3v1Tags(folder)) return false;
            if (!CheckMp4Tags(folder)) return false;
            if (!CheckLrcParsing(folder)) return false;

            Log(9, "标签与歌词解析全部正确（FLAC / ID3v2.3 / ID3v2.4 / ID3v1 / MP4 / LRC）");
            return true;
        }

        private static bool CheckFlacTags(string folder)
        {
            var cover = MakePng();
            var lyrics = "[00:01.00]第一句\n[00:05.50]第二句\n";
            var path = Path.Combine(folder, "sample.flac");

            File.WriteAllBytes(path, BuildFlac("测试曲目", "测试歌手", "测试专辑", "2024", "7", lyrics, cover));

            var tags = TagReader.Read(path);

            if (tags.Title != "测试曲目" || tags.Artist != "测试歌手" || tags.Album != "测试专辑")
            {
                Log(9, $"FLAC 基本字段不正确：{tags.Title} / {tags.Artist} / {tags.Album}");
                return false;
            }

            if (tags.Year != "2024" || tags.TrackNumber != "7")
            {
                Log(9, $"FLAC 年份 / 音轨号不正确：{tags.Year} / {tags.TrackNumber}");
                return false;
            }

            if (tags.SampleRate != 44100 || tags.Channels != 2 || tags.BitsPerSample != 16)
            {
                Log(9, $"FLAC STREAMINFO 解析不正确：{tags.SampleRate} / {tags.Channels} / {tags.BitsPerSample}");
                return false;
            }

            if (tags.DurationMilliseconds is < 2900 or > 3100)
            {
                Log(9, $"FLAC 时长不正确：{tags.DurationMilliseconds} ms");
                return false;
            }

            if (!tags.HasCoverArt || tags.CoverArt!.Length != cover.Length || tags.CoverMimeType != "image/png")
            {
                Log(9, $"FLAC 内嵌封面不正确：{tags.CoverArt?.Length} 字节 / {tags.CoverMimeType}");
                return false;
            }

            // 走统一的装载器，等于顺带验证了"内嵌歌词"这条路
            var document = LyricsLoader.Load(path, tags);

            if (!document.IsSynchronized || document.Lines.Count != 2)
            {
                Log(9, $"FLAC 内嵌歌词解析失败：同步={document.IsSynchronized}，行数={document.Lines.Count}");
                return false;
            }

            if (document.Lines[0].Text != "第一句" || document.Lines[0].Time != TimeSpan.FromSeconds(1))
            {
                Log(9, $"FLAC 内嵌歌词首行不正确：{document.Lines[0]}");
                return false;
            }

            Log(9, $"FLAC 标签与内嵌歌词正确（封面 {cover.Length} 字节，{document.Lines.Count} 行歌词）");
            return true;
        }

        private static bool CheckId3v2Tags(string folder)
        {
            var cover = MakePng();
            var gbk = Encoding.GetEncoding(936);

            // 标题用 GBK 写成编码 0（国内抓轨工具最常见的写法），专辑名用 UTF-16 带 BOM
            var frames = new List<byte[]>
            {
                Id3Frame("TIT2", TextBody(0, gbk.GetBytes("晴天")), 3),
                Id3Frame("TPE1", TextBody(3, Encoding.UTF8.GetBytes("周杰伦")), 3),
                Id3Frame("TALB", TextBody(1, Concat(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes("叶惠美"))), 3),
                Id3Frame("TRCK", TextBody(3, Encoding.UTF8.GetBytes("3/10")), 3),
                Id3Frame("TCON", TextBody(0, Encoding.Latin1.GetBytes("(17)Rock")), 3),
                Id3Frame("COMM", CommentBody(3, "eng", "测试备注"), 3),
                Id3Frame("USLT", DescriptorBody(3, "chi", "[00:02.00]甲\n[00:04.00]乙\n"), 3),
                Id3Frame("APIC", PictureBody(3, "image/png", 3, cover), 3)
            };

            var path = Path.Combine(folder, "id3-uslt.mp3");
            File.WriteAllBytes(path, BuildId3File(3, frames));

            var tags = TagReader.Read(path);

            if (tags.Title != "晴天")
            {
                Log(9, $"ID3v2 GBK 标题解码不正确：{tags.Title}");
                return false;
            }

            if (tags.Artist != "周杰伦")
            {
                Log(9, $"ID3v2 UTF-8 艺术家解码不正确：{tags.Artist}");
                return false;
            }

            if (tags.Album != "叶惠美")
            {
                Log(9, $"ID3v2 UTF-16 专辑名解码不正确：{tags.Album}");
                return false;
            }

            if (tags.TrackNumber != "3/10")
            {
                Log(9, $"ID3v2 音轨号不正确：{tags.TrackNumber}");
                return false;
            }

            if (tags.Genre != "Rock")
            {
                Log(9, $"ID3v2 流派 \"(17)Rock\" 没有还原成 Rock：{tags.Genre}");
                return false;
            }

            if (tags.Comment != "测试备注")
            {
                Log(9, $"ID3v2 备注不正确：{tags.Comment}");
                return false;
            }

            if (!tags.HasCoverArt || tags.CoverArt!.Length != cover.Length)
            {
                Log(9, $"ID3v2 APIC 封面不正确：{tags.CoverArt?.Length} 字节（期望 {cover.Length}）");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("甲"))
            {
                Log(9, "ID3v2 USLT 歌词没有读到");
                return false;
            }

            // TXXX:LYRICS 是另一条常见路径，单独造一个文件
            var txxxPath = Path.Combine(folder, "id3-txxx.mp3");
            File.WriteAllBytes(txxxPath, BuildId3File(3, new List<byte[]>
            {
                Id3Frame("TIT2", TextBody(3, Encoding.UTF8.GetBytes("梯艾克斯")), 3),
                Id3Frame("TXXX", DescriptorBody(0, gbk.GetBytes("LYRICS"), "[00:03.00]来自 TXXX\n"), 3)
            }));

            var txxxTags = TagReader.Read(txxxPath);
            if (txxxTags.Lyrics == null || !txxxTags.Lyrics.Contains("来自 TXXX"))
            {
                Log(9, $"ID3v2 TXXX:LYRICS 歌词没有读到：{txxxTags.Lyrics}");
                return false;
            }

            Log(9, "ID3v2.3 标签正确（GBK / UTF-8 / UTF-16 文本、USLT、TXXX、APIC、流派还原）");
            return true;
        }

        private static bool CheckSynchronizedLyricsFrame(string folder)
        {
            // ID3v2.4 的帧长度是 synchsafe 的，SYLT 又自带毫秒时间轴——
            // 这两处都容易写错，所以单独验证
            var sylt = new List<byte> { 3 };                        // 编码：UTF-8
            sylt.AddRange(Encoding.ASCII.GetBytes("chi"));          // 语言
            sylt.Add(2);                                            // 时间戳格式：毫秒
            sylt.Add(1);                                            // 内容类型：歌词
            sylt.Add(0);                                            // 描述（空，以 0 结尾）
            sylt.AddRange(Encoding.UTF8.GetBytes("第一句"));
            sylt.Add(0);
            sylt.AddRange(UInt32BE(1000));
            sylt.AddRange(Encoding.UTF8.GetBytes("第二句"));
            sylt.Add(0);
            sylt.AddRange(UInt32BE(5500));

            var path = Path.Combine(folder, "id3-sylt.mp3");
            File.WriteAllBytes(path, BuildId3File(4, new List<byte[]>
            {
                Id3Frame("TIT2", TextBody(3, Encoding.UTF8.GetBytes("同步歌词")), 4),
                Id3Frame("SYLT", sylt.ToArray(), 4)
            }));

            var tags = TagReader.Read(path);

            if (tags.Title != "同步歌词")
            {
                Log(9, $"ID3v2.4 synchsafe 帧长度解析不正确，标题为：{tags.Title}");
                return false;
            }

            if (!tags.LyricsSynchronized)
            {
                Log(9, "ID3v2 SYLT 没有被识别成带时间轴的歌词");
                return false;
            }

            var document = LyricsDocument.Parse(tags.Lyrics);

            if (!document.IsSynchronized || document.Lines.Count != 2)
            {
                Log(9, $"SYLT 转写成的 LRC 无法解析出 2 行：{document.Lines.Count}");
                return false;
            }

            if (document.Lines[1].Time != TimeSpan.FromMilliseconds(5500))
            {
                Log(9, $"SYLT 时间戳不正确：{document.Lines[1].Time}（期望 00:00:05.500）");
                return false;
            }

            Log(9, $"ID3v2.4 SYLT 时间轴正确（{document.Lines[0]} / {document.Lines[1]}）");
            return true;
        }

        private static bool CheckId3v1Tags(string folder)
        {
            var gbk = Encoding.GetEncoding(936);
            var path = Path.Combine(folder, "id3v1.mp3");

            // 只有 128 字节尾巴的老式标签，前面填一些非标签数据
            var bytes = new byte[256 + 128];
            for (var i = 0; i < 256; i++) bytes[i] = 0x55;

            var tail = 256;
            bytes[tail] = (byte)'T';
            bytes[tail + 1] = (byte)'A';
            bytes[tail + 2] = (byte)'G';
            WriteFixed(bytes, tail + 3, 30, gbk.GetBytes("老歌"));
            WriteFixed(bytes, tail + 33, 30, gbk.GetBytes("老歌手"));
            WriteFixed(bytes, tail + 63, 30, gbk.GetBytes("老专辑"));
            WriteFixed(bytes, tail + 93, 4, Encoding.ASCII.GetBytes("1998"));
            WriteFixed(bytes, tail + 97, 28, gbk.GetBytes("老备注"));

            File.WriteAllBytes(path, bytes);

            var tags = TagReader.Read(path);

            if (tags.Title != "老歌" || tags.Artist != "老歌手" || tags.Album != "老专辑")
            {
                Log(9, $"ID3v1 基本字段不正确：{tags.Title} / {tags.Artist} / {tags.Album}");
                return false;
            }

            if (tags.Year != "1998" || tags.Comment != "老备注")
            {
                Log(9, $"ID3v1 年份 / 备注不正确：{tags.Year} / {tags.Comment}");
                return false;
            }

            Log(9, "ID3v1 兜底标签正确（GBK 文本）");
            return true;
        }

        private static bool CheckMp4Tags(string folder)
        {
            var cover = MakePng();
            var path = Path.Combine(folder, "sample.m4a");

            File.WriteAllBytes(path, BuildMp4("M4A 曲目", "M4A 歌手", "[00:01.50]M4A 歌词\n", cover, 5));

            var tags = TagReader.Read(path);

            if (tags.Title != "M4A 曲目" || tags.Artist != "M4A 歌手")
            {
                Log(9, $"MP4 ilst 文本字段不正确：{tags.Title} / {tags.Artist}");
                return false;
            }

            if (tags.TrackNumber != "5")
            {
                Log(9, $"MP4 trkn 音轨号不正确：{tags.TrackNumber}");
                return false;
            }

            if (tags.Lyrics == null || !tags.Lyrics.Contains("M4A 歌词"))
            {
                Log(9, $"MP4 ©lyr 歌词没有读到：{tags.Lyrics}");
                return false;
            }

            if (!tags.HasCoverArt || tags.CoverArt!.Length != cover.Length || tags.CoverMimeType != "image/png")
            {
                Log(9, $"MP4 covr 封面不正确：{tags.CoverArt?.Length} 字节 / {tags.CoverMimeType}");
                return false;
            }

            Log(9, "MP4 / M4A ilst 标签正确（含 ©lyr 歌词与 covr 封面）");
            return true;
        }

        private static bool CheckLrcParsing(string folder)
        {
            // 多时间戳、offset、mm:ss.xx 与 hh:mm:ss.xx 混用
            const string Text =
                "[ti:测试]\n[ar:歌手]\n[offset:+500]\n[00:10.00]甲\n[00:01.00][00:05.00]乙\n[00:20.5]丙\n[01:02:03.25]丁\n";

            var document = LyricsDocument.Parse(Text, "test.lrc");

            if (document.Lines.Count != 5)
            {
                Log(9, $"LRC 行数不正确：{document.Lines.Count}（期望 5，多时间戳要拆成多行）");
                return false;
            }

            if (document.Lines[0].Time != TimeSpan.FromMilliseconds(500) ||
                document.Lines[1].Time != TimeSpan.FromMilliseconds(4500) ||
                document.Lines[2].Time != TimeSpan.FromMilliseconds(9500) ||
                document.Lines[3].Time != TimeSpan.FromMilliseconds(20000))
            {
                Log(9, "LRC 时间戳或 offset 应用不正确："
                       + string.Join(" / ", document.Lines.Select(l => l.Time.ToString())));
                return false;
            }

            if (document.Lines[4].Time != TimeSpan.FromMilliseconds(3722750))
            {
                Log(9, $"LRC 带小时的写法解析不正确：{document.Lines[4].Time}");
                return false;
            }

            if (document.Title != "测试" || document.Artist != "歌手" || document.Offset != TimeSpan.FromMilliseconds(500))
            {
                Log(9, $"LRC 头部信息解析不正确：{document.Title} / {document.Artist} / {document.Offset}");
                return false;
            }

            // 二分定位
            if (document.LineIndexAt(TimeSpan.Zero) != -1 ||
                document.LineIndexAt(TimeSpan.FromMilliseconds(500)) != 0 ||
                document.LineIndexAt(TimeSpan.FromMilliseconds(4999)) != 1 ||
                document.LineIndexAt(TimeSpan.FromMilliseconds(9500)) != 2 ||
                document.LineIndexAt(TimeSpan.FromMilliseconds(3722750)) != 4)
            {
                Log(9, "LRC 当前行定位不正确");
                return false;
            }

            // 纯文本歌词
            var plain = LyricsDocument.Parse("第一行\n\n第二行");
            if (plain.IsSynchronized || plain.PlainText != "第一行\n\n第二行")
            {
                Log(9, $"纯文本歌词处理不正确：同步={plain.IsSynchronized}，内容=\"{plain.PlainText}\"");
                return false;
            }

            // 外挂 .lrc 用 GBK 保存（国内最常见的编码）
            var media = Path.Combine(folder, "sidecar.flac");
            File.WriteAllBytes(media, BuildFlac("外挂", "歌手", "专辑", "2020", "1", null, null));

            var gbk = Encoding.GetEncoding(936);
            File.WriteAllBytes(
                Path.Combine(folder, "sidecar.lrc"),
                gbk.GetBytes("[00:01.00]外挂歌词第一行\n[00:02.00]外挂歌词第二行\n"));

            var sidecar = LyricsLoader.Load(media, TagReader.Read(media));

            if (!sidecar.IsSynchronized || sidecar.Lines[0].Text != "外挂歌词第一行")
            {
                Log(9, $"GBK 编码的外挂 .lrc 读取不正确：{sidecar.Lines.FirstOrDefault()?.Text}");
                return false;
            }

            if (sidecar.Source != "sidecar.lrc")
            {
                Log(9, $"外挂歌词的来源标注不正确：{sidecar.Source}");
                return false;
            }

            // 带时间轴的内嵌歌词应当优先于纯文本的外挂 .lrc
            var bothMedia = Path.Combine(folder, "priority.flac");
            File.WriteAllBytes(bothMedia, BuildFlac("优先", "歌手", "专辑", "2020", "1", "[00:09.00]内嵌带时间轴\n", null));
            File.WriteAllText(Path.Combine(folder, "priority.lrc"), "外挂纯文本，没有时间轴\n");

            var priority = LyricsLoader.Load(bothMedia, TagReader.Read(bothMedia));
            if (!priority.IsSynchronized || priority.Source != LyricsLoader.EmbeddedSource)
            {
                Log(9, $"歌词优先级不正确：同步={priority.IsSynchronized}，来源={priority.Source}");
                return false;
            }

            // 同名的 .txt 是最后的兜底
            var txtMedia = Path.Combine(folder, "plain.flac");
            File.WriteAllBytes(txtMedia, BuildFlac("兜底", "歌手", "专辑", "2020", "1", null, null));
            File.WriteAllText(Path.Combine(folder, "plain.txt"), "只有 txt 歌词\n");

            var fromTxt = LyricsLoader.Load(txtMedia, TagReader.Read(txtMedia));
            if (fromTxt.IsEmpty || fromTxt.IsSynchronized || !fromTxt.PlainText.Contains("只有 txt 歌词"))
            {
                Log(9, "同名 .txt 兜底歌词没有读到");
                return false;
            }

            Log(9, "LRC 解析正确（多时间戳 / offset / 带小时写法 / GBK 外挂 / 优先级 / txt 兜底）");
            return true;
        }
    }
}
