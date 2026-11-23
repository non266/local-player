// 检查点 38（第 33 个运行步骤）：文件页缩略图性能（1.30.0）。
//
// 合成 600 个条目 + 每次 2 ms 的假封面，验：
//   ① 打开时只按可见范围排（不是整个目录）；② 可见项先出图、UI 不被占住；
//   ③ 滚到底能补图，且缓存满了只重建可见范围（不反复重排整个目录）；
//   ④ 预算 300 真的截断；⑤ 视频截帧在飞时换目录，封面不被挡住。

using System.Diagnostics;

namespace SmokeTest
{
    internal static partial class Program
    {
        private const string ThumbPerfRoot = @"C:\smoke-thumb-perf";
        private const string ThumbPerfVideoRoot = @"C:\smoke-thumb-perf-video";
        private const int ThumbPerfCount = 600;

        private static bool TestFileBrowserThumbnailPerformance()
        {
            using var form = new 播放器.MainForm(Array.Empty<string>());

            try
            {
                form.Show();
                PumpMessages(300);

                var sidebar = Find(form, "sidebarPanel") as SidebarPanel;

                if (sidebar == null)
                {
                    Log(38, "缩略图性能：找不到侧栏");
                    return false;
                }

                sidebar.ActiveTab = SidebarTab.Files;
                PumpMessages(200);

                var view = form.FileBrowser;
                var service = new SyntheticThumbnailService();

                view.Service = service;
                view.SetAutoRefreshForTest(false);
                view.SetVideoFramesForTest(null);
                view.ThumbnailSource = new SlowCoverArtLoader(TimeSpan.FromMilliseconds(2));
                view.SetThumbnailsForTest(false);
                view.SetThumbnailBudgetForTest(ThumbPerfCount);

                view.NavigateForTest(ThumbPerfRoot);

                if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count == ThumbPerfCount, 8000))
                {
                    Log(38, $"缩略图性能：合成目录没有读出来（{view.List.Items.Count} 行，"
                            + $"状态：{view.StatusText}，path=「{view.State.CurrentPath}」）");
                    return false;
                }

                var visibleBefore = 0;
                var resetsBefore = view.ThumbnailResetCountForTest;
                var heartbeat = new UiHeartbeat();
                var openWatch = Stopwatch.StartNew();

                view.SetThumbnailsForTest(true);

                var opened = PumpUntil(
                    () => !view.IsLoading
                          && view.PendingThumbnailCountForTest == 0
                          && !view.IsThumbnailBusyForTest,
                    10000);

                openWatch.Stop();
                var openGap = heartbeat.MaxGapMilliseconds;
                heartbeat.Dispose();

                var openRequests = view.ThumbnailRequestCountForTest;
                var openApplied = view.ThumbnailAppliedCountForTest;
                var openBatches = view.ThumbnailApplyBatchCountForTest;
                var openLookups = view.ThumbnailLookupCountForTest;
                var openVisibleWithThumbs = VisibleThumbnailCount(view);
                visibleBefore = VisibleItemCount(view);
                var openResets = view.ThumbnailResetCountForTest - resetsBefore;

                Log(38, $"缩略图性能（打开 {ThumbPerfCount} 项）：稳定={opened}、"
                        + $"{openWatch.ElapsedMilliseconds} ms、请求 {openRequests}、贴图 {openApplied}、"
                        + $"批 {openBatches}、查找 {openLookups}、缩略图 {view.ThumbnailCountForTest}、"
                        + $"可见 {visibleBefore} 项 / 有图 {openVisibleWithThumbs}、"
                        + $"来源调用 {((SlowCoverArtLoader)view.ThumbnailSource).Calls}、"
                        + $"复位 +{openResets}、UI 最大停顿 {openGap} ms");

                if (!opened)
                {
                    Log(38, "缩略图性能：打开之后队列没有稳定下来（可能又进了复位 → 重排）");
                    return false;
                }

                if (visibleBefore <= 0 || openVisibleWithThumbs != visibleBefore)
                {
                    Log(38, $"缩略图性能：可见项没有全部出图（可见 {visibleBefore} / 有图 {openVisibleWithThumbs}）");
                    return false;
                }

                if (openRequests <= 0 || openRequests > 300)
                {
                    Log(38, $"缩略图性能：打开时排了 {openRequests} 个请求，期望只排可见范围（≤ 300）");
                    return false;
                }

                if (openRequests >= ThumbPerfCount)
                {
                    Log(38, $"缩略图性能：打开时排了 {openRequests} 个请求，等于整个目录（{ThumbPerfCount}）");
                    return false;
                }

                if (openBatches <= 0 || openBatches > openApplied / 4 + 2)
                {
                    Log(38, $"缩略图性能：贴图批次 {openBatches}，贴图 {openApplied}（没有批量）");
                    return false;
                }

