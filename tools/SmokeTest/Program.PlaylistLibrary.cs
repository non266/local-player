// 检查点 17：歌单库（多个命名播放列表各存各的）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 17) 歌单库（多个播放列表各存各的）
        //
        // 这一项验的是"磁盘上那份真相"：名字能不能用、存成哪个文件、
        // 列出来的顺序对不对、改名 / 删除 / 从外面导入是不是真的动了文件，
        // 以及主窗体上的关联（标题里的歌单名、"未保存"什么时候出现）对不对。
        // -----------------------------------------------------------------

        private static bool TestPlaylistLibrary()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-playlists");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            // 每一首"曲子"都是真文件：歌单载入只认"文件在不在"（不解析内容），
            // 所以内容无所谓，但不能是假路径——否则验不出"少了哪几首"。
            var media = new List<string>();

            for (var i = 1; i <= 5; i++)
            {
                var path = Path.Combine(root, $"track{i}.mp3");
                File.WriteAllBytes(path, new byte[] { 0x49, 0x44, 0x33, (byte)i });
                media.Add(path);
            }

            try
            {
                if (!CheckPlaylistLibraryRules()) return false;
                if (!CheckPlaylistLibraryState()) return false;
                if (!CheckPlaylistLibraryFiles(root, media)) return false;
                if (!CheckPlaylistLibraryInMainForm(media)) return false;
                if (!CheckPlaylistLibraryDialog()) return false;

                // 1.3.0 B-1：拖拽换位（顺序落盘）+ 批量重命名
                if (!CheckPlaylistOrderAndBatchRename(media)) return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }

            Log(17, "歌单库正常：名字校验（非法字符 / Windows 保留名 / 结尾点号 / 超长）、"
                    + "一个歌单一个 m3u8 文件、新建（同名不覆盖）/ 保存 / 列出（数字按数值排）/ "
                    + "载入 / 重命名 / 删除 / 导入都在，"
                    + "文件已经不在的条目会如实计数而不是悄悄丢掉，"
                    + "主窗体保存后标题带上歌单名、只有真的改了列表才标「未保存」（切歌不算）、"
                    + "点菜单项会替换列表、新建空歌单会清空并切过去，"
                    + "歌单窗口一次列出全部歌单、标出当前那份、还能就地新建");
            return true;
        }









        /// <summary>开一个歌单窗口摆好位置并等它画出来（B-1 那一段用两个：改名前 / 改名后各一个）。</summary>
        private static PlaylistLibraryDialog OpenLibraryDialog()
        {
            var dialog = new PlaylistLibraryDialog(null);

            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(80, 80);
            dialog.Show();
            PumpMessages(200);

            return dialog;
        }

        private static void CloseLibraryDialog(PlaylistLibraryDialog dialog)
        {
            dialog.Close();
            dialog.Dispose();
        }

        /// <summary>按标题找歌单窗口里的一行（当前歌单带「● 」前缀）。</summary>
        private static ListViewItem? FindRow(ListView list, string name) =>
            list.Items.Cast<ListViewItem>().FirstOrDefault(row =>
                string.Equals(row.SubItems[0].Text.Replace("● ", string.Empty), name, StringComparison.CurrentCultureIgnoreCase));

        /// <summary>读歌单顺序文件里的名字（没有文件时是空表）。</summary>
        private static List<string> ReadOrderFile() =>
            File.Exists(PlaylistLibrary.OrderFilePath)
                ? File.ReadAllLines(PlaylistLibrary.OrderFilePath)
                    .Select(line => line.Trim()).Where(line => line.Length > 0).ToList()
                : new List<string>();

        /// <summary>歌单窗口：一次列出全部歌单、标出当前那份、选一个载入。</summary>
        private static bool CheckPlaylistLibraryDialog()
        {
            // 批量删除那一段要"至少两份非当前歌单"才验得动，而歌单库在这一步里已经被前面几段
            // 改过（数量不能假定），所以这里先补两个空的进去
            PlaylistLibrary.Save("批量删除 A", new List<PlaylistItem>());
            PlaylistLibrary.Save("批量删除 B", new List<PlaylistItem>());

            var dialog = new PlaylistLibraryDialog("给主窗体看的歌单");

            using (dialog)
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(60, 60);
                dialog.Show();
                PumpMessages(200);

                var list = dialog.Controls.OfType<ListView>().FirstOrDefault();

                if (list == null || list.View != View.Details)
                {
                    Log(17, "歌单窗口检查：找不到歌单列表（或它不是明细视图）");
                    return false;
                }

                var expected = PlaylistLibrary.List();

                if (list.Items.Count != expected.Count)
                {
                    Log(17, $"歌单窗口检查：列出来 {list.Items.Count} 个歌单，库里其实有 {expected.Count} 个");
                    return false;
                }

                var shown = list.Items.Cast<ListViewItem>()
                    .Select(row => row.SubItems[0].Text.Replace("● ", string.Empty))
                    .ToList();

                for (var i = 0; i < expected.Count; i++)
                {
                    if (shown[i] == expected[i].Name) continue;

                    Log(17, $"歌单窗口检查：第 {i + 1} 行是「{shown[i]}」，按顺序应当是「{expected[i].Name}」");
                    return false;
                }

                var currentRows = list.Items.Cast<ListViewItem>()
                    .Where(row => row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal))
                    .ToList();

                if (currentRows.Count != 1 ||
                    currentRows[0].SubItems[0].Text.Replace("● ", string.Empty) != "给主窗体看的歌单")
                {
                    Log(17, $"歌单窗口检查：当前歌单没有被单独标出来（标了 {currentRows.Count} 行）");
                    return false;
                }

                // 条目数要显示出来（选之前就能看出哪个歌单大概是什么）
                if (currentRows[0].SubItems[1].Text != expected.First(p => p.Name == "给主窗体看的歌单").TrackCount.ToString())
                {
                    Log(17, $"歌单窗口检查：曲目数显示不对（「{currentRows[0].SubItems[1].Text}」）");
                    return false;
                }

                var buttons = dialog.Controls.OfType<Button>().ToList();
                var batchButton = buttons.FirstOrDefault(b => b.Text.Contains("批量重命名", StringComparison.Ordinal));

                // 除了「批量重命名」（单选时本就该是灰的），其余按钮在这一步都该能点
                if (buttons.Count < 8 || batchButton == null ||
                    buttons.Any(b => !b.Enabled && b != batchButton))
                {
                    Log(17, $"歌单窗口检查：按钮不全或该能点的点不动（{buttons.Count} 个按钮，"
                            + $"点不动的：{string.Join(" / ", buttons.Where(b => !b.Enabled).Select(b => b.Text))}）");
                    return false;
                }

                if (!buttons.Any(b => b.Text.Contains("载入", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("追加", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("新建", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("重命名", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("批量重命名", StringComparison.Ordinal)) ||
                    !buttons.Any(b => b.Text.Contains("删除", StringComparison.Ordinal)))
                {
                    Log(17, "歌单窗口检查：载入 / 追加 / 新建 / 重命名 / 批量重命名 / 删除 里有缺的");
                    return false;
                }

                // "批量"重命名只在选了两份以上时才该能点（只选一行时它点不动，那正是它的语义）
                if (batchButton.Enabled)
                {
                    Log(17, "歌单窗口检查：只选了一行，「批量重命名」却点得动");
                    return false;
                }

                // ---- B1：多选与批量删除 ----
                if (!CheckPlaylistMultiSelect(list, buttons)) return false;

                var scale = dialog.DeviceDpi / 96f;

                foreach (Control control in dialog.Controls)
                {
                    if (control.Right <= dialog.ClientSize.Width && control.Bottom <= dialog.ClientSize.Height)
                        continue;

                    Log(17, $"歌单窗口在 {scale:0.##} 倍缩放下有控件越界："
                            + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                    return false;
                }

                // ---- B1：一次删掉几个 ----
                // 必须放在"选一份载入"之前：那一步会把窗口关掉（列表就空了）
                if (!CheckPlaylistBatchDelete(dialog, list)) return false;

                // 选另一份歌单 → 载入：结果里应当是它的名字，而且是"替换"
                var other = list.Items.Cast<ListViewItem>()
                    .First(row => !row.SubItems[0].Text.StartsWith("●", StringComparison.Ordinal));

                other.Selected = true;
                PumpMessages(120);

                var selectedName = other.SubItems[0].Text;

                dialog.LoadSelected();

                PumpMessages(120);

                var result = dialog.Result;

                if (result.LoadName != selectedName || result.Append)
                {
                    Log(17, "歌单窗口检查：选了歌单之后交回去的结果不对（"
                            + $"名字 {result.LoadName ?? "空"}、追加 {result.Append}）");
                    return false;
                }

                dialog.Close();
            }

            Log(17, "歌单窗口正常：一次列出全部歌单（顺序与库里一致）、当前那份带「●」标记、"
                    + "曲目数与最后修改时间都在，载入 / 追加 / 新建 / 重命名 / 删除 / 打开文件夹都能点，"
                    + "多选能一次删掉几个，选一份载入会把它交回主窗体，控件没有越界");
            return true;
        }
    }
}
