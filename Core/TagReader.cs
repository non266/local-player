using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace 播放器.Core
{
    /// <summary>
    /// 直接从媒体文件里读元数据标签，不经过 libvlc。
    /// <para>
    /// 之所以自己解析：libvlc 只暴露 <c>Media.Meta()</c> 的十几个字段，
    /// <b>没有歌词</b>，也不给内嵌封面；而本播放器需要显示歌词与专辑封面，
    /// 所以这里按格式规范自己读文件内部结构：
    /// </para>
    /// <list type="bullet">
    /// <item>ID3v2（MP3 等）：<c>TIT2/TPE1/TALB/TCON/TRCK/COMM</c>、<c>USLT</c>（纯文本歌词）、
    ///       <c>SYLT</c>（带时间轴歌词）、<c>APIC</c>（封面）、<c>TXXX:LYRICS</c></item>
    /// <item>FLAC：<c>STREAMINFO</c>（采样率/声道/位深）、<c>VORBIS_COMMENT</c>
    ///       （含 <c>LYRICS</c> / <c>UNSYNCEDLYRICS</c>）、<c>PICTURE</c>（封面）</item>
    /// <item>MP4 / M4A：<c>ilst</c> 里的 <c>©nam ©ART ©alb ©day ©gen ©cmt ©lyr covr trkn</c></item>
    /// <item>ID3v1：仅在上面的标签完全读不到时兜底</item>
    /// </list>
    /// <para>
    /// 整个解析过程<b>只读不写</b>，任何异常都被吞掉并返回空标签——
    /// 标签读不出来绝不能影响播放。
    /// </para>
    /// </summary>
    public static class TagReader
    {
        /// <summary>读 ID3v2 时最多向后读的字节数，防止被异常的长度字段牵着走。</summary>
        private const int MaxId3Bytes = 24 * 1024 * 1024;

        /// <summary>内嵌封面的体积上限；超过这个数基本可以认为是损坏数据。</summary>
        private const int MaxCoverBytes = 12 * 1024 * 1024;

        /// <summary>Vorbis comment 里最多解析多少条，防御损坏文件。</summary>
        private const int MaxVorbisComments = 4096;

        /// <summary>
        /// 读取指定文件的标签；文件不存在、格式不支持或数据损坏时返回空标签（不抛异常）。
        /// </summary>
        public static MediaTags Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new MediaTags();

            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                var tags = new MediaTags();
                ReadInto(stream, Path.GetExtension(path).ToLowerInvariant(), tags);
                return tags;
            }
            catch (Exception)
            {
                return new MediaTags();
            }
        }

        // ---- 入口分发 ---------------------------------------------------------

        private static void ReadInto(Stream stream, string extension, MediaTags tags)
        {
            stream.Position = 0;
            var magic = ReadBytes(stream, 12);
            var head = Encoding.Latin1.GetString(magic);

            var hasId3 = head.StartsWith("ID3", StringComparison.Ordinal);
            var isMp4 = IsMp4Extension(extension) || (head.Length >= 8 && string.CompareOrdinal(head, 4, "ftyp", 0, 4) == 0);

            if (hasId3)
            {
                // ID3v2 的 total size 只有解析完头部才知道，用返回值带出来。
                var tagBytes = ReadId3v2(stream, tags);

                // 少数 FLAC 会在 fLaC 之前挂一个 ID3v2 标签，跳过去再认一次。
                stream.Position = Math.Clamp(tagBytes, 0, stream.Length);
                var after = ReadBytes(stream, 4);
                var marker = Encoding.Latin1.GetString(after);

                if (marker == "fLaC")
                    ReadFlacBlocks(stream, tags);      // 注意：此时流已在 fLaC 之后，见方法内说明
                else if (isMp4)
                    ReadMp4(stream, tags);
            }
            else if (head.StartsWith("fLaC", StringComparison.Ordinal))
            {
                // 上面读了 12 字节 magic，而 ReadFlacBlocks 要求流停在 fLaC 标记之后。
                stream.Position = 4;
                ReadFlacBlocks(stream, tags);
            }
            else if (isMp4)
            {
                ReadMp4(stream, tags);
            }

            // ID3v1 是 128 字节的尾巴，只有前面什么都没读到时才值得一看。
            if (tags.Title == null && tags.Artist == null && tags.Album == null)
                ReadId3v1(stream, tags);
        }

        private static bool IsMp4Extension(string extension) =>
            extension is ".mp4" or ".m4a" or ".m4v" or ".m4b" or ".mov" or ".3gp" or ".3g2" or ".f4v";

        // ---- ID3v2 ------------------------------------------------------------

        /// <summary>解析 ID3v2 标签，返回该标签占用的总字节数（含 10 字节头）。</summary>
        private static long ReadId3v2(Stream stream, MediaTags tags)
        {
            stream.Position = 0;
            var header = ReadBytes(stream, 10);
            if (header.Length < 10) return 0;

            var major = header[3];
            var flags = header[5];
            var declaredSize = SynchSafe(header, 6, 4);
            if (declaredSize <= 0 || major is < 2 or > 4) return 0;

            var bodySize = (int)Math.Min(declaredSize, MaxId3Bytes);
            var body = ReadBytes(stream, bodySize);

            // 返回值会被调用方当成"真实流偏移"用（ReadInto 里 stream.Position = tagBytes），
            // 所以必须返回<b>实际读到的</b>长度。文件比声明的短、或者标签超过 MaxId3Bytes
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
                    tags.Genre ??= NormalizeGenre(DecodeTextFrame(body));
                    break;

                case "TDRC":
                case "TYER":
                case "TYE":
                    tags.Year ??= NormalizeYear(DecodeTextFrame(body));
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
            var text = Decode(body[1..], encoding);
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
            var end = FindTerminator(body, 4, encoding);
            var start = end + TerminatorLength(encoding);
            if (start >= body.Length) return false;

            text = Decode(body[start..], encoding);
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

            var end = FindTerminator(body, 6, encoding);
            var position = end + TerminatorLength(encoding);
            var lines = new List<string>();

            while (position < body.Length)
            {
                var textEnd = FindTerminator(body, position, encoding);
                var text = Decode(body.Slice(position, textEnd - position), encoding);

                var stampStart = textEnd + TerminatorLength(encoding);
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
                var mimeEnd = FindTerminator(body, 1, 0);
                mime = Encoding.Latin1.GetString(body.Slice(1, Math.Max(0, mimeEnd - 1)));
                position = mimeEnd + 1;
            }

            if (position >= body.Length) return;

            var pictureType = body[position++];
            var descriptionEnd = FindTerminator(body, position, encoding);
            var description = Decode(body.Slice(position, descriptionEnd - position), encoding);
            position = descriptionEnd + TerminatorLength(encoding);

            if (position >= body.Length) return;

            var picture = body[position..];
            if (picture.Length is 0 or > MaxCoverBytes) return;

            // 3 = 正面封面；已经有图时只有正面封面能把它换掉。
            if (tags.CoverArt == null || pictureType == 3)
            {
                tags.CoverArt = picture.ToArray();
                tags.CoverMimeType = string.IsNullOrWhiteSpace(mime) ? GuessMimeType(picture) : mime;
            }
        }

        /// <summary>TXXX 帧：编码(1) + 说明(结尾 0) + 值。中文音乐文件常把歌词塞在 <c>TXXX:LYRICS</c>。</summary>
        private static void ApplyUserTextFrame(ReadOnlySpan<byte> body, MediaTags tags)
        {
            if (body.Length < 3) return;

            var encoding = body[0];
            var end = FindTerminator(body, 1, encoding);
            var description = Decode(body.Slice(1, end - 1), encoding);

            var start = end + TerminatorLength(encoding);
            if (start >= body.Length) return;

            if (!IsLyricsKey(description)) return;

            var value = Decode(body[start..], encoding);
            if (!string.IsNullOrWhiteSpace(value)) tags.Lyrics ??= value;
        }

        // ---- FLAC -------------------------------------------------------------

        /// <summary>
        /// 解析 FLAC 的元数据块。
        /// <para>
        /// 进入时流的位置必须是 <c>fLaC</c> 标记之后（4 字节处），
        /// 即紧邻第一个元数据块头。
        /// </para>
        /// </summary>
        private static void ReadFlacBlocks(Stream stream, MediaTags tags)
        {
            var header = new byte[4];

            while (true)
            {
                if (stream.Read(header, 0, 4) != 4) return;

                var isLast = (header[0] & 0x80) != 0;
                var type = header[0] & 0x7F;
                var length = (header[1] << 16) | (header[2] << 8) | header[3];

                if (length < 0 || stream.Position + length > stream.Length) return;

                switch (type)
                {
                    case 0:     // STREAMINFO
                        ReadFlacStreamInfo(ReadBytes(stream, length), tags);
                        break;
                    case 4:     // VORBIS_COMMENT
                        ReadVorbisComment(ReadBytes(stream, length), tags);
                        break;
                    case 6:     // PICTURE
                        ReadFlacPicture(ReadBytes(stream, length), tags);
                        break;
                    default:
                        Skip(stream, length);
                        break;
                }

                if (isLast) return;
            }
        }

        /// <summary>STREAMINFO：34 字节，尾部打包了采样率、声道数、位深和总样本数。</summary>
        private static void ReadFlacStreamInfo(byte[] data, MediaTags tags)
        {
            // 第 10~17 字节共 64 位：20 位采样率 | 3 位(声道数-1) | 5 位(位深-1) | 36 位总样本数
            if (data.Length < 18) return;

            long packed = 0;
            for (var i = 10; i < 18; i++) packed = (packed << 8) | data[i];

            var sampleRate = (int)((packed >> 44) & 0xFFFFF);
            var channels = (int)((packed >> 41) & 0x07) + 1;
            var bitsPerSample = (int)((packed >> 36) & 0x1F) + 1;
            var totalSamples = packed & 0xF_FFFF_FFFFL;

            if (sampleRate > 0) tags.SampleRate = sampleRate;
            if (channels > 0) tags.Channels = channels;
            if (bitsPerSample > 0) tags.BitsPerSample = bitsPerSample;

            if (sampleRate > 0 && totalSamples > 0)
                tags.DurationMilliseconds = totalSamples * 1000L / sampleRate;
        }

        /// <summary>VORBIS_COMMENT：全部小端序，键名大小写不敏感。</summary>
        private static void ReadVorbisComment(byte[] data, MediaTags tags)
        {
            var offset = 0;

            if (!TryReadUInt32LittleEndian(data, ref offset, out var vendorLength)) return;
            if (!TryAdvance(data, ref offset, (int)vendorLength)) return;
            if (!TryReadUInt32LittleEndian(data, ref offset, out var count)) return;

            count = Math.Min(count, MaxVorbisComments);

            for (uint i = 0; i < count; i++)
            {
                if (!TryReadUInt32LittleEndian(data, ref offset, out var length)) return;
                if (!TryAdvance(data, ref offset, (int)length)) return;

                var entry = Encoding.UTF8.GetString(data, offset - (int)length, (int)length);
                var separator = entry.IndexOf('=');
                if (separator <= 0) continue;

                ApplyVorbisField(entry[..separator].Trim(), entry[(separator + 1)..], tags);
            }
        }

        private static void ApplyVorbisField(string key, string value, MediaTags tags)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            // 归一化：去空格、去下划线、转大写，这样 "Album Artist" / "ALBUMARTIST" 都能认。
            var normalized = NormalizeKey(key);

            switch (normalized)
            {
                case "TITLE":
                    tags.Title ??= value;
                    break;
                case "ARTIST":
                    tags.Artist ??= value;
                    break;
                case "ALBUM":
                    tags.Album ??= value;
                    break;
                case "ALBUMARTIST":
                    tags.AlbumArtist ??= value;
                    break;
                case "GENRE":
                    tags.Genre ??= value;
                    break;
                case "DATE":
                case "YEAR":
                    tags.Year ??= NormalizeYear(value);
                    break;
                case "TRACKNUMBER":
                    tags.TrackNumber ??= value;
                    break;
                case "COMMENT":
                case "DESCRIPTION":
                    tags.Comment ??= value;
                    break;
                case "LYRICS":
                case "LYRIC":
                case "UNSYNCEDLYRICS":
                case "SYNCEDLYRICS":
                case "LYRICSLRC":
                    // 有些抓轨工具既写 LYRICS 又写 UNSYNCEDLYRICS，取第一个非空的即可。
                    tags.Lyrics ??= value;
                    break;
            }
        }

        /// <summary>FLAC PICTURE 块：除最后的图片数据外全部是大端序。</summary>
        private static void ReadFlacPicture(byte[] data, MediaTags tags)
        {
            var offset = 0;

            if (!TryReadUInt32BigEndian(data, ref offset, out var pictureType)) return;
            if (!TryReadUInt32BigEndian(data, ref offset, out var mimeLength)) return;
            if (!TryAdvance(data, ref offset, (int)mimeLength)) return;

            var mime = Encoding.ASCII.GetString(data, offset - (int)mimeLength, (int)mimeLength);

            if (!TryReadUInt32BigEndian(data, ref offset, out var descriptionLength)) return;
            if (!TryAdvance(data, ref offset, (int)descriptionLength)) return;

            var description = Encoding.UTF8.GetString(data, offset - (int)descriptionLength, (int)descriptionLength);

            // 宽、高、色深、索引色数，各 4 字节
            for (var i = 0; i < 4; i++)
                if (!TryReadUInt32BigEndian(data, ref offset, out _)) return;

            if (!TryReadUInt32BigEndian(data, ref offset, out var pictureLength)) return;
            if (!TryAdvance(data, ref offset, (int)pictureLength)) return;

            if (pictureLength == 0 || pictureLength > MaxCoverBytes) return;

            if (tags.CoverArt == null || pictureType == 3)
            {
                tags.CoverArt = new byte[(int)pictureLength];
                Array.Copy(data, offset - (int)pictureLength, tags.CoverArt, 0, (int)pictureLength);
                tags.CoverMimeType = string.IsNullOrWhiteSpace(mime) ? GuessMimeType(tags.CoverArt) : mime;
            }
        }

        // ---- MP4 / M4A --------------------------------------------------------

        /// <summary>MP4 是嵌套的 atom 树，走 moov → udta → meta → ilst。</summary>
        private static void ReadMp4(Stream stream, MediaTags tags)
        {
            foreach (var atom in EnumerateAtoms(stream, 0, stream.Length))
            {
                if (atom.Type != "moov") continue;

                foreach (var udta in FindAtoms(stream, atom, "udta"))
                    foreach (var meta in FindAtoms(stream, udta, "meta"))
                        foreach (var ilst in FindAtoms(stream, meta, "ilst", leadingBytes: 4))
                            ReadIlst(stream, ilst, tags);

                // 少数文件把 meta 直接放在 moov 下
                foreach (var meta in FindAtoms(stream, atom, "meta"))
                    foreach (var ilst in FindAtoms(stream, meta, "ilst", leadingBytes: 4))
                        ReadIlst(stream, ilst, tags);
            }
        }

        private static void ReadIlst(Stream stream, Atom ilst, MediaTags tags)
        {
            foreach (var item in EnumerateAtoms(stream, ilst.DataStart, ilst.End))
            {
                foreach (var dataAtom in EnumerateAtoms(stream, item.DataStart, item.End))
                {
                    if (dataAtom.Type != "data" || dataAtom.DataLength <= 8) continue;

                    // data 原子开头 8 字节：4 字节版本/标志（低 24 位是数据类型）+ 4 字节保留
                    stream.Position = dataAtom.DataStart;
                    var head = ReadBytes(stream, 8);
                    if (head.Length < 8) break;

                    var dataType = ((uint)head[0] << 24) | ((uint)head[1] << 16) | ((uint)head[2] << 8) | head[3];
                    var payloadLength = (int)Math.Min(dataAtom.DataLength - 8, MaxCoverBytes);

                    stream.Position = dataAtom.DataStart + 8;
                    var payload = ReadBytes(stream, payloadLength);

                    ApplyIlstField(item.Type, dataType, payload, tags);
                    break;
                }
            }
        }

        private static void ApplyIlstField(string atomType, uint dataType, byte[] payload, MediaTags tags)
        {
            if (payload.Length == 0) return;

            switch (atomType)
            {
                case "©nam":
                    tags.Title ??= Utf8(payload);
                    break;
                case "©ART":
                    tags.Artist ??= Utf8(payload);
                    break;
                case "aART":
                    tags.AlbumArtist ??= Utf8(payload);
                    break;
                case "©alb":
                    tags.Album ??= Utf8(payload);
                    break;
                case "©gen":
                    tags.Genre ??= Utf8(payload);
                    break;
                case "©day":
                    tags.Year ??= NormalizeYear(Utf8(payload));
                    break;
                case "©cmt":
                    tags.Comment ??= Utf8(payload);
                    break;
                case "©lyr":
                    tags.Lyrics ??= Utf8(payload);
                    break;
                case "trkn":
                    // 8 字节：2 字节保留 + 2 字节音轨号 + 2 字节总数 + 2 字节保留
                    if (payload.Length >= 4)
                    {
                        var number = (payload[2] << 8) | payload[3];
                        if (number > 0) tags.TrackNumber ??= number.ToString();
                    }
                    break;
                case "covr":
                    if (tags.CoverArt != null) break;
                    tags.CoverArt = payload;
                    tags.CoverMimeType = dataType switch
                    {
                        13 => "image/jpeg",
                        14 => "image/png",
                        27 => "image/bmp",
                        _ => GuessMimeType(payload)
                    };
                    break;
            }
        }

        private static string Utf8(byte[] payload) => Encoding.UTF8.GetString(payload).TrimEnd('\0').Trim();

        /// <summary>遍历 [start, end) 区间内的同级 atom。</summary>
        private static IEnumerable<Atom> EnumerateAtoms(Stream stream, long start, long end)
        {
            var position = start;

            while (position + 8 <= end)
            {
                stream.Position = position;
                var header = ReadBytes(stream, 8);
                if (header.Length != 8) yield break;

                var size = (long)(((uint)header[0] << 24) | ((uint)header[1] << 16) |
                                  ((uint)header[2] << 8) | header[3]);

                // atom 类型是 4 字节，其中 '©' 是 0xA9，必须按 Latin-1 解而不是 ASCII。
                var type = Encoding.Latin1.GetString(header, 4, 4);
                long headerSize = 8;

                if (size == 1)
                {
                    var extended = ReadBytes(stream, 8);
                    if (extended.Length != 8) yield break;

                    size = 0;
                    foreach (var b in extended) size = (size << 8) | b;
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    size = end - position;      // 延伸到末尾
                }

                if (size < headerSize || position + size > end) yield break;

                yield return new Atom(type, position + headerSize, size - headerSize);
                position += size;
            }
        }

        private static IEnumerable<Atom> FindAtoms(Stream stream, Atom parent, string type, int leadingBytes = 0)
        {
            var start = parent.DataStart + leadingBytes;
            if (start >= parent.End) yield break;

            foreach (var child in EnumerateAtoms(stream, start, parent.End))
                if (child.Type == type) yield return child;
        }

        private readonly struct Atom
        {
            public Atom(string type, long dataStart, long dataLength)
            {
                Type = type;
                DataStart = dataStart;
                DataLength = dataLength;
            }

            public string Type { get; }
            public long DataStart { get; }
            public long DataLength { get; }
            public long End => DataStart + DataLength;
        }

        // ---- ID3v1 ------------------------------------------------------------

        /// <summary>最后 128 字节的 ID3v1 标签；只在 ID3v2 什么都读不到时兜底。</summary>
        private static void ReadId3v1(Stream stream, MediaTags tags)
        {
            if (stream.Length < 128) return;

            stream.Position = stream.Length - 128;
            var data = ReadBytes(stream, 128);
            if (data.Length < 128) return;
            if (data[0] != 'T' || data[1] != 'A' || data[2] != 'G') return;

            tags.Title ??= Id3v1Field(data, 3, 30);
            tags.Artist ??= Id3v1Field(data, 33, 30);
            tags.Album ??= Id3v1Field(data, 63, 30);
            tags.Year ??= Id3v1Field(data, 93, 4);

            // ID3v1.1 把备注的最后一字节当作音轨号
            if (data[125] == 0 && data[126] != 0)
            {
                tags.Comment ??= Id3v1Field(data, 97, 28);
                tags.TrackNumber ??= data[126].ToString();
            }
            else
            {
                tags.Comment ??= Id3v1Field(data, 97, 30);
            }
        }

        private static string? Id3v1Field(byte[] data, int offset, int length)
        {
            var end = offset;
            while (end < offset + length && data[end] != 0) end++;

            var text = Decode(data.AsSpan(offset, end - offset), 0).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        // ---- 文本解码 ---------------------------------------------------------

        /// <summary>
        /// 按 ID3v2 的编码字节解码正文。
        /// <para>
        /// 0 = ISO-8859-1（但中文标签实际几乎都是 GBK，见 <see cref="DecodeLegacy"/>）、
        /// 1 = 带 BOM 的 UTF-16、2 = 无 BOM 的 UTF-16BE、3 = UTF-8。
        /// </para>
        /// </summary>
        private static string Decode(ReadOnlySpan<byte> data, byte encoding)
        {
            if (data.Length == 0) return string.Empty;

            switch (encoding)
            {
                case 0:
                    return DecodeLegacy(data);
                case 1:
                    return DecodeUtf16(data, withBom: true);
                case 2:
                    return DecodeUtf16(data, withBom: false);
                case 3:
                    return Encoding.UTF8.GetString(data).TrimEnd('\0');
                default:
                    return DecodeLegacy(data);
            }
        }

        /// <summary>
        /// 解码声明为 ISO-8859-1 的文本。
        /// <para>
        /// 大量中文音乐文件把 GBK 字节写成编码 0，所以交给 <see cref="LegacyText"/>
        /// 按 GBK 优先、Latin-1 兜底去猜。
        /// </para>
        /// </summary>
        private static string DecodeLegacy(ReadOnlySpan<byte> data) => LegacyText.DecodeLegacy(data);

        private static string DecodeUtf16(ReadOnlySpan<byte> data, bool withBom)
        {
            if (data.Length < 2) return string.Empty;

            ReadOnlySpan<byte> body;
            Encoding encoding;

            if (withBom)
            {
                if (data[0] == 0xFF && data[1] == 0xFE)
                {
                    body = data[2..];
                    encoding = Encoding.Unicode;
                }
                else if (data[0] == 0xFE && data[1] == 0xFF)
                {
                    body = data[2..];
                    encoding = Encoding.BigEndianUnicode;
                }
                else
                {
                    body = data;
                    encoding = Encoding.Unicode;    // 没写 BOM 就按小端猜
                }
            }
            else
            {
                body = data;
                encoding = Encoding.BigEndianUnicode;
            }

            if (body.Length % 2 != 0) body = body[..^1];
            return encoding.GetString(body).TrimEnd('\0');
        }

        // ---- 小工具 -----------------------------------------------------------

        /// <summary>找一个以 0 结尾的字符串的结束位置（UTF-16 按双字节对齐找）。</summary>
        private static int FindTerminator(ReadOnlySpan<byte> data, int start, byte encoding)
        {
            if (encoding is 1 or 2)
            {
                for (var i = start; i + 1 < data.Length; i += 2)
                    if (data[i] == 0 && data[i + 1] == 0) return i;
            }
            else
            {
                for (var i = start; i < data.Length; i++)
                    if (data[i] == 0) return i;
            }

            return data.Length;
        }

        /// <summary>结尾符占几个字节。</summary>
        private static int TerminatorLength(byte encoding) => encoding is 1 or 2 ? 2 : 1;

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

        private static bool IsLyricsKey(string description) =>
            NormalizeKey(description) is "LYRICS" or "LYRIC" or "UNSYNCEDLYRICS" or "SYNCEDLYRICS" or "LYRICSLRC";

        private static string NormalizeKey(string key)
        {
            var builder = new StringBuilder(key.Length);
            foreach (var c in key)
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));

            return builder.ToString();
        }

        /// <summary>把 ID3v2.3 的 <c>"(17)Rock"</c> 这类写法还原成 <c>"Rock"</c>。</summary>
        private static string? NormalizeGenre(string? genre)
        {
            if (string.IsNullOrWhiteSpace(genre)) return genre;

            if (genre[0] == '(')
            {
                var close = genre.IndexOf(')');
                if (close > 0 && close + 1 < genre.Length) return genre[(close + 1)..].Trim();
            }

            return genre.Trim();
        }

        /// <summary>TDRC 之类是完整时间戳，只保留年份。</summary>
        private static string? NormalizeYear(string? year)
        {
            if (string.IsNullOrWhiteSpace(year)) return year;

            var trimmed = year.Trim();
            if (trimmed.Length >= 4 && char.IsDigit(trimmed[0]) && char.IsDigit(trimmed[1]) &&
                char.IsDigit(trimmed[2]) && char.IsDigit(trimmed[3]))
                return trimmed[..4];

            return trimmed;
        }

        private static string GuessMimeType(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
            if (data.Length >= 3 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "image/gif";
            if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return "image/bmp";
            return "image/jpeg";
        }

        private static byte[] ReadBytes(Stream stream, int count)
        {
            if (count <= 0) return Array.Empty<byte>();

            var buffer = new byte[count];
            var read = 0;

            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0) break;
                read += n;
            }

            if (read == count) return buffer;

            var partial = new byte[read];
            Array.Copy(buffer, partial, read);
            return partial;
        }

        private static bool Skip(Stream stream, long count)
        {
            if (count <= 0) return true;

            if (stream.CanSeek)
            {
                if (stream.Position + count > stream.Length)
                {
                    stream.Position = stream.Length;
                    return false;
                }

                stream.Position += count;
                return true;
            }

            var buffer = new byte[8192];
            while (count > 0)
            {
                var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (n <= 0) return false;
                count -= n;
            }

            return true;
        }

        private static bool TryReadUInt32LittleEndian(byte[] data, ref int offset, out uint value)
        {
            value = 0;
            if (offset + 4 > data.Length) return false;

            value = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            offset += 4;
            return true;
        }

        private static bool TryReadUInt32BigEndian(byte[] data, ref int offset, out uint value)
        {
            value = 0;
            if (offset + 4 > data.Length) return false;

            value = ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                    ((uint)data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            return true;
        }

        private static bool TryAdvance(byte[] data, ref int offset, int count)
        {
            if (count < 0 || offset + count > data.Length) return false;

            offset += count;
            return true;
        }
    }
}
