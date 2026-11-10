using System;
using System.Drawing;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>界面计时器：进度条与音量条的同步、拖动进度时的行为。</summary>
    public partial class MainForm : Form
    {
        /// <summary>测试用：进度条现在能不能拖（DVD 菜单上是关着的）。</summary>
        internal bool SeekBarEnabled => trackSeek.Enabled;

        /// <summary>
        /// 界面计时器跳了几拍（冒烟测试用）。
        /// <para>
        /// 探索针 / 扫描是不是把界面拖住了，最直接的判据就是"这段时间计时器还在不在跳"——
        /// 比"操作耗时小于 N 毫秒"稳（后者的 N 没法定）。
        /// </para>
        /// </summary>
        internal int UiTickCount { get; private set; }

        // =====================================================================
        // 进度条与音量条
        // =====================================================================
        private void OnUiTimerTick(object? sender, EventArgs e)
        {
            UiTickCount++;

            // 网络路径的存在性检查在后台跑，回到这里收尾：事件、引擎调用都留在界面线程。
            _playback.PumpPendingProbe();

            // 扫描进度（「取消扫描 · 已找到 N 项」）跟着这一拍刷：后台只动计数，不 Post。
            UpdateScanProgress();

            // 时长扫描逐条置位"待更新"，这里按计时器节流统一重算一次标题里的总时长。
            if (_playlistSummaryDirty)
            {
                _playlistSummaryDirty = false;
                UpdatePlaylistSummary();
            }

            UpdateProgress();

            // 光盘的位置说明（"菜单" / "标题 2 / 12 · 第 3 章"）只在变化时刷一次状态栏。
            UpdateDiscStatus();

            // 音频窗口里顺手对一下卡片尺寸：事件顺序不可控（启动就播、最大化后还原、换 DPI），
            // 尺寸已经对的时候这里只做一次比较（见 docs/1.10.0计划.md 的 B9）
            HealAudioCardLayout();

            // 睡眠定时器到点就在这里停：用现成的 200 ms 计时器，不另起一个（见 MainForm.SleepTimer.cs）。
            CheckSleepTimer();
        }

        private void UpdateProgress()
        {
            if (!_engine.HasMedia) return;

            // 1.16.0：DVD 菜单自己那条 title 只有几百毫秒（本机那张盘 466 ms），按"进度"画出来
            // 会一直是 0:00、拖到哪儿都在菜单里跳，看着像坏了。菜单上没有"进度"可言：
            // 时间标签与进度条一起置灰，等进了正片再恢复。
            if (IsDiscMenuOnScreen)
            {
                if (trackSeek.Enabled) trackSeek.Enabled = false;

                lblCurrentTime.Text = "--:--:--";
                lblTotalTime.Text = "--:--:--";
                _surfaces.UpdateProgress(0, 0);
                return;
            }

            if (!trackSeek.Enabled) trackSeek.Enabled = true;

            var length = _engine.Length;
            var time = _engine.Time;

            // 续播定位与 A-B 循环都在播放业务里：续播要等"媒体播起来 + 总时长已知"，
            // 越过 B 点才跳回 A 点（拖动进度条时不掺和——那会儿用户正捏着位置）。
            time = _playback.Tick(time, length, _userSeeking);

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

            // 全屏时那条浮在画面底部的进度条（1.11.0 F14）跟这里同步：
            // 同一次算出来的时间与长度，不另起一套计时（不在全屏 / 没建出来时它自己会忽略）。
            _surfaces.UpdateProgress(time, length);

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
