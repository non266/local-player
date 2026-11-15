using System;
using System.IO;
using System.Text;

// APEv2 / APEv1 标签：文件尾部 32 字节的 "APETAGEX" footer 描述整块标签。
//
// 布局（footer 与可选的 header 各 32 字节，全都小端序）：
//   "APETAGEX" 8 | 版本 4 | 标签总长 4（含 footer、不含 header）| 条目数 4 | 标志 4 | 保留 8
// 条目：值长 4 | 标志 4 | 键（ASCII，0 结尾）| 值
//
// 文本键与 Vorbis comment 的键名是同一套写法（Title / Artist / Year / Track…），所以文本字段
// 直接交给 <see cref="FlacTagReader.ApplyVorbisField"/> 归一，不另写一份映射——
// 两份结构一样的解析器迟早会慢慢漂开。
// 封面是二进制条目 "Cover Art (Front)"：值是「文件名 + 0 + 图片字节」。

namespace 播放器.Core
{
    internal static class ApeTagReader
    {
        private const int FooterSize = 32;
        private const int MaxItemCount = 4096;
        private const int MaxTagBytes = 16 * 1024 * 1024;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("APETAGEX");

        /// <summary>文件尾是不是 APE 标签（不移动调用方关心的读取位置，方法自己恢复）。</summary>
        internal static bool LooksLikeApeTag(Stream stream)
        {
            if (!stream.CanSeek || stream.Length < FooterSize) return false;

            var position = stream.Position;
            try
            {
                stream.Position = stream.Length - FooterSize;
                var magic = TagBytes.ReadBytes(stream, Magic.Length);
                return magic.Length == Magic.Length && IsMagic(magic);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                stream.Position = position;
            }
        }

        /// <summary>读尾部 APE 标签；没有标签、或结构坏了就安静返回（不影响播放）。</summary>
        internal static void ReadApeTag(Stream stream, MediaTags tags)
        {
            if (!stream.CanSeek || stream.Length < FooterSize) return;

            var footer = new byte[FooterSize];
            stream.Position = stream.Length - FooterSize;
            if (!ReadExactly(stream, footer, 0, FooterSize)) return;
            if (!IsMagic(footer.AsSpan(0, Magic.Length))) return;

            var tagSize = ReadUInt32LittleEndian(footer, 12);
            var itemCount = ReadUInt32LittleEndian(footer, 16);

            if (tagSize < FooterSize || tagSize > MaxTagBytes) return;
            if (itemCount > MaxItemCount) itemCount = MaxItemCount;

            var itemsLength = (int)tagSize - FooterSize;
            if (itemsLength <= 0 || itemCount == 0) return;

            var itemsStart = stream.Length - tagSize;       // 标签总长含 footer，所以起点在这里
            if (itemsStart < 0) return;

            stream.Position = itemsStart;
            var region = TagBytes.ReadBytes(stream, itemsLength);
            if (region.Length < itemsLength) return;

            var offset = 0;

            for (var i = 0; i < itemCount; i++)
            {
                if (!TagBytes.TryReadUInt32LittleEndian(region, ref offset, out var valueLength)) return;
                if (!TagBytes.TryReadUInt32LittleEndian(region, ref offset, out _)) return;   // 条目标志（只读 / 二进制）这里用不到

                var key = ReadKey(region, ref offset);
                if (key == null) return;

                if (valueLength > MaxTagBytes || offset + (int)valueLength > region.Length) return;

                var value = region.AsSpan(offset, (int)valueLength);
                offset += (int)valueLength;

                Apply(key, value, tags);
            }
        }

        private static void Apply(string key, ReadOnlySpan<byte> value, MediaTags tags)
        {
            var normalized = TagText.NormalizeKey(key);

            // 封面是二进制条目：值 = 文件名 + 0 + 图片字节
            if (normalized == "COVERARTFRONT" || normalized == "COVERART")
            {
                ApplyCover(value, tags);
                return;
            }

            if (value.Length == 0) return;

            var text = Encoding.UTF8.GetString(value).TrimEnd('\0').TrimStart('\uFEFF');
            if (text.Length == 0) return;

            FlacTagReader.ApplyVorbisField(key, text, tags);
        }

        private static void ApplyCover(ReadOnlySpan<byte> value, MediaTags tags)
        {
            // "文件名\0图片"：文件名是给人看的，解析用不到，跳到第一个 0 之后。
            var separator = value.IndexOf((byte)0);
            if (separator < 0 || separator + 1 >= value.Length) return;

            var image = value[(separator + 1)..];
            if (image.Length == 0 || image.Length > TagReader.MaxCoverBytes) return;

            tags.CoverArt = image.ToArray();
            tags.CoverMimeType = TagText.GuessMimeType(image);
        }

        /// <summary>键名是 ASCII、以 0 结尾；太长的键直接判坏数据（正常键不会超过几十字节）。</summary>
        private static string? ReadKey(byte[] region, ref int offset)
        {
            var start = offset;

            while (offset < region.Length && region[offset] != 0)
            {
                offset++;
                if (offset - start > 1024) return null;
            }

            if (offset >= region.Length) return null;       // 没有结尾 0

            var key = Encoding.ASCII.GetString(region, start, offset - start);
            offset++;                                       // 跳过 0
            return key;
        }

        private static bool IsMagic(ReadOnlySpan<byte> data)
        {
            if (data.Length < Magic.Length) return false;

            for (var i = 0; i < Magic.Length; i++)
                if (data[i] != Magic[i]) return false;

            return true;
        }

        private static uint ReadUInt32LittleEndian(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
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
