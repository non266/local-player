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

            // A-B 循环：越过 B 点（含正好到 B）就跳回 A 点。拖动进度条时不掺和——
            // 那会儿用户正捏着位置，把他拽回 A 点只会让人以为进度条坏了。
            if (!_userSeeking)
                time = ApplyAbLoop(time);

            if (!_userSeeking && length > 0)
            {
                _suppressSeekEvent = true;
                trackSeek.Value = (int)Math.Clamp(time * SeekBarResolution / length, 0, SeekBarResolution);
                _suppressSeekEvent = false;
            }

            // ⚠ 拖动期间不要用引擎时间覆盖"当前时间"：那一格显示的是用户要跳到的位置
            // （由 OnSeekScroll 写）。以前这里无条件写，计时器每 200 ms 就把它盖回去一次，
            // 表现就是"拖动时时间反复跳回真实播放进度"——两个写者互相盖。
            if (!_userSeeking)
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
            // 只有左键拖动才算数：右键点一下进度条不该顺手 seek 一次（它只是"停下"，不该改变播放位置）。
            if (e.Button != MouseButtons.Left) return;
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
