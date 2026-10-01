using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>界面计时器：进度条与音量条的同步、拖动进度时的行为。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 进度条与音量条
        // =====================================================================
        private void OnUiTimerTick(object? sender, EventArgs e)
        {
            // 时长扫描逐条置位"待更新"，这里按计时器节流统一重算一次标题里的总时长。
            if (_playlistSummaryDirty)
            {
                _playlistSummaryDirty = false;
                UpdatePlaylistSummary();
            }

            UpdateProgress();
        }

        private void UpdateProgress()
        {
            if (!_engine.HasMedia) return;

            // 续播定位要等媒体真正播起来、且总时长已知，否则 libvlc 还没准备好。
            ApplyPendingResume();

            var length = _engine.Length;
            var time = _engine.Time;

            if (!_userSeeking && length > 0)
            {
                _suppressSeekEvent = true;
                trackSeek.Value = (int)Math.Clamp(time * SeekBarResolution / length, 0, SeekBarResolution);
                _suppressSeekEvent = false;
            }

            lblCurrentTime.Text = TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(time));
            lblTotalTime.Text = length > 0
                ? TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(length))
                : "--:--:--";

            RecordCurrentPosition();
            UpdateLyricsPosition();
        }

        private void OnSeekScroll(object? sender, EventArgs e)
        {
            if (_suppressSeekEvent || _suspendUiEvents) return;

            var target = GetSeekTarget();
            if (target < 0) return;

            lblCurrentTime.Text = TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(target));

            // 拖动过程中不立即跳转，松手时才定位，避免频繁 seek。
            if (!_userSeeking)
                _engine.SeekTo(target);
        }

        private void OnSeekMouseUp(object? sender, MouseEventArgs e)
        {
            if (!_userSeeking) return;
            _userSeeking = false;

            var target = GetSeekTarget();
            if (target >= 0)
                _engine.SeekTo(target);
        }

        private long GetSeekTarget()
        {
            var length = _engine.Length;
            if (!_engine.HasMedia || length <= 0) return -1;

            return (long)(length * (trackSeek.Value / (double)SeekBarResolution));
        }

        private void OnVolumeScroll(object? sender, EventArgs e)
        {
            if (_suspendUiEvents) return;

            ApplyVolume(trackVolume.Value);
            if (trackVolume.Value > 0 && _engine.Muted) SetMute(false);
        }
    }
}
