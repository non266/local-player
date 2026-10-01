using System;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>引擎事件：状态变化、播放结束、错误、轨道变化、随机播放开关。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 引擎事件
        // =====================================================================
        private void OnEngineStateChanged(object? sender, PlayerState state)
        {
            if (state == PlayerState.Playing)
            {
                _consecutiveErrors = 0;

                // libvlc 换媒体时会重建视频输出，画面调整 / 去隔行 / 延迟都会被重置，
                // 所以每次真正播起来都要重新推一遍。
                ApplyVideoSettingsToEngine();

                sidebarPanel.VideoTools.SetPlaybackAvailable(true);
            }

            if (state is PlayerState.Stopped or PlayerState.Ended or PlayerState.Error)
                sidebarPanel.VideoTools.SetPlaybackAvailable(false);

            // 任务栏缩略图上的按钮跟着切换播放/暂停图标。
            _taskbarButtons?.SetPlaying(state == PlayerState.Playing);

            // 暂停时立刻把进度落盘，避免强制结束丢掉位置。
            if (state == PlayerState.Paused) CommitHistory();

            UpdateTransportState();

            switch (state)
            {
                case PlayerState.Playing:
                    SetStatus("正在播放：" + (_playlist.Current?.DisplayName ?? string.Empty));
                    break;
                case PlayerState.Paused:
                    SetStatus("已暂停");
                    break;
                case PlayerState.Opening:
                    SetStatus("正在打开…");
                    break;
                case PlayerState.Stopped:
                    SetStatus("已停止");
                    break;
                case PlayerState.Ended:
                    SetStatus("播放结束");
                    break;
                case PlayerState.Error:
                    SetStatus("播放出错");
                    break;
            }
        }

        private void OnEnginePlaybackEnded(object? sender, EventArgs e)
        {
            if (_settings.RepeatMode == RepeatMode.One)
            {
                _engine.Replay();
                return;
            }

            PlayNext(false);
        }

        private void OnEngineError(object? sender, string message)
        {
            SetStatus("播放错误：" + message);

            var item = _playlist.Current;
            if (item != null)
            {
                item.HasError = true;
                item.ErrorMessage = message;
                RefreshPlaylistView();
            }

            // 连续失败时自动跳过，但设上限，避免整列表都是坏文件时无限跳。
            if (++_consecutiveErrors <= MaxConsecutiveErrors && _playlist.Count > 1)
            {
                _skipTimer.Stop();
                _skipTimer.Start();
            }
        }

        private void OnSkipTimerTick(object? sender, EventArgs e)
        {
            _skipTimer.Stop();
            PlayNext(false);
        }

        private void OnEngineTracksChanged(object? sender, EventArgs e) => RebuildTrackMenus();

        private void OnShuffleChanged(object? sender, EventArgs e)
        {
            if (_suspendUiEvents) return;

            _settings.Shuffle = menuShuffle.Checked;
            SetStatus(menuShuffle.Checked ? "已开启随机播放" : "已关闭随机播放");
        }
    }
}
