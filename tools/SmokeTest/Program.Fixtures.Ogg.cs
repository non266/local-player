// 合成样本：Ogg 页（Opus / Vorbis / Speex）与 APEv2 / APEv1 标签。
//
// 手写字节而不是调 ffmpeg：这几条检查要能稳定复现"跨页的评论包""坏的 base64""截断的页"，
// 现造的真实文件反而不好摆这些形状。真实文件那一段（GATED）另走 ffmpeg。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // Ogg
        // -----------------------------------------------------------------

        /// <summary>Opus：头信息包 + 评论包（可撑到跨页）。</summary>
        private static byte[] BuildOggOpus(
            string title, string artist, string album, string year, string track, string lyrics, byte[] cover,
            bool crossPageComment = false, string? extraEntry = null)
        {
            var comments = BuildTagEntries(title, artist, album, year, track, lyrics, cover);

            if (crossPageComment) comments.Add("PADDING=" + new string('x', 70000));
            if (extraEntry != null) comments.Add(extraEntry);

            var tagsPacket = Concat(Encoding.ASCII.GetBytes("OpusTags"), BuildVorbisComment(comments));

            var head = new byte[19];
            Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 0);
            head[8] = 1;                                        // 版本
            head[9] = 2;                                        // 声道
            head[10] = 0x00;                                    // 预跳（3840，小端）
            head[11] = 0x0F;
            BitConverter.GetBytes(48000).CopyTo(head, 12);      // 输入采样率
            head[18] = 0;                                       // 声道映射族

            return BuildOggFile(new[] { head, tagsPacket }, crossPageComment ? 1 : -1);
        }

        /// <summary>Vorbis：头信息包 + 评论包（评论包尾部带 framing bit）。</summary>
        private static byte[] BuildOggVorbis(
            string title, string artist, string album, string year, string track, string lyrics, byte[] cover)
        {
            var comments = BuildTagEntries(title, artist, album, year, track, lyrics, cover);
            var commentBody = BuildVorbisComment(comments);

            var tagsPacket = Concat(
                new byte[] { 0x03 }, Encoding.ASCII.GetBytes("vorbis"), commentBody, new byte[] { 0x01 });

            var head = new byte[30];
            head[0] = 0x01;
            Encoding.ASCII.GetBytes("vorbis").CopyTo(head, 1);
            head[11] = 2;                                       // 声道
            BitConverter.GetBytes(44100).CopyTo(head, 12);      // 采样率
            head[28] = 0xB8;                                    // 块大小（0x8 = 256，0xB = 2048）
            head[29] = 0x01;                                    // framing

            return BuildOggFile(new[] { head, tagsPacket }, -1);
        }

        /// <summary>Speex：头信息包（80 字节）+ 裸 Vorbis comment 结构的评论包。</summary>
        private static byte[] BuildOggSpeex(string title, string lyrics)
        {
            var head = new byte[80];
            Encoding.ASCII.GetBytes("Speex   ").CopyTo(head, 0);

            var tagsPacket = BuildVorbisComment(BuildTagEntries(title, null, null, null, null, lyrics, null));

            return BuildOggFile(new[] { head, tagsPacket }, -1);
        }

        /// <summary>
        /// Ogg-FLAC（1.23.0）：首包 0x7F "FLAC" + STREAMINFO，之后每个包是一个带 4 字节块头的元数据块
        /// （评论块一个包、图片块一个包，图片块打"最后一块"标志）。
        /// </summary>
        private static byte[] BuildOggFlac(
            string title, string artist, string album, string year, string track, string lyrics, byte[] cover)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            const int bitsPerSample = 16;
            const long totalSamples = sampleRate * 3L;

            var streamInfo = new byte[34];
            WriteUInt16BE(streamInfo, 0, 4096);
            WriteUInt16BE(streamInfo, 2, 4096);

            long packed = ((long)sampleRate << 44)
                          | ((long)(channels - 1) << 41)
                          | ((long)(bitsPerSample - 1) << 36)
                          | totalSamples;

            for (var i = 0; i < 8; i++) streamInfo[10 + i] = (byte)(packed >> (56 - i * 8));

            var comments = BuildTagEntries(title, artist, album, year, track, lyrics, null);

            var first = Concat(
                new byte[] { 0x7F },
                Encoding.ASCII.GetBytes("FLAC"),
                new byte[] { 1, 0 },
                new byte[] { 0, 3 },                        // 头包数（大端）：STREAMINFO + 评论 + 图片
                Encoding.ASCII.GetBytes("fLaC"),
                FlacBlock(0, streamInfo));

            var comment = FlacBlock(4, BuildVorbisComment(comments));
            var picture = FlacBlock(6, BuildFlacPicture(cover));
            picture[0] |= 0x80;                              // 最后一块

            return BuildOggFile(new[] { first, comment, picture }, -1);
        }

        /// <summary>Theora（1.23.0）：0x80 theora 头 + 0x81 theora 评论包 + 0x82 theora setup 包。</summary>
        private static byte[] BuildOggTheora(string title, string artist, string lyrics)
        {
            var ident = Concat(new byte[] { 0x80 }, Encoding.ASCII.GetBytes("theora"), new byte[1]);
            var comments = BuildTagEntries(title, artist, null, null, null, lyrics, null);
            var comment = Concat(new byte[] { 0x81 }, Encoding.ASCII.GetBytes("theora"), BuildVorbisComment(comments));
            var setup = Concat(new byte[] { 0x82 }, Encoding.ASCII.GetBytes("theora"), new byte[3]);

            return BuildOggFile(new[] { ident, comment, setup }, -1);
        }

        private static List<string> BuildTagEntries(
            string title, string artist, string album, string year, string track, string lyrics, byte[] cover)
        {
            var comments = new List<string>();

            if (title != null) comments.Add("TITLE=" + title);
            if (artist != null) comments.Add("ARTIST=" + artist);
            if (album != null) comments.Add("ALBUM=" + album);
            if (year != null) comments.Add("DATE=" + year);
            if (track != null) comments.Add("TRACKNUMBER=" + track);
            if (lyrics != null) comments.Add("LYRICS=" + lyrics);

            if (cover != null)
                comments.Add("METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(BuildFlacPicture(cover)));

            return comments;
        }

        /// <summary>
        /// 把包写成页：默认一包一页；<paramref name="splitPacketIndex"/> ≥ 0 时把那个包拆成两页
        /// （第一页 255 个段全满，第二页以"继续"标志开头），专门验跨页拼接。
        /// </summary>
        private static byte[] BuildOggFile(IReadOnlyList<byte[]> packets, int splitPacketIndex)
        {
            var pages = new List<byte[]>();
            var sequence = 0;
            var first = true;

            for (var p = 0; p < packets.Count; p++)
            {
                var chunks = LacingChunks(packets[p]);

                if (p == splitPacketIndex && chunks.Count > 255)
                {
                    pages.Add(OggPage((byte)(first ? 0x02 : 0x00), sequence++, chunks.Take(255).ToList()));
                    first = false;
                    pages.Add(OggPage(0x01, sequence++, chunks.Skip(255).ToList()));
                    continue;
                }

                pages.Add(OggPage((byte)(first ? 0x02 : 0x00), sequence++, chunks));
                first = false;
            }

            return Concat(pages.ToArray());
        }

        /// <summary>按 Ogg 的段表规则把包切段：每段最多 255 字节，最后一段必须 &lt; 255（正好整除时补一个空段）。</summary>
        private static List<byte[]> LacingChunks(byte[] packet)
        {
            var chunks = new List<byte[]>();
            var offset = 0;

            while (packet.Length - offset >= 255)
            {
                chunks.Add(packet[offset..(offset + 255)]);
                offset += 255;
            }

            chunks.Add(packet[offset..]);
            return chunks;
        }

        private static byte[] OggPage(byte type, int sequence, IReadOnlyList<byte[]> chunks)
        {
            using var stream = new MemoryStream();

            stream.Write(Encoding.ASCII.GetBytes("OggS"));
            stream.WriteByte(0);                                    // 版本
            stream.WriteByte(type);
            stream.Write(new byte[8]);                              // 粒度位置（解析用不到）
            stream.Write(BitConverter.GetBytes(0x1234));            // 串号
            stream.Write(BitConverter.GetBytes(sequence));
            stream.Write(new byte[4]);                              // CRC（解析不校验）
            stream.WriteByte((byte)chunks.Count);

            foreach (var chunk in chunks) stream.WriteByte((byte)chunk.Length);
            foreach (var chunk in chunks) stream.Write(chunk);

            return stream.ToArray();
        }

        // -----------------------------------------------------------------
        // APE
        // -----------------------------------------------------------------

        /// <summary>
        /// APEv1 / APEv2 文件：假音频数据 +（可选 header）+ 条目 + footer。
        /// <paramref name="withHeader"/> 为真时前面多 32 字节 header——标签总长仍然只算"条目 + footer"。
        /// </summary>
        private static byte[] BuildApeFile(
            string title, string artist, string album, string year, string track, string lyrics, byte[] cover,
            bool withHeader = false, int version = 2000)
        {
            using var items = new MemoryStream();
            var count = 0;

            void AddText(string key, string value)
            {
                if (value == null) return;
                WriteApeItem(items, key, Encoding.UTF8.GetBytes(value));
                count++;
            }

            AddText("Title", title);
            AddText("Artist", artist);
            AddText("Album", album);
            AddText("Year", year);
            AddText("Genre", "电子");
            AddText("Track", track);
            AddText("Comment", "猿格式备注");
            if (lyrics != null) AddText("Lyrics", lyrics);

            if (cover != null)
            {
                // 封面是二进制条目：值 = 文件名 + 0 + 图片字节
                WriteApeItem(items, "Cover Art (Front)", Concat(Encoding.UTF8.GetBytes("cover.png"), new byte[] { 0 }, cover));
                count++;
            }

            var itemsBytes = items.ToArray();
            var tagSize = itemsBytes.Length + 32;

            using var output = new MemoryStream();

            output.Write(Encoding.ASCII.GetBytes("MAC "));          // Monkey's Audio 文件头
            output.Write(new byte[4096]);                           // 假音频数据

            if (withHeader)
                output.Write(ApeHeaderFooter(version, tagSize, count, isHeader: true, hasHeader: true));

            output.Write(itemsBytes);
            output.Write(ApeHeaderFooter(version, tagSize, count, isHeader: false, hasHeader: withHeader));

            return output.ToArray();
        }

        private static void WriteApeItem(Stream stream, string key, byte[] value)
        {
            stream.Write(BitConverter.GetBytes(value.Length));
            stream.Write(BitConverter.GetBytes(0));                 // 条目标志
            stream.Write(Encoding.ASCII.GetBytes(key));
            stream.WriteByte(0);
            stream.Write(value);
        }

        private static byte[] ApeHeaderFooter(int version, int tagSize, int count, bool isHeader, bool hasHeader)
        {
            var block = new byte[32];

            Encoding.ASCII.GetBytes("APETAGEX").CopyTo(block, 0);
            BitConverter.GetBytes(version).CopyTo(block, 8);
            BitConverter.GetBytes(tagSize).CopyTo(block, 12);
            BitConverter.GetBytes(count).CopyTo(block, 16);

            // 标志：最高位 = 带 header；再下一位 = 这一块是 header
            var flags = 0u;
            if (hasHeader) flags |= 0x80000000u;
            if (isHeader) flags |= 0x20000000u;

            BitConverter.GetBytes(flags).CopyTo(block, 20);
            return block;
        }
    }
}
