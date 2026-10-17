using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

// 拖动换位：插位换算（纯函数）+ 挪完立即写顺序文件。

namespace 播放器.Ui
{
    internal sealed partial class PlaylistLibraryDialog
    {
        // ---- 拖动换位（调整歌单顺序） -------------------------------------------

        private void OnRowDrag(object? sender, ItemDragEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            // 和主列表一样只拖"一行"：多选时拖哪一行都不明确（多选是给删除 / 批量重命名用的）
            if (_list.SelectedItems.Count != 1) return;
            if (e.Item is not ListViewItem row) return;

            _draggingRow = true;

            try
            {
                _list.DoDragDrop(row, DragDropEffects.Move);
            }
            finally
            {
                _draggingRow = false;
                _list.InsertionMark.Index = -1;
            }
        }

        private void OnRowsDragOver(object? sender, DragEventArgs e)
        {
            if (!_draggingRow) return;      // 从外面拖文件进来时走导入那条路

            var point = _list.PointToClient(new Point(e.X, e.Y));
            var target = _list.InsertionMark.NearestIndex(point);

            if (target > -1)
            {
                var bounds = _list.GetItemRect(target);

                _list.InsertionMark.AppearsAfterItem = point.Y > bounds.Top + bounds.Height / 2;
                _list.InsertionMark.Index = target;
            }

            e.Effect = DragDropEffects.Move;
        }

        private void OnRowsDragDrop(object? sender, DragEventArgs e)
        {
            if (!_draggingRow) return;      // 同上

            var insertAt = _list.InsertionMark.Index;
            var after = _list.InsertionMark.AppearsAfterItem;
            _list.InsertionMark.Index = -1;

            if (insertAt < 0 || _list.SelectedIndices.Count != 1) return;

            MoveSelectedTo(TargetIndex(_list.SelectedIndices[0], insertAt, after));
        }

        /// <summary>
        /// 拖放的"插到哪儿"换成"挪完之后的最终下标"。
        /// <para>
        /// 插到自己后面时要减一：把自己从列表里摘掉之后，后面的下标会整体前移一位。
        /// 主列表的拖动换位是同一个算术（那里写在 <c>OnPlaylistReorderDrop</c> 里）；
        /// 这里抽成纯函数是为了能直接断言——差一位这种错在界面上很难看出来。
        /// </para>
        /// </summary>
        internal static int TargetIndex(int from, int insertAt, bool after)
        {
            var target = after ? insertAt + 1 : insertAt;

            if (target > from) target--;

            return Math.Max(0, target);
        }

        /// <summary>
        /// 把选中的那一行挪到第 <paramref name="targetIndex"/> 位，<b>顺序立即写进顺序文件</b>
        /// （这个窗口的语义就是"直接动文件"，拖完不落盘等于白拖）。
        /// </summary>
        internal bool MoveSelectedTo(int targetIndex) => MoveSelectedTo(targetIndex, interactive: true);

        /// <summary>
        /// 同上；<paramref name="interactive"/> 为 false 时失败也不弹框（理由写进窗口里那行字），
        /// 供冒烟测试驱动——模态框会把测试挂死。
        /// </summary>
        internal bool MoveSelectedTo(int targetIndex, bool interactive)
        {
            var selected = SelectedPlaylist();
            if (selected == null) return false;

            if (!PlaylistLibrary.Move(selected.Name, targetIndex, out var error))
            {
                if (interactive)
                    MessageBox.Show(this, error, "调整歌单顺序失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                _note = error;
                UpdateDetail();
                return false;
            }

            _result.Changed = true;
            Reload(selected.Name);

            _note = $"已把「{selected.Name}」挪到第 {targetIndex + 1} 位"
                    + $"（顺序记在 {PlaylistLibrary.OrderFileName} 里，删掉它就回到按名字排）。";

            UpdateDetail();
            return true;
        }
    }
}