                if (openLookups > openApplied * 2)
                {
                    Log(38, $"缩略图性能：查找 {openLookups} 次 / 贴图 {openApplied} 次（还在全表扫）");
                    return false;
                }

                if (openGap > 300)
                {
                    Log(38, $"缩略图性能：打开时 UI 停顿 {openGap} ms（期望 ≤ 300）");
                    return false;
                }

                // 稳定之后不能再有新请求：这是"没有复位环"的直接证据。
                var requestSnapshot = openRequests;
                var appliedSnapshot = openApplied;
                var viewUpdatesSnapshot = view.ThumbnailViewUpdateCountForTest;
                PumpMessages(1000);

                if (view.ThumbnailViewUpdateCountForTest != viewUpdatesSnapshot)
                {
                    Log(38, $"缩略图性能：空闲时还在重绘（Begin/EndUpdate {viewUpdatesSnapshot} → "
                            + $"{view.ThumbnailViewUpdateCountForTest}）——这会让缩略图一直闪");
                    return false;
                }

                if (view.ThumbnailRequestCountForTest != requestSnapshot
                    || view.ThumbnailAppliedCountForTest != appliedSnapshot)
                {
                    Log(38, $"缩略图性能：稳定后又起了请求（{requestSnapshot} → {view.ThumbnailRequestCountForTest}，"
                            + $"贴图 {appliedSnapshot} → {view.ThumbnailAppliedCountForTest}）");
                    return false;
                }

                // ---- 滚到底：补图 + 缓存满了只重建可见范围 ----
                var scrollHeartbeat = new UiHeartbeat();
                var beforeScrollRequests = view.ThumbnailRequestCountForTest;
                var resetsBeforeScroll = view.ThumbnailResetCountForTest;

                for (var index = 20; index < ThumbPerfCount; index += 20)
                {
                    view.List.Items[index].EnsureVisible();
                    PumpMessages(250);
                }

                view.List.Items[ThumbPerfCount - 1].EnsureVisible();
                PumpMessages(300);

                var scrolled = PumpUntil(
                    () => view.PendingThumbnailCountForTest == 0 && !view.IsThumbnailBusyForTest,
                    10000);

                var scrollGap = scrollHeartbeat.MaxGapMilliseconds;
                scrollHeartbeat.Dispose();

                var resets = view.ThumbnailResetCountForTest - resetsBeforeScroll;
                var lastHasThumb = view.ImageIndexForTest($"song-{ThumbPerfCount - 1:D3}.mp3")
                                   >= view.TypeIconCountForTest;

                Log(38, $"缩略图性能（滚到底）：稳定={scrolled}、请求 {beforeScrollRequests} → "
                        + $"{view.ThumbnailRequestCountForTest}、复位 {resets}、缩略图 {view.ThumbnailCountForTest}、"
                        + $"末项有图={lastHasThumb}、来源调用 {((SlowCoverArtLoader)view.ThumbnailSource).Calls}、"
                        + $"UI 最大停顿 {scrollGap} ms");

                if (!scrolled || !lastHasThumb)
                {
                    Log(38, "缩略图性能：滚到底之后末项没有缩略图 / 队列没有稳定");
                    return false;
                }

                if (resets is < 1 or > 3)
                {
                    Log(38, $"缩略图性能：滚到底的过程中复位了 {resets} 次（期望 1～3）");
                    return false;
                }

                if (view.ThumbnailRequestCountForTest <= beforeScrollRequests)
                {
                    Log(38, "缩略图性能：滚动之后没有补新的请求（可见范围没跟着走）");
                    return false;
                }

                if (scrollGap > 300)
                {
                    Log(38, $"缩略图性能：滚动时 UI 停顿 {scrollGap} ms（期望 ≤ 300）");
                    return false;
                }

                // 复位（这里用清内存入口）之后滚动位置不能跳回顶部。
                var firstBefore = view.FirstVisibleIndexForTest;
                view.ClearThumbnailMemoryForTest();

                if (!PumpUntil(
                        () => view.PendingThumbnailCountForTest == 0 && !view.IsThumbnailBusyForTest,
                        8000))
                {
                    Log(38, "缩略图性能：复位之后队列没有稳定");
                    return false;
                }

                var firstAfter = view.FirstVisibleIndexForTest;

                Log(38, $"缩略图性能（复位）：首个可见 {firstBefore} → {firstAfter}、"
                        + $"末项有图 {view.ImageIndexForTest($"song-{ThumbPerfCount - 1:D3}.mp3") >= view.TypeIconCountForTest}、"
                        + $"复位 {view.ThumbnailResetCountForTest}");

