using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

// 打开与播放控制（外加媒体的创建 / 释放）。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        // ---- 打开与播放 -------------------------------------------------------

        /// <summary>
        /// 打开一个本地文件或网络流并立即开始播放。失败时抛出异常，由调用方提示用户。
        /// </summary>
        public void Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("媒体路径不能为空。", nameof(path));

            if (!MediaFormats.IsOpenable(path))
                throw new FileNotFoundException("找不到媒体文件。", path);

            // 画面旋转是内核级选项：要换的那种旋转和当前内核不一致时，在这里先换内核
            // （界面会在 CoreRebuilt 里把窗口改挂到新播放器上）。
            EnsureCore(Rotation);

            // 先释放上一个媒体，避免 libvlc 侧引用堆积。
            DisposeCurrentMedia();

            var media = CreateMedia(path);
            _currentMedia = media;
            _currentPath = path;
            _endReached = false;
            ResetFrameStepping();

            if (!_mediaPlayer.Play(media))
            {
                var detail = _libVlc.LastLibVLCError;
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(detail) ? "无法开始播放该媒体。" : detail);
            }
        }

        /// <summary>继续播放；若已播完或已停止则从头重新播放。</summary>
        public void Play()
        {
            if (_currentMedia == null) return;

            if (_state == PlayerState.Ended || _state == PlayerState.Stopped)
            {
                _endReached = false;
                ResetFrameStepping();
                _mediaPlayer.Play(_currentMedia);
                return;
            }

            if (!_mediaPlayer.IsPlaying)
            {
                ResetFrameStepping();
                _mediaPlayer.Play();
            }
        }

        /// <summary>从头重新播放当前媒体（用于单曲循环）。</summary>
        public void Replay()
        {
            if (_currentMedia == null) return;
            _endReached = false;
            _mediaPlayer.Play(_currentMedia);
        }

        /// <summary>暂停。</summary>
        public void Pause()
        {
            if (_mediaPlayer.CanPause && _mediaPlayer.IsPlaying)
                _mediaPlayer.SetPause(true);
        }

        /// <summary>在播放/暂停之间切换。</summary>
        public void TogglePlayPause()
        {
            if (_currentMedia == null) return;

            if (_mediaPlayer.IsPlaying)
                Pause();
            else
                Play();
        }

        /// <summary>停止播放并释放当前媒体。</summary>
        public void Stop()
        {
            _endReached = false;
            if (_currentMedia == null) return;

            _mediaPlayer.Stop();
            DisposeCurrentMedia();
            _currentPath = null;
            SetState(PlayerState.Stopped);
        }

        /// <summary>跳转到指定毫秒位置。</summary>
        public void SeekTo(long milliseconds)
        {
            if (_currentMedia == null) return;

            var length = Length;
            var target = Math.Clamp(milliseconds, 0, length > 0 ? length : long.MaxValue);

            // 跳转之后原来的逐帧序号就没意义了
            ResetFrameStepping();

            _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(target));
        }

        /// <summary>按相对毫秒数跳转。</summary>
        public void SeekBy(long deltaMilliseconds) => SeekTo(Time + deltaMilliseconds);

        /// <summary>按百分比跳转（0~1）。</summary>
        public void SeekToFraction(double fraction)
        {
            var length = Length;
            if (length <= 0) return;
            SeekTo((long)(length * Math.Clamp(fraction, 0.0, 1.0)));
        }

        // ---- 内部实现 ---------------------------------------------------------

        private Media CreateMedia(string path)
        {
            // 串流走 Uri 构造，本地文件走路径构造；选项串由 MediaOptions 拼（纯函数，便于断言）。
            Uri? uri = null;
            var isStream = MediaFormats.IsStreamUri(path) && Uri.TryCreate(path, UriKind.Absolute, out uri);

            var media = isStream && uri != null
                ? new Media(_libVlc, uri)
                : new Media(_libVlc, path, FromType.FromPath);

            foreach (var option in MediaOptions.For(isStream, NormalizeVolume))
                media.AddOption(option);

            return media;
        }

        private void DisposeCurrentMedia()
        {
            var media = _currentMedia;
            _currentMedia = null;
            if (media == null) return;

            try
            {
                media.Dispose();
            }
            catch (Exception ex)
            {
// 释放失败不应影响后续播放。
                AppLog.Swallowed("释放失败不应影响后续播放。", ex);
            }
        }
    }
}
