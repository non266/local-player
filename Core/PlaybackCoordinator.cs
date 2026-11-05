using System;

namespace 播放器.Core
{
    /// <summary>即将打开一份媒体（还没调 <see cref="PlayerEngine.Open"/>）：宿主可以先把按文件记住的东西摆好。</summary>
    public sealed class PlaybackOpeningEventArgs : EventArgs
    {
        public PlaybackOpeningEventArgs(string path, int index)
        {
            Path = path;
            Index = index;
        }

        public string Path { get; }

        public int Index { get; }
    }

    /// <summary>这一份媒体已经交给引擎：宿主负责标题、列表高亮、读标签这些界面动作。</summary>
    public sealed class PlaybackOpenedEventArgs : EventArgs
    {
        public PlaybackOpenedEventArgs(string path, int index, string displayName)
        {
            Path = path;
            Index = index;
            DisplayName = displayName;
        }

        public string Path { get; }

        public int Index { get; }

        public string DisplayName { get; }
    }

    /// <summary>
    /// 播放业务：<b>放哪一首、下一首是谁、放完了怎么办</b>。
    /// <para>
    /// 这些原来在 <c>MainForm.Playback.cs</c> 里（682 行）：顺延跳过打不开的项、随机播放的洗牌袋、
    /// 循环、插播队列、「下一首」在列表末尾回绕、按文件续播、A-B 循环、音量均衡重载。
    /// 它们与"窗口、菜单、状态栏"无关，只认 <see cref="PlayerEngine"/> 与 <see cref="Playlist"/>，
    /// 所以收进 <c>Core</c>，成为<b>唯一会按业务顺序调用引擎的地方</b>。
    /// </para>
    /// <para>
    /// 界面那半边通过事件接：<see cref="Opening"/>（打开前：落历史、记当前文件、摆旋转）、
    /// <see cref="Opened"/>（打开后：标题 / 高亮 / 读标签）、<see cref="StatusChanged"/>、
    /// <see cref="ListInvalidated"/>、<see cref="QueueChanged"/>。协调器不碰控件、不弹框。
    /// </para>
    /// <para>线程约定与搬家之前一样：全部在界面线程上调用。</para>
    /// </summary>
    public sealed partial class PlaybackCoordinator
    {
        /// <summary>连续失败这么多次就停下，避免整列表都是坏文件时无限跳。</summary>
        public const int MaxConsecutiveErrors = 5;

        /// <summary>
        /// 列表里这一项打不开时的固定提示。用常量比较，避免每次顺延都重复标记、重复刷列表。
        /// <para>启动时后台核对"上次那些文件还在不在"也用这一句标记。</para>
        /// </summary>
        public const string MissingFileMessage = "文件不存在或无法访问";

        private readonly PlayerEngine _engine;
        private readonly Playlist _playlist;
        private readonly PlaybackQueue _queue;
        private readonly AppSettings _settings;
        private readonly Func<string, TimeSpan?> _resumeLookup;

        /// <summary>随机播放的"洗牌袋"：一轮之内每首恰好一次，一轮完了重洗。</summary>
        private readonly ShuffleBag _shuffleBag = new ShuffleBag();

        private int _consecutiveErrors;

        /// <summary>排定的续播位置（还没等到"媒体播起来 + 总时长已知"）。</summary>
        private TimeSpan? _pendingResume;

        public PlaybackCoordinator(
            PlayerEngine engine,
            Playlist playlist,
            PlaybackQueue queue,
            AppSettings settings,
            Func<string, TimeSpan?> resumeLookup)
        {
            _engine = engine;
            _playlist = playlist;
            _queue = queue;
            _settings = settings;
            _resumeLookup = resumeLookup;
        }

        /// <summary>马上要打开这一份（宿主在这里落历史、设置会话路径、摆画面旋转）。</summary>
        public event EventHandler<PlaybackOpeningEventArgs>? Opening;

        /// <summary>已经交给引擎（宿主在这里刷标题 / 列表 / 标签 / 状态栏）。</summary>
        public event EventHandler<PlaybackOpenedEventArgs>? Opened;

