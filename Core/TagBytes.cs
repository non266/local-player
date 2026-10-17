using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// 字节读写小工具：ReadBytes / Skip / TryReadUInt32… / TryAdvance（四种格式共用）。

namespace 播放器.Core
{
    internal static class TagBytes
    {
        internal static byte[] ReadBytes(Stream stream, int count)
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

        internal static bool Skip(Stream stream, long count)
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

        internal static bool TryReadUInt32LittleEndian(byte[] data, ref int offset, out uint value)
        {
            value = 0;
            if (offset + 4 > data.Length) return false;

            value = (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            offset += 4;
            return true;
        }

        internal static bool TryReadUInt32BigEndian(byte[] data, ref int offset, out uint value)
        {
            value = 0;
            if (offset + 4 > data.Length) return false;

            value = ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                    ((uint)data[offset + 2] << 8) | data[offset + 3];
            offset += 4;
            return true;
        }

        internal static bool TryAdvance(byte[] data, ref int offset, int count)
        {
            if (count < 0 || offset + count > data.Length) return false;

            offset += count;
            return true;
        }
    }
}
