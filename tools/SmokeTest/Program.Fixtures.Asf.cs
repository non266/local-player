// 合成样本：ASF（.wma / .asf / .wmv）的标签头。
//
// 手写字节而不是调 ffmpeg：这里要能稳定摆出"头长度撒谎""WM/Picture 数据长度离谱"这种坏形状，
// 真实文件反而造不出来。真实文件那一段是 GATED 的（播放器_TEST_TAGPROBE=1），另走 ffmpeg。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static readonly byte[] AsfHeaderGuid =
            { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };

        private static readonly byte[] AsfContentDescriptionGuid =
            { 0x33, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };

        private static readonly byte[] AsfExtendedContentDescriptionGuid =
            { 0x40, 0xA4, 0xD0, 0xD2, 0x07, 0xE3, 0xD2, 0x11, 0x97, 0xF0, 0x00, 0xA0, 0xC9, 0x5E, 0xA8, 0x50 };

        /// <summary>
        /// ASF 文件：头对象（Content Description + Extended Content Description）+ 一段假音频数据。
        /// <paramref name="badPictureLength"/> 非空时把 WM/Picture 的数据长度写成那个值（验"只丢封面"）。
        /// </summary>
        private static byte[] BuildAsfFile(
            string title, string artist, string comment, string album, string year, int? trackNumber,
            string genre, string lyrics, byte[] cover, int? badPictureLength = null, bool lieAboutObjectSize = false)
        {
            var content = BuildAsfContentDescription(title, artist, comment);

            var entries = new List<byte[]>();

            void AddText(string name, string value)
            {
                if (value == null) return;
                entries.Add(AsfEntry(name, 0, Utf16(value)));
            }

            AddText("WM/AlbumTitle", album);
            AddText("WM/Year", year);
            AddText("WM/Genre", genre);
            AddText("WM/Lyrics", lyrics);

            if (trackNumber is int track)
            {
                var value = new byte[4];
                BitConverter.GetBytes(track).CopyTo(value, 0);
                entries.Add(AsfEntry("WM/TrackNumber", 3, value));
            }

            if (cover != null)
            {
                entries.Add(AsfEntry("WM/Picture", 1, AsfPicture(cover, badPictureLength)));
            }

            var extended = Concat(entries.ToArray());

            using var extendedStream = new MemoryStream();
            extendedStream.Write(BitConverter.GetBytes((ushort)entries.Count));
            extendedStream.Write(extended);

            var objects = Concat(
                AsfObject(AsfContentDescriptionGuid, content),
                AsfObject(AsfExtendedContentDescriptionGuid, extendedStream.ToArray()));

            if (lieAboutObjectSize && objects.Length >= 32)
            {
                // 第一个对象头里的长度写成"比实际大很多"：解析器必须停，而不是照着读到文件尾
                var lie = BitConverter.GetBytes((ulong)objects.Length + 100_000);
                lie.CopyTo(objects, 16);
            }

            using var output = new MemoryStream();

            output.Write(AsfHeaderGuid);
            output.Write(BitConverter.GetBytes((ulong)(30 + objects.Length)));   // 头对象总长
            output.Write(BitConverter.GetBytes(2));                             // 头对象个数
            output.WriteByte(0x01);
            output.WriteByte(0x02);
            output.Write(objects);

            // 后面的音频数据（解析器不该碰它）
            output.Write(new byte[1024]);

            return output.ToArray();
        }

        /// <summary>Content Description：5 个 WORD 长度 + 5 段 UTF-16LE（标题 / 作者 / 版权 / 描述 / 分级）。</summary>
        private static byte[] BuildAsfContentDescription(string title, string artist, string comment)
        {
            var fields = new[]
            {
                Utf16(title ?? string.Empty),
                Utf16(artist ?? string.Empty),
                Array.Empty<byte>(),
                Utf16(comment ?? string.Empty),
                Array.Empty<byte>()
            };

            using var stream = new MemoryStream();

            foreach (var field in fields) stream.Write(BitConverter.GetBytes((ushort)field.Length));
            foreach (var field in fields) stream.Write(field);

            return stream.ToArray();
        }

        private static byte[] AsfEntry(string name, int valueType, byte[] value)
        {
            using var stream = new MemoryStream();

            stream.Write(BitConverter.GetBytes((ushort)Utf16(name).Length));
            stream.Write(Utf16(name));
            stream.Write(BitConverter.GetBytes((ushort)valueType));
            stream.Write(BitConverter.GetBytes((ushort)value.Length));
            stream.Write(value);

            return stream.ToArray();
        }

        private static byte[] AsfObject(byte[] guid, byte[] payload)
        {
            using var stream = new MemoryStream();

            stream.Write(guid);
            stream.Write(BitConverter.GetBytes((ulong)(24 + payload.Length)));
            stream.Write(payload);

            return stream.ToArray();
        }

        /// <summary>WM/Picture：类型(1) + 数据长(4 LE) + MIME / 描述（UTF-16，NUL 结尾）+ 图片数据。</summary>
        private static byte[] AsfPicture(byte[] image, int? badLength)
        {
            using var stream = new MemoryStream();

            stream.WriteByte(3);                                                    // 正面封面
            stream.Write(BitConverter.GetBytes((uint)(badLength ?? image.Length)));
            stream.Write(Utf16("image/png"));
            stream.Write(new byte[2]);                                              // MIME 的 NUL
            stream.Write(Utf16(string.Empty));
            stream.Write(new byte[2]);                                              // 描述的 NUL
            stream.Write(image);

            return stream.ToArray();
        }

        private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);
    }
}
