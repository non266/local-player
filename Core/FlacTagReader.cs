using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// FLAC：STREAMINFO、VORBIS_COMMENT、PICTURE。

namespace 播放器.Core
{
    internal static class FlacTagReader
    {
        // ---- FLAC -------------------------------------------------------------

        /// <summary>
        /// 解析 FLAC 的元数据块。
        /// <para>
        /// 进入时流的位置必须是 <c>fLaC</c> 标记之后（4 字节处），
        /// 即紧邻第一个元数据块头。
        /// </para>
        /// </summary>
        internal static void ReadFlacBlocks(Stream stream, MediaTags tags)
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
                        ReadFlacStreamInfo(TagBytes.ReadBytes(stream, length), tags);
                        break;
                    case 4:     // VORBIS_COMMENT
                        ReadVorbisComment(TagBytes.ReadBytes(stream, length), tags);
                        break;
                    case 6:     // PICTURE
                        ReadFlacPicture(TagBytes.ReadBytes(stream, length), tags);
                        break;
                    default:
                        TagBytes.Skip(stream, length);
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

            if (!TagBytes.TryReadUInt32LittleEndian(data, ref offset, out var vendorLength)) return;
            if (!TagBytes.TryAdvance(data, ref offset, (int)vendorLength)) return;
            if (!TagBytes.TryReadUInt32LittleEndian(data, ref offset, out var count)) return;

            count = Math.Min(count, TagReader.MaxVorbisComments);

            for (uint i = 0; i < count; i++)
            {
                if (!TagBytes.TryReadUInt32LittleEndian(data, ref offset, out var length)) return;
                if (!TagBytes.TryAdvance(data, ref offset, (int)length)) return;

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
            var normalized = TagText.NormalizeKey(key);

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
                    tags.Year ??= TagText.NormalizeYear(value);
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

            if (!TagBytes.TryReadUInt32BigEndian(data, ref offset, out var pictureType)) return;
            if (!TagBytes.TryReadUInt32BigEndian(data, ref offset, out var mimeLength)) return;
            if (!TagBytes.TryAdvance(data, ref offset, (int)mimeLength)) return;

            var mime = Encoding.ASCII.GetString(data, offset - (int)mimeLength, (int)mimeLength);

            if (!TagBytes.TryReadUInt32BigEndian(data, ref offset, out var descriptionLength)) return;
            if (!TagBytes.TryAdvance(data, ref offset, (int)descriptionLength)) return;

            var description = Encoding.UTF8.GetString(data, offset - (int)descriptionLength, (int)descriptionLength);

            // 宽、高、色深、索引色数，各 4 字节
            for (var i = 0; i < 4; i++)
                if (!TagBytes.TryReadUInt32BigEndian(data, ref offset, out _)) return;

            if (!TagBytes.TryReadUInt32BigEndian(data, ref offset, out var pictureLength)) return;
            if (!TagBytes.TryAdvance(data, ref offset, (int)pictureLength)) return;

            if (pictureLength == 0 || pictureLength > TagReader.MaxCoverBytes) return;

            if (tags.CoverArt == null || pictureType == 3)
            {
                tags.CoverArt = new byte[(int)pictureLength];
                Array.Copy(data, offset - (int)pictureLength, tags.CoverArt, 0, (int)pictureLength);
                tags.CoverMimeType = string.IsNullOrWhiteSpace(mime) ? TagText.GuessMimeType(tags.CoverArt) : mime;
            }
        }
    }
}
