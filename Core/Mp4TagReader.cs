using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// MP4 / M4A 的 ilst 原子：文本、©lyr、covr、trkn 与原子遍历。

namespace 播放器.Core
{
    internal static class Mp4TagReader
    {
        // ---- MP4 / M4A --------------------------------------------------------

        /// <summary>MP4 是嵌套的 atom 树，走 moov → udta → meta → ilst。</summary>
        internal static void ReadMp4(Stream stream, MediaTags tags)
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
                    var head = TagBytes.ReadBytes(stream, 8);
                    if (head.Length < 8) break;

                    var dataType = ((uint)head[0] << 24) | ((uint)head[1] << 16) | ((uint)head[2] << 8) | head[3];
                    var payloadLength = (int)Math.Min(dataAtom.DataLength - 8, TagReader.MaxCoverBytes);

                    stream.Position = dataAtom.DataStart + 8;
                    var payload = TagBytes.ReadBytes(stream, payloadLength);

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
                    tags.Year ??= TagText.NormalizeYear(Utf8(payload));
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
                        _ => TagText.GuessMimeType(payload)
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
                var header = TagBytes.ReadBytes(stream, 8);
                if (header.Length != 8) yield break;

                var size = (long)(((uint)header[0] << 24) | ((uint)header[1] << 16) |
                                  ((uint)header[2] << 8) | header[3]);

                // atom 类型是 4 字节，其中 '©' 是 0xA9，必须按 Latin-1 解而不是 ASCII。
                var type = Encoding.Latin1.GetString(header, 4, 4);
                long headerSize = 8;

                if (size == 1)
                {
                    var extended = TagBytes.ReadBytes(stream, 8);
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
    }
}
