using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Ogg：Vorbis / Opus / Speex / FLAC / Theora 的评论包。
//
// Ogg 的文件结构是"页"串起来的：每页一个 27 字节页头 + 一张段表（最多 255 段），
// 段长 < 255 表示一个包在这里结束，= 255 表示这个包还要接着下一页。
// 评论头（TITLE / ARTIST / 封面 / 歌词）永远在最前面那几个包里，所以这里只往下看有限页数。
//
// 评论包里面的内容与 FLAC 的 VORBIS_COMMENT 是同一套结构（小端序：厂商串、条数、
// 每条"长度 + UTF-8 文本"），所以解析直接复用 <see cref="FlacTagReader"/> 那一份，
// 不另写一套——两套结构一样的解析器迟早会慢慢漂开。
//
// 五种编码的"评论头"形状（1.23.0 补了后两种）：
//   · Vorbis：第 1 个包以 0x01 "vorbis" 开头，第 2 个包以 0x03 "vorbis" 开头，尾部带 framing bit
//   · Opus：第 1 个包 "OpusHead"，第 2 个包 "OpusTags"
//   · Speex：第 2 个包**没有前缀**，直接是 Vorbis comment
//   · FLAC（Ogg-FLAC）：第 1 个包以 0x7F "FLAC" 开头、尾部是 fLaC + STREAMINFO；
//     之后每个包是一个**带 4 字节块头**的 FLAC 元数据块（类型 4 = 评论、6 = 图片）
//   · Theora：第 2 个包以 0x81 "theora" 开头，去掉 7 字节后是 Vorbis comment

namespace 播放器.Core
{
    internal static class OggTagReader
    {
        private const int PageHeaderSize = 27;

        /// <summary>最多往下看多少页 / 多少字节。评论头在前面，坏文件不能把标签队列拖住。</summary>
        private const int MaxPages = 32;
        private const int MaxReadBytes = 2 * 1024 * 1024;

        /// <summary>单个包的上限（评论块理论上可以很大，但超过这个数基本是坏数据）。</summary>
        private const int MaxPacketBytes = 8 * 1024 * 1024;