        /// <summary>列表里的错误标记变了（宿主重建列表）。</summary>
        public event EventHandler? ListInvalidated;

        /// <summary>要显示给用户的一句话（播完了 / 打不开这一项）。</summary>
        public event EventHandler<string>? StatusChanged;

        /// <summary>「下一首播放」队列变了（宿主刷 ▶ 标记与菜单）。</summary>
        public event EventHandler? QueueChanged;

        /// <summary>在停下来之前（宿主在这里记住按文件的延迟）。</summary>
        public event EventHandler? Stopping;

        /// <summary>已经停下来了（宿主清空侧栏 / 标题 / 系统媒体控件）。</summary>
        public event EventHandler? Stopped;

        /// <summary>续播定位真的发出去了（宿主说一句"已从 … 继续播放"）。</summary>
        public event EventHandler<TimeSpan>? ResumeApplied;

        public PlaybackQueue Queue => _queue;

        /// <summary>
        /// 播放指定索引；若该项不可播放则自动顺延，整轮都不可用时停止。
        /// <para>
        /// <paramref name="keepCurrent"/> 为 true 时<b>不动播放列表的当前项</b>：
        /// 「下一首播放」的插播用它——播完插播的这一首还要回到列表原来的位置继续。
        /// </para>
        /// <para>
        /// <paramref name="allowFallback"/> 为 false 时，失败就停下说清楚（用户明确点开的那一项，
        /// 例如「打开光盘」，不该被顺手换成列表里别的歌）。
        /// </para>
        /// </summary>
        public void PlayIndex(int index, bool keepCurrent = false, bool allowFallback = true)
        {
            if (_playlist.Count == 0) return;
            if (index < 0 || index >= _playlist.Count) index = 0;

            // 顺延时只记标记、不刷列表。
            // 宿主重建列表是整体重建，放在这个循环里就会变成"跳过一个重建一次"——
            // 整份列表都失效时要重建上千次，窗口直接假死。
            var marked = false;
            string? lastError = null;

            for (var offset = 0; offset < _playlist.Count; offset++)
            {
                var candidate = (index + offset) % _playlist.Count;
                var item = _playlist.Items[candidate];

                // 启动时后台核对已经标成"打不开"的条目直接跳过：网络盘离线时，
                // 每个 File.Exists 都可能卡到 SMB 超时，列表里几百条的时候
                // 按一次播放能把界面冻住好几分钟。网络流 / 光盘不适用（它们本来就不查磁盘）。
                if (!item.IsLocation && item.HasError && item.ErrorMessage == MissingFileMessage)
                {
                    lastError = item.ErrorMessage;

                    if (!allowFallback) break;

                    continue;
                }

                if (!MediaFormats.IsOpenable(item.FilePath))
                {
                    if (!item.HasError || item.ErrorMessage != MissingFileMessage)
                    {
                        item.HasError = true;
                        item.ErrorMessage = MissingFileMessage;
                        marked = true;
                    }

                    lastError = item.ErrorMessage;

                    if (!allowFallback) break;

                    continue;
                }

                var wasMarked = item.HasError;

                item.HasError = false;
                item.ErrorMessage = null;

                try
                {
                    // 排定这一首的续播位置（等媒体真的播起来、总时长已知才 seek）。
                    ScheduleResume(item.FilePath);

                    // 插播（「下一首播放」）刻意不动列表当前项：播完还要回到原来的位置继续。
                    if (!keepCurrent) _playlist.SetCurrent(candidate);

                    Opening?.Invoke(this, new PlaybackOpeningEventArgs(item.FilePath, candidate));

                    // 手动播到的这一项如果本来排着队，就从队里去掉：它已经不是"下一首"了。
                    if (_queue.Remove(item.FilePath))
                    {
                        item.Queued = false;
                        QueueChanged?.Invoke(this, EventArgs.Empty);
                    }

                    // A-B 循环的点是"给这一首设的"：换到别的文件就清掉。
                    ClearAbLoopForOtherFile(item.FilePath);

                    _engine.Open(item.FilePath);

                    Opened?.Invoke(this, new PlaybackOpenedEventArgs(item.FilePath, candidate, item.DisplayName));

                    if (marked || wasMarked) ListInvalidated?.Invoke(this, EventArgs.Empty);

                    return;
                }
                catch (Exception ex)
                {
                    item.HasError = true;
                    item.ErrorMessage = ex.Message;
                    marked = true;
                    lastError = ex.Message;

                    if (!allowFallback) break;
                }
            }

            if (marked) ListInvalidated?.Invoke(this, EventArgs.Empty);

            Stop();

            StatusChanged?.Invoke(this, allowFallback
                ? "播放列表中没有可播放的媒体"
                : "打不开这一项：" + (lastError ?? "没有可播放的内容"));
        }

