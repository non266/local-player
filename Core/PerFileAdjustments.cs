using System;

namespace 播放器.Core
{
    /// <summary>某个文件上次用的三样调整（毫秒）。没有记录时三项都是 0。</summary>
    public readonly struct TrackAdjustments
    {
        public TrackAdjustments(
            long audioDelayMilliseconds,
            long subtitleDelayMilliseconds,
            long lyricsOffsetMilliseconds)
        {
            AudioDelayMilliseconds = audioDelayMilliseconds;
            SubtitleDelayMilliseconds = subtitleDelayMilliseconds;
            LyricsOffsetMilliseconds = lyricsOffsetMilliseconds;
        }

        /// <summary>音画延迟：正值 = 声音延后。</summary>
        public long AudioDelayMilliseconds { get; }

        /// <summary>字幕延迟：正值 = 字幕延后。</summary>
        public long SubtitleDelayMilliseconds { get; }

        /// <summary>歌词偏移：正值 = 歌词提前（和 LRC 的 <c>[offset:]</c> 同号）。</summary>
        public long LyricsOffsetMilliseconds { get; }

        /// <summary>三样都是 0（等于"这个文件没调过"）。</summary>
        public bool IsEmpty =>
            AudioDelayMilliseconds == 0 &&
            SubtitleDelayMilliseconds == 0 &&
            LyricsOffsetMilliseconds == 0;
    }

    /// <summary>
    /// 按文件记住的"调整"：音画延迟、字幕延迟、歌词偏移。
    /// <para>
    /// 它们和播放进度存在同一个 <c>history.json</c>（见 <see cref="PlaybackHistory"/>），
    /// 但<b>性质不一样</b>：进度是"看过哪儿"，每秒都在变、定期落盘就行；
    /// 调整是用户一项项调出来的，改一次就该立刻落地——用户可能马上切歌、马上关程序。
    /// 这个类就是那条规矩的唯一落点：所有"记住某个文件的调整"都从它走，
    /// 免得哪条链路漏了落盘（歌词偏移一开始就落盘，两个延迟却要等定时器）。
    /// </para>
    /// <para>
    /// 只管"记什么、什么时候落盘"，具体存储仍然是 <see cref="PlaybackHistory"/>，
    /// 文件格式不变。
    /// </para>
    /// </summary>
    public sealed class PerFileAdjustments
    {
        private readonly PlaybackHistory _history;

        public PerFileAdjustments(PlaybackHistory history) =>
            _history = history ?? throw new ArgumentNullException(nameof(history));

        /// <summary>
        /// 取某个文件上次用的三样调整。没记过、或者路径不可记（网络流 / 空路径）时都是 0——
        /// 读不会凭空建记录，所以"最近播放"不会被读操作污染。
        /// </summary>
        public TrackAdjustments Get(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return default;

            _history.TryGetDelays(path, out var audio, out var subtitle);
            _history.TryGetLyricsOffset(path, out var offset);

            return new TrackAdjustments(audio, subtitle, offset);
        }

        /// <summary>记住这个文件的音画 / 字幕延迟，并立刻排一次后台落盘。</summary>
        public void RecordDelays(string? path, long audioDelayMilliseconds, long subtitleDelayMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            _history.RecordDelays(path, audioDelayMilliseconds, subtitleDelayMilliseconds);
            _history.SaveInBackground();
        }

        /// <summary>记住这个文件的歌词偏移，并立刻排一次后台落盘。</summary>
        public void RecordLyricsOffset(string? path, long milliseconds)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            _history.RecordLyricsOffset(path, milliseconds);
            _history.SaveInBackground();
        }
    }
}
