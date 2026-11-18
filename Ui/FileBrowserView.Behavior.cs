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
            RaiseStateChanged();
        }

        private void FinishWithError(string message)
        {
            _entries = new List<FileBrowserEntry>();
            _truncated = false;
            _pendingSelectPath = null;
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

            StartLoad();
        }

        // ---- 列表 -------------------------------------------------------------

        private void FillList()
        {
            var icons = _icons;
            var imageIndex = icons?.IndexOf(FileBrowserKind.Other) ?? 0;

            _visibleEntries = _entries
                .Where(entry => FileBrowserFilter.Matches(entry, _query)
                                && FileBrowserFilter.MatchesType(entry, _typeFilter))
                .ToList();

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

                    item.SubItems.Add(entry.SizeText);
                    _list.Items.Add(item);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            UpdateSortHeaders();
        }

        private void UpdateStatusCount()
        {
            SetStatus(DescribeCount(), false);
        }

        private string DescribeCount()
        {
            if (_isRoots)
                return _entries.Count == 0 ? "没有找到可用的磁盘" : $"{_entries.Count} 个驱动器";

            if (_entries.Count == 0) return "这个文件夹里没有可显示的项目";

            var filtering = IsFiltering;
            var visibleCount = _visibleEntries.Count;
            var visibleFolders = _visibleEntries.Count(entry => entry.IsDirectory);

            var text = filtering
                ? (_typeFilter == FileBrowserTypeFilter.All ? string.Empty : DescribeTypeFilter() + " · ")
                  + $"筛选 {visibleCount:N0}/{_entries.Count:N0} 项（{visibleFolders:N0} 个文件夹）"
                : $"{_entries.Count:N0} 项（{_entries.Count(entry => entry.IsDirectory):N0} 个文件夹）";

            if (_truncated) text += " · 已达显示上限，只列出了一部分";

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

        /// <summary>搜索 / 类型筛选变化：只重排现有条目，不读磁盘。</summary>
        private void ApplyFilter()
        {
            // 正在读取时不动列表；读完会按当前的查询 / 类型再 FillList。
            if (_loading) return;

            FillList();
            UpdateStatusCount();
        }

        /// <summary>路径变化时清空过滤；F5 刷新保留。</summary>
        private void ResetFilter()
        {
            if (!IsFiltering && _search.Text.Length == 0) return;

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
            _typeFilter = FileBrowserTypeFilter.All;
        }

        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                ClearSearch();
                _list.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                _list.Focus();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        internal void ClearSearch()
        {
            if (_search.Text.Length == 0 && _query.Length == 0) return;

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

        internal void SetShowAllFiles(bool value)
        {
            if (_state.ShowAllFiles == value) return;

            _state.ShowAllFiles = value;
            RaiseStateChanged();

            if (_loadedOnce) StartLoad();
        }

        internal void SetSort(FileSortField field, bool descending)
        {
            _state.SortField = field;
            _state.SortDescending = descending;

            if (_entries.Count > 0)
            {
                FileBrowserService.Sort(_entries, _state.SortField, _state.SortDescending);
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
