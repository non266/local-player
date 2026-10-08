using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 主窗口中与播放列表相关的部分：列表渲染、增删改、拖放与时长回填。
    /// <para>
    /// 关键约定：<c>ListViewItem.Tag</c> 里存的是<b>播放列表模型的索引</b>。
    /// 一旦启用搜索过滤，界面行号就和模型索引不再一一对应，
    /// 所有"从界面行 → 模型条目"的转换都必须走 <see cref="GetRowPlaylistIndex"/>。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        /// <summary>播放列表索引 → 界面行。每次重建列表时一起重建，供 FindRow 做 O(1) 查找。</summary>
        private readonly Dictionary<int, ListViewItem> _rowByPlaylistIndex =
            new Dictionary<int, ListViewItem>();

        /// <summary>标题里的总时长需要重算（由后台时长扫描逐条置位，界面计时器统一处理）。</summary>
        private bool _playlistSummaryDirty;

        private void OnPlaylistModelChanged(object? sender, EventArgs e) => RefreshPlaylistView();

        /// <summary>
        /// 按列表当前宽度分配列宽，保证「时长」列始终看得见。
        /// <para>
        /// 设计器里写的是固定列宽（44 + 196 + 72 = 312 逻辑像素）。侧栏显示以后，
        /// 播放列表面板可能只剩 220 逻辑像素宽，固定列宽会超出可用宽度，
        /// 逼出一条横向滚动条、把时长列挤到视野外（实测在较小的窗口上就是这样）。
        /// 这里让标题列吃掉剩余宽度，序号与时长保持固定。
        /// </para>
        /// </summary>
        private void AdjustPlaylistColumns()
        {
            var width = listViewPlaylist.ClientSize.Width;
            if (width <= 0) return;

            var fixedWidth = colIndex.Width + colDuration.Width;

            // 留一点余量给边框，避免刚好卡着又冒出横向滚动条
            var titleWidth = width - fixedWidth - 4;

            // 窄到标题列只剩几十像素时就不再压了，让横向滚动条出现，至少还能读
            colTitle.Width = Math.Max(60, titleWidth);
        }

        /// <summary>按模型重建列表视图（应用当前筛选，保留滚动位置与选中项）。</summary>
        private void RefreshPlaylistView()
        {
            var selected = GetSelectedPlaylistIndices();
            var topIndex = listViewPlaylist.TopItem?.Index ?? 0;
            var visible = new List<int>();

            listViewPlaylist.BeginUpdate();
            try
            {
                listViewPlaylist.Items.Clear();

                // 行号 → 模型索引的映射只在重建时算一次。
                // 以前 FindRow 每次都遍历整个 ListView，而回填时长、拖动换位、选中
                // 都要用它，列表大了以后这些操作会一起变慢。
                _rowByPlaylistIndex.Clear();

                for (var i = 0; i < _playlist.Count; i++)
                {
                    var source = _playlist.Items[i];

                    if (!MatchesFilter(source)) continue;

                    visible.Add(i);

                    // 序号用模型索引 + 1：筛选时编号保持稳定，不会跳来跳去。
                    var row = new ListViewItem((i + 1).ToString()) { Tag = i };

                    // 「下一首播放」排进队的行在标题前挂个 ▶（标记只存在内存里，见 PlaylistItem.Queued）。
                    row.SubItems.Add(source.Queued ? "▶ " + source.DisplayName : source.DisplayName);
                    row.SubItems.Add(source.DurationText);
                    row.ToolTipText = source.HasError && !string.IsNullOrEmpty(source.ErrorMessage)
                        ? source.FilePath + Environment.NewLine + "无法播放：" + source.ErrorMessage
                        : source.FilePath;

                    if (source.HasError)
                    {
                        row.ForeColor = _palette.ErrorFore;
                    }
                    else if (IsNowPlayingRow(source, i))
                    {
                        row.Font = _playingItemFont;
                        row.ForeColor = _palette.PlayingFore;
                    }

                    listViewPlaylist.Items.Add(row);
                    _rowByPlaylistIndex[i] = row;
                }
            }
            finally
            {
                listViewPlaylist.EndUpdate();
            }

            UpdatePlaylistSummary(visible);

            // 还原滚动位置，避免扫描时长时列表跳动。
            if (topIndex > 0 && topIndex < listViewPlaylist.Items.Count)
                listViewPlaylist.TopItem = listViewPlaylist.Items[topIndex];

            foreach (var index in selected)
            {
                var row = FindRow(index);
                if (row != null) row.Selected = true;
            }

            UpdateTransportState();
        }

        /// <summary>
        /// 这一行是不是"现在正在播的那一项"（加粗显示）。
        /// <para>
        /// 一般就是列表的当前项；但「下一首播放」的插播会刻意不动当前项，
        /// 那会儿要按<b>引擎真正打开的路径</b>来认，否则加粗的是另外一首，和状态栏自相矛盾。
        /// </para>
        /// <para>还没开始播（例如刚恢复会话）时退回"加粗当前项"，和以前一样。</para>
        /// </summary>
        private bool IsNowPlayingRow(PlaylistItem item, int index)
        {
            var path = _engine.CurrentPath;

            if (!string.IsNullOrEmpty(path))
                return string.Equals(path, item.FilePath, StringComparison.OrdinalIgnoreCase);

            return index == _playlist.CurrentIndex;
        }

        /// <summary>把当前播放项滚动到可见位置并选中。</summary>
        private void HighlightPlayingItem()
        {
            // 按引擎路径找：插播时"在播的那一项"和"列表当前项"不是同一项。
            var index = PlayingPlaylistIndex();
            var row = FindRow(index >= 0 ? index : _playlist.CurrentIndex);
            if (row == null) return;

            row.EnsureVisible();

            if (listViewPlaylist.SelectedIndices.Count <= 1)
            {
                listViewPlaylist.SelectedIndices.Clear();
                row.Selected = true;
                row.Focused = true;
            }
        }

        /// <summary>按播放列表索引找到对应的界面行；被筛选隐藏时返回 <c>null</c>。O(1)。</summary>
        private ListViewItem? FindRow(int playlistIndex)
        {
            if (playlistIndex < 0) return null;
            return _rowByPlaylistIndex.TryGetValue(playlistIndex, out var row) ? row : null;
        }

        private static int GetRowPlaylistIndex(ListViewItem row) =>
            row.Tag is int index ? index : -1;

        /// <summary>取出选中行对应的播放列表索引。</summary>
        private List<int> GetSelectedPlaylistIndices()
        {
            var result = new List<int>();

            foreach (ListViewItem row in listViewPlaylist.SelectedItems)
            {
                var index = GetRowPlaylistIndex(row);
                if (index >= 0) result.Add(index);
            }

            result.Sort();
            return result;
        }

        private void ApplyScannedDuration(DurationFoundEventArgs e)
        {
            var index = _playlist.IndexOfPath(e.FilePath);

            if (index < 0)
            {
                // 以前这里是静默 return：界面上只表现为"某一项时长未知"，卡在哪儿只能猜
                AppLog.Warn($"时长回填时列表里已经没有这一项（可能已被移除或换了列表）：{e.FilePath}");
                return;
            }

            if (!_playlist.SetDuration(e.FilePath, e.Duration))
            {
                AppLog.Debug($"时长回填被跳过（这一项已经有值了）：{e.FilePath}");
                return;
            }

            // 该行可能正因为筛选而不在界面上，找不到就算了。
            var row = FindRow(index);
            if (row != null)
                row.SubItems[2].Text = TimeFormatter.Format(e.Duration);

            // 标题里的总时长标记为待更新，交给 200ms 的界面计时器去算。
            // 以前每回填一条就重算一次（O(n) 扫全部条目 + 重建可见索引表），
            // 给上千条记录补时长时这部分会累积成明显的卡顿。
            _playlistSummaryDirty = true;
        }

        /// <summary>当前界面上可见的播放列表索引。</summary>
        private List<int> GetVisiblePlaylistIndices()
        {
            var result = new List<int>(listViewPlaylist.Items.Count);

            foreach (ListViewItem row in listViewPlaylist.Items)
            {
                var index = GetRowPlaylistIndex(row);
                if (index >= 0) result.Add(index);
            }

            return result;
        }

        /// <summary>
        /// 刷新播放列表标题：条目数 + 总时长。
        /// <para>
        /// VLC 本体并不显示列表总时长（社区要靠扩展实现），这里直接内置：
        /// 时长由后台扫描器逐条补全，因此未解析完的条目会单独标注，避免总数看起来"少了一截"。
        /// 启用搜索时改为汇总筛选结果，这样"筛出来的这批一共多久"一目了然。
        /// </para>
        /// </summary>
        private void UpdatePlaylistSummary(IReadOnlyList<int>? visible = null)
        {
            var total = _playlist.Count;
            if (total == 0)
            {
                SetPlaylistHeader("播放列表（0）");
                return;
            }

            if (_playlistFilter.Length == 0)
            {
                var (sum, known, unknown) = _playlist.GetDurationSummary();

                if (known == 0)
                {
                    SetPlaylistHeader($"播放列表（{total}）· 总计 --:--");
                    return;
                }

                var summary = $"播放列表（{total}）· 总计 {TimeFormatter.Format(sum)}";
                if (unknown > 0)
                    summary += $"（{unknown} 项未知）";

                SetPlaylistHeader(summary);
                return;
            }

            visible ??= GetVisiblePlaylistIndices();

            var filteredTotal = TimeSpan.Zero;
            var filteredKnown = 0;

            foreach (var index in visible)
            {
                var duration = _playlist.Items[index].Duration;
                if (!duration.HasValue) continue;

                filteredTotal += duration.Value;
                filteredKnown++;
            }

            var filteredUnknown = visible.Count - filteredKnown;
            var text = $"筛选 {visible.Count}/{total} 项 · 总计 "
                       + (filteredKnown == 0 ? "--:--" : TimeFormatter.Format(filteredTotal));

            if (filteredUnknown > 0)
                text += $"（{filteredUnknown} 项未知）";

            SetPlaylistHeader(text);
        }

        /// <summary>
        /// 把一段文字设成播放列表标题，前面带上"当前歌单"前缀。
        /// <para>前缀（例如「【我的最爱·未保存】」）单独算，所以筛选/总计那几种写法
        /// 都能共用同一套拼接，不用各自记着加前缀。</para>
        /// </summary>
        private void SetPlaylistHeader(string text) =>
            lblPlaylistHeader.Text = PlaylistHeaderPrefix() + text;

        private void OnDurationFound(object? sender, DurationFoundEventArgs e)
        {
            if (_disposed) return;

            if (!IsHandleCreated)
            {
                // 窗口句柄还没建好（构造函数里就开始扫描时会出现）：以前这里直接 return，
                // 那条时长就永久丢了——界面上表现为"某一项时长一直是 --:--"。
                AppLog.Warn($"时长已解析但窗口句柄还没就绪，先攒着稍后补：{e.FilePath}");

                lock (_pendingDurations) _pendingDurations.Add(e);
                return;
            }

            try
            {
                BeginInvoke(new Action(() => ApplyScannedDuration(e)));
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>句柄还没就绪时攒下来的时长结果（见 <see cref="OnDurationFound"/>）。</summary>
        private readonly List<DurationFoundEventArgs> _pendingDurations = new List<DurationFoundEventArgs>();

        private void FlushPendingDurations()
        {
            List<DurationFoundEventArgs>? pending = null;

            lock (_pendingDurations)
            {
                if (_pendingDurations.Count > 0)
                {
                    pending = new List<DurationFoundEventArgs>(_pendingDurations);
                    _pendingDurations.Clear();
                }
            }

            if (pending == null) return;

            AppLog.Info($"补上 {pending.Count} 条在窗口就绪之前就解析好的时长");

            foreach (var item in pending)
                ApplyScannedDuration(item);
        }

        /// <summary>加入播放列表之后要不要顺手开始播放。</summary>
        private enum AddPlayback
        {
            /// <summary>只入列表，不自动播。</summary>
            None,

            /// <summary>当前没有在播才播新加入的第一个。</summary>
            IfIdle,

            /// <summary>总是播新加入的第一个（用户明确指定了要放的东西）。</summary>
            Always
        }

        /// <summary>
        /// 把一批路径加入播放列表。<b>文件夹会在后台线程递归展开。</b>
        /// <para>
        /// 以前这里对目录直接同步调用 <see cref="MediaFormats.EnumerateMediaFiles(string, bool)"/>，
        /// 于是在 UI 线程上枚举 + 排序整个目录树。拖进来一个音乐库或者一个盘符，
        /// 窗口会白屏几秒到几十秒，且没有任何提示。现在只把"取出路径列表"放到后台，
        /// 真正改动播放列表仍然回到 UI 线程做。
        /// </para>
        /// </summary>
        private void AddPathsWithFolders(IEnumerable<string> paths, AddPlayback playback)
        {
            var list = paths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim('"', ' '))
                .Where(p => p.Length > 0)
                .ToList();

            if (list.Count == 0) return;

            var folders = new List<string>();
            var direct = new List<string>();

            foreach (var path in list)
            {
                if (Directory.Exists(path)) folders.Add(path);
                else direct.Add(path);
            }

            // 直接给出的文件（以及网络串流、字幕）本来就不需要枚举，同步处理即可。
            if (direct.Count > 0)
            {
                var addedNow = AddPathsToPlaylist(direct, distinct: true);
                if (ShouldAutoPlay(playback, addedNow.Count > 0)) PlayIndex(addedNow[0]);
            }

            if (folders.Count == 0) return;

            SetStatus(folders.Count == 1
                ? "正在扫描文件夹…"
                : $"正在扫描 {folders.Count} 个文件夹…");

            Task.Run(() =>
            {
                var found = new List<string>();
                foreach (var folder in folders)
                {
                    try
                    {
                        found.AddRange(MediaFormats.EnumerateMediaFiles(folder, true));
                    }
                    catch (Exception ex)
                    {
// 个别子目录没有权限就跳过，不要让整批失败。
                        AppLog.Swallowed("个别子目录没有权限就跳过，不要让整批失败。", ex);
                    }
                }
                return found;
            }).ContinueWith(
                task => PostScannedFiles(task, playback),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private bool ShouldAutoPlay(AddPlayback playback, bool addedSomething)
        {
            if (!addedSomething) return false;
            if (playback == AddPlayback.None) return false;
            return playback == AddPlayback.Always || !_engine.HasMedia;
        }

        /// <summary>后台枚举结束，回到 UI 线程把结果补进播放列表。</summary>
        private void PostScannedFiles(Task<List<string>> task, AddPlayback playback)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;

                    var files = task.IsCompletedSuccessfully ? task.Result : new List<string>();
                    if (files.Count == 0)
                    {
                        SetStatus("文件夹里没有找到可播放的媒体文件");
                        return;
                    }

                    var added = AddPathsToPlaylist(files, distinct: true);
                    if (ShouldAutoPlay(playback, added.Count > 0)) PlayIndex(added[0]);
                }));
            }
            catch (Exception ex)
            {
// 窗体正在销毁，结果丢掉就行。
                AppLog.Swallowed("窗体正在销毁，结果丢掉就行。", ex);
            }
        }

        /// <summary>
        /// 把一批路径加入播放列表：目录会递归展开，字幕文件会直接挂到当前媒体上。
        /// 返回新增条目的索引。
        /// <para>调用方请用 <see cref="AddPathsWithFolders"/>：本方法遇到目录会<b>同步</b>枚举。</para>
        /// </summary>
        private List<int> AddPathsToPlaylist(IEnumerable<string> paths, bool distinct)
        {
            var media = new List<string>();
            var subtitles = new List<string>();
            var playlists = new List<string>();

            foreach (var raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var path = raw.Trim('"', ' ');

                if (Directory.Exists(path))
                {
                    media.AddRange(MediaFormats.EnumerateMediaFiles(path, true));
                }
                else if (MediaFormats.IsSubtitleFile(path) && File.Exists(path))
                {
                    subtitles.Add(path);
                }
                else if (MediaFormats.IsPlaylistFile(path) && File.Exists(path))
                {
                    // m3u / m3u8 不是媒体文件：丢给 VLC 只会得到一条"无法播放"，
                    // 而拖它进来的人想要的是"把里面的条目加进来"。
                    playlists.Add(path);
                }
                else if (MediaFormats.IsMediaFile(path) || MediaFormats.IsStreamUri(path))
                {
                    media.Add(path);
                }
                else if (File.Exists(path))
                {
                    // 扩展名不在已知列表里也交给 VLC 试一次。
                    media.Add(path);
                }
            }

            var added = distinct ? _playlist.AddRangeDistinct(media) : _playlist.AddRange(media);

            if (added.Count > 0)
            {
                _scanner.Enqueue(added.Select(i => _playlist.Items[i].FilePath));
                SetStatus($"已添加 {added.Count} 个文件到播放列表");
            }

            if (subtitles.Count > 0)
            {
                if (_engine.HasMedia)
                {
                    foreach (var subtitle in subtitles)
                        _engine.AddSubtitleFile(subtitle);

                    SetStatus("已加载字幕：" + Path.GetFileName(subtitles[subtitles.Count - 1]));
                }
                else
                {
                    SetStatus("请先播放一个视频，再加载字幕文件");
                }
            }

            // 拖进来的 m3u / m3u8 走「从 m3u 文件追加」那条路（和 Ctrl+L 完全同一条代码）：
            // 追加而不是替换——拖放是"往列表里加东西"的手势，替换会把现有列表弄没。
            foreach (var playlist in playlists)
                LoadPlaylistFromFile(playlist, replace: false);

            return added.ToList();
        }

        /// <summary>
        /// 给冒烟测试用的入口：等价于"把这一批路径拖进窗口"（不自动播放）。
        /// <para>拖放本身没法在测试里稳定复现（要合成鼠标消息），所以走同一个分类与添加逻辑。</para>
        /// </summary>
        internal void AddPathsForTest(IEnumerable<string> paths) =>
            AddPathsWithFolders(paths, AddPlayback.None);

        private void RemoveSelectedPlaylistItems()
        {
            var selected = GetSelectedPlaylistIndices();
            if (selected.Count == 0)
            {
                SetStatus("请先在列表中选择要移除的项目");
                return;
            }

            // 移除正在播放的项时停止播放，避免"列表里没有但仍在响"的困惑状态。
            if (selected.Contains(_playlist.CurrentIndex))
            {
                _engine.Stop();
                UpdateWindowTitle();
            }

            _playlist.RemoveMany(selected);
            SetStatus($"已从播放列表移除 {selected.Count} 项");
        }

        private void MoveSelected(int delta)
        {
            var selected = GetSelectedPlaylistIndices();
            if (selected.Count != 1)
            {
                SetStatus("请先选中一项再移动");
                return;
            }

            var from = selected[0];
            var to = from + delta;
            if (to < 0 || to >= _playlist.Count) return;

            _playlist.Move(from, to);
            SelectPlaylistIndex(to);
        }

        /// <summary>按模型索引选中并滚动到可见位置。</summary>
        private void SelectPlaylistIndex(int playlistIndex)
        {
            var row = FindRow(playlistIndex);
            if (row == null) return;

            listViewPlaylist.SelectedIndices.Clear();
            row.Selected = true;
            row.EnsureVisible();
        }

        private void ClearPlaylist()
        {
            if (_playlist.Count == 0) return;

            var answer = MessageBox.Show(
                this,
                "确定要清空播放列表吗？当前正在播放的媒体也会停止。",
                "清空播放列表",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            _engine.Stop();
            _scanner.ClearPending();
            _playlist.Clear();
            ClearPlaylistFilter();

            UpdateWindowTitle();
            UpdateTransportState();
            SetStatus("播放列表已清空");
        }

        private void OnPlaylistDoubleClick(object? sender, EventArgs e)
        {
            var selected = GetSelectedPlaylistIndices();
            if (selected.Count == 0) return;

            PlayIndex(selected[0]);
        }

        private void OnPlaylistKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                PlaySelectedItem();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedPlaylistItems();
                e.Handled = true;
            }
        }

        private void OnPlaylistMouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            var row = listViewPlaylist.GetItemAt(e.X, e.Y);
            if (row == null) return;
            if (row.Selected) return;

            listViewPlaylist.SelectedIndices.Clear();
            row.Selected = true;
        }

        private void RevealSelectedInExplorer()
        {
            var selected = GetSelectedPlaylistIndices();
            if (selected.Count == 0) return;

            var path = _playlist.Items[selected[0]].FilePath;
            if (!File.Exists(path))
            {
                SetStatus("文件不存在：" + path);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError("无法打开资源管理器", ex);
            }
        }

        private void CopySelectedPaths()
        {
            var selected = GetSelectedPlaylistIndices();
            if (selected.Count == 0) return;

            var text = string.Join(Environment.NewLine, selected.Select(i => _playlist.Items[i].FilePath));

            try
            {
                Clipboard.SetText(text);
                SetStatus($"已复制 {selected.Count} 条路径到剪贴板");
            }
            catch (Exception ex)
            {
                ShowError("复制失败", ex);
            }
        }

        /// <summary>在视频画面上滚动滚轮即可调节音量。</summary>
        private void OnSurfaceMouseWheel(object? sender, MouseEventArgs e)
        {
            AdjustVolume(e.Delta > 0 ? 5 : -5);
        }
    }
}
