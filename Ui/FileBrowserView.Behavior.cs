using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>文件页上能发起的动作（测试与右键菜单共用）。</summary>
    internal enum FileBrowserAction
    {
        Play,
        Add,
        Queue,
        PlayFolder
    }

    internal sealed partial class FileBrowserView
    {
        private bool _isRoots;

        /// <summary>第一次加载失败时回此电脑用的标记（只对"恢复上次目录"生效）。</summary>
        private bool _initialLoadPending;

        // ---- 加载 -------------------------------------------------------------

        internal void EnsureLoaded()
        {
            if (_loadedOnce) return;

            _initialLoadPending = _state.CurrentPath != null;
            _loadedOnce = true;
            StartLoad();
        }

        private void StartLoad()
        {
            if (!IsHandleCreated)
            {
                // 还没建句柄：等 OnHandleCreated / OnVisibleChanged 再来一次，别把结果丢在半路上。
                _loadedOnce = false;
                return;
            }

            _loadedOnce = true;

            var target = _state.CurrentPath;

            CancelLoad();
            StopWatch();

            var cts = new CancellationTokenSource();
            _loadCts = cts;

            var version = ++_loadVersion;
            var showAll = _state.ShowAllFiles;

            _loading = true;
            _entries = new List<FileBrowserEntry>();

            _list.BeginUpdate();

            try
            {
                _list.Items.Clear();
            }
            finally
            {
                _list.EndUpdate();
            }

            UpdateAddressText();
            UpdateNavigationButtons();
            SetStatus("正在读取…", false);

            Task.Run(() => _service.ReadDirectory(target, showAll, cts.Token), cts.Token)
                .ContinueWith(
                    task => CompleteLoad(version, target, task, cts),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private void CompleteLoad(
            int version, string? target, Task<FileBrowserReadResult> task, CancellationTokenSource cts)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;

                try
                {
                    BeginInvoke(new Action(() => ApplyLoad(version, target, task)));
                }
                catch (InvalidOperationException)
                {
                    // 窗口正在销毁（ObjectDisposedException 也在这条线上）：结果丢掉。
                }
            }
            finally
            {
                // 任务已经结束，token 不会再被读；这里才释放 CTS，
                // 避免在 CancelLoad 里提前 Dispose 掉别的线程还在用的 WaitHandle。
                if (ReferenceEquals(_loadCts, cts)) _loadCts = null;

                cts.Dispose();
            }
        }

        private void ApplyLoad(int version, string? target, Task<FileBrowserReadResult> task)
        {
            if (version != _loadVersion || IsDisposed) return;

            _loading = false;

            if (task.IsCanceled) return;

            if (!task.IsCompletedSuccessfully)
            {
                var message = task.Exception?.GetBaseException().Message ?? "未知错误";
                AppLog.Swallowed("文件浏览器读取任务失败：" + (target ?? FileBrowserPaths.ComputerLabel), task.Exception!);
                FinishWithError("读取失败：" + message);
                return;
            }

            var result = task.Result;

            if (result.Cancelled) return;

            if (!string.IsNullOrEmpty(result.Error))
            {
                if (_initialLoadPending && target != null)
                {
                    // 上次的目录不在了（盘拔了 / 共享离线）：回此电脑，不在这里反复重试。
                    _initialLoadPending = false;
                    _state.Reset(null);
                    UpdateAddressText();
                    StartLoad();
                    SetStatus("上次的文件夹打不开，已回到此电脑", true);
                    return;
                }

                FinishWithError(result.Error);
                return;
            }

            _initialLoadPending = false;
            _isRoots = result.IsRoots;
            _entries = result.Entries;
            _truncated = result.Truncated;

            FileBrowserService.Sort(_entries, _state.SortField, _state.SortDescending);

            FillList();
            UpdateStatusCount();
            ApplyPendingSelection();

            _state.NoteSuccessfulLoad(target);
            UpdateAddressText();
            UpdateNavigationButtons();

            // 1.28.0：读成功之后盯住这个目录（网络位置 / 此电脑不盯）。
            RestartWatch();

            // 树跟着列表走（树只有在显示出来时才会真的去展开）。
            SelectTreePath(target);

            RaiseStateChanged();
        }

        private void FinishWithError(string message)
        {
            _entries = new List<FileBrowserEntry>();
            _truncated = false;
            _pendingSelectPath = null;

            StopWatch();

            FillList();
            UpdateNavigationButtons();
            ReportStatus(message, true);
        }

        private void CancelLoad()
        {
            // 只取消，不 Dispose：读取线程可能还在用这个 token（WaitHandle / Register）。
            // 真正的释放放在任务结束后的 CompleteLoad 里；那边可能已经把 CTS Dispose 了，
            // 所以这里对"已经释放"要宽容（否则关窗 / 下一次导航会抛 ObjectDisposedException）。
            var cts = _loadCts;
            _loadCts = null;

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 任务刚好结束并释放了 CTS：没有可取消的了。
                }
            }

            _loadVersion++;
        }

        // ---- 导航 -------------------------------------------------------------

        internal void NavigateTo(string? raw)
        {
            if (!FileBrowserPaths.TryNormalize(raw, out var path, out var error))
            {
                ReportStatus(error, true);
                return;
            }

            NavigateToPath(path);
        }

        internal void NavigateForTest(string? raw) => NavigateTo(raw);

        private void NavigateToPath(string? path)
        {
            ResetFilter();
            _pendingSelectPath = null;
            _state.Navigate(path);
            UpdateAddressText();
            UpdateNavigationButtons();
            StartLoad();
        }

        internal void GoBack()
        {
            if (!_state.TryGoBack(out _)) return;

            ResetFilter();
            _pendingSelectPath = null;
            UpdateAddressText();
            UpdateNavigationButtons();
            StartLoad();
        }

        internal void GoForward()
        {
            if (!_state.TryGoForward(out _)) return;

            ResetFilter();
            _pendingSelectPath = null;
            UpdateAddressText();
            UpdateNavigationButtons();
            StartLoad();
        }

        internal void GoUp()
        {
            if (_state.CurrentPath == null) return;

            NavigateToPath(FileBrowserPaths.ParentOf(_state.CurrentPath));
        }

        internal void Reload()
        {
            if (!_loadedOnce)
            {
                EnsureLoaded();
                return;
            }

            if (_searchActive || _searching)
            {
                if (!string.IsNullOrWhiteSpace(_searchQuery))
                {
                    StartRecursiveSearch();
                    return;
                }

                CancelSearch();
            }

            StartLoad();
        }

        // ---- 列表 -------------------------------------------------------------

        private void FillList()
        {
            var icons = _icons;
            var imageIndex = icons?.IndexOf(FileBrowserKind.Other) ?? 0;
            var source = _searchActive ? _searchResults : _entries;

            _visibleEntries = source
                .Where(entry =>
                {
                    // 递归结果已经由搜索按查询筛过；这里只再应用「只看类型」。
                    if (_searchActive) return FileBrowserFilter.MatchesType(entry, _typeFilter);

                    // 勾了「含子文件夹」但还没按 Enter：列表显示当前层全部条目，
                    // 查询词先不生效；否则会看起来像“搜索没反应”。
                    if (_recursiveSearch.Checked) return FileBrowserFilter.MatchesType(entry, _typeFilter);

                    return FileBrowserFilter.Matches(entry, _query)
                           && FileBrowserFilter.MatchesType(entry, _typeFilter);
                })
                .ToList();

            ApplyThumbnailMode();

            _itemsByPath.Clear();

            var thumbnails = _thumbnailIndices;
            var rows = new List<ListViewItem>(_visibleEntries.Count);

            _list.BeginUpdate();

            try
            {
                _list.Items.Clear();

                foreach (var entry in _visibleEntries)
                {
                    var item = new ListViewItem(entry.Name)
                    {
                        Tag = entry,
                        ImageIndex = icons?.IndexOf(entry.Kind) ?? imageIndex,
                        ToolTipText = entry.ToolTip
                    };

                    // 已缓存的缩略图直接贴上：别等到滚动轮询再来一次。
                    if (thumbnails.Count > 0 && !entry.IsDirectory
                        && thumbnails.TryGetValue(ThumbnailKey(entry), out var thumbIndex))
                    {
                        item.ImageIndex = thumbIndex;
                    }

                    item.SubItems.Add(entry.SizeText);
                    item.SubItems.Add(FileBrowserPaths.RelativeFolder(_state.CurrentPath, entry.FullPath));

                    _itemsByPath[entry.FullPath] = item;
                    rows.Add(item);
                }

                _list.Items.AddRange(rows.ToArray());
            }
            finally
            {
                _list.EndUpdate();
            }

            AdjustColumns();
            UpdateSortHeaders();
            RefreshThumbnails();
        }

        private void UpdateStatusCount()
        {
            SetStatus(DescribeCount(), false);
        }

        private string DescribeCount()
        {
            if (_isRoots)
                return _entries.Count == 0 ? "没有找到可用的磁盘" : $"{_entries.Count} 个驱动器";

            if (_searchActive) return DescribeSearchResult();

            if (_recursiveSearch.Checked && !_searching)
            {
                return string.IsNullOrWhiteSpace(_query)
                    ? "含子文件夹：输入关键字后按 Enter 搜索"
                    : $"含子文件夹：按 Enter 搜索「{_query.Trim()}」";
            }

            if (_entries.Count == 0) return "这个文件夹里没有可显示的项目";

            var filtering = IsFiltering;
            var visibleCount = _visibleEntries.Count;
            var visibleFolders = _visibleEntries.Count(entry => entry.IsDirectory);

            var text = filtering
                ? (_typeFilter == FileBrowserTypeFilter.All ? string.Empty : DescribeTypeFilter() + " · ")
                  + $"筛选 {visibleCount:N0}/{_entries.Count:N0} 项（{visibleFolders:N0} 个文件夹）"
                : $"{_entries.Count:N0} 项（{_entries.Count(entry => entry.IsDirectory):N0} 个文件夹）";

            if (_truncated) text += " · 已达显示上限，只列出了一部分";

            // 缩略图是按每目录预算做的；少做了要在状态行说，不能看起来像"全都做了"。
            if (_thumbnailSkipped > 0)
                text += $" · 缩略图只做了前 {MaxThumbnails} 个文件（看得到的才做）";

            if (_thumbnailFramesSkipped > 0)
                text += $" · 视频画面只做了前 {FrameBudget} 个";

            return text;
        }

        private string DescribeTypeFilter() => _typeFilter switch
        {
            FileBrowserTypeFilter.Video => "只看视频",
            FileBrowserTypeFilter.Audio => "只看音频",
            FileBrowserTypeFilter.Subtitle => "只看字幕",
            FileBrowserTypeFilter.Playlist => "只看播放列表",
            _ => string.Empty
        };

        private bool IsFiltering =>
            !string.IsNullOrWhiteSpace(_query) || _typeFilter != FileBrowserTypeFilter.All;

        /// <summary>
        /// 搜索 / 类型筛选变化：只重排现有条目，不读磁盘。
        /// 递归结果是一个快照：查询一改就丢掉旧结果并回到当前层，按 Enter 再搜。
        /// </summary>
        private void ApplyFilter()
        {
            // 正在读取时不动列表；读完会按当前的查询 / 类型再 FillList。
            if (_loading) return;

            if (_searching || _searchActive)
            {
                var sameQuery = string.Equals(_query.Trim(), _searchQuery, StringComparison.Ordinal);

                if (!sameQuery || string.IsNullOrWhiteSpace(_query))
                {
                    CancelSearch();
                }
                else if (_searching)
                {
                    // 正在搜同一个查询：改「只看类型」只记下来，等结果回来再应用，
                    // 不能让 FillList 把“正在搜索”的状态行盖掉。
                    return;
                }
            }

            FillList();
            UpdateStatusCount();
        }

        /// <summary>路径变化时清空过滤与搜索范围；F5 刷新保留。</summary>
        private void ResetFilter()
        {
            CancelSearch();

            if (_search.Text.Length > 0)
            {
                _suppressFilterEvents = true;

                try
                {
                    _search.Text = string.Empty;
                }
                finally
                {
                    _suppressFilterEvents = false;
                }
            }

            _query = string.Empty;
            _typeFilter = FileBrowserTypeFilter.All;
            SetRecursiveChecked(false);
            UpdateSearchPlaceholder();
        }

        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            if (!HandleSearchKey(e.KeyCode)) return;

            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private bool HandleSearchKey(Keys key)
        {
            if (key == Keys.Escape)
            {
                ClearSearch();
                _list.Focus();
                return true;
            }

            if (key != Keys.Enter) return false;

            if (_recursiveSearch.Checked) StartRecursiveSearch();

            _list.Focus();
            return true;
        }

        internal void ClearSearch()
        {
            CancelSearch();

            _suppressFilterEvents = true;

            try
            {
                _search.Text = string.Empty;
            }
            finally
            {
                _suppressFilterEvents = false;
            }

            _query = string.Empty;
            ApplyFilter();
        }

        internal void FocusSearch()
        {
            _search.Focus();
            _search.SelectAll();
        }

        internal void ClearSearchForTest() => ClearSearch();

        internal void SetTypeFilterForTest(FileBrowserTypeFilter filter) => SetTypeFilter(filter);

        internal void SetRecursiveSearchForTest(bool value) => _recursiveSearch.Checked = value;

        /// <summary>测试钩子：按当前查询与「含子文件夹」直接开始一次递归搜索（等价于按 Enter）。</summary>
        internal void StartRecursiveSearchForTest()
        {
            if (!_recursiveSearch.Checked) SetRecursiveChecked(true);

            StartRecursiveSearch();
        }

        internal bool IsSearchingForTest() => _searching;

        internal bool IsSearchActiveForTest() => _searchActive;

        internal bool IsRecursiveSearchForTest() => _recursiveSearch.Checked;

        internal bool ProcessSearchKeyForTest(Keys key) => HandleSearchKey(key);

        internal void SetSearchServiceForTest(FileBrowserSearch service) =>
            _searchService = service ?? new FileBrowserSearch();

        private void OnRecursiveSearchChanged()
        {
            UpdateSearchPlaceholder();

            if (_suppressScopeEvents) return;

            if (!_recursiveSearch.Checked) CancelSearch();

            ApplyFilter();
        }

        private void SetRecursiveChecked(bool value)
        {
            if (_recursiveSearch.Checked == value) return;

            _suppressScopeEvents = true;

            try
            {
                _recursiveSearch.Checked = value;
            }
            finally
            {
                _suppressScopeEvents = false;
            }

            UpdateSearchPlaceholder();
        }

        private void UpdateSearchScopeEnabled()
        {
            var enabled = _state.CurrentPath != null;
            _recursiveSearch.Enabled = enabled;

            if (!enabled) SetRecursiveChecked(false);
        }

        private void SetTypeFilter(FileBrowserTypeFilter filter)
        {
            if (_typeFilter == filter) return;

            _typeFilter = filter;
            ApplyFilter();
        }

        /// <summary>定位：导航到文件所在目录，并在读完目录后选中它。</summary>
        internal void NavigateToFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ReportStatus("文件已经不在了：" + path, true);
                return;
            }

            var directory = Path.GetDirectoryName(path);

            if (string.IsNullOrWhiteSpace(directory))
            {
                ReportStatus("这个文件没有所在文件夹", true);
                return;
            }

            _pendingSelectPath = path;

            // 定位要看得见目标：先把"只看视频 / 搜索"这类临时视图清掉。
            ResetFilter();

            if (!_loading
                && _loadedOnce
                && string.Equals(_state.CurrentPath, directory, StringComparison.OrdinalIgnoreCase))
            {
                FillList();
                UpdateStatusCount();
                ApplyPendingSelection();
                return;
            }

            _state.Navigate(directory);
            UpdateAddressText();
            UpdateNavigationButtons();
            StartLoad();
        }

        internal void NavigateToFileForTest(string path) => NavigateToFile(path);

        // ---- 递归搜索 ---------------------------------------------------------

        /// <summary>按当前查询搜索当前文件夹及子文件夹；只由 Enter / 测试触发，打字不扫盘。</summary>
        private void StartRecursiveSearch()
        {
            var root = _state.CurrentPath;
            var query = _query.Trim();

            if (root == null)
            {
                ReportStatus("先进入一个文件夹，再搜索子文件夹", true);
                return;
            }

            if (query.Length == 0)
            {
                SetStatus("输入关键字后按 Enter 搜索子文件夹", false);
                return;
            }

            if (_loading)
            {
                SetStatus("目录还在读取，读完再按 Enter 搜索", true);
                return;
            }

            CancelSearch();

            var cts = new CancellationTokenSource();
            _searchCts = cts;

            var version = ++_searchVersion;
            var showAll = _state.ShowAllFiles;

            _searchQuery = query;
            _searching = true;
            _searchActive = false;
            _searchResults.Clear();
            _searchFolders = 0;
            _searchEntries = 0;
            _searchFound = 0;
            _searchUnreadable = 0;
            _searchTruncated = false;

            _list.BeginUpdate();

            try
            {
                _list.Items.Clear();
            }
            finally
            {
                _list.EndUpdate();
            }

            _visibleEntries = new List<FileBrowserEntry>();
            AdjustColumns();
            SetStatus(DescribeSearchProgress(), false);

            var progress = new SearchProgressRelay(this, version);
            var token = cts.Token;

            Task.Run(
                    () => _searchService.Search(root, query, showAll, token, progress),
                    token)
                .ContinueWith(
                    task => CompleteSearch(version, task, cts),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }

        private void CompleteSearch(
            int version, Task<FileBrowserSearchResult> task, CancellationTokenSource cts)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;

                try
                {
                    BeginInvoke(new Action(() => ApplySearch(version, task)));
                }
                catch (InvalidOperationException)
                {
                    // 窗口正在销毁：结果丢掉。
                }
            }
            finally
            {
                if (ReferenceEquals(_searchCts, cts)) _searchCts = null;

                cts.Dispose();
            }
        }

        private void ApplySearch(int version, Task<FileBrowserSearchResult> task)
        {
            if (version != _searchVersion || IsDisposed) return;

            _searching = false;

            if (task.IsCanceled) return;

            if (!task.IsCompletedSuccessfully)
            {
                AppLog.Swallowed("文件页递归搜索任务失败", task.Exception!);
                _searchActive = false;
                _searchResults.Clear();
                FillList();
                ReportStatus("搜索失败，详情见日志", true);
                return;
            }

            var result = task.Result;

            _searchFolders = result.FoldersVisited;
            _searchEntries = result.EntriesVisited;
            _searchFound = result.Entries.Count;
            _searchUnreadable = result.UnreadableFolders;
            _searchTruncated = result.Truncated;

            if (result.Cancelled) return;

            if (!string.IsNullOrEmpty(result.Error))
            {
                _searchActive = false;
                _searchResults.Clear();
                FillList();
                ReportStatus(result.Error, true);
                return;
            }

            _searchResults = result.Entries;
            _searchActive = true;

            FileBrowserService.Sort(_searchResults, _state.SortField, _state.SortDescending);

            FillList();
            UpdateStatusCount();
        }

        private void CancelSearch()
        {
            var cts = _searchCts;
            _searchCts = null;

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 任务刚好结束并释放了 CTS。
                }
            }

            _searchVersion++;
            _searching = false;
            _searchActive = false;
            _searchResults.Clear();
            _searchFound = 0;
        }

        private void ApplySearchProgress(int version, FileBrowserSearchProgress progress)
        {
            if (IsDisposed || version != _searchVersion || !_searching) return;

            _searchFolders = progress.FoldersVisited;
            _searchEntries = progress.EntriesVisited;
            _searchFound = progress.Results;
            SetStatus(DescribeSearchProgress(), false);
        }

        private string DescribeSearchProgress() =>
            $"正在搜索子文件夹…（看了 {_searchFolders:N0} 个文件夹 / {_searchEntries:N0} 项 / "
            + $"找到 {_searchFound:N0} 个） · 按 Esc 取消";

        /// <summary>
        /// 把后台的进度搬回 UI 线程。<b>不要用 <see cref="Progress{T}"/> 依赖 <c>SynchronizationContext</c></b>：
        /// 测试宿主里它可能是空的，回调会跑到线程池上，晚到的进度就能把完成后的状态行盖掉。
        /// </summary>
        private sealed class SearchProgressRelay : IProgress<FileBrowserSearchProgress>
        {
            private readonly FileBrowserView _owner;
            private readonly int _version;

            internal SearchProgressRelay(FileBrowserView owner, int version)
            {
                _owner = owner;
                _version = version;
            }

            public void Report(FileBrowserSearchProgress value)
            {
                if (_owner.IsDisposed || !_owner.IsHandleCreated) return;

                try
                {
                    _owner.BeginInvoke(new Action(() => _owner.ApplySearchProgress(_version, value)));
                }
                catch (InvalidOperationException)
                {
                    // 窗口正在销毁。
                }
            }
        }

        private string DescribeSearchResult()
        {
            var type = _typeFilter == FileBrowserTypeFilter.All
                ? string.Empty
                : DescribeTypeFilter() + " · ";

            var text = $"含子文件夹 · {type}搜索「{_searchQuery}」：{_visibleEntries.Count:N0} 项";

            if (_visibleEntries.Count != _searchResults.Count)
                text += $" / {_searchResults.Count:N0}";

            text += $" · 看了 {_searchFolders:N0} 个文件夹 / {_searchEntries:N0} 项";

            if (_searchUnreadable > 0)
                text += $" · {_searchUnreadable:N0} 个文件夹读不了";

            if (_searchTruncated)
                text += " · 已达搜索上限，只找到一部分";

            return text;
        }

        private void ApplyPendingSelection()
        {
            var pending = _pendingSelectPath;

            if (pending == null) return;

            _pendingSelectPath = null;

            var item = _list.Items
                .OfType<ListViewItem>()
                .FirstOrDefault(row => row.Tag is FileBrowserEntry entry
                                       && string.Equals(entry.FullPath, pending, StringComparison.OrdinalIgnoreCase));

            if (item == null)
            {
                ReportStatus("当前文件不在这个文件夹里：" + pending, true);
                return;
            }

            _list.SelectedItems.Clear();
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
            _list.Focus();
        }

        private void UpdateSortHeaders()
        {
            var nameArrow = _state.SortField == FileSortField.Name
                ? (_state.SortDescending ? " ▼" : " ▲")
                : string.Empty;

            var sizeArrow = _state.SortField == FileSortField.Size && !_isRoots
                ? (_state.SortDescending ? " ▼" : " ▲")
                : string.Empty;

            _nameColumn.Text = "名称" + nameArrow;
            _sizeColumn.Text = (_isRoots ? "类型" : "大小") + sizeArrow;
        }

        private void UpdateAddressText()
        {
            _address.Text = _state.CurrentPath == null
                ? FileBrowserPaths.ComputerLabel
                : _state.CurrentPath;
        }

        private void UpdateNavigationButtons()
        {
            _backButton.Enabled = _state.CanGoBack;
            _forwardButton.Enabled = _state.CanGoForward;
            _upButton.Enabled = _state.CanGoUp;
            UpdateSearchScopeEnabled();
        }

        // ---- 交互 -------------------------------------------------------------

        private void OnAddressKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;

                NavigateTo(_address.Text);
                return;
            }

            if (e.KeyCode != Keys.Escape) return;

            e.Handled = true;
            e.SuppressKeyPress = true;
            UpdateAddressText();
        }

        private void OnListKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedPaths();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            switch (e.KeyCode)
            {
                case Keys.Enter:
                    ActivateSelection();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;

                case Keys.Back:
                    GoUp();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;

                case Keys.F5:
                    Reload();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;

                case Keys.Delete:
                    // 文件浏览器不删文件，也不能让这个键漏到"从播放列表移除"。
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;

                // Alt+← / Alt+→：后退 / 前进。窗体那边也会接（焦点不一定在列表上）。
                case Keys.Left when e.Alt:
                    GoBack();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;

                case Keys.Right when e.Alt:
                    GoForward();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    break;
            }
        }

        private void OnColumnClick(object? sender, ColumnClickEventArgs e)
        {
            if (e.Column == 0)
                ToggleSort(FileSortField.Name);
            else if (e.Column == 1 && !_isRoots)
                ToggleSort(FileSortField.Size);
        }

        private void OnListMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            // 右键点在空白处：先清掉选中，菜单里就只剩"刷新 / 排序 / 显示所有文件"。
            if (_list.GetItemAt(e.X, e.Y) == null) _list.SelectedItems.Clear();
        }

        internal void ActivateSelection()
        {
            var entry = _list.FocusedItem?.Tag as FileBrowserEntry
                        ?? SelectedEntries().FirstOrDefault();

            if (entry == null) return;

            if (entry.IsDirectory)
            {
                NavigateTo(entry.FullPath);
                return;
            }

            var paths = SelectedEntries()
                .Where(item => !item.IsDirectory)
                .Select(item => item.FullPath)
                .ToList();

            if (paths.Count == 0) paths.Add(entry.FullPath);

            PlayRequested?.Invoke(this, paths);
        }

        internal void ActivateForTest() => ActivateSelection();

        internal void InvokeActionForTest(FileBrowserAction action)
        {
            switch (action)
            {
                case FileBrowserAction.Play:
                    RaisePlay();
                    break;
                case FileBrowserAction.Add:
                    RaiseAdd();
                    break;
                case FileBrowserAction.Queue:
                    RaiseQueue();
                    break;
                case FileBrowserAction.PlayFolder:
                    RaisePlayFolder();
                    break;
            }
        }

        internal void SelectByNameForTest(string name)
        {
            var item = _list.Items
                .OfType<ListViewItem>()
                .FirstOrDefault(row => string.Equals(row.Text, name, StringComparison.OrdinalIgnoreCase));

            if (item == null) return;

            _list.SelectedItems.Clear();
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
        }

        private List<FileBrowserEntry> SelectedEntries()
        {
            var entries = new List<FileBrowserEntry>();

            foreach (ListViewItem item in _list.SelectedItems)
            {
                if (item.Tag is FileBrowserEntry entry) entries.Add(entry);
            }

            return entries;
        }

        // ---- 拖动到播放列表 ---------------------------------------------------

        private void OnFileItemDrag(object? sender, ItemDragEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _loading) return;

            var data = BuildFileDropData();
            if (data == null) return;

            _list.DoDragDrop(data, DragDropEffects.Copy);
        }

        /// <summary>
        /// 拖放数据：同时带 <see cref="DataFormats.FileDrop"/>（外部程序可用）与
        /// <see cref="FileBrowserDrag.Format"/>（本程序据此把文件夹递归走有界扫描）。
        /// </summary>
        private DataObject? BuildFileDropData()
        {
            var paths = _list.SelectedItems
                .Cast<ListViewItem>()
                .OrderBy(item => item.Index)
                .Select(item => item.Tag)
                .OfType<FileBrowserEntry>()
                .Select(entry => entry.FullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0) return null;

            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            data.SetData(FileBrowserDrag.Format, paths);
            return data;
        }

        internal DataObject? BuildFileDropDataForTest() => BuildFileDropData();

        // ---- 排序 / 过滤 -------------------------------------------------------

        /// <summary>
        /// 打开 / 关闭平铺缩略图：只换列表形态，不重读目录（缓存里有的封面直接贴回去）。
        /// 这个选择进 <see cref="FileBrowserState.ShowThumbnails"/>，退出 / 存方案时统一落盘。
        /// </summary>
        internal void SetThumbnails(bool value)
        {
            if (_state.ShowThumbnails == value) return;

            _state.ShowThumbnails = value;
            RaiseStateChanged();

            FillList();
            UpdateStatusCount();
        }

        internal void SetShowAllFiles(bool value)
        {
            if (_state.ShowAllFiles == value) return;

            _state.ShowAllFiles = value;
            RaiseStateChanged();

            if (_searchActive || _searching)
            {
                if (_recursiveSearch.Checked && !string.IsNullOrWhiteSpace(_query))
                {
                    StartRecursiveSearch();
                    return;
                }

                CancelSearch();
            }

            if (_loadedOnce) StartLoad();
        }

        internal void SetSort(FileSortField field, bool descending)
        {
            _state.SortField = field;
            _state.SortDescending = descending;

            if (_searching)
            {
                UpdateSortHeaders();
                RaiseStateChanged();
                return;
            }

            var source = _searchActive ? _searchResults : _entries;

            if (source.Count > 0)
            {
                FileBrowserService.Sort(source, _state.SortField, _state.SortDescending);
                FillList();
            }
            else
            {
                UpdateSortHeaders();
            }

            RaiseStateChanged();
        }

        private void ToggleSort(FileSortField field)
        {
            var descending = _state.SortField == field && !_state.SortDescending;
            SetSort(field, descending);
        }

        // ---- 右键菜单 ---------------------------------------------------------

        private void BuildContextMenu()
        {
            _playItem = new ToolStripMenuItem("播放(&P)");
            _playItem.Click += (s, e) => RaisePlay();

            _addItem = new ToolStripMenuItem("添加到播放列表(&A)");
            _addItem.Click += (s, e) => RaiseAdd();

            _queueItem = new ToolStripMenuItem("下一首播放(&N)");
            _queueItem.Click += (s, e) => RaiseQueue();

            _revealItem = new ToolStripMenuItem("在资源管理器中显示(&E)");
            _revealItem.Click += (s, e) => RevealSelected();

            _copyItem = new ToolStripMenuItem("复制路径(&C)");
            _copyItem.Click += (s, e) => CopySelectedPaths();

            _refreshItem = new ToolStripMenuItem("刷新(&R)");
            _refreshItem.Click += (s, e) => Reload();

            _contextMenu.Items.AddRange(new ToolStripItem[]
            {
                _playItem, _addItem, _queueItem,
                new ToolStripSeparator(),
                _revealItem, _copyItem,
                new ToolStripSeparator(),
                _refreshItem
            });

            _contextMenu.ShowImageMargin = false;
            _contextMenu.Opening += (s, e) => UpdateContextMenu();

            _list.ContextMenuStrip = _contextMenu;
        }

        private void UpdateContextMenu()
        {
            var entries = SelectedEntries();
            var files = entries.Where(entry => !entry.IsDirectory).ToList();
            var folders = entries.Where(entry => entry.IsDirectory).ToList();

            _playItem.Enabled = entries.Count > 0;
            _playItem.Text = entries.Count == 1 && folders.Count == 1
                ? "播放这个文件夹（递归）(&P)"
                : "播放(&P)";

            _addItem.Enabled = entries.Count > 0;
            _addItem.Text = folders.Count > 0
                ? "添加整个文件夹到播放列表(&A)"
                : "添加到播放列表(&A)";

            _queueItem.Enabled = files.Any(entry => MediaFormats.IsMediaFile(entry.FullPath));
            _revealItem.Enabled = entries.Count == 1;
            _copyItem.Enabled = entries.Count > 0;
        }

        private void RaisePlay()
        {
            var entries = SelectedEntries();

            if (entries.Count == 1 && entries[0].IsDirectory)
            {
                PlayFolderRequested?.Invoke(this, entries[0].FullPath);
                return;
            }

            var paths = entries
                .Where(entry => !entry.IsDirectory)
                .Select(entry => entry.FullPath)
                .ToList();

            if (paths.Count == 0) return;

            PlayRequested?.Invoke(this, paths);
        }

        private void RaiseAdd()
        {
            var paths = SelectedEntries().Select(entry => entry.FullPath).ToList();

            if (paths.Count == 0) return;

            AddRequested?.Invoke(this, paths);
        }

        private void RaiseQueue()
        {
            var paths = SelectedEntries()
                .Where(entry => !entry.IsDirectory && MediaFormats.IsMediaFile(entry.FullPath))
                .Select(entry => entry.FullPath)
                .ToList();

            if (paths.Count == 0)
            {
                ReportStatus("选中的项里没有可播放的媒体文件", true);
                return;
            }

            QueueRequested?.Invoke(this, paths);
        }

        private void RaisePlayFolder()
        {
            var entries = SelectedEntries();

            if (entries.Count == 1 && entries[0].IsDirectory)
                PlayFolderRequested?.Invoke(this, entries[0].FullPath);
        }

        private void RevealSelected()
        {
            var entries = SelectedEntries();
            if (entries.Count != 1) return;

            var entry = entries[0];

            try
            {
                var arguments = entry.IsDirectory
                    ? "\"" + entry.FullPath + "\""
                    : "/select,\"" + entry.FullPath + "\"";

                Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件浏览器打开资源管理器失败", ex);
                ReportStatus("无法打开资源管理器", true);
            }
        }

        private void CopySelectedPaths()
        {
            var entries = SelectedEntries();
            if (entries.Count == 0) return;

            try
            {
                Clipboard.SetText(string.Join(Environment.NewLine, entries.Select(entry => entry.FullPath)));
                SetStatus($"已复制 {entries.Count} 条路径到剪贴板", false);
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件浏览器复制路径失败", ex);
                ReportStatus("复制失败", true);
            }
        }

        // ---- 位置菜单（收藏 / 最近 / 排序 / 显示所有）-------------------------

        private void BuildPositionMenu()
        {
            _positionMenu.ShowImageMargin = false;
            _positionMenu.ShowCheckMargin = true;
            _positionMenu.Opening += (s, e) => RebuildPositionMenu();
        }

        private void RebuildPositionMenu()
        {
            ClearMenuItems(_positionMenu.Items);

            var locate = new ToolStripMenuItem("定位当前播放文件(&L)");
            locate.Click += (s, e) => LocateRequested?.Invoke(this, EventArgs.Empty);
            _positionMenu.Items.Add(locate);
            _positionMenu.Items.Add(new ToolStripSeparator());

            var computer = new ToolStripMenuItem("此电脑(&T)");
            computer.Click += (s, e) => NavigateTo(null);
            _positionMenu.Items.Add(computer);
            _positionMenu.Items.Add(new ToolStripSeparator());

            AddMenuHeader("收藏");
            AddPathItems(_state.Favorites, favorite => NavigateTo(favorite));

            var favoriteItem = new ToolStripMenuItem(
                _state.IsFavorite(_state.CurrentPath) ? "取消收藏当前文件夹(&F)" : "收藏当前文件夹(&F)")
            {
                Enabled = _state.CurrentPath != null
            };

            favoriteItem.Click += (s, e) => ToggleCurrentFavorite();
            _positionMenu.Items.Add(favoriteItem);

            _positionMenu.Items.Add(new ToolStripSeparator());

            AddMenuHeader("最近浏览");
            AddPathItems(_state.Recent, recent => NavigateTo(recent));

            var clearRecent = new ToolStripMenuItem("清除最近浏览(&C)") { Enabled = _state.Recent.Count > 0 };
            clearRecent.Click += (s, e) =>
            {
                _state.ClearRecent();
                RaiseStateChanged();
                RecentCleared?.Invoke(this, EventArgs.Empty);
                SetStatus("已清除最近浏览的文件夹", false);
            };
            _positionMenu.Items.Add(clearRecent);

            _positionMenu.Items.Add(new ToolStripSeparator());

            var showAll = new ToolStripMenuItem("显示所有文件（含隐藏 / 不支持的类型）(&A)")
            {
                Checked = _state.ShowAllFiles,
                CheckOnClick = false
            };
            showAll.Click += (s, e) => SetShowAllFiles(!_state.ShowAllFiles);
            _positionMenu.Items.Add(showAll);

            var thumbnails = new ToolStripMenuItem("缩略图（平铺）(&K)")
            {
                Checked = _state.ShowThumbnails,
                CheckOnClick = false,
                ToolTipText = "用封面缩略图平铺显示音视频文件（网络位置只显示图标）"
            };
            thumbnails.Click += (s, e) => SetThumbnails(!_state.ShowThumbnails);
            _positionMenu.Items.Add(thumbnails);

            var budget = new ToolStripMenuItem("缩略图数量(&N)");

            var budgetKeys = new[] { "&A", "&B", "&C" };
            var budgetIndex = 0;

            foreach (var option in FileBrowserThumbnailBudgets.Options)
            {
                var key = budgetIndex < budgetKeys.Length ? budgetKeys[budgetIndex] : string.Empty;
                budgetIndex++;

                var item = new ToolStripMenuItem($"{option:N0} 个({key})") { Checked = MaxThumbnails == option };
                var value = option;

                item.Click += (s, e) => SetThumbnailBudget(value);
                budget.DropDownItems.Add(item);
            }

            _positionMenu.Items.Add(budget);

            var clearCache = new ToolStripMenuItem("清空缩略图缓存(&H)")
            {
                ToolTipText = "删掉磁盘上缓存下来的缩略图（会重新读封面 / 重新截帧）"
            };
            clearCache.Click += (s, e) => ClearThumbnailCache();
            _positionMenu.Items.Add(clearCache);

            var autoRefresh = new ToolStripMenuItem("自动刷新(&R)")
            {
                Checked = _state.AutoRefresh,
                CheckOnClick = false,
                ToolTipText = "盯着当前文件夹：本地文件有增删改就静默刷新（网络位置不盯）"
            };
            autoRefresh.Click += (s, e) => SetAutoRefresh(!_state.AutoRefresh);
            _positionMenu.Items.Add(autoRefresh);

            var folderTree = new ToolStripMenuItem("文件夹树(&T)")
            {
                Checked = _state.ShowFolderTree,
                CheckOnClick = false,
                Enabled = CanShowFolderTree || _state.ShowFolderTree,
                ToolTipText = "左边显示文件夹树（窗口太窄时置灰，拖宽或分离成独立窗口再用）"
            };
            folderTree.Click += (s, e) => SetFolderTree(!_state.ShowFolderTree);
            _positionMenu.Items.Add(folderTree);

            var type = new ToolStripMenuItem("只看类型(&T)");
            type.DropDownItems.Add(CreateTypeItem("全部(&A)", FileBrowserTypeFilter.All));
            type.DropDownItems.Add(CreateTypeItem("视频(&V)", FileBrowserTypeFilter.Video));
            type.DropDownItems.Add(CreateTypeItem("音频(&M)", FileBrowserTypeFilter.Audio));
            type.DropDownItems.Add(CreateTypeItem("字幕(&S)", FileBrowserTypeFilter.Subtitle));
            type.DropDownItems.Add(CreateTypeItem("播放列表(&P)", FileBrowserTypeFilter.Playlist));
            _positionMenu.Items.Add(type);

            var sort = new ToolStripMenuItem("排序(&S)");

            sort.DropDownItems.Add(CreateSortItem("名称(&N)", FileSortField.Name));
            sort.DropDownItems.Add(CreateSortItem("大小(&S)", FileSortField.Size));
            sort.DropDownItems.Add(CreateSortItem("修改时间(&T)", FileSortField.Modified));
            sort.DropDownItems.Add(CreateSortItem("类型(&Y)", FileSortField.Type));
            sort.DropDownItems.Add(new ToolStripSeparator());

            var descending = new ToolStripMenuItem("降序(&D)") { Checked = _state.SortDescending };
            descending.Click += (s, e) => SetSort(_state.SortField, !_state.SortDescending);
            sort.DropDownItems.Add(descending);

            _positionMenu.Items.Add(sort);
        }

        private ToolStripMenuItem CreateSortItem(string text, FileSortField field)
        {
            var item = new ToolStripMenuItem(text) { Checked = _state.SortField == field };
            item.Click += (s, e) => SetSort(field, _state.SortDescending);
            return item;
        }

        private ToolStripMenuItem CreateTypeItem(string text, FileBrowserTypeFilter filter)
        {
            var item = new ToolStripMenuItem(text) { Checked = _typeFilter == filter };
            item.Click += (s, e) => SetTypeFilter(filter);
            return item;
        }

        private void AddMenuHeader(string text)
        {
            _positionMenu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
        }

        private void AddPathItems(IReadOnlyList<string> paths, Action<string> open)
        {
            if (paths.Count == 0)
            {
                _positionMenu.Items.Add(new ToolStripMenuItem("（暂无）") { Enabled = false });
                return;
            }

            foreach (var path in paths)
            {
                var item = new ToolStripMenuItem(FileBrowserPaths.Describe(path).Replace("&", "&&"))
                {
                    Tag = path,
                    ToolTipText = path
                };

                item.Click += (s, e) => open(path);
                _positionMenu.Items.Add(item);
            }
        }

        private void ToggleCurrentFavorite()
        {
            var path = _state.CurrentPath;
            if (path == null) return;

            var wasFavorite = _state.IsFavorite(path);
            var changed = wasFavorite ? _state.RemoveFavorite(path) : _state.AddFavorite(path);

            if (!changed) return;

            SetStatus(wasFavorite ? "已取消收藏：" + path : "已收藏：" + path, false);

            RaiseStateChanged();
        }

        private static void ClearMenuItems(ToolStripItemCollection items)
        {
            var snapshot = new ToolStripItem[items.Count];
            items.CopyTo(snapshot, 0);

            foreach (var item in snapshot)
                item.Dispose();

            items.Clear();
        }

        // ---- 状态 -------------------------------------------------------------

        private void SetStatus(string text, bool error)
        {
            _statusIsError = error;
            _status.Text = text;
            _status.ForeColor = error ? _palette.ErrorFore : _palette.HintFore;
        }

        private void ReportStatus(string text, bool error)
        {
            SetStatus(text, error);
            StatusRequested?.Invoke(this, text);
        }

        private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