                if (Math.Abs(firstAfter - firstBefore) > 20)
                {
                    Log(38, $"缩略图性能：复位之后滚动位置跳了（{firstBefore} → {firstAfter}）");
                    return false;
                }

                // ---- 预算 300：看得到的顺序，最多 300 个 ----
                view.SetThumbnailBudgetForTest(300);
                PumpMessages(200);

                for (var index = 20; index < ThumbPerfCount; index += 20)
                {
                    view.List.Items[index].EnsureVisible();
                    PumpMessages(250);
                }

                if (!PumpUntil(
                        () => view.PendingThumbnailCountForTest == 0 && !view.IsThumbnailBusyForTest,
                        8000))
                {
                    Log(38, "缩略图性能：预算测试的队列没有稳定");
                    return false;
                }

                Log(38, $"缩略图性能（预算 300）：已选 {view.ThumbnailChosenCountForTest}、"
                        + $"跳过 {view.ThumbnailsSkippedForTest}、缩略图 {view.ThumbnailCountForTest}、"
                        + $"状态「{view.StatusText}」");

                if (view.ThumbnailChosenCountForTest > 300 || view.ThumbnailsSkippedForTest <= 0)
                {
                    Log(38, $"缩略图性能：预算 300 没有截断（已选 {view.ThumbnailChosenCountForTest}、"
                            + $"跳过 {view.ThumbnailsSkippedForTest}）");
                    return false;
                }

                if (!view.StatusText.Contains("缩略图只做了前 300", StringComparison.Ordinal))
                {
                    Log(38, "缩略图性能：预算截断没有写进状态行（" + view.StatusText + "）");
                    return false;
                }

                // ---- 隐藏文件页：停止读盘；重新显示不炸 ----
                var hiddenRequests = view.ThumbnailRequestCountForTest;
                view.Visible = false;

                if (!PumpUntil(() => view.PendingThumbnailCountForTest == 0, 3000))
                {
                    Log(38, "缩略图性能：隐藏之后队列没有停下（pending "
                            + view.PendingThumbnailCountForTest + "）");
                    return false;
                }

                PumpMessages(600);

                if (view.ThumbnailRequestCountForTest != hiddenRequests)
                {
                    Log(38, "缩略图性能：隐藏之后还在排新的请求");
                    return false;
                }

                view.Visible = true;
                PumpMessages(400);

                // ---- 视频截帧在飞时换目录：封面不能被挡住 ----
                var blocking = new BlockingFrameSource();
                view.ThumbnailSource = new SlowCoverArtLoader(TimeSpan.FromMilliseconds(2), nullForVideo: true);
                view.SetVideoFramesForTest(blocking);
                view.SetThumbnailsForTest(false);
                view.NavigateForTest(ThumbPerfVideoRoot);

                if (!PumpUntil(() => !view.IsLoading && view.List.Items.Count == 4, 8000))
                {
                    Log(38, "缩略图性能：视频夹没有读出来（" + view.List.Items.Count + " 行）");
                    return false;
                }

                view.SetThumbnailsForTest(true);

                if (!PumpUntil(() => blocking.Started, 8000))
                {
                    Log(38, "缩略图性能：截帧没有开始（视频帧来源未被调用）");
                    return false;
                }

                var navigateWatch = Stopwatch.StartNew();
                view.NavigateForTest(ThumbPerfRoot);
                var audioReady = PumpUntil(
                    () => !view.IsLoading
                          && view.PendingThumbnailCountForTest == 0
                          && view.ImageIndexForTest("song-000.mp3") >= view.TypeIconCountForTest,
                    5000);
                navigateWatch.Stop();

                Log(38, $"缩略图性能（截帧在飞时导航）：首图就绪={audioReady}、"
                        + $"{navigateWatch.ElapsedMilliseconds} ms、截帧已取消={blocking.Cancelled}、"
                        + $"截帧开始={blocking.Started}");

                if (!audioReady || navigateWatch.ElapsedMilliseconds >= 1500 || !blocking.Cancelled)
                {
                    Log(38, "缩略图性能：截帧在飞时导航被挡住了（封面等了截帧）");
                    return false;
                }

