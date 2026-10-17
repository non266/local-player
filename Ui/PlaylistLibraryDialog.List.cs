using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

// 列表：重新载入、选中项、多选按钮状态与下面那行详情。

namespace 播放器.Ui
{
    internal sealed partial class PlaylistLibraryDialog
    {
        // ---- 列表 -------------------------------------------------------------

        private void Reload(string? selectName = null)
        {
            _playlists = PlaylistLibrary.List().ToList();

            var previous = selectName ?? SelectedPlaylist()?.Name ?? _result.CurrentName;

            _list.BeginUpdate();

            try
            {
                _list.Items.Clear();

                foreach (var playlist in _playlists)
                {
                    var isCurrent = IsCurrent(playlist.Name);

                    // 当前歌单加"●"并加粗：一眼知道现在列表对应的是哪一份
                    var row = new ListViewItem(isCurrent ? "● " + playlist.Name : playlist.Name)
                    {
                        Tag = playlist,
                        ToolTipText = playlist.FilePath,
                        Font = isCurrent ? _currentFont : UiFonts.Sidebar
                    };

                    row.SubItems.Add(playlist.TrackCount < 0 ? "?" : playlist.TrackCount.ToString());
                    row.SubItems.Add(playlist.ModifiedLocal.ToString("yyyy-MM-dd HH:mm"));

                    _list.Items.Add(row);
                }
            }
            finally
            {
                _list.EndUpdate();
            }

            _note = null;

            var wanted = -1;

            if (previous != null)
            {
                for (var i = 0; i < _playlists.Count; i++)
                {
                    if (!string.Equals(_playlists[i].Name, previous, StringComparison.CurrentCultureIgnoreCase))
                        continue;

                    wanted = i;
                    break;
                }
            }

            if (wanted < 0 && _list.Items.Count > 0) wanted = 0;

            if (wanted >= 0) _list.Items[wanted].Selected = true;

            OnSelectionChanged();
        }

        private SavedPlaylist? SelectedPlaylist() =>
            _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as SavedPlaylist : null;

        /// <summary>选中的那几份（按列表顺序）；多选时只有"删除"用得上。</summary>
        private List<SavedPlaylist> SelectedPlaylists()
        {
            var selected = new List<SavedPlaylist>();

            foreach (ListViewItem row in _list.SelectedItems)
            {
                if (row.Tag is SavedPlaylist playlist) selected.Add(playlist);
            }

            return selected;
        }

        private bool IsCurrent(string name) =>
            _result.CurrentName != null &&
            string.Equals(name, _result.CurrentName, StringComparison.CurrentCultureIgnoreCase);

        private void OnSelectionChanged()
        {
            _note = null;

            var count = _list.SelectedItems.Count;

            // 载入 / 追加 / 重命名 针对"一份歌单"：选了两份以上就点不动（点哪一个都不明确）
            _loadButton.Enabled = count == 1;
            _appendButton.Enabled = count == 1;
            _renameButton.Enabled = count == 1;

            // 批量重命名要的就是"选了好几份"：一份直接点「重命名…」就够了
            _batchButton.Enabled = count > 1;

            // 删除支持多选：选中几个就删几个，按钮上直接写出个数
            _deleteButton.Enabled = count > 0;
            _deleteButton.Text = count > 1 ? $"删除选中的 {count} 个" : "删除";

            UpdateDetail();
        }

        private void UpdateDetail()
        {
            if (_note != null)
            {
                _detail.Text = _note;
                return;
            }

            var selected = SelectedPlaylist();

            if (selected == null)
            {
                _detail.Text = _playlists.Count == 0
                    ? "歌单库里还没有歌单。先回主窗口保存一个，或者把 m3u 文件拖进来。"
                    : "歌单文件夹：" + PlaylistLibrary.Directory;
                return;
            }

            var count = selected.TrackCount < 0 ? "读不出条目数" : $"{selected.TrackCount} 项";

            _detail.Text = $"{selected.Name} · {count} · {selected.FilePath}"
                           + (IsCurrent(selected.Name) ? "　（当前列表对应的歌单）" : string.Empty);
        }
    }
}
