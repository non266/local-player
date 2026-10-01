// 检查点 5~6：主窗体的布局 / 主题 / 搜索 / 排序 / 播放历史 / WndProc 健壮性。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 2) 主窗体
        // -----------------------------------------------------------------

        private static bool TestMainForm()
        {
            // 生成若干个命名各异的测试音频：既用来验证列表标题的总时长汇总，
            // 也让搜索与排序有足够多样的数据。
            var names = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot" };
            var files = new List<string>();

            for (var i = 0; i < names.Length; i++)
            {
                var path = Path.Combine(AppContext.BaseDirectory, $"form-{names[i]}.wav");
                WriteWav(path, seconds: i + 1, frequency: 400 + i * 40);
                files.Add(path);
            }

            try
            {
                using (var form = new 播放器.MainForm(files.ToArray()))
                {
                    // 访问 Handle 会强制创建窗口句柄（但不显示窗口），
                    // 从而真正走一遍设计器布局与 VideoView 的 libvlc 绑定。
                    var handle = form.Handle;
                    form.CreateControl();

                    Log(5, "MainForm 构造成功，句柄 = 0x" + handle.ToInt64().ToString("X"));
                    Log(5, "客户区 = " + form.ClientSize.Width + "x" + form.ClientSize.Height
                            + "，DeviceDpi = " + form.DeviceDpi);

                    // OnLoad 中的窗口尺寸恢复 / 工作区钳制 / 分隔条计算只有真正显示才会执行。
                    form.Show();
                    PumpMessages(600);

                    var area = Screen.FromControl(form).WorkingArea;
                    Log(6, "窗口边界 = " + form.Bounds + "，屏幕工作区 = " + area);

                    if (form.Width > area.Width || form.Height > area.Height)
                    {
                        Log(6, "窗口尺寸超出了屏幕工作区");
                        return false;
                    }

                    if (!CheckTransportLayout(form)) return false;
                    if (!CheckWndProcRobustness(form)) return false;
                    if (!CheckPlaylistSearch(form)) return false;
                    if (!CheckPlaylistSort(form)) return false;
                    if (!CheckPlaylistSummary(form)) return false;
                    if (!CheckDurationsBeforeHandle()) return false;
                    if (!CheckThemes(form)) return false;

                    // 放在最后：它按 Delete 会真的把列表项删掉一项，别影响前面的检查
                    if (!CheckSearchTyping(form)) return false;

                    // 关闭会走完整的 FormClosing / FormClosed 流程。
                    Log(6, "准备关闭窗体…");
                    form.Close();
                    Log(6, "窗体已关闭");
                    PumpMessages(200);
                }

                // 关窗时"界面 → 设置"的那一段镜像，单独再摆一次状态验（它自己会还原设置）
                if (!CheckSessionMirror()) return false;

                // A1：启动恢复上次列表时不做同步磁盘查询，打不开的交给后台核对标出来
                if (!CheckRestoreSessionOptimistic()) return false;

                // A2：全局媒体键"哪几个没注册上"要说清是哪一个
                if (!CheckMediaKeyReporting()) return false;

                Log(6, "MainForm 显示 / 关闭正常（设置位置：" + AppSettings.SettingsFilePath + "）");
                return true;
            }
            catch (Exception ex)
            {
                Log(6, "MainForm 显示 / 释放失败: " + ex);
                return false;
            }
            finally
            {
                foreach (var file in files)
                    TryDelete(file);
            }
        }

        /// <summary>
        /// 时长扫描在<b>构造函数里</b>就开跑了，结果可能早于窗口句柄回到主窗体。
        /// <para>
        /// 以前这一支是直接 <c>return</c>（"句柄还没建好，丢掉"）：那条时长就永久丢了，
        /// 界面上表现为<b>某一项一直是 --:--</b>。它只在"扫描比建窗口快"的时候出现，
        /// 所以是抽风式复现——实测三次里红两次（标题里的"（1 项未知）"）。
        /// </para>
        /// <para>
        /// 这里刻意<b>不碰</b>句柄：先把扫描跑完、再要句柄，把那个时间窗固定下来。
        /// 判据是"所有条目最终都有时长"，而不是"走了哪条代码路径"。
        /// </para>
        /// </summary>
        private static bool CheckDurationsBeforeHandle()
        {
            var files = new List<string>();

            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(AppContext.BaseDirectory, $"prehandle-{i}.wav");
                WriteWav(path, seconds: i + 1, frequency: 500 + i * 50);
                files.Add(path);
            }

            try
            {
                using var form = new 播放器.MainForm(files.ToArray());

                // 关键：这里不访问 form.Handle，让后台扫描先跑完
                PumpMessages(1500);

                var handleExisted = form.IsHandleCreated;

                var playlist = PlaylistOf(form);

                var before = playlist.Items.Count(item => item.Duration.HasValue);

                // 现在才要句柄：OnHandleCreated 应当把攒下的结果补上
                var handle = form.Handle;
                PumpMessages(400);

                var after = playlist.Items.Count(item => item.Duration.HasValue);

                if (after != files.Count)
                {
                    Log(6, $"时长回填检查：句柄就绪前解析好的时长丢了"
                            + $"（{before} → {after} / {files.Count} 项；句柄此前是否已建：{handleExisted}，句柄 0x{handle.ToInt64():X}）");
                    return false;
                }

                Log(6, $"时长回填正常：句柄就绪前解析好的 {files.Count} 项一条都没丢"
                        + $"（句柄建好前后都能补上；这次构造期间是否已建句柄：{handleExisted}）");
                return true;
            }
            catch (Exception ex)
            {
                Log(6, "时长回填检查失败: " + ex);
                return false;
            }
            finally
            {
                foreach (var file in files) TryDelete(file);
            }
        }

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

        /// <summary>把 "mm:ss" / "h:mm:ss" 解析成秒；无法解析返回 -1。</summary>
        private static long ParseClock(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return -1;

            var parts = text.Trim().Split(':');
            if (parts.Length < 2 || parts.Length > 3) return -1;

            long total = 0;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var value) || value < 0) return -1;
                total = total * 60 + value;
            }

            return total;
        }

        /// <summary>从 "播放列表（N）· 总计 h:mm:ss" 里取出总时长（秒）。</summary>
        private static long ParseHeaderTotal(string headerText)
        {
            const string marker = "总计 ";
            var index = headerText.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return -1;

            var rest = headerText.Substring(index + marker.Length);
            var cut = rest.IndexOf('（');
            if (cut >= 0) rest = rest.Substring(0, cut);

            return ParseClock(rest.Trim());
        }

        private static string FormatClock(long seconds) =>
            $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}";

        /// <summary>
        /// 传送栏布局回归检查。
        /// <para>
        /// 挡的是这个真实出现过的问题：WinForms 的 <see cref="TrackBar"/> 的 AutoSize 默认为 true，
        /// 会完全忽略设计器里设置的 Height。结果进度条实际高度从 34 涨到 56，
        /// 一头盖住下面的音量行，音量条本身还被传送栏裁掉一截。
        /// </para>
        /// </summary>
        private static bool CheckTransportLayout(Form form)
        {
            var transport = Find(form, "pnlTransport");
            var seek = Find(form, "trackSeek");
            var volume = Find(form, "trackVolume");
            var timeLabel = Find(form, "lblCurrentTime");
            var totalLabel = Find(form, "lblTotalTime");
            var volumeCaption = Find(form, "lblVolumeCaption");

            if (transport == null || seek == null || volume == null ||
                timeLabel == null || totalLabel == null || volumeCaption == null)
            {
                Log(6, "传送栏布局检查：找不到预期的控件");
                return false;
            }

            if (seek.AutoSize || volume.AutoSize)
            {
                Log(6, "传送栏布局检查：滑块控件的 AutoSize 必须为 false，否则设计高度会被忽略");
                return false;
            }

            if (seek.Bottom > volume.Top || seek.Bottom > volumeCaption.Top)
            {
                Log(6, $"传送栏布局检查：进度条盖住了音量行（进度条底边 {seek.Bottom}，音量行顶边 {volumeCaption.Top}）");
                return false;
            }

            if (volume.Bottom > transport.Height)
            {
                Log(6, $"传送栏布局检查：音量条超出传送栏（底边 {volume.Bottom} > 面板高 {transport.Height}）");
                return false;
            }

            if (timeLabel.Right > seek.Left || seek.Right > totalLabel.Left)
            {
                Log(6, "传送栏布局检查：时间标签与进度条重叠");
                return false;
            }

            Log(6, $"传送栏布局正常（进度条 {seek.Width}x{seek.Height}，音量条 {volume.Width}x{volume.Height}）");
            return true;
        }

        /// <summary>
        /// 逐套切换主题，验证菜单接线正常、配色确实落到了控件上，且不抛异常。
        /// 结束后会把主题恢复成本次运行前的选择，避免测试改动使用者的设置。
        /// </summary>
        private static bool CheckThemes(Form form)
        {
            var strip = form.Controls.OfType<MenuStrip>().FirstOrDefault();
            var view = strip?.Items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "视图(&V)");
            var themeMenu = view?.DropDownItems.OfType<ToolStripMenuItem>()
                .FirstOrDefault(i => i.Text == "主题(&T)");

            if (themeMenu == null)
            {
                Log(6, "主题检查：找不到「视图 → 主题」菜单");
                return false;
            }

            var items = themeMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();
            var original = items.FirstOrDefault(i => i.Checked);

            foreach (var id in new[] { ThemeId.Light, ThemeId.Dark, ThemeId.Oled, ThemeId.Vlc, ThemeId.Auto })
            {
                var name = Themes.DisplayName(id);
                var item = items.FirstOrDefault(i => i.Text == name);
                if (item == null)
                {
                    Log(6, "主题检查：菜单里缺少「" + name + "」");
                    return false;
                }

                item.PerformClick();
                PumpMessages(150);

                var palette = Themes.Resolve(id);
                var listView = Find(form, "listViewPlaylist");
                var slider = Find(form, "trackSeek");

                if (!item.Checked)
                {
                    Log(6, $"主题检查：切到「{name}」后菜单勾选没有更新");
                    return false;
                }

                if (form.BackColor != palette.WindowBack)
                {
                    Log(6, $"主题检查：「{name}」的窗体底色未生效（实际 {form.BackColor}，期望 {palette.WindowBack}）");
                    return false;
                }

                if (listView.BackColor != palette.ListBack)
                {
                    Log(6, $"主题检查：「{name}」的列表底色未生效");
                    return false;
                }

                if (slider != null && slider.BackColor != palette.TransportBack)
                {
                    Log(6, $"主题检查：「{name}」的传送栏配色未落到滑块上");
                    return false;
                }
            }

            // 恢复原选择
            if (original != null)
            {
                original.PerformClick();
                PumpMessages(150);
            }

            var names = string.Join(" / ", new[] { "跟随系统", "浅色", "深色", "OLED 纯黑", "VLC 橙" });
            Log(6, $"5 套主题切换正常，配色均已生效：{names}");
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

        /// <summary>
        /// A1：启动恢复上次的播放列表时<b>不做同步磁盘查询</b>。
        /// <para>
        /// 判据里不含计时（那会在慢机器上抖）：构造函数返回时，那条"已经不存在"的路径
        /// <b>仍然在列表里</b>——旧实现在这一步同步 <c>File.Exists</c>，把它过滤掉了，
        /// 而网络盘离线时每个路径还要等一次 SMB 超时（"双击之后半天不出窗口"）。
        /// 打不开的条目改由后台核对标出来（同一个检查的后半段）。
        /// </para>
        /// </summary>
        private static bool CheckRestoreSessionOptimistic()
        {
            var root = Path.Combine(AppContext.BaseDirectory, "smoke-session");
            TryDeleteDirectory(root);
            Directory.CreateDirectory(root);

            var existing = Path.Combine(root, "在的.mp3");
            File.WriteAllBytes(existing, new byte[] { 0x49, 0x44, 0x33, 3 });

            var missing = Path.Combine(root, "不在的.mp3");
            var stream = "http://127.0.0.1:9/连不上也不要紧.mp3";

            var settings = AppSettings.Load();
            var previousPlaylist = settings.LastPlaylist;
            var previousIndex = settings.LastPlaylistIndex;

            try
            {
                settings.LastPlaylist = new List<string> { existing, missing, stream };
                settings.LastPlaylistIndex = -1;
                settings.Save();

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    var playlist = PlaylistOf(form);

                    // 构造函数返回时三条都要在（含不存在的那条）
                    if (playlist.Count != 3)
                    {
                        Log(6, $"启动恢复检查：构造函数返回时列表里有 {playlist.Count} 项，期望 3"
                                + "（打不开的那条不该在启动时被过滤掉，也不该在启动时去查磁盘）");
                        return false;
                    }

                    form.Show();
                    PumpMessages(900);      // 等后台核对

                    var missingItem = playlist.Items.FirstOrDefault(item => item.FilePath == missing);
                    var existingItem = playlist.Items.FirstOrDefault(item => item.FilePath == existing);
                    var streamItem = playlist.Items.FirstOrDefault(item => item.FilePath == stream);

                    if (missingItem == null)
                    {
                        Log(6, "启动恢复检查：不存在的那条从列表里消失了");
                        return false;
                    }

                    if (!missingItem.HasError || missingItem.ErrorMessage != "文件不存在或无法访问")
                    {
                        Log(6, "启动恢复检查：后台核对没有把打不开的那条标出来"
                                + $"（HasError={missingItem.HasError}、原因「{missingItem.ErrorMessage ?? "（空）"}」）");
                        return false;
                    }

                    if (existingItem == null || existingItem.HasError)
                    {
                        Log(6, "启动恢复检查：明明在的那条被标成了打不开");
                        return false;
                    }

                    if (streamItem == null || streamItem.HasError)
                    {
                        Log(6, "启动恢复检查：网络串流被当成「文件不存在」标了（串流本来就不查磁盘）");
                        return false;
                    }

                    if (!ReadStatus(form).Contains("打不开", StringComparison.Ordinal))
                    {
                        Log(6, $"启动恢复检查：标出来了但状态栏没说（「{ReadStatus(form)}」）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(200);
                }

                Log(6, "启动恢复检查正常：上次的列表原样恢复（含已经打不开的那条、不做同步磁盘查询），"
                        + "打不开的由后台核对标出来并在状态栏说明，在的与网络串流都不误标");
                return true;
            }
            finally
            {
                // 这一步改过设置文件里的"上次列表"，必须还原：
                // 后面的步骤会从设置里恢复会话，留着这几条（其中一条不存在）会污染别人
                var restore = AppSettings.Load();
                restore.LastPlaylist = previousPlaylist;
                restore.LastPlaylistIndex = previousIndex;
                restore.Save();

                TryDeleteDirectory(root);
            }
        }

        /// <summary>
        /// A2：全局媒体键"哪几个没注册上"要写清名字。
        /// <para>
        /// 两段：纯函数（缺哪几个就点哪几个的名字）+ <b>真实那条路</b>——测试自己先用
        /// <c>RegisterHotKey</c> 占住其中一个键（同一个虚拟键系统只允许一个窗口注册），
        /// 再开主窗体，它必然少注册一个，必须报出那一个的名字。
        /// </para>
        /// </summary>
        private static bool CheckMediaKeyReporting()
        {
            var all = MediaKeys.Requested.Select(key => key.Id).ToList();

            if (MediaKeys.DescribeMissing(all) != null)
            {
                Log(6, "媒体键检查：四个都注册上了，却说有没注册上的");
                return false;
            }

            var target = MediaKeys.Requested[2];        // 下一首
            var withoutTarget = all.Where(id => id != target.Id).ToList();

            if (MediaKeys.DescribeMissing(withoutTarget) is not { } partial ||
                !partial.Contains(target.Name, StringComparison.Ordinal))
            {
                Log(6, $"媒体键检查：缺了「{target.Name}」但提示里没写出名字"
                        + $"（「{MediaKeys.DescribeMissing(withoutTarget) ?? "（什么都没有）"}」）");
                return false;
            }

            if (MediaKeys.DescribeMissing(Array.Empty<int>()) is not { } none ||
                !none.Contains("一个都没注册上", StringComparison.Ordinal))
            {
                Log(6, "媒体键检查：一个都没注册上时，提示没写清是全部失败");
                return false;
            }

            // 注册发生在建句柄的时候（InitializeSystemIntegration），所以先把开关写进设置
            var settings = AppSettings.Load();
            settings.GlobalMediaKeys = true;
            settings.Save();

            using var probe = new Form { ShowInTaskbar = false, Opacity = 0 };
            probe.Show();
            PumpMessages(150);

            const int probeHotKeyId = 0x7F01;   // 测试自己的 id，和媒体键的 0x9E0x 段不冲突

            if (!RegisterHotKey(probe.Handle, probeHotKeyId, 0, (uint)target.VirtualKey))
            {
                Log(6, $"媒体键检查：「{target.Name}」在这台机器上已经被别的程序占着，"
                        + "抢占那条路这次跳过（纯函数那几条照旧生效）");
                return true;
            }

            try
            {
                using var victim = new 播放器.MainForm(Array.Empty<string>());

                victim.Show();
                PumpMessages(450);

                var warning = victim.MediaKeyWarning;

                if (victim.RegisteredMediaKeys.Contains(target.Id))
                {
                    Log(6, $"媒体键检查：明明被测试占着，「{target.Name}」却被算成注册成功了");
                    return false;
                }

                if (warning == null || !warning.Contains(target.Name, StringComparison.Ordinal))
                {
                    Log(6, "媒体键检查：自己占住一个媒体键之后，主窗体没有说清是哪一个没注册上"
                            + $"（提示「{warning ?? "（什么都没有）"}」、成功 {victim.RegisteredMediaKeys.Count}/4）");
                    return false;
                }

                // 关窗会注销掉那些键，所以这个数要在关之前取
                var registeredCount = victim.RegisteredMediaKeys.Count;

                victim.Close();
                PumpMessages(200);

                Log(6, $"媒体键检查正常：占住「{target.Name}」之后主窗体报出了它的名字"
                        + $"（当时成功注册 {registeredCount}/4，提示："
                        + (warning.Length > 60 ? warning.Substring(0, 60) + "…" : warning) + "）");
                return true;
            }
            finally
            {
                UnregisterHotKey(probe.Handle, probeHotKeyId);
            }
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

        /// <summary>
        /// 播放记忆的判定规则。
        /// <para>
        /// 刻意用 <c>new PlaybackHistory()</c> 而不是 <c>Load()</c>，避免动到使用者真实的 history.json。
        /// </para>
        /// </summary>
        private static bool CheckPlaybackHistory()
        {
            var history = new PlaybackHistory();

            const string normal = @"C:\media\album\a.mp3";
            const string tooEarly = @"C:\media\album\b.mp3";
            const string nearlyDone = @"C:\media\album\c.mp3";

            history.Record(normal, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10));
            if (!history.TryGetResumePosition(normal, out var position) ||
                Math.Abs(position.TotalSeconds - 180) > 0.5)
            {
                Log(8, "播放历史：正常进度没有取回续播位置");
                return false;
            }

            // 开头十几秒不值得续播
            history.Record(tooEarly, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
            if (history.TryGetResumePosition(tooEarly, out _))
            {
                Log(8, "播放历史：刚开头几秒不应该续播");
                return false;
            }

            // 已经播到结尾附近，当作看完，下次从头播
            history.Record(nearlyDone, TimeSpan.FromSeconds(595), TimeSpan.FromMinutes(10));
            if (history.TryGetResumePosition(nearlyDone, out _))
            {
                Log(8, "播放历史：接近结尾时不应该续播");
                return false;
            }

            // 网络流不记录（地址没有稳定标识）
            history.Record("https://example.com/live.m3u8", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
            if (history.TryGetResumePosition("https://example.com/live.m3u8", out _))
            {
                Log(8, "播放历史：网络流不应该被记录");
                return false;
            }

            if (history.Recent.Count != 3)
            {
                Log(8, $"播放历史：最近播放应为 3 条，实际 {history.Recent.Count}");
                return false;
            }

            if (history.Recent[0] != nearlyDone)
            {
                Log(8, "播放历史：最近播放没有按时间倒序");
                return false;
            }

            // 清进度但保留最近播放
            history.ClearPositions();
            if (history.TryGetResumePosition(normal, out _))
            {
                Log(8, "播放历史：清除进度后仍能取回续播位置");
                return false;
            }

            if (history.Recent.Count != 3)
            {
                Log(8, "播放历史：清除进度不应该影响最近播放列表");
                return false;
            }

            // 清空
            history.Clear();
            if (history.Count != 0 || history.Recent.Count != 0)
            {
                Log(8, "播放历史：清空后仍有残留记录");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 按文件记住的调整（音画 / 字幕延迟、歌词偏移）。
        /// <para>
        /// 这三样和播放进度存在同一个 <c>history.json</c>，但规矩不同：
        /// 进度定期落盘就行，调整是用户一项项调出来的，<b>改一次就该落地</b>。
        /// 以前只有歌词偏移这么做，两个延迟要等 20 秒的定时器（写文件的定时器还只在放歌时才走），
        /// 所以"调完延迟马上关程序"会丢——这一段以前也没有任何检查。
        /// </para>
        /// </summary>
        private static bool CheckPerFileAdjustments()
        {
            const string a = @"C:\media\album\adjust-a.mp4";
            const string b = @"C:\media\album\adjust-b.mp4";
            const string stream = "https://example.com/live.m3u8";

            // ---- 内存里的规矩：三样各记各的、读不建壳，不用碰磁盘 ----
            // （刻意不碰磁盘：落盘是"另一次写入在路上就并进下一次"的合并写，
            //   连着记两笔时第二笔会等下一次调用，用它来验语义会验成时序。）
            var store = new PlaybackHistory();
            var adjustments = new PerFileAdjustments(store);

            if (!adjustments.Get(a).IsEmpty || store.Count != 0)
            {
                Log(8, $"按文件调整：读一个没记过的文件就建了记录（现在 {store.Count} 条）");
                return false;
            }

            adjustments.RecordDelays(a, 150, -250);
            adjustments.RecordLyricsOffset(b, 500);

            var fromA = adjustments.Get(a);
            var fromB = adjustments.Get(b);

            if (fromA.AudioDelayMilliseconds != 150 || fromA.SubtitleDelayMilliseconds != -250 ||
                fromA.LyricsOffsetMilliseconds != 0)
            {
                Log(8, "按文件调整：A 的音画 / 字幕延迟没记对"
                        + $"（{fromA.AudioDelayMilliseconds} / {fromA.SubtitleDelayMilliseconds}"
                        + $" / 歌词 {fromA.LyricsOffsetMilliseconds}）");
                return false;
            }

            if (fromB.LyricsOffsetMilliseconds != 500 ||
                fromB.AudioDelayMilliseconds != 0 || fromB.SubtitleDelayMilliseconds != 0)
            {
                Log(8, $"按文件调整：B 的歌词偏移没记对，或者串到别的文件上了（{fromB.LyricsOffsetMilliseconds}）");
                return false;
            }

            // 网络流没有稳定标识，记了也没意义（和播放进度同一条规矩）
            adjustments.RecordDelays(stream, 300, 300);

            if (!adjustments.Get(stream).IsEmpty)
            {
                Log(8, "按文件调整：网络流也被记下来了");
                return false;
            }

            // 空路径不该炸
            adjustments.RecordLyricsOffset(null, 900);

            // 「清除最近播放」清的是历史，不该顺手把用户一项项调出来的东西也清掉
            store.Clear();

            var clearedA = adjustments.Get(a);

            if (clearedA.AudioDelayMilliseconds != 150 || clearedA.SubtitleDelayMilliseconds != -250)
            {
                Log(8, "按文件调整：「清除最近播放」把音画 / 字幕延迟也清掉了"
                        + $"（{clearedA.AudioDelayMilliseconds} / {clearedA.SubtitleDelayMilliseconds}）");
                return false;
            }

            if (adjustments.Get(b).LyricsOffsetMilliseconds != 500)
            {
                Log(8, "按文件调整：「清除最近播放」把歌词偏移也清掉了");
                return false;
            }

            // ---- 改一次就该落盘：这一条要碰磁盘（用一条自己的路径，免得和别的检查打架）----
            // 这条路径只是 history.json 里的一个键，不需要真的存在文件——
            // 所以别把它挪到 smoke-tags 之类的目录下（那是第 9 步才建的，而这里在第 4 步跑）
            var diskPath = Path.Combine(AppContext.BaseDirectory, "adjust-eager.mp4");
            var onDiskHistory = PlaybackHistory.Load();

            new PerFileAdjustments(onDiskHistory).RecordDelays(diskPath, 150, -250);

            if (!WaitForAdjustmentsOnDisk(diskPath, 150, -250, 5))
            {
                Log(8, "按文件调整：改了延迟没有马上落盘（等定时器的话，用户当场关程序就丢了）");
                return false;
            }

            // 收尾：这条记录不该留在 history.json 里影响后面的检查
            onDiskHistory.Forget(diskPath);
            onDiskHistory.Save();

            Log(8, "按文件记住的调整正常：音画 / 字幕延迟与歌词偏移各记各的、读不建壳、"
                    + "网络流不记、改一次就已经落盘、「清除最近播放」不碰它们");
            return true;
        }

        /// <summary>等后台那次落盘把值写进磁盘（刻意不主动 Save，验的就是"改一次就落盘"）。</summary>
        private static bool WaitForAdjustmentsOnDisk(string path, long audio, long subtitle, int seconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);

            while (DateTime.UtcNow < deadline)
            {
                var fromDisk = new PerFileAdjustments(PlaybackHistory.Load()).Get(path);

                if (fromDisk.AudioDelayMilliseconds == audio &&
                    fromDisk.SubtitleDelayMilliseconds == subtitle)
                    return true;

                Thread.Sleep(50);
            }

            return false;
        }

        /// <summary>
        /// 退出时"界面 → 设置"的镜像：摆在界面上的状态，关窗时要原样写进 <c>settings.json</c>。
        /// <para>
        /// 这一段以前没人验（只有「当前歌单名」靠第 17 步间接验到）。它的失败方式很隐蔽：
        /// <b>改了设置，重启却没生效</b>——只有用户撞见才知道。加一个设置项时忘了在
        /// <c>SaveSession</c> 里补一行，就是这个结果。
        /// </para>
        /// <para>
        /// 只验不依赖真实媒体的那几项（音量 / 静音 / 置顶 / 侧栏页 / 窗口位置与大小 / 分隔条 /
        /// 播放列表）；速率与纵横比要真的在放媒体才有意义，那两项由第 13 步在真视频上验。
        /// </para>
        /// </summary>
        private static bool CheckSessionMirror()
        {
            // 先记下现在的设置：这一段会把 AlwaysOnTop、播放列表等摆成别的样子，
            // 验完要还原——后面还有十几步会各自建窗体读这份设置
            var defaults = AppSettings.Load();
            var files = new List<string>();

            for (var i = 1; i <= 3; i++)
            {
                // 直接写在程序目录，不借 smoke-tags：那个目录是第 9 步建的，
                // 而这一段在第 6 步跑——干净树上还没有它（这个坑是干净树验证抓出来的：
                // 以前能过，只是因为工作目录里留着上一次的 smoke-tags）
                var path = Path.Combine(AppContext.BaseDirectory, $"session-{i}.mp3");
                File.WriteAllBytes(path, new byte[] { 0x49, 0x44, 0x33, (byte)i });
                files.Add(path);
            }

            try
            {
                using var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(300);

                var volume = Find(form, "trackVolume");
                var sidebar = Find(form, "sidebarPanel");
                var split = Find(form, "splitVideo") as SplitContainer;

                if (volume == null || sidebar == null || split == null)
                {
                    Log(6, "会话镜像检查：找不到音量条 / 侧栏 / 分隔条");
                    return false;
                }

                // 摆一个"和默认值明显不一样"的状态
                ((FlatSlider)volume).Value = 42;
                ((SidebarPanel)sidebar).ActiveTab = SidebarTab.Info;
                form.TopMost = true;
                form.Bounds = new Rectangle(form.Bounds.X, form.Bounds.Y, 1280, 800);

                form.SetMute(true);

                split.SplitterDistance = Math.Max(split.Panel1MinSize, split.SplitterDistance - 40);

                var playlist = PlaylistOf(form);
                playlist.Clear();
                playlist.AddRange(files);
                playlist.SetCurrent(2);

                var bounds = form.Bounds;
                var splitter = split.SplitterDistance;
                var paths = playlist.GetPaths();
                var index = playlist.CurrentIndex;

                // 关窗 → OnFormClosingInternal → SaveSession
                form.Close();
                PumpMessages(200);

                var saved = AppSettings.Load();

                if (saved.Volume != 42)
                {
                    Log(6, $"会话镜像检查：音量没写进设置（设 42，写出来 {saved.Volume}）");
                    return false;
                }

                if (!saved.Muted)
                {
                    Log(6, "会话镜像检查：静音状态没写进设置");
                    return false;
                }

                if (!saved.AlwaysOnTop)
                {
                    Log(6, "会话镜像检查：置顶状态没写进设置");
                    return false;
                }

                if (saved.SidebarPage != SidebarTab.Info)
                {
                    Log(6, $"会话镜像检查：侧栏停在的页没写进设置（写出来 {saved.SidebarPage}）");
                    return false;
                }

                if (saved.WindowX != bounds.X || saved.WindowY != bounds.Y ||
                    saved.WindowWidth != bounds.Width || saved.WindowHeight != bounds.Height)
                {
                    Log(6, "会话镜像检查：窗口位置 / 大小没写进设置"
                            + $"（界面 {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}；"
                            + $"设置 {saved.WindowX},{saved.WindowY} {saved.WindowWidth}x{saved.WindowHeight}）");
                    return false;
                }

                if (saved.SplitterDistance != splitter)
                {
                    Log(6, $"会话镜像检查：分隔条位置没写进设置（界面 {splitter}，写出来 {saved.SplitterDistance}）");
                    return false;
                }

                if (saved.SidebarWidth <= 0)
                {
                    Log(6, $"会话镜像检查：侧栏宽度没写进设置（{saved.SidebarWidth}）");
                    return false;
                }

                if (saved.LastPlaylistIndex != index || !saved.LastPlaylist.SequenceEqual(paths))
                {
                    Log(6, "会话镜像检查：播放列表 / 当前项没写进设置"
                            + $"（界面 {index} 项号、{paths.Count} 项；设置 {saved.LastPlaylistIndex}、{saved.LastPlaylist.Count} 项）");
                    return false;
                }

                Log(6, "会话镜像正常：音量 / 静音 / 置顶 / 侧栏页 / 窗口位置与大小 / 分隔条 / 播放列表都写进了设置");
                return true;
            }
            finally
            {
                foreach (var file in files) TryDelete(file);

                defaults.Save();
            }
        }

        /// <summary>
        /// 窗口消息处理的健壮性。
        /// <para>
        /// 挡的是一个真实发生过的崩溃：<c>WndProc</c> 曾经对<b>每一条</b>消息都调用
        /// <c>m.WParam.ToInt32()</c>。64 位下 wParam / lParam 经常是句柄甚至指针，
        /// 值超出 Int32 范围，于是抛 <c>OverflowException</c> 直接弹错误框。
        /// 正确做法是先按消息类型分流、再用 <c>ToInt64()</c> 取值。
        /// </para>
        /// </summary>
        private static bool CheckWndProcRobustness(Form form)
        {
            var main = form as 播放器.MainForm;

            if (main == null)
            {
                Log(6, "窗口消息检查：这不是主窗体");
                return false;
            }

            var huge = unchecked((long)0xFFFFFFFF00000000);
            var highPointer = unchecked((long)0x7FFFFFFF00000000);

            var cases = new[]
            {
                (Msg: 0x0111, WParam: huge, LParam: 0L, Name: "WM_COMMAND"),
                (Msg: 0x0312, WParam: huge, LParam: 0L, Name: "WM_HOTKEY"),
                (Msg: 0x0319, WParam: 0L, LParam: highPointer, Name: "WM_APPCOMMAND"),
                (Msg: 0x001A, WParam: huge, LParam: highPointer, Name: "WM_SETTINGCHANGE"),
                (Msg: 0x0401, WParam: huge, LParam: highPointer, Name: "WM_USER+1")
            };

            foreach (var item in cases)
            {
                var message = Message.Create(
                    form.Handle, item.Msg, new IntPtr(item.WParam), new IntPtr(item.LParam));

                try
                {
                    main.ProcessWindowMessage(ref message);
                }
                catch (Exception ex)
                {
                    Log(6, $"窗口消息检查：{item.Name} 带大句柄参数时抛了 "
                           + $"{ex.GetType().Name}：{ex.Message}");
                    return false;
                }
            }

            Log(6, "窗口消息处理在 64 位大句柄 / 指针参数下不会溢出");
            return true;
        }
    }
}
