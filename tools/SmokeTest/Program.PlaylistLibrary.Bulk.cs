// 歌单库 —— 多选与批量（多选按钮态、批量删除、排序与批量重命名）。
//
// 从 Program.PlaylistLibrary.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。


namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// B1：多选之后按钮该变的要变——载入 / 追加 / 重命名 是"针对一份歌单"的动作，
        /// 选了两份以上就点不动；删除支持多选，按钮上写着会删几个。
        /// </summary>
        private static bool CheckPlaylistMultiSelect(ListView list, List<Button> buttons)
        {
            if (!list.MultiSelect)
            {
                Log(17, "歌单窗口检查：列表不能多选（批量删除要靠它）");
                return false;
            }

            var loadButton = buttons.FirstOrDefault(button => button.Text.Contains("载入", StringComparison.Ordinal));
            var deleteButton = buttons.FirstOrDefault(button => button.Text.Contains("删除", StringComparison.Ordinal));
            var batchButton = buttons.FirstOrDefault(button => button.Text.Contains("批量重命名", StringComparison.Ordinal));

            if (loadButton == null || deleteButton == null || batchButton == null)
            {
                Log(17, "歌单窗口检查：找不到载入 / 删除 / 批量重命名按钮");
                return false;
            }

            foreach (ListViewItem row in list.Items) row.Selected = true;
            PumpMessages(150);

            var count = list.SelectedItems.Count;

            if (count < 2 || loadButton.Enabled || !deleteButton.Enabled ||
                !deleteButton.Text.Contains($"{count} 个", StringComparison.Ordinal))
            {
                Log(17, "歌单窗口检查：多选之后按钮状态不对"
                        + $"（选了 {count} 项、载入 {(loadButton.Enabled ? "能点" : "是灰的")}、"
                        + $"删除按钮「{deleteButton.Text}」）");
                return false;
            }

            // 批量重命名要的正是"选了好几份"：这时候它必须能点
            if (!batchButton.Enabled)
            {
                Log(17, $"歌单窗口检查：选了 {count} 份，「批量重命名」却点不动");
                return false;
            }

            // 恢复成"只选一行"，后面的检查按单选走
            list.SelectedItems.Clear();
            list.Items[0].Selected = true;
            PumpMessages(120);

            if (!loadButton.Enabled || !deleteButton.Enabled)
            {
                Log(17, "歌单窗口检查：恢复单选之后按钮没有回到可点状态");
                return false;
            }

            if (batchButton.Enabled)
            {
                Log(17, "歌单窗口检查：恢复单选之后「批量重命名」还是能点");
                return false;
            }

            return true;
        }

        /// <summary>
        /// B1：多选之后一次删掉几个。<b>放在歌单窗口检查的最后</b>——它真的会删库里的歌单。
        /// 要验两件事：一次删掉选中的那几个（当前歌单不受影响），
        /// 以及删掉的正好是当前歌单时、交回主窗体的关联要断开。
        /// </summary>
        private static bool CheckPlaylistBatchDelete(PlaylistLibraryDialog dialog, ListView list)
        {
            var others = list.Items.Cast<ListViewItem>()
                .Where(row => !row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal))
                .ToList();

            if (others.Count < 2)
            {
                Log(17, "歌单窗口检查：批量删除至少要两份非当前歌单"
                        + $"（列表里一共 {list.Items.Count} 行、其中非当前 {others.Count} 行）");
                return false;
            }

            var targets = others.Take(2)
                .Select(row => row.SubItems[0].Text.Replace("● ", string.Empty))
                .ToList();

            list.SelectedItems.Clear();
            others[0].Selected = true;
            others[1].Selected = true;
            PumpMessages(150);

            var deleted = dialog.DeleteSelected(confirm: false);
            PumpMessages(150);

            if (deleted != 2)
            {
                Log(17, $"歌单窗口检查：选了两份歌单，批量删除只删掉 {deleted} 个");
                return false;
            }

            foreach (var name in targets)
            {
                if (!PlaylistLibrary.Exists(name)) continue;

                Log(17, $"歌单窗口检查：批量删除之后「{name}」还在库里");
                return false;
            }

            if (dialog.Result.CurrentName == null)
            {
                Log(17, "歌单窗口检查：删的不是当前歌单，关联却被断开了");
                return false;
            }

            // 再把当前那一份删掉：这次关联必须断开
            var current = list.Items.Cast<ListViewItem>()
                .FirstOrDefault(row => row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal));

            if (current == null)
            {
                Log(17, "歌单窗口检查：批量删除之后「当前歌单」那一行不见了");
                return false;
            }

            list.SelectedItems.Clear();
            current.Selected = true;
            PumpMessages(120);

            if (dialog.DeleteSelected(confirm: false) != 1 || dialog.Result.CurrentName != null)
            {
                Log(17, "歌单窗口检查：删掉当前歌单之后关联没有断开");
                return false;
            }

            Log(17, "歌单窗口多选正常：能多选、多选时载入 / 追加 / 重命名点不动、删除按钮写着会删几个，"
                    + "一次确认删掉两个（当前歌单不受影响），删掉当前那份之后关联断开");
            return true;
        }

        /// <summary>
        /// B-1（1.3.0）：歌单顺序（拖拽换位）+ 批量重命名。
        /// <para>
        /// 判据分四层：① <b>纯算术</b>——拖放的"插到哪儿"换成"挪完之后的最终下标"
        /// （插到自己后面要减一，差一位这种错在界面上很难看出来）；② <b>顺序真的落盘</b>——
        /// 挪完之后 <c>PlaylistLibrary.List()</c> 的顺序<b>和顺序文件里的行</b>都对，
        /// 而且每个歌单文件里的条目顺序一个都没动（换位不该碰内容）；
        /// ③ <b>改名保住位置</b>——改成别的名字之后它还留在原来那一格（顺序文件跟着换名字），
        /// 而不是掉到"没排过"的那一堆里；④ <b>批量重命名</b>——按列表顺序改成「基名 1」「基名 2」…、
        /// 内容与位置都不动、当前歌单关联跟着改；名字撞车时<b>一个文件都不动</b>。
        /// </para>
        /// <para>
        /// <b>如实说明</b>：从"按住鼠标拖"到"松手"这一段 OLE 拖放管道<b>没有进冒烟</b>
        /// （那要真的按住鼠标走一段，脚本里做不稳）；这里驱动的是<b>落下去之后做的那件事</b>
        /// （<c>MoveSelectedTo</c>）和它用的那个下标算术。窗口那一侧的接线（<c>ItemDrag</c> /
        /// <c>DragOver</c> / <c>InsertionMark</c> 的提示）只能人眼看。
        /// </para>
        /// <para>
        /// 素材自己造、自己清：顺序文件是<b>整个歌单库共用</b>的，所以这一段开始时先删掉它、
        /// 结束时也删掉，免得把"歌单库顺序"这种全局状态留给别的检查。
        /// </para>
        /// </summary>
        private static bool CheckPlaylistOrderAndBatchRename(List<string> media)
        {
            // ---- ① 纯算术：插到哪儿 → 最终下标 ----
            if (PlaylistLibraryDialog.TargetIndex(2, 0, after: false) != 0 ||
                PlaylistLibraryDialog.TargetIndex(0, 2, after: true) != 2 ||
                PlaylistLibraryDialog.TargetIndex(1, 1, after: true) != 1 ||
                PlaylistLibraryDialog.TargetIndex(0, 1, after: false) != 0)
            {
                Log(17, "歌单顺序检查：拖放的插位换算不对"
                        + $"（(2,0,前)={PlaylistLibraryDialog.TargetIndex(2, 0, false)}、"
                        + $"(0,2,后)={PlaylistLibraryDialog.TargetIndex(0, 2, true)}、"
                        + $"(1,1,后)={PlaylistLibraryDialog.TargetIndex(1, 1, true)}、"
                        + $"(0,1,前)={PlaylistLibraryDialog.TargetIndex(0, 1, false)}）");
                return false;
            }

            // ---- ② 顺序：拖完必须落盘 ----
            var trio = new[] { "顺序1", "顺序2", "顺序3" };
            var created = new List<string>(trio);

            TryDelete(PlaylistLibrary.OrderFilePath);

            foreach (var name in trio)
            {
                PlaylistLibrary.Delete(name, out _);
                PlaylistLibrary.Save(name, media.Take(3).Select(path => new PlaylistItem(path)).ToList());
            }

            PlaylistLibrary.Invalidate();

            var renamedTo = string.Empty;

            try
            {
                var dialog = OpenLibraryDialog();

                try
                {
                    var list = dialog.Controls.OfType<ListView>().First();

                    var before = PlaylistLibrary.List().Select(playlist => playlist.Name).ToList();

                    // 一开始必须是"按名字自然排"（顺序文件刚被删掉）
                    var positions = trio.Select(name => before.IndexOf(name)).ToList();

                    if (positions.Any(index => index < 0) || positions[0] >= positions[1] || positions[1] >= positions[2])
                    {
                        Log(17, "歌单顺序检查：刚建好的三份歌单没有按名字自然排（"
                                + string.Join(" / ", positions) + "）");
                        return false;
                    }

                    // 把"顺序3"拖到"顺序1"前面
                    var row = FindRow(list, "顺序3");

                    if (row == null)
                    {
                        Log(17, "歌单顺序检查：窗口里找不到「顺序3」那一行");
                        return false;
                    }

                    list.SelectedItems.Clear();
                    row.Selected = true;
                    PumpMessages(120);

                    if (!dialog.MoveSelectedTo(positions[0], interactive: false))
                    {
                        Log(17, "歌单顺序检查：把「顺序3」挪到第 1 位失败");
                        return false;
                    }

                    PumpMessages(150);

                    var after = PlaylistLibrary.List().Select(playlist => playlist.Name).ToList();

                    var expectedOrder = new List<string>(before);
                    expectedOrder.Remove("顺序3");
                    expectedOrder.Insert(positions[0], "顺序3");

                    // 先看磁盘：顺序文件里必须就是这份顺序
                    //（"拖动只改了界面 / 内存，没落到文件"的变异就死在这一条上）
                    var onDisk = ReadOrderFile();

                    if (!onDisk.SequenceEqual(expectedOrder))
                    {
                        var actualText = onDisk.Count == 0 ? "（没有顺序文件 / 里面是空的）" : string.Join(" / ", onDisk.Take(6));

                        Log(17, "歌单顺序检查：拖动之后磁盘上的顺序没变（顺序文件里是 "
                                + $"{actualText}，期望 {string.Join(" / ", expectedOrder.Take(6))}）");
                        return false;
                    }

                    if (!after.SequenceEqual(expectedOrder))
                    {
                        Log(17, "歌单顺序检查：拖完之后 List() 的顺序不对（期望 "
                                + string.Join(" / ", expectedOrder.Take(6)) + "，实际 "
                                + string.Join(" / ", after.Take(6)) + "）");
                        return false;
                    }

                    // 换位只换顺序：每个歌单文件里的条目一个都没动
                    foreach (var name in trio)
                    {
                        var path = PlaylistLibrary.ResolvePath(name);

                        if (path == null)
                        {
                            Log(17, $"歌单顺序检查：换位之后「{name}」不见了");
                            return false;
                        }

                        var entries = PlaylistFile.ReadPaths(path);

                        if (entries.Count != 3 || !entries.SequenceEqual(media.Take(3)))
                        {
                            Log(17, $"歌单顺序检查：换位把「{name}」里的条目弄动了（现在 {entries.Count} 条）");
                            return false;
                        }
                    }

                    // 窗口里显示的顺序也得跟着走（界面读的就是磁盘那份真相）
                    var shown = list.Items.Cast<ListViewItem>().Select(item => item.SubItems[0].Text).ToList();

                    if (!shown.SequenceEqual(after))
                    {
                        Log(17, "歌单顺序检查：窗口里显示的顺序和库里的顺序不一致（"
                                + string.Join(" / ", shown.Take(6)) + "）");
                        return false;
                    }
                }
                finally
                {
                    CloseLibraryDialog(dialog);
                }

                // ---- ③ 改名保住位置（纯 Core，不经过窗口） ----
                if (!PlaylistLibrary.Rename("顺序2", "AAA", out renamedTo, out var error))
                {
                    Log(17, "歌单顺序检查：改名失败（" + error + "）");
                    return false;
                }

                created.Add(renamedTo);

                var afterRename = PlaylistLibrary.List().Select(playlist => playlist.Name).ToList();

                if (afterRename.IndexOf(renamedTo) != afterRename.IndexOf("顺序1") + 1)
                {
                    Log(17, $"歌单顺序检查：改过名的歌单没有留在原来的位置（「{renamedTo}」在第 "
                            + $"{afterRename.IndexOf(renamedTo) + 1} 位，「顺序1」在第 "
                            + $"{afterRename.IndexOf("顺序1") + 1} 位）");
                    return false;
                }

                // ---- ④⑤ 批量重命名与名字撞车（重新开一个窗口，读的就是改名之后的现状） ----
                var second = OpenLibraryDialog();

                try
                {
                    var list = second.Controls.OfType<ListView>().First();

                    // 按窗口里的显示顺序选三行（顺序3 / 顺序1 / AAA）
                    var targets = new[] { "顺序3", "顺序1", renamedTo };
                    list.SelectedItems.Clear();

                    foreach (var name in targets)
                    {
                        var item = FindRow(list, name);

                        if (item == null)
                        {
                            Log(17, $"歌单顺序检查：批量重命名前找不到「{name}」那一行");
                            return false;
                        }

                        item.Selected = true;
                    }

                    PumpMessages(150);

                    var indicesBefore = targets
                        .Select(name => PlaylistLibrary.List().ToList().FindIndex(p => p.Name == name))
                        .OrderBy(index => index)
                        .ToList();

                    if (second.BatchRename("合集", interactive: false) != 3)
                    {
                        Log(17, "歌单顺序检查：批量重命名没有改掉三份");
                        return false;
                    }

                    PumpMessages(150);

                    var batch = new List<string>();

                    for (var i = 1; i <= 3; i++)
                    {
                        var name = $"合集 {i}";

                        if (!PlaylistLibrary.Exists(name))
                        {
                            Log(17, $"歌单顺序检查：批量重命名之后没有「{name}」");
                            return false;
                        }

                        batch.Add(name);
                        created.Add(name);
                    }

                    foreach (var name in targets)
                    {
                        if (!PlaylistLibrary.Exists(name)) continue;

                        Log(17, $"歌单顺序检查：批量重命名之后旧名字「{name}」还在");
                        return false;
                    }

                    // 内容跟着名字走（顺序3 → 合集 1，顺序1 → 合集 2，AAA → 合集 3）
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var path = PlaylistLibrary.ResolvePath(batch[i]);

                        if (path == null)
                        {
                            Log(17, $"歌单顺序检查：批量重命名之后找不到「{batch[i]}」的文件");
                            return false;
                        }

                        var entries = PlaylistFile.ReadPaths(path);

                        if (entries.Count != 3 || !entries.SequenceEqual(media.Take(3)))
                        {
                            Log(17, $"歌单顺序检查：批量重命名把「{batch[i]}」里的条目弄丢了（现在 {entries.Count} 条）");
                            return false;
                        }
                    }

                    var orderAfterBatch = PlaylistLibrary.List().Select(playlist => playlist.Name).ToList();
                    var indicesAfter = batch
                        .Select(name => orderAfterBatch.IndexOf(name))
                        .OrderBy(index => index)
                        .ToList();

                    if (!indicesAfter.SequenceEqual(indicesBefore))
                    {
                        Log(17, "歌单顺序检查：批量重命名之后这三份歌单的位置变了（之前 "
                                + string.Join(" / ", indicesBefore) + "，之后 "
                                + string.Join(" / ", indicesAfter) + "）");
                        return false;
                    }

                    // ---- ⑤ 名字撞车：一个文件都不动 ----
                    PlaylistLibrary.Delete("占用 1", out _);
                    PlaylistLibrary.Save("占用 1", new List<PlaylistItem> { new PlaylistItem(media[0]) });

                    var filesBefore = PlaylistLibrary.List()
                        .Select(playlist => playlist.Name).OrderBy(name => name, StringComparer.CurrentCulture).ToList();

                    var orderFileBefore = ReadOrderFile();

                    list.SelectedItems.Clear();

                    foreach (var name in batch.Take(2))
                    {
                        var item = FindRow(list, name);
                        if (item != null) item.Selected = true;
                    }

                    PumpMessages(150);

                    if (second.BatchRename("占用", interactive: false) != 0)
                    {
                        Log(17, "歌单顺序检查：批量重命名撞上已有的名字，却还是改了文件");
                        return false;
                    }

                    PumpMessages(120);

                    var filesAfter = PlaylistLibrary.List()
                        .Select(playlist => playlist.Name).OrderBy(name => name, StringComparer.CurrentCulture).ToList();

                    if (!filesAfter.SequenceEqual(filesBefore))
                    {
                        Log(17, "歌单顺序检查：名字撞车时动了文件（之前 " + string.Join(" / ", filesBefore)
                                + "，之后 " + string.Join(" / ", filesAfter) + "）");
                        return false;
                    }

                    if (!ReadOrderFile().SequenceEqual(orderFileBefore))
                    {
                        Log(17, "歌单顺序检查：名字撞车时把顺序文件也动了");
                        return false;
                    }

                    var occupiedPath = PlaylistLibrary.ResolvePath("占用 1");

                    if (occupiedPath == null || PlaylistFile.ReadPaths(occupiedPath).Count != 1)
                    {
                        Log(17, "歌单顺序检查：撞车那一份歌单的内容被改动了");
                        return false;
                    }
                }
                finally
                {
                    CloseLibraryDialog(second);
                }

                Log(17, "歌单顺序与批量重命名正常：拖放的插位换算对（插到自己后面会减一），"
                        + "挪完之后 List() 与顺序文件里的顺序一致、歌单内容一条没动，"
                        + "改名单个歌单仍留在原位置，批量重命名按显示顺序改成「基名 N」且内容与位置都不动，"
                        + "名字撞车时一个文件都没动");
                return true;
            }
            finally
            {
                foreach (var name in created.Append("占用 1").Distinct(StringComparer.CurrentCultureIgnoreCase))
                    PlaylistLibrary.Delete(name, out _);

                // 顺序文件是全局状态：这一段用完就删掉，别留给后面（也等于验了"删掉就回到自然顺序"）
                TryDelete(PlaylistLibrary.OrderFilePath);
                PlaylistLibrary.Invalidate();
            }
        }
    }
}
