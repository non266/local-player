using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// 文本解码与字段归一化：ID3 的四种编码、结尾符、封面 MIME、流派 / 年份 / 歌词键名。

namespace 播放器.Core
{
    internal static class TagText
    {
        // ---- 文本解码 ---------------------------------------------------------

        /// <summary>
        /// 按 ID3v2 的编码字节解码正文。
        /// <para>
        /// 0 = ISO-8859-1（但中文标签实际几乎都是 GBK，见 <see cref="DecodeLegacy"/>）、
        /// 1 = 带 BOM 的 UTF-16、2 = 无 BOM 的 UTF-16BE、3 = UTF-8。
        /// </para>
        /// </summary>
        internal static string Decode(ReadOnlySpan<byte> data, byte encoding)
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
        internal static string DecodeLegacy(ReadOnlySpan<byte> data) => LegacyText.DecodeLegacy(data);

        internal static string DecodeUtf16(ReadOnlySpan<byte> data, bool withBom)
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
        internal static int FindTerminator(ReadOnlySpan<byte> data, int start, byte encoding)
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
        internal static int TerminatorLength(byte encoding) => encoding is 1 or 2 ? 2 : 1;

        internal static bool IsLyricsKey(string description) =>
            NormalizeKey(description) is "LYRICS" or "LYRIC" or "UNSYNCEDLYRICS" or "SYNCEDLYRICS" or "LYRICSLRC";

        internal static string NormalizeKey(string key)
        {
            var builder = new StringBuilder(key.Length);
            foreach (var c in key)
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToUpperInvariant(c));

            return builder.ToString();
        }

        /// <summary>把 ID3v2.3 的 <c>"(17)Rock"</c> 这类写法还原成 <c>"Rock"</c>。</summary>
        internal static string? NormalizeGenre(string? genre)
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
        internal static string? NormalizeYear(string? year)
        {
            if (string.IsNullOrWhiteSpace(year)) return year;

            var trimmed = year.Trim();
            if (trimmed.Length >= 4 && char.IsDigit(trimmed[0]) && char.IsDigit(trimmed[1]) &&
                char.IsDigit(trimmed[2]) && char.IsDigit(trimmed[3]))
                return trimmed[..4];

            return trimmed;
        }

        internal static string GuessMimeType(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
            if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
            if (data.Length >= 3 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "image/gif";
            if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return "image/bmp";
            return "image/jpeg";
        }
    }
}
