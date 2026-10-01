using System;

namespace 播放器.Core
{
    /// <summary>
    /// 「当前这一首」的会话状态：文件路径、标签、歌词，以及这首自己的歌词偏移。
    /// <para>
    /// 这几样原来散在主窗体的五个私有字段里（<c>_currentMediaPath</c> / <c>_currentTags</c> /
    /// <c>_currentLyrics</c> / <c>_lyricsOffsetMilliseconds</c> / <c>_metadataToken</c>），
    /// 侧栏、桌面歌词、在线歌词、音画延迟四条链路都能直接读、直接写，改一处要看五处。
    /// 收进一个对象之后：<b>切歌 = 换一份会话</b>，界面各处只负责"把会话画出来"。
    /// </para>
    /// <para>
    /// 这里只放状态和纯计算（歌词平移、偏移钳制、"这份结果还属于当前这首吗"）。
    /// <b>不碰界面、不碰文件、不碰网络</b>：异步读标签与在线查找仍由窗体发起，
    /// 拿回结果之后再写回这里——所以它可以脱离窗体单独推演。
    /// </para>
    /// <para>
    /// 线程约定和搬家之前一样：读标签的异步续体只把结果丢回界面线程再写进来，
    /// 因此下面这些状态全部只在界面线程上读写，不加锁。
    /// </para>
    /// </summary>
    public sealed class TrackLyricsSession
    {
        private int _metadataToken;

        /// <summary>当前曲目的完整路径；没有在放的曲目时为 <c>null</c>。</summary>
        public string? Path { get; private set; }

        /// <summary>当前曲目的标签（封面也在里面：在线取回封面时就是塞进这一份）。</summary>
        public MediaTags Tags { get; private set; } = new MediaTags();

        /// <summary>当前曲目的歌词，<b>没有</b>叠加歌词偏移（偏移在 <see cref="ShiftedLyrics"/> 里套）。</summary>
        public LyricsDocument Lyrics { get; private set; } = LyricsDocument.Empty;

        /// <summary>当前这首自己的歌词偏移（毫秒，正数 = 歌词提前）。</summary>
        public long LyricsOffsetMilliseconds { get; private set; }

        /// <summary>有在放的曲目吗（只看路径，不看标签是否已经读回来）。</summary>
        public bool HasTrack => !string.IsNullOrEmpty(Path);

        /// <summary>
        /// 借一个版本号，表示"我要去读某一首了"。
        /// <para>
        /// 读标签要真的读文件（内嵌封面动辄一两 MB），是丢在线程池里做的。
        /// 结果回来时用它比对：版本号变了就说明用户早切歌了，这份结果直接丢掉。
        /// </para>
        /// </summary>
        public int BeginMetadata() => ++_metadataToken;

        /// <summary>这份异步结果还属于当前这一首吗。</summary>
        public bool IsCurrent(int token) => token == _metadataToken;

        /// <summary>切歌那一瞬间先记下路径（标签还在读，界面已经要显示它了）。</summary>
        public void SetPath(string? path) => Path = path;

        /// <summary>换上另一份歌词（在线取回来的走这里）。</summary>
        public void SetLyrics(LyricsDocument? lyrics) => Lyrics = lyrics ?? LyricsDocument.Empty;

        /// <summary>整首换掉：路径、标签、歌词一次到位。</summary>
        public void Apply(string? path, MediaTags? tags, LyricsDocument? lyrics)
        {
            Path = path;
            Tags = tags ?? new MediaTags();
            Lyrics = lyrics ?? LyricsDocument.Empty;
        }

        /// <summary>把在线取到的封面挂到当前标签上。</summary>
        /// <remarks>
        /// 侧栏的封面页与「媒体信息」都从标签里取封面，所以"这张封面哪来的"只有这一处，
        /// 不用给在线封面单开一条链路。
        /// </remarks>
        public void ApplyCover(byte[] data, string? mime)
        {
            Tags.CoverArt = data;
            Tags.CoverMimeType = mime;
        }

        /// <summary>
        /// 设一个新的偏移，返回钳制之后真正生效的值
        /// （调用方要用它说"已按上限 ±30 秒处理"）。
        /// </summary>
        public long SetOffset(long milliseconds)
        {
            LyricsOffsetMilliseconds = LyricsOffset.Clamp(milliseconds);
            return LyricsOffsetMilliseconds;
        }

        /// <summary>按当前偏移平移过的歌词（偏移为 0 时原样返回同一份）。</summary>
        public LyricsDocument ShiftedLyrics =>
            LyricsOffsetMilliseconds == 0
                ? Lyrics
                : Lyrics.Shifted(TimeSpan.FromMilliseconds(LyricsOffsetMilliseconds));
    }
}
