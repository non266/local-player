using System;
using System.Collections.Generic;
using System.Linq;

namespace 播放器.Core
{
    /// <summary>播放列表排序依据。</summary>
    public enum PlaylistSortField
    {
        /// <summary>按加入顺序，用于"恢复原始顺序"。</summary>
        AddedOrder,

        Name,

        Path,

        Duration
    }

    /// <summary>
    /// 有序的播放列表模型。所有变更都会触发 <see cref="Changed"/>，
    /// 界面据此整体刷新（列表规模在本地播放场景下完全够用）。
    /// </summary>
    public sealed class Playlist
    {
        private readonly List<PlaylistItem> _items = new List<PlaylistItem>();
        private int _nextOrder;

        /// <summary>
        /// 路径 → 索引的索引表。
        /// <para>
        /// 时长扫描是<b>逐条</b>回填的，每回填一条都要按路径找条目。以前是线性扫描，
        /// 于是给 n 条记录补全时长就要 O(n²) 次字符串比较（外加重复扫描 ListView），
        /// 列表上千条时后面几条每次都要几百万次比较。这里用字典把查找压成 O(1)。
        /// </para>
        /// <para>任何改变顺序或数量的操作都必须重建它（见 <see cref="RebuildIndex"/>）。</para>
        /// </summary>
        private readonly Dictionary<string, int> _indexByPath =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>列表内容或当前项发生变化。</summary>
        public event EventHandler? Changed;

        /// <summary>
        /// <b>列表内容</b>发生变化（增删改、换位、排序、清空），但切换当前项不算。
        /// <para>
        /// <see cref="Changed"/> 把"开始播下一首"也当成一次变化，界面据此整体刷新是对的。
        /// 但歌单的"有没有改动过"不能用它判断：播放一首歌就会把当前项挪一格，
        /// 那样每播一首歌单都会被标成"已修改"，提示就变成噪音了。
        /// </para>
        /// </summary>
        public event EventHandler? ContentChanged;

        /// <summary>只读的条目集合，索引即列表序号。</summary>
        public IReadOnlyList<PlaylistItem> Items => _items;

        public int Count => _items.Count;

        /// <summary>当前（正在播放的）条目索引，-1 表示无。</summary>
        public int CurrentIndex { get; private set; } = -1;

        /// <summary>当前条目。</summary>
        public PlaylistItem? Current =>
            CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;

        /// <summary>
        /// 添加单个文件。返回其索引。
        /// </summary>
        public int Add(string filePath)
        {
            _items.Add(new PlaylistItem(filePath) { AddedOrder = _nextOrder++ });
            RememberFirst(filePath, _items.Count - 1);
            OnContentChanged();
            return _items.Count - 1;
        }

        /// <summary>
        /// 批量添加，返回新增条目的索引列表。整批只触发一次 <see cref="Changed"/>。
        /// </summary>
        public IReadOnlyList<int> AddRange(IEnumerable<string> filePaths)
        {
            var added = new List<int>();
            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                _items.Add(new PlaylistItem(path) { AddedOrder = _nextOrder++ });
                added.Add(_items.Count - 1);
                RememberFirst(path, _items.Count - 1);
            }

            if (added.Count > 0) OnChanged();
            return added;
        }

        /// <summary>
        /// 批量添加并跳过已存在的路径，用于"打开文件夹"这类容易重复的场景。
        /// </summary>
        public IReadOnlyList<int> AddRangeDistinct(IEnumerable<string> filePaths)
        {
            // 同一批里也可能有重复（比如选了互相重叠的两个目录），所以还需要一个批内集合。
            var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fresh = new List<string>();

            foreach (var path in filePaths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (_indexByPath.ContainsKey(path)) continue;
                if (!batch.Add(path)) continue;

                fresh.Add(path);
            }

            return AddRange(fresh);
        }

        /// <summary>在指定位置插入。</summary>
        public void Insert(int index, string filePath)
        {
            index = Math.Clamp(index, 0, _items.Count);
            _items.Insert(index, new PlaylistItem(filePath) { AddedOrder = _nextOrder++ });

            if (CurrentIndex >= index) CurrentIndex++;
            RebuildIndex();
            OnContentChanged();
        }

        /// <summary>
        /// 移除指定索引。若移除的是当前项，则当前索引回到 -1（由调用方决定是否停止播放）。
        /// </summary>
        public void RemoveAt(int index)
        {
            if (index < 0 || index >= _items.Count) return;

            _items.RemoveAt(index);

            if (index == CurrentIndex)
            {
                CurrentIndex = -1;
            }
            else if (index < CurrentIndex)
            {
                CurrentIndex--;
            }

            RebuildIndex();
            OnContentChanged();
        }

        /// <summary>
        /// 批量移除（内部按索引倒序删除）。
        /// <para>
        /// <b>整批只触发一次 <see cref="Changed"/></b>：以前是每个索引走一次
        /// <see cref="RemoveAt"/>，界面收到 n 次通知就要整体重建 n 次列表，
        /// 一次删掉几百项会让窗口明显卡住。
        /// </para>
        /// </summary>
        public void RemoveMany(IEnumerable<int> indexes)
        {
            var targets = indexes.Distinct().Where(i => i >= 0 && i < _items.Count).ToList();
            if (targets.Count == 0) return;

            var removedCurrent = targets.Contains(CurrentIndex);

            // 从后往前删，前面的索引才不会失效。
            targets.Sort();
            for (var i = targets.Count - 1; i >= 0; i--)
                _items.RemoveAt(targets[i]);

            if (removedCurrent)
            {
                CurrentIndex = -1;
            }
            else if (CurrentIndex >= 0)
            {
                // 数一数当前项前面被删掉了几个。
                var before = 0;
                foreach (var index in targets)
                {
                    if (index < CurrentIndex) before++;
                }

                CurrentIndex -= before;
            }

            RebuildIndex();
            OnContentChanged();
        }

