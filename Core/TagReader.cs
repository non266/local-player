using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace 播放器.Core
{
    /// <summary>
    /// 直接从媒体文件里读元数据标签，不经过 libvlc。
    /// <para>
    /// 之所以自己解析：libvlc 只暴露 <c>Media.Meta()</c> 的十几个字段，
    /// <b>没有歌词</b>，也不给内嵌封面；而本播放器需要显示歌词与专辑封面，
    /// 所以这里按格式规范自己读文件内部结构：
    /// </para>
    /// <list type="bullet">
    /// <item>ID3v2（MP3 等）：<c>TIT2/TPE1/TALB/TCON/TRCK/COMM</c>、<c>USLT</c>（纯文本歌词）、
    ///       <c>SYLT</c>（带时间轴歌词）、<c>APIC</c>（封面）、<c>TXXX:LYRICS</c></item>
    /// <item>FLAC：<c>STREAMINFO</c>（采样率/声道/位深）、<c>VORBIS_COMMENT</c>
    ///       （含 <c>LYRICS</c> / <c>UNSYNCEDLYRICS</c>）、<c>PICTURE</c>（封面）</item>
    /// <item>MP4 / M4A：<c>ilst</c> 里的 <c>©nam ©ART ©alb ©day ©gen ©cmt ©lyr covr trkn</c></item>
    /// <item>ID3v1：仅在上面的标签完全读不到时兜底</item>
    /// </list>
    /// <para>
    /// 整个解析过程<b>只读不写</b>，任何异常都被吞掉并返回空标签——
    /// 标签读不出来绝不能影响播放。
    /// </para>
    /// </summary>
    public static class TagReader
    {
        /// <summary>读 ID3v2 时最多向后读的字节数，防止被异常的长度字段牵着走。</summary>
        internal const int MaxId3Bytes = 24 * 1024 * 1024;

        /// <summary>内嵌封面的体积上限；超过这个数基本可以认为是损坏数据。</summary>
        internal const int MaxCoverBytes = 12 * 1024 * 1024;

        /// <summary>Vorbis comment 里最多解析多少条，防御损坏文件。</summary>
        internal const int MaxVorbisComments = 4096;

        /// <summary>
        /// 读取指定文件的标签；文件不存在、格式不支持或数据损坏时返回空标签（不抛异常）。
        /// </summary>
        public static MediaTags Read(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return new MediaTags();

            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                var tags = new MediaTags();
                ReadInto(stream, Path.GetExtension(path).ToLowerInvariant(), tags);
                return tags;
            }
            catch (Exception)
            {
                return new MediaTags();
            }
        }

        // ---- 入口分发 ---------------------------------------------------------

        private static void ReadInto(Stream stream, string extension, MediaTags tags)
        {
            stream.Position = 0;
            var magic = TagBytes.ReadBytes(stream, 12);
            var head = Encoding.Latin1.GetString(magic);

            var hasId3 = head.StartsWith("ID3", StringComparison.Ordinal);
            var isMp4 = IsMp4Extension(extension) || (head.Length >= 8 && string.CompareOrdinal(head, 4, "ftyp", 0, 4) == 0);

            if (hasId3)
            {
                // ID3v2 的 total size 只有解析完头部才知道，用返回值带出来。
                var tagBytes = Id3v2TagReader.ReadId3v2(stream, tags);

                // 少数 FLAC 会在 fLaC 之前挂一个 ID3v2 标签，跳过去再认一次。
                stream.Position = Math.Clamp(tagBytes, 0, stream.Length);
                var after = TagBytes.ReadBytes(stream, 4);
                var marker = Encoding.Latin1.GetString(after);

                if (marker == "fLaC")
                    FlacTagReader.ReadFlacBlocks(stream, tags);      // 注意：此时流已在 fLaC 之后，见方法内说明
                else if (isMp4)
                    Mp4TagReader.ReadMp4(stream, tags);
            }
            else if (head.StartsWith("fLaC", StringComparison.Ordinal))
            {
                // 上面读了 12 字节 magic，而 ReadFlacBlocks 要求流停在 fLaC 标记之后。
                stream.Position = 4;
                FlacTagReader.ReadFlacBlocks(stream, tags);
            }
            else if (isMp4)
            {
                Mp4TagReader.ReadMp4(stream, tags);
            }

            // ID3v1 是 128 字节的尾巴，只有前面什么都没读到时才值得一看。
            if (tags.Title == null && tags.Artist == null && tags.Album == null)
                Id3v1TagReader.ReadId3v1(stream, tags);
        }

        private static bool IsMp4Extension(string extension) =>
            extension is ".mp4" or ".m4a" or ".m4v" or ".m4b" or ".mov" or ".3gp" or ".3g2" or ".f4v";
    }
}
