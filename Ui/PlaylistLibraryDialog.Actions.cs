using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

// 单份操作：载入 / 追加 / 新建 / 改名 / 删除 / 打开文件夹。

namespace 播放器.Ui
{
    internal sealed partial class PlaylistLibraryDialog
    {
        // ---- 操作 -------------------------------------------------------------

        internal void LoadSelected()
        {
            var selected = SelectedPlaylist();
            if (selected == null) return;

            _result.LoadName = selected.Name;
            _result.Append = false;
            _result.NewName = null;      // 选了别的歌单，之前新建的那份就不切过去了

            DialogResult = DialogResult.OK;
            Close();
        }

        private void AppendSelected()
        {
            var selected = SelectedPlaylist();
            if (selected == null) return;

            _result.LoadName = selected.Name;
            _result.Append = true;
            _result.NewName = null;

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>
        /// 新建一个空歌单。文件在这里就建好（这本来就是"新建"该做的事），
        /// 至于要不要把当前播放列表切过去，等窗口关掉之后由主窗体问过用户再决定——
        /// 清空当前列表会打断正在放的东西，不该由一个"管理歌单"的窗口替用户决定。
        /// </summary>
        private void CreateNew()
        {
            var raw = InputDialog.Show(this, "新建歌单", "新歌单的名字：", SuggestName());
            if (raw == null) return;

            if (!PlaylistLibrary.TryNormalizeName(raw, out var name, out var error))
            {
                MessageBox.Show(this, error, "歌单名不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (PlaylistLibrary.Exists(name))
            {
                MessageBox.Show(
                    this,
                    $"已经有一个叫「{name}」的歌单了。",
                    "新建歌单失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            try
            {
                PlaylistLibrary.Create(name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "新建歌单失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _result.NewName = name;
            _result.Changed = true;

            Reload(name);

            _note = $"已新建空歌单「{name}」；关掉本窗口后会问你要不要切过去。";
            UpdateDetail();
        }

        /// <summary>新歌单的默认名：新歌单 / 新歌单 2 / …（避开已经用掉的名字）。</summary>
        private static string SuggestName()
        {
            const string stem = "新歌单";

            var candidate = stem;

            for (var suffix = 2; suffix <= 99 && PlaylistLibrary.Exists(candidate); suffix++)
                candidate = $"{stem} {suffix}";

            return candidate;
        }

        private void RenameSelected()
        {
            var selected = SelectedPlaylist();
            if (selected == null) return;

            var raw = InputDialog.Show(this, "重命名歌单", "新的名字：", selected.Name);
            if (raw == null) return;

            if (!PlaylistLibrary.Rename(selected.Name, raw, out var actual, out var error))
            {
                MessageBox.Show(this, error, "重命名歌单失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (IsCurrent(selected.Name)) _result.CurrentName = actual;

            _result.Changed = true;
            Reload(actual);

            _note = $"「{selected.Name}」已改名为「{actual}」。";
            UpdateDetail();
        }

        /// <summary>
        /// 删除选中的歌单（支持多选）：<b>一次确认，逐个删</b>，删不掉的说清是哪一个、为什么。
        /// <para>返回真的删掉了几个。<paramref name="confirm"/> 为 false 时不弹确认框（给冒烟测试用）。</para>
        /// </summary>
        internal int DeleteSelected(bool confirm)
        {
            var selected = SelectedPlaylists();
            if (selected.Count == 0) return 0;

            if (confirm)
            {
                var what = selected.Count == 1
                    ? $"要删除歌单「{selected[0].Name}」吗？"
                    : $"要删除这 {selected.Count} 个歌单吗？\n\n"
                      + string.Join("\n", selected.Select(playlist => "· " + playlist.Name));

                var answer = MessageBox.Show(
                    this,
                    what + "\n\n（只删这些歌单文件，里面的歌曲、视频一个都不会动。）",
                    "删除歌单",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (answer != DialogResult.Yes) return 0;
            }

            var deleted = new List<string>();
            var failures = new List<string>();

            foreach (var playlist in selected)
            {
                if (PlaylistLibrary.Delete(playlist.Name, out var error)) deleted.Add(playlist.Name);
                else failures.Add($"「{playlist.Name}」：{error}");
            }

            if (deleted.Count == 0)
            {
                MessageBox.Show(this, string.Join("\n", failures), "删除歌单失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return 0;
            }

            // 删掉的正好是"当前歌单"时，主窗体的关联要跟着断开
            if (deleted.Any(IsCurrent)) _result.CurrentName = null;

            _result.Changed = true;
            Reload();

            _note = deleted.Count == 1
                ? $"已删除歌单「{deleted[0]}」。"
                : $"已删除 {deleted.Count} 个歌单：{string.Join("、", deleted)}。";

            if (failures.Count > 0)
            {
                _note += $" 另有 {failures.Count} 个没删掉：{string.Join("；", failures)}";

                MessageBox.Show(this, string.Join("\n", failures), "部分歌单没删掉",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            UpdateDetail();
            return deleted.Count;
        }

        private void OpenFolder()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"\"{PlaylistLibrary.EnsureDirectory()}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "打开歌单文件夹失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