        private static readonly byte[] VorbisHeaderPrefix = { 0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' };
        private static readonly byte[] VorbisCommentPrefix = { 0x03, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' };
        private static readonly byte[] OpusHead = Encoding.ASCII.GetBytes("OpusHead");
        private static readonly byte[] OpusTags = Encoding.ASCII.GetBytes("OpusTags");
        private static readonly byte[] SpeexHeader = Encoding.ASCII.GetBytes("Speex   ");
        private static readonly byte[] FlacHeaderPrefix = { 0x7F, (byte)'F', (byte)'L', (byte)'A', (byte)'C' };
        private static readonly byte[] TheoraHeaderPrefix = { 0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a' };
        private static readonly byte[] TheoraCommentPrefix = { 0x81, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a' };

        private enum Codec
        {
            Unknown,
            Vorbis,
            Opus,
            Speex,
            Flac,
            Theora
        }

        /// <summary>
        /// 从 Ogg 里读出标签。进入时流可以在任意位置（方法自己回到 0）；
        /// 不是 Ogg、或里面没有认得的评论头时安静返回（标签读不出来不能影响播放）。
        /// </summary>
        internal static void ReadOggTags(Stream stream, MediaTags tags)
        {
            stream.Position = 0;

            var codec = Codec.Unknown;
            var packetIndex = 0;                 // 已经拼完的包序号：0 = 编码头信息包
            var packet = new MemoryStream();
            var packetOpen = false;              // 当前包跨过了上一页的末尾
            var read = 0;
            var header = new byte[PageHeaderSize];

            for (var page = 0; page < MaxPages && read < MaxReadBytes; page++)
            {
                if (!ReadExactly(stream, header, 0, PageHeaderSize)) return;
                read += PageHeaderSize;

                if (header[0] != (byte)'O' || header[1] != (byte)'g' || header[2] != (byte)'g' || header[3] != (byte)'S') return;
                if (header[4] != 0) return;      // 不认识的页版本

                var type = header[5];
                var segmentCount = header[26];

                var lacing = new byte[segmentCount];
                if (!ReadExactly(stream, lacing, 0, segmentCount)) return;
                read += segmentCount;

                var bodyLength = 0;
                for (var i = 0; i < segmentCount; i++) bodyLength += lacing[i];

                // 单页数据最多 255 × 255 字节；超过这个数说明页头被写坏了。
                if (bodyLength < 0 || bodyLength > 255 * 255) return;

                var body = new byte[bodyLength];
                if (!ReadExactly(stream, body, 0, bodyLength)) return;
                read += bodyLength;

                // 页头说"这个包接着上一页"，但我们手上没有没拼完的包：这一页开头那半个包是残片，丢掉。
                var dropLeading = (type & 0x01) != 0 && !packetOpen;
                var offset = 0;

                for (var i = 0; i < segmentCount; i++)
                {
                    var length = lacing[i];

                    if (dropLeading)
                    {
                        offset += length;
                        if (length < 255) dropLeading = false;
                        continue;
                    }

                    if (packetOpen)
                    {
                        if (packet.Length + length > MaxPacketBytes) return;
                        packet.Write(body, offset, length);
                    }
                    else
                    {
                        packet.SetLength(0);
                        packet.Write(body, offset, length);
                        packetOpen = true;
                    }

                    offset += length;

                    if (length < 255)       // 包结束
                    {
                        var data = packet.ToArray();
                        packetOpen = false;
                        packet.SetLength(0);

                        if (HandlePacket(data, ref codec, ref packetIndex, tags)) return;
                    }
                }
            }
        }

        /// <summary>
        /// 处理一个拼完的包：第 0 个认编解码，其余按编码取评论头。返回 <c>true</c> 表示"不用再往下读了"。
        /// <para>
        /// 只有 Ogg-FLAC 会连着读多个包（评论块与图片块各一个包），其余编码读到评论包就停。
        /// </para>
        /// </summary>
        private static bool HandlePacket(byte[] data, ref Codec codec, ref int packetIndex, MediaTags tags)
        {
            if (packetIndex == 0)
            {
                codec = DetectCodec(data);

                if (codec == Codec.Unknown) return true;    // 认不出来就别猜

                packetIndex = 1;

                // Ogg-FLAC 的 STREAMINFO 藏在首包尾部（采样率 / 声道 / 位深 / 时长）
                if (codec == Codec.Flac) ReadOggFlacStreamInfo(data, tags);

                return false;
            }

            switch (codec)
            {
                case Codec.Vorbis:
                    if (!StartsWith(data, VorbisCommentPrefix)) return true;
                    FlacTagReader.ReadVorbisComment(data[VorbisCommentPrefix.Length..], tags);
                    return true;

                case Codec.Opus:
                    if (!StartsWith(data, OpusTags)) return true;
                    FlacTagReader.ReadVorbisComment(data[OpusTags.Length..], tags);
                    return true;

                case Codec.Speex:
                    // Speex 的评论包没有前缀，直接就是 Vorbis comment 结构（带厂商串、不带 framing bit）。
                    FlacTagReader.ReadVorbisComment(data, tags);
                    return true;

                case Codec.Theora:
                    // Theora 的评论包以 0x81 "theora" 开头（7 字节），后面是同一套 Vorbis comment。
                    if (!StartsWith(data, TheoraCommentPrefix)) return true;
                    FlacTagReader.ReadVorbisComment(data[TheoraCommentPrefix.Length..], tags);
                    return true;

                case Codec.Flac:
                    return !ReadOggFlacMetadataBlock(data, tags, out var isLast) || isLast;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Ogg-FLAC 的首包：0x7F "FLAC" + 主 / 次版本 + 头包数（2 字节大端）+ "fLaC" + STREAMINFO 块。
        /// 认得出来就把 STREAMINFO 交给 FLAC 那份解析（采样率 / 声道 / 位深 / 时长）。
        /// </summary>
        private static void ReadOggFlacStreamInfo(byte[] data, MediaTags tags)
        {
            // 5（0x7F + FLAC）+ 1 主版本 + 1 次版本 + 2 头包数 = 9，然后是 "fLaC"
            const int flacMarker = 9;

            if (data.Length < flacMarker + 4 + 4 + 34) return;

            if (data[flacMarker] != (byte)'f' || data[flacMarker + 1] != (byte)'L' ||
                data[flacMarker + 2] != (byte)'a' || data[flacMarker + 3] != (byte)'C')
                return;

            var block = flacMarker + 4;

            // 块头：1 字节（类型 + 最后一块标志）+ 3 字节长度（大端）
            var type = data[block] & 0x7F;
            var length = (data[block + 1] << 16) | (data[block + 2] << 8) | data[block + 3];

            if (type != 0 || length < 34 || block + 4 + 34 > data.Length) return;

            var streamInfo = new byte[34];
            Array.Copy(data, block + 4, streamInfo, 0, 34);

            FlacTagReader.ReadFlacStreamInfo(streamInfo, tags);
        }

        /// <summary>
        /// Ogg-FLAC 的元数据块包：4 字节块头（类型 + 最后一块标志 + 3 字节长度）+ 块数据。
        /// 类型 4 = Vorbis comment、6 = PICTURE，其余跳过。返回 <c>false</c> 表示"这不是一个元数据块"。
        /// </summary>
        private static bool ReadOggFlacMetadataBlock(byte[] data, MediaTags tags, out bool isLast)
        {
            isLast = false;

            if (data.Length < 4) return false;

            isLast = (data[0] & 0x80) != 0;

            var type = data[0] & 0x7F;
            var length = (data[1] << 16) | (data[2] << 8) | data[3];

            if (length < 0 || 4 + length > data.Length) return false;

            var block = new byte[length];
            Array.Copy(data, 4, block, 0, length);

            switch (type)
            {
                case 4:
                    FlacTagReader.ReadVorbisComment(block, tags);
                    break;
                case 6:
                    FlacTagReader.ReadFlacPicture(block, tags);
                    break;
            }

            return true;
        }

        private static Codec DetectCodec(byte[] data)
        {
            if (StartsWith(data, VorbisHeaderPrefix)) return Codec.Vorbis;
            if (StartsWith(data, OpusHead)) return Codec.Opus;
            if (StartsWith(data, SpeexHeader)) return Codec.Speex;
            if (StartsWith(data, FlacHeaderPrefix)) return Codec.Flac;
            if (StartsWith(data, TheoraHeaderPrefix)) return Codec.Theora;
            return Codec.Unknown;
        }

        private static bool StartsWith(byte[] data, byte[] prefix)
        {
            if (data.Length < prefix.Length) return false;

            for (var i = 0; i < prefix.Length; i++)
                if (data[i] != prefix[i]) return false;

            return true;
        }

        /// <summary>读满 <paramref name="count"/> 字节；文件提前结尾返回 <c>false</c>。</summary>
        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            if (count == 0) return true;

            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0) return false;
                read += n;
            }

            return true;
        }
    }
}
