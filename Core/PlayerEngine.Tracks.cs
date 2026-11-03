using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

// 字幕与音轨、媒体信息、去隔行、音画 / 字幕同步。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        // ---- 字幕与音轨 -------------------------------------------------------

        /// <summary>加载外挂字幕文件并立即启用。</summary>
        public bool AddSubtitleFile(string path)
        {
            if (!File.Exists(path)) return false;
            if (_currentMedia == null) return false;

            var uri = new Uri(path).AbsoluteUri;
            return _mediaPlayer.AddSlave(MediaSlaveType.Subtitle, uri, true);
        }

        /// <summary>关闭字幕显示（-1 表示禁用）。</summary>
        public void DisableSubtitle() => _mediaPlayer.SetSpu(-1);

        /// <summary>获取可用字幕轨道。</summary>
        public IReadOnlyList<TrackDescription> GetSubtitleTracks() =>
            _mediaPlayer.SpuDescription ?? Array.Empty<TrackDescription>();

        /// <summary>获取可用音轨。</summary>
        public IReadOnlyList<TrackDescription> GetAudioTracks() =>
            _mediaPlayer.AudioTrackDescription ?? Array.Empty<TrackDescription>();

        /// <summary>切换字幕轨道。</summary>
        public void SetSubtitleTrack(int id) => _mediaPlayer.SetSpu(id);

        /// <summary>切换音轨。</summary>
        public void SetAudioTrack(int id) => _mediaPlayer.SetAudioTrack(id);

        /// <summary>当前字幕轨道 id。</summary>
        public int CurrentSubtitleTrack => _mediaPlayer.Spu;

        /// <summary>当前音轨 id。</summary>
        public int CurrentAudioTrack => _mediaPlayer.AudioTrack;

        // ---- 媒体信息 ---------------------------------------------------------

        /// <summary>当前媒体的轨道清单；尚未开始播放时为空。</summary>
        public IReadOnlyList<MediaTrack> GetTracks()
        {
            var media = _currentMedia;
            if (media == null) return Array.Empty<MediaTrack>();

            try { return media.Tracks ?? Array.Empty<MediaTrack>(); }
            catch (Exception) { return Array.Empty<MediaTrack>(); }
        }

        /// <summary>轨道的可读编解码器名称，例如 <c>H264 - MPEG-4 AVC (part 10)</c>。</summary>
        public string? DescribeCodec(MediaTrack track)
        {
            var media = _currentMedia;
            if (media == null) return null;

            try { return media.CodecDescription(track.TrackType, (uint)track.Id); }
            catch (Exception) { return null; }
        }

        // ---- 去隔行 -----------------------------------------------------------

        /// <summary>
        /// 设置去隔行模式；<c>null</c> 或空串表示关闭。
        /// <para>可用值：<c>auto</c>、<c>blend</c>、<c>bob</c>、<c>linear</c>、<c>x</c>、
        /// <c>yadif</c>、<c>yadif2x</c>、<c>phosphor</c>、<c>ivtc</c>。</para>
        /// </summary>
        public bool SetDeinterlace(string? mode)
        {
            try
            {
                _mediaPlayer.SetDeinterlace(string.IsNullOrWhiteSpace(mode) ? null : mode);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- 音画与字幕同步 ---------------------------------------------------

        /// <summary>音频延迟（毫秒），正值表示声音推后。</summary>
        public long AudioDelayMilliseconds
        {
            get
            {
                try { return _mediaPlayer.AudioDelay / 1000; }
                catch (Exception) { return 0; }
            }
        }

        /// <summary>字幕延迟（毫秒），正值表示字幕推后。</summary>
        public long SubtitleDelayMilliseconds
        {
            get
            {
                try { return _mediaPlayer.SpuDelay / 1000; }
                catch (Exception) { return 0; }
            }
        }

        /// <summary>设置音频延迟（毫秒）。libvlc 的接口收的是微秒。</summary>
        public bool SetAudioDelayMilliseconds(long milliseconds)
        {
            try { return _mediaPlayer.SetAudioDelay(Math.Clamp(milliseconds, -60000, 60000) * 1000); }
            catch (Exception) { return false; }
        }

        /// <summary>设置字幕延迟（毫秒）。libvlc 的接口收的是微秒。</summary>
        public bool SetSubtitleDelayMilliseconds(long milliseconds)
        {
            try { return _mediaPlayer.SetSpuDelay(Math.Clamp(milliseconds, -60000, 60000) * 1000); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 轨道清单解析出来了吗；解析出来了，<paramref name="hasVideo"/> 才是"真实"的结论。
        /// <para>
        /// 刚打开媒体的那一瞬间 <c>GetTracks()</c> 是空的，这时不能拿"没有视频轨"当结论——
        /// 窗口形态那套判定要的正是"知道才判、不知道就先用扩展名猜"（见 window modes）。
        /// </para>
        /// </summary>
        public bool TryGetHasVideoTrack(out bool hasVideo)
        {
            hasVideo = false;

            var tracks = GetTracks();
            if (tracks.Count == 0) return false;

            foreach (var track in tracks)
            {
                if (track.TrackType != TrackType.Video) continue;

                hasVideo = true;
                return true;
            }

            return true;
        }

        /// <summary>
        /// 这个文件的真实轨道结论；<paramref name="path"/> 与当前媒体不一致、或轨道还没出来时返回 <c>null</c>。
        /// <para>
        /// 比对路径是必须的：<c>Open()</c> 之后 libvlc 解析有延迟，中间那几拍 <c>media.Tracks</c>
        /// 可能还是空的、甚至还没来得及换成新媒体的——拿上一首的轨道去决定窗口形态会切错。
        /// </para>
        /// </summary>
        public bool? HasVideoTrackFor(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase)) return null;

            return TryGetHasVideoTrack(out var hasVideo) ? hasVideo : null;
        }

        private MediaTrack? FindVideoTrack()
        {
            try
            {
                foreach (var track in GetTracks())
                    if (track.TrackType == TrackType.Video) return track;
            }
            catch (Exception ex)
            {
// 读不到就当没有视频轨
                AppLog.Swallowed("读不到就当没有视频轨", ex);
            }

            return null;
        }
    }
}
