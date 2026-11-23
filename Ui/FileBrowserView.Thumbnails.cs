using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 文件页的「平铺缩略图」：大图标集、封面缓存、按可见区域排队、批量贴图。
    /// <para>
    /// 缩略图是音视频文件的<b>封面</b>（内嵌图优先，其次同目录外挂图），
    /// <b>不从视频画面截帧</b>、不认图片文件、不落盘（1.28.0 起视频首帧也做，但有上限）。
    /// 解码只在 <see cref="ThumbnailQueue"/> 的专用线程上做；界面线程只按视口要图、
    /// 每 50 ms 取一小批贴到行上，迟到的结果按代际号丢掉。
    /// </para>
    /// <para>
    /// 1.30.0（所有者报「打开缩略图后浏览太卡顿」）：
    /// <list type="bullet">
    ///   <item>不再一进目录把整个目录（最多 600 / 1000）排进队列，只排当前可见范围 + 前后各一屏；</item>
    ///   <item>贴图不再"一张一个 BeginInvoke + 每张遍历整张列表"，改成队列 + 字典 + 每拍最多 16 张；</item>
    ///   <item>缓存满了不再"清空后重排整个目录"，只重建当前可见范围；</item>
    ///   <item>视频截帧可取消：换目录 / 滚动时不再让一张 2.5 秒的等待挡住后面的封面。</item>
    /// </list>
    /// 平铺只是"换一种列表形态"：<c>_entries</c> / `_searchResults` 两套数据不动，
    /// 索引、拖放、双击、右键、键盘全都沿用原来的那几段代码。
    /// </para>
    /// </summary>
    internal sealed partial class FileBrowserView
    {
        /// <summary>缩略图边长（逻辑像素，按 DPI 缩放）。</summary>
        private const int ThumbnailEdgeLogical = 56;

        /// <summary>
        /// 内存缓存上限（条）。1.30.0 起它只决定"什么时候重建可见范围"，
        /// 不再触发"把整个目录重新排一遍"。
        /// </summary>
        private const int ThumbnailCapacity = 512;

        /// <summary>每目录最多给多少个文件做缩略图（默认值，实际取 <see cref="FileBrowserState.ThumbnailBudget"/>）。</summary>
        private const int ThumbnailRequestDefault = 300;

        /// <summary>每目录最多截多少个视频首帧（1.28.0；不暴露设置：截帧比读封面贵两个数量级）。</summary>
        internal const int FrameBudget = 60;

        /// <summary>滚动停下多久之后才排视频截帧：滚动中只做封面。</summary>
        private const int FrameQuietMilliseconds = 300;

        /// <summary>一拍最多贴几张；配合 50 ms 的间隔约 320 张/秒，足够追上后台。</summary>
        private const int ApplyBatchSize = 16;

        private FileIconSet? _largeIcons;

        private readonly List<Bitmap> _thumbnailBitmaps = new List<Bitmap>();

        private readonly Dictionary<string, int> _thumbnailIndices =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _thumbnailMisses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>还没交付的键（封面与帧都算）。</summary>
        private readonly HashSet<string> _thumbnailPending =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>其中属于"帧"的键：取消帧时只清这些 pending，不碰封面。</summary>
        private readonly HashSet<string> _framePending =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>本轮已经消耗过缩略图预算的键（复位后保留，避免重复吃预算）。</summary>
        private readonly HashSet<string> _thumbnailChosen =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>本轮已经消耗过帧预算的键。</summary>
        private readonly HashSet<string> _frameChosen =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _thumbnailSkippedKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _frameSkippedKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>路径 → 行；贴图时 O(1) 找到，不再每张遍历整个列表。</summary>
        private readonly Dictionary<string, ListViewItem> _itemsByPath =
            new Dictionary<string, ListViewItem>(StringComparer.OrdinalIgnoreCase);

        /// <summary>后台交回来、还没贴到行上的结果。</summary>
        private readonly ConcurrentQueue<ThumbnailResult> _thumbnailApply =
            new ConcurrentQueue<ThumbnailResult>();

        private System.Windows.Forms.Timer? _thumbnailTimer;
        private int _thumbnailTicks;
        private int _lastRangeFirst = -1;
        private int _lastRangeLast = -1;
        private long _lastScrollTicks;

        private ThumbnailQueue? _thumbnailQueue;
        private CoverArtLoader _thumbnailSource = new CoverArtLoader();
        private int _thumbnailVersion;

        /// <summary>磁盘缓存（1.28.0）：点开过的目录第二次进来不再读封面 / 截帧。</summary>
        private readonly ThumbnailCache _thumbnailCache = new ThumbnailCache();

        /// <summary>视频首帧来源；第一次真要截帧时才建（建 libvlc 实例不便宜）。</summary>
        private IVideoFrameSource? _thumbnailFrames;

        /// <summary>测试注入过假的帧来源（不再建真的）。</summary>
        private bool _thumbnailFramesInjected;

        /// <summary>因为超过每目录预算而没做缩略图的文件数（状态行要说明）。</summary>
        private int _thumbnailSkipped;

        /// <summary>因为超过每目录截帧预算而只显示类型图标的视频数（状态行要说明）。</summary>
        private int _thumbnailFramesSkipped;

        // ---- 测试计数 ---------------------------------------------------------

        private int _thumbnailRequests;
        private int _thumbnailResets;
        private int _thumbnailApplied;
        private int _thumbnailBatches;
        private int _thumbnailLookups;
        private int _thumbnailViewUpdates;

        /// <summary>测试钩子：换掉封面来源（假的慢 / 假的数据 / 卡住不放，都靠它注入）。</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal CoverArtLoader ThumbnailSource
        {
            get => _thumbnailSource;
            set
            {
                _thumbnailSource = value ?? new CoverArtLoader();

                if (_thumbnailQueue == null) return;

                var old = _thumbnailQueue;
                old.Ready -= OnThumbnailReadyFromWorker;
                old.Dispose();

                _thumbnailQueue = null;
                _thumbnailPending.Clear();
                _framePending.Clear();
                EnsureThumbnailQueue();
            }
        }

        /// <summary>每目录的缩略图预算；测试可以调小来验上限。</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int MaxThumbnails { get; set; } = ThumbnailRequestDefault;

        /// <summary>缩略图边长（已按 DPI 缩放）。</summary>
        private int ThumbnailEdge => Scaled(ThumbnailEdgeLogical);

        /// <summary>当前该不该显示缩略图：用户打开了开关、且不在搜索结果页。</summary>
        private bool WantsThumbnails => _state.ShowThumbnails && !_searchActive && !_searching;

        /// <summary>真正在跑：开关开着、有句柄、而且这个控件当前可见（切到歌词页就停）。</summary>
        private bool ThumbnailsActive => WantsThumbnails && IsHandleCreated && Visible;

        // ---- 测试钩子（只加只读属性 / 方法，避免 WFO1000）---------------------

        internal bool IsTilesForTest => _list.View == View.LargeIcon;

        internal int ThumbnailEdgeForTest => ThumbnailEdge;

        internal int ThumbnailCountForTest => _thumbnailIndices.Count;

        /// <summary>还没交付的条数：队列待做 + 在飞 1 + 已交回来但没贴 + 还没销账的键。</summary>
        internal int PendingThumbnailCountForTest =>
            (_thumbnailQueue?.Pending ?? 0)
            + _thumbnailApply.Count
            + _thumbnailPending.Count
            + (_thumbnailQueue?.Busy == true ? 1 : 0);

        internal bool IsThumbnailBusyForTest => _thumbnailQueue?.Busy == true || !_thumbnailApply.IsEmpty;

        internal int TypeIconCountForTest => _icons?.Images.Images.Count ?? 0;

        internal IReadOnlyCollection<string> ThumbnailKeysForTest =>
            new List<string>(_thumbnailIndices.Keys);

        internal int ThumbnailRequestCountForTest => _thumbnailRequests;

        internal int ThumbnailResetCountForTest => _thumbnailResets;

        internal int ThumbnailAppliedCountForTest => _thumbnailApplied;

        internal int ThumbnailApplyBatchCountForTest => _thumbnailBatches;

        internal int ThumbnailLookupCountForTest => _thumbnailLookups;

        /// <summary>
        /// 真正包过一次 BeginUpdate/EndUpdate 的次数。空闲时它必须不再增长——
        /// 否则就是"每 50 ms 空转一次重绘"，表现正是缩略图一直闪。
        /// </summary>
        internal int ThumbnailViewUpdateCountForTest => _thumbnailViewUpdates;

        internal (int First, int Last) ThumbnailRangeForTest
        {
            get
            {
                ComputeThumbnailRange(out var first, out var last);
                return (first, last);
            }
        }

        internal int ThumbnailChosenCountForTest => _thumbnailChosen.Count;

        internal int FrameChosenCountForTest => _frameChosen.Count;

        internal void SetThumbnailsForTest(bool value) => SetThumbnails(value);

        internal void ScrollByForTest(int delta) => ScrollBy(delta);

        internal void RebuildPositionMenuForTest() => RebuildPositionMenu();

        internal static bool ThumbnailAppliesForTest(FileBrowserEntry entry) => ThumbnailApplies(entry);

        internal int ImageIndexForTest(string name)
        {
            var item = _list.Items
                .Cast<ListViewItem>()
                .FirstOrDefault(row => string.Equals(row.Text, name, StringComparison.OrdinalIgnoreCase));

            return item?.ImageIndex ?? -1;
        }

        internal Image? LargeImageForTest(int index)
        {
            var images = _largeIcons?.Images.Images;

            if (images == null || index < 0 || index >= images.Count) return null;

            return images[index];
        }

        // ---- 预算 / 缓存（1.28.0）---------------------------------------------

        /// <summary>每目录缩略图预算：只认 300 / 600 / 1000，别的值落到最近一档。</summary>
        internal void SetThumbnailBudget(int budget)
        {
            var normalized = FileBrowserThumbnailBudgets.Normalize(budget);

            if (_state.ThumbnailBudget == normalized) return;

            _state.ThumbnailBudget = normalized;
            MaxThumbnails = normalized;
            RaiseStateChanged();

            FillList();
            UpdateStatusCount();
        }

        internal void SetThumbnailBudgetForTest(int budget) => SetThumbnailBudget(budget);

        internal int MaxThumbnailsForTest => MaxThumbnails;

        internal int FramesSkippedForTest => _thumbnailFramesSkipped;

        internal int ThumbnailsSkippedForTest => _thumbnailSkipped;

        internal (int Count, long Bytes) ThumbnailCacheStatsForTest => _thumbnailCache.Stats();

        internal string ThumbnailCacheFolderForTest => _thumbnailCache.Folder;

        /// <summary>给测试用：某个文件对应的磁盘缓存文件（没有就 null）。</summary>
        internal string? ThumbnailCacheFileForTest(string path)
        {
            var entry = _entries.FirstOrDefault(candidate =>
                string.Equals(candidate.FullPath, path, StringComparison.OrdinalIgnoreCase));

            if (entry == null) return null;

            var key = ThumbnailCache.Key(entry.FullPath, entry.Size, entry.Modified.Ticks, ThumbnailEdge);
            var file = _thumbnailCache.FullPathFor(key);

            return System.IO.File.Exists(file) ? file : null;
        }

        /// <summary>只把内存里的正 / 负缓存与位图丢掉（磁盘缓存不动）：验"第二次进来只读盘"。</summary>
        internal void ClearThumbnailMemoryForTest() => ResetThumbnails();

        /// <summary>「位置 → 清空缩略图缓存」：删磁盘 + 清内存，状态行说明删了多少。</summary>
        internal void ClearThumbnailCache()
        {
            var (count, bytes) = _thumbnailCache.Clear();

            ResetThumbnails();

            SetStatus($"已清空缩略图缓存（{count:N0} 个文件 / {FileBrowserService.FormatSize(bytes)}）", false);
        }

        internal void ClearThumbnailCacheForTest() => ClearThumbnailCache();

        // ---- 形态 -------------------------------------------------------------

        /// <summary>
        /// 按当前状态摆列表形态：平铺（大图标 + 缩略图）或详情（列 + 小图标）。
        /// <para>
        /// 切回详情必须把每一步的 <c>ImageIndex</c> 复位成小图标那一套：
        /// 详情用的是 <c>SmallImageList</c>，缩略图的索引（≥ 7）在那里是空的。
        /// </para>
        /// </summary>
        private void ApplyThumbnailMode()
        {
            var tiles = WantsThumbnails;

            if (tiles)
            {
                EnsureLargeIcons();

                if (!ReferenceEquals(_list.LargeImageList, _largeIcons!.Images))
                    _list.LargeImageList = _largeIcons.Images;

                if (_list.View != View.LargeIcon) _list.View = View.LargeIcon;

                _list.FullRowSelect = false;

                EnsureThumbnailTimer();

                if (ThumbnailsActive) _thumbnailTimer!.Start();

                return;
            }

            StopThumbnailTimer();

            if (_list.View != View.Details) _list.View = View.Details;

            _list.FullRowSelect = true;
            ResetItemImages();
            AdjustColumns();
        }

        /// <summary>列表填完之后按当前源排队；详情下把没跑完的请求取消掉。</summary>
        private void RefreshThumbnails()
        {
            if (WantsThumbnails) StartThumbnailPass();
            else CancelThumbnails();
        }

        private void EnsureLargeIcons()
        {
            if (_largeIcons != null) return;

            _largeIcons = new FileIconSet(_palette, ThumbnailEdge);
            _list.LargeImageList = _largeIcons.Images;
        }

        private void EnsureThumbnailQueue()
        {
            if (_thumbnailQueue != null) return;

            _thumbnailQueue = new ThumbnailQueue(_thumbnailSource, _thumbnailCache, EnsureFrameSource());

            _thumbnailQueue.Ready += OnThumbnailReadyFromWorker;
        }

        private void EnsureThumbnailTimer()
        {
            if (_thumbnailTimer != null) return;

            _thumbnailTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _thumbnailTimer.Tick += OnThumbnailTimerTick;
        }

        private void StopThumbnailTimer()
        {
            _thumbnailTimer?.Stop();
        }

        private void DisposeThumbnailTimer()
        {
            if (_thumbnailTimer == null) return;

            _thumbnailTimer.Stop();
            _thumbnailTimer.Tick -= OnThumbnailTimerTick;
            _thumbnailTimer.Dispose();
            _thumbnailTimer = null;
        }

        /// <summary>
        /// 第一次真要截帧时才建 <see cref="VideoFrameGrabber"/>（一个 libvlc 实例 + 一个播放器）。
        /// 建不出来（例如这台机器上 libvlc 起不来）就返回 <c>null</c>：缩略图退回"封面 → 类型图标"。
        /// </summary>
        private IVideoFrameSource? EnsureFrameSource()
        {
            if (_thumbnailFrames != null) return _thumbnailFrames;
            if (_thumbnailFramesInjected) return null;

            try
            {
                _thumbnailFrames = new VideoFrameGrabber();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页缩略图：建不出抓帧器，视频只用封面", ex);
                _thumbnailFrames = null;
                _thumbnailFramesInjected = true;
            }

            return _thumbnailFrames;
        }

        /// <summary>测试钩子：换掉帧来源（<c>null</c> = 本视图永远不截帧）。</summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal void SetVideoFramesForTest(IVideoFrameSource? frames)
        {
            _thumbnailFrames = frames;
            _thumbnailFramesInjected = true;

            if (_thumbnailQueue == null) return;

            var old = _thumbnailQueue;
            old.Ready -= OnThumbnailReadyFromWorker;
            old.Dispose();
            _thumbnailQueue = null;
            _thumbnailPending.Clear();
            _framePending.Clear();

            EnsureThumbnailQueue();
        }

        // ---- 排队 -------------------------------------------------------------

        private static bool ThumbnailApplies(FileBrowserEntry entry) =>
            !entry.IsDirectory
            && entry.Kind is FileBrowserKind.Video or FileBrowserKind.Audio
            && !MediaFormats.IsNetworkPath(entry.FullPath);

        private static string ThumbnailKey(FileBrowserEntry entry) =>
            entry.FullPath + "|" + entry.Size.ToString() + "|" + entry.Modified.Ticks.ToString();

        /// <summary>新的一轮（填完列表 / 换目录 / 改预算）：版本 +1，清空本轮状态，再按视口排。</summary>
        private void StartThumbnailPass()
        {
            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _framePending.Clear();
            _thumbnailChosen.Clear();
            _frameChosen.Clear();
            _thumbnailSkippedKeys.Clear();
            _frameSkippedKeys.Clear();
            _thumbnailSkipped = 0;
            _thumbnailFramesSkipped = 0;
            _lastRangeFirst = -1;
            _lastRangeLast = -1;

            _thumbnailQueue?.CancelAll();

            if (!ThumbnailsActive) return;

            EnsureThumbnailQueue();
            EnsureThumbnailTimer();
            _thumbnailTimer!.Start();

            // 新目录先让封面跑一轮：视频帧等滚动停下来再说。
            _lastScrollTicks = Environment.TickCount64;
            RequestVisibleThumbnails();
        }

        /// <summary>
        /// 只排"当前可见范围 + 前后各一屏"：这是 1.30.0 的核心。
        /// 滚动 / 原生滚轮 / 键盘改变视口时都会走到这里。
        /// </summary>
        private void RequestVisibleThumbnails()
        {
            if (!ThumbnailsActive || _list.Items.Count == 0) return;
            if (_visibleEntries.Count != _list.Items.Count) return;

            ComputeThumbnailRange(out var first, out var last);

            if (first != _lastRangeFirst || last != _lastRangeLast)
            {
                _lastRangeFirst = first;
                _lastRangeLast = last;
                _lastScrollTicks = Environment.TickCount64;
                CancelFrameRequests();
            }

            if (last < first) return;

            var budget = Math.Max(0, MaxThumbnails);
            var quiet = Environment.TickCount64 - _lastScrollTicks >= FrameQuietMilliseconds;
            var statusChanged = false;

            for (var index = first; index <= last && index < _visibleEntries.Count; index++)
            {
                var entry = _visibleEntries[index];

                if (!ThumbnailApplies(entry)) continue;

                var key = ThumbnailKey(entry);

                var isVideo = entry.Kind == FileBrowserKind.Video;

                var chosen = _thumbnailChosen.Contains(key);

                if (!chosen)
                {
                    if (_thumbnailChosen.Count >= budget)
                    {
                        if (_thumbnailSkippedKeys.Add(key))
                        {
                            _thumbnailSkipped++;
                            statusChanged = true;
                        }

                        // 预算外的项即使内存里还留着图，也回到类型图标：
                        // "最多做 N 个"是用户看得见的承诺，不能因为缓存命中就变成 N+。
                        // 只在真的不一样时赋值：每 200 ms 给同一项重设一次 ImageIndex 也会闪。
                        if (_itemsByPath.TryGetValue(entry.FullPath, out var item) && _icons != null)
                        {
                            var typeIndex = _icons.IndexOf(entry.Kind);

                            if (item.ImageIndex != typeIndex) item.ImageIndex = typeIndex;
                        }

                        continue;
                    }

                    _thumbnailChosen.Add(key);
                }

                if (_thumbnailMisses.Contains(key)) continue;
                if (_thumbnailPending.Contains(key)) continue;
                if (_thumbnailIndices.ContainsKey(key)) continue;

                // 视频整条推迟到滚动停下：避免一边滚一边让 libvlc 解码。
                if (isVideo && !quiet) continue;

                var wantFrame = false;

                if (isVideo)
                {
                    if (_frameChosen.Contains(key))
                    {
                        wantFrame = true;
                    }
                    else if (_frameChosen.Count < FrameBudget)
                    {
                        _frameChosen.Add(key);
                        wantFrame = true;
                    }
                    else if (_frameSkippedKeys.Add(key))
                    {
                        _thumbnailFramesSkipped++;
                        statusChanged = true;
                    }
                }

                _thumbnailPending.Add(key);
                if (wantFrame) _framePending.Add(key);

                if (!_thumbnailQueue!.Request(
                        key, entry.FullPath, entry.Size, entry.Modified.Ticks, ThumbnailEdge, _thumbnailVersion, wantFrame))
                {
                    // 队列满了：本次没排进去，等下一拍重试；预算已经记下，不再重复扣。
                    _thumbnailPending.Remove(key);
                    _framePending.Remove(key);
                    continue;
                }

                _thumbnailRequests++;
            }

            if (statusChanged) UpdateStatusCount();
        }

        /// <summary>视口变了：丢掉还没开始的帧，并让正在跑的那一次尽快收手；封面队列不动。</summary>
        private void CancelFrameRequests()
        {
            foreach (var key in _framePending) _thumbnailPending.Remove(key);

            _framePending.Clear();
            _thumbnailQueue?.CancelFrames();
        }

        private void CancelThumbnails()
        {
            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _framePending.Clear();
            _thumbnailSkipped = 0;
            _thumbnailFramesSkipped = 0;
            _lastRangeFirst = -1;
            _lastRangeLast = -1;
            _thumbnailQueue?.CancelAll();
            DrainThumbnailResults(disposeOnly: true);
            StopThumbnailTimer();
        }

        // ---- 视口 -------------------------------------------------------------

        private void ComputeThumbnailRange(out int first, out int last)
        {
            first = 0;
            last = -1;

            var count = _list.Items.Count;

            if (count == 0 || !_list.IsHandleCreated) return;

            if (_list.View == View.LargeIcon)
            {
                first = FirstVisibleLargeIconIndex();

                var height = Math.Max(1, _list.ClientSize.Height);
                last = first;

                while (last + 1 < count && _list.Items[last + 1].Bounds.Top < height) last++;

                var visible = Math.Max(1, last - first + 1);
                var margin = visible;

                first = Math.Max(0, first - margin);
                last = Math.Min(count - 1, last + margin);
                return;
            }

            first = _list.TopItem?.Index ?? 0;

            var rowHeight = Math.Max(1, _list.GetItemRect(first).Height);
            var rows = Math.Max(1, _list.ClientSize.Height / rowHeight);
            last = Math.Min(count - 1, first + rows - 1);

            first = Math.Max(0, first - rows);
            last = Math.Min(count - 1, last + rows);
        }

        /// <summary>平铺下第一个"底边还在视口里"的项；二分，20k 项也不用整表扫。</summary>
        private int FirstVisibleLargeIconIndex()
        {
            var count = _list.Items.Count;

            if (count == 0) return 0;
            if (_list.Items[0].Bounds.Height <= 0) return 0;

            var low = 0;
            var high = count - 1;

            while (low < high)
            {
                var middle = low + (high - low) / 2;

                if (_list.Items[middle].Bounds.Bottom <= 0) low = middle + 1;
                else high = middle;
            }

            return low;
        }

        // ---- 贴图 -------------------------------------------------------------

        private void OnThumbnailReadyFromWorker(ThumbnailResult result)
        {
            if (IsDisposed || !IsHandleCreated || !WantsThumbnails)
            {
                result.Bitmap?.Dispose();
                return;
            }

            // 不在这里 BeginInvoke：界面每 50 ms 自己来取一批。
            _thumbnailApply.Enqueue(result);
        }

        private void OnThumbnailTimerTick(object? sender, EventArgs e)
        {
            if (IsDisposed) return;

            try
            {
                if (!ThumbnailsActive)
                {
                    StopThumbnailTimer();
                    return;
                }

                DrainThumbnailResults(disposeOnly: false);

                // 每 4 拍（约 200 ms）检查一次视口与"队列满没排进去"的重试。
                if (++_thumbnailTicks % 4 != 0) return;

                RequestVisibleThumbnails();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页缩略图定时贴图失败", ex);
            }
        }

        private void DrainThumbnailResults(bool disposeOnly)
        {
            if (disposeOnly)
            {
                while (_thumbnailApply.TryDequeue(out var discarded)) discarded.Bitmap?.Dispose();

                return;
            }

            // 空闲时不要碰 ListView：每 50 ms 空包一次 Begin/EndUpdate 就是持续重绘（缩略图一直闪）。
            if (_thumbnailApply.IsEmpty) return;

            var applied = 0;

            _list.BeginUpdate();
            _thumbnailViewUpdates++;

            try
            {
                while (applied < ApplyBatchSize && _thumbnailApply.TryDequeue(out var result))
                {
                    applied++;
                    ApplyThumbnail(result);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            if (applied > 0) _thumbnailBatches++;
        }

        private void ApplyThumbnail(ThumbnailResult result)
        {
            try
            {
                if (IsDisposed || result.Version != _thumbnailVersion || !WantsThumbnails)
                {
                    result.Bitmap?.Dispose();
                    return;
                }

                _thumbnailPending.Remove(result.Key);
                _framePending.Remove(result.Key);

                if (result.Aborted)
                {
                    // 截帧被取消：不记负缓存，也不贴图；滚回来 / 停下来还会再排。
                    return;
                }

                if (result.Bitmap == null)
                {
                    _thumbnailMisses.Add(result.Key);
                    return;
                }

                if (_thumbnailIndices.ContainsKey(result.Key))
                {
                    result.Bitmap.Dispose();
                    return;
                }

                if (_thumbnailIndices.Count >= ThumbnailCapacity)
                {
                    // 缓存满了：只重建当前可见范围，绝不再把整个目录重排一遍。
                    result.Bitmap.Dispose();
                    ResetThumbnails();
                    return;
                }

                var index = AddThumbnail(result.Bitmap);
                _thumbnailIndices[result.Key] = index;
                SetItemImage(result.Path, index);
                _thumbnailApplied++;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页缩略图贴图失败", ex);
                result.Bitmap?.Dispose();
            }
        }

        private int AddThumbnail(Bitmap bitmap)
        {
            EnsureLargeIcons();

            var images = _largeIcons!.Images.Images;
            images.Add(bitmap);
            _thumbnailBitmaps.Add(bitmap);

            return images.Count - 1;
        }

        private void SetItemImage(string path, int index)
        {
            _thumbnailLookups++;

            if (_itemsByPath.TryGetValue(path, out var item) && item.ImageIndex != index)
                item.ImageIndex = index;
        }

        private void ResetItemImages()
        {
            if (_icons == null) return;

            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is FileBrowserEntry entry) item.ImageIndex = _icons.IndexOf(entry.Kind);
            }
        }

        /// <summary>
        /// 丢掉内存里的缩略图并按当前可见范围重建（不再全量重排）。
        /// 主题 / DPI / 缓存满了 / 清空缓存都走这里。
        /// </summary>
        private void ResetThumbnails()
        {
            var anchor = _list.View == View.LargeIcon
                ? FirstVisibleLargeIconIndex()
                : (_list.TopItem?.Index ?? 0);
            var anchorTop = anchor >= 0 && anchor < _list.Items.Count
                ? _list.Items[anchor].Bounds.Top
                : 0;

            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _framePending.Clear();
            _thumbnailMisses.Clear();
            _thumbnailIndices.Clear();
            _lastRangeFirst = -1;
            _lastRangeLast = -1;
            _thumbnailQueue?.CancelAll();

            // 顺序不能反：先摘掉 LargeImageList，再释放它里面的位图。
            _list.LargeImageList = null;
            _largeIcons?.Dispose();
            _largeIcons = null;

            foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose();
            _thumbnailBitmaps.Clear();

            ResetItemImages();

            _thumbnailResets++;

            if (!WantsThumbnails) return;

            EnsureLargeIcons();
            _list.LargeImageList = _largeIcons!.Images;

            if (anchor >= 0 && anchor < _list.Items.Count)
            {
                _list.EnsureVisible(anchor);

                var nowTop = _list.Items[anchor].Bounds.Top;
                var delta = nowTop - anchorTop;

                if (delta != 0) SendMessage(_list.Handle, LvmScroll, IntPtr.Zero, (IntPtr)(-delta));
            }

            EnsureThumbnailTimer();
            if (ThumbnailsActive) _thumbnailTimer!.Start();

            RequestVisibleThumbnails();
        }

        /// <summary>释放顺序同样是"先摘再放"：ListView 重绘时不能摸到已经 Dispose 的图。</summary>
        private void DisposeThumbnails()
        {
            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _framePending.Clear();
            _thumbnailMisses.Clear();
            _thumbnailIndices.Clear();

            DisposeThumbnailTimer();

            DrainThumbnailResults(disposeOnly: true);

            if (_thumbnailQueue != null)
            {
                _thumbnailQueue.Ready -= OnThumbnailReadyFromWorker;
                _thumbnailQueue.Dispose();
                _thumbnailQueue = null;
            }

            // 抓帧器（一个 libvlc 实例 + 播放器）也跟着一起放掉。
            if (_thumbnailFrames is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("文件页缩略图：释放抓帧器失败", ex);
                }
            }

            _thumbnailFrames = null;

            _list.LargeImageList = null;
            _largeIcons?.Dispose();
            _largeIcons = null;

            foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose();
            _thumbnailBitmaps.Clear();

            _itemsByPath.Clear();
        }

        /// <summary>主题 / DPI 变了：大图标与缩略图缓存一起作废（索引都是旧的）。</summary>
        private void RebuildThumbnails()
        {
            ResetThumbnails();
        }
    }
}
