using System;
using System.Collections.Generic;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 播放控制：播放 / 暂停 / 上下曲、进度、音量、速率、循环（含 A-B 循环）、画面比例。
    /// </summary>
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

        /// <summary>播放指定索引（会同步播放列表的当前项）。</summary>
        private void PlayIndex(int index) => PlayIndex(index, keepCurrent: false);

        /// <summary>
        /// 播放指定索引；若该项不可播放则自动顺延，整轮都不可用时停止。
        /// <para>
        /// <paramref name="keepCurrent"/> 为 true 时<b>不动播放列表的当前项</b>：
        /// 「下一首播放」的插播用它——播完插播的这一首还要回到列表原来的位置继续。
        /// </para>
        /// </summary>
        private void PlayIndex(int index, bool keepCurrent)
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

                // 启动时后台核对已经标成"打不开"的条目直接跳过：网络盘离线时，
                // 每个 File.Exists 都可能卡到 SMB 超时，列表里几百条的时候
                // 按一次播放能把界面冻住好几分钟。串流不适用（它本来就不查磁盘）。
                if (!item.IsStream && item.HasError && item.ErrorMessage == MissingFileMessage)
                    continue;

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

                    // 插播（「下一首播放」）刻意不动列表当前项：播完还要回到原来的位置继续。
                    if (!keepCurrent) _playlist.SetCurrent(candidate);

                    // 记录当前文件：音画/字幕延迟要按文件记住
                    _track.SetPath(item.FilePath);

                    // 手动播到的这一项如果本来排着队，就从队里去掉：它已经不是"下一首"了。
                    if (_queue.Remove(item.FilePath)) item.Queued = false;

                    // A-B 循环的点是"给这一首设的"：换到别的文件就清掉。
                    // 同一首重新起播（例如开了音量均衡要重载媒体）不算换歌，见那个方法的说明。
                    ClearAbLoopForOtherFile(item.FilePath);

                    // 画面旋转是内核级选项（libvlc 3 的 transform 只能在实例级设）：
                    // 必须在创建媒体之前摆好，开播时那个内核不一致就换内核（换片自动恢复）。
                    _engine.Rotation = Adjustments.Get(item.FilePath).Rotation;

                    AppLog.Info($"打开媒体：{item.FilePath}");

                    _engine.Open(item.FilePath);
                    UpdateWindowTitle();
                    HighlightPlayingItem();

                    // 上面的循环可能已经改过若干行的错误标记，这里统一刷一次。
                    if (marked || wasMarked) RefreshPlaylistView();

                    SetStatus("正在播放：" + item.DisplayName + QueueStatusSuffix());

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

            // 停止也清循环点：停完再点播放是从头开始，留着 A-B 只会让人以为它还在起作用。
            ClearAbLoopCore();

            _engine.Stop();
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
        // A-B 循环
        // =====================================================================

        /// <summary>A 点；<c>null</c> = 没设。与 <see cref="_abLoopEnd"/> 都有值才算生效。</summary>
        private TimeSpan? _abLoopStart;

        private TimeSpan? _abLoopEnd;

        /// <summary>
        /// 这两个点是<b>在哪个文件上</b>设的。
        /// <para>
        /// 换到别的文件才清：同一首重新起播（音量均衡要重载媒体）不算换歌，
        /// 一个开关把用户刚设的循环点清掉会很难解释。
        /// </para>
        /// </summary>
        private string? _abLoopPath;

        /// <summary>
        /// 已经朝 A 点发过 seek、但引擎还没真的回来。
        /// <para>
        /// libvlc 的定位是异步的：不记这一笔的话，在引擎时间跌回 B 之前每一拍都会再发一次
        /// seek，画面会一顿一顿地跳。
        /// </para>
        /// </summary>
        private bool _abLoopWrapPending;

        private ToolStripMenuItem? _menuAbLoop;

        private ToolStripMenuItem? _menuAbLoopState;

        private ToolStripMenuItem? _menuAbSetStart;

        private ToolStripMenuItem? _menuAbSetEnd;

        private ToolStripMenuItem? _menuAbClear;

        /// <summary>只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。</summary>
        internal long? AbLoopStartMilliseconds =>
            _abLoopStart.HasValue ? (long)_abLoopStart.Value.TotalMilliseconds : null;

        internal long? AbLoopEndMilliseconds =>
            _abLoopEnd.HasValue ? (long)_abLoopEnd.Value.TotalMilliseconds : null;

        internal bool AbLoopActive => _abLoopStart.HasValue && _abLoopEnd.HasValue;

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

        private static long Milliseconds(TimeSpan? value) =>
            value.HasValue ? (long)value.Value.TotalMilliseconds : 0;

        private void RefreshAbLoopMenu()
        {
            if (_menuAbLoopState == null) return;

            _menuAbLoopState.Text = "当前：" + AbLoop.Describe(AbLoopStartMilliseconds, AbLoopEndMilliseconds);

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
                _menuAbClear.Enabled = _abLoopStart.HasValue || _abLoopEnd.HasValue;
        }

        /// <summary>
        /// 「把 A 点设在当前时间」。A 不早于 B 时<b>如实拒绝</b>并说明原因——不偷偷挪成
        /// "B 点前面一点"：那样用户设的就不是他点的那一刻，而菜单里还写着另外一个数。
        /// </summary>
        private void SetAbLoopStart()
        {
            if (!_engine.HasMedia)
            {
                SetStatus("请先播放一段媒体，再把 A 点设在当前时间");
                return;
            }

            var value = _engine.Time;
            var reject = AbLoop.RejectStart(value, AbLoopEndMilliseconds);

            if (reject != null)
            {
                SetStatus("A-B 循环：" + reject);
                return;
            }

            _abLoopStart = TimeSpan.FromMilliseconds(value);
            _abLoopPath = _engine.CurrentPath;
            _abLoopWrapPending = false;

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

            var value = _engine.Time;
            var reject = AbLoop.RejectEnd(value, AbLoopStartMilliseconds);

            if (reject != null)
            {
                SetStatus("A-B 循环：" + reject);
                return;
            }

            _abLoopEnd = TimeSpan.FromMilliseconds(value);
            _abLoopPath = _engine.CurrentPath;

            RefreshAbLoopMenu();
            SetStatus("A-B 循环：" + DescribeAbLoop());
        }

        private string DescribeAbLoop()
        {
            if (AbLoopActive)
                return AbLoop.Describe(AbLoopStartMilliseconds, AbLoopEndMilliseconds) + "，播放到 B 就跳回 A";

            return _abLoopStart.HasValue
                ? "A " + AbLoop.Describe(Milliseconds(_abLoopStart)) + "（B 点还没设）"
                : "B " + AbLoop.Describe(Milliseconds(_abLoopEnd)) + "（A 点还没设）";
        }

        /// <summary>用户点了「清除 A-B」。</summary>
        private void ClearAbLoopByUser()
        {
            if (!_abLoopStart.HasValue && !_abLoopEnd.HasValue)
            {
                SetStatus("当前没有设 A-B 循环点");
                return;
            }

            ClearAbLoopCore();
            SetStatus("已清除 A-B 循环点");
        }

        /// <summary>把两个点丢掉（用户清除 / 换歌 / 停止都走这里）。</summary>
        private void ClearAbLoopCore()
        {
            _abLoopStart = null;
            _abLoopEnd = null;
            _abLoopPath = null;
            _abLoopWrapPending = false;

            RefreshAbLoopMenu();
        }

        /// <summary>换到别的文件就把循环点清掉；<paramref name="path"/> 是即将播放的文件。</summary>
        private void ClearAbLoopForOtherFile(string? path)
        {
            if (!_abLoopStart.HasValue && !_abLoopEnd.HasValue) return;

            if (_abLoopPath != null && string.Equals(_abLoopPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            ClearAbLoopCore();
        }

        /// <summary>
        /// 界面计时器每一拍问一句：越过 B 点了吗。越了就跳回 A 点。
        /// <para>
        /// 放在进度刷新里、不另起计时器：那里本来每拍都在算引擎时间，
        /// 而且跳回去之后滑块与时间标签要立刻跟着回到 A 点，写在一处才不会一格显示旧值。
        /// </para>
        /// </summary>
        /// <returns>该显示成什么时间（刚跳回 A 点时就是 A 点）。</returns>
        private long ApplyAbLoop(long time)
        {
            var start = AbLoopStartMilliseconds;
            var end = AbLoopEndMilliseconds;

            if (!AbLoop.ShouldWrap(time, start, end))
            {
                _abLoopWrapPending = false;
                return time;
            }

            // 已经发过 seek、引擎还没回来：这期间不再重复发。
            if (_abLoopWrapPending) return time;

            _abLoopWrapPending = true;
            _engine.SeekTo(start!.Value);

            return start.Value;
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
        /// 所以这里直接把当前这一首<b>重载</b>一遍（从头开始），并在状态栏把这件事说出来——
        /// 而不是假装"已经生效"，让用户对着没变化的音量怀疑自己点错了。
        /// </para>
        /// </summary>
        private void SetNormalize(bool on)
        {
            _settings.NormalizeVolume = on;
            _engine.NormalizeVolume = on;

            // 铺设置 / 载入设置方案时不许顺手重载媒体：那是批量赋值，
            // 而且"当前这一首"可能马上就要换，这里再叠一次重载纯属白干。
            if (_suspendUiEvents) return;

            if (!_engine.HasMedia || _playlist.CurrentIndex < 0)
            {
                SetStatus(on ? "已开启音量均衡（下次播放生效）" : "已关闭音量均衡（下次播放生效）");
                return;
            }

            PlayIndex(_playlist.CurrentIndex);

            // 重载一定要从头：续播位置是"下次接着看"用的，留着它会让这次的"已重载"
            // 看起来毫无动静——媒体确实重建了，画面却还在原处。
            _pendingResume = null;

            SetStatus(on
                ? "已开启音量均衡：当前这一首已从头开始重播以应用"
                : "已关闭音量均衡：当前这一首已从头开始重播以应用");
        }
    }
}
