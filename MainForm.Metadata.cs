using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using Windows.Media;
using 播放器.Core;
using 播放器.Ui;

// 曲目元数据：切歌后异步读标签 / 歌词，回填侧栏与桌面歌词，组装「媒体信息」页。

namespace 播放器
{
    /// <summary>
    /// 曲目元数据：切歌之后读标签与歌词、把它们推到侧栏与桌面歌词，以及「媒体信息」页的内容。
    /// <para>
    /// 读标签要真的读文件（内嵌封面动辄一两 MB），所以丢在线程池里做，靠
    /// <see cref="TrackLyricsSession"/> 的版本号判断"这份结果还属于当前这首吗"。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        // =====================================================================
        // 曲目元数据
        // =====================================================================
        /// <summary>切歌后加载标签、歌词、封面与媒体信息。传 <c>null</c> 表示清空。</summary>
        internal void LoadTrackMetadata(string? path)
        {
            var token = _track.BeginMetadata();

            // 上一首还在飞的在线查找作废，免得它的结果盖到新歌上
            _online.Cancel();

            if (string.IsNullOrEmpty(path))
            {
                ApplyMetadata(null, new MediaTags(), LyricsDocument.Empty);
                return;
            }

            // 读标签要真的读文件（内嵌封面动辄一两 MB），丢到线程池里做，
            // 免得切歌时界面顿一下。
            Task.Run(() =>
            {
                var tags = TagReader.Read(path);
                return (Tags: tags, Lyrics: LyricsLoader.Load(path, tags));
            }).ContinueWith(
                task => PostMetadata(path, token, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void PostMetadata(string path, int token, Task<(MediaTags Tags, LyricsDocument Lyrics)> task)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || !_track.IsCurrent(token)) return;

                    if (task.IsCompletedSuccessfully)
                        ApplyMetadata(path, task.Result.Tags, task.Result.Lyrics);
                    else
                        ApplyMetadata(path, new MediaTags(), LyricsDocument.Empty);
                }));
            }
            catch (Exception ex)
            {
// 窗体正在销毁，结果丢掉就行
                AppLog.Swallowed("窗体正在销毁，结果丢掉就行", ex);
            }
        }

        internal void ApplyMetadata(string? path, MediaTags tags, LyricsDocument lyrics)
        {
            ApplyMetadata(path, tags, lyrics, allowOnlineLookup: true);
        }

        /// <param name="allowOnlineLookup">
        /// 是否允许"本地没有就去网上要"。从在线结果自己触发的刷新要传 <c>false</c>，
        /// 否则拿回封面之后又会因为"歌词还没有"再发起一轮查找。
        /// </param>
        internal void ApplyMetadata(string? path, MediaTags tags, LyricsDocument lyrics, bool allowOnlineLookup)
        {
            _track.Apply(path, tags, lyrics);

            // 换歌先把这个文件自己的歌词偏移接上：下面推给界面时才是对的那一份
            RestoreLyricsOffsetForCurrentMedia(path);

            // 系统媒体控件（音量弹窗里那一块）跟着换歌
            UpdateSystemMediaControls(path, tags);

            sidebarPanel.ShowTrack(path, tags, _track.ShiftedLyrics, BuildInfoRows(path, tags));
            UpdateLyricsPosition();
            UpdateTrayText();

            // 桌面歌词是独立的窗口，内容要单独推一次（曲名在没有歌词时用来兜底）
            RefreshDesktopLyricsContent();

            if (allowOnlineLookup) QueueOnlineLookup(path, tags, lyrics);
        }

        /// <summary>
        /// 把这一首的曲名 / 歌手推给系统媒体控件（音量弹窗 / 锁屏里那一块）。
        /// <para>没有接上时什么都不做。曲名与界面上的「标题」取同一份兜底（标签 → 文件名）。</para>
        /// </summary>
        private void UpdateSystemMediaControls(string? path, MediaTags? tags)
        {
            if (_smtc == null) return;

            var title = FirstNonEmpty(tags?.Title, path == null ? null : Path.GetFileNameWithoutExtension(path));

            _smtc.SetTrack(title, tags?.Artist, _engine.HasVideo);

            // 封面：内嵌图（同目录的封面图这一版没喂，如实写在 README 里）
            _smtc.SetCover(tags?.CoverArt, tags?.CoverMimeType);
            _smtc.SetTimeline(_engine.Length, _engine.Time);
        }

        /// <summary>推一次进度（界面计时器每 5 拍调一次，够音量弹窗里那条进度用了）。</summary>
        private void UpdateSystemMediaControlsPosition()
        {
            if (_smtc == null) return;

            _smtc.SetTimeline(_engine.Length, _engine.Time);
        }

        /// <summary>
        /// 把"现在这一首 + 现在的状态"整体补推一次。
        /// <para>
        /// 会话是在 <c>OnLoad</c> 里建的，而启动那一首的元数据在<b>构造函数</b>里就已经推过了
        /// （那时候还没有会话）——不补这一次，音量弹窗里第一首永远是空的。
        /// </para>
        /// </summary>
        private void SyncSystemMediaControls()
        {
            if (_smtc == null) return;

            UpdateSystemMediaControls(_track.Path, _track.Tags);

            _smtc.SetStatus(_engine.State switch
            {
                PlayerState.Playing => MediaPlaybackStatus.Playing,
                PlayerState.Paused => MediaPlaybackStatus.Paused,
                PlayerState.Error => MediaPlaybackStatus.Closed,
                _ => MediaPlaybackStatus.Stopped
            });
        }

        /// <summary>
        /// 只重建「媒体信息」页。
        /// <para>调延迟之类的操作会频繁触发，不能走 <c>ShowTrack</c>——
        /// 那会把歌词滚回顶部、封面重新解码一遍。</para>
        /// </summary>
        private void RefreshInfoPanel() =>
            sidebarPanel.UpdateInfoRows(BuildInfoRows(_track.Path, _track.Tags));

        /// <summary>把播放位置同步给歌词页与桌面歌词，让它们高亮 / 换到正确的行。</summary>
        private void UpdateLyricsPosition()
        {
            if (!_engine.HasMedia) return;

            var position = TimeSpan.FromMilliseconds(_engine.Time);

            sidebarPanel.UpdatePosition(position);
            _desktopLyrics?.UpdatePosition(position);

            // 顺手把"在不在播"推过去：控制条第一个按钮画播放还是暂停靠它。
            // 属性 setter 里有相等判断，没变的时候不会重画。
            SyncDesktopLyricsPlayingState();

            // 系统媒体控件那条进度也顺手推一下：每 5 拍（1 秒）一次就够，
            // 每次都推的话 COM 调用比这一整段还贵。
            if (_smtc != null && ++_smtcTick >= 5)
            {
                _smtcTick = 0;
                UpdateSystemMediaControlsPosition();
            }
        }

        /// <summary>组装「媒体信息」页的内容。</summary>
        internal IReadOnlyList<(string Name, string Value)> BuildInfoRows(string? path, MediaTags? tags)
        {
            var rows = new List<(string, string)>();

            void Add(string name, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) rows.Add((name, value!));
            }

            Add("标题", FirstNonEmpty(tags?.Title, path == null ? null : Path.GetFileNameWithoutExtension(path)));
            Add("艺术家", tags?.Artist);
            Add("专辑", tags?.Album);
            Add("专辑艺术家", tags?.AlbumArtist);
            Add("年份", tags?.Year);
            Add("音轨号", tags?.TrackNumber);
            Add("流派", tags?.Genre);
            Add("备注", tags?.Comment);

            Add("歌词", _track.Lyrics.IsEmpty
                ? null
                : _track.Lyrics.IsSynchronized
                    ? $"{_track.Lyrics.Source} · {_track.Lyrics.Lines.Count} 行（带轴）"
                    : $"{_track.Lyrics.Source} · 纯文本");

            Add("封面", tags is { HasCoverArt: true }
                ? $"{tags.CoverArt!.Length / 1024} KB · {ShortMime(tags.CoverMimeType)}"
                : null);

            if (path == null) return rows;

            Add("文件", Path.GetFileName(path));
            Add("位置", Path.GetDirectoryName(path));
            Add("大小", FormatFileSize(path));
            Add("格式", Path.GetExtension(path).TrimStart('.').ToUpperInvariant());

            var length = _engine.Length;
            Add("时长", length > 0 ? TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(length)) : null);

            // 视频规格与同步状态
            var fps = _engine.FrameRate;
            Add("帧率", fps > 0 ? $"{fps:0.###} fps" : null);
            Add("音频延迟", _engine.AudioDelayMilliseconds != 0
                ? TimeFormatter.FormatMilliseconds(_engine.AudioDelayMilliseconds)
                : null);
            Add("字幕延迟", _engine.SubtitleDelayMilliseconds != 0
                ? TimeFormatter.FormatMilliseconds(_engine.SubtitleDelayMilliseconds)
                : null);
            Add("歌词偏移", _track.LyricsOffsetMilliseconds != 0 ? LyricsOffset.Describe(_track.LyricsOffsetMilliseconds) : null);
            Add("去隔行", _settings.Deinterlace.Length > 0
                ? Core.DeinterlaceModes.Describe(_settings.Deinterlace)
                : null);

            // libvlc 报告的规格（采样率、分辨率等）
            Add("采样率", tags?.SampleRate is > 0 ? $"{tags!.SampleRate} Hz" : null);
            Add("声道数", tags?.Channels is > 0 ? $"{tags!.Channels} 声道" : null);
            Add("位深", tags?.BitsPerSample is > 0 ? $"{tags!.BitsPerSample} bit" : null);

            AppendTrackRows(rows);
            return rows;
        }

        private void AppendTrackRows(List<(string, string)> rows)
        {
            var tracks = _engine.GetTracks();
            if (tracks.Count == 0) return;

            var video = 0;
            var audio = 0;
            var subtitle = 0;

            foreach (var track in tracks)
            {
                var label = track.TrackType switch
                {
                    TrackType.Video => $"视频轨 {++video}",
                    TrackType.Audio => $"音轨 {++audio}",
                    TrackType.Text => $"字幕轨 {++subtitle}",
                    _ => "未知轨"
                };

                var description = DescribeEngineTrack(track);
                if (!string.IsNullOrWhiteSpace(description)) rows.Add((label, description));
            }
        }

        private string DescribeEngineTrack(MediaTrack track)
        {
            var parts = new List<string>();

            var codec = _engine.DescribeCodec(track);
            parts.Add(!string.IsNullOrWhiteSpace(codec) ? codec! : FourCc(track.Codec));

            switch (track.TrackType)
            {
                case TrackType.Video:
                {
                    var size = track.Data.Video;
                    if (size.Width > 0 && size.Height > 0) parts.Add($"{size.Width}×{size.Height}");
                    if (size.FrameRateNum > 0 && size.FrameRateDen > 0)
                        parts.Add($"{size.FrameRateNum / (double)size.FrameRateDen:0.###} fps");
                    break;
                }

                case TrackType.Audio:
                {
                    var sound = track.Data.Audio;
                    if (sound.Rate > 0) parts.Add($"{sound.Rate} Hz");
                    if (sound.Channels > 0) parts.Add($"{sound.Channels} 声道");
                    break;
                }
            }

            if (track.Bitrate > 0) parts.Add($"{track.Bitrate / 1000} kbps");

            // "und" 是 libvlc 表示"未标注语言"，显示出来没有意义
            if (!string.IsNullOrWhiteSpace(track.Language) &&
                !string.Equals(track.Language, "und", StringComparison.OrdinalIgnoreCase))
                parts.Add(track.Language!);

            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private static string FourCc(uint codec)
        {
            var bytes = new[]
            {
                (byte)(codec >> 24), (byte)(codec >> 16), (byte)(codec >> 8), (byte)codec
            };

            var printable = bytes.All(b => b >= 32 && b < 127);
            if (!printable) return $"0x{codec:X8}";

            var text = new string(bytes.Select(b => (char)b).ToArray()).Trim();
            return text.Length > 0 ? text : $"0x{codec:X8}";
        }

        private static string? FormatFileSize(string path)
        {
            try
            {
                var bytes = new FileInfo(path).Length;

                return bytes switch
                {
                    >= 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
                    >= 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
                    >= 1024 => $"{bytes / 1024.0:0.#} KB",
                    _ => $"{bytes} B"
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ShortMime(string? mime) =>
            string.IsNullOrWhiteSpace(mime) ? "未知格式" : mime!.Replace("image/", string.Empty).ToUpperInvariant();

        private static string? FirstNonEmpty(params string?[] candidates)
        {
            foreach (var candidate in candidates)
                if (!string.IsNullOrWhiteSpace(candidate)) return candidate;

            return null;
        }
    }
}
