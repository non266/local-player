using System;
using System.Collections.Generic;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>播放控制：播放 / 暂停 / 上下曲、进度、音量、速率、循环、画面比例。</summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 播放控制
        // =====================================================================
        private void PlayButtonClicked()
        {
            if (_engine.HasMedia)
                _engine.Play();
            else
                PlayCurrentOrFirst();
        }

        private void TogglePlayPause()
        {
            if (!_engine.HasMedia)
            {
                PlayCurrentOrFirst();
                return;
            }

            _engine.TogglePlayPause();
        }

        private void PlayCurrentOrFirst()
        {
            if (_playlist.Count == 0)
            {
                OpenFilesDialog();
                return;
            }

            PlayIndex(_playlist.CurrentIndex < 0 ? 0 : _playlist.CurrentIndex);
        }

        /// <summary>
        /// 播放指定索引；若该项不可播放则自动顺延，整轮都不可用时停止。
        /// </summary>
        private void PlayIndex(int index)
        {
            if (_playlist.Count == 0) return;
            if (index < 0 || index >= _playlist.Count) index = 0;

            // 顺延时只记标记、不刷列表。
            // RefreshPlaylistView 是整体重建 ListView，放在这个循环里就会变成
            // "跳过一个重建一次"——整份列表都失效时要重建上千次，窗口直接假死。
            var marked = false;

            for (var offset = 0; offset < _playlist.Count; offset++)
            {
                var candidate = (index + offset) % _playlist.Count;
                var item = _playlist.Items[candidate];

                if (!MediaFormats.IsOpenable(item.FilePath))
                {
                    if (!item.HasError || item.ErrorMessage != MissingFileMessage)
                    {
                        item.HasError = true;
                        item.ErrorMessage = MissingFileMessage;
                        marked = true;
                    }
                    continue;
                }

                var wasMarked = item.HasError;

                item.HasError = false;
                item.ErrorMessage = null;

                try
                {
                    // 切歌之前先把上一首的进度落盘，然后排定这一首的续播位置。
                    CommitHistory();
                    ScheduleResume(item.FilePath);

                    _playlist.SetCurrent(candidate);

                    // 记录当前文件：音画/字幕延迟要按文件记住
                    _track.SetPath(item.FilePath);

                    AppLog.Info($"打开媒体：{item.FilePath}");

                    _engine.Open(item.FilePath);
                    UpdateWindowTitle();
                    HighlightPlayingItem();

                    // 上面的循环可能已经改过若干行的错误标记，这里统一刷一次。
                    if (marked || wasMarked) RefreshPlaylistView();

                    SetStatus("正在播放：" + item.DisplayName);

                    // 标签、歌词、封面、媒体信息都在这里刷新
                    LoadTrackMetadata(item.FilePath);
                    return;
                }
                catch (Exception ex)
                {
                    item.HasError = true;
                    item.ErrorMessage = ex.Message;
                    marked = true;
                }
            }

            if (marked) RefreshPlaylistView();

            StopPlayback();
            SetStatus("播放列表中没有可播放的媒体");
        }

        private void PlaySelectedItem()
        {
            var selected = GetSelectedPlaylistIndices();
            if (selected.Count == 0) return;
            PlayIndex(selected[0]);
        }

        private void PlayNext(bool userInitiated)
        {
            if (_playlist.Count == 0) return;

            if (_settings.Shuffle && _playlist.Count > 1)
            {
                PlayIndex(NextRandomIndex());
                return;
            }

            var next = _playlist.CurrentIndex + 1;
            if (next >= _playlist.Count)
            {
                if (userInitiated || _settings.RepeatMode == RepeatMode.All)
                {
                    next = 0;
                }
                else
                {
                    StopPlayback();
                    SetStatus("播放列表已结束");
                    return;
                }
            }

            PlayIndex(next);
        }

        private int NextRandomIndex()
        {
            if (_playlist.Count <= 1) return 0;

            int index;
            do
            {
                index = _random.Next(_playlist.Count);
            }
            while (index == _playlist.CurrentIndex);

            return index;
        }

        private void PlayPrevious()
        {
            if (_playlist.Count == 0) return;

            // 与常见播放器一致：播放超过 3 秒时，"上一个"先回到本曲开头。
            if (_engine.HasMedia && _engine.Time > 3000)
            {
                _engine.SeekTo(0);
                return;
            }

            var previous = _playlist.CurrentIndex - 1;
            if (previous < 0) previous = _playlist.Count - 1;
            PlayIndex(previous);
        }

        private void StopPlayback()
        {
            // 停之前先把当前文件的延迟记下来，否则这次调整就白做了
            RememberDelays();

            _engine.Stop();
            _track.SetPath(null);

            // 侧栏也要跟着清空：否则视频区已经回到"把文件拖到这里"，
            // 侧栏却还挂着上一首的歌词、专辑封面和媒体信息，自相矛盾。
            LoadTrackMetadata(null);

            UpdateWindowTitle();
            RefreshPlaylistView();
            UpdateTransportState();
            sidebarPanel.VideoTools.SetPlaybackAvailable(false);
            sidebarPanel.VideoTools.SetDelays(0, 0);
            SetStatus("已停止");
        }

        private void SeekRelative(long milliseconds)
        {
            if (!_engine.HasMedia) return;
            _engine.SeekBy(milliseconds);
            UpdateProgress();
        }

        private void ToggleMute() => SetMute(!_engine.Muted);

        internal void SetMute(bool muted)
        {
            _engine.Muted = muted;
            menuMute.Checked = muted;
            SetStatus(muted ? "已静音" : "已取消静音");
            UpdateVolumeLabel();
        }

        private void AdjustVolume(int delta)
        {
            if (_suspendUiEvents) return;

            var value = Math.Clamp(trackVolume.Value + delta, trackVolume.Minimum, trackVolume.Maximum);
            trackVolume.Value = value;
            ApplyVolume(value);

            if (value > 0 && _engine.Muted) SetMute(false);
        }

        private void ApplyVolume(int value)
        {
            _engine.Volume = value;
            _settings.Volume = value;
            UpdateVolumeLabel();
        }

        private void UpdateVolumeLabel()
        {
            lblVolumeValue.Text = _engine.Muted ? "静音" : trackVolume.Value + "%";
        }

        private void SetRate(float rate)
        {
            _engine.Rate = rate;
            _settings.Rate = _engine.Rate;
            ApplyRateToMenu(_engine.Rate);
            lblRate.Text = _engine.Rate.ToString("0.##") + "x";
            SetStatus("播放速度：" + _engine.Rate.ToString("0.##") + " 倍");
        }

        private void ApplyRateToMenu(float rate)
        {
            menuRateHalf.Checked = Math.Abs(rate - 0.5f) < 0.01f;
            menuRate075.Checked = Math.Abs(rate - 0.75f) < 0.01f;
            menuRateNormal.Checked = Math.Abs(rate - 1.0f) < 0.01f;
            menuRate125.Checked = Math.Abs(rate - 1.25f) < 0.01f;
            menuRate150.Checked = Math.Abs(rate - 1.5f) < 0.01f;
            menuRate200.Checked = Math.Abs(rate - 2.0f) < 0.01f;
            lblRate.Text = rate.ToString("0.##") + "x";
        }

        private void SetRepeatMode(RepeatMode mode)
        {
            _settings.RepeatMode = mode;
            ApplyRepeatModeToMenu(mode);

            switch (mode)
            {
                case RepeatMode.One:
                    SetStatus("循环模式：单曲循环");
                    break;
                case RepeatMode.All:
                    SetStatus("循环模式：列表循环");
                    break;
                default:
                    SetStatus("循环模式：不循环");
                    break;
            }
        }

        private void ApplyRepeatModeToMenu(RepeatMode mode)
        {
            menuRepeatNone.Checked = mode == RepeatMode.None;
            menuRepeatOne.Checked = mode == RepeatMode.One;
            menuRepeatAll.Checked = mode == RepeatMode.All;
        }

        private void SetAspectRatio(ToolStripMenuItem source)
        {
            var ratio = source.Tag as string ?? string.Empty;

            _engine.AspectRatio = ratio;
            _settings.AspectRatio = ratio;
            ApplyAspectMenu(ratio);

            SetStatus("画面比例：" + (string.IsNullOrEmpty(ratio) ? "默认" : source.Text));
        }

        private void ApplyAspectMenu(string ratio)
        {
            foreach (var item in GetAspectMenuItems())
            {
                var value = item.Tag as string ?? string.Empty;
                item.Checked = string.Equals(value, ratio ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }

        private IEnumerable<ToolStripMenuItem> GetAspectMenuItems()
        {
            yield return menuAspectDefault;
            yield return menuAspect169;
            yield return menuAspect43;
            yield return menuAspect185;
            yield return menuAspect235;
            yield return menuAspectOriginal;
        }
    }
}
