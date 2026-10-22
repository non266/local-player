// 播放列表：找出重复项（1.7.0 F4）。自己起窗体、不碰共享列表。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// F4（1.7.0）：找出重复项——同一个文件**保留第一次出现的那一项**，其余只**选中**（不删）。
        /// <para>
        /// 自己起一个 MainForm：共享的那张列表后面还要给别的检查用，动它容易牵连。
        /// 路径比对用大小写不同的写法验一次（Windows 上那是同一个文件）。
        /// </para>
        /// </summary>
        private static bool CheckPlaylistDuplicates()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-duplicates");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            var a = Path.Combine(root, "dup-a.wav");
            var b = Path.Combine(root, "dup-b.wav");
            var c = Path.Combine(root, "dup-c.wav");

            WriteWav(a, seconds: 1, frequency: 440);
            WriteWav(b, seconds: 1, frequency: 480);
            WriteWav(c, seconds: 1, frequency: 520);

            // 同一个文件、只改文件名大小写：Windows 上 File.Exists 认它，找重复也该认它
            var aDifferentCase = Path.Combine(root, "DUP-A.WAV");

            try
            {
                using (var form = new 播放器.MainForm(new[] { a, b, c }))
                {
                    form.Show();
                    PumpMessages(400);

                    // 入口那条路会去重（"打开文件夹"的语义），所以这里直接往模型里追加两份：
                    // 列表**允许**重复（同一段听两遍是合法用法），「找出重复项」就是给这种状态用的。
                    var model = PlaylistOf(form);
                    model.Add(b);
                    model.Add(aDifferentCase);
                    PumpMessages(300);

                    var list = Find(form, "listViewPlaylist") as ListView;
                    var item = list?.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
                        .FirstOrDefault(i => i.Name == "cmsFindDuplicates");

                    if (list == null || item == null)
                    {
                        Log(6, "找出重复项：找不到列表，或右键菜单里没有「找出重复项」");
                        return false;
                    }

                    if (list.Items.Count != 5)
                    {
                        Log(6, $"找出重复项：列表项数是 {list.Items.Count}，期望 5（同一段听两遍是合法用法，不该被并掉）");
                        return false;
                    }

                    var currentBefore = PlaylistOf(form).CurrentIndex;

                    item.PerformClick();
                    PumpMessages(200);

                    var selected = list.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();

                    if (!selected.SequenceEqual(new[] { 3, 4 }))
                    {
                        Log(6, $"找出重复项：选中的是 [{string.Join(",", selected)}]，期望 [3,4]"
                                + "（同一个文件要保留第一次出现的那一项）");
                        return false;
                    }

                    if (list.Items.Count != 5)
                    {
                        Log(6, "找出重复项：选中之后列表项数变了——它不该自动删");
                        return false;
                    }

                    if (PlaylistOf(form).CurrentIndex != currentBefore)
                    {
                        Log(6, "找出重复项：当前项被挪动了（找重复不该改播放位置）");
                        return false;
                    }

                    if (!form.StatusText.Contains("找到 2 项重复", StringComparison.Ordinal))
                    {
                        Log(6, "找出重复项：状态栏没说清找到几项（现在「" + form.StatusText + "」）");
                        return false;
                    }

                    // 筛选状态下与拖动同口径：先清筛选再找
                    var search = Find(form, "txtPlaylistSearch") as TextBox;

                    if (search != null)
                    {
                        search.Text = "dup-b";
                        PumpMessages(200);

                        item.PerformClick();
                        PumpMessages(150);

                        if (!form.StatusText.Contains("请先清空搜索框", StringComparison.Ordinal))
                        {
                            Log(6, "找出重复项：筛选状态下没有提示先清空搜索框（现在「"
                                    + form.StatusText + "」）");
                            return false;
                        }

                        search.Clear();
                        PumpMessages(150);
                    }

                    form.Close();
                    PumpMessages(200);
                }

                // 没有重复的列表：如实说"没有重复项"
                using (var form = new 播放器.MainForm(new[] { a, b, c }))
                {
                    form.Show();
                    PumpMessages(400);

                    var list = Find(form, "listViewPlaylist") as ListView;
                    var item = list?.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
                        .FirstOrDefault(i => i.Name == "cmsFindDuplicates");

                    if (list == null || item == null)
                    {
                        Log(6, "找出重复项：第二个窗体里找不到菜单项");
                        return false;
                    }

                    item.PerformClick();
                    PumpMessages(150);

                    if (!form.StatusText.Contains("没有重复项", StringComparison.Ordinal))
                    {
                        Log(6, "找出重复项：没有重复时状态栏没说「没有重复项」（现在「"
                                + form.StatusText + "」）");
                        return false;
                    }

                    if (list.SelectedIndices.Count != 0)
                    {
                        Log(6, "找出重复项：没有重复却选中了东西");
                        return false;
                    }

                    form.Close();
                    PumpMessages(200);
                }

                Log(6, "找出重复项正常：保留第一次出现的那一项、其余只选中不删（大小写不同的写法也认成同一个文件），"
                        + "筛选时提示先清空，没有重复就如实说");
                return true;
            }
            catch (Exception ex)
            {
                Log(6, "找出重复项检查失败: " + ex.Message);
                return false;
            }
            finally
            {
                TryDeleteDirectory(root);
            }
        }

    }
}
