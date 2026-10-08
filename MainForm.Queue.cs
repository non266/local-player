using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 「下一首播放」队列：把选中的几项排到当前这首后面（临时插播，最多
    /// <see cref="PlaybackQueue.Capacity"/> 条、不重复），当前这首<b>自然播完</b>时先播队首，
    /// 队列空了再回到列表原来的位置继续。
    /// <para>
    /// 队列和列表是两件事：插播时<b>不动列表的当前项</b>（<c>PlayIndex(…, keepCurrent: true)</c>），
    /// 所以"回到列表继续"是从插播之前那一项的下一条接着走——这正是按「下一首播放」时想要的意思。
    /// 队列不落盘，关掉程序就没了（见 <see cref="PlaybackQueue"/>）。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private readonly PlaybackQueue _queue = new PlaybackQueue();

        private ToolStripMenuItem? _menuPlayNext;

        private ToolStripMenuItem? _menuClearQueue;

        /// <summary>只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。</summary>
        internal PlaybackQueue Queue => _queue;

        internal ToolStripMenuItem? MenuPlayNext => _menuPlayNext;

        internal ToolStripMenuItem? MenuClearQueue => _menuClearQueue;

        private void ConfigureQueueMenu()
        {
            menuPlayback.DropDownItems.Add(new ToolStripSeparator());

            _menuPlayNext = new ToolStripMenuItem("下一首播放") { Name = "menuPlayNext" };
            _menuPlayNext.Click += (s, e) => QueueSelectedForNext();
            menuPlayback.DropDownItems.Add(_menuPlayNext);

            _menuClearQueue = new ToolStripMenuItem("清空下一首队列") { Name = "menuClearQueue" };
            _menuClearQueue.Click += (s, e) => ClearPlayNextQueue();
            menuPlayback.DropDownItems.Add(_menuClearQueue);

            // 队列长度只有下拉那一刻才是新的（跟 A-B 循环那几项一个道理）。
            // 右键菜单同理：它的"清空"项也要按队列空不空来置灰。
            menuPlayback.DropDownOpening += (s, e) => RefreshQueueMenu();
            contextMenuPlaylist.Opening += (s, e) => RefreshQueueMenu();
            RefreshQueueMenu();
        }

        private void RefreshQueueMenu()
        {
            var hasQueue = _queue.Count > 0;

            if (_menuPlayNext != null)
            {
                _menuPlayNext.Enabled = _playlist.Count > 0;
                _menuPlayNext.Text = _queue.Count == 0
                    ? "下一首播放"
                    : $"下一首播放（队列里 {_queue.Count} 项）";
            }

            if (_menuClearQueue != null)
            {
                _menuClearQueue.Enabled = hasQueue;
                _menuClearQueue.Text = _queue.Count == 0
                    ? "清空下一首队列"
                    : $"清空下一首队列（{_queue.Count} 项）";
            }

            cmsPlayNext.Enabled = _playlist.Count > 0;
            cmsClearQueue.Enabled = hasQueue;
        }

        /// <summary>把列表里选中的那几项排进「下一首播放」（右键菜单与「播放」菜单共用）。</summary>
        private void QueueSelectedForNext() => QueueForNext(GetSelectedPlaylistIndices());

        private void QueueForNext(IReadOnlyList<int> indexes)
        {
            if (indexes.Count == 0)
            {
                SetStatus("先在列表里选一项，再点「下一首播放」");
                return;
            }

            var added = 0;
            var already = 0;
            var full = 0;

            foreach (var index in indexes)
            {
                if (index < 0 || index >= _playlist.Count) continue;

                switch (_queue.Enqueue(_playlist.Items[index].FilePath))
                {
                    case QueueAddResult.Added:
                        added++;
                        break;
                    case QueueAddResult.AlreadyQueued:
                        already++;
                        break;
                    case QueueAddResult.Full:
                        full++;
                        break;
                }
            }

            MarkQueuedItems();
            RefreshQueueMenu();

            // 一条都没进也要说清为什么，别让用户以为点了没反应。
            if (added == 0)
            {
                SetStatus(full > 0
                    ? $"「下一首播放」队列最多 {PlaybackQueue.Capacity} 条，现在满了"
                    : $"选中的 {already} 条已经在「下一首播放」队列里了");
                return;
            }

            var extra = already > 0 ? $"，另有 {already} 条本来就在队里" : string.Empty;
            var fullNote = full > 0 ? $"，{full} 条没进（队列最多 {PlaybackQueue.Capacity} 条）" : string.Empty;

            SetStatus($"已排入「下一首播放」{added} 条{extra}{fullNote}"
                    + $"（队列里现在有 {_queue.Count} 项）");
        }

        private void ClearPlayNextQueue()
        {
            if (_queue.Count == 0)
            {
                SetStatus("「下一首播放」队列本来就是空的");
                return;
            }

            var count = _queue.Count;
            _queue.Clear();
            MarkQueuedItems();
            RefreshQueueMenu();

            SetStatus($"已清空「下一首播放」队列（{count} 项）");
        }

        /// <summary>
        /// 把队列里的路径同步到列表项的 <c>▶</c> 标记上。
        /// <para>队列只认路径，标记挂在列表项上，所以每次队列一变都要对一遍。</para>
        /// </summary>
        private void MarkQueuedItems()
        {
            var changed = false;

            foreach (var item in _playlist.Items)
            {
                var queued = _queue.Contains(item.FilePath);
                if (item.Queued == queued) continue;

                item.Queued = queued;
                changed = true;
            }

            if (changed) RefreshPlaylistView();
        }

        /// <summary>状态栏里那句"队列里还有几项"（队空时什么都不加）。</summary>
        private string QueueStatusSuffix() =>
            _queue.Count == 0 ? string.Empty : $"（「下一首播放」队列里还有 {_queue.Count} 项）";

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
                    MarkQueuedItems();
                    RefreshQueueMenu();
                    continue;
                }

                MarkQueuedItems();
                RefreshQueueMenu();

                PlayIndex(index, keepCurrent: true);
                return true;
            }
        }

        /// <summary>
        /// 正在播的这一项在列表里的索引（按引擎真正打开的路径找）。
        /// <para>
        /// 不能只看 <c>_playlist.CurrentIndex</c>：插播时列表当前项是<b>刻意不动</b>的
        /// （见 <see cref="PlayNextFromQueue"/>），照它找会指到另外一首。
        /// </para>
        /// <para>不在列表里（不在 / 还没播）返回 <c>-1</c>。</para>
        /// </summary>
        private int PlayingPlaylistIndex()
        {
            var path = _engine.CurrentPath;
            if (string.IsNullOrEmpty(path)) return -1;

            return _playlist.IndexOfPath(path);
        }

        /// <summary>
        /// 正在播的这一份怎么称呼（状态栏用）。
        /// <para>同样按引擎路径认，不按列表当前项——否则插播时会报成另外一首。</para>
        /// </summary>
        private string NowPlayingDisplayName()
        {
            var path = _engine.CurrentPath;
            if (string.IsNullOrEmpty(path)) return _playlist.Current?.DisplayName ?? string.Empty;

            var item = _playlist.Items.FirstOrDefault(
                candidate => string.Equals(candidate.FilePath, path, System.StringComparison.OrdinalIgnoreCase));

            if (item != null) return item.DisplayName;

            try
            {
                return Path.GetFileName(path);
            }
            catch (System.ArgumentException)
            {
                return path;
            }
        }
    }
}
