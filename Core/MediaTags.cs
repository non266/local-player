using System;

namespace 播放器.Core
{
    /// <summary>
    /// 从媒体文件内部读出的元数据标签。
    /// <para>
    /// 由 <see cref="TagReader"/> 填充。读不到的字段一律保持 <c>null</c>，
    /// 由界面自己决定用什么代替（一般是退回文件名）。
    /// </para>
    /// </summary>
    public sealed class MediaTags
    {
        /// <summary>曲名。</summary>
        public string? Title { get; set; }

        /// <summary>艺术家 / 演唱者。</summary>
        public string? Artist { get; set; }

        /// <summary>专辑名。</summary>
        public string? Album { get; set; }

        /// <summary>专辑艺术家。</summary>
        public string? AlbumArtist { get; set; }

        /// <summary>流派。</summary>
        public string? Genre { get; set; }

        /// <summary>年份。</summary>
        public string? Year { get; set; }

        /// <summary>音轨号，可能形如 <c>"3/12"</c>。</summary>
        public string? TrackNumber { get; set; }

        /// <summary>备注。</summary>
        public string? Comment { get; set; }

        /// <summary>
        /// 歌词原文。可能是带 <c>[mm:ss.xx]</c> 时间戳的 LRC 文本，也可能是纯文本，
        /// 交给 <see cref="LyricsDocument.Parse"/> 去判定。
        /// </summary>
        public string? Lyrics { get; set; }

        /// <summary>歌词是否自带时间轴（例如 ID3v2 的 SYLT 帧）。</summary>
        public bool LyricsSynchronized { get; set; }

        /// <summary>内嵌封面图的原始字节。</summary>
        public byte[]? CoverArt { get; set; }

        /// <summary>封面的 MIME 类型，例如 <c>image/jpeg</c>。</summary>
        public string? CoverMimeType { get; set; }

        /// <summary>采样率（Hz）；只有文件头自带时才有值。</summary>
        public int? SampleRate { get; set; }

        /// <summary>声道数。</summary>
        public int? Channels { get; set; }

        /// <summary>位深。</summary>
        public int? BitsPerSample { get; set; }

        /// <summary>文件头里自带的总时长（毫秒）；只有能算出来的格式才有值。</summary>
        public long? DurationMilliseconds { get; set; }

        /// <summary>是否有内嵌封面。</summary>
        public bool HasCoverArt => CoverArt is { Length: > 0 };

        /// <summary>是否有歌词（无论带不带时间轴）。</summary>
        public bool HasLyrics => !string.IsNullOrWhiteSpace(Lyrics);
    }
}