        /// <summary>没有当前项时从第一项开始（列表空时由界面决定要不要弹"打开文件"）。</summary>
        public void PlayCurrentOrFirst()
        {
            if (_playlist.Count == 0) return;

            PlayIndex(_playlist.CurrentIndex < 0 ? 0 : _playlist.CurrentIndex);
        }

        /// <summary>
        /// 下一个。<paramref name="userInitiated"/> 为 true（用户主动按的）时，
        /// 列表末尾会回到第一项；自然播完则看循环模式。
        /// </summary>
        public bool PlayNext(bool userInitiated)
        {
            if (_playlist.Count == 0) return false;

            if (_settings.Shuffle && _playlist.Count > 1)
            {
                PlayIndex(NextRandomIndex());
                return true;
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
                    Stop();
                    StatusChanged?.Invoke(this, "播放列表已结束");
                    return false;
                }
            }

            PlayIndex(next);
            return true;
        }

        /// <summary>
        /// 上一个。与常见播放器一致：播放超过 3 秒时，先回到本曲开头。
        /// </summary>
        public void PlayPrevious()
        {
            if (_playlist.Count == 0) return;

            if (_engine.HasMedia && _engine.Time > 3000)
            {
                _engine.SeekTo(0);
                return;
            }

            var previous = _playlist.CurrentIndex - 1;
            if (previous < 0) previous = _playlist.Count - 1;
            PlayIndex(previous);
        }

