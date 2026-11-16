using System;
using System.IO;
using System.Text;

// ASF（.wma / .asf / .wmv）的标签：头对象里的 Content Description 与 Extended Content Description。
//
// 为什么自己解析：libvlc 的 Media.Meta() 没有歌词、也不给内嵌封面；而 ASF 的标签结构是
// 公开且稳定的（GUID + 长度 + 负载），读它比"多挂一个解码器"便宜得多。
//
// 只认两个对象（其余一律跳过）：
//   · Content Description：标题 / 作者 / 版权 / 描述 / 分级（5 个 WORD 长度 + 5 段 UTF-16LE 文本）
//   · Extended Content Description：WM/AlbumTitle、WM/AlbumArtist、WM/Year、WM/TrackNumber、
//     WM/Genre、WM/Lyrics、WM/Picture（封面）等
// 全部只读；任何长度撒谎 / 截断都安静停在原地（标签读不出来不影响播放）。

namespace 播放器.Core
{
    internal static class AsfTagReader
    {
        private const int HeaderFixedBytes = 30;
        private const int ObjectHeaderBytes = 24;
        private const int MaxHeaderBytes = 16 * 1024 * 1024;
        private const int MaxObjects = 64;
        private const int MaxEntries = 4096;
        private const int MaxStringBytes = 1024 * 1024;

        /// <summary>ASF Header Object：75B22630-668E-11CF-A6D9-00AA0062CE6C。</summary>
        private static readonly byte[] HeaderObjectGuid =
            { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };

        /// <summary>Content Description Object：75B22633-668E-11CF-A6D9-00AA0062CE6C。</summary>
        private static readonly byte[] ContentDescriptionGuid =
            { 0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };

        /// <summary>Extended Content Description Object：D2D0A440-E307-11D2-97F0-00A0C95EA850。</summary>
        private static readonly byte[] ExtendedContentDescriptionGuid =
            { 0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11, 0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50 };

        /// <summary>文件开头是不是 ASF 头对象（不改变流位置）。</summary>
        internal static bool LooksLikeAsf(Stream stream)
        {
            if (!stream.CanSeek) return false;

            var position = stream.Position;

            stream.Position = 0;
            var head = TagBytes.ReadBytes(stream, 16);
            stream.Position = position;

            return StartsWith(head, HeaderObjectGuid);
        }

        /// <summary>读 ASF 头对象里的标签；不是 ASF / 结构坏了就安静返回。</summary>
        internal static void ReadAsfTags(Stream stream, MediaTags tags)
        {
            stream.Position = 0;

            var header = TagBytes.ReadBytes(stream, HeaderFixedBytes);

            if (!StartsWith(header, HeaderObjectGuid) || header.Length < HeaderFixedBytes) return;

            var headerSize = (long)ReadUInt64LittleEndian(header, 16);
            var objectCount = (int)ReadUInt32LittleEndian(header, 24);

            // 头部大小与对象数都是文件自己写的：先夹住，坏文件不能把标签队列拖住
            if (headerSize < HeaderFixedBytes || headerSize > MaxHeaderBytes) return;

            var payload = TagBytes.ReadBytes(stream, (int)(headerSize - HeaderFixedBytes));

            ParseObjects(payload, objectCount, tags);
        }

        private static void ParseObjects(byte[] payload, int objectCount, MediaTags tags)
        {
            var offset = 0;
            var count = Math.Clamp(objectCount, 0, MaxObjects);
            var seen = 0;

            while (seen < count && offset + ObjectHeaderBytes <= payload.Length)
            {
                var guid = new byte[16];
                Array.Copy(payload, offset, guid, 0, 16);

                var size = (long)ReadUInt64LittleEndian(payload, offset + 16);

                // 对象长度必须装得下"头 + 负载"，否则停（后面再也对不齐）
                if (size < ObjectHeaderBytes || size > payload.Length - offset) return;

                var dataOffset = offset + ObjectHeaderBytes;
                var dataLength = (int)size - ObjectHeaderBytes;

                if (StartsWith(guid, ContentDescriptionGuid))
                {
                    ReadContentDescription(payload, dataOffset, dataLength, tags);
                }
                else if (StartsWith(guid, ExtendedContentDescriptionGuid))
                {
                    ReadExtendedContentDescription(payload, dataOffset, dataLength, tags);
                }

                offset += (int)size;
                seen++;
            }
        }

        /// <summary>
        /// Content Description：5 个 WORD 长度（标题 / 作者 / 版权 / 描述 / 分级），
        /// 后面依次是 5 段 UTF-16LE 文本（长度是字节数，不带结尾符）。
        /// </summary>
        private static void ReadContentDescription(byte[] data, int start, int length, MediaTags tags)
        {
            if (length < 10) return;

            var end = start + length;
            var lengths = new int[5];

            for (var i = 0; i < 5; i++) lengths[i] = ReadUInt16LittleEndian(data, start + i * 2);

            var offset = start + 10;

            string ReadField(int byteLength)
            {
                if (byteLength <= 0) return string.Empty;

                // UTF-16：奇数长度说明文件坏了，丢掉最后那半个字符
                var usable = byteLength & ~1;

                if (offset + usable > end)
                {
                    offset = end;
                    return string.Empty;
                }

                var text = Encoding.Unicode.GetString(data, offset, usable).TrimEnd('\0');
                offset += byteLength;
                return text;
            }

            var title = ReadField(lengths[0]);
            var author = ReadField(lengths[1]);
            var description = ReadField(lengths[3]);

            if (!string.IsNullOrWhiteSpace(title)) tags.Title ??= title;
            if (!string.IsNullOrWhiteSpace(author)) tags.Artist ??= author;
            if (!string.IsNullOrWhiteSpace(description)) tags.Comment ??= description;
        }

