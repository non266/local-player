using System;
using System.Windows.Forms;
using 播放器.Core;
using 播放器.Ui;

namespace 播放器
{
    /// <summary>
    /// 视频播放相关：画面调整、去隔行、音画与字幕延迟、逐帧步进。
    /// <para>
    /// 这些参数大多只对"当前这个视频"有意义（尤其是延迟），所以延迟按文件记在播放历史里，
    /// 换片自动恢复；画面调整与去隔行则是全局偏好，存在设置文件里。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        /// <summary>音频延迟每次调整的步长（毫秒）。</summary>
        private const long AudioDelayStepMilliseconds = 50;

        /// <summary>字幕延迟每次调整的步长（毫秒）。字幕比音频更需要粗调。</summary>
        private const long SubtitleDelayStepMilliseconds = 250;

        /// <summary>逐帧前等待"暂停"真正生效的时间（毫秒）。</summary>
        private const int FrameStepSettleMilliseconds = 180;
        private ToolStripMenuItem? _menuDeinterlace;
        private ToolStripMenuItem? _menuSync;
        private ToolStripMenuItem? _menuFrameStep;
        private ToolStripMenuItem? _menuRotation;

        private System.Windows.Forms.Timer? _frameStepTimer;
        private bool _pendingFrameStep;
        private bool _pendingFrameForward;
        private int _frameStepWaits;

        // =====================================================================
        // 初始化
        // =====================================================================

        private void ConfigureVideoMenus()
        {
            ConfigureDeinterlaceMenu();
            ConfigureRotationMenu();
            ConfigureSyncMenu();
            ConfigureFrameStepMenu();
            WireVideoToolsView();
        }

        /// <summary>
        /// 「视图 → 画面旋转」：不旋转 / 顺时针 90° / 180° / 逆时针 90° / 水平翻转 / 垂直翻转。
        /// <para>
        /// 按文件记住（和音画延迟一样），而且是<b>媒体级</b>滤镜：切换要重载当前这一首，
        /// 但位置保持——旋转多半是"看着不对才去调"的，把进度丢掉会很难受。
        /// </para>
        /// </summary>
        private void ConfigureRotationMenu()
        {
            _menuRotation = new ToolStripMenuItem("画面旋转");

            foreach (var (label, value) in Core.ScreenRotations.All)
            {
                var item = new ToolStripMenuItem(label) { Tag = value, Name = "menuRotation" + value };
                item.Click += (s, e) => SetRotation((ScreenRotation)item.Tag!);
                _menuRotation.DropDownItems.Add(item);
            }

            InsertIntoViewMenu(_menuRotation, menuViewAspect);
        }

        /// <summary>只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。</summary>
        internal ToolStripMenuItem? MenuRotation => _menuRotation;

        /// <summary>建一个时长扫描器接在当前内核上（换内核之后要重来一遍）。</summary>
        private DurationScanner CreateScanner()
        {
            var scanner = new DurationScanner(_engine.LibVlc);
            scanner.DurationFound += OnDurationFound;

            return scanner;
        }

        private void RebuildScanner() => _scanner = CreateScanner();

        /// <summary>
        /// 引擎换了内核（换画面旋转时，libvlc 3 的 <c>transform</c> 只能在内核级设）：
        /// 把窗口与时长扫描改挂到新播放器 / 新内核上，并把音量 / 静音 / 速率 / 均衡器重新铺一遍
        /// ——那些状态挂在<b>播放器</b>上，换了播放器就没了。
        /// </summary>
        private void OnEngineCoreRebuilt()
        {
            videoView.MediaPlayer = _engine.Player;

            _scanner.DurationFound -= OnDurationFound;
            _scanner.Dispose();
            RebuildScanner();

            _engine.Volume = _settings.Volume;
            _engine.Muted = _settings.Muted;
            _engine.Rate = _settings.Rate <= 0 ? 1.0f : _settings.Rate;
            ApplyEqualizer();

            UpdateVolumeLabel();

            AppLog.Info("换了 libvlc 内核以应用画面旋转。");
        }

