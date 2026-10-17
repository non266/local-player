using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// ID3v2（含 2.3 / 2.4）的帧解析：文本、COMM、USLT、SYLT、APIC、TXXX。

namespace 播放器.Core
{
    internal static class Id3v2TagReader
    {
        // ---- ID3v2 ------------------------------------------------------------

        /// <summary>解析 ID3v2 标签，返回该标签占用的总字节数（含 10 字节头）。</summary>
        internal static long ReadId3v2(Stream stream, MediaTags tags)
        {
            stream.Position = 0;
            var header = TagBytes.ReadBytes(stream, 10);
            if (header.Length < 10) return 0;

            var major = header[3];
            var flags = header[5];
            var declaredSize = SynchSafe(header, 6, 4);
            if (declaredSize <= 0 || major is < 2 or > 4) return 0;

            var bodySize = (int)Math.Min(declaredSize, TagReader.MaxId3Bytes);
            var body = TagBytes.ReadBytes(stream, bodySize);

            // 返回值会被调用方当成"真实流偏移"用（ReadInto 里 stream.Position = tagBytes），
            // 所以必须返回<b>实际读到的</b>长度。文件比声明的短、或者标签超过 TagReader.MaxId3Bytes
            // 被截断时，返回声明的长度会让流定位落到标签中间甚至文件末尾之外。
            var consumed = 10L + body.Length;
            if (body.Length == 0) return consumed;

            // 整个标签级别的去同步：存储时插入了 0xFF 00，长度字段指的是去同步之前的大小。
            if ((flags & 0x80) != 0) body = DeUnsynchronise(body);

            var offset = 0;
            if ((flags & 0x40) != 0) offset = ExtendedHeaderSize(body, major);
            if (offset >= body.Length) return consumed;

            ParseId3Frames(body.AsSpan(offset), major, tags);
            return consumed;
        }

        /// <summary>返回扩展头的长度（含自身）。</summary>
        private static int ExtendedHeaderSize(byte[] body, byte major)
        {
            if (body.Length < 4) return body.Length;

            if (major == 3)
            {
                // v2.3：4 字节长度，不含这 4 字节自身
                var size = (int)(((uint)body[0] << 24) | ((uint)body[1] << 16) | ((uint)body[2] << 8) | body[3]);
                return Math.Clamp(4 + size, 4, body.Length);
            }

            // v2.4：synchsafe 长度，含自身
            var value = SynchSafe(body, 0, 4);
            return value <= 0 ? 0 : (int)Math.Clamp(value, 6, body.Length);
        }

        private static void ParseId3Frames(ReadOnlySpan<byte> data, byte major, MediaTags tags)
        {
            var idLength = major == 2 ? 3 : 4;
            var headerLength = major == 2 ? 6 : 10;
            var position = 0;

            while (position + headerLength <= data.Length)
            {
                var id = Encoding.Latin1.GetString(data.Slice(position, idLength));
                if (id[0] == '\0') break;          // 后面的都是 padding
                if (!IsFrameId(id)) break;         // 已经错位，继续读只会读到垃圾

                long size;
                if (major == 2)
                    size = (data[position + 3] << 16) | (data[position + 4] << 8) | data[position + 5];
                else if (major == 3)
                    size = ((uint)data[position + 4] << 24) | ((uint)data[position + 5] << 16) |
                           ((uint)data[position + 6] << 8) | data[position + 7];
                else
                    size = SynchSafe(data, position + 4, 4);

                if (size <= 0 || position + headerLength + size > data.Length) break;

                var flags = major == 2 ? (byte)0 : data[position + 9];
                var body = data.Slice(position + headerLength, (int)size);
                position += headerLength + (int)size;

                if (major == 4)
                {
                    if ((flags & 0x0C) != 0) continue;              // 压缩 / 加密的帧不处理
                    if ((flags & 0x40) != 0) body = body[1..];      // 分组标识占 1 字节
                    if (body.Length == 0) continue;
                    if ((flags & 0x02) != 0) body = DeUnsynchronise(body);
                    if ((flags & 0x01) != 0)                        // 数据长度指示占 4 字节
                    {
                        if (body.Length < 4) continue;
                        body = body[4..];
                    }
                }
                else if (major == 3 && (flags & 0xE0) != 0)
                {
                    continue;                                       // v2.3 的压缩 / 加密 / 分组帧
                }

                ApplyId3Frame(id, body, tags);
            }
        }

