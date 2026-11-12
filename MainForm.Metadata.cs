using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// 读标签要真的读文件（内嵌封面动辄一两 MB），所以交给
    /// <see cref="MetadataLoadQueue"/> 在专用线程上读（有界、最新优先）；读回来之前界面先占位
    /// （「正在读取标签…」），靠 <see cref="MediaSession"/> 的版本号判断"这份结果还属于当前这首吗"。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        // =====================================================================
        // 曲目元数据
        // =====================================================================

        /// <summary>
        /// 读标签 / 歌词的队列（1.19.0）：最多两条在飞、只留一条"最新待读"，跑在专用线程上。
        /// <para>
        /// 为什么不就地 <c>Task.Run</c>：连切二十首就会在池上攒二十条并发读，而离线共享上
        /// 一次读文件能卡几十秒（1.17.0 实测 <c>File.Exists</c> 82～93 秒）——被放弃的读取
        /// 只是"结果丢掉"，线程照样占着池里的工作线程。细节见 <see cref="MetadataLoadQueue"/>。
        /// </para>
        /// <para>在构造函数里建（字段初始值设定项不能引用实例方法）。</para>
        /// </summary>
        private readonly MetadataLoadQueue _metadata;

        /// <summary>
        /// 真的去磁盘上读一份标签 + 歌词。
        /// <para>冒烟测试换成受闸门控制的假读取，用来验"占位 / 有界 / 迟到的不覆盖"。</para>
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Func<string, MetadataResult> MetadataLoad { get; set; } = ReadMetadataFromDisk;

        /// <summary>读完了、但已经切到别的歌（结果被丢掉）的次数。</summary>
        internal int MetadataDiscards { get; private set; }

        /// <summary>
        /// 占位前的宽限（毫秒）：本地读标签通常几十毫秒，那么快就没必要把上一首的封面摘掉再放回来
        /// （每切一首闪一下比"晚半拍"更碍眼）。超过了才换成「正在读取标签…」。
        /// </summary>
        private const int MetadataPendingGraceMs = 150;

        /// <summary>宽限计时器：到点还在读就把界面换成占位（一次性，到点就停）。</summary>
        private readonly System.Windows.Forms.Timer _metadataPendingTimer;

        private string? _pendingMetadataPath;
        private int _pendingMetadataToken;

        /// <summary>测试用：读标签的队列本身（<c>Starts</c> / <c>Skips</c> / <c>InFlight</c> / <c>Pending</c>）。</summary>
        internal MetadataLoadQueue Metadata => _metadata;

        /// <summary>切歌后加载标签、歌词、封面与媒体信息。传 <c>null</c> 表示清空。</summary>
        internal void LoadTrackMetadata(string? path)
        {
            var token = _track.BeginMetadata();

            // 上一首还在飞的在线查找作废，免得它的结果盖到新歌上
            _online.Cancel();

            if (string.IsNullOrEmpty(path))
            {
                _metadata.Clear();
                ClearPendingMetadataDisplay();
                ApplyMetadata(null, new MediaTags(), LyricsDocument.Empty);
                return;
            }

            // 光盘（dvd / cdda 这类 MRL）没有标签、没有歌词、更不该去网上查：
            // 直接给一份空的，别让 TagReader 对着 "dvdsimple:///L:" 去当文件读。
            if (MediaFormats.IsDiscLocation(path))
            {
                _metadata.Clear();
                ClearPendingMetadataDisplay();
                ApplyMetadata(path, new MediaTags(), LyricsDocument.Empty, allowOnlineLookup: false);
                return;
            }

            // 网络流（http / rtsp …）没有"文件里的标签"可读：以前会给它白开一次 FileStream
            // 再失败（还顺带读一遍外挂歌词），现在直接按"没有标签"处理。
            if (MediaFormats.IsStreamUri(path))
            {
                _metadata.Clear();
                ClearPendingMetadataDisplay();
                ApplyMetadata(path, new MediaTags(), LyricsDocument.Empty);
                return;
            }

            // 先去排队读；读得慢（超过宽限）才把上一首的封面 / 词曲摘掉、换成「正在读取标签…」。
            // 读得快的时候界面根本不动——每切一首闪一下比"晚半拍"更碍眼。
            ShowMetadataPendingSoon(path, token);
            _metadata.Request(path, token);
        }

        /// <summary>记下"这一首还在读"，宽限到点之后再决定要不要占位。</summary>
        private void ShowMetadataPendingSoon(string path, int token)
        {
            _pendingMetadataPath = path;
            _pendingMetadataToken = token;

            _metadataPendingTimer.Stop();
            _metadataPendingTimer.Start();
        }

        /// <summary>撤掉"还在读"的标记（读完了 / 不读了 / 换形态了）。</summary>
        private void ClearPendingMetadataDisplay()
        {
            _pendingMetadataPath = null;
            _metadataPendingTimer.Stop();
        }

        private void OnMetadataPendingTimerTick(object? sender, EventArgs e)
        {
            _metadataPendingTimer.Stop();

            var path = _pendingMetadataPath;
            _pendingMetadataPath = null;

            // 计时器到点时可能已经读回来了（或者用户又切走了）：那就不占位
            if (path == null || !_track.IsCurrent(_pendingMetadataToken)) return;

            ShowMetadataPending(path);
        }

        /// <summary>
        /// 占位：这一首的标签还没读回来。
        /// <para>
        /// 走同一个 <see cref="ApplyMetadata"/>，只是 <c>pending: true</c> 时：信息页只写两行
        /// （不调 <c>FormatFileSize</c>）、封面留空、系统媒体控件那边也不查外挂封面——
        /// <b>占位期间一次磁盘都不碰</b>。
        /// </para>
        /// </summary>
        private void ShowMetadataPending(string path) =>
            ApplyMetadata(path, new MediaTags(), LyricsDocument.Empty, allowOnlineLookup: false, pending: true);

        private static MetadataResult ReadMetadataFromDisk(string path)
        {
            var tags = TagReader.Read(path);
            return new MetadataResult(tags, LyricsLoader.Load(path, tags));
        }

        private void PostMetadata(string path, int token, MetadataResult? result)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;

                    if (!_track.IsCurrent(token))
                    {
                        // 用户早切到别的歌了：这份结果直接丢掉（不碰界面、也不报错）
                        MetadataDiscards++;
                        return;
                    }

                    // 读回来了：别让宽限计时器到点再把占位盖上去
                    ClearPendingMetadataDisplay();

                    if (result is { } value)
                        ApplyMetadata(path, value.Tags, value.Lyrics);
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
        /// <param name="pending">
        /// 占位（1.19.0）：标签还在读。<c>true</c> 时界面写「正在读取标签…」、封面留空，
        /// 而且<b>一处磁盘都不碰</b>（不算文件大小、不找外挂封面）。
        /// </param>
        internal void ApplyMetadata(
            string? path, MediaTags tags, LyricsDocument lyrics, bool allowOnlineLookup, bool pending = false)
        {
            _track.Apply(path, tags, lyrics);

            // 换歌先把这个文件自己的歌词偏移接上：下面推给界面时才是对的那一份
            RestoreLyricsOffsetForCurrentMedia(path);

            // 系统媒体控件（音量弹窗里那一块）跟着换歌
            UpdateSystemMediaControls(path, tags, includeSidecarCover: !pending);

            sidebarPanel.ShowTrack(
                path,
                tags,
                _track.ShiftedLyrics,
                pending ? BuildPendingInfoRows(path) : BuildInfoRows(path, tags),
                pending);

            UpdateLyricsPosition();
            UpdateTrayText();

            // 音频窗口那张卡片跟着换封面 / 曲名；顺手按新文件决定要不要换形态
            _surfaces.Audio.ShowTrack(path, tags, pending);
            SyncWindowShape(path);

            // 桌面歌词是独立的窗口，内容要单独推一次（曲名在没有歌词时用来兜底）
            RefreshDesktopLyricsContent();

            if (allowOnlineLookup) QueueOnlineLookup(path, tags, lyrics);
        }

        /// <summary>
        /// 把这一首的曲名 / 歌手 / 封面推给系统媒体控件（音量弹窗 / 锁屏里那一块）。
        /// <para>没有接上时什么都不做。曲名与界面上的「标题」取同一份兜底（标签 → 文件名）。</para>
        /// </summary>
        /// <param name="includeSidecarCover">
        /// 要不要去找同目录的外挂封面。占位（标签还在读）时传 <c>false</c>：
        /// 那时候要的是"把上一首那张摘掉"，而不是去枚举目录。
        /// </param>
        private void UpdateSystemMediaControls(string? path, MediaTags? tags, bool includeSidecarCover = true)
        {
            if (_smtc == null) return;

            var title = FirstNonEmpty(tags?.Title, path == null ? null : Path.GetFileNameWithoutExtension(path));

            _smtc.SetTrack(title, tags?.Artist, _engine.HasVideo && !IsAudioWindow);

            // 封面：文件里内嵌的那张优先；没有再找同目录的 cover.jpg / folder.jpg 这类外挂封面
            // （找哪个文件由 Core\SidecarCover 定，和侧栏封面、和"要不要去网上找"是同一份判定）
            if (tags is { HasCoverArt: true })
                _smtc.SetCover(tags.CoverArt, tags.CoverMimeType);
            else
                _smtc.SetCoverFile(includeSidecarCover ? SidecarCover.FindFor(path) : null);

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

            // 音频窗口那张卡片只显示"当前这一句"；没有歌词 / 纯文本歌词时清空，不留一行旧字
            if (IsAudioWindow) _surfaces.Audio.SetLyricLine(CurrentLyricLine(position));

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

        /// <summary>
        /// 当前这一句歌词（音频窗口卡片底部那一行用）。
        /// <para>纯文本歌词没有时间轴、没有歌词时都返回 <c>null</c>——卡片上就不占这一行。</para>
        /// </summary>
        private string? CurrentLyricLine(TimeSpan position)
        {
            var lyrics = _track.ShiftedLyrics;
            if (lyrics.IsEmpty || !lyrics.IsSynchronized) return null;

            var index = lyrics.LineIndexAt(position);
            if (index < 0 || index >= lyrics.Lines.Count) return null;

            return lyrics.Lines[index].Text;
        }

        /// <summary>
        /// 占位时的「媒体信息」页：只说"在读"和"在读哪个文件"。
        /// <para>刻意不调 <see cref="BuildInfoRows"/>：那里面会算文件大小、查外挂封面，
        /// 占位期间不该在界面线程上碰磁盘。</para>
        /// </summary>
        internal static IReadOnlyList<(string Name, string Value)> BuildPendingInfoRows(string? path)
        {
            var rows = new List<(string, string)> { ("状态", CoverView.PendingText) };

            var name = path == null ? null : Path.GetFileName(path);
            if (!string.IsNullOrEmpty(name)) rows.Add(("文件", name!));

            return rows;
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