        /// <summary>
        /// 画面旋转 / 翻转。<b>按文件记住</b>，改完重载当前这一首才生效（媒体级滤镜）。
        /// </summary>
        private void SetRotation(ScreenRotation rotation)
        {
            if (_track.Path is not { Length: > 0 } path)
            {
                SetStatus("请先播放一个视频，再调整画面旋转");
                return;
            }

            // 音频文件没有画面：如实说一句，别记一条用不上的设置
            if (!_engine.HasVideo && rotation != _engine.Rotation)
            {
                SetStatus("这个文件没有画面（音频），旋转对它没有作用");
                return;
            }

            Adjustments.RecordRotation(path, rotation);

            _engine.Rotation = rotation;
            ApplyRotationMenu(rotation);

            var position = _engine.Time;

            PlayIndex(_playlist.CurrentIndex);

            // 重载之后回到原来的位置（界面计时器会在媒体真的播起来之后定位）。
            _pendingResume = position > 0 ? TimeSpan.FromMilliseconds(position) : (TimeSpan?)null;

            SetStatus($"画面旋转：{Core.ScreenRotations.Describe(rotation)}"
                      + $"（已重新起播以应用，从 {TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(position))} 继续）");
        }

        private void ApplyRotationMenu(ScreenRotation rotation)
        {
            if (_menuRotation == null) return;

            foreach (ToolStripItem item in _menuRotation.DropDownItems)
            {
                if (item is ToolStripMenuItem menuItem && menuItem.Tag is ScreenRotation value)
                    menuItem.Checked = value == rotation;
            }
        }

        /// <summary>
        /// 没有画面（音频、或者还没开始播）时整组点不动：旋转对它是空操作，
        /// 点了没反应只会让人以为功能坏了。
        /// </summary>
        private void RefreshRotationMenu()
        {
            if (_menuRotation == null) return;

            _menuRotation.Enabled = _engine.HasMedia && _engine.HasVideo;
            ApplyRotationMenu(_engine.Rotation);
        }

        private void ConfigureDeinterlaceMenu()
        {
            _menuDeinterlace = new ToolStripMenuItem("去隔行");

            foreach (var (label, value) in Core.DeinterlaceModes.All)
            {
                var item = new ToolStripMenuItem(label) { Tag = value };
                item.Click += (s, e) => SetDeinterlace((string)item.Tag!);
                _menuDeinterlace.DropDownItems.Add(item);
            }

            InsertIntoViewMenu(_menuDeinterlace, menuViewAspect);
        }

        private void ConfigureSyncMenu()
        {
            _menuSync = new ToolStripMenuItem("音画同步");

            AddMenuItem(_menuSync, "音频延迟 −50 ms", "G", () => StepDelay(audio: true, direction: -1));
            AddMenuItem(_menuSync, "音频延迟 +50 ms", "H", () => StepDelay(audio: true, direction: 1));
            _menuSync.DropDownItems.Add(new ToolStripSeparator());
            AddMenuItem(_menuSync, "字幕延迟 −250 ms", "J", () => StepDelay(audio: false, direction: -1));
            AddMenuItem(_menuSync, "字幕延迟 +250 ms", "K", () => StepDelay(audio: false, direction: 1));
            _menuSync.DropDownItems.Add(new ToolStripSeparator());
            AddMenuItem(_menuSync, "延迟归零", string.Empty, ResetDelays);

            menuPlayback.DropDownItems.Add(_menuSync);
        }

        private void ConfigureFrameStepMenu()
        {
            _menuFrameStep = new ToolStripMenuItem("逐帧");

            AddMenuItem(_menuFrameStep, "下一帧", "E", () => StepFrame(true));
            AddMenuItem(_menuFrameStep, "上一帧", "Shift+E", () => StepFrame(false));
            _menuFrameStep.DropDownItems.Add(new ToolStripSeparator());
            AddMenuItem(_menuFrameStep, "跳转到时间码…", string.Empty, JumpToTimeCode);

            menuPlayback.DropDownItems.Add(_menuFrameStep);
        }