        /// <summary>停止（清掉续播、A-B 与连续失败计数）；界面清理由 <see cref="Stopping"/> / <see cref="Stopped"/> 负责。</summary>
        public void Stop()
        {
            Stopping?.Invoke(this, EventArgs.Empty);

            // 停止也清循环点：停完再点播放是从头开始，留着 A-B 只会让人以为它还在起作用。
            ClearAbLoopCore();

            _engine.Stop();
            _pendingResume = null;
            _consecutiveErrors = 0;

            Stopped?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 自然播完：单曲循环 → 重播；插播队列优先；否则下一首（不循环时停在末尾）。
        /// <para>睡眠定时器的"听完这一首就停"由界面先判，判到了就不会调到这里。</para>
        /// </summary>
        public void HandleEnded()
        {
            if (_settings.RepeatMode == RepeatMode.One)
            {
                _engine.Replay();
                return;
            }

            if (PlayNextFromQueue()) return;

            PlayNext(userInitiated: false);
        }

        /// <summary>引擎报错：界面标记那一项之后把计数交给这里（连续失败上限由 <see cref="ShouldAutoSkipAfterError"/> 判）。</summary>
        public void RegisterError() => _consecutiveErrors++;

        /// <summary>真正播起来时清零（一次成功之后，之前那串失败就不算"连续"）。</summary>
        public void ResetErrorCount() => _consecutiveErrors = 0;

        /// <summary>连续失败时要不要自动跳过（设上限，避免整列表都是坏文件时无限跳）。</summary>
        public bool ShouldAutoSkipAfterError() =>
            _consecutiveErrors <= MaxConsecutiveErrors && _playlist.Count > 1;

        /// <summary>
        /// 把当前这一首从头重载（音量均衡是媒体级滤镜，切换必须重载）。
        /// 续播位置要清掉——"已重载"却还停在原位置，看起来就是没生效。
        /// </summary>
        /// <returns>真的重载了没有（没有媒体 / 没有列表当前项时为 false）。</returns>
        public bool ReloadCurrent()
        {
            if (!_engine.HasMedia || _playlist.CurrentIndex < 0) return false;

            PlayIndex(_playlist.CurrentIndex);
            _pendingResume = null;
            return true;
        }

        /// <summary>关掉「记住播放进度」时把排着的续播位置丢掉。</summary>
        public void ClearPendingResume() => _pendingResume = null;

        /// <summary>
        /// 直接排一个续播位置（按文件记忆之外的一次性定位，例如换画面旋转后重载回原处）。
        /// </summary>
        public void ScheduleResumeAt(TimeSpan? position) => _pendingResume = position;

        /// <summary>
        /// 界面计时器每一拍调一次：续播定位 + A-B 回跳。
        /// </summary>
        /// <param name="engineTime">引擎当前时间。</param>
        /// <param name="engineLength">引擎总时长。</param>
        /// <param name="userSeeking">用户正捏着进度条：这时不掺和 A-B（把他拽回 A 点只会让人以为进度条坏了）。</param>
        /// <returns>该显示成什么时间（刚跳回 A 点时就是 A 点）。</returns>
        public long Tick(long engineTime, long engineLength, bool userSeeking)
        {
            ApplyPendingResume(engineLength);

            return userSeeking ? engineTime : ApplyAbLoop(engineTime);
        }

        /// <summary>
        /// 随机播放用"洗牌袋"：把列表洗一遍、一个一个拿出来，**一轮之内每首恰好播一次**；
        /// 袋子空了重新洗，而且不会立刻重复刚播过的那一首。
        /// </summary>
        private int NextRandomIndex()
        {
            if (_playlist.Count <= 1) return 0;

            // 列表条目数变了（载入歌单 / 增删 / 清空）就按新的池子重洗。
            if (_shuffleBag.Count != _playlist.Count) _shuffleBag.Reset(_playlist.Count);

            return _shuffleBag.Next(_playlist.CurrentIndex);
        }

        /// <summary>
        /// 自然播完时先问队列：非空就播队首，<b>不动列表的当前项</b>。
        /// </summary>
        /// <returns>这次"播完了"是不是被队列接管了。</returns>
        private bool PlayNextFromQueue()
        {
            while (true)
            {
                var path = _queue.Dequeue();
                if (path == null) return false;

                var index = _playlist.IndexOfPath(path);

                // 入队之后那一项被删了 / 列表换过一份：这条就当没了，继续看下一条。
                if (index < 0)
                {
                    AppLog.Info($"「下一首播放」队列里的条目已经不在列表里，跳过：{path}");
                    QueueChanged?.Invoke(this, EventArgs.Empty);
                    continue;
                }

                QueueChanged?.Invoke(this, EventArgs.Empty);

                PlayIndex(index, keepCurrent: true);
                return true;
            }
        }

        /// <summary>排定续播位置：等媒体真正播起来之后再定位。</summary>
        private void ScheduleResume(string filePath)
        {
            _pendingResume = null;

            if (!_settings.ResumePlayback) return;

            var position = _resumeLookup(filePath);
            if (position.HasValue) _pendingResume = position;
        }

        /// <summary>
        /// 待续播位置要等媒体真正开始播放、且总时长已知之后才能 seek，
        /// 否则 libvlc 还没准备好，定位会被丢掉。
        /// </summary>
        private void ApplyPendingResume(long engineLength)
        {
            if (!_pendingResume.HasValue) return;
            if (!_engine.HasMedia || !_engine.IsPlaying) return;
            if (engineLength <= 0) return;

            var target = _pendingResume.Value;
            _pendingResume = null;

            _engine.SeekTo((long)target.TotalMilliseconds);

            ResumeApplied?.Invoke(this, target);
        }
    }
}
