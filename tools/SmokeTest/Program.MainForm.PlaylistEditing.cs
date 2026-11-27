// 播放列表整批移动与撤销（1.32.0）：上移 / 下移 / 拖动按块走，Ctrl+Z 放回最近一次移除。
//
// 1.31.0 把 MultiSelect 打开了，但上移 / 下移 / 拖动仍只认一项；批量删除也没有撤销。
// 这一组钉住"块语义"（相对顺序不变、当前项按引用重算）与"撤销只保留一级、内容一变就作废"。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool TestPlaylistBatchEditing()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-playlist-editing");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            var paths = Enumerable.Range(1, 6)
                .Select(i => Path.Combine(root, $"edit-{i}.wav"))
                .ToArray();

            for (var i = 0; i < paths.Length; i++)
                WriteWav(paths[i], seconds: 10, frequency: 440 + i * 20);

            var extra = Path.Combine(root, "edit-7.wav");
            WriteWav(extra, seconds: 10, frequency: 560);

            try
            {
                if (!CheckBatchMovement(paths)) return false;
                if (!CheckUndoRemoval(paths, extra)) return false;
                if (!CheckPlaylistModel()) return false;

                Log(40, "播放列表整批移动与撤销正常：块移动 / 菜单数量 / 拖动 / 边界 / 撤销一级 / 内容变化失效");
                return true;
            }
            catch (Exception ex)
            {
                Log(40, "播放列表整批移动与撤销检查失败: " + ex.Message);
                return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

        /// <summary>整批上移 / 下移 / 拖动 / 边界 / 筛选。用第一个窗体，不碰撤销。</summary>
        private static bool CheckBatchMovement(string[] paths)
        {
            using var form = new 播放器.MainForm(paths);
            form.Show();
            PumpMessages(500);

            var list = Find(form, "listViewPlaylist") as ListView;

            if (list == null)
            {
                Log(40, "播放列表整批移动：找不到 listViewPlaylist");
                return false;
            }

            var menu = list.ContextMenuStrip;
            var moveUp = MenuItem(menu, "cmsMoveUp");
            var moveDown = MenuItem(menu, "cmsMoveDown");

            if (moveUp == null || moveDown == null)
            {
                Log(40, "播放列表整批移动：右键菜单里缺 cmsMoveUp / cmsMoveDown");
                return false;
            }

            SelectRows(list, 1, 3);
            PumpMessages(120);
            form.UpdatePlaylistSelectionMenuForTest();

            if (!moveUp.Enabled || !moveUp.Text.Contains("2 项", StringComparison.Ordinal)
                || !moveDown.Enabled || !moveDown.Text.Contains("2 项", StringComparison.Ordinal))
            {
                Log(40, "播放列表整批移动：选中 2 项时上移 / 下移没有写明数量或不可点（现在「"
                        + moveUp.Text + "」/「" + moveDown.Text + "」，上移可点=" + moveUp.Enabled + "）");
                return false;
            }

            moveUp.PerformClick();
            PumpMessages(250);

            var expected = new[] { paths[1], paths[3], paths[0], paths[2], paths[4], paths[5] };

            if (!PlaylistOf(form).GetPaths().SequenceEqual(expected))
            {
                Log(40, "播放列表整批移动：上移之后顺序不对（现在 ["
                        + string.Join(",", PlaylistOf(form).GetPaths().Select(Path.GetFileName)) + "]）");
                return false;
            }

            if (!form.SelectedPlaylistIndicesForTest.SequenceEqual(new[] { 0, 1 }))
            {
                Log(40, "播放列表整批移动：上移之后选中项没有跟着块（现在 ["
                        + string.Join(",", form.SelectedPlaylistIndicesForTest) + "]）");
                return false;
            }

            moveDown.PerformClick();
            PumpMessages(250);

            expected = new[] { paths[0], paths[1], paths[3], paths[2], paths[4], paths[5] };

            if (!PlaylistOf(form).GetPaths().SequenceEqual(expected))
            {
                Log(40, "播放列表整批移动：下移之后顺序不对（现在 ["
                        + string.Join(",", PlaylistOf(form).GetPaths().Select(Path.GetFileName)) + "]）");
                return false;
            }

            if (!form.SelectedPlaylistIndicesForTest.SequenceEqual(new[] { 1, 2 }))
            {
                Log(40, "播放列表整批移动：下移之后选中项没有跟着块（现在 ["
                        + string.Join(",", form.SelectedPlaylistIndicesForTest) + "]）");
                return false;
            }

            // 拖动入口：多选必须允许（真实鼠标拖动合成不出来，这里走同一个判定 + 落点逻辑）。
            SelectRows(list, 1, 3);
            PumpMessages(120);

            if (!form.CanReorderPlaylistForTest(out var reason) || reason != null)
            {
                Log(40, "播放列表整批移动：多选时拖动入口被拒绝了（原因「" + (reason ?? "（空）") + "」）");
                return false;
            }

            SelectRows(list, 0, 1);
            PumpMessages(120);
            form.ReorderPlaylistForTest(6);
            PumpMessages(250);

            expected = new[] { paths[3], paths[2], paths[4], paths[5], paths[0], paths[1] };

            if (!PlaylistOf(form).GetPaths().SequenceEqual(expected))
            {
                Log(40, "播放列表整批移动：拖到末尾之后顺序不对（现在 ["
                        + string.Join(",", PlaylistOf(form).GetPaths().Select(Path.GetFileName)) + "]）");
                return false;
            }

            if (!form.SelectedPlaylistIndicesForTest.SequenceEqual(new[] { 4, 5 }))
            {
                Log(40, "播放列表整批移动：拖到末尾之后选中项没有跟着块（现在 ["
                        + string.Join(",", form.SelectedPlaylistIndicesForTest) + "]）");
                return false;
            }

            // 边界：第一项不能再上移，最后一项不能再下移。
            SelectRows(list, 0);
            PumpMessages(120);
            form.UpdatePlaylistSelectionMenuForTest();

            if (moveUp.Enabled)
            {
                Log(40, "播放列表整批移动：第一项时「上移」不该可点");
                return false;
            }

            SelectRows(list, list.Items.Count - 1);
            PumpMessages(120);
            form.UpdatePlaylistSelectionMenuForTest();

            if (moveDown.Enabled)
            {
                Log(40, "播放列表整批移动：最后一项时「下移」不该可点");
                return false;
            }

            // 筛选 + 多选：整批移动禁用，并给原因；单选仍可用。
            var search = Find(form, "txtPlaylistSearch") as TextBox;

            if (search == null)
            {
                Log(40, "播放列表整批移动：找不到 txtPlaylistSearch");
                return false;
            }

            search.Text = "edit";
            PumpMessages(300);
            SelectRows(list, 0, 1);
            PumpMessages(120);
            form.UpdatePlaylistSelectionMenuForTest();

            if (moveUp.Enabled || moveDown.Enabled)
            {
                Log(40, "播放列表整批移动：筛选状态下多选时上移 / 下移不该可点");
                return false;
            }

            var before = PlaylistOf(form).GetPaths();
            form.MovePlaylistSelectionForTest(-1);
            PumpMessages(120);

            if (!PlaylistOf(form).GetPaths().SequenceEqual(before))
            {
                Log(40, "播放列表整批移动：筛选状态下多选上移竟然改了顺序");
                return false;
            }

            if (!form.StatusText.Contains("筛选状态下不能整批移动", StringComparison.Ordinal))
            {
                Log(40, "播放列表整批移动：筛选状态下多选上移没有说明原因（现在「"
                        + form.StatusText + "」）");
                return false;
            }

            form.Close();
            PumpMessages(200);
            return true;
        }

        /// <summary>撤销：一级、原位置、选中恢复、内容一变就作废。</summary>
        private static bool CheckUndoRemoval(string[] paths, string extra)
        {
            using var form = new 播放器.MainForm(paths);
            form.Show();
            PumpMessages(500);

            var list = Find(form, "listViewPlaylist") as ListView;

            if (list == null)
            {
                Log(40, "播放列表撤销移除：找不到 listViewPlaylist");
                return false;
            }

            var menu = list.ContextMenuStrip;
            var remove = MenuItem(menu, "cmsRemove");
            var undo = MenuItem(menu, "cmsUndo");

            if (remove == null || undo == null)
            {
                Log(40, "播放列表撤销移除：右键菜单里缺 cmsRemove / cmsUndo");
                return false;
            }

            form.UpdatePlaylistSelectionMenuForTest();

            if (undo.Enabled)
            {
                Log(40, "播放列表撤销移除：还没删东西时「撤销移除」不该可点");
                return false;
            }

            SelectRows(list, 1, 3);
            PumpMessages(120);
            remove.PerformClick();
            PumpMessages(300);

            if (PlaylistOf(form).GetPaths().SequenceEqual(paths))
            {
                Log(40, "播放列表撤销移除：移除没有生效（列表还是 6 项）");
                return false;
            }

            form.UpdatePlaylistSelectionMenuForTest();

            if (!undo.Enabled || !undo.Text.Contains("2 项", StringComparison.Ordinal))
            {
                Log(40, "播放列表撤销移除：移除 2 项之后菜单没有可撤销的 2 项（现在「"
                        + undo.Text + "」，可点=" + undo.Enabled + "）");
                return false;
            }

            form.HandlePlaylistKeyForTest(Keys.Z, control: true);
            PumpMessages(400);

            if (!PlaylistOf(form).GetPaths().SequenceEqual(paths))
            {
                Log(40, "播放列表撤销移除：Ctrl+Z 之后顺序不是原来的（现在 ["
                        + string.Join(",", PlaylistOf(form).GetPaths().Select(Path.GetFileName)) + "]）");
                return false;
            }

            if (!form.SelectedPlaylistIndicesForTest.SequenceEqual(new[] { 1, 3 }))
            {
                Log(40, "播放列表撤销移除：撤销之后没有选中还原的条目（现在 ["
                        + string.Join(",", form.SelectedPlaylistIndicesForTest) + "]）");
                return false;
            }

            if (form.CanUndoPlaylistRemovalForTest)
            {
                Log(40, "播放列表撤销移除：撤销完之后快照还在（会让人以为还能再撤一次）");
                return false;
            }

            if (!form.StatusText.Contains("已撤销移除", StringComparison.Ordinal))
            {
                Log(40, "播放列表撤销移除：状态栏没有说清撤销结果（现在「" + form.StatusText + "」）");
                return false;
            }

            // 排序只发 ContentChanged、条数不变：旧快照同样必须作废。
            SelectRows(list, 1, 3);
            PumpMessages(120);
            remove.PerformClick();
            PumpMessages(300);

            if (!form.CanUndoPlaylistRemovalForTest)
            {
                Log(40, "播放列表撤销移除：第二次移除之后反而没有快照了");
                return false;
            }

            form.ClickPlaylistColumn(1);
            PumpMessages(300);

            if (form.CanUndoPlaylistRemovalForTest)
            {
                Log(40, "播放列表撤销移除：排序之后旧的撤销快照还在（内容顺序已经变了）");
                return false;
            }

            // 再删一次，然后加一个新文件：条数变了，旧快照也必须作废。
            SelectRows(list, 0, 1);
            PumpMessages(120);
            remove.PerformClick();
            PumpMessages(300);

            if (!form.CanUndoPlaylistRemovalForTest)
            {
                Log(40, "播放列表撤销移除：第三次移除之后反而没有快照了");
                return false;
            }

            form.AddPathsForTest(new[] { extra });
            PumpMessages(400);

            if (form.CanUndoPlaylistRemovalForTest)
            {
                Log(40, "播放列表撤销移除：添加新文件之后旧的撤销快照还在（内容已经变了）");
                return false;
            }

            var countBefore = list.Items.Count;
            form.HandlePlaylistKeyForTest(Keys.Z, control: true);
            PumpMessages(200);

            if (list.Items.Count != countBefore
                || !form.StatusText.Contains("没有可撤销的移除", StringComparison.Ordinal))
            {
                Log(40, "播放列表撤销移除：没有快照时 Ctrl+Z 改了列表或没说明（现在 "
                        + list.Items.Count + " 项，状态「" + form.StatusText + "」）");
                return false;
            }

            form.Close();
            PumpMessages(200);
            return true;
        }

        /// <summary>纯模型：块移动 / 还原之后当前项都还是同一个对象（不靠"路径刚好一样"）。</summary>
        private static bool CheckPlaylistModel()
        {
            var model = new Playlist();

            for (var i = 0; i < 6; i++)
                model.Add("model-" + i + ".wav");

            model.SetCurrent(4);

            var moved = model.MoveMany(new[] { 1, 3 }, 6);

            if (!moved.SequenceEqual(new[] { 4, 5 })
                || !model.Current!.FilePath.EndsWith("model-4.wav", StringComparison.Ordinal)
                || model.CurrentIndex != 2)
            {
                Log(40, "播放列表整批移动：模型 MoveMany 之后当前项 / 返回索引不对（当前 "
                        + model.CurrentIndex + "，返回 [" + string.Join(",", moved) + "]）");
                return false;
            }

            var currentInside = new Playlist();

            for (var i = 0; i < 6; i++)
                currentInside.Add("inside-" + i + ".wav");

            currentInside.SetCurrent(3);
            currentInside.MoveMany(new[] { 1, 3 }, 6);

            if (currentInside.CurrentIndex != 5
                || !currentInside.Current!.FilePath.EndsWith("inside-3.wav", StringComparison.Ordinal))
            {
                Log(40, "播放列表整批移动：当前项在块里时没有按引用跟到新位置（现在 "
                        + currentInside.CurrentIndex + "）");
                return false;
            }

            // 还原：把刚才移到末尾的两项删掉，再按最终位置插回去；当前项不能被弄丢。
            var restoreItems = new[] { model.Items[4], model.Items[5] };
            model.RemoveMany(new[] { 4, 5 });

            if (model.CurrentIndex != 2)
            {
                Log(40, "播放列表整批移动：删除块之后当前项索引不对（现在 " + model.CurrentIndex + "）");
                return false;
            }

            var restored = model.InsertRestored(restoreItems, new[] { 4, 5 });

            if (!restored.SequenceEqual(new[] { 4, 5 })
                || !model.GetPaths().SequenceEqual(new[]
                {
                    "model-0.wav", "model-2.wav", "model-4.wav",
                    "model-5.wav", "model-1.wav", "model-3.wav"
                })
                || model.CurrentIndex != 2
                || !model.Current!.FilePath.EndsWith("model-4.wav", StringComparison.Ordinal))
            {
                Log(40, "播放列表整批移动：InsertRestored 没有按最终位置放回（现在 ["
                        + string.Join(",", model.GetPaths()) + "]，当前 " + model.CurrentIndex + "）");
                return false;
            }

            return true;
        }
    }
}
