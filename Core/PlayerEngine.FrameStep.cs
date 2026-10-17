using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;

// 逐帧：帧长 / 帧率与按帧长 seek 的前进后退。

namespace 播放器.Core
{
    public sealed partial class PlayerEngine
    {
        // ---- 逐帧 -------------------------------------------------------------

        /// <summary>
        /// 当前视频的帧间隔（毫秒）；未知时为 0。
        /// <para>后退一帧要靠它来估算——libvlc 只提供"下一帧"，没有"上一帧"。</para>
        /// </summary>
        public double FrameDurationMilliseconds
        {
            get
            {
                var track = FindVideoTrack();
                if (track == null) return 0;

                var video = track.Value.Data.Video;
                if (video.FrameRateNum == 0 || video.FrameRateDen == 0) return 0;

                var fps = video.FrameRateNum / (double)video.FrameRateDen;
                return fps > 0.01 ? 1000.0 / fps : 0;
            }
        }

        /// <summary>当前视频的帧率；未知时为 0。</summary>
        public double FrameRate
        {
            get
            {
                var duration = FrameDurationMilliseconds;
                return duration > 0 ? 1000.0 / duration : 0;
            }
        }

        /// <summary>
        /// 走一帧。<paramref name="forward"/> 为 <c>false</c> 时退一帧。
        /// <para>三个 libvlc 限制决定了这里的写法：</para>
        /// <list type="bullet">
        /// <item><c>NextFrame()</c> 在当前环境下<b>不生效</b>——调用成功，但快照与屏幕像素都毫无变化
        /// （用三个视频、快照对比与真实按键两种方式验证过）。</item>
        /// <item>libvlc 本来就没有"上一帧"（VLC 本体也只有下一帧）。</item>
        /// <item>暂停时 <c>MediaPlayer.Time</c> 不推进——它是播放时钟，不是帧号。</item>
        /// </list>
        /// <para>
        /// 所以两个方向统一用"按帧长 seek"来实现：暂停状态下 libvlc 会解码到目标时间并显示那一帧，
        /// 落点稳定。帧序号由本类自己维护，跳转或重新播放时清零。
        /// </para>
        /// </summary>
        public bool StepFrame(bool forward)
        {
            if (_currentMedia == null) return false;

            try
            {
                // 逐帧只在暂停状态下有意义。调用方要等暂停真正生效再调，
                // 否则 NextFrame 会被静默丢掉。
                if (_mediaPlayer.IsPlaying && _mediaPlayer.CanPause)
                    _mediaPlayer.SetPause(true);

                var frameDuration = FrameDurationMilliseconds;
                if (frameDuration <= 0.5) frameDuration = 40;      // 拿不到帧率就按 25fps 估

                // 暂停时 MediaPlayer.Time 不推进（它是播放时钟），所以自己记住相对暂停点走了几帧。
                if (_frameStepBaseMilliseconds < 0)
                {
                    _frameStepBaseMilliseconds = Time;
                    _frameStepIndex = 0;
                }

                var next = _frameStepIndex + (forward ? 1 : -1);
                if (next < 0) return false;                        // 已经是这一段的第一帧

                // 前进方向也要设上限。以前只挡了后退，按住"下一帧"越过片尾之后
                // _frameStepIndex 会一直累加，再想用"上一帧"回到片内就要按很多次。
                var length = Length;
                var candidate = _frameStepBaseMilliseconds + (long)Math.Round(next * frameDuration);
                if (length > 0 && candidate > length) return false;

                // ⚠ 这里刻意<b>不用</b> libvlc 的 NextFrame()：实测它在当前环境下完全不生效——
                // 调用返回成功，但无论快照还是屏幕像素都一模一样（用三个视频、两种方式验证过）。
                // 而"定位到某个精确时间"是可靠的：暂停状态下 libvlc 会解码到目标时间并显示那一帧，
                // 所以两个方向都用 seek 来实现，既统一又真的能走帧。
                _frameStepIndex = next;

                var target = _frameStepBaseMilliseconds + (long)Math.Round(_frameStepIndex * frameDuration);

                // 直接用底层 SeekTo：走公开的 SeekTo 会把帧序号重置掉
                _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(Math.Max(0, target)));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>清掉逐帧状态（播放、跳转、换片之后都不该继续沿用）。</summary>
        private void ResetFrameStepping()
        {
            _frameStepBaseMilliseconds = -1;
            _frameStepIndex = 0;
        }
    }
}
