using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using 播放器.Core;

// 批量重命名：先验全部名字再动文件，改到一半最难受。

namespace 播放器.Ui
{
    internal sealed partial class PlaylistLibraryDialog
    {
        // ---- 批量重命名 ---------------------------------------------------------

        /// <summary>
        /// 批量重命名选中的歌单：按<b>列表显示顺序</b>依次改成「基名 1」「基名 2」…
        /// <para>
        /// <b>先把所有新名字验一遍，再动文件</b>：任何一个不合法、或者撞上别的歌单，
        /// 就一个文件都不动——批量操作改到一半最难受（手上几个改了一半的名字，
        /// 还得自己去分辨哪几个是新的）。
        /// </para>
        /// <para><paramref name="rawBaseName"/> 为 <c>null</c> 时弹输入框（冒烟测试直接传基名）。</para>
        /// </summary>
        /// <returns>真的改了几个。</returns>
        internal int BatchRename(string? rawBaseName) => BatchRename(rawBaseName, interactive: true);

        /// <summary>
        /// 同上；<paramref name="interactive"/> 为 false 时<b>一个对话框都不弹</b>
        /// （理由写进窗口下方那行字里），供冒烟测试驱动——模态框会把测试挂死。
        /// </summary>
        internal int BatchRename(string? rawBaseName, bool interactive)
        {
            var selected = SelectedPlaylists();

            // 被挡住时统一走这里：该弹框的弹框，不弹框时也要把理由留在窗口里
            int Reject(string message, string title)
            {
                if (interactive)
                {
                    MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                _note = message.Replace(Environment.NewLine, " ");
                UpdateDetail();
                return 0;
            }

            if (selected.Count < 2)
            {
                if (interactive && rawBaseName == null)
                {
                    MessageBox.Show(
                        this,
                        "批量重命名要先选中两份以上；只改一份直接点「重命名…」就行。",
                        "批量重命名歌单",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return 0;
            }

            var baseName = rawBaseName;

            if (baseName == null)
            {
                baseName = InputDialog.Show(
                    this,
                    "批量重命名歌单",
                    $"新名字的基名（这 {selected.Count} 份会按这里的顺序变成「基名 1」「基名 2」…）：",
                    "歌单");

                if (baseName == null) return 0;
            }

            // ---- ① 先把全部目标名字算出来并验一遍 ----
            var plans = new List<(string Old, string New)>();

            for (var i = 0; i < selected.Count; i++)
            {
                if (!PlaylistLibrary.TryNormalizeName($"{baseName} {i + 1}", out var clean, out var error))
                    return Reject(error, "批量重命名歌单失败");

                plans.Add((selected[i].Name, clean));
            }

            foreach (var plan in plans)
            {
                // 自己的名字没变（"基名 1"正好就是它现在叫的）：放行，不用动文件
                if (string.Equals(plan.Old, plan.New, StringComparison.CurrentCultureIgnoreCase)) continue;

                if (PlaylistLibrary.Exists(plan.New))
                {
                    return Reject(
                        $"已经有一个叫「{plan.New}」的歌单了，这次一个文件都没动。"
                        + Environment.NewLine + Environment.NewLine
                        + "换个基名，或者先把那份改名 / 删掉再试。",
                        "批量重命名歌单失败");
                }
            }

            var duplicate = plans
                .GroupBy(plan => plan.New, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicate != null)
                return Reject($"算出来的名字里有重复的「{duplicate.Key}」，这次一个文件都没动。", "批量重命名歌单失败");

            // ---- ② 验完了才动文件 ----
            var renamed = new List<string>();
            var failures = new List<string>();

            foreach (var plan in plans)
            {
                if (string.Equals(plan.Old, plan.New, StringComparison.CurrentCultureIgnoreCase))
                {
                    renamed.Add(plan.New);
                    continue;
                }

                if (!PlaylistLibrary.Rename(plan.Old, plan.New, out var actual, out var error))
                {
                    failures.Add($"「{plan.Old}」：{error}");
                    continue;
                }

                if (IsCurrent(plan.Old)) _result.CurrentName = actual;

                renamed.Add(actual);
            }

            if (renamed.Count == 0)
                return Reject(string.Join(Environment.NewLine, failures), "批量重命名歌单失败");

            _result.Changed = true;
            Reload(renamed[renamed.Count - 1]);

            _note = $"已重命名 {renamed.Count} 个：{string.Join(" / ", renamed)}。";

            if (failures.Count > 0)
            {
                _note += $" 另有 {failures.Count} 个没改成：{string.Join("；", failures)}";

                if (interactive)
                {
                    MessageBox.Show(
                        this,
                        string.Join(Environment.NewLine, failures),
                        "部分歌单没改成",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }

            UpdateDetail();
            return renamed.Count;
        }
    }
}
