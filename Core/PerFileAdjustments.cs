using System;
using System.Threading;
using System.Threading.Tasks;

namespace 播放器.Core
{
    /// <summary>某个文件上次用的那几样调整（延迟是毫秒；没有记录时都是"没调过"）。</summary>
    public readonly struct TrackAdjustments
    {
        public TrackAdjustments(
            long audioDelayMilliseconds,
            long subtitleDelayMilliseconds,
            long lyricsOffsetMilliseconds,
            ScreenRotation rotation = ScreenRotation.None)
        {
            AudioDelayMilliseconds = audioDelayMilliseconds;
            SubtitleDelayMilliseconds = subtitleDelayMilliseconds;
            LyricsOffsetMilliseconds = lyricsOffsetMilliseconds;
            Rotation = rotation;
        }

        /// <summary>音画延迟：正值 = 声音延后。</summary>
        public long AudioDelayMilliseconds { get; }

        /// <summary>字幕延迟：正值 = 字幕延后。</summary>
        public long SubtitleDelayMilliseconds { get; }

        /// <summary>歌词偏移：正值 = 歌词提前（和 LRC 的 <c>[offset:]</c> 同号）。</summary>
        public long LyricsOffsetMilliseconds { get; }

        /// <summary>画面旋转 / 翻转（按文件记住；<see cref="ScreenRotation.None"/> = 没转过）。</summary>
        public ScreenRotation Rotation { get; }

        /// <summary>几样都是"没调过"。</summary>
        public bool IsEmpty =>
            AudioDelayMilliseconds == 0 &&
            SubtitleDelayMilliseconds == 0 &&
            LyricsOffsetMilliseconds == 0 &&
            Rotation == ScreenRotation.None;
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
        /// 取某个文件上次用的几样调整。没记过、或者路径不可记（网络流 / 空路径）时都是"没调过"——
        /// 读不会凭空建记录，所以"最近播放"不会被读操作污染。
        /// </summary>
        public TrackAdjustments Get(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return default;

            _history.TryGetDelays(path, out var audio, out var subtitle);
            _history.TryGetLyricsOffset(path, out var offset);
            _history.TryGetRotation(path, out var rotation);

            return new TrackAdjustments(audio, subtitle, offset, rotation);
        }

        /// <summary>记住这个文件的音画 / 字幕延迟，并立刻落盘（一次都不许丢，见 <see cref="SaveEagerly"/>）。</summary>
        public void RecordDelays(string? path, long audioDelayMilliseconds, long subtitleDelayMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            _history.RecordDelays(path, audioDelayMilliseconds, subtitleDelayMilliseconds);
            SaveEagerly();
        }

        /// <summary>记住这个文件的歌词偏移，并立刻落盘（一次都不许丢，见 <see cref="SaveEagerly"/>）。</summary>
        public void RecordLyricsOffset(string? path, long milliseconds)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            _history.RecordLyricsOffset(path, milliseconds);
            SaveEagerly();
        }

        /// <summary>
        /// 记住这个文件的画面旋转 / 翻转，并立刻落盘（转个画面是按文件记的，换片要能自己回来）。
        /// </summary>
        public void RecordRotation(string? path, ScreenRotation rotation)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            _history.RecordRotation(path, rotation);
            SaveEagerly();
        }

        /// <summary>
        /// "改一次就落盘"这条承诺的落实处。
        /// <para>
        /// 直接排一次后台写入；要是**已经有一次写入在飞**（<see cref="PlaybackHistory.SaveInBackground"/>
        /// 返回 <c>false</c>，改动只留在内存里），就在后台补一次——不能只等"下一次调用"：
        /// <b>关掉「记住播放进度」之后，按文件的调整只剩这一条落盘路径</b>
        /// （定时落盘与退出落盘都以那个开关为前提），而用户很可能调完就关程序。
        /// </para>
        /// <para>
        /// 补写用的是**这个实例**（也就是持有这次改动的那个）：历史是"一个实例一份内存副本、
        /// 共写同一个文件"，让写入任务自己复查 <c>_dirty</c> 而重写会把别的实例的旧快照冲出去。
        /// 补几次还排不上（比如磁盘卡住）就退回同步写一次——它会先等在飞那次落地。
        /// </para>
        /// </summary>
        private void SaveEagerly()
        {
            if (_history.SaveInBackground()) return;

            Task.Run(() =>
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    Thread.Sleep(50);

                    if (_history.SaveInBackground()) return;
                }

                _history.Save();
            });
        }
    }
}
