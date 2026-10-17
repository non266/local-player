using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

// 从外面拖 m3u / m3u8 进来导入。

namespace 播放器.Ui
{
    internal sealed partial class PlaylistLibraryDialog
    {
        // ---- 拖入 m3u 导入 -----------------------------------------------------

        private static bool HasPlaylistFiles(IDataObject? data)
        {
            if (data?.GetDataPresent(DataFormats.FileDrop) != true) return false;

            if (data.GetData(DataFormats.FileDrop) is not string[] files) return false;

            return files.Any(IsPlaylistFile);
        }

        private static bool IsPlaylistFile(string path)
        {
            var extension = Path.GetExtension(path);

            return string.Equals(extension, ".m3u", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(extension, ".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            // 拖自己的一行（调整顺序）不是"拖文件进来"，别把它判成 None
            if (_draggingRow || e.Data?.GetDataPresent(typeof(ListViewItem)) == true)
            {
                e.Effect = DragDropEffects.Move;
                return;
            }

            e.Effect = HasPlaylistFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            if (_draggingRow || e.Data?.GetDataPresent(typeof(ListViewItem)) == true) return;

            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;

            var imported = new List<string>();
            var problems = new List<string>();

            foreach (var file in files.Where(IsPlaylistFile))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var overwrite = false;

                if (PlaylistLibrary.Exists(name))
                {
                    var answer = MessageBox.Show(
                        this,
                        $"歌单库里已经有一个叫「{name}」的歌单了。\n\n用拖进来的这个文件覆盖它吗？",
                        "导入歌单",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (answer != DialogResult.Yes)
                    {
                        problems.Add($"{Path.GetFileName(file)}：已跳过（同名歌单保留原样）");
                        continue;
                    }

                    overwrite = true;
                }

                if (PlaylistLibrary.Import(file, overwrite, out var importedName, out var error))
                    imported.Add(importedName);
                else
                    problems.Add($"{Path.GetFileName(file)}：{error}");
            }

            if (problems.Count > 0)
            {
                MessageBox.Show(
                    this,
                    string.Join(Environment.NewLine, problems),
                    "导入歌单",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            if (imported.Count == 0) return;

            _result.Changed = true;
            Reload(imported[imported.Count - 1]);

            _note = $"已导入 {imported.Count} 个歌单：{string.Join(" / ", imported)}";
            UpdateDetail();
        }
    }
}