        /// <summary>把条目从 <paramref name="from"/> 移到 <paramref name="to"/>。</summary>
        public void Move(int from, int to)
        {
            if (from < 0 || from >= _items.Count) return;
            to = Math.Clamp(to, 0, _items.Count - 1);
            if (from == to) return;

            var item = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, item);

            // 同步当前索引，保证正在播放的条目不会因为拖动而错位。
            if (CurrentIndex == from)
            {
                CurrentIndex = to;
            }
            else if (from < CurrentIndex && to >= CurrentIndex)
            {
                CurrentIndex--;
            }
            else if (from > CurrentIndex && to <= CurrentIndex)
            {
                CurrentIndex++;
            }

            RebuildIndex();
            OnContentChanged();
        }

        /// <summary>
        /// 按指定依据重排列表。当前播放项会跟着走，不会因为排序而错位。
        /// </summary>
        public void Sort(PlaylistSortField field, bool descending)
        {
            if (_items.Count <= 1) return;

            var current = Current;

            IOrderedEnumerable<PlaylistItem> ordered = field switch
            {
                PlaylistSortField.Name => _items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                PlaylistSortField.Path => _items.OrderBy(i => i.FilePath, StringComparer.CurrentCultureIgnoreCase),
                PlaylistSortField.Duration => _items.OrderBy(i => i.Duration ?? TimeSpan.Zero),
                _ => _items.OrderBy(i => i.AddedOrder)
            };

            var sorted = (descending ? ordered.Reverse() : ordered).ToList();

            _items.Clear();
            _items.AddRange(sorted);

            // 用引用找回来，避免依赖 PlaylistItem 的 Equals。
            CurrentIndex = current == null ? -1 : _items.IndexOf(current);

            RebuildIndex();
            OnContentChanged();
        }

        /// <summary>清空列表。</summary>
        public void Clear()
        {
            if (_items.Count == 0 && CurrentIndex == -1) return;
            _items.Clear();
            _indexByPath.Clear();
            CurrentIndex = -1;
            OnContentChanged();
        }

        /// <summary>设置当前条目索引（不触发播放，仅更新模型）。</summary>
        public void SetCurrent(int index)
        {
            var value = index >= 0 && index < _items.Count ? index : -1;
            if (value == CurrentIndex) return;
            CurrentIndex = value;
            OnChanged();
        }

        /// <summary>按路径查找首个匹配项。O(1)（走 <see cref="_indexByPath"/>）。</summary>
        public int IndexOfPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return -1;
            return _indexByPath.TryGetValue(filePath, out var index) ? index : -1;
        }

        /// <summary>
        /// 更新某个条目的时长（由后台扫描器回调）。
        /// <para>
        /// 这里刻意<b>不</b>触发 <see cref="Changed"/>：扫描是逐条完成的，
        /// 整体重建列表会不断重置滚动位置并造成闪烁，界面改为直接更新对应行。
        /// </para>
        /// </summary>
        public bool SetDuration(string filePath, TimeSpan duration)
        {
            var index = IndexOfPath(filePath);
            if (index < 0) return false;

            var item = _items[index];
            if (item.Duration.HasValue) return false;

            item.Duration = duration;
            return true;
        }

        // ---- 路径索引的维护 ---------------------------------------------------

        /// <summary>登记路径对应的索引，<b>只记第一次出现的位置</b>（与旧的线性查找语义一致）。</summary>
        private void RememberFirst(string filePath, int index)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (!_indexByPath.ContainsKey(filePath)) _indexByPath[filePath] = index;
        }

        /// <summary>数量或顺序发生位移后重建索引表。插入/删除/移动/排序本来就已经是 O(n)。</summary>
        private void RebuildIndex()
        {
            _indexByPath.Clear();

            for (var i = 0; i < _items.Count; i++)
                RememberFirst(_items[i].FilePath, i);
        }

        /// <summary>取出所有条目路径。</summary>
        public List<string> GetPaths() => _items.Select(i => i.FilePath).ToList();

        /// <summary>
        /// 汇总列表时长：已知时长之和，以及尚未解析出时长的条目数。
        /// 时长由后台扫描器逐条补全，所以这两个值会随着扫描推进而变化。
        /// </summary>
        public (TimeSpan Total, int Known, int Unknown) GetDurationSummary()
        {
            var total = TimeSpan.Zero;
            var known = 0;

            foreach (var item in _items)
            {
                if (item.Duration.HasValue)
                {
                    total += item.Duration.Value;
                    known++;
                }
            }

            return (total, known, _items.Count - known);
        }

        private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// 内容变了：先通知"内容有改动"，再通知界面整体刷新。
        /// <para>顺序固定成这样，"已修改"标记一定先于重新渲染的那一次发生。</para>
        /// </summary>
        private void OnContentChanged()
        {
            ContentChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
