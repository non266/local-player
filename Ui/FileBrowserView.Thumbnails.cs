using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 文件页的「平铺缩略图」：大图标集、封面缓存、后台排队与贴图。
    /// <para>
    /// 缩略图是音视频文件的<b>封面</b>（内嵌图优先，其次同目录外挂图），
    /// <b>不从视频画面截帧</b>、不认图片文件、不落盘。解码只在 <see cref="ThumbnailQueue"/>
    /// 的专用线程上做；界面线程只负责把结果贴到行上，迟到的结果按代际号丢掉。
    /// </para>
    /// <para>
    /// 平铺只是"换一种列表形态"：<c>_entries</c> / `_searchResults` 两套数据不动，
    /// 索引、拖放、双击、右键、键盘全都沿用原来的那几段代码。
    /// </para>
    /// </summary>
    internal sealed partial class FileBrowserView
    {
        /// <summary>缩略图边长（逻辑像素，按 DPI 缩放）。</summary>
        private const int ThumbnailEdgeLogical = 56;

        /// <summary>内存缓存上限（条）。满了整份丢掉重建：比维护 LRU 简单，代价可接受。</summary>
        private const int ThumbnailCapacity = 512;

        /// <summary>每目录最多给多少个文件做缩略图（默认值）。</summary>
        private const int ThumbnailRequestDefault = 300;

        private FileIconSet? _largeIcons;

        private readonly List<Bitmap> _thumbnailBitmaps = new List<Bitmap>();

        private readonly Dictionary<string, int> _thumbnailIndices =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _thumbnailMisses =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<string> _thumbnailPending =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ThumbnailQueue? _thumbnailQueue;
        private CoverArtLoader _thumbnailSource = new CoverArtLoader();
        private int _thumbnailVersion;

        /// <summary>因为超过每目录预算而没做缩略图的文件数（状态行要说明）。</summary>
        private int _thumbnailSkipped;

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

        // ---- 测试钩子（只加只读属性 / 方法，避免 WFO1000）---------------------

        internal bool IsTilesForTest => _list.View == View.LargeIcon;

        internal int ThumbnailEdgeForTest => ThumbnailEdge;

        internal int ThumbnailCountForTest => _thumbnailIndices.Count;

        internal int PendingThumbnailCountForTest => _thumbnailQueue?.Pending ?? 0;

        internal bool IsThumbnailBusyForTest => _thumbnailQueue?.Busy ?? false;

        internal int TypeIconCountForTest => _icons?.Images.Images.Count ?? 0;

        internal IReadOnlyCollection<string> ThumbnailKeysForTest =>
            new List<string>(_thumbnailIndices.Keys);

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
                _list.LargeImageList = _largeIcons!.Images;
                _list.FullRowSelect = false;
                _list.View = View.LargeIcon;
                return;
            }

            if (_list.View != View.Details) _list.View = View.Details;

            _list.FullRowSelect = true;
            ResetItemImages();
            AdjustColumns();
        }

        /// <summary>列表填完之后按当前源排队；详情下把没跑完的请求取消掉。</summary>
        private void RefreshThumbnails()
        {
            if (WantsThumbnails) RequestThumbnails();
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

            _thumbnailQueue = new ThumbnailQueue(_thumbnailSource);
            _thumbnailQueue.Ready += OnThumbnailReadyFromWorker;
        }

        // ---- 排队 -------------------------------------------------------------

        private static bool ThumbnailApplies(FileBrowserEntry entry) =>
            !entry.IsDirectory
            && entry.Kind is FileBrowserKind.Video or FileBrowserKind.Audio
            && !MediaFormats.IsNetworkPath(entry.FullPath);

        private static string ThumbnailKey(FileBrowserEntry entry) =>
            entry.FullPath + "|" + entry.Size.ToString() + "|" + entry.Modified.Ticks.ToString();

        /// <summary>
        /// 按当前列表顺序排队：命中的贴缓存、没封面的跳过（负缓存）、其余交给后台队列。
        /// 只有前 <see cref="MaxThumbnails"/> 个音视频文件占预算，超出的在状态行说明。
        /// </summary>
        private void RequestThumbnails()
        {
            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _thumbnailSkipped = 0;
            _thumbnailQueue?.CancelAll();

            if (!WantsThumbnails) return;

            EnsureThumbnailQueue();

            var edge = ThumbnailEdge;
            var budget = Math.Max(0, MaxThumbnails);
            var used = 0;
            var version = _thumbnailVersion;

            foreach (var entry in _visibleEntries)
            {
                if (!ThumbnailApplies(entry)) continue;

                if (used >= budget)
                {
                    _thumbnailSkipped++;
                    continue;
                }

                used++;

                var key = ThumbnailKey(entry);

                if (_thumbnailIndices.TryGetValue(key, out var index))
                {
                    SetItemImage(entry.FullPath, index);
                    continue;
                }

                if (_thumbnailMisses.Contains(key)) continue;
                if (!_thumbnailPending.Add(key)) continue;

                _thumbnailQueue!.Request(key, entry.FullPath, edge, version);
            }
        }

        private void CancelThumbnails()
        {
            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _thumbnailSkipped = 0;
            _thumbnailQueue?.CancelAll();
        }

        // ---- 贴图 -------------------------------------------------------------

        private void OnThumbnailReadyFromWorker(ThumbnailResult result)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                result.Bitmap?.Dispose();
                return;
            }

            try
            {
                // 进度 / 结果一律用控件自己的 BeginInvoke 搬回 UI 线程，不用 Progress<T>：
                // 测试宿主里 SynchronizationContext 可能为空，那样回调会跑到线程池上与列表抢。
                BeginInvoke(new Action(() => ApplyThumbnail(result)));
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件页缩略图排队贴图失败", ex);
                result.Bitmap?.Dispose();
            }
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
                    // 缓存满了：整份丢掉重建，别让一个会话越浏览越占内存。
                    result.Bitmap.Dispose();
                    ResetThumbnails();
                    return;
                }

                var index = AddThumbnail(result.Bitmap);
                _thumbnailIndices[result.Key] = index;
                SetItemImage(result.Path, index);
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
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is not FileBrowserEntry entry) continue;
                if (!string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase)) continue;

                item.ImageIndex = index;
                return;
            }
        }

        private void ResetItemImages()
        {
            if (_icons == null) return;

            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is FileBrowserEntry entry) item.ImageIndex = _icons.IndexOf(entry.Kind);
            }
        }

        private void ResetThumbnails()
        {
            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _thumbnailMisses.Clear();
            _thumbnailIndices.Clear();
            _thumbnailQueue?.CancelAll();

            // 顺序不能反：先摘掉 LargeImageList，再释放它里面的位图。
            _list.LargeImageList = null;
            _largeIcons?.Dispose();
            _largeIcons = null;

            foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose();
            _thumbnailBitmaps.Clear();

            ResetItemImages();

            if (!WantsThumbnails) return;

            EnsureLargeIcons();
            _list.LargeImageList = _largeIcons!.Images;
            RequestThumbnails();
        }

        /// <summary>释放顺序同样是"先摘再放"：ListView 重绘时不能摸到已经 Dispose 的图。</summary>
        private void DisposeThumbnails()
        {
            _thumbnailVersion++;
            _thumbnailPending.Clear();
            _thumbnailMisses.Clear();
            _thumbnailIndices.Clear();

            if (_thumbnailQueue != null)
            {
                _thumbnailQueue.Ready -= OnThumbnailReadyFromWorker;
                _thumbnailQueue.Dispose();
                _thumbnailQueue = null;
            }

            _list.LargeImageList = null;
            _largeIcons?.Dispose();
            _largeIcons = null;

            foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose();
            _thumbnailBitmaps.Clear();
        }

        /// <summary>主题 / DPI 变了：大图标与缩略图缓存一起作废（索引都是旧的）。</summary>
        private void RebuildThumbnails()
        {
            ResetThumbnails();
        }

        /// <summary>
        /// 平铺模式的滚轮：按"当前第一项可见项"的 <see cref="ListViewItem.Bounds"/> 算目标行再
        /// <c>EnsureVisible</c>。图标视图里没有详情那套 <c>TopItem</c> 语义，不能照抄。
        /// </summary>
        private void ScrollTilesBy(int offset)
        {
            if (_list.Items.Count == 0) return;

            var items = _list.Items;
            var first = 0;
            var bestTop = int.MaxValue;

            for (var i = 0; i < items.Count; i++)
            {
                var bounds = items[i].Bounds;

                if (bounds.Bottom <= 0) continue;
                if (bounds.Top >= bestTop) continue;

                bestTop = bounds.Top;
                first = i;
            }

            var target = Math.Clamp(first + offset, 0, items.Count - 1);
            items[target].EnsureVisible();
        }
    }
}