        /// <summary>
        /// 把去隔行与"按文件记住的延迟"推给引擎。
        /// <para>libvlc 在换媒体时会重建视频输出并把这些状态重置，所以每次开播都要重设一遍。</para>
        /// </summary>
        private void ApplyVideoSettingsToEngine()
        {
            if (!string.IsNullOrEmpty(_settings.Deinterlace))
                _engine.SetDeinterlace(_settings.Deinterlace);

            RestoreDelaysForCurrentMedia();
        }

        /// <summary>往 <paramref name="anchor"/> 后面插一项，让画面相关的菜单聚在一起。</summary>
        private void InsertIntoViewMenu(ToolStripMenuItem item, ToolStripMenuItem anchor)
        {
            var index = menuView.DropDownItems.IndexOf(anchor);
            if (index < 0)
                menuView.DropDownItems.Add(item);
            else
                menuView.DropDownItems.Insert(index + 1, item);
        }

        private static void AddMenuItem(ToolStripMenuItem parent, string text, string shortcut, Action action)
        {
            var item = new ToolStripMenuItem(text);

            if (!string.IsNullOrEmpty(shortcut))
                item.ShortcutKeyDisplayString = shortcut;

            item.Click += (s, e) => action();
            parent.DropDownItems.Add(item);
        }

        private void WireVideoToolsView()
        {
            var tools = sidebarPanel.VideoTools;

            tools.SetDeinterlaceMode(_settings.Deinterlace);

            tools.DeinterlaceChanged += (s, mode) => SetDeinterlace(mode);
            tools.FrameStepRequested += (s, forward) => StepFrame(forward);
            tools.TimeCodeRequested += (s, e) => JumpToTimeCode();
            tools.DelayStepRequested += (s, e) => StepDelay(e.Audio, e.Direction);
            tools.ResetDelaysRequested += (s, e) => ResetDelays();
        }

        // =====================================================================
        // 去隔行
        // =====================================================================

        private void SetDeinterlace(string? mode)
        {
            _settings.Deinterlace = mode ?? string.Empty;

            _engine.SetDeinterlace(_settings.Deinterlace);
            sidebarPanel.VideoTools.SetDeinterlaceMode(_settings.Deinterlace);

            if (_menuDeinterlace != null)
            {
                foreach (ToolStripItem item in _menuDeinterlace.DropDownItems)
                {
                    if (item is ToolStripMenuItem menuItem)
                        menuItem.Checked = string.Equals(
                            menuItem.Tag as string ?? string.Empty,
                            _settings.Deinterlace,
                            StringComparison.OrdinalIgnoreCase);
                }
            }

            SetStatus(_settings.Deinterlace.Length == 0
                ? "已关闭去隔行"
                : "去隔行：" + Core.DeinterlaceModes.Describe(_settings.Deinterlace));
        }

        // =====================================================================
        // 音画与字幕延迟
        // =====================================================================

