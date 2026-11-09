using System;
using System.Collections.Generic;
using System.Linq;

namespace 播放器.Core
{
    /// <summary>
    /// 播放列表这块数据的<b>唯一主人</b>：列表模型、插播队列、排序方式、筛选词。
    /// <para>
    /// 以前这几样散在 <c>MainForm</c> 的四个字段里（<c>_playlist</c> / <c>_queue</c> /
    /// <c>_sortField</c> / <c>_playlistFilter</c>），谁都能直接改。收进来之后规则很硬：
    /// <b>增删改、排序、筛选只经过这里</b>，界面（ListView / 菜单 / 歌单窗口）只读或调方法。
    /// </para>
    /// <para>
    /// 排序与筛选是"这一次界面怎么排/怎么筛"，所以排序依据写回 <see cref="AppSettings"/>
    /// （下次启动照旧），筛选词不落盘（跟以前一样）。
    /// </para>
    /// </summary>
    public sealed class PlaylistWorkspace
    {
        private readonly AppSettings _settings;

        private List<string> _filterTokens = new List<string>();

        public PlaylistWorkspace(AppSettings settings)
        {
            _settings = settings;

            List = new Playlist();
            Queue = new PlaybackQueue();

            SortField = settings.SortField;
            SortDescending = settings.SortDescending;
        }

        /// <summary>有序的播放列表模型。所有条目都在这里，不再另存一份。</summary>
        public Playlist List { get; }

        /// <summary>「下一首播放」队列（纯内存，关掉程序就没了）。</summary>
        public PlaybackQueue Queue { get; }

        public PlaylistSortField SortField { get; private set; }

        public bool SortDescending { get; private set; }

        /// <summary>当前筛选词（空串 = 不筛）。</summary>
        public string Filter { get; private set; } = string.Empty;

        /// <summary>筛选词按空格拆开之后的几个词（彼此是"与"）。</summary>
        public IReadOnlyList<string> FilterTokens => _filterTokens;

        /// <summary>当前项（正在播 / 接着要从哪儿播）；没有时为 -1。</summary>
        public int CurrentIndex => List.CurrentIndex;

        public PlaylistItem? Current => List.Current;

        public int Count => List.Count;

        public void SetCurrent(int index) => List.SetCurrent(index);

        /// <summary>就地换掉某一项的地址（DVD 在菜单 / 主标题之间切换用，见 <see cref="Playlist.SetPath"/>）。</summary>
        public void SetPath(int index, string filePath) => List.SetPath(index, filePath);

        /// <summary>应用搜索关键字；空格分隔的多个词之间是"与"的关系。</summary>
        public void SetFilter(string? text)
        {
            var filter = (text ?? string.Empty).Trim();

            Filter = filter;
            _filterTokens = filter
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        /// <summary>条目是否命中当前筛选条件。</summary>
        public bool Matches(PlaylistItem item)
        {
            if (_filterTokens.Count == 0) return true;

            var name = item.DisplayName;
            var path = item.FilePath;

            foreach (var token in _filterTokens)
            {
                // 用 OrdinalIgnoreCase 而不是 CurrentCultureIgnoreCase：
                // 文件和路径的匹配不该随系统区域设置变化。土耳其语区里
                // CurrentCultureIgnoreCase 会把 "I"/"i" 映射成不同结果，
                // 搜 "id" 就匹配不上 "ID3"。
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (path.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) continue;

                return false;
            }

            return true;
        }

        /// <summary>按当前设置重新对齐排序依据（载入设置方案 / 铺设置时用；<b>不重排列表</b>）。</summary>
        public void SyncSortFromSettings()
        {
            SortField = _settings.SortField;
            SortDescending = _settings.SortDescending;
        }

        /// <summary>按字段排序，并把排序依据写回设置（下次启动照旧）。</summary>
        public void ApplySort(PlaylistSortField field, bool descending)
        {
            SortField = field;
            SortDescending = descending;

            _settings.SortField = field;
            _settings.SortDescending = descending;

            List.Sort(field, descending);
        }

        /// <summary>
        /// 把"排序依据"退回原始顺序，<b>但不重排列表</b>。
        /// <para>用在"列表顺序已经被别的东西定好了"的场合：手工拖动换位之后、载入歌单之后。
        /// 必须同时改字段、设置与菜单三处——只改字段的话下次启动还会按旧字段重新排序。</para>
        /// </summary>
        public void ResetSortToAddedOrder()
        {
            SortField = PlaylistSortField.AddedOrder;
            SortDescending = false;
            _settings.SortField = PlaylistSortField.AddedOrder;
            _settings.SortDescending = false;
        }

        /// <summary>点击排序项：已在该字段上则切换升降序，否则按升序排。返回是否变成降序。</summary>
        public bool ToggleSort(PlaylistSortField field)
        {
            var descending = SortField == field && !SortDescending;
            ApplySort(field, descending);
            return descending;
        }

        /// <summary>
        /// 条目数 + 总时长（可只算某几个索引 —— 筛选之后"这批一共多久"）。
        /// <para>未解析完的条数单独给出，避免总数看起来"少了一截"。</para>
        /// </summary>
        public (TimeSpan Total, int Known, int Unknown) DurationSummary(IReadOnlyList<int>? indices = null)
        {
            if (indices == null)
            {
                var (sum, known, unknown) = List.GetDurationSummary();
                return (sum, known, unknown);
            }

            var total = TimeSpan.Zero;
            var knownCount = 0;

            foreach (var index in indices)
            {
                if (index < 0 || index >= List.Count) continue;

                var duration = List.Items[index].Duration;
                if (!duration.HasValue) continue;

                total += duration.Value;
                knownCount++;
            }

            return (total, knownCount, indices.Count - knownCount);
        }
    }
}
