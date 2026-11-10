using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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

        /// <summary>
        /// 网络位置的解析（判目录 / 递归枚举 / 读网络 m3u）。界面线程把它丢到线程池上跑，
        /// 自己一次磁盘都不碰。<b>测试钩子</b>：三个 I/O 委托可以换成假的，
        /// 和 <see cref="PlaybackCoordinator.PathProbe"/> 是同一种做法。
        /// </summary>
        private readonly NetworkPathImport _networkImport = new NetworkPathImport();

        /// <summary>只读入口，给冒烟测试用（主工程里有 <c>InternalsVisibleTo("SmokeTest")</c>）。</summary>
        internal NetworkPathImport NetworkImport => _networkImport;

        /// <summary>只读入口（给冒烟测试 / 大列表探针用）：播放列表控件即使被分离到悬浮窗也还是它。</summary>
        internal ListView PlaylistView => listViewPlaylist;

        /// <summary>
        /// "视图里的行 ↔ 模型路径"的快照（1.15.0），只在**无筛选**时维护。
        /// <para>
        /// 末尾追加靠它判断"前缀没变、只是多了几条"；切歌靠它判断"行没变、只有当前项变了"。
        /// 一旦对不上（插入 / 移动 / 删除 / 排序 / 替换 / 换主题）就退回整建。
        /// 重建一开始就置 <c>null</c>，中途出问题也不会让下一次走快路。
        /// </para>
        /// </summary>
        private List<string>? _renderedPaths;

        /// <summary>上次整建 / 追加 / 切歌之后，模型里的"当前项"索引（1.15.0 的切歌快路用）。</summary>
        private int _renderedCurrentIndex = -1;

        /// <summary>
        /// 现在挂着"正在播"标记（加粗 + 在播色）的那一行。
        /// <para>
        /// 换歌时只改这一行和新的一行，不重建整个列表（见 <see cref="UpdateNowPlayingRow"/>）。
        /// </para>
        /// </summary>
        private ListViewItem? _nowPlayingRow;

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

        /// <summary>
        /// 按模型刷新列表视图。
        /// <para>
        /// 末尾追加走 <see cref="TryAppendPlaylistView"/>（只给新增条目造行）；
        /// 其它变更（筛选 / 插入 / 删除 / 移动 / 排序 / 清空）照旧整建，保留滚动位置与选中项。
        /// </para>
        /// </summary>
        private void RefreshPlaylistView()
        {
            if (TryRefreshPlaylistViewIncrementally()) return;

            var selected = GetSelectedPlaylistIndices();
            var topIndex = listViewPlaylist.TopItem?.Index ?? 0;
            var visible = new List<int>();

            // 重建期间快照作废：中途出任何问题，下一次追加都必须退回整建。
            _renderedPaths = null;

            listViewPlaylist.BeginUpdate();
            try
            {
                listViewPlaylist.Items.Clear();

                // 行号 → 模型索引的映射只在重建时算一次。
                // 以前 FindRow 每次都遍历整个 ListView，而回填时长、拖动换位、选中
                // 都要用它，列表大了以后这些操作会一起变慢。
                _rowByPlaylistIndex.Clear();

                // 行是刚造出来的，旧的"在播"那一行已经不在了
                _nowPlayingRow = null;

                for (var i = 0; i < _playlist.Count; i++)
                {
                    if (!MatchesFilter(_playlist.Items[i])) continue;

                    visible.Add(i);
                    var row = CreatePlaylistRow(i);
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

            // 只有"无筛选"时行与模型才一一对应；下一次追加靠它判断能不能只补新行。
            _renderedPaths = _workspace.Filter.Length == 0 ? _playlist.GetPaths() : null;
            _renderedCurrentIndex = _playlist.CurrentIndex;

            UpdateTransportState();
        }

        /// <summary>
        /// 造一行：序号 / ▶ 标记 / 时长 / 提示 / 错误色 / 在播加粗。
        /// <para>整建与末尾追加共用这一处，免得两条路的行内容慢慢漂开。</para>
        /// </summary>
        private ListViewItem CreatePlaylistRow(int index)
        {
            var source = _playlist.Items[index];

            // 序号用模型索引 + 1：筛选时编号保持稳定，不会跳来跳去。
            var row = new ListViewItem((index + 1).ToString()) { Tag = index };

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
            else if (IsNowPlayingRow(source, index))
            {
                row.Font = _playingItemFont;
                row.ForeColor = _palette.PlayingFore;
                _nowPlayingRow = row;
            }

            return row;
        }

        /// <summary>
        /// 增量刷新的两条快路（1.15.0），不重建整张表：
        /// <list type="number">
        ///   <item><b>切歌</b>：条数没变、只有"当前项"变了 → 只挪在播加粗那一行（O(1)）；</item>
        ///   <item><b>末尾追加</b>：条数变多、前缀逐条相同 → 只给新增条目造行。</item>
        /// </list>
        /// <para>
        /// 判定条件是"视图里的行与模型逐条对得上"：<see cref="_renderedPaths"/> 只在无筛选的整建
        /// （或上一次快路）之后维护；插入 / 移动 / 删除 / 排序 / 替换 / 换主题都会在前缀、条数或
        /// "当前项没变"这三道关上失配，自动退回整建。<b>宁可慢，不可错</b>。
        /// </para>
        /// </summary>
        private bool TryRefreshPlaylistViewIncrementally()
        {
            if (_workspace.Filter.Length > 0) return false;

            var rendered = _renderedPaths;
            if (rendered == null) return false;

            // 条数没变：只有"当前项"变了才值得走快路。
            // 换主题 / 队列标记 / 排序这些也是条数不变，但它们要么根本没改当前项（换主题），
            // 要么前缀对不上（排序 / 移动），必须整建。
            if (_playlist.Count == rendered.Count)
            {
                if (_playlist.CurrentIndex == _renderedCurrentIndex) return false;
                if (!RenderedPrefixMatches(rendered)) return false;

                UpdateNowPlayingRow();
                UpdateTransportState();
                _renderedCurrentIndex = _playlist.CurrentIndex;
                return true;
            }

            if (_playlist.Count < rendered.Count) return false;
            if (!RenderedPrefixMatches(rendered)) return false;

            listViewPlaylist.BeginUpdate();
            try
            {
                for (var i = rendered.Count; i < _playlist.Count; i++)
                {
                    var row = CreatePlaylistRow(i);
                    listViewPlaylist.Items.Add(row);
                    _rowByPlaylistIndex[i] = row;
                    rendered.Add(_playlist.Items[i].FilePath);
                }
            }
            finally
            {
                listViewPlaylist.EndUpdate();
            }

            // 无筛选时汇总只扫一遍时长（50k 约 1～2 ms），不重建任何行。
            UpdatePlaylistSummary();
            UpdateTransportState();
            _renderedCurrentIndex = _playlist.CurrentIndex;
            return true;
        }

        /// <summary>快照与模型的前缀是否逐条相同（行与模型一一对应的判据）。</summary>
        private bool RenderedPrefixMatches(List<string> rendered)
        {
            for (var i = 0; i < rendered.Count; i++)
            {
                if (!string.Equals(_playlist.Items[i].FilePath, rendered[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
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

        /// <summary>
        /// 把"正在播"那一行的加粗 / 在播色挪到新的那一行（<b>不重建整个列表</b>）。
        /// <para>
        /// 为什么非要有这一步：<see cref="RefreshPlaylistView"/> 里的顺序是
        /// "列表模型变了就重建"，而在 <c>PlayIndex</c> 里那次模型变化
        /// （<c>_playlist.SetCurrent</c> → <c>Changed</c>）发生在 <c>_engine.Open()</c> <b>之前</b>，
        /// 于是重建时引擎的路径<b>还是上一首</b>，加粗就画在上一首上——表现就是
        /// 列表里"选中的是正在播那首、加粗的却是上一首"（所有者的截图正是这样）。
        /// 引擎路径换成新的之后，由这里只挪那两行，不必再来一次整体重建。
        /// </para>
        /// <para>没有在播的东西（停下来了、或者这一项被筛选隐藏 / 打不开）时只负责把旧标记清掉。</para>
        /// </summary>
        private void UpdateNowPlayingRow()
        {
            // 旧的那一行恢复默认：Font = null / ForeColor = Empty 就是"跟着列表走"，与刚造出来的行一样。
            // ⚠ 这两处必须一起还原，否则会留下"加粗但没颜色"这种半截状态。
            if (_nowPlayingRow != null)
            {
                _nowPlayingRow.Font = null;
                _nowPlayingRow.ForeColor = Color.Empty;
                _nowPlayingRow = null;
            }

            if (_playlist.Count == 0) return;

            var index = PlayingPlaylistIndex();
            if (index < 0 || index >= _playlist.Count) return;

            // 打不开的条目按错误色画，不参与"在播"标记（与重建时的分支保持一致）
            if (_playlist.Items[index].HasError) return;

            var row = FindRow(index);
            if (row == null) return;

            row.Font = _playingItemFont;
            row.ForeColor = _palette.PlayingFore;
            _nowPlayingRow = row;
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

            if (_workspace.Filter.Length == 0)
            {
                var (sum, known, unknown) = _workspace.DurationSummary();

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

            var (filteredTotal, filteredKnown, filteredUnknown) = _workspace.DurationSummary(visible);
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

        /// <summary>「加入播放列表」之后要不要顺手开始播放。</summary>
        private enum AddPlayback
        {
            /// <summary>只入列表，不自动播。</summary>
            None,

            /// <summary>当前没有在播才播新加入的第一个。</summary>
            IfIdle,

            /// <summary>总是播新加入的第一个（用户明确指定了要放的东西）。</summary>
            Always
        }

        /// <summary>一批后台扫描的产物：本地目录找到的、网络那一路解析出来的、有没有被取消。</summary>
        private readonly struct ScanOutcome
        {
            internal ScanOutcome(List<string> local, NetworkPathImportResult network, bool cancelled)
            {
                Local = local;
                Network = network;
                Cancelled = cancelled;
            }

            internal List<string> Local { get; }

            internal NetworkPathImportResult Network { get; }

            internal bool Cancelled { get; }
        }


        /// <summary>
        /// 把一批路径加入播放列表。<b>本地文件夹与网络位置都在后台线程展开 / 解析。</b>
        /// <para>
        /// 以前这里对目录直接同步调用 <see cref="MediaFormats.EnumerateMediaFiles(string, bool)"/>，
        /// 于是在 UI 线程上枚举 + 排序整个目录树。拖进来一个音乐库或者一个盘符，
        /// 窗口会白屏几秒到几十秒，且没有任何提示。现在只把"取出路径列表"放到后台，
        /// 真正改动播放列表仍然回到 UI 线程做。
        /// </para>
        /// <para>
        /// <b>网络位置单独一路</b>（1.14.0）：UNC / 网络盘上的路径在界面线程上一次磁盘都不碰。
        /// 扩展名已经说明是媒体 / 字幕的直接收下；m3u 与"可能是目录"的路径交给
        /// <see cref="NetworkPathImport"/> 在线程池上判目录、递归枚举、读条目。
        /// 离线时这一步会等满 SMB 超时，但等的是后台线程，窗口照常能用。
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
            var network = new List<string>();

            foreach (var path in list)
            {
                // 网络位置不在这里问 Directory.Exists：离线 NAS / SMB 上它会卡满超时，
                // 而这一段在界面线程上。
                if (MediaFormats.IsNetworkPath(path))
                {
                    // 扩展名已经说明是媒体 / 字幕的，直接按文件收下（一次磁盘都不碰，也省掉一次
                    // 后台往返）；m3u 要读里面的条目，和"可能是个目录"的一起交给后台。
                    if (MediaFormats.IsPlaylistFile(path))
                    {
                        network.Add(path);
                    }
                    else if (MediaFormats.IsMediaFile(path) || MediaFormats.IsSubtitleFile(path))
                    {
                        direct.Add(path);
                    }
                    else
                    {
                        // 扩展名不认识：可能是网络目录，也可能离线——只有后台能问出来（1.14.0）。
                        network.Add(path);
                    }

                    continue;
                }

                if (Directory.Exists(path)) folders.Add(path);
                else direct.Add(path);
            }

            // 直接给出的本地文件（以及网络串流、字幕）本来就不需要枚举，同步处理即可。
            if (direct.Count > 0)
            {
                var addedNow = AddPathsToPlaylist(direct, distinct: true);
                if (ShouldAutoPlay(playback, addedNow.Count > 0)) PlayIndex(addedNow[0]);
            }

            if (folders.Count == 0 && network.Count == 0) return;

            // 1.17.0：这一批扫描有把手（可以取消），状态栏也能显示"已经找到多少"。
            var batch = BeginScan();
            var token = batch.Token;

            SetStatus(DescribeScanStart(folders.Count, network.Count));

            Task.Run(() =>
            {
                var found = new List<string>();

                foreach (var folder in folders)
                {
                    // 本地目录没有条数上限（1.17.0 只给网络那一侧收口），但取消照样生效：
                    // 在文件夹之间看一眼，不必把整棵树扫完才响应。
                    if (token.IsCancellationRequested) break;

                    var before = found.Count;

                    try
                    {
                        found.AddRange(MediaFormats.EnumerateMediaFiles(folder, true));
                    }
                    catch (Exception ex)
                    {
// 个别子目录没有权限就跳过，不要让整批失败。
                        AppLog.Swallowed("个别子目录没有权限就跳过，不要让整批失败。", ex);
                    }

                    // 本地找到的也算"已找到"：不然只有网络那一路时进度才动，看着像卡住了。
                    if (found.Count > before) Interlocked.Add(ref _scanFound, found.Count - before);
                }

                // 网络这一路自己吞异常（逐条 try/catch），这里不会因为一条离线路径整批失败。
                var imported = _networkImport.Run(network, token, _ => NoteScanFound());

                return new ScanOutcome(found, imported, token.IsCancellationRequested);
            }).ContinueWith(
                task => PostScannedFiles(task, playback, batch),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        /// <summary>扫描开始时的状态栏：本地文件夹与网络位置分开说，别让用户以为只是本地目录慢。</summary>
        private static string DescribeScanStart(int folders, int network)
        {
            if (network == 0)
                return folders == 1 ? "正在扫描文件夹…" : $"正在扫描 {folders} 个文件夹…";

            var networkText = network == 1 ? "1 个网络位置" : $"{network} 个网络位置";

            if (folders == 0) return $"正在扫描{networkText}…";

            var folderText = folders == 1 ? "1 个文件夹" : $"{folders} 个文件夹";
            return $"正在扫描{folderText} / {networkText}…";
        }

        private bool ShouldAutoPlay(AddPlayback playback, bool addedSomething)
        {
            if (!addedSomething) return false;
            if (playback == AddPlayback.None) return false;
            return playback == AddPlayback.Always || !_engine.HasMedia;
        }

        /// <summary>后台扫描结束，回到 UI 线程把结果补进播放列表。</summary>
        private void PostScannedFiles(Task<ScanOutcome> task, AddPlayback playback, ScanBatch batch)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;

                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;

                    try
                    {
                        if (!task.IsCompletedSuccessfully)
                        {
                            // 到这里只可能是本地目录那一段抛了（网络那一段逐条吞异常）。
                            AppLog.Swallowed("扫描文件夹失败，这一批没有加进列表。", task.Exception!);
                            SetStatus("扫描文件夹失败，请看日志");
                            return;
                        }

                        var outcome = task.Result;
                        var cancelled = outcome.Cancelled || outcome.Network.Cancelled;
                        var imported = outcome.Network;
                        var added = new List<int>();

                        // 本地目录里找到的文件先加：和以前一样走 AddPathsToPlaylist 的分类（媒体 / 字幕）。
                        if (outcome.Local.Count > 0)
                            added.AddRange(AddPathsToPlaylist(outcome.Local, distinct: true));

                        // 网络目录里找到的文件 / 直接给出的网络文件（顺序由 NetworkPathImport 保证）。
                        if (imported.Items.Count > 0)
                            added.AddRange(AddPathsToPlaylist(imported.Items, distinct: true));

                        // 网络 m3u 的条目录「追加」语义：不去重（和本地 m3u 的拖入完全一致）。
                        if (imported.FromPlaylists.Count > 0)
                        {
                            var first = _playlist.Count;
                            _playlist.AddRange(imported.FromPlaylists);
                            _scanner.Enqueue(imported.FromPlaylists);

                            for (var i = 0; i < imported.FromPlaylists.Count; i++)
                                added.Add(first + i);
                        }

                        if (cancelled)
                        {
                            // 取消之后不自动开播：用户按的是"停"，不是"放"。
                            SetStatus(DescribeCancelledScan(added.Count, imported));
                        }
                        else
                        {
                            SetStatus(DescribeScanResult(added.Count, imported, outcome.Local.Count,
                                _networkImport.Limits));

                            if (ShouldAutoPlay(playback, added.Count > 0)) PlayIndex(added[0]);
                        }
                    }
                    finally
                    {
                        EndScan(batch);
                    }
                }));
            }
            catch (Exception ex)
            {
// 窗体正在销毁，结果丢掉就行。
                AppLog.Swallowed("窗体正在销毁，结果丢掉就行。", ex);
            }
        }

        /// <summary>
        /// 扫描结束后的状态栏：把"加了几项"与"哪几条没成"一次说清。
        /// <para>
        /// 网络位置离线时 <c>Directory.Exists</c> 只能返回 false，和"真的不是目录"分不开，
        /// 所以文案写"可能离线"——不假装我们知道原因。
        /// </para>
        /// </summary>
        private static string DescribeScanResult(
            int addedCount,
            NetworkPathImportResult imported,
            int localFilesFound,
            MediaScanLimits limits)
        {
            var parts = new List<string>();

            if (addedCount > 0) parts.Add($"已添加 {addedCount} 项");

            // 触到上限必须说出来：不能让用户以为"这个目录就这么点东西"。
            if (imported.Truncated)
                parts.Add($"网络目录太大，已达扫描上限（{limits.MaxDepth} 层 / {limits.MaxFiles:N0} 个文件），"
                          + "只扫了一部分");

            if (imported.Unreachable.Count > 0)
                parts.Add($"{imported.Unreachable.Count} 个网络位置没能当文件夹打开（可能离线），"
                          + "已按单个文件收下：" + imported.Unreachable[0]);

            if (imported.UnreadablePlaylists.Count > 0)
                parts.Add($"{imported.UnreadablePlaylists.Count} 个网络 m3u 读不出来（可能离线）："
                          + imported.UnreadablePlaylists[0]);

            if (imported.EmptyPlaylists.Count > 0)
                parts.Add($"{imported.EmptyPlaylists.Count} 个网络 m3u 里没有可用的条目："
                          + imported.EmptyPlaylists[0]);

            if (imported.Failures.Count > 0)
                parts.Add($"{imported.Failures.Count} 个网络位置读不出来（详情见日志）："
                          + imported.Failures[0]);

            if (imported.FromPlaylists.Count > 0)
                parts.Add($"从网络 m3u 追加 {imported.FromPlaylists.Count} 项");

            if (parts.Count == 0)
            {
                // 什么都没加：分三种情形说清楚，别停在一句"正在扫描…"上。
                if (imported.Folders.Count > 0 && imported.Items.Count == 0)
                    parts.Add("网络位置里没有找到可播放的媒体文件");
                else if (localFilesFound == 0 && imported.Items.Count == 0 && imported.Folders.Count == 0)
                    parts.Add("文件夹里没有找到可播放的媒体文件");
                else
                    parts.Add("没有新增条目（可能已经在列表里）");
            }

            return string.Join("；", parts);
        }

        /// <summary>
        /// 把一批路径加入播放列表：<b>本地</b>目录会递归展开，字幕文件会直接挂到当前媒体上。
        /// 返回新增条目的索引。
        /// <para>调用方请用 <see cref="AddPathsWithFolders"/>：本方法遇到本地目录会<b>同步</b>枚举。</para>
        /// <para>
        /// 网络位置不在这里展开目录（也不问存在性）：路径的判定与枚举在
        /// <see cref="NetworkPathImport"/> 里、后台线程上做完了，这里只按扩展名分类。
        /// 网络 m3u 在那一边就已经读成条目，正常情况下不会再走到下面那条
        /// <c>LoadPlaylistFromFile</c> 分支——留着是给"别的入口直接传网络 m3u 进来"兜底。
        /// </para>
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

                // 网络位置：一次磁盘都不碰（File.Exists / Directory.Exists 都可能等满 SMB 超时）。
                // 按扩展名分类，剩下的交给播放前的后台探测判定"在不在"。
                if (MediaFormats.IsNetworkPath(path))
                {
                    if (MediaFormats.IsSubtitleFile(path)) subtitles.Add(path);
                    else if (MediaFormats.IsPlaylistFile(path)) playlists.Add(path);
                    else media.Add(path);

                    continue;
                }

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
                else if (MediaFormats.IsMediaFile(path) || MediaFormats.IsLocationUri(path))
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

            // 网络位置不在这里查存在性（离线时 File.Exists 会卡满超时）；
            // 交给资源管理器自己试，失败它会弹自己的提示。
            if (!MediaFormats.IsNetworkPath(path) && !File.Exists(path))
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

        // =====================================================================
        // 找出重复项（1.7.0 F4）
        // =====================================================================

        private ToolStripMenuItem? _cmsFindDuplicates;

        /// <summary>测试用：右键菜单里那一项（没加就返回 <c>null</c>）。</summary>
        internal ToolStripMenuItem? MenuFindDuplicates => _cmsFindDuplicates;

        /// <summary>
        /// 在右键菜单里加「找出重复项」。
        /// <para>
        /// **不改 Designer 生成的代码**：运行时插在「从列表移除」后面。顺序与 cmsRemove 绑着，
        /// 找不到它（理论上不会）就追加到末尾。
        /// </para>
        /// </summary>
        private void ConfigureFindDuplicatesMenu()
        {
            if (_cmsFindDuplicates != null) return;

            _cmsFindDuplicates = new ToolStripMenuItem("找出重复项(&D)")
            {
                Name = "cmsFindDuplicates",
                ToolTipText = "按完整路径找出重复项：只选中，不自动删"
            };

            _cmsFindDuplicates.Click += (s, e) => FindDuplicateItems();

            var index = contextMenuPlaylist.Items.IndexOf(cmsRemove);

            if (index < 0) contextMenuPlaylist.Items.Add(_cmsFindDuplicates);
            else contextMenuPlaylist.Items.Insert(index + 1, _cmsFindDuplicates);
        }

        /// <summary>
        /// 找出重复项：每个文件**第一次出现的那一项保留**，其余重复项只**选中**（不删）。
        /// <para>
        /// 筛选状态下与拖动同口径：界面行与模型索引不连续，选错了更难解释，先请用户清掉筛选。
        /// </para>
        /// </summary>
        private void FindDuplicateItems()
        {
            if (_playlist.Count == 0)
            {
                SetStatus("播放列表是空的，没有重复项");
                return;
            }

            if (_workspace.Filter.Length > 0)
            {
                SetStatus("筛选状态下不找重复项，请先清空搜索框");
                return;
            }

            var duplicates = PlaylistDuplicates.FindDuplicates(_playlist.GetPaths());

            if (duplicates.Count == 0)
            {
                listViewPlaylist.SelectedIndices.Clear();
                SetStatus("没有重复项");
                return;
            }

            listViewPlaylist.BeginUpdate();

            try
            {
                listViewPlaylist.SelectedIndices.Clear();

                var missing = 0;

                foreach (var index in duplicates)
                {
                    var row = FindRow(index);

                    if (row == null)
                    {
                        missing++;
                        continue;
                    }

                    row.Selected = true;
                }

                SetStatus($"找到 {duplicates.Count} 项重复，已选中"
                          + (missing > 0 ? $"（{missing} 项被筛选挡住）" : string.Empty)
                          + "；要删按 Delete（不会自动删）");
            }
            finally
            {
                listViewPlaylist.EndUpdate();
            }
        }
    }
}
