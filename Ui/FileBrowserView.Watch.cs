using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 文件页的自动刷新（1.28.0）：盯住当前目录，变化之后<b>静默</b>重读一遍。
    /// <para>
    /// 静默的意思是：不设 <c>_loading</c>、不显示「正在读取…」、不清空列表；读完先比签名，
    /// 一模一样就什么都不做，有变化才换条目并<b>按路径恢复选中项 / 焦点 / 顶部项</b>。
    /// 搜索结果是一次快照，期间不刷新（与 1.26.0 的"不自动刷新结果"一致）。
    /// </para>
    /// <para>
    /// 只有本地目录建 watcher（<see cref="FolderWatch.CanWatch"/>）；检测到目录没了就停掉 watcher
    /// 并走一次普通 <see cref="Reload"/>，让既有的错误路径说话，不自动跳父目录。
    /// </para>
    /// </summary>
    internal sealed partial class FileBrowserView
    {
        private FolderWatch? _watch;
        private bool _silentLoading;
        private bool _silentRefreshQueued;
        private bool _watchSuppressed;

        private int _silentRefreshes;
        private int _silentRebuilds;

        private readonly HashSet<string> _silentSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string? _silentFocus;
        private int _silentTopIndex;

        // ---- 测试钩子 ---------------------------------------------------------

        internal bool IsWatchingForTest => _watch != null;

        internal bool IsSilentRefreshingForTest => _silentLoading;

        internal int WatchOverflowsForTest => _watch?.Overflows ?? 0;

        /// <summary>静默刷新"跑完并应用"的次数（含"内容没变"的那几次）。</summary>
        internal int SilentRefreshCountForTest => _silentRefreshes;

        /// <summary>静默刷新真的重建了列表的次数（内容变了才会加）。</summary>
        internal int SilentRebuildCountForTest => _silentRebuilds;

        internal void RaiseWatchRefreshForTest() => HandleWatchChanged();

        internal void SetAutoRefreshForTest(bool value) => SetAutoRefresh(value);

        // ---- 开关 / 生命周期 ---------------------------------------------------

        internal void SetAutoRefresh(bool value)
        {
            if (_state.AutoRefresh == value) return;

            _state.AutoRefresh = value;
            _watchSuppressed = false;

            RaiseStateChanged();

            if (value) RestartWatch();
            else StopWatch();

            SetStatus(value ? "自动刷新已打开" : "自动刷新已关闭", false);
        }

        /// <summary>读成功之后（重新）盯住当前目录；网络位置 / 此电脑 / 关掉开关都不建。</summary>
        private void RestartWatch()
        {
            StopWatch();

            if (_watchSuppressed || !_state.AutoRefresh) return;
            if (_searchActive || _searching) return;

            var path = _state.CurrentPath;

            if (!FolderWatch.CanWatch(path)) return;

            var watch = new FolderWatch(path!);

            watch.Changed += OnWatchChangedFromThread;
            watch.OverflowExhausted += OnWatchOverflowFromThread;

            if (!watch.Start())
            {
                watch.Changed -= OnWatchChangedFromThread;
                watch.OverflowExhausted -= OnWatchOverflowFromThread;
                watch.Dispose();
                return;
            }

            _watch = watch;
        }

        private void StopWatch()
        {
            var watch = _watch;
            _watch = null;

            if (watch == null) return;

            watch.Changed -= OnWatchChangedFromThread;
            watch.OverflowExhausted -= OnWatchOverflowFromThread;
            watch.Dispose();
        }

        private void OnWatchChangedFromThread()
        {
            if (IsDisposed || !IsHandleCreated) return;

            try
            {
                BeginInvoke(new Action(HandleWatchChanged));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁：这一次变化丢掉。
            }
        }

        private void OnWatchOverflowFromThread()
        {
            if (IsDisposed || !IsHandleCreated) return;

            try
            {
                BeginInvoke(new Action(StopWatchAfterOverflow));
            }
            catch (InvalidOperationException)
            {
                // 同上。
            }
        }

        private void StopWatchAfterOverflow()
        {
            if (IsDisposed) return;

            StopWatch();

            _watchSuppressed = true;

            SetStatus("这个文件夹变化太频繁，自动刷新已暂停（重新打开开关或换个目录）", true);
            AppLog.Warn("文件页自动刷新：事件缓冲连续溢出，本目录停用自动刷新");
        }

        // ---- 静默刷新 ---------------------------------------------------------

        internal void HandleWatchChanged()
        {
            if (IsDisposed) return;

            // 搜索结果是一次快照：不刷新（与 1.26.0 的口径一致）。
            if (_searchActive || _searching) return;

            if (!_state.AutoRefresh)
            {
                StopWatch();
                return;
            }

            if (_loading)
            {
                // 正在读目录（首次加载 / F5）：这一次读回来的就是最新的，没必要再排一次。
                return;
            }

            StartSilentRefresh();
        }

        private void StartSilentRefresh()
        {
            if (_silentLoading)
            {
                _silentRefreshQueued = true;
                return;
            }

            var target = _state.CurrentPath;

            // 此电脑没有"目录变化"可言。
            if (target == null) return;

            var showAll = _state.ShowAllFiles;

            _silentLoading = true;
            CaptureSilentState();

            Task.Run(() => _service.ReadDirectory(target, showAll, CancellationToken.None))
                .ContinueWith(
                    task => ApplySilentRefreshFromThread(target, task),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private void ApplySilentRefreshFromThread(string? target, Task<FileBrowserReadResult> task)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                _silentLoading = false;
                return;
            }

            try
            {
                BeginInvoke(new Action(() => ApplySilentRefresh(target, task)));
            }
            catch (InvalidOperationException)
            {
                _silentLoading = false;
            }
        }

        private void ApplySilentRefresh(string? target, Task<FileBrowserReadResult> task)
        {
            _silentLoading = false;

            if (IsDisposed) return;

            // 期间导航走了：结果属于上一个目录，丢掉。
            if (!string.Equals(target, _state.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

            if (!task.IsCompletedSuccessfully)
            {
                AppLog.Swallowed("文件页自动刷新：读取失败", task.Exception);
                return;
            }

            var result = task.Result;

            if (result.Cancelled) return;

            if (!string.IsNullOrEmpty(result.Error))
            {
                // 目录没了 / 拔盘：停掉 watcher，走一次普通刷新让用户看到既有错误提示。
                StopWatch();
                Reload();
                return;
            }

            var changed = !string.Equals(Signature(_entries), Signature(result.Entries), StringComparison.Ordinal);

            _silentRefreshes++;

            _isRoots = result.IsRoots;
            _truncated = result.Truncated;

            if (changed)
            {
                _silentRebuilds++;

                _entries = result.Entries;
                FileBrowserService.Sort(_entries, _state.SortField, _state.SortDescending);

                FillList();
                RestoreSilentState();
                UpdateStatusCount();
            }

            if (_silentRefreshQueued)
            {
                _silentRefreshQueued = false;
                StartSilentRefresh();
            }
        }

        private void CaptureSilentState()
        {
            _silentSelection.Clear();

            foreach (ListViewItem item in _list.SelectedItems)
            {
                if (item.Tag is FileBrowserEntry entry) _silentSelection.Add(entry.FullPath);
            }

            _silentFocus = _list.FocusedItem?.Tag is FileBrowserEntry focused ? focused.FullPath : null;
            _silentTopIndex = _list.TopItem?.Index ?? 0;
        }

        private void RestoreSilentState()
        {
            try
            {
                _list.BeginUpdate();

                _list.SelectedItems.Clear();

                foreach (ListViewItem item in _list.Items)
                {
                    if (item.Tag is not FileBrowserEntry entry) continue;

                    if (_silentSelection.Contains(entry.FullPath)) item.Selected = true;

                    if (_silentFocus == null ||
                        !string.Equals(entry.FullPath, _silentFocus, StringComparison.OrdinalIgnoreCase)) continue;

                    item.Focused = true;

                    if (item.Selected) item.EnsureVisible();
                }

                if (_list.View == View.Details && _silentTopIndex > 0 && _silentTopIndex < _list.Items.Count)
                    _list.TopItem = _list.Items[_silentTopIndex];
            }
            finally
            {
                _list.EndUpdate();
            }
        }

        /// <summary>条目的"同一份内容"签名：路径 + 大小 + 修改时间（顺序无关）。</summary>
        private static string Signature(IReadOnlyList<FileBrowserEntry> entries)
        {
            var parts = new List<string>(entries.Count);

            foreach (var entry in entries)
                parts.Add(entry.FullPath + "|" + entry.Size + "|" + entry.Modified.Ticks);

            parts.Sort(StringComparer.OrdinalIgnoreCase);

            return string.Join("\n", parts);
        }
    }
}
