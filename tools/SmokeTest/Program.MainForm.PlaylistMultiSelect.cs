// 播放列表多选（1.31.0）：批量移除 / 下一首 / 复制 / Ctrl+A / 单项移动。
//
// 这一组挡的是"代码已经按多项写、入口却没开多选"的缺口：以前 RemoveMany /
// QueueSelectedForNext / CopySelectedPaths 都支持多项，但 listViewPlaylist.MultiSelect
// 是 false，用户用 Ctrl / Shift 根本选不中第二项。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool TestPlaylistMultiSelect()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-playlist-multi");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            var paths = Enumerable.Range(1, 6)
                .Select(i => Path.Combine(root, $"multi-{i}.wav"))
                .ToArray();

            for (var i = 0; i < paths.Length; i++)
                WriteWav(paths[i], seconds: 10, frequency: 440 + i * 20);

            try
            {
                using var form = new 播放器.MainForm(paths);
                form.Show();
                PumpMessages(500);

                var list = Find(form, "listViewPlaylist") as ListView;

                if (list == null)
                {
                    Log(39, "播放列表多选：找不到 listViewPlaylist");
                    return false;
                }

                if (!list.MultiSelect)
                {
                    Log(39, "播放列表多选：列表没有开 MultiSelect，Ctrl / Shift 选不中第二项");
                    return false;
                }

                if (list.Items.Count != paths.Length)
                {
                    Log(39, $"播放列表多选：列表有 {list.Items.Count} 项，期望 {paths.Length}");
                    return false;
                }

                SelectRows(list, 1, 3);
                PumpMessages(120);

                if (!form.SelectedPlaylistIndicesForTest.SequenceEqual(new[] { 1, 3 }))
                {
                    Log(39, "播放列表多选：选中两行之后取到的模型索引不是 [1,3]（现在 ["
                            + string.Join(",", form.SelectedPlaylistIndicesForTest) + "]）");
                    return false;
                }

                form.UpdatePlaylistSelectionMenuForTest();

                var menu = list.ContextMenuStrip;
                var remove = MenuItem(menu, "cmsRemove");
                var copy = MenuItem(menu, "cmsCopyPath");
                var reveal = MenuItem(menu, "cmsReveal");
                var moveUp = MenuItem(menu, "cmsMoveUp");
                var playNext = MenuItem(menu, "cmsPlayNext");

                if (remove == null || copy == null || reveal == null || moveUp == null || playNext == null)
                {
                    Log(39, "播放列表多选：右键菜单里缺 cmsRemove / cmsCopyPath / cmsReveal / cmsMoveUp / cmsPlayNext");
                    return false;
                }

                if (!remove.Text.Contains("2 项", StringComparison.Ordinal)
                    || !copy.Text.Contains("2", StringComparison.Ordinal)
                    || !reveal.Text.Contains("第一项", StringComparison.Ordinal)
                    || !playNext.Text.Contains("2 项", StringComparison.Ordinal))
                {
                    Log(39, "播放列表多选：选中 2 项时右键菜单没有写明数量（现在「"
                            + remove.Text + "」/「" + copy.Text + "」/「" + reveal.Text + "」/「"
                            + playNext.Text + "」）");
                    return false;
                }

                if (!moveUp.Enabled || !moveUp.Text.Contains("2 项", StringComparison.Ordinal))
                {
                    Log(39, "播放列表多选：选中 2 项时「上移」该可点并写明 2 项（现在「"
                            + moveUp.Text + "」，可点=" + moveUp.Enabled + "）");
                    return false;
                }

                var copied = form.SelectedPathsTextForTest()
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.None);

                if (copied.Length != 2 || copied[0] != paths[1] || copied[1] != paths[3])
                {
                    Log(39, $"播放列表多选：复制文本不是两行、按列表顺序（现在 {copied.Length} 行）");
                    return false;
                }

                // Ctrl+A 只选当前看得见的行；先验全部，再验筛选后。
                form.HandlePlaylistKeyForTest(Keys.A, control: true);
                PumpMessages(120);

                if (form.SelectedPlaylistIndicesForTest.Count != paths.Length)
                {
                    Log(39, $"播放列表多选：Ctrl+A 选中 {form.SelectedPlaylistIndicesForTest.Count} 项，"
                            + $"期望 {paths.Length}");
                    return false;
                }

                var search = Find(form, "txtPlaylistSearch") as TextBox;

                if (search == null)
                {
                    Log(39, "播放列表多选：找不到 txtPlaylistSearch");
                    return false;
                }

                search.Text = "multi-2";
                PumpMessages(300);
                form.HandlePlaylistKeyForTest(Keys.A, control: true);
                PumpMessages(120);

                if (form.SelectedPlaylistIndicesForTest.Count != 1)
                {
                    Log(39, "播放列表多选：筛选到 1 行之后 Ctrl+A 选中了 "
                            + form.SelectedPlaylistIndicesForTest.Count + " 项（会把被筛掉的也带进批量删除）");
                    return false;
                }

                search.Clear();
                PumpMessages(300);

                // 批量入队：两条都进，顺序按列表。
                SelectRows(list, 1, 3);
                PumpMessages(120);

                if (!form.SelectedPlaylistIndicesForTest.SequenceEqual(new[] { 1, 3 }))
                {
                    Log(39, "播放列表多选：清筛选后重新选两行，取到的模型索引不是 [1,3]（现在 ["
                            + string.Join(",", form.SelectedPlaylistIndicesForTest) + "]）");
                    return false;
                }

                playNext.PerformClick();
                PumpMessages(50);

                if (form.Queue.Count != 2)
                {
                    Log(39, $"播放列表多选：批量「下一首播放」队列里有 {form.Queue.Count} 条，期望 2");
                    return false;
                }

                form.MenuClearQueue?.PerformClick();
                PumpMessages(200);

                // 批量移除：6 → 4，删完不能残留旧索引对应的选中项。
                SelectRows(list, 1, 3);
                PumpMessages(120);
                remove.PerformClick();
                PumpMessages(300);

                if (list.Items.Count != 4)
                {
                    Log(39, $"播放列表多选：批量移除后剩 {list.Items.Count} 项，期望 4");
                    return false;
                }

                if (list.SelectedIndices.Count != 0)
                {
                    Log(39, "播放列表多选：批量移除后还残留着选中项（重建按旧索引选到了别的行）");
                    return false;
                }

                if (!form.StatusText.Contains("移除 2 项", StringComparison.Ordinal))
                {
                    Log(39, "播放列表多选：批量移除后状态栏没说清移除了几项（现在「"
                            + form.StatusText + "」）");
                    return false;
                }

                var remaining = PlaylistOf(form).GetPaths();

                if (!remaining.SequenceEqual(new[] { paths[0], paths[2], paths[4], paths[5] }))
                {
                    Log(39, "播放列表多选：批量移除删错了条目（现在 ["
                            + string.Join(",", remaining.Select(Path.GetFileName)) + "]）");
                    return false;
                }

                // 单选路径不变：一项时上移可用，移除 1 项也只报 1 项。
                // 先验边界：第一项不能再上移（1.32.0 起菜单按"块能不能动"置灰）。
                SelectRows(list, 0);
                PumpMessages(120);
                form.UpdatePlaylistSelectionMenuForTest();

                if (moveUp.Enabled)
                {
                    Log(39, "播放列表多选：第一项时「上移」不该可点");
                    return false;
                }

                SelectRows(list, 1);
                PumpMessages(120);
                form.UpdatePlaylistSelectionMenuForTest();

                if (!moveUp.Enabled)
                {
                    Log(39, "播放列表多选：只选一项时「上移」反而不可用了");
                    return false;
                }

                remove.PerformClick();
                PumpMessages(250);

                if (list.Items.Count != 3 || !form.StatusText.Contains("移除 1 项", StringComparison.Ordinal))
                {
                    Log(39, $"播放列表多选：单选移除不对（剩 {list.Items.Count} 项，状态「"
                            + form.StatusText + "」）");
                    return false;
                }

                form.Close();
                PumpMessages(200);

                Log(39, "播放列表多选正常：Ctrl+A / 批量移除 / 下一首 / 复制 / 单项移动都按预期");
                return true;
            }
            catch (Exception ex)
            {
                Log(39, "播放列表多选检查失败: " + ex.Message);
                return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        private static void SelectRows(ListView list, params int[] indexes)
        {
            list.SelectedIndices.Clear();

            foreach (var index in indexes)
            {
                if (index < 0 || index >= list.Items.Count) continue;

                list.Items[index].Selected = true;
                list.Items[index].Focused = true;
            }

            if (indexes.Length > 0 && indexes[^1] < list.Items.Count)
                list.Items[indexes[^1]].EnsureVisible();
        }

        private static ToolStripMenuItem? MenuItem(ContextMenuStrip? menu, string name)
        {
            return menu?.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        }
    }
}