        private void StepDelay(bool audio, int direction)
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一个视频，再调整同步");
                return;
            }

            var step = audio ? AudioDelayStepMilliseconds : SubtitleDelayStepMilliseconds;
            var current = audio ? _engine.AudioDelayMilliseconds : _engine.SubtitleDelayMilliseconds;

            SetDelay(audio, current + direction * step);
        }

        private void SetDelay(bool audio, long milliseconds)
        {
            if (audio)
                _engine.SetAudioDelayMilliseconds(milliseconds);
            else
                _engine.SetSubtitleDelayMilliseconds(milliseconds);

            // 读回真实值：libvlc 可能不接受某些数值，以它为准显示才不会骗人
            var applied = audio ? _engine.AudioDelayMilliseconds : _engine.SubtitleDelayMilliseconds;

            UpdateDelayDisplay();
            RememberDelays();
            RefreshInfoPanel();

            SetStatus((audio ? "音频延迟 " : "字幕延迟 ") + TimeFormatter.FormatMilliseconds(applied));
        }

        private void ResetDelays()
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一个视频，再调整同步");
                return;
            }

            _engine.SetAudioDelayMilliseconds(0);
            _engine.SetSubtitleDelayMilliseconds(0);

            UpdateDelayDisplay();
            RememberDelays();
            RefreshInfoPanel();

            SetStatus("音频与字幕延迟已归零");
        }

        private void UpdateDelayDisplay() =>
            sidebarPanel.VideoTools.SetDelays(_engine.AudioDelayMilliseconds, _engine.SubtitleDelayMilliseconds);

        /// <summary>把当前延迟记到正在播放的文件名下（顺带落盘，见 <see cref="PerFileAdjustments"/>）。</summary>
        private void RememberDelays()
        {
            if (_track.Path is not { Length: > 0 } path) return;

            Adjustments.RecordDelays(
                path, _engine.AudioDelayMilliseconds, _engine.SubtitleDelayMilliseconds);
        }

        /// <summary>换片之后恢复这个文件上次用的延迟。libvlc 会在换媒体时把延迟清零。</summary>
        private void RestoreDelaysForCurrentMedia()
        {
            var saved = Adjustments.Get(_track.Path);

            if (saved.AudioDelayMilliseconds != 0) _engine.SetAudioDelayMilliseconds(saved.AudioDelayMilliseconds);
            if (saved.SubtitleDelayMilliseconds != 0) _engine.SetSubtitleDelayMilliseconds(saved.SubtitleDelayMilliseconds);

            UpdateDelayDisplay();
        }

        // =====================================================================
        // 逐帧
        // =====================================================================

        private void StepFrame(bool forward)
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一个视频，再逐帧查看");
                return;
            }

            // libvlc 的 NextFrame 只在暂停状态下有效，而"暂停"是异步生效的：
            // 正在播放时立刻调用多半会被丢掉，所以先暂停、稍等一下再走这一帧。
            if (_engine.IsPlaying)
            {
                _engine.Pause();

                _pendingFrameStep = true;
                _pendingFrameForward = forward;
                _frameStepWaits = 0;

                _frameStepTimer ??= CreateFrameStepTimer();
                _frameStepTimer.Stop();
                _frameStepTimer.Start();
                return;
            }

            DoStepFrame(forward);
        }

        private void DoStepFrame(bool forward)
        {
            if (!_engine.StepFrame(forward))
            {
                SetStatus("这个视频不支持逐帧");
                return;
            }

            UpdateLyricsPosition();

            var time = TimeSpan.FromMilliseconds(_engine.Time);
            var fps = _engine.FrameRate;

            SetStatus(fps > 0
                ? $"逐帧（{fps:0.##} fps）：{TimeFormatter.FormatWithHours(time)}"
                : "逐帧：" + TimeFormatter.FormatWithHours(time));
        }

        private System.Windows.Forms.Timer CreateFrameStepTimer()
        {
            var timer = new System.Windows.Forms.Timer { Interval = FrameStepSettleMilliseconds };

            timer.Tick += (s, e) =>
            {
                if (!_pendingFrameStep)
                {
                    timer.Stop();
                    return;
                }

                // 暂停是异步生效的：还没停稳就继续等，最多等 2 秒。
                // 不等的话，走帧会在播放状态下发生，画面会直接继续播下去。
                if (_engine.IsPlaying && ++_frameStepWaits <= 10) return;

                timer.Stop();
                _pendingFrameStep = false;
                _frameStepWaits = 0;
                DoStepFrame(_pendingFrameForward);
            };

            return timer;
        }

        private void JumpToTimeCode()
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一个视频，再跳转");
                return;
            }

            var current = TimeFormatter.FormatWithHours(TimeSpan.FromMilliseconds(_engine.Time));
            var input = InputDialog.Show(
                this, "跳转到时间码", $"时间（秒 / 分:秒 / 时:分:秒.毫秒，当前 {current}）：", current);

            if (input == null) return;

            if (!TimeFormatter.TryParse(input, out var target))
            {
                SetStatus("时间码看不懂，试试 90、1:30 或 00:12:34.5");
                return;
            }

            _engine.SeekTo((long)target.TotalMilliseconds);
            SetStatus("已跳转到 " + TimeFormatter.FormatWithHours(target));
        }

        private void DisposeVideoResources()
        {
            _frameStepTimer?.Stop();
            _frameStepTimer?.Dispose();
            _frameStepTimer = null;
        }
    }
}
