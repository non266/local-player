using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 播放控制的<b>界面那一半</b>：播放 / 暂停 / 上一首 / 下一首按钮、进度、音量、速率、
    /// A-B 循环菜单、音量均衡。
    /// <para>
    /// "放哪一首、播完怎么办、顺延跳过打不开的项、随机 / 循环 / 插播 / 续播 / A-B 的回跳"
    /// 都在 <see cref="PlaybackCoordinator"/>（<c>Core</c>）里；这里只把它的结果画出来、
    /// 把用户的操作转给它。事件接线在本文件顶部的 <see cref="AttachPlaybackEvents"/>。
    /// </para>
    /// </summary>
    public partial class MainForm : Form
    {
        // =====================================================================
        // 与播放业务的接线
        // =====================================================================

        private void AttachPlaybackEvents()
        {
            _playback.Opening += OnPlaybackOpening;
            _playback.Opened += OnPlaybackOpened;
            _playback.ListInvalidated += (s, e) => RefreshPlaylistView();
            _playback.StatusChanged += (s, text) => SetStatus(text);
            _playback.QueueChanged += OnPlaybackQueueChanged;
            _playback.Stopping += (s, e) => RememberDelays();
            _playback.Stopped += OnPlaybackStopped;
            _playback.ResumeApplied += OnPlaybackResumeApplied;
        }

        /// <summary>马上要打开这一份：先把上一首的进度落盘，再把按文件记住的东西摆好。</summary>
        private void OnPlaybackOpening(object? sender, PlaybackOpeningEventArgs e)
        {
            CommitHistory();

            // 记录当前文件：音画/字幕延迟与画面旋转要按文件记
            _track.SetPath(e.Path);
            PrepareTrackRestore(e.Path);

            // 画面旋转是内核级选项（libvlc 3 的 transform 只能在实例级设）：
            // 必须在创建媒体之前摆好，开播时那个内核不一致就换内核（换片自动恢复）。
            _engine.Rotation = Adjustments.Get(e.Path).Rotation;

            // 网络流只记 scheme://host/…，口令 / 一次性参数不进日志（1.21.0）。
            AppLog.Info($"打开媒体：{MediaFormats.DescribeForLog(e.Path)}");
        }

        /// <summary>已经交给引擎：标题、列表高亮、状态栏、标签 / 歌词 / 封面都从这里刷。</summary>
        private void OnPlaybackOpened(object? sender, PlaybackOpenedEventArgs e)
        {
            UpdateWindowTitle();
            HighlightPlayingItem();

            // 加粗那一行要单独挪一次：上面 SetCurrent 触发的整体重建发生在 Open 之前，
            // 那会儿引擎路径还是上一首，加粗就画在上一首上。
            UpdateNowPlayingRow();

            SetStatus("正在播放：" + e.DisplayName + QueueStatusSuffix());

            // 标签、歌词、封面、媒体信息都在这里刷新
            LoadTrackMetadata(e.Path);
        }

        /// <summary>停下来了：清除上一首留下的侧栏 / 标题 / 系统媒体控件。</summary>
        private void OnPlaybackStopped(object? sender, EventArgs e)
        {
            _track.SetPath(null);

            // 侧栏也要跟着清空：否则视频区已经回到"把文件拖到这里"，
            // 侧栏却还挂着上一首的歌词、专辑封面和媒体信息，自相矛盾。
            LoadTrackMetadata(null);

            // 系统媒体控件那边也不再挂着上一首（音量弹窗里那一块清空）
            _smtc?.Clear();

            UpdateWindowTitle();
            RefreshPlaylistView();
            UpdateTransportState();
            sidebarPanel.VideoTools.SetPlaybackAvailable(false);
            sidebarPanel.VideoTools.SetDelays(0, 0);
            SetStatus("已停止");
        }

        private void OnPlaybackQueueChanged(object? sender, EventArgs e)
        {
            MarkQueuedItems();
            RefreshQueueMenu();
        }

        private void OnPlaybackResumeApplied(object? sender, TimeSpan position)
        {
            var name = _playlist.Current?.DisplayName ?? Path.GetFileName(_engine.CurrentPath ?? string.Empty);
            SetStatus($"已从 {TimeFormatter.Format(position)} 继续播放：{name}");
        }

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

            _playback.PlayCurrentOrFirst();
        }

        /// <summary>播放指定索引（会同步播放列表的当前项）。</summary>
        private void PlayIndex(int index) => _playback.PlayIndex(index);

        /// <summary>播放指定索引；<paramref name="keepCurrent"/> 见协调器说明。</summary>
        private void PlayIndex(int index, bool keepCurrent) => _playback.PlayIndex(index, keepCurrent);

        /// <param name="allowFallback">失败时要不要顺延到下一项（用户明确点开的那一项传 <c>false</c>）。</param>
        private void PlayIndex(int index, bool keepCurrent, bool allowFallback) =>
            _playback.PlayIndex(index, keepCurrent, allowFallback);

        private void PlaySelectedItem()
        {
            var selected = GetSelectedPlaylistIndices();
            if (selected.Count == 0) return;
            PlayIndex(selected[0]);
        }

        private void PlayNext(bool userInitiated) => _playback.PlayNext(userInitiated);

        private void PlayPrevious() => _playback.PlayPrevious();

        private void StopPlayback() => _playback.Stop();

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

        // =====================================================================
        // A-B 循环（菜单与状态文案；两个点与回跳在 PlaybackCoordinator.AbLoop.cs）
        // =====================================================================

        private ToolStripMenuItem? _menuAbLoop;

        private ToolStripMenuItem? _menuAbLoopState;

        private ToolStripMenuItem? _menuAbSetStart;

        private ToolStripMenuItem? _menuAbSetEnd;

        private ToolStripMenuItem? _menuAbClear;

        /// <summary>只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。</summary>
        internal long? AbLoopStartMilliseconds => _playback.AbLoopStartMilliseconds;

        internal long? AbLoopEndMilliseconds => _playback.AbLoopEndMilliseconds;

        internal bool AbLoopActive => _playback.AbLoopActive;

        /// <summary>给冒烟测试按 <c>Name</c> 找菜单项：文字里带着"当前时间"，会随播放变化。</summary>
        internal ToolStripMenuItem? MenuAbLoop => _menuAbLoop;

        private void ConfigureAbLoopMenu()
        {
            _menuAbLoop = new ToolStripMenuItem("A-B 循环");

            // 第一行是"现在是什么状态"（只读）：设过之后在菜单里就直接读得出当前 A/B，
            // 不必靠记、也不必专门去看状态栏。
            _menuAbLoopState = new ToolStripMenuItem("未设 A-B")
            {
                Name = "menuAbLoopState",
                Enabled = false
            };
            _menuAbLoop.DropDownItems.Add(_menuAbLoopState);
            _menuAbLoop.DropDownItems.Add(new ToolStripSeparator());

            _menuAbSetStart = new ToolStripMenuItem("把 A 点设在当前时间") { Name = "menuAbSetStart" };
            _menuAbSetStart.Click += (s, e) => SetAbLoopStart();
            _menuAbLoop.DropDownItems.Add(_menuAbSetStart);

            _menuAbSetEnd = new ToolStripMenuItem("把 B 点设在当前时间") { Name = "menuAbSetEnd" };
            _menuAbSetEnd.Click += (s, e) => SetAbLoopEnd();
            _menuAbLoop.DropDownItems.Add(_menuAbSetEnd);

            _menuAbLoop.DropDownItems.Add(new ToolStripSeparator());

            _menuAbClear = new ToolStripMenuItem("清除 A-B") { Name = "menuAbClear" };
            _menuAbClear.Click += (s, e) => ClearAbLoopByUser();
            _menuAbLoop.DropDownItems.Add(_menuAbClear);

            // "当前时间是多少"只有下拉那一刻才知道，所以每次都重写一遍文字。
            _menuAbLoop.DropDownOpening += (s, e) => RefreshAbLoopMenu();

            menuPlayback.DropDownItems.Add(_menuAbLoop);
        }

        private void RefreshAbLoopMenu()
        {
            if (_menuAbLoopState == null) return;

            _menuAbLoopState.Text = "当前：" + AbLoop.Describe(
                _playback.AbLoopStartMilliseconds, _playback.AbLoopEndMilliseconds);

            var hasMedia = _engine.HasMedia;
            var now = hasMedia ? AbLoop.Describe(_engine.Time) : string.Empty;

            if (_menuAbSetStart != null)
            {
                _menuAbSetStart.Enabled = hasMedia;
                _menuAbSetStart.Text = hasMedia ? "把 A 点设在当前时间（" + now + "）" : "把 A 点设在当前时间";
            }

            if (_menuAbSetEnd != null)
            {
                _menuAbSetEnd.Enabled = hasMedia;
                _menuAbSetEnd.Text = hasMedia ? "把 B 点设在当前时间（" + now + "）" : "把 B 点设在当前时间";
            }

            if (_menuAbClear != null)
                _menuAbClear.Enabled = _playback.HasAbLoopStart || _playback.HasAbLoopEnd;
        }

        /// <summary>「把 A 点设在当前时间」。A 不早于 B 时如实拒绝并说明原因。</summary>
        private void SetAbLoopStart()
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一段媒体，再把 A 点设在当前时间");
                return;
            }

            var reject = _playback.SetAbLoopStart(_engine.Time);
            if (reject != null)
            {
                SetStatus("A-B 循环：" + reject);
                return;
            }

            RefreshAbLoopMenu();
            SetStatus("A-B 循环：" + DescribeAbLoop());
        }

        /// <summary>「把 B 点设在当前时间」。B 不晚于 A 时同样如实拒绝。</summary>
        private void SetAbLoopEnd()
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一段媒体，再把 B 点设在当前时间");
                return;
            }

            var reject = _playback.SetAbLoopEnd(_engine.Time);
            if (reject != null)
            {
                SetStatus("A-B 循环：" + reject);
                return;
            }

            RefreshAbLoopMenu();
            SetStatus("A-B 循环：" + DescribeAbLoop());
        }

        private string DescribeAbLoop()
        {
            if (_playback.AbLoopActive)
            {
                return AbLoop.Describe(_playback.AbLoopStartMilliseconds, _playback.AbLoopEndMilliseconds)
                       + "，播放到 B 就跳回 A";
            }

            return _playback.HasAbLoopStart
                ? "A " + AbLoop.Describe(_playback.AbLoopStartMilliseconds ?? 0) + "（B 点还没设）"
                : "B " + AbLoop.Describe(_playback.AbLoopEndMilliseconds ?? 0) + "（A 点还没设）";
        }

        /// <summary>用户点了「清除 A-B」。</summary>
        private void ClearAbLoopByUser()
        {
            if (!_playback.HasAbLoopStart && !_playback.HasAbLoopEnd)
            {
                SetStatus("当前没有设 A-B 循环点");
                return;
            }

            _playback.ClearAbLoop();
            RefreshAbLoopMenu();
            SetStatus("已清除 A-B 循环点");
        }

        // =====================================================================
        // 音量均衡（normalize）
        // =====================================================================

        private ToolStripMenuItem? _menuNormalizeVolume;

        /// <summary>只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。</summary>
        internal ToolStripMenuItem? MenuNormalizeVolume => _menuNormalizeVolume;

        private void ConfigureNormalizeMenu()
        {
            _menuNormalizeVolume = new ToolStripMenuItem("音量均衡（normalize）")
            {
                Name = "menuNormalizeVolume",
                CheckOnClick = true,
                ToolTipText = "把忽大忽小的音量拉平。这是媒体级滤镜：切换会重载当前这一首"
            };

            _menuNormalizeVolume.CheckedChanged += (s, e) => SetNormalize(_menuNormalizeVolume.Checked);

            menuPlayback.DropDownItems.Add(_menuNormalizeVolume);
        }

        /// <summary>
        /// 开关音量均衡。
        /// <para>
        /// ⚠ 它是<b>媒体级</b>滤镜（<c>:audio-filter=normvol</c>）：改设置不影响已经打开的媒体，
        /// 所以这里直接把当前这一首<b>重载</b>一遍（从头开始），并在状态栏把这件事说出来。
        /// </para>
        /// </summary>
        private void SetNormalize(bool on)
        {
            _settings.NormalizeVolume = on;
            _engine.NormalizeVolume = on;

            // 铺设置 / 载入设置方案时不许顺手重载媒体：那是批量赋值，
            // 而且"当前这一首"可能马上就要换，这里再叠一次重载纯属白干。
            if (_suspendUiEvents) return;

            if (!_playback.ReloadCurrent())
            {
                SetStatus(on ? "已开启音量均衡（下次播放生效）" : "已关闭音量均衡（下次播放生效）");
                return;
            }

            SetStatus(on
                ? "已开启音量均衡：当前这一首已从头开始重播以应用"
                : "已关闭音量均衡：当前这一首已从头开始重播以应用");
        }
    }
}
