using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// ID3v1 兜底标签（128 字节尾巴）。

namespace 播放器.Core
{
    internal static class Id3v1TagReader
    {
        // ---- ID3v1 ------------------------------------------------------------

        /// <summary>最后 128 字节的 ID3v1 标签；只在 ID3v2 什么都读不到时兜底。</summary>
        internal static void ReadId3v1(Stream stream, MediaTags tags)
        {
            if (stream.Length < 128) return;

            stream.Position = stream.Length - 128;
            var data = TagBytes.ReadBytes(stream, 128);
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

            var text = TagText.Decode(data.AsSpan(offset, end - offset), 0).Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
    }
}