        private static void ApplyId3Frame(string id, ReadOnlySpan<byte> body, MediaTags tags)
        {
            switch (id)
            {
                case "TIT2":
                case "TT2":
                    tags.Title ??= DecodeTextFrame(body);
                    break;

                case "TPE1":
                case "TP1":
                    tags.Artist ??= DecodeTextFrame(body);
                    break;

                case "TALB":
                case "TAL":
                    tags.Album ??= DecodeTextFrame(body);
                    break;

                case "TPE2":
                case "TP2":
                    tags.AlbumArtist ??= DecodeTextFrame(body);
                    break;

                case "TCON":
                case "TCO":
                    tags.Genre ??= TagText.NormalizeGenre(DecodeTextFrame(body));
                    break;

                case "TDRC":
                case "TYER":
                case "TYE":
                    tags.Year ??= TagText.NormalizeYear(DecodeTextFrame(body));
                    break;

                case "TRCK":
                case "TRK":
                    tags.TrackNumber ??= DecodeTextFrame(body);
                    break;

                case "COMM":
                case "COM":
                    tags.Comment ??= DecodeCommentFrame(body);
                    break;

                case "USLT":
                case "ULT":
                    // 纯文本歌词：语言(3) + 说明(结尾 0) + 正文
                    if (DecodeDescriptorFrame(body, out var lyrics) && !string.IsNullOrWhiteSpace(lyrics))
                        tags.Lyrics ??= lyrics;
                    break;

                case "SYLT":
                case "SLT":
                    ApplySynchronizedLyrics(body, tags);
                    break;

                case "APIC":
                case "PIC":
                    ApplyPictureFrame(id, body, tags);
                    break;

                case "TXXX":
                case "TXX":
                    ApplyUserTextFrame(body, tags);
                    break;
            }
        }

