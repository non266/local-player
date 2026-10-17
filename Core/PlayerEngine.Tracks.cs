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
