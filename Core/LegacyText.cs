using System;
using System.Text;

namespace 播放器.Core
{
    /// <summary>
    /// 老式中文文本的编码处理。
    /// <para>
    /// 国内的 ID3v2 标签与 .lrc 歌词大量使用 GBK，却经常把编码字节写成
    /// ISO-8859-1 或者干脆不写 BOM，所以只能按字节结构去判断。
    /// GBK（代码页 936）由 <see cref="CodePagesEncodingProvider"/> 提供；
    /// 万一运行环境拿不到，就退回 Latin-1——它解码永不失败，只是中文会变成乱码，
    /// 总好过抛异常。
    /// </para>
    /// </summary>
    internal static class LegacyText
    {
        private static readonly object Gate = new object();
        private static bool _ready;
        private static Encoding _chinese = Encoding.Latin1;

        /// <summary>GBK 编码（代码页 936）；不可用时是 Latin-1。</summary>
        public static Encoding Chinese
        {
            get
            {
                if (_ready) return _chinese;

                lock (Gate)
                {
                    if (_ready) return _chinese;

                    try
                    {
                        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                        _chinese = Encoding.GetEncoding(936);
                    }
                    catch (Exception)
                    {
                        _chinese = Encoding.Latin1;
                    }

                    _ready = true;
                }

                return _chinese;
            }
        }

        /// <summary>
        /// GBK 的双字节结构校验：首字节 0x81~0xFE，尾字节 0x40~0xFE 且不为 0x7F。
        /// <para>至少要出现一个高位字节，否则纯 ASCII 也算"通过"就没有意义了。</para>
        /// </summary>
        public static bool IsValidGbk(ReadOnlySpan<byte> data)
        {
            var index = 0;
            var hasHighByte = false;

            while (index < data.Length)
            {
                var b = data[index];

                if (b < 0x80)
                {
                    index++;
                    continue;
                }

                hasHighByte = true;

                if (b is 0x80 or 0xFF) return false;
                if (index + 1 >= data.Length) return false;

                var trail = data[index + 1];
                if (trail is < 0x40 or > 0xFE || trail == 0x7F) return false;

                index += 2;
            }

            return hasHighByte;
        }

        /// <summary>
        /// 解码一段「编码不明」的字节：能按 GBK 校验通就按 GBK，否则按 Latin-1。
        /// </summary>
        public static string DecodeLegacy(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return string.Empty;

            var chinese = Chinese;
            if (!ReferenceEquals(chinese, Encoding.Latin1) && IsValidGbk(data))
                return chinese.GetString(data).TrimEnd('\0');

            return Encoding.Latin1.GetString(data).TrimEnd('\0');
        }

        /// <summary>
        /// 解码整个文本文件（.lrc 等）：先看 BOM，再严格按 UTF-8 试一遍，
        /// UTF-8 解不通说明是 GBK 歌词——这是国内最常见的情况。
        /// </summary>
        public static string DecodeFile(byte[] bytes)
        {
            if (bytes.Length == 0) return string.Empty;

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);

            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Chinese.GetString(bytes);
            }
        }
    }
}