        /// <summary>文本帧：编码(1) + 正文。</summary>
        private static string? DecodeTextFrame(ReadOnlySpan<byte> body)
        {
            if (body.Length < 2) return null;

            var encoding = body[0];
            var text = TagText.Decode(body[1..], encoding);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>COMM 帧：编码(1) + 语言(3) + 简短说明(结尾 0) + 正文。</summary>
        private static string? DecodeCommentFrame(ReadOnlySpan<byte> body)
        {
            if (!DecodeDescriptorFrame(body, out var text)) return null;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>跳过「编码 + 3 字节语言 + 以 0 结尾的描述」，返回后面的正文。</summary>
        private static bool DecodeDescriptorFrame(ReadOnlySpan<byte> body, out string text)
        {
            text = string.Empty;
            if (body.Length < 5) return false;

            var encoding = body[0];
            var end = TagText.FindTerminator(body, 4, encoding);
            var start = end + TagText.TerminatorLength(encoding);
            if (start >= body.Length) return false;

            text = TagText.Decode(body[start..], encoding);
            return true;
        }

        /// <summary>
        /// SYLT 帧：编码(1) + 语言(3) + 时间戳格式(1) + 内容类型(1) + 描述(结尾 0)
        /// + 若干「正文(结尾 0) + 4 字节时间戳」。
        /// <para>这里把它转写成 LRC 文本，后续统一交给 <see cref="LyricsDocument"/> 解析。</para>
        /// </summary>
        private static void ApplySynchronizedLyrics(ReadOnlySpan<byte> body, MediaTags tags)
        {
            if (body.Length < 8) return;

            var encoding = body[0];
            var timeFormat = body[4];

            // 时间戳格式 2 = 毫秒；1 = MPEG 帧号，极少见，直接放弃。
            if (timeFormat != 2) return;

            var end = TagText.FindTerminator(body, 6, encoding);
            var position = end + TagText.TerminatorLength(encoding);
            var lines = new List<string>();

            while (position < body.Length)
            {
                var textEnd = TagText.FindTerminator(body, position, encoding);
                var text = TagText.Decode(body.Slice(position, textEnd - position), encoding);

                var stampStart = textEnd + TagText.TerminatorLength(encoding);
                if (stampStart + 4 > body.Length) break;

                var stamp = ((uint)body[stampStart] << 24) | ((uint)body[stampStart + 1] << 16) |
                            ((uint)body[stampStart + 2] << 8) | body[stampStart + 3];
                position = stampStart + 4;

                if (string.IsNullOrWhiteSpace(text)) continue;

                var time = TimeSpan.FromMilliseconds(stamp);
                lines.Add($"[{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}]{text}");
            }

            if (lines.Count == 0) return;

            tags.Lyrics = string.Join("\n", lines);
            tags.LyricsSynchronized = true;
        }

        /// <summary>APIC / PIC 帧：编码(1) + MIME 或格式(3) + 图片类型(1) + 描述(结尾 0) + 图片数据。</summary>
        private static void ApplyPictureFrame(string id, ReadOnlySpan<byte> body, MediaTags tags)
        {
            if (body.Length < 5) return;

            var encoding = body[0];
            int position;
            string mime;

            if (id == "PIC")
            {
                var format = Encoding.Latin1.GetString(body.Slice(1, 3)).TrimEnd('\0');
                mime = format.Equals("PNG", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                position = 4;
            }
            else
            {
                // MIME 固定是 Latin-1 且以 0 结尾
                var mimeEnd = TagText.FindTerminator(body, 1, 0);
                mime = Encoding.Latin1.GetString(body.Slice(1, Math.Max(0, mimeEnd - 1)));
                position = mimeEnd + 1;
            }

            if (position >= body.Length) return;

            var pictureType = body[position++];
            var descriptionEnd = TagText.FindTerminator(body, position, encoding);
            var description = TagText.Decode(body.Slice(position, descriptionEnd - position), encoding);
            position = descriptionEnd + TagText.TerminatorLength(encoding);

            if (position >= body.Length) return;

            var picture = body[position..];
            if (picture.Length is 0 or > TagReader.MaxCoverBytes) return;

            // 3 = 正面封面；已经有图时只有正面封面能把它换掉。
            if (tags.CoverArt == null || pictureType == 3)
            {
                tags.CoverArt = picture.ToArray();
                tags.CoverMimeType = string.IsNullOrWhiteSpace(mime) ? TagText.GuessMimeType(picture) : mime;
            }
        }

        /// <summary>TXXX 帧：编码(1) + 说明(结尾 0) + 值。中文音乐文件常把歌词塞在 <c>TXXX:LYRICS</c>。</summary>
        private static void ApplyUserTextFrame(ReadOnlySpan<byte> body, MediaTags tags)
        {
            if (body.Length < 3) return;

            var encoding = body[0];
            var end = TagText.FindTerminator(body, 1, encoding);
            var description = TagText.Decode(body.Slice(1, end - 1), encoding);

            var start = end + TagText.TerminatorLength(encoding);
            if (start >= body.Length) return;

            if (!TagText.IsLyricsKey(description)) return;

            var value = TagText.Decode(body[start..], encoding);
            if (!string.IsNullOrWhiteSpace(value)) tags.Lyrics ??= value;
        }


        /// <summary>ID3v2.4 的 synchsafe 整数：每字节只用低 7 位。</summary>
        private static int SynchSafe(ReadOnlySpan<byte> data, int offset, int count)
        {
            if (offset + count > data.Length) return 0;

            var value = 0;
            for (var i = 0; i < count; i++) value = (value << 7) | (data[offset + i] & 0x7F);
            return value;
        }

        /// <summary>去掉去同步时插入的 0xFF 00 中的 00。</summary>
        private static byte[] DeUnsynchronise(ReadOnlySpan<byte> data)
        {
            var result = new byte[data.Length];
            var length = 0;

            for (var i = 0; i < data.Length; i++)
            {
                result[length++] = data[i];
                if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0) i++;
            }

            if (length == result.Length) return result;

            var trimmed = new byte[length];
            Array.Copy(result, trimmed, length);
            return trimmed;
        }

        private static bool IsFrameId(string id)
        {
            if (id.Length == 0) return false;

            foreach (var c in id)
                if (!((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))) return false;

            return true;
        }
    }
}
