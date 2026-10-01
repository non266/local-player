using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

namespace 播放器
{
    /// <summary>
    /// 主窗口中播放列表的检索与重排：搜索过滤、排序、拖动换位。
    /// </summary>
    public partial class MainForm
    {
        private string _playlistFilter = string.Empty;
        private List<string> _filterTokens = new List<string>();
        private PlaylistSortField _sortField = PlaylistSortField.AddedOrder;
        private bool _sortDescending;
        private bool _draggingRow;

        // -----------------------------------------------------------------
        // 搜索过滤
        // -----------------------------------------------------------------

        /// <summary>应用搜索关键字；空格分隔的多个词之间是"与"的关系。</summary>
        private void ApplyPlaylistFilter(string? text)
        {
            var filter = (text ?? string.Empty).Trim();

            if (string.Equals(filter, _playlistFilter, StringComparison.Ordinal)) return;

            _playlistFilter = filter;
            _filterTokens = filter
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            RefreshPlaylistView();

            if (filter.Length == 0)
                SetStatus($"已清除筛选，共 {_playlist.Count} 项");
            else
                SetStatus($"筛选「{filter}」，匹配 {listViewPlaylist.Items.Count}/{_playlist.Count} 项");
        }

        private void ClearPlaylistFilter()
        {
            if (_playlistFilter.Length == 0) return;

            if (txtPlaylistSearch.Text.Length > 0)
                txtPlaylistSearch.Clear();   // 会触发 TextChanged 走一遍刷新
            else
                ApplyPlaylistFilter(string.Empty);
        }

        /// <summary>条目是否命中当前筛选条件。</summary>
        private bool MatchesFilter(PlaylistItem item)
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

        // -----------------------------------------------------------------
        // 排序
        // -----------------------------------------------------------------

        private void ApplySort(PlaylistSortField field, bool descending)
        {
            _sortField = field;
            _sortDescending = descending;

            _settings.SortField = field;
            _settings.SortDescending = descending;

            _playlist.Sort(field, descending);
            RefreshPlaylistView();
            ApplySortMenu();

            var name = SortFieldName(field);
            SetStatus(field == PlaylistSortField.AddedOrder
                ? "已恢复原始顺序"
                : $"已按{name}{(descending ? "降序" : "升序")}排列");
        }

        /// <summary>
        /// 把"排序依据"退回原始顺序，<b>但不重排列表</b>。
        /// <para>
        /// 用在"列表顺序已经被别的东西定好了"的场合：手工拖动换位之后，
        /// 以及载入歌单之后（歌单文件里的顺序就是用户存的顺序）。
        /// 必须同时改字段、设置和菜单三处——只改字段的话下次启动
        /// <c>RestoreSession</c> 还会按旧字段重新排序，把用户排好的顺序悄悄丢掉。
        /// </para>
        /// </summary>
        private void ResetSortToAddedOrder()
        {
            _sortField = PlaylistSortField.AddedOrder;
            _sortDescending = false;
            _settings.SortField = PlaylistSortField.AddedOrder;
            _settings.SortDescending = false;

            ApplySortMenu();
        }

        /// <summary>点击排序项：已在该字段上则切换升降序，否则按升序排。</summary>
        private void ToggleSort(PlaylistSortField field)
        {
            var descending = _sortField == field && !_sortDescending;
            ApplySort(field, descending);
        }

        /// <summary>
        /// 点列表表头排序。<b>三下走一圈</b>：升序 → 降序 → 回原始顺序。
        /// <para>
        /// 第 0 列是序号，点它直接"恢复原始顺序"——那一列本来就是"加入的次序"，
        /// 这样"我排乱了怎么回去"在表头上就有个入口，不用去翻右键菜单。
        /// </para>
        /// <para>
        /// 状态和右键菜单是同一份（<see cref="ApplySort"/> 会把菜单与表头一起刷），
        /// 所以两边不会各说各的。
        /// </para>
        /// </summary>
        private void OnPlaylistColumnClick(object? sender, ColumnClickEventArgs e)
        {
            // 列的次序见 MainForm.Designer.cs：# / 标题 / 时长
            switch (e.Column)
            {
                case 0:
                    ApplySort(PlaylistSortField.AddedOrder, false);
                    return;

                case 1:
                case 2:
                    var field = e.Column == 1 ? PlaylistSortField.Name : PlaylistSortField.Duration;

                    if (_sortField != field) ApplySort(field, descending: false);           // 换一列：先升序
                    else if (!_sortDescending) ApplySort(field, descending: true);          // 同一列第二下：反向
                    else ApplySort(PlaylistSortField.AddedOrder, false);                    // 第三下：回原始顺序
                    return;
            }
        }

