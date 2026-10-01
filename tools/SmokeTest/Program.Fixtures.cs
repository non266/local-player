// 合成样本：WAV / PNG / JPEG / FLAC / ID3v1-v2 / MP4 的字节流。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 合成样本
        // -----------------------------------------------------------------

        private static byte[] MakePng()
        {
            using var bitmap = new Bitmap(8, 8);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(0x33, 0x99, 0xFF));
                graphics.FillEllipse(Brushes.White, 1, 1, 6, 6);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }

        private static byte[] BuildFlac(
            string title, string artist, string album, string year, string track, string lyrics, byte[] cover)
        {
            const int sampleRate = 44100;
            const int channels = 2;
            const int bitsPerSample = 16;
            const long totalSamples = sampleRate * 3L;

            var streamInfo = new byte[34];
            WriteUInt16BE(streamInfo, 0, 4096);
            WriteUInt16BE(streamInfo, 2, 4096);

            // ⚠ 操作数必须先转成 long：C# 里 int 的移位只取低 5 位，
            // 写成 (sampleRate << 44) 实际等于 << 12，造出来的样本会是错的。
            long packed = ((long)sampleRate << 44)
                          | ((long)(channels - 1) << 41)
                          | ((long)(bitsPerSample - 1) << 36)
                          | totalSamples;
            for (var i = 0; i < 8; i++) streamInfo[10 + i] = (byte)(packed >> (56 - i * 8));

            var comments = new List<string>
            {
                "TITLE=" + title,
                "ARTIST=" + artist,
                "ALBUM=" + album,
                "DATE=" + year,
                "TRACKNUMBER=" + track
            };

            if (lyrics != null) comments.Add("LYRICS=" + lyrics);

            var blocks = new List<byte[]>
            {
                FlacBlock(0, streamInfo),
                FlacBlock(4, BuildVorbisComment(comments))
            };

            if (cover != null) blocks.Add(FlacBlock(6, BuildFlacPicture(cover)));

            // 最后一个元数据块要把最高位置 1
            blocks[^1][0] |= 0x80;

            using var output = new MemoryStream();
            output.Write(Encoding.ASCII.GetBytes("fLaC"));

            foreach (var block in blocks) output.Write(block);

            return output.ToArray();
        }

        private static byte[] FlacBlock(int type, byte[] data)
        {
            var block = new byte[4 + data.Length];
            block[0] = (byte)type;
            block[1] = (byte)(data.Length >> 16);
            block[2] = (byte)(data.Length >> 8);
            block[3] = (byte)data.Length;
            data.CopyTo(block, 4);
            return block;
        }

        private static byte[] BuildVorbisComment(IReadOnlyList<string> entries)
        {
            using var stream = new MemoryStream();

            var vendor = Encoding.UTF8.GetBytes("SmokeTest");
            stream.Write(BitConverter.GetBytes(vendor.Length));
            stream.Write(vendor);
            stream.Write(BitConverter.GetBytes(entries.Count));

            foreach (var entry in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                stream.Write(BitConverter.GetBytes(bytes.Length));
                stream.Write(bytes);
            }

            return stream.ToArray();
        }

        private static byte[] BuildFlacPicture(byte[] image)
        {
            using var stream = new MemoryStream();

            WriteUInt32BE(stream, 3);                       // 图片类型：正面封面
            var mime = Encoding.ASCII.GetBytes("image/png");
            WriteUInt32BE(stream, mime.Length);
            stream.Write(mime);
            var description = Encoding.UTF8.GetBytes("cover");
            WriteUInt32BE(stream, description.Length);
            stream.Write(description);
            WriteUInt32BE(stream, 1);                       // 宽
            WriteUInt32BE(stream, 1);                       // 高
            WriteUInt32BE(stream, 24);                      // 色深
            WriteUInt32BE(stream, 0);                       // 索引色数
            WriteUInt32BE(stream, image.Length);
            stream.Write(image);

            return stream.ToArray();
        }

        /// <summary>按 ID3v2 的版本组装完整文件（标签 + 一点填充，模拟真实 mp3 的尾部）。</summary>
        private static byte[] BuildId3File(int version, IReadOnlyList<byte[]> frames)
        {
            var body = Concat(frames.ToArray());
            var header = new byte[10];

            header[0] = (byte)'I';
            header[1] = (byte)'D';
            header[2] = (byte)'3';
            header[3] = (byte)version;
            header[4] = 0;
            header[5] = 0;

            // 标签总长度永远是 synchsafe 的
            header[6] = (byte)((body.Length >> 21) & 0x7F);
            header[7] = (byte)((body.Length >> 14) & 0x7F);
            header[8] = (byte)((body.Length >> 7) & 0x7F);
            header[9] = (byte)(body.Length & 0x7F);

            using var output = new MemoryStream();
            output.Write(header);
            output.Write(body);
            output.Write(new byte[64]);     // 填充
            return output.ToArray();
        }

        private static byte[] Id3Frame(string id, byte[] body, int version)
        {
            var frame = new byte[10 + body.Length];
            Encoding.Latin1.GetBytes(id).CopyTo(frame, 0);

            if (version == 4)
            {
                // v2.4 的帧长度是 synchsafe
                frame[4] = (byte)((body.Length >> 21) & 0x7F);
                frame[5] = (byte)((body.Length >> 14) & 0x7F);
                frame[6] = (byte)((body.Length >> 7) & 0x7F);
                frame[7] = (byte)(body.Length & 0x7F);
            }
            else
            {
                frame[4] = (byte)(body.Length >> 24);
                frame[5] = (byte)(body.Length >> 16);
                frame[6] = (byte)(body.Length >> 8);
                frame[7] = (byte)body.Length;
            }

            body.CopyTo(frame, 10);
            return frame;
        }

        private static byte[] TextBody(byte encoding, byte[] text) => Concat(new[] { encoding }, text);

        /// <summary>编码字节 + 3 字节语言 + 以 0 结尾的描述 + 正文。</summary>
        private static byte[] DescriptorBody(byte encoding, string language, string text) =>
            DescriptorBody(encoding, Encoding.ASCII.GetBytes(language), text);

        /// <summary>编码字节 + 标识（3 字节语言，或 TXXX 的描述）+ 结尾 0 + 正文。</summary>
        private static byte[] DescriptorBody(byte encoding, byte[] identifier, string text)
        {
            var terminator = encoding is 1 or 2 ? new byte[] { 0, 0 } : new byte[] { 0 };

            return Concat(
                new[] { encoding },
                identifier,
                terminator,
                EncodeText(encoding, text));
        }

        /// <summary>
        /// 按 ID3v2 的编码字节编码正文。
        /// <para>样本必须与声明的编码一致，否则测的就不是解析器而是样本自己的错。</para>
        /// </summary>
        private static byte[] EncodeText(byte encoding, string text) => encoding switch
        {
            0 => Encoding.GetEncoding(936).GetBytes(text),
            1 => Concat(Encoding.Unicode.GetPreamble(), Encoding.Unicode.GetBytes(text)),
            2 => Encoding.BigEndianUnicode.GetBytes(text),
            _ => Encoding.UTF8.GetBytes(text)
        };

        private static byte[] CommentBody(byte encoding, string language, string text) =>
            DescriptorBody(encoding, language, text);

        /// <summary>编码字节 + MIME(以 0 结尾) + 图片类型 + 描述(以 0 结尾) + 图片数据。</summary>
        private static byte[] PictureBody(byte encoding, string mime, byte pictureType, byte[] image) =>
            Concat(
                new[] { encoding },
                Encoding.Latin1.GetBytes(mime),
                new byte[] { 0 },
                new[] { pictureType },
                new byte[] { 0 },
                image);

        private static byte[] BuildMp4(string title, string artist, string lyrics, byte[] cover, int trackNumber)
        {
            var items = new List<byte[]>
            {
                Mp4Item("©nam", Mp4Data(1, Encoding.UTF8.GetBytes(title))),
                Mp4Item("©ART", Mp4Data(1, Encoding.UTF8.GetBytes(artist))),
                Mp4Item("©lyr", Mp4Data(1, Encoding.UTF8.GetBytes(lyrics))),
                Mp4Item("covr", Mp4Data(14, cover))
            };

            var track = new byte[8];
            track[2] = (byte)(trackNumber >> 8);
            track[3] = (byte)trackNumber;
            track[4] = 0;
            track[5] = 10;
            items.Add(Mp4Item("trkn", Mp4Data(0, track)));

            // meta 是 full box，子原子前面要先放 4 字节版本/标志
            var ilst = Atom("ilst", Concat(items.ToArray()));
            var meta = Atom("meta", Concat(new byte[4], ilst));
            var udta = Atom("udta", meta);
            var moov = Atom("moov", udta);
            var ftyp = Atom("ftyp", Encoding.ASCII.GetBytes("M4A ").Concat(new byte[4]).Concat(Encoding.ASCII.GetBytes("M4A mp42isom")).ToArray());

            return Concat(ftyp, moov);
        }

        private static byte[] Mp4Item(string name, byte[] data) => Atom(name, data);

        /// <summary>data 原子：4 字节类型 + 4 字节保留 + 负载。</summary>
        private static byte[] Mp4Data(uint type, byte[] payload)
        {
            var body = new byte[8 + payload.Length];
            WriteUInt32BE(body, 0, (int)type);
            payload.CopyTo(body, 8);
            return Atom("data", body);
        }

        private static byte[] Atom(string type, byte[] payload)
        {
            var atom = new byte[8 + payload.Length];
            WriteUInt32BE(atom, 0, atom.Length);
            Encoding.Latin1.GetBytes(type).CopyTo(atom, 4);
            payload.CopyTo(atom, 8);
            return atom;
        }

        private static byte[] Concat(params byte[][] arrays)
        {
            var total = arrays.Sum(a => a.Length);
            var result = new byte[total];
            var offset = 0;

            foreach (var array in arrays)
            {
                array.CopyTo(result, offset);
                offset += array.Length;
            }

            return result;
        }

        private static byte[] UInt32BE(uint value) =>
            new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

        private static void WriteUInt32BE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteUInt32BE(Stream stream, int value) => stream.Write(UInt32BE((uint)value));

        private static void WriteUInt16BE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static void WriteFixed(byte[] buffer, int offset, int length, byte[] text)
        {
            Array.Fill(buffer, (byte)0, offset, length);
            Array.Copy(text, 0, buffer, offset, Math.Min(length, text.Length));
        }

        /// <summary>现场画一张真 JPEG（给"本地已有封面"那种场景当旁挂封面用）。</summary>
        private static byte[] BuildJpeg()
        {
            using var bitmap = new Bitmap(8, 8);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(0xCC, 0x66, 0x33));
            }

            using var memory = new MemoryStream();
            bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Jpeg);

            return memory.ToArray();
        }

        /// <summary>写一个 16bit / 44.1kHz 单声道正弦波 WAV。</summary>
        private static void WriteWav(string path, int seconds, double frequency)
        {
            const int sampleRate = 44100;
            const short channels = 1;
            const short bitsPerSample = 16;

            var sampleCount = sampleRate * seconds;
            var dataSize = sampleCount * channels * (bitsPerSample / 8);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { 'R', 'I', 'F', 'F' });
                writer.Write(36 + dataSize);
                writer.Write(new[] { 'W', 'A', 'V', 'E' });
                writer.Write(new[] { 'f', 'm', 't', ' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * (bitsPerSample / 8));
                writer.Write((short)(channels * (bitsPerSample / 8)));
                writer.Write(bitsPerSample);
                writer.Write(new[] { 'd', 'a', 't', 'a' });
                writer.Write(dataSize);

                for (var i = 0; i < sampleCount; i++)
                {
                    var value = Math.Sin(2 * Math.PI * frequency * i / sampleRate);
                    writer.Write((short)(value * 12000));
                }
            }
        }
    }
}