                Log(38, "文件页缩略图性能正常：只排可见范围、可见项先出图、批量贴图、"
                        + "滚动补图、复位只重建可见范围、预算截断、隐藏即停、截帧可取消");
                return true;
            }
            finally
            {
                var view = form.FileBrowser;

                view.SetThumbnailsForTest(false);
                view.ThumbnailSource = new CoverArtLoader();
                view.SetVideoFramesForTest(null);
                view.SetThumbnailBudgetForTest(FileBrowserThumbnailBudgets.Default);
                view.ClearThumbnailCacheForTest();

                form.Close();
                PumpMessages(200);
            }
        }

        private static int VisibleItemCount(FileBrowserView view)
        {
            var height = view.List.ClientSize.Height;

            return view.List.Items
                .Cast<ListViewItem>()
                .Count(item => item.Bounds.Bottom > 0 && item.Bounds.Top < height);
        }

        private static int VisibleThumbnailCount(FileBrowserView view)
        {
            var height = view.List.ClientSize.Height;
            var icons = view.TypeIconCountForTest;

            return view.List.Items
                .Cast<ListViewItem>()
                .Count(item => item.Bounds.Bottom > 0
                               && item.Bounds.Top < height
                               && item.ImageIndex >= icons);
        }

        /// <summary>合成目录：不落盘，避免为了量性能先造 600 个文件。</summary>
        private sealed class SyntheticThumbnailService : FileBrowserService
        {
            internal override FileBrowserReadResult ReadDirectory(
                string? path, bool showAllFiles, CancellationToken token)
            {
                var result = new FileBrowserReadResult();

                if (string.Equals(path, ThumbPerfVideoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var name = $"clip-{i}.mp4";

                        result.Entries.Add(new FileBrowserEntry(
                            name,
                            Path.Combine(ThumbPerfVideoRoot, name),
                            FileBrowserKind.Video,
                            4096 + i,
                            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i)));
                    }

                    return result;
                }

                if (!string.Equals(path, ThumbPerfRoot, StringComparison.OrdinalIgnoreCase))
                    return result;

                for (var i = 0; i < ThumbPerfCount; i++)
                {
                    var name = $"song-{i:D3}.mp3";

                    result.Entries.Add(new FileBrowserEntry(
                        name,
                        Path.Combine(ThumbPerfRoot, name),
                        FileBrowserKind.Audio,
                        1024 + i,
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i)));
                }

                return result;
            }

            internal override FileBrowserReadResult ListRoots(CancellationToken token) =>
                new FileBrowserReadResult { IsRoots = true };
        }

        /// <summary>假封面：每次 2 ms，返回一张纯色 PNG；只数调用次数，不碰真实文件。</summary>
        private sealed class SlowCoverArtLoader : CoverArtLoader
        {
            private readonly TimeSpan _delay;
            private readonly bool _nullForVideo;
            private readonly byte[] _png = MakeSolidPng(Color.FromArgb(0x33, 0x99, 0xBB), 64);
            private int _calls;

            internal SlowCoverArtLoader(TimeSpan delay, bool nullForVideo = false)
            {
                _delay = delay;
                _nullForVideo = nullForVideo;
            }

            internal int Calls => Volatile.Read(ref _calls);

            internal override byte[]? Load(string path)
            {
                if (_nullForVideo && string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase))
                    return null;

                Interlocked.Increment(ref _calls);

                if (_delay > TimeSpan.Zero) Thread.Sleep(_delay);

                return _png;
            }
        }

        /// <summary>慢截帧：每 25 ms 看一次取消；不取消就等满 2 秒再给颜色。</summary>
        private sealed class BlockingFrameSource : IVideoFrameSource
        {
            private int _started;
            private int _cancelled;

            internal bool Started => Volatile.Read(ref _started) == 1;

            internal bool Cancelled => Volatile.Read(ref _cancelled) == 1;

            public Bitmap? Grab(string path, int edge, Func<bool>? isCancelled = null)
            {
                Interlocked.Exchange(ref _started, 1);

                var deadline = Environment.TickCount64 + 2000;

                while (Environment.TickCount64 < deadline)
                {
                    if (isCancelled?.Invoke() == true)
                    {
                        Interlocked.Exchange(ref _cancelled, 1);
                        return null;
                    }

                    Thread.Sleep(25);
                }

                return MakeSolidBitmap(Color.FromArgb(0xEE, 0x88, 0x22), edge);
            }
        }

        /// <summary>25 ms 一拍的心跳：Tick 之间最长的一次间隔就是 UI 线程被占住的时间。</summary>
        private sealed class UiHeartbeat : IDisposable
        {
            private readonly System.Windows.Forms.Timer _timer = new() { Interval = 25 };
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private long _last = -1;

            internal UiHeartbeat()
            {
                _timer.Tick += (s, e) =>
                {
                    var now = _watch.ElapsedMilliseconds;

                    if (_last >= 0)
                    {
                        var gap = now - _last;
                        if (gap > MaxGapMilliseconds) MaxGapMilliseconds = gap;
                    }

                    _last = now;
                };

                _timer.Start();
            }

            internal long MaxGapMilliseconds { get; private set; }

            public void Dispose()
            {
                _timer.Stop();
                _timer.Dispose();
            }
        }
    }
}
