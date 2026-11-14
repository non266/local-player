using System;
using System.Windows.Forms;
using Windows.Media;
using 播放器.Core;
using 播放器.Ui;

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
            // 1.21.0：每次新的"打开"都从零计时。不能只靠界面计时器里那次复位——
            // 用户"按停止、马上再点播放"时，两次状态变化可能落在同一拍计时器里，
            // 上一次的"已经提示过"就会被带进这一次（冒烟里就是这么抓到的）。
            if (state == PlayerState.Opening) _slowOpenWatch.Reset();

            if (state == PlayerState.Playing)
            {
                // 1.21.0：刚才要是提示过"网络流还没播起来"，把实际等了多久记进日志（不含地址）。
                LogSlowOpenRecovered();

                // 一次真的播起来了：之前那串失败不再算"连续"。
                _playback.ResetErrorCount();

                // libvlc 换媒体时会重建视频输出，画面调整 / 去隔行 / 延迟都会被重置，
                // 所以每次真正播起来都要重新推一遍。
                ApplyVideoSettingsToEngine();

                sidebarPanel.VideoTools.SetPlaybackAvailable(true);
            }

            if (state is PlayerState.Stopped or PlayerState.Ended or PlayerState.Error)
                sidebarPanel.VideoTools.SetPlaybackAvailable(false);

            // 「画面旋转」那一组按"有没有画面"来置灰：音频文件点了也没用
            RefreshRotationMenu();

            // 系统媒体控件那边的播放状态（音量弹窗里那个图标）
            _smtc?.SetStatus(state switch
            {
                PlayerState.Playing => MediaPlaybackStatus.Playing,
                PlayerState.Paused => MediaPlaybackStatus.Paused,
                PlayerState.Error => MediaPlaybackStatus.Closed,
                _ => MediaPlaybackStatus.Stopped
            });

            // 任务栏缩略图上的按钮：图标与置灰由下面的 UpdateTransportState 统一推，
            // 不再单独 SetPlaying（两处各推一次迟早对不上）。

            // 暂停时立刻把进度落盘，避免强制结束丢掉位置。
            if (state == PlayerState.Paused) CommitHistory();

            UpdateTransportState();

            switch (state)
            {
                case PlayerState.Playing:
                    // 按"引擎真正在播的那一份"报，而不是列表的当前项：插播时两者不是同一项。
                    SetStatus("正在播放：" + NowPlayingDisplayName() + QueueStatusSuffix());

                    // 1.16.0：停在 DVD 菜单上时，方向键现在归 libvlc，状态栏得把这件事说出来
                    AnnounceDiscMenuIfNeeded();
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

            // 睡眠定时器到点那句话压过上面的通用文案（"已暂停 / 已停止"）——不然用户只看到
            // "已暂停"，不知道是谁让他停的。只说一次。
            ApplySleepTimerMessageToStatus();
        }

        private void OnEnginePlaybackEnded(object? sender, EventArgs e)
        {
            // 「听完这一首就停」优先于循环与插播：用户要的就是"这一首放完就别再响了"。
            if (ApplySleepTimerAtTrackEnd()) return;

            // 单曲循环 / 插播队列 / 下一首（含末尾停住）都在播放业务里，
            // 界面只负责先判睡眠定时器。
            _playback.HandleEnded();
        }

        private void OnEngineError(object? sender, string message)
        {
            // 1.16.0：DVD 菜单打不开时先就地退回主标题（同一张盘只退一次）。
            // 这条错误是"这张盘的菜单这条路不通"，不是"文件坏了"，不该标红、也不该自动跳过。
            if (TryFallBackDiscMenu(message)) return;

            SetStatus("播放错误：" + message);

            var item = _playlist.Current;
            if (item != null)
            {
                item.HasError = true;
                item.ErrorMessage = message;
                RefreshPlaylistView();
            }

            // 连续失败时自动跳过，但设上限，避免整列表都是坏文件时无限跳。
            _playback.RegisterError();

            if (_playback.ShouldAutoSkipAfterError())
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

        private void OnEngineTracksChanged(object? sender, EventArgs e)
        {
            RebuildTrackMenus();

            // 轨道变了（换片、字幕加载）"有没有视频轨"也可能变了
            RefreshRotationMenu();

            // 1.10.0 F11′：轨道清单出来了，按**真实轨道**纠正一次窗口形态
            // （扩展名只是解析之前的猜测；只有音轨的 mkv 就是靠这里才切进音频窗口的）
            SyncWindowShapeFromTracks();
        }

        private void OnShuffleChanged(object? sender, EventArgs e)
        {
            if (_suspendUiEvents) return;

            _settings.Shuffle = menuShuffle.Checked;
            SetStatus(menuShuffle.Checked ? "已开启随机播放" : "已关闭随机播放");
        }
    }
}