        /// <summary>
        /// Extended Content Description：count × (名长 / 名 / 值类型 / 值长 / 值)，
        /// 名字与字符串值都是 UTF-16LE（名字常带 NUL 结尾）。
        /// </summary>
        private static void ReadExtendedContentDescription(byte[] data, int start, int length, MediaTags tags)
        {
            if (length < 2) return;

            var end = start + length;
            var count = Math.Clamp((int)ReadUInt16LittleEndian(data, start), 0, MaxEntries);
            var offset = start + 2;

            for (var i = 0; i < count; i++)
            {
                if (offset + 2 > end) return;

                var nameLength = ReadUInt16LittleEndian(data, offset);
                offset += 2;

                if (nameLength <= 0 || nameLength > MaxStringBytes || offset + nameLength + 4 > end) return;

                var name = Encoding.Unicode.GetString(data, offset, nameLength & ~1).TrimEnd('\0');
                offset += nameLength;

                var valueType = ReadUInt16LittleEndian(data, offset);
                offset += 2;

                var valueLength = ReadUInt16LittleEndian(data, offset);
                offset += 2;

                if (valueLength > MaxStringBytes || offset + valueLength > end) return;

                ApplyField(name, valueType, data, offset, valueLength, tags);

                offset += valueLength;
            }
        }

        private static void ApplyField(string name, int valueType, byte[] data, int offset, int length, MediaTags tags)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            var key = name.Trim();

            switch (key)
            {
                case "WM/Title":
                    tags.Title ??= ReadText(valueType, data, offset, length);
                    break;
                case "WM/AlbumTitle":
                    tags.Album ??= ReadText(valueType, data, offset, length);
                    break;
                case "WM/AlbumArtist":
                    tags.AlbumArtist ??= ReadText(valueType, data, offset, length);
                    break;
                case "WM/Year":
                    tags.Year ??= TagText.NormalizeYear(ReadText(valueType, data, offset, length));
                    break;
                case "WM/TrackNumber":
                    tags.TrackNumber ??= ReadText(valueType, data, offset, length);
                    break;
                case "WM/Genre":
                    tags.Genre ??= ReadText(valueType, data, offset, length);
                    break;
                case "WM/Lyrics":
                    tags.Lyrics ??= ReadText(valueType, data, offset, length);
                    break;
                case "WM/Picture":
                    if (valueType == 1) ReadPicture(data, offset, length, tags);
                    break;
            }
        }

        /// <summary>按值类型取文本：0=Unicode、2=BOOL、3=DWORD、4=QWORD、5=WORD。</summary>
        private static string? ReadText(int valueType, byte[] data, int offset, int length)
        {
            var text = valueType switch
            {
                0 => Encoding.Unicode.GetString(data, offset, length & ~1).TrimEnd('\0'),
                2 => length >= 4 ? (ReadUInt32LittleEndian(data, offset) != 0 ? "1" : "0") : null,
                3 => length >= 4 ? ReadUInt32LittleEndian(data, offset).ToString() : null,
                4 => length >= 8 ? ReadUInt64LittleEndian(data, offset).ToString() : null,
                5 => length >= 2 ? ReadUInt16LittleEndian(data, offset).ToString() : null,
                _ => null
            };

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        /// <summary>
        /// WM/Picture：类型(1) + 数据长(4 LE) + MIME(UTF-16, NUL 结尾) + 描述(UTF-16, NUL 结尾) + 数据。
        /// 图片太大 / 数据越界只丢封面，别的字段照读。
        /// </summary>
        private static void ReadPicture(byte[] data, int start, int length, MediaTags tags)
        {
            if (length < 5) return;

            var end = start + length;
            var pictureType = data[start];
            var dataLength = (long)ReadUInt32LittleEndian(data, start + 1);
            var offset = start + 5;

            var mimeEnd = FindUtf16Terminator(data, offset, end);
            if (mimeEnd < 0) return;

            var mime = Encoding.Unicode.GetString(data, offset, mimeEnd - offset).TrimEnd('\0');
            offset = mimeEnd + 2;

            var descriptionEnd = FindUtf16Terminator(data, offset, end);
            if (descriptionEnd < 0) return;

            offset = descriptionEnd + 2;

            if (dataLength <= 0 || dataLength > TagReader.MaxCoverBytes || offset + dataLength > end) return;

            // 已经有封面时，只让"正面封面（3）"顶掉它
            if (tags.CoverArt != null && pictureType != 3) return;

            tags.CoverArt = new byte[(int)dataLength];
            Array.Copy(data, offset, tags.CoverArt, 0, (int)dataLength);
            tags.CoverMimeType = string.IsNullOrWhiteSpace(mime)
                ? TagText.GuessMimeType(tags.CoverArt)
                : mime;
        }

        // ---- 小工具 -----------------------------------------------------------

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;

            for (var i = 0; i < prefix.Length; i++)
                if (data[i] != prefix[i]) return false;

            return true;
        }

        /// <summary>UTF-16LE 的结尾 00 00，返回 00 的位置；没有返回 <c>-1</c>。</summary>
        private static int FindUtf16Terminator(byte[] data, int start, int end)
        {
            for (var i = start; i + 1 < end; i += 2)
                if (data[i] == 0 && data[i + 1] == 0) return i;

            return -1;
        }

        private static int ReadUInt16LittleEndian(byte[] data, int offset) =>
            data[offset] | (data[offset + 1] << 8);

        private static uint ReadUInt32LittleEndian(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        private static ulong ReadUInt64LittleEndian(byte[] data, int offset)
        {
            ulong value = 0;

            for (var i = 7; i >= 0; i--) value = (value << 8) | data[offset + i];

            return value;
        }
    }
}
