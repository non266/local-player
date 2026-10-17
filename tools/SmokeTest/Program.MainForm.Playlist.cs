
// 播放列表：标题栏汇总、搜索（含输入框打字）、排序。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 播放列表标题要汇总出整个列表的总时长。
        /// <para>
        /// 这里刻意不用固定期望值：窗体启动时会恢复使用者的真实播放列表，
        /// 条目和总时长都不可预知。改为"标题里的总时长必须等于逐行时长之和"，
        /// 既不受播放列表内容影响，又真正校验了汇总逻辑。
        /// </para>
        /// </summary>
        private static bool CheckPlaylistSummary(Form form)
        {
            var header = Find(form, "lblPlaylistHeader");
            var listView = Find(form, "listViewPlaylist") as ListView;

            if (header == null || listView == null)
            {
                Log(6, "播放列表汇总检查：找不到标题或列表控件");
                return false;
            }

            if (listView.Items.Count == 0)
            {
                Log(6, "播放列表汇总检查：列表为空，跳过");
                return true;
            }

            // 等后台扫描把所有时长补全（未知项会以"（N 项未知）"标注在标题里）。
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                if (!header.Text.Contains("未知", StringComparison.Ordinal)) break;
                PumpMessages(200);
            }

            long expectedSeconds = 0;
            var unknown = 0;

            foreach (ListViewItem row in listView.Items)
            {
                var seconds = ParseClock(row.SubItems[2].Text);
                if (seconds < 0)
                {
                    unknown++;
                    continue;
                }

                expectedSeconds += seconds;
            }

            if (unknown > 0)
            {
                Log(6, $"播放列表汇总检查：仍有 {unknown} 项时长未知，标题为 \"{header.Text}\"");
                return false;
            }

            if (!header.Text.Contains($"（{listView.Items.Count}）", StringComparison.Ordinal))
            {
                Log(6, $"播放列表汇总检查：条目数对不上，标题为 \"{header.Text}\"，实际 {listView.Items.Count} 项");
                return false;
            }

            var actual = ParseHeaderTotal(header.Text);
            if (actual < 0)
            {
                Log(6, $"播放列表汇总检查：标题里读不出总时长：\"{header.Text}\"");
                return false;
            }

            // 每一行的时长都截断到秒显示，汇总用的是含毫秒的真实时长，
            // 所以"逐行求和"必然略小于标题总时长，上界是每行差不到 1 秒。
            var tolerance = listView.Items.Count;
            var difference = Math.Abs(actual - expectedSeconds);
            if (difference > tolerance)
            {
                Log(6, $"播放列表汇总检查：总时长偏差过大。标题={actual}s，逐行求和={expectedSeconds}s，"
                       + $"相差 {difference}s（允许上界 {tolerance}s）");
                return false;
            }

            Log(6, $"播放列表汇总正确：{listView.Items.Count} 项，总计 {FormatClock(actual)}"
                   + $"（与逐行求和时间一致，偏差 {difference}s）");
            return true;
        }

        /// <summary>
        /// 搜索过滤 + 列表索引映射。
        /// <para>
        /// 这段挡的是本次重构里最容易出错的地方：启用筛选后，界面行号和播放列表模型索引
        /// 不再一一对应，所有"界面行 → 条目"的转换都要靠 <c>ListViewItem.Tag</c> 里存的模型索引。
        /// 这里逐行校验序号列与 Tag 是否仍然一致，并确认清空关键字后能完全还原。
        /// </para>
        /// </summary>
        private static bool CheckPlaylistSearch(Form form)
        {
            var search = Find(form, "txtPlaylistSearch") as TextBox;
            var listView = Find(form, "listViewPlaylist") as ListView;

            if (search == null || listView == null)
            {
                Log(6, "搜索检查：找不到搜索框或列表控件");
                return false;
            }

            var originalCount = listView.Items.Count;
            if (originalCount < 3)
            {
                Log(6, "搜索检查：列表条目太少，跳过");
                return true;
            }

            var title = listView.Items[0].SubItems[1].Text;
            var token = title.Length >= 2 ? title.Substring(0, 2) : title;
            if (string.IsNullOrWhiteSpace(token))
            {
                Log(6, "搜索检查：取不到可用的关键字");
                return false;
            }

            search.Text = token;
            PumpMessages(250);

            var matched = listView.Items.Count;
            if (matched == 0)
            {
                Log(6, $"搜索检查：关键字「{token}」没有匹配到任何条目");
                return false;
            }

            foreach (ListViewItem row in listView.Items)
            {
                if (row.Tag is not int playlistIndex)
                {
                    Log(6, "搜索检查：行里没有记录播放列表索引（Tag 丢失）");
                    return false;
                }

                // 序号列用的就是模型索引 + 1，筛选时编号要保持稳定。
                if (row.Text != (playlistIndex + 1).ToString())
                {
                    Log(6, $"搜索检查：序号列与 Tag 不一致（显示 {row.Text}，Tag {playlistIndex}）");
                    return false;
                }

                var name = row.SubItems[1].Text;
                var path = row.ToolTipText ?? string.Empty;
                var hit = name.Contains(token, StringComparison.CurrentCultureIgnoreCase) ||
                          path.Contains(token, StringComparison.CurrentCultureIgnoreCase);

                if (!hit)
                {
                    Log(6, $"搜索检查：可见行「{name}」并不匹配关键字「{token}」");
                    return false;
                }
            }

            search.Clear();
            PumpMessages(250);

            if (listView.Items.Count != originalCount)
            {
                Log(6, $"搜索检查：清空关键字后没有还原（{listView.Items.Count} != {originalCount}）");
                return false;
            }

            // 再验证一次"确实会过滤"：用一个不可能命中的关键字，应当一条都不剩。
            search.Text = "zzz-不存在的关键字-zzz";
            PumpMessages(250);

            if (listView.Items.Count != 0)
            {
                Log(6, $"搜索检查：不可能命中的关键字却匹配到 {listView.Items.Count} 项");
                return false;
            }

            search.Clear();
            PumpMessages(250);

            if (listView.Items.Count != originalCount)
            {
                Log(6, "搜索检查：第二次清空后没有还原");
                return false;
            }

            Log(6, $"搜索过滤正常：{originalCount} 项中匹配「{token}」{matched} 项，清空后完全还原");
            return true;
        }

        /// <summary>
        /// 焦点在搜索框里打字时，主窗体的单键快捷键不能把按键吃掉。
        /// <para>
        /// ⚠ 这条是实测出来的：WinForms 会把同一条 <c>WM_KEYDOWN</c> 既交给文本框、
        /// 又交给窗体的 <c>ProcessCmdKey</c>。窗体一旦 <c>return true</c>，
        /// 那条按键就被整个吃掉——<b>字符进不了搜索框，播放控制的动作却照跑</b>：
        /// 在搜索框里打不出空格（"空格分隔多个关键字"实际上是坏的，还顺手把播放暂停了），
        /// 按 Delete 会删掉播放列表里的选中项（状态栏："已从播放列表移除 1 项"）。
        /// </para>
        /// <para>
        /// 正因为如此，这里必须<b>真的往窗口句柄投按键</b>：
        /// 直接给控件赋文本、或者反射调用 <c>ProcessCmdKey</c>，走的都是另一条路，
        /// 永远量不到这个 bug（旧测试就是这么漏掉它的）。
        /// </para>
        /// </summary>
        private static bool CheckSearchTyping(Form form)
        {
            var search = Find(form, "txtPlaylistSearch") as TextBox;
            var listView = Find(form, "listViewPlaylist") as ListView;

            if (search == null || listView == null)
            {
                Log(6, "搜索打字检查：找不到搜索框或列表控件");
                return false;
            }

            search.Clear();
            PumpMessages(200);

            // —— 临时诊断结束 ——

            if (listView.Items.Count == 0)
            {
                Log(6, "搜索打字检查：列表是空的，验不了 Delete 会不会误删");
                return false;
            }

            // 1) 字符必须真的进搜索框（别为了挡快捷键，把打字也一起挡掉了）
            TypeKey(search.Handle, Keys.S);
            TypeKey(search.Handle, Keys.Space);
            PumpMessages(250);

            if (search.Text != "s ")
            {
                Log(6, $"搜索打字检查：字符没有进搜索框（现在的内容是 [{search.Text}]）");
                return false;
            }

            // 2) 搜索框里的 Delete 不能删播放列表：这就是当初实测到的那一下
            search.Clear();
            PumpMessages(250);

            listView.SelectedItems.Clear();
            listView.Items[0].Selected = true;
            var rowsBefore = listView.Items.Count;

            TypeKey(search.Handle, Keys.Delete);
            PumpMessages(300);

            if (listView.Items.Count != rowsBefore)
            {
                Log(6, $"搜索打字检查：在搜索框里按 Delete 把播放列表删掉了"
                     + $"（{rowsBefore} → {listView.Items.Count} 项）");
                return false;
            }

            // 3) 反向确认没有"矫枉过正"：按键不是打在文本框上时，快捷键必须还是活的。
            //    这里把消息投给列表本身，走的是和真实按键同一条路。
            listView.SelectedItems.Clear();     // 只留一项，删掉的项数才可预期
            listView.Items[0].Selected = true;
            PumpMessages(150);

            TypeKey(listView.Handle, Keys.Delete);
            PumpMessages(350);

            if (listView.Items.Count != rowsBefore - 1)
            {
                Log(6, $"搜索打字检查：焦点不在文本框里时 Delete 反而不生效了"
                     + $"（{rowsBefore} → {listView.Items.Count} 项）");
                return false;
            }

            Log(6, $"搜索打字检查正常：搜索框里打字不会误触快捷键（打 s 和空格只进文本框），"
                 + $"Delete 在搜索框里不删歌、在列表上照删（{rowsBefore} → {listView.Items.Count} 项）");

            search.Clear();
            PumpMessages(150);
            return true;
        }

        /// <summary>排序：按名称升序 / 降序切换，以及恢复原始顺序。用真实的右键菜单项触发。</summary>
        private static bool CheckPlaylistSort(Form form)
        {
            var listView = Find(form, "listViewPlaylist") as ListView;
            var sortMenu = listView?.ContextMenuStrip?.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Name == "cmsSort");

            if (listView == null || sortMenu == null)
            {
                Log(6, "排序检查：找不到列表或排序菜单");
                return false;
            }

            if (listView.Items.Count < 3)
            {
                Log(6, "排序检查：列表条目太少，跳过");
                return true;
            }

            var original = ReadTitles(listView);

            var byName = sortMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Name == "cmsSortName");
            var byOriginal = sortMenu.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Name == "cmsSortOriginal");

            if (byName == null || byOriginal == null)
            {
                Log(6, "排序检查：排序菜单项缺失");
                return false;
            }

            byName.PerformClick();
            PumpMessages(200);
            if (!IsSorted(ReadTitles(listView), descending: false))
            {
                Log(6, "排序检查：按名称升序的结果不正确");
                return false;
            }

            // 再点一次同一项应切换为降序
            byName.PerformClick();
            PumpMessages(200);
            if (!IsSorted(ReadTitles(listView), descending: true))
            {
                Log(6, "排序检查：再次点击没有切换成降序");
                return false;
            }

            byOriginal.PerformClick();
            PumpMessages(200);

            if (!original.SequenceEqual(ReadTitles(listView), StringComparer.Ordinal))
            {
                Log(6, "排序检查：恢复原始顺序后与排序前不一致");
                return false;
            }

            // ---- 表头点击排序：点一下升序、再点反向、第三下回原始顺序 ----
            if (listView.HeaderStyle != ColumnHeaderStyle.Clickable)
            {
                Log(6, $"排序检查：表头点不动（HeaderStyle = {listView.HeaderStyle}，排序只藏在右键菜单里）");
                return false;
            }

            var mainForm = (播放器.MainForm)form;

            mainForm.ClickPlaylistColumn(1);
            PumpMessages(200);

            if (!IsSorted(ReadTitles(listView), descending: false) ||
                !listView.Columns[1].Text.Contains("↑", StringComparison.Ordinal))
            {
                Log(6, "排序检查：点「标题」表头之后没有按名称升序，或表头上没写出方向"
                        + $"（表头「{listView.Columns[1].Text}」）");
                return false;
            }

            if (byName.Text.IndexOf('↑') < 0)
            {
                Log(6, $"排序检查：点了表头，右键菜单里的排序状态没跟着刷（「{byName.Text}」）");
                return false;
            }

            mainForm.ClickPlaylistColumn(1);
            PumpMessages(200);

            if (!IsSorted(ReadTitles(listView), descending: true) ||
                !listView.Columns[1].Text.Contains("↓", StringComparison.Ordinal))
            {
                Log(6, $"排序检查：同一列表头再点一下没有反向（表头「{listView.Columns[1].Text}」）");
                return false;
            }

            mainForm.ClickPlaylistColumn(1);
            PumpMessages(200);

            if (!original.SequenceEqual(ReadTitles(listView), StringComparer.Ordinal))
            {
                Log(6, "排序检查：同一列表头第三下没有回到原始顺序");
                return false;
            }

            if (listView.Columns[1].Text != "标题" || listView.Columns[2].Text != "时长")
            {
                Log(6, "排序检查：回到原始顺序之后表头上的箭头没去掉"
                        + $"（「{listView.Columns[1].Text}」/「{listView.Columns[2].Text}」）");
                return false;
            }

            // 第 0 列是序号：点它就该"放回原始顺序"
            mainForm.ClickPlaylistColumn(2);
            PumpMessages(200);

            if (!IsSorted(ReadTitles(listView), descending: false))
            {
                Log(6, "排序检查：点「时长」表头没有按时长升序");
                return false;
            }

            mainForm.ClickPlaylistColumn(0);
            PumpMessages(200);

            if (!original.SequenceEqual(ReadTitles(listView), StringComparer.Ordinal))
            {
                Log(6, "排序检查：点「#」表头没有回到原始顺序");
                return false;
            }

            Log(6, $"排序正常：{original.Count} 项可用右键菜单切换升降序并恢复原始顺序，"
                    + "表头也能点（一下升序 / 再点反向 / 第三下回原始顺序，方向箭头与菜单同步），"
                    + "点「#」表头直接回原始顺序");
            return true;
        }

        private static List<string> ReadTitles(ListView listView)
        {
            var titles = new List<string>();

            foreach (ListViewItem row in listView.Items)
                titles.Add(row.SubItems[1].Text);

            return titles;
        }

        private static bool IsSorted(List<string> titles, bool descending)
        {
            for (var i = 1; i < titles.Count; i++)
            {
                var order = string.Compare(titles[i - 1], titles[i], StringComparison.CurrentCultureIgnoreCase);
                if (descending ? order < 0 : order > 0) return false;
            }

            return true;
        }
    }
}