        private void ApplySortMenu()
        {
            UpdateSortItem(cmsSortName, PlaylistSortField.Name, "按名称");
            UpdateSortItem(cmsSortPath, PlaylistSortField.Path, "按路径");
            UpdateSortItem(cmsSortDuration, PlaylistSortField.Duration, "按时长");
            cmsSortOriginal.Checked = _sortField == PlaylistSortField.AddedOrder;

            UpdateSortHeaders();
        }

        /// <summary>
        /// 给冒烟测试用的入口：等价于"点了第 <paramref name="column"/> 个表头"。
        /// <para>
        /// 表头的点击没法在测试里合成（要真的把鼠标点到表头控件上），所以走同一个处理方法，
        /// 保证测的就是真实那条路。
        /// </para>
        /// </summary>
        internal void ClickPlaylistColumn(int column) =>
            OnPlaylistColumnClick(null, new ColumnClickEventArgs(column));

        /// <summary>
        /// 表头文字带上当前的排序方向（"标题 ↑"）。
        /// <para>
        /// 表头是原生绘制的，改文字就够了；列宽是 <see cref="AdjustPlaylistColumns"/> 显式设的，
        /// 不受文字长度影响，所以加个箭头不会把列挤变形。
        /// </para>
        /// </summary>
        private void UpdateSortHeaders()
        {
            colTitle.Text = _sortField == PlaylistSortField.Name
                ? $"标题{(_sortDescending ? " ↓" : " ↑")}"
                : "标题";

            colDuration.Text = _sortField == PlaylistSortField.Duration
                ? $"时长{(_sortDescending ? " ↓" : " ↑")}"
                : "时长";
        }

        private void UpdateSortItem(ToolStripMenuItem item, PlaylistSortField field, string caption)
        {
            var active = _sortField == field;
            item.Checked = active;
            item.Text = active ? $"{caption} {( _sortDescending ? "↓" : "↑")}" : caption;
        }

        private static string SortFieldName(PlaylistSortField field) => field switch
        {
            PlaylistSortField.Name => "名称",
            PlaylistSortField.Path => "路径",
            PlaylistSortField.Duration => "时长",
            _ => "原始顺序"
        };

        // -----------------------------------------------------------------
        // 拖动换位
        // -----------------------------------------------------------------

        private void OnPlaylistItemDrag(object? sender, ItemDragEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // 筛选状态下界面行与模型索引不是连续对应的，拖动容易产生歧义，直接禁用。
            if (_playlistFilter.Length > 0)
            {
                SetStatus("筛选状态下不能拖动排序，请先清空搜索框");
                return;
            }

            if (listViewPlaylist.SelectedItems.Count != 1) return;
            if (e.Item is not ListViewItem row) return;

            _draggingRow = true;
            try
            {
                listViewPlaylist.DoDragDrop(row, DragDropEffects.Move);
            }
            finally
            {
                _draggingRow = false;
                listViewPlaylist.InsertionMark.Index = -1;
            }
        }

        private void OnPlaylistDragOver(object? sender, DragEventArgs e)
        {
            if (!_draggingRow) return;

            var point = listViewPlaylist.PointToClient(new Point(e.X, e.Y));
            var target = listViewPlaylist.InsertionMark.NearestIndex(point);

            if (target > -1)
            {
                var bounds = listViewPlaylist.GetItemRect(target);
                listViewPlaylist.InsertionMark.AppearsAfterItem =
                    point.X > bounds.Left + bounds.Width / 2;
                listViewPlaylist.InsertionMark.Index = target;
            }

            e.Effect = DragDropEffects.Move;
        }

        private void OnPlaylistReorderDrop(object? sender, DragEventArgs e)
        {
            if (!_draggingRow) return;

            var insertAt = listViewPlaylist.InsertionMark.Index;
            var after = listViewPlaylist.InsertionMark.AppearsAfterItem;
            listViewPlaylist.InsertionMark.Index = -1;

            if (insertAt < 0) return;

            if (after) insertAt++;

            var selected = GetSelectedPlaylistIndices();
            if (selected.Count != 1) return;

            var from = selected[0];
            var to = insertAt > from ? insertAt - 1 : insertAt;
            to = Math.Clamp(to, 0, _playlist.Count - 1);

            if (to == from) return;

            // 拖动换位之后"原始顺序"已经不存在，标记一下让排序菜单不误导。
            ResetSortToAddedOrder();

            _playlist.Move(from, to);
            SelectPlaylistIndex(to);

            SetStatus("已调整顺序");
        }
    }
}
