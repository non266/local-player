using System;
using System.IO;
using System.Threading.Tasks;

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

        /// <summary>
        /// 网络位置在 <see cref="ProbeTimeout"/> 之内没回结果时的标记。
        /// 与 <see cref="MissingFileMessage"/> 刻意分开：超时只是"这次没问到"，
        /// 下次点这一项还要再试，不能当成永久缺失直接跳过。
        /// </summary>
        public const string NetworkTimeoutMessage = "网络位置响应超时";

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

        /// <summary>网络路径的存在性探测（默认在线程池里做 <c>File.Exists</c>；测试可替换）。</summary>
        private Func<string, Task<bool>> _pathProbe = DefaultPathProbe;

        /// <summary>
        /// 播放请求的代际号：每次 <see cref="PlayIndex"/> / <see cref="Stop"/> 都 +1。
        /// 后台探测回来时号码对不上，就直接丢掉——用户已经点了别的（或同一项又点了一次）。
        /// </summary>
        private int _playGeneration;

        /// <summary>正在后台探测的那一条（同一时刻最多一条：探测按播放顺序串行做）。</summary>
        private PendingProbe? _pendingProbe;

        /// <summary>一次播放请求从哪个索引开始、要不要顺延、是不是重载。</summary>
        private sealed class PlayRun
        {
            public PlayRun(int generation, int index, bool keepCurrent, bool allowFallback, bool suppressResume)
            {
                Generation = generation;
                Index = index;
                KeepCurrent = keepCurrent;
                AllowFallback = allowFallback;
                SuppressResume = suppressResume;
            }

            public int Generation { get; }

            public int Index { get; }

            public bool KeepCurrent { get; }

            public bool AllowFallback { get; }

            /// <summary>重载当前媒体：不要接上"上次播到哪"（否则看起来像没重载）。</summary>
            public bool SuppressResume { get; }

            /// <summary>这一轮有没有把条目标成"打不开"（标过就要刷列表）。</summary>
            public bool Marked { get; set; }

            /// <summary>最后一条失败原因（整轮都失败时用来说清为什么）。</summary>
            public string? LastError { get; set; }
        }

        /// <summary>一条还在后台查"在不在"的网络路径。</summary>
        private sealed class PendingProbe
        {
            public PlayRun Run = null!;

            public int Candidate;

            public int Offset;

            public string Path = string.Empty;

            public DateTime StartedUtc;

            public Task<bool> Task = null!;
        }

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

        /// <summary>
        /// 网络路径"在不在"的探测方式。默认在线程池里调 <c>File.Exists</c>；
        /// 冒烟测试换成可控的假探测，才好在"结果还没回来"的那一刻做断言。
        /// </summary>
        internal Func<string, Task<bool>> PathProbe
        {
            get => _pathProbe;
            set => _pathProbe = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// 单条网络路径最多等多久（默认 8 秒）。超时不等于"文件不存在"：
        /// 这一项只标"超时"、保留在列表里，下次点它还会再试。
        /// </summary>
        internal TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>现在有没有一条网络路径正在后台探测（冒烟测试用）。</summary>
        internal bool HasPendingProbe => _pendingProbe != null;

        private static Task<bool> DefaultPathProbe(string path) => Task.Run(() => File.Exists(path));

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
        public void PlayIndex(int index, bool keepCurrent = false, bool allowFallback = true) =>
            StartPlayRequest(index, keepCurrent, allowFallback, suppressResume: false);

        /// <summary>
        /// 开始一轮"放这一项（打不开就顺延）"。网络位置不在这里查磁盘：
        /// 起一个后台探测、马上返回，等 <see cref="PumpPendingProbe"/> 收尾。
        /// </summary>
        private void StartPlayRequest(int index, bool keepCurrent, bool allowFallback, bool suppressResume)
        {
            if (_playlist.Count == 0) return;
            if (index < 0 || index >= _playlist.Count) index = 0;

            // 上一轮还在后台探测的路径作废：用户已经换了目标（或同一项又点了一次）。
            _pendingProbe = null;

            var run = new PlayRun(++_playGeneration, index, keepCurrent, allowFallback, suppressResume);

            PlayFrom(run, 0);
        }

        /// <summary>
        /// 从 <paramref name="offset"/> 起试这一项、打不开就往后顺延。
        /// <para>
        /// 顺延时只记标记、不刷列表：宿主重建列表是整体重建，放在这个循环里就会变成
        /// "跳过一个重建一次"——整份列表都失效时要重建上千次，窗口直接假死。
        /// </para>
        /// <para>
        /// 碰到网络位置就起后台探测并<b>先返回</b>；探测结果由
        /// <see cref="PumpPendingProbe"/> 接上，从下一项继续。
        /// </para>
        /// </summary>
        private void PlayFrom(PlayRun run, int offset)
        {
            for (; offset < _playlist.Count; offset++)
            {
                if (run.Generation != _playGeneration) return;

                var candidate = (run.Index + offset) % _playlist.Count;
                var item = _playlist.Items[candidate];

                // 启动时后台核对已经标成"打不开"的条目直接跳过：再问一次只会再等一遍
                // SMB 超时。网络流 / 光盘不适用（它们本来就不查磁盘）。
                if (!item.IsLocation && item.HasError && item.ErrorMessage == MissingFileMessage)
                {
                    run.LastError = item.ErrorMessage;

                    if (!run.AllowFallback) break;

                    continue;
                }

                // 网络位置：界面线程一个字节都不读盘，交给后台探测。
                if (MediaFormats.IsNetworkPath(item.FilePath))
                {
                    StartProbe(run, candidate, offset);
                    return;
                }

                if (!MediaFormats.IsOpenable(item.FilePath))
                {
                    MarkMissing(run, item);

                    if (!run.AllowFallback) break;

                    continue;
                }

                if (TryOpen(run, candidate, item)) return;
            }

            Finish(run);
        }

        /// <summary>真正把这一项交给引擎。失败时标红并返回 false（由调用方决定要不要顺延）。</summary>
        private bool TryOpen(PlayRun run, int candidate, PlaylistItem item)
        {
            var wasMarked = item.HasError;

            item.HasError = false;
            item.ErrorMessage = null;

            try
            {
                // 排定这一首的续播位置（等媒体真的播起来、总时长已知才 seek）。
                if (run.SuppressResume) _pendingResume = null;
                else ScheduleResume(item.FilePath);

                // 插播（「下一首播放」）刻意不动列表当前项：播完还要回到原来的位置继续。
                if (!run.KeepCurrent) _playlist.SetCurrent(candidate);

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

                if (run.Marked || wasMarked) ListInvalidated?.Invoke(this, EventArgs.Empty);

                return true;
            }
            catch (Exception ex)
            {
                item.HasError = true;
                item.ErrorMessage = ex.Message;

                run.Marked = true;
                run.LastError = ex.Message;

                return false;
            }
        }

        private static void MarkMissing(PlayRun run, PlaylistItem item)
        {
            if (!item.HasError || item.ErrorMessage != MissingFileMessage)
            {
                item.HasError = true;
                item.ErrorMessage = MissingFileMessage;
            }

            run.Marked = true;
            run.LastError = MissingFileMessage;
        }

        /// <summary>给这一项起后台探测；结果没回来之前什么都不做。</summary>
        private void StartProbe(PlayRun run, int candidate, int offset)
        {
            var path = _playlist.Items[candidate].FilePath;

            var pending = new PendingProbe
            {
                Run = run,
                Candidate = candidate,
                Offset = offset,
                Path = path,
                StartedUtc = DateTime.UtcNow
            };

            try
            {
                pending.Task = _pathProbe(path);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("探测网络路径失败，按打不开处理：" + path, ex);
                pending.Task = Task.FromResult(false);
            }

            _pendingProbe = pending;

            StatusChanged?.Invoke(this, "正在检查网络位置：" + Path.GetFileName(path));
        }

        /// <summary>
        /// 收一条后台探测的结果（界面计时器每拍调一次，必须在界面线程上）。
        /// <para>
        /// 结果回来时先看代际号：用户可能已经点了别的，这一次的结果就该丢掉。
        /// 超时不当"不存在"：只标超时、保留条目，下次点它还会再试。
        /// </para>
        /// </summary>
        internal void PumpPendingProbe()
        {
            var pending = _pendingProbe;
            if (pending == null) return;

            // 列表被清空 / 这一项被删掉 / 换了位置：这条结果已经没有意义了。
            if (_playlist.Count == 0 ||
                pending.Candidate >= _playlist.Count ||
                !string.Equals(_playlist.Items[pending.Candidate].FilePath, pending.Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                _pendingProbe = null;
                return;
            }

            if (pending.Run.Generation != _playGeneration)
            {
                _pendingProbe = null;
                return;
            }

            if (!pending.Task.IsCompleted)
            {
                if (DateTime.UtcNow - pending.StartedUtc < ProbeTimeout) return;

                _pendingProbe = null;

                var item = _playlist.Items[pending.Candidate];

                // 不判"文件不存在"：超时只说明这次没问到。条目留着，下次点还会再试。
                item.HasError = true;
                item.ErrorMessage = NetworkTimeoutMessage;

                pending.Run.Marked = true;
                pending.Run.LastError = NetworkTimeoutMessage;

                AppLog.Info("网络位置探测超时，先跳过这一项：" + pending.Path);
                StatusChanged?.Invoke(this, "网络位置响应超时，先跳过这一项：" + item.DisplayName);

                if (pending.Run.AllowFallback) PlayFrom(pending.Run, pending.Offset + 1);
                else Finish(pending.Run);

                return;
            }

            _pendingProbe = null;

            bool exists;

            try
            {
                exists = pending.Task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("探测网络路径失败，按打不开处理：" + pending.Path, ex);
                exists = false;
            }

            if (pending.Run.Generation != _playGeneration) return;

            var probed = _playlist.Items[pending.Candidate];

            if (!exists)
            {
                MarkMissing(pending.Run, probed);

                if (pending.Run.AllowFallback) PlayFrom(pending.Run, pending.Offset + 1);
                else Finish(pending.Run);

                return;
            }

            if (TryOpen(pending.Run, pending.Candidate, probed)) return;

            if (pending.Run.AllowFallback) PlayFrom(pending.Run, pending.Offset + 1);
            else Finish(pending.Run);
        }

        /// <summary>整轮都没打开：刷一次列表、停下、说清原因。</summary>
        private void Finish(PlayRun run)
        {
            if (run.Generation != _playGeneration) return;

            if (run.Marked) ListInvalidated?.Invoke(this, EventArgs.Empty);

            Stop();

            var message = run.AllowFallback
                ? "播放列表中没有可播放的媒体"
                : "打不开这一项：" + (run.LastError ?? "没有可播放的内容");

            // 整轮只卡在"网络位置超时"时，说清是超时而不是"列表里什么都没有"。
            if (run.AllowFallback && run.LastError == NetworkTimeoutMessage)
                message = "网络位置响应超时，没有可播放的内容";

            StatusChanged?.Invoke(this, message);
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
            // 停下也把正在后台探测的那条作废：它回来时不该再把媒体打开。
            _playGeneration++;
            _pendingProbe = null;

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

            StartPlayRequest(_playlist.CurrentIndex, keepCurrent: false, allowFallback: true,
                suppressResume: true);
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
