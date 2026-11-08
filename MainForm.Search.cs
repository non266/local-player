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
    /// <para>
    /// 状态（筛选词、排序依据、列表与队列）都在 <see cref="PlaylistWorkspace"/> 里；
    /// 这里只负责菜单 / 表头 / 文本框与状态栏文案。
    /// </para>
    /// </summary>
    public partial class MainForm
    {
        private bool _draggingRow;

        // -----------------------------------------------------------------
        // 搜索过滤
        // -----------------------------------------------------------------

        /// <summary>应用搜索关键字；空格分隔的多个词之间是"与"的关系。</summary>
        private void ApplyPlaylistFilter(string? text)
        {
            var filter = (text ?? string.Empty).Trim();

            if (string.Equals(filter, _workspace.Filter, StringComparison.Ordinal)) return;

            _workspace.SetFilter(filter);
            RefreshPlaylistView();

            if (filter.Length == 0)
                SetStatus($"已清除筛选，共 {_playlist.Count} 项");
            else
                SetStatus($"筛选「{filter}」，匹配 {listViewPlaylist.Items.Count}/{_playlist.Count} 项");
        }

        /// <summary>给冒烟测试 / 大列表探针用的入口：等价于在搜索框里输入这段文字。</summary>
        internal void ApplyPlaylistFilterForTest(string text) => ApplyPlaylistFilter(text);

        private void ClearPlaylistFilter()
        {
            if (_workspace.Filter.Length == 0) return;

            if (txtPlaylistSearch.Text.Length > 0)
            {
                txtPlaylistSearch.Clear();   // 会触发 TextChanged 走一遍刷新
            }
            else
            {
                ApplyPlaylistFilter(string.Empty);
            }
        }

        /// <summary>条目是否命中当前筛选条件（实现见 <see cref="PlaylistWorkspace.Matches"/>）。</summary>
        private bool MatchesFilter(PlaylistItem item) => _workspace.Matches(item);

        // -----------------------------------------------------------------
        // 排序
        // -----------------------------------------------------------------

        private void ApplySort(PlaylistSortField field, bool descending)
        {
            _workspace.ApplySort(field, descending);
            RefreshPlaylistView();
            ApplySortMenu();

            var name = SortFieldName(field);
            SetStatus(field == PlaylistSortField.AddedOrder
                ? "已恢复原始顺序"
                : $"已按{name}{(_workspace.SortDescending ? "降序" : "升序")}排列");
        }

        /// <summary>
        /// 把"排序依据"退回原始顺序，<b>但不重排列表</b>（手工拖动 / 载入歌单之后用）。
        /// </summary>
        private void ResetSortToAddedOrder()
        {
            _workspace.ResetSortToAddedOrder();
            ApplySortMenu();
        }

        /// <summary>点击排序项：已在该字段上则切换升降序，否则按升序排。</summary>
        private void ToggleSort(PlaylistSortField field)
        {
            var descending = _workspace.SortField == field && !_workspace.SortDescending;
            ApplySort(field, descending);
        }

        /// <summary>
        /// 点列表表头排序。<b>三下走一圈</b>：升序 → 降序 → 回原始顺序。
        /// <para>第 0 列是序号，点它直接"恢复原始顺序"。</para>
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

                    if (_workspace.SortField != field) ApplySort(field, descending: false);          // 换一列：先升序
                    else if (!_workspace.SortDescending) ApplySort(field, descending: true);         // 同一列第二下：反向
                    else ApplySort(PlaylistSortField.AddedOrder, false);                             // 第三下：回原始顺序
                    return;
            }
        }

        private void ApplySortMenu()
        {
            UpdateSortItem(cmsSortName, PlaylistSortField.Name, "按名称");
            UpdateSortItem(cmsSortPath, PlaylistSortField.Path, "按路径");
            UpdateSortItem(cmsSortDuration, PlaylistSortField.Duration, "按时长");
            cmsSortOriginal.Checked = _workspace.SortField == PlaylistSortField.AddedOrder;

            UpdateSortHeaders();
        }

        /// <summary>
        /// 给冒烟测试用的入口：等价于"点了第 <paramref name="column"/> 个表头"。
        /// </summary>
        internal void ClickPlaylistColumn(int column) =>
            OnPlaylistColumnClick(null, new ColumnClickEventArgs(column));

        /// <summary>表头文字带上当前的排序方向（"标题 ↑"）。</summary>
        private void UpdateSortHeaders()
        {
            colTitle.Text = _workspace.SortField == PlaylistSortField.Name
                ? $"标题{(_workspace.SortDescending ? " ↓" : " ↑")}"
                : "标题";

            colDuration.Text = _workspace.SortField == PlaylistSortField.Duration
                ? $"时长{(_workspace.SortDescending ? " ↓" : " ↑")}"
                : "时长";
        }

        private void UpdateSortItem(ToolStripMenuItem item, PlaylistSortField field, string caption)
        {
            var active = _workspace.SortField == field;
            item.Checked = active;
            item.Text = active ? $"{caption} {(_workspace.SortDescending ? "↓" : "↑")}" : caption;
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
            if (_workspace.Filter.Length > 0)
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
