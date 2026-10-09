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

                // B5：播放中拖动进度条——拖动期间界面显示的是"要跳到的位置"，松手才跳
                if (!CheckSeekBarDragging()) return false;

                // 1.3.0 A-1：A-B 循环——越过 B 点（含正好到 B）跳回 A，越界设点如实拒绝
                if (!CheckAbLoop()) return false;

                // 1.3.0 A-2：音量均衡——选项串按开关拼，切换会重载当前这一首
                if (!CheckNormalizeVolume()) return false;

                // 1.3.0 A-3：「下一首播放」队列——插播时列表当前项不动，插播完回到列表继续
                if (!CheckPlayNextQueue()) return false;

                // 1.3.0：只 Dispose 没 Close 的表单必须把计时器停掉（僵尸会写旧历史）
                if (!CheckDisposedFormStopsTimers()) return false;

                // 1.3.0 C-1：系统媒体控件（音量弹窗里那一块）——喂进去的曲名 / 状态读得回来
                if (!CheckSystemMediaControls()) return false;

                // 启动是否恢复上次的播放列表：默认关（打开就是空列表），开关打开才恢复
                if (!CheckStartupPlaylistPreference()) return false;

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
        /// B5：播放中拖动进度条——拖动期间"当前时间"必须显示**要跳到的位置**，
        /// 滑块取值不能被界面计时器抢回去，引擎也不能被中途 seek；松手才真正跳。
        /// <para>
        /// 以前 <c>UpdateProgress()</c> 里的 <c>if (!_userSeeking)</c> 只护住了滑块取值，
        /// <b>时间标签在护罩外面</b>：计时器每 200 ms 用真实播放时间把它写回去，
        /// 而拖动时 <c>OnSeekScroll</c> 又把它写成拖动目标——两个写者互相盖，
        /// 用户看到的就是"拖动时反复跳回真实进度"。顺带两条同源毛病：
        /// 按住拖到控件外面不跟手（<c>OnMouseLeave</c> 在捕获期间清了拖动状态）、
        /// 右键点一下也会 seek（<c>_userSeeking</c> 不分按键）。
        /// </para>
        /// <para>
        /// 时序全靠 <c>SendMessage</c> 发鼠标消息（不走合成真鼠标），所以不受"有没有人在动鼠标"影响；
        /// 断言也不拿"播放位置刚好是多少"当基准，只比拖动前后。
        /// </para>
        /// </summary>
        private static bool CheckSeekBarDragging()
        {
            var wav = Path.Combine(AppContext.BaseDirectory, "seek-drag.wav");

            // 60 秒：拖到中间之后还要留出"按住右键 1.5 秒"的余量，不然片子会在第四段之前播完
            // （播完之后引擎时间回到 0，会被误读成"右键把位置拽回去了"）。
            WriteWav(wav, seconds: 60, frequency: 440);

            try
            {
                using var form = new 播放器.MainForm(new[] { wav });
                form.Show();
                PumpMessages(400);

                var slider = Find(form, "trackSeek") as FlatSlider;
                var label = Find(form, "lblCurrentTime");
                var engine = form.Engine;

                if (slider == null || label == null)
                {
                    Log(6, "进度条拖动检查：找不到进度条或时间标签");
                    return false;
                }

                // 等它真的在播、且时长已经知道（拖动位置是按总时长换算的）
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && (engine.Length <= 0 || !engine.IsPlaying)) PumpMessages(100);

                if (engine.Length <= 0 || !engine.IsPlaying)
                {
                    Log(6, $"进度条拖动检查：60 秒的 WAV 没播起来（时长 {engine.Length}，在播 {engine.IsPlaying}）");
                    return false;
                }

                var mid = slider.Width / 2;
                var y = slider.Height / 2;

                // ---- ① 按住拖到中间，然后**保持不动** ----
                SendMouseTo(slider, WmLButtonDown, 10, y, MkLButton);
                SendMouseTo(slider, WmMouseMove, mid, y, MkLButton);
                PumpMessages(120);

                var draggedValue = slider.Value;
                var draggedLabel = label.Text;
                var expectedLabel = TimeFormatter.FormatWithHours(
                    TimeSpan.FromMilliseconds(engine.Length * draggedValue / 1000.0));

                if (Math.Abs(draggedValue - 500) > 20)
                {
                    Log(6, $"进度条拖动检查：拖到中间之后取值不对（{draggedValue}/1000）");
                    return false;
                }

                if (draggedLabel != expectedLabel)
                {
                    Log(6, "进度条拖动检查：拖动时时间标签没有显示要跳到的位置"
                            + $"（「{draggedLabel}」，期望「{expectedLabel}」）");
                    return false;
                }

                var timeBeforeHold = engine.Time;

                PumpMessages(600);          // 够 3 个界面计时器周期

                if (label.Text != draggedLabel)
                {
                    Log(6, "进度条拖动检查：按住不动时时间标签被播放时间盖回去了"
                            + $"（「{draggedLabel}」→「{label.Text}」）");
                    return false;
                }

                if (slider.Value != draggedValue)
                {
                    Log(6, $"进度条拖动检查：按住不动时滑块取值被抢回去了（{draggedValue} → {slider.Value}）");
                    return false;
                }

                var timeAfterHold = engine.Time;

                if (timeAfterHold < timeBeforeHold + 300 || timeAfterHold > timeBeforeHold + 3000)
                {
                    Log(6, "进度条拖动检查：按住不动这 600 ms 里播放位置不对（既没照常往前走、也没跳走才怪）"
                            + $"（{timeBeforeHold} → {timeAfterHold} ms）");
                    return false;
                }

                // ---- ② 按住拖到进度条外面（下方）：仍然要跟手 ----
                var beforeOutside = slider.Value;
                var outsideX = Math.Min(slider.Width - 4, mid + 20);   // 只往右挪一点点：松手之后离片尾还远

                SendMouseTo(slider, WmMouseMove, outsideX, slider.Height + 40, MkLButton);
                SendMouseTo(slider, WmMouseLeave, outsideX, slider.Height + 40);   // 系统在光标离开控件时会发的消息
                PumpMessages(80);

                var afterLeave = slider.Value;

                SendMouseTo(slider, WmMouseMove, Math.Min(slider.Width - 4, outsideX + 40), slider.Height + 40, MkLButton);
                PumpMessages(80);

                if (slider.Value <= beforeOutside || slider.Value <= afterLeave)
                {
                    Log(6, "进度条拖动检查：按住拖到进度条外面之后不跟手了"
                            + $"（{beforeOutside} → 离开后 {afterLeave} → 再移动 {slider.Value}）");
                    return false;
                }

                // ---- ③ 松手：这时候才真的跳 ----
                var releaseValue = slider.Value;
                SendMouseTo(slider, WmLButtonUp, Math.Min(slider.Width - 4, outsideX + 40), slider.Height + 40);
                PumpMessages(500);

                var expectedSeek = (long)(engine.Length * releaseValue / 1000.0);
                var seekedTo = engine.Time;

                if (Math.Abs(seekedTo - expectedSeek) > 800)
                {
                    Log(6, $"进度条拖动检查：松手之后没有跳到目标（在 {seekedTo} ms，期望约 {expectedSeek} ms）");
                    return false;
                }

                // ---- ④ 右键点一下：不许 seek，也不许让界面计时器停摆 ----
                PumpMessages(300);

                var labelsWhileHoldingRight = new List<string>();
                SendMouseTo(slider, WmRButtonDown, slider.Width / 5, y);

                for (var i = 0; i < 15; i++)     // 按住 1.5 秒：这期间显示的时间必然要走过至少一秒
                {
                    PumpMessages(100);

                    if (!labelsWhileHoldingRight.Contains(label.Text)) labelsWhileHoldingRight.Add(label.Text);
                }

                SendMouseTo(slider, WmRButtonUp, slider.Width / 5, y);
                PumpMessages(200);

                if (labelsWhileHoldingRight.Count < 2)
                {
                    Log(6, "进度条拖动检查：按住右键时界面计时器停摆了"
                            + $"（1.5 秒里时间标签一直是「{labelsWhileHoldingRight.FirstOrDefault()}」）");
                    return false;
                }

                if (engine.Time < seekedTo - 300)
                {
                    Log(6, $"进度条拖动检查：右键点进度条把播放位置拽回去了（{seekedTo} → {engine.Time} ms）");
                    return false;
                }

                Log(6, "进度条拖动正常：拖动期间标签显示要跳到的位置、滑块不被抢、引擎不中途 seek，"
                        + "拖出控件仍跟手，松手才跳，右键点一下既不 seek 也不让计时器停摆");
                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>
        /// A-1：A-B 循环。
        /// <para>
        /// 三段：<b>纯逻辑</b>（越界判断的边界——引擎时间<b>正好等于 B</b> 那一拍就该回跳；
        /// 以及 A/B 互比时的拒绝规则）→ <b>真的回跳</b>（起播一段 30 秒的 WAV，
        /// 用「播放 → A-B 循环」里真实的菜单项设点，泵 3 秒看时间是不是一直被压在 B 点以内、
        /// 并且确实跳回过 A 点）→ <b>拒绝 / 清除 / 停止</b>（A 不早于 B、B 不晚于 A 都要如实拒绝
        /// 并写出原因；清除与停止之后不再回跳）。
        /// </para>
        /// <para>
        /// 边界那一条只能写在纯函数上：<c>Time</c> 是浮动的，靠"泵到某一刻再看它落在哪"
        /// 永远碰不到"正好等于 B"，判据里的 <c>&gt;=</c> 被改成 <c>&gt;</c> 也照样能过。
        /// </para>
        /// <para>
        /// 素材用 30 秒而不是计划里写的 6 秒：这一段总共要泵十几秒，6 秒的片子会在中途播完，
        /// 而播完之后引擎时间会回到 0（B5 那一步踩过这个坑），看上去就像"还在回跳"。
        /// </para>
        /// </summary>
        private static bool CheckAbLoop()
        {
            // ---- ① 纯逻辑：越界判断的边界与拒绝规则 ----
            if (!AbLoop.ShouldWrap(2000, 1000, 2000))
            {
                Log(6, "A-B 循环：引擎时间正好等于 B 点时没有回跳（边界判据写成了 >）");
                return false;
            }

            if (AbLoop.ShouldWrap(1999, 1000, 2000))
            {
                Log(6, "A-B 循环：还没到 B 点就回跳了");
                return false;
            }

            if (AbLoop.ShouldWrap(2000, null, 2000) || AbLoop.ShouldWrap(2000, 1000, null))
            {
                Log(6, "A-B 循环：只设了一个点也回跳了");
                return false;
            }

            if (AbLoop.RejectStart(2000, 2000) == null || AbLoop.RejectStart(2500, 1000) == null)
            {
                Log(6, "A-B 循环：A 点不早于 B 点时没有拒绝");
                return false;
            }

            if (AbLoop.RejectStart(500, 1000) != null)
            {
                Log(6, "A-B 循环：A 点明明早于 B 点却被拒绝了");
                return false;
            }

            if (AbLoop.RejectEnd(1000, 1000) == null || AbLoop.RejectEnd(500, 1000) == null)
            {
                Log(6, "A-B 循环：B 点不晚于 A 点时没有拒绝");
                return false;
            }

            if (AbLoop.RejectEnd(2000, 1000) != null)
            {
                Log(6, "A-B 循环：B 点明明晚于 A 点却被拒绝了");
                return false;
            }

            var wav = Path.Combine(AppContext.BaseDirectory, "ab-loop.wav");
            WriteWav(wav, seconds: 30, frequency: 440);

            try
            {
                using var form = new 播放器.MainForm(new[] { wav });
                form.Show();
                PumpMessages(400);

                var engine = form.Engine;
                var setStart = FindAbLoopItem(form, "menuAbSetStart");
                var setEnd = FindAbLoopItem(form, "menuAbSetEnd");
                var clear = FindAbLoopItem(form, "menuAbClear");
                var play = FindTopMenuItem(form, "播放");
                var stop = play == null ? null : FindMenuItem(play.DropDownItems, "停止");

                if (setStart == null || setEnd == null || clear == null || stop == null)
                {
                    Log(6, "A-B 循环：找不到「播放 → A-B 循环」里的菜单项（或「停止」）");
                    return false;
                }

                if (!PumpUntil(() => engine.Length > 0 && engine.IsPlaying, 10000))
                {
                    Log(6, $"A-B 循环：30 秒的 WAV 没播起来（时长 {engine.Length}，在播 {engine.IsPlaying}）");
                    return false;
                }

                // ---- ② 拒绝：B 点设在 A 点之前 ----
                engine.SeekTo(3000);
                if (!PumpUntil(() => engine.Time >= 2900, 4000))
                {
                    Log(6, $"A-B 循环：定位到 3 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setStart.PerformClick();
                var a = form.AbLoopStartMilliseconds;

                if (a == null || a < 2900)
                {
                    Log(6, $"A-B 循环：「把 A 点设在当前时间」没设上（A = {(a.HasValue ? a.Value + " ms" : "null")}，"
                            + $"状态栏：{form.StatusText}）");
                    return false;
                }

                if (!form.StatusText.Contains("B 点还没设"))
                {
                    Log(6, "A-B 循环：只设了 A 点，状态栏却没说明 B 点还没设（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(1000);
                if (!PumpUntil(() => engine.Time < a.Value - 500, 4000))
                {
                    Log(6, $"A-B 循环：定位回 1 秒没成功（现在 {engine.Time} ms，A 点是 {a.Value} ms）");
                    return false;
                }

                setEnd.PerformClick();

                if (form.AbLoopEndMilliseconds != null)
                {
                    Log(6, $"A-B 循环：B 点早于 A 点却被设上了（B = {form.AbLoopEndMilliseconds} ms，A = {a.Value} ms）");
                    return false;
                }

                if (!form.StatusText.Contains("不晚于"))
                {
                    Log(6, "A-B 循环：B 点被拒绝了，但状态栏没说清原因（" + form.StatusText + "）");
                    return false;
                }

                // ---- ③ 拒绝：A 点设在 B 点之后 ----
                clear.PerformClick();

                if (form.AbLoopStartMilliseconds != null || form.AbLoopEndMilliseconds != null)
                {
                    Log(6, "A-B 循环：点了「清除 A-B」，点还在");
                    return false;
                }

                engine.SeekTo(1000);
                if (!PumpUntil(() => engine.Time >= 900 && engine.Time < 2500, 4000))
                {
                    Log(6, $"A-B 循环：定位回 1 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setEnd.PerformClick();
                var b = form.AbLoopEndMilliseconds;

                if (b == null)
                {
                    Log(6, "A-B 循环：先设 B 点没设上（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(3000);
                if (!PumpUntil(() => engine.Time >= 2900, 4000))
                {
                    Log(6, $"A-B 循环：定位到 3 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                if (engine.Time < b.Value + 500)
                {
                    Log(6, $"A-B 循环：只有 B 点没有 A 点的时候回跳了（B = {b.Value} ms，现在 {engine.Time} ms）");
                    return false;
                }

                setStart.PerformClick();

                if (form.AbLoopStartMilliseconds != null || form.AbLoopActive)
                {
                    Log(6, $"A-B 循环：A 点晚于 B 点却被设上了（A = {form.AbLoopStartMilliseconds} ms，B = {b.Value} ms）");
                    return false;
                }

                if (!form.StatusText.Contains("不早于"))
                {
                    Log(6, "A-B 循环：A 点被拒绝了，但状态栏没说清原因（" + form.StatusText + "）");
                    return false;
                }

                // ---- ④ 真的回跳 ----
                clear.PerformClick();

                engine.SeekTo(1000);
                if (!PumpUntil(() => engine.Time >= 900 && engine.Time < 2500, 4000))
                {
                    Log(6, $"A-B 循环：定位回 1 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setStart.PerformClick();
                var loopStart = form.AbLoopStartMilliseconds;

                if (loopStart == null)
                {
                    Log(6, "A-B 循环：A 点没设上（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(loopStart.Value + 1000);
                if (!PumpUntil(() => engine.Time >= loopStart.Value + 900, 4000))
                {
                    Log(6, $"A-B 循环：定位到 A 点后面 1 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setEnd.PerformClick();
                var loopEnd = form.AbLoopEndMilliseconds;

                if (loopEnd == null || !form.AbLoopActive)
                {
                    Log(6, $"A-B 循环：B 点没设上（{form.StatusText}）");
                    return false;
                }

                if (loopEnd.Value - loopStart.Value < 500)
                {
                    Log(6, $"A-B 循环：A/B 两点离得太近（A {loopStart.Value} → B {loopEnd.Value} ms），这段验不出回跳");
                    return false;
                }

                if (!form.StatusText.Contains("跳回 A"))
                {
                    Log(6, "A-B 循环：两个点都设好了，状态栏却没说明「播放到 B 就跳回 A」（" + form.StatusText + "）");
                    return false;
                }

                // 窗口只有约 1 秒：泵 3 秒，不循环的话时间会一路走到 B + 2000 ms 以外。
                var samples = new List<long>();
                var wrappedBack = false;
                var overshoot = 0L;

                for (var i = 0; i < 30; i++)
                {
                    PumpMessages(100);

                    var now = engine.Time;
                    samples.Add(now);

                    if (now - loopEnd.Value > overshoot) overshoot = now - loopEnd.Value;
                    if (samples.Count >= 2 && samples[^2] - now >= 300) wrappedBack = true;
                }

                if (overshoot > 900)
                {
                    Log(6, $"A-B 循环：播放越过了 B 点却没有回跳（最多越到 B + {overshoot} ms，"
                            + $"A {loopStart.Value} → B {loopEnd.Value} ms）");
                    return false;
                }

                if (!wrappedBack)
                {
                    Log(6, $"A-B 循环：3 秒里没看到跳回 A 点（A {loopStart.Value} → B {loopEnd.Value} ms，"
                            + $"采样最少 {samples.Min()}、最多 {samples.Max()} ms）");
                    return false;
                }

                // ---- ⑤ 清除之后不再回跳 ----
                clear.PerformClick();

                if (form.AbLoopActive || form.AbLoopStartMilliseconds != null || form.AbLoopEndMilliseconds != null)
                {
                    Log(6, "A-B 循环：点了「清除 A-B」，点还在");
                    return false;
                }

                var clearedAt = engine.Time;

                if (!PumpUntil(() => engine.Time > loopEnd.Value + 800, 6000))
                {
                    Log(6, $"A-B 循环：清除之后还在回跳（清除时 {clearedAt} ms，现在 {engine.Time} ms，"
                            + $"B 点是 {loopEnd.Value} ms）");
                    return false;
                }

                // ---- ⑥ 停止时清除 ----
                setStart.PerformClick();
                var stopStart = form.AbLoopStartMilliseconds;

                if (stopStart == null)
                {
                    Log(6, "A-B 循环：准备验「停止时清除」时 A 点没设上（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(stopStart.Value + 1000);

                if (!PumpUntil(() => engine.Time >= stopStart.Value + 900, 4000))
                {
                    Log(6, $"A-B 循环：准备验「停止时清除」时定位没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setEnd.PerformClick();

                if (!form.AbLoopActive)
                {
                    Log(6, "A-B 循环：准备验「停止时清除」时两个点没设上（" + form.StatusText + "）");
                    return false;
                }

                stop.PerformClick();

                if (form.AbLoopActive || form.AbLoopStartMilliseconds != null || form.AbLoopEndMilliseconds != null)
                {
                    Log(6, "A-B 循环：停止播放之后循环点还在");
                    return false;
                }

                Log(6, "A-B 循环正常：越过 B 点（含正好到 B 那一拍）跳回 A，"
                        + "A 不早于 B / B 不晚于 A 都如实拒绝并写出原因，清除与停止之后不再回跳");
                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>
        /// A-2：音量均衡（normalize）。
        /// <para>
        /// 两段：<b>纯函数</b>（选项串里该不该有 <c>:audio-filter=normvol</c>）→
        /// <b>真切换</b>（用「播放 → 音量均衡（normalize）」那一项真的点一下：
        /// 路径不变、媒体重载过、位置回到开头、状态栏把"从头开始重播"说出来）。
        /// </para>
        /// <para>
        /// <b>响度效果不做数字断言</b>：进程里量不到输出电平（要量化得给引擎加一条只在测试里用的
        /// PCM 回调，那是另一档工作量）。这里验的是"滤镜确实挂进了媒体选项"，
        /// 效果本身靠耳朵——README 里也是这么写的。
        /// </para>
        /// <para>
        /// "重载过"的判据用<b>采样到的最小位置</b>：不重载的话时间只会往前走（采样最小值仍在 4 秒附近），
        /// 重载之后必然经过 0 秒附近。
        /// </para>
        /// </summary>
        private static bool CheckNormalizeVolume()
        {
            // ---- ① 纯函数：选项串 ----
            var off = MediaOptions.For(isStream: false, normalizeVolume: false);
            var on = MediaOptions.For(isStream: false, normalizeVolume: true);

            if (off.Any(option => option.Contains(MediaOptions.NormalizeAudioFilter, StringComparison.Ordinal)))
            {
                Log(6, "音量均衡：关着的时候选项串里却有 normvol（" + string.Join(" ", off) + "）");
                return false;
            }

            if (!on.Any(option => option == ":audio-filter=" + MediaOptions.NormalizeAudioFilter))
            {
                Log(6, "音量均衡：开着的时候选项串里没有 :audio-filter=normvol（" + string.Join(" ", on) + "）");
                return false;
            }

            if (!on.Any(option => option.StartsWith(":file-caching=", StringComparison.Ordinal)))
            {
                Log(6, "音量均衡：开着的时候把本地文件的缓存选项弄丢了（" + string.Join(" ", on) + "）");
                return false;
            }

            var stream = MediaOptions.For(isStream: true, normalizeVolume: false);

            if (!stream.Any(option => option.StartsWith(":network-caching=", StringComparison.Ordinal)))
            {
                Log(6, "音量均衡：网络流没走网络缓存（" + string.Join(" ", stream) + "）");
                return false;
            }

            var wav = Path.Combine(AppContext.BaseDirectory, "normalize.wav");
            WriteWav(wav, seconds: 30, frequency: 440);

            try
            {
                using var form = new 播放器.MainForm(new[] { wav });
                form.Show();
                PumpMessages(400);

                var engine = form.Engine;
                var menu = form.MenuNormalizeVolume;
                var play = FindTopMenuItem(form, "播放");

                if (menu == null || play == null || !play.DropDownItems.Contains(menu))
                {
                    Log(6, "音量均衡：找不到「播放 → 音量均衡（normalize）」这一项");
                    return false;
                }

                if (!PumpUntil(() => engine.Length > 0 && engine.IsPlaying, 10000))
                {
                    Log(6, $"音量均衡：30 秒的 WAV 没播起来（时长 {engine.Length}，在播 {engine.IsPlaying}）");
                    return false;
                }

                if (!PumpUntil(() => engine.Time >= 3900, 8000))
                {
                    Log(6, $"音量均衡：播放没有推进到 4 秒（现在 {engine.Time} ms）");
                    return false;
                }

                var path = engine.CurrentPath;
                var before = engine.Time;

                // ---- ② 真的点一下（开）----
                menu.PerformClick();

                var statusAfterOn = form.StatusText;

                if (!form.Settings.NormalizeVolume || !engine.NormalizeVolume)
                {
                    Log(6, $"音量均衡：点开了开关，设置 / 引擎却没跟上（设置 {form.Settings.NormalizeVolume}，"
                            + $"引擎 {engine.NormalizeVolume}）");
                    return false;
                }

                if (!statusAfterOn.Contains("从头开始重播"))
                {
                    Log(6, "音量均衡：切换之后状态栏没说明「从头开始重播以应用」（" + statusAfterOn + "）");
                    return false;
                }

                if (!PumpUntil(() => engine.CurrentPath != null && engine.CurrentPath == path, 5000))
                {
                    Log(6, $"音量均衡：切换之后播放的换成了别的文件（{path} → {engine.CurrentPath}）");
                    return false;
                }

                var minAfterOn = engine.Time;

                for (var i = 0; i < 20; i++)
                {
                    PumpMessages(100);
                    if (engine.Time < minAfterOn) minAfterOn = engine.Time;
                }

                if (minAfterOn > before - 2000)
                {
                    Log(6, $"音量均衡：开了均衡之后没有重载媒体（切换前 {before} ms，"
                            + $"切换后采样到的最近位置 {minAfterOn} ms；重载过的话会经过 0 秒附近）");
                    return false;
                }

                // ---- ③ 再点一下（关）----
                menu.PerformClick();

                var statusAfterOff = form.StatusText;

                if (form.Settings.NormalizeVolume || engine.NormalizeVolume)
                {
                    Log(6, $"音量均衡：点关了开关，设置 / 引擎还开着（设置 {form.Settings.NormalizeVolume}，"
                            + $"引擎 {engine.NormalizeVolume}）");
                    return false;
                }

                if (!statusAfterOff.Contains("从头开始重播"))
                {
                    Log(6, "音量均衡：关掉之后状态栏没说明「从头开始重播以应用」（" + statusAfterOff + "）");
                    return false;
                }

                var beforeOff = engine.Time;
                var minAfterOff = beforeOff;

                for (var i = 0; i < 20; i++)
                {
                    PumpMessages(100);
                    if (engine.Time < minAfterOff) minAfterOff = engine.Time;
                }

                if (minAfterOff > beforeOff + 100)
                {
                    Log(6, $"音量均衡：关掉均衡之后没有重载媒体（切换后采样到的最近位置 {minAfterOff} ms，"
                            + $"切换前 {beforeOff} ms）");
                    return false;
                }

                if (engine.CurrentPath != path)
                {
                    Log(6, $"音量均衡：关掉之后播放的不是同一首（{path} → {engine.CurrentPath}）");
                    return false;
                }

                Log(6, "音量均衡正常：选项串按开关正确拼出 :audio-filter=normvol（关着时没有、缓存选项没丢），"
                        + "真切换会重载当前这一首并从头开始（路径不变），状态栏说明「已从头开始重播以应用」");
                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>
        /// A-3：「下一首播放」队列。
        /// <para>
        /// 三段：<b>纯逻辑</b>（入队去重、条数上限、出队顺序）→ <b>两个入口</b>
        /// （列表右键「下一首播放」把选中的行排进队、列表标题前出现 <c>▶</c>；
        /// 「播放 → 清空下一首队列」把队清掉、标记跟着消失）→ <b>真的插播</b>
        /// （起播列表第 1 项、把第 3 项排进队，等<span>自然播完</span>：
        /// 引擎换到第 3 项而 <c>CurrentIndex</c> <b>没动</b>；第 3 项播完再回到列表里第 1 项的下一项）。
        /// </para>
        /// <para>
        /// 素材是 3 秒的小 WAV：这一段就是要等"自然播完"，靠真的 EndReached 触发，
        /// 不自己造事件（造事件等于把被测的那条路绕过去了）。
        /// </para>
        /// </summary>
        private static bool CheckPlayNextQueue()
        {
            // ---- ① 纯逻辑 ----
            var queue = new PlaybackQueue();

            if (queue.Enqueue("a.mp3") != QueueAddResult.Added)
            {
                Log(6, "下一首播放队列：第一次入队没成功");
                return false;
            }

            if (queue.Enqueue("a.mp3") != QueueAddResult.AlreadyQueued || queue.Count != 1)
            {
                Log(6, $"下一首播放队列：重复入队没有去重（队里 {queue.Count} 条）");
                return false;
            }

            if (queue.Enqueue("A.MP3") != QueueAddResult.AlreadyQueued)
            {
                Log(6, "下一首播放队列：同一个文件的路径大小写不同就被排了两遍（Windows 上是一份）");
                return false;
            }

            queue.Enqueue("b.mp3");

            if (queue.Dequeue() != "a.mp3" || queue.Dequeue() != "b.mp3")
            {
                Log(6, "下一首播放队列：出队顺序不是入队顺序");
                return false;
            }

            if (queue.Dequeue() != null || queue.Count != 0)
            {
                Log(6, "下一首播放队列：空队还能取出东西来");
                return false;
            }

            for (var i = 0; i < PlaybackQueue.Capacity; i++) queue.Enqueue("f" + i);

            if (queue.Count != PlaybackQueue.Capacity || queue.Enqueue("over.mp3") != QueueAddResult.Full)
            {
                Log(6, $"下一首播放队列：条数上限不对（最多 {PlaybackQueue.Capacity} 条，队里 {queue.Count} 条）");
                return false;
            }

            if (!queue.Remove("f5") || queue.Remove("never-there.mp3") || queue.Count != PlaybackQueue.Capacity - 1)
            {
                Log(6, "下一首播放队列：Remove 没有正确移掉一条（或移掉了不存在的）");
                return false;
            }

            if (queue.Enqueue("after-remove.mp3") != QueueAddResult.Added)
            {
                Log(6, "下一首播放队列：腾出位置之后还是进不去");
                return false;
            }

            queue.Clear();

            if (queue.Count != 0) { Log(6, "下一首播放队列：Clear 之后还有东西"); return false; }

            // ---- ② 两个入口 + 真的插播 ----
            var first = Path.Combine(AppContext.BaseDirectory, "queue-a.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "queue-b.wav");
            var third = Path.Combine(AppContext.BaseDirectory, "queue-c.wav");

            WriteWav(first, seconds: 3, frequency: 440);
            WriteWav(second, seconds: 3, frequency: 480);
            WriteWav(third, seconds: 3, frequency: 520);

            try
            {
                using var form = new 播放器.MainForm(new[] { first, second, third });
                form.Show();
                PumpMessages(400);

                var engine = form.Engine;
                var playlist = form.Playlist;
                var list = Find(form, "listViewPlaylist") as ListView;

                if (list == null)
                {
                    Log(6, "下一首播放队列：找不到播放列表控件");
                    return false;
                }

                var queueItem = list.ContextMenuStrip == null
                    ? null
                    : FindMenuItem(list.ContextMenuStrip.Items, "下一首播放");

                if (queueItem == null)
                {
                    Log(6, "下一首播放队列：列表右键菜单里没有「下一首播放」");
                    return false;
                }

                if (form.MenuClearQueue == null || form.MenuPlayNext == null)
                {
                    Log(6, "下一首播放队列：「播放」菜单里没有「下一首播放 / 清空下一首队列」");
                    return false;
                }

                var play = FindTopMenuItem(form, "播放");

                if (play == null || !play.DropDownItems.Contains(form.MenuClearQueue) ||
                    !play.DropDownItems.Contains(form.MenuPlayNext))
                {
                    Log(6, "下一首播放队列：那两项没挂在「播放」菜单下");
                    return false;
                }

                if (!PumpUntil(() => engine.IsPlaying && playlist.CurrentIndex == 0, 10000))
                {
                    Log(6, $"下一首播放队列：列表第 1 项没播起来（当前项 {playlist.CurrentIndex}，"
                            + $"在播 {engine.IsPlaying}）");
                    return false;
                }

                // 选中第 3 项，走真正的右键菜单
                list.SelectedIndices.Clear();
                list.Items[2].Selected = true;
                queueItem.PerformClick();
                PumpMessages(120);

                if (form.Queue.Count != 1 || form.Queue.Paths[0] != third)
                {
                    Log(6, $"下一首播放队列：右键「下一首播放」没把第 3 项排进队（队里 {form.Queue.Count} 条，"
                            + $"状态栏：{form.StatusText}）");
                    return false;
                }

                if (!form.StatusText.Contains("已排入"))
                {
                    Log(6, "下一首播放队列：排进队之后状态栏没说（" + form.StatusText + "）");
                    return false;
                }

                if (!list.Items[2].SubItems[1].Text.StartsWith("▶ ", StringComparison.Ordinal))
                {
                    Log(6, "下一首播放队列：排进队的行标题前没有 ▶（「" + list.Items[2].SubItems[1].Text + "」）");
                    return false;
                }

                if (list.Items[0].SubItems[1].Text.StartsWith("▶ ", StringComparison.Ordinal))
                {
                    Log(6, "下一首播放队列：没排进队的行也被标上了 ▶");
                    return false;
                }

                // 清空：走「播放」菜单那一项
                form.MenuClearQueue.PerformClick();
                PumpMessages(120);

                if (form.Queue.Count != 0)
                {
                    Log(6, $"下一首播放队列：「清空下一首队列」没清掉（还剩 {form.Queue.Count} 条）");
                    return false;
                }

                if (list.Items[2].SubItems[1].Text.StartsWith("▶ ", StringComparison.Ordinal))
                {
                    Log(6, "下一首播放队列：清空之后 ▶ 标记还在");
                    return false;
                }

                // 再排一次，这次真的等它播完
                list.SelectedIndices.Clear();
                list.Items[2].Selected = true;
                queueItem.PerformClick();
                PumpMessages(120);

                if (!form.Queue.Contains(third))
                {
                    Log(6, "下一首播放队列：第二次排进队没成功（" + form.StatusText + "）");
                    return false;
                }

                var indexBefore = playlist.CurrentIndex;

                // 判据是"第 1 项播完的那一刻被换成了哪一份媒体"：看队列就该换成排进队的第 3 项，
                // 不看队列就换成列表里的第 2 项。不拿"几秒之内出现"当判据——列表本来就排着第 3 项，
                // 时间放宽一点它自己也会播到那儿（第一版就是这么把变异放过去的）。
                if (!PumpUntil(() => engine.CurrentPath != null &&
                                     !string.Equals(engine.CurrentPath, first, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(6, "下一首播放队列：第 1 项自然播完之后什么都没接着播");
                    return false;
                }

                var nextAfterFirst = engine.CurrentPath;

                if (!string.Equals(nextAfterFirst, third, StringComparison.OrdinalIgnoreCase))
                {
                    Log(6, $"下一首播放队列：第 1 项播完之后播的是「{Path.GetFileName(nextAfterFirst ?? string.Empty)}」，"
                            + $"而不是排进队的「{Path.GetFileName(third)}」（排完队却没被用上）");
                    return false;
                }

                if (playlist.CurrentIndex != indexBefore)
                {
                    Log(6, $"下一首播放队列：插播把列表的当前项也改了（{indexBefore} → {playlist.CurrentIndex}）");
                    return false;
                }

                if (form.Queue.Count != 0)
                {
                    Log(6, $"下一首播放队列：插播开始之后队里还有 {form.Queue.Count} 条（该出队了）");
                    return false;
                }

                if (!PumpUntil(() => engine.CurrentPath != null &&
                                     string.Equals(engine.CurrentPath, second, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(6, "下一首播放队列：插播的那一首播完之后没回到列表里第 1 项的下一项（现在在播 "
                            + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                if (playlist.CurrentIndex != indexBefore + 1)
                {
                    Log(6, $"下一首播放队列：回到列表继续时当前项不对（期望 {indexBefore + 1}，"
                            + $"实际 {playlist.CurrentIndex}）");
                    return false;
                }

                Log(6, "下一首播放队列正常：入队去重、上限 20 条、出队按入队顺序；"
                        + "右键「下一首播放」会打上 ▶、菜单能清空；自然播完时插播队首且列表当前项不动，"
                        + "插播完再回到列表的下一项");
                return true;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                TryDelete(third);
            }
        }

        /// <summary>A-B 循环子菜单里按 <c>Name</c> 找一项：文字里带着"当前时间"，不能按文字找。</summary>
        private static ToolStripMenuItem? FindAbLoopItem(播放器.MainForm form, string name)
        {
            var menu = form.MenuAbLoop;
            if (menu == null) return null;

            foreach (ToolStripItem item in menu.DropDownItems)
            {
                if (item is ToolStripMenuItem menuItem && menuItem.Name == name)
                    return menuItem;
            }

            return null;
        }

        /// <summary>
        /// 只 Dispose 没 Close 的表单必须收干净。
        /// <para>
        /// 这是 1.3.0 查"第 18 步偶发红"时揪出来的：界面计时器以前<b>只在 FormClosed 里停</b>，
        /// 而冒烟里到处是 <c>using var form</c>（只 Dispose、从不 Close），于是那个表单的
        /// 200 ms 计时器<b>一直跑下去</b>——每 200 ms 读一次引擎、每 100 拍（20 秒）把
        /// <b>自己那份旧历史</b>写回 <c>history.json</c>。历史是"一个实例一份内存副本、共写同一个文件"，
        /// 所以僵尸那一下会把别的实例刚写进去的记录盖掉：第 18 步"偏移没写进 history.json"
        /// 就是这么偶发红的（单跑这一步从来是绿的——那会儿还没有僵尸）。
        /// </para>
        /// </summary>
        private static bool CheckDisposedFormStopsTimers()
        {
            var wav = Path.Combine(AppContext.BaseDirectory, "dispose-form.wav");
            WriteWav(wav, seconds: 30, frequency: 440);

            try
            {
                var form = new 播放器.MainForm(new[] { wav });

                try
                {
                    form.Show();
                    PumpMessages(400);

                    if (!form.UiTimerRunning)
                    {
                        Log(6, "只 Dispose 检查：刚打开窗体时界面计时器就没在跑");
                        return false;
                    }

                    PumpUntil(() => form.Engine.HasMedia && form.Engine.IsPlaying, 10000);

                    // 冒烟里最常见的那种用法：只 Dispose，不 Close
                    form.Dispose();
                    PumpMessages(300);

                    if (form.UiTimerRunning)
                    {
                        Log(6, "只 Dispose 的表单还在跑界面计时器"
                                + "（它会每 20 秒把自己那份旧历史写回磁盘，盖掉别人刚写的记录）");
                        return false;
                    }

                    Log(6, "只 Dispose 的表单收尾正常：界面计时器停了"
                            + "（以前它会一直跑，每 20 秒把旧历史写回磁盘）");
                    return true;
                }
                finally
                {
                    form.Dispose();
                }
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>
        /// C-1（1.3.0）：系统媒体控件（SMTC，音量弹窗 / 锁屏里那一块"正在播放"）。
        /// <para>
        /// <b>验到哪一层</b>：这里验的是"接口拿到了、<c>IsEnabled</c> 为真、曲名 / 歌手 / 状态 / 时长
        /// 喂进去之后<b>读得回来</b>"——那几个 getter 是<b>真的去问系统那个对象</b>的，
        /// 不是记我们自己的变量。封面这一版也接上了：验的是"没内嵌图时挑了旁边那张
        /// <c>cover.jpg</c>、交给系统的图片文件系统那边确实读了（<c>CoverSet</c>）、
        /// 切到没有封面的那首会清空"。但<b>系统界面上到底画没画出来，冒烟验不到</b>，只能人眼看一次
        /// （README 与 CHANGELOG 里都写着这一条）。
        /// </para>
        /// <para>
        /// 拿不到接口（老系统 / 被策略禁掉）时<b>打印原因后跳过</b>——照 <c>播放器_TEST_VIDEO</c> 的先例，
        /// 跳过必须出声。
        /// </para>
        /// </summary>
        private static bool CheckSystemMediaControls()
        {
            // 两首各放一个目录：第一首旁边没有封面图，第二首旁边放一张 cover.jpg
            // （外加一张干扰用的 album.jpg）。这样"封面从哪来"这条断言才有靶子
            // ——这两首 WAV 都没有内嵌图。
            var smokeRoot = Path.Combine(AppContext.BaseDirectory, "smoke-smtc");
            var plainFolder = Path.Combine(smokeRoot, "plain");
            var artFolder = Path.Combine(smokeRoot, "art");

            TryDeleteDirectory(smokeRoot);
            Directory.CreateDirectory(plainFolder);
            Directory.CreateDirectory(artFolder);

            var first = Path.Combine(plainFolder, "smtc-a.wav");
            var second = Path.Combine(artFolder, "smtc-b.wav");
            var sidecar = Path.Combine(artFolder, "cover.jpg");

            // 干扰项：按文件名排序 album.jpg 排在 cover.jpg 前面。
            // 挑封面不能看文件系统的枚举顺序，得按"常见命名"的优先级——cover.jpg 赢。
            var rival = Path.Combine(artFolder, "album.jpg");

            WriteWav(first, seconds: 30, frequency: 440);
            WriteWav(second, seconds: 30, frequency: 480);
            File.WriteAllBytes(sidecar, BuildJpeg());
            File.WriteAllBytes(rival, BuildJpeg());

            try
            {
                // 这一段是纯规则（不碰系统媒体控件），所以放在"拿不到接口就跳过"之前：
                // 老系统上 SMTC 会跳过，但"挑哪张外挂封面"照样得验。
                var picked = SidecarCover.FindInFolder(artFolder);

                if (!string.Equals(picked, sidecar, StringComparison.OrdinalIgnoreCase))
                {
                    Log(6, "挑外挂封面：同一目录里 cover.jpg 与 album.jpg 都在时没有按命名优先级选"
                            + $"（选中了「{Path.GetFileName(picked ?? "没有")}」，期望 cover.jpg）");
                    return false;
                }

                if (SidecarCover.HasInFolder(plainFolder))
                {
                    Log(6, "挑外挂封面：没有封面图的目录被判成有封面");
                    return false;
                }

                using var form = new 播放器.MainForm(new[] { first, second });
                form.Show();
                PumpMessages(500);

                var smtc = form.SystemMediaControls;

                if (smtc == null)
                {
                    Log(6, "系统媒体控件检查：跳过（这台机器没接上："
                            + (播放器.Ui.SmtcSession.UnavailableReason ?? "没给理由") + "）");
                    return true;
                }

                if (!smtc.IsEnabled)
                {
                    Log(6, "系统媒体控件检查：接口接上了，但系统那边 IsEnabled 是假");
                    return false;
                }

                if (!PumpUntil(() => form.Engine.IsPlaying, 10000))
                {
                    Log(6, "系统媒体控件检查：WAV 没播起来");
                    return false;
                }

                // 素材没有标签，所以曲名走的是"文件名兜底"，和界面上「标题」那一行同源
                var firstName = Path.GetFileNameWithoutExtension(first);

                if (!PumpUntil(() => smtc.Title == firstName, 6000))
                {
                    Log(6, $"系统媒体控件检查：曲名没喂进去（现在是「{smtc.Title}」，期望「{firstName}」）");
                    return false;
                }

                if (smtc.Status != Windows.Media.MediaPlaybackStatus.Playing)
                {
                    Log(6, $"系统媒体控件检查：正在播，系统那边的状态却是 {smtc.Status}（期望 Playing）");
                    return false;
                }

                // 第一首旁边没有封面图：封面来源必须是空的
                if (smtc.CoverPath != null)
                {
                    Log(6, "系统媒体控件检查：这首旁边没有封面图，封面来源却不是空的（"
                            + Path.GetFileName(smtc.CoverPath) + "）");
                    return false;
                }

                // 那五个按钮：开关都必须是开的（否则弹窗上根本不显示按钮）
                if (!smtc.IsPlayEnabled || !smtc.IsPauseEnabled || !smtc.IsStopEnabled ||
                    !smtc.IsNextEnabled || !smtc.IsPreviousEnabled)
                {
                    Log(6, "系统媒体控件检查：按钮开关没全开"
                            + $"（播放 {smtc.IsPlayEnabled} / 暂停 {smtc.IsPauseEnabled} / 停止 {smtc.IsStopEnabled} /"
                            + $" 上一个 {smtc.IsPreviousEnabled} / 下一个 {smtc.IsNextEnabled}）");
                    return false;
                }

                // 按钮按下之后我们要做的事（系统真的有没有把事件送过来，冒烟验不到——如实写在日志里）
                // 「暂停 / 播放」那一对
                smtc.SimulateButton(播放器.Ui.SmtcButton.Pause);
                PumpMessages(200);

                if (form.Engine.IsPlaying)
                {
                    Log(6, "系统媒体控件检查：按了「暂停」之后还在播");
                    return false;
                }

                smtc.SimulateButton(播放器.Ui.SmtcButton.Play);
                PumpMessages(200);

                if (!form.Engine.IsPlaying)
                {
                    Log(6, "系统媒体控件检查：按了「播放」之后没有继续播");
                    return false;
                }

                // 暂停：状态要跟着变（这条同时验了"状态是往系统那边写的"）
                form.Engine.Pause();

                if (!PumpUntil(() => smtc.Status == Windows.Media.MediaPlaybackStatus.Paused, 6000))
                {
                    Log(6, $"系统媒体控件检查：暂停之后系统那边的状态还是 {smtc.Status}（期望 Paused）");
                    return false;
                }

                form.Engine.Play();
                PumpMessages(200);

                // 时长与进度：系统那侧没有读回来的接口，验"我们喂过、值合理"
                if (!PumpUntil(() => smtc.TimelineUpdated && smtc.DurationMilliseconds > 0, 6000))
                {
                    Log(6, $"系统媒体控件检查：时间轴没喂进去（喂过 {smtc.TimelineUpdated}，"
                            + $"时长 {smtc.DurationMilliseconds} ms）");
                    return false;
                }

                if (smtc.DurationMilliseconds < 25000 || smtc.DurationMilliseconds > 35000)
                {
                    Log(6, "系统媒体控件检查：喂给系统媒体控件的时长不对"
                            + $"（{smtc.DurationMilliseconds} ms，素材是 30 秒）");
                    return false;
                }

                // 切歌：曲名要换成新的（这一条就是那个变异的靶子）
                var play = FindTopMenuItem(form, "播放");
                var next = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");

                if (next == null)
                {
                    Log(6, "系统媒体控件检查：找不到「播放 → 下一个」");
                    return false;
                }

                next.PerformClick();
                PumpMessages(300);

                var secondName = Path.GetFileNameWithoutExtension(second);

                if (!PumpUntil(() => smtc.Title == secondName, 10000))
                {
                    Log(6, $"系统媒体控件检查：切歌之后系统那边的曲名还是「{smtc.Title}」，"
                            + $"期望「{secondName}」");
                    return false;
                }

                // 第二首没有内嵌图，旁边那张 cover.jpg 就得被当成封面用上
                if (!PumpUntil(() => string.Equals(smtc.CoverPath, sidecar, StringComparison.OrdinalIgnoreCase), 6000))
                {
                    Log(6, "系统媒体控件检查：同目录的 cover.jpg 没被当成封面用上（现在的封面来源是「"
                            + (smtc.CoverPath ?? "没有") + "」，期望「" + sidecar + "」）");
                    return false;
                }

                // 交出去的是一张真的图片文件，系统那边得接下（这一步验到"系统读了这张文件"）
                if (!PumpUntil(() => smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：cover.jpg 交给系统之后没被接受（CoverSet 还是假）");
                    return false;
                }

                // 「下一个 / 上一个」：现在在第二首，按「下一个」应当绕回第一首
                smtc.SimulateButton(播放器.Ui.SmtcButton.Next);

                if (!PumpUntil(() => form.Engine.CurrentPath == first, 15000))
                {
                    Log(6, "系统媒体控件检查：按了「下一个」之后没有绕回第一首（现在在播 "
                            + $"{Path.GetFileName(form.Engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                if (!PumpUntil(() => smtc.Title == firstName, 10000))
                {
                    Log(6, $"系统媒体控件检查：按「下一个」切歌之后曲名没跟上（现在是「{smtc.Title}」）");
                    return false;
                }

                // 绕回第一首（旁边没有封面图）：封面也得跟着变空——
                // 不清的话音量弹窗里会一直挂着上一首那张 cover.jpg
                if (!PumpUntil(() => smtc.CoverPath == null && !smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：切回没有封面那首之后，封面还挂着上一首的"
                            + $"（来源「{smtc.CoverPath ?? "没有"}」，喂过 {smtc.CoverSet}）");
                    return false;
                }

                smtc.SimulateButton(播放器.Ui.SmtcButton.Previous);

                if (!PumpUntil(() => form.Engine.CurrentPath == second, 15000))
                {
                    Log(6, "系统媒体控件检查：按了「上一个」之后没有回到第二首（现在在播 "
                            + $"{Path.GetFileName(form.Engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                // 「上一个」回到第二首（旁边有 cover.jpg）：封面得重新接上那张外挂图
                if (!PumpUntil(() => string.Equals(smtc.CoverPath, sidecar, StringComparison.OrdinalIgnoreCase)
                                     && smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：按「上一个」回到有封面那首之后，封面没接回来"
                            + $"（来源「{smtc.CoverPath ?? "没有"}」，喂过 {smtc.CoverSet}）");
                    return false;
                }

                // 内嵌图优先于目录里的外挂封面：造一个内嵌 PNG 的 mp3（目录里同时有 cover.jpg），
                // 直接喂元数据——不必真的播它
                var embedded = MakePng();
                var tagged = Path.Combine(artFolder, "smtc-带内嵌封面.mp3");

                File.WriteAllBytes(tagged, BuildId3File(3, new List<byte[]>
                {
                    Id3Frame("APIC", PictureBody(3, "image/png", 3, embedded), 3)
                }));

                var taggedTags = TagReader.Read(tagged);

                if (!taggedTags.HasCoverArt)
                {
                    Log(6, "系统媒体控件检查：造出来的内嵌封面素材没读到封面（这一步验不下去）");
                    return false;
                }

                form.ApplyMetadata(tagged, taggedTags, LyricsDocument.Empty, allowOnlineLookup: false);

                if (!PumpUntil(() => smtc.CoverPath != null &&
                                     smtc.CoverPath.EndsWith("smtc-cover.png", StringComparison.OrdinalIgnoreCase) &&
                                     smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：有内嵌图时封面来源不对（现在是「"
                            + (smtc.CoverPath ?? "没有") + "」，期望数据目录里的 smtc-cover.png，"
                            + "而不是同目录那张 cover.jpg）");
                    return false;
                }

                // 落成临时文件的那份必须就是内嵌图本身，不能是目录里那张
                byte[] handedOver;

                try
                {
                    handedOver = File.ReadAllBytes(smtc.CoverPath!);
                }
                catch (Exception ex)
                {
                    Log(6, "系统媒体控件检查：封面来源文件读不回来（" + ex.Message + "）");
                    return false;
                }

                if (!handedOver.AsSpan().SequenceEqual(embedded))
                {
                    Log(6, "系统媒体控件检查：喂给系统的封面不是内嵌那张"
                            + $"（{handedOver.Length} 字节，内嵌图是 {embedded.Length} 字节）");
                    return false;
                }

                // 第五个按钮：停止。放在最后——它会把这一首停掉，前面几条断言都需要它一直在播。
                // 顺带把 SmtcSession.Clear() 那条路也走一遍（停止时系统那边不许还挂着上一首）。
                smtc.SimulateButton(播放器.Ui.SmtcButton.Stop);
                PumpMessages(400);

                if (form.Engine.IsPlaying)
                {
                    Log(6, "系统媒体控件检查：按了「停止」之后还在播");
                    return false;
                }

                if (!PumpUntil(() => smtc.CoverPath == null && !smtc.CoverSet, 6000))
                {
                    Log(6, "系统媒体控件检查：停下来之后系统那边还挂着封面"
                            + $"（来源「{smtc.CoverPath ?? "没有"}」，喂过 {smtc.CoverSet}）");
                    return false;
                }

                Log(6, "系统媒体控件正常：接口接上了、IsEnabled 为真，曲名随歌切换，"
                        + "播放 / 暂停状态与时长都喂了进去并读得回来，五个按钮的开关都开着、"
                        + "按钮回调（播放 / 暂停 / 停止 / 下一个 / 上一个）真的作用在引擎上；"
                        + "封面按「内嵌图 → 同目录外挂封面（cover.jpg 优先于 album.jpg / folder.jpg）」取，"
                        + "切到没封面的那首会清空、回到有封面的那首会接回来，停下来也会清空，"
                        + "交给系统的图片文件系统那边确实读了（CoverSet）"
                        + "（⚠ 系统界面上真的画出来了没有、系统有没有把按钮事件送过来，冒烟验不到——只能人眼看）");
                return true;
            }
            finally
            {
                TryDeleteDirectory(smokeRoot);
            }
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
            var previousRestore = settings.RestoreLastPlaylist;

            try
            {
                settings.LastPlaylist = new List<string> { existing, missing, stream };
                settings.LastPlaylistIndex = -1;

                // 这一步验的是"恢复这条路上不做同步磁盘查询"，所以必须真的让它恢复：
                // 那个开关默认是关的（默认打开程序 = 空列表），不显式打开的话这里什么都验不到。
                settings.RestoreLastPlaylist = true;
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
                restore.RestoreLastPlaylist = previousRestore;
                restore.Save();

                TryDeleteDirectory(root);
            }
        }

        /// <summary>
        /// 「启动时恢复上次播放列表」开关：<b>默认关 —— 打开程序就是一张空列表</b>。
        /// <para>
        /// 判据是<b>同一份设置、两次启动的对比</b>：
        /// 关着的时候，设置里明明存着上次的列表和「当前歌单」，启动后<b>两样都不该回来</b>
        /// （空列表挂着歌单名是危险的：往里加两首再点「覆盖保存」，那份歌单就被两首换掉了，
        /// 所以判据落在"菜单里有没有那个「覆盖保存」入口"上）；
        /// 开着的时候，同一个歌单名必须真的恢复出来、并有对应的入口。
        /// 两侧都验才算数——只验"关着是空的"的话，歌单文件压根没建出来也会绿。
        /// </para>
        /// </summary>
        private static bool CheckStartupPlaylistPreference()
        {
            // ---- 纯函数那半：默认值 + ForgetSession 清的是哪三项 ----
            if (new AppSettings().RestoreLastPlaylist)
            {
                Log(6, "启动开关检查：新设置的默认值不是「关」（打开程序应当就是空列表）");
                return false;
            }

            var probe = new AppSettings { RestoreLastPlaylist = true, LastPlaylistIndex = 3, CurrentPlaylistName = "某个歌单" };
            probe.LastPlaylist.Add(@"D:\媒体\某首歌.mp3");
            probe.ForgetSession();

            if (probe.LastPlaylist.Count != 0 || probe.LastPlaylistIndex != -1 || probe.CurrentPlaylistName.Length != 0)
            {
                Log(6, "启动开关检查：ForgetSession 没有把三项会话状态都清掉"
                        + $"（列表 {probe.LastPlaylist.Count} 项、当前项 {probe.LastPlaylistIndex}、"
                        + $"当前歌单「{probe.CurrentPlaylistName}」）");
                return false;
            }

            const string name = "启动开关检查";
            var keep = Path.Combine(AppContext.BaseDirectory, "startup-keep.mp3");
            var before = AppSettings.Load();

            try
            {
                PlaylistLibrary.Delete(name, out _);
                PlaylistLibrary.Create(name);       // 真在库里建一份，"开着能恢复"才有意义
                PlaylistLibrary.Invalidate();

                File.WriteAllBytes(keep, new byte[] { 0x49, 0x44, 0x33, 4 });

                // ---- 关：设置里存着上次的列表和歌单名，启动后两样都不该回来 ----
                var off = AppSettings.Load();
                off.RestoreLastPlaylist = false;
                off.LastPlaylist = new List<string> { keep };
                off.LastPlaylistIndex = 0;
                off.CurrentPlaylistName = name;
                off.Save();

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    // 显示出来再验：菜单勾选和"关窗落盘"这两件事都要走真实的窗口生命周期
                    // （没建过句柄的窗体 Close() 不会走 FormClosing，设置也就写不下去）。
                    form.Show();
                    PumpMessages(200);

                    var playlist = PlaylistOf(form);

                    if (playlist.Count != 0)
                    {
                        Log(6, $"启动开关检查：开关关着，却恢复了 {playlist.Count} 项");
                        return false;
                    }

                    if (PlaylistsOf(form).CurrentName.Length != 0)
                    {
                        Log(6, $"启动开关检查：空列表却挂着歌单名「{PlaylistsOf(form).CurrentName}」"
                                + "（往里加两首再点「覆盖保存」就会把那份歌单换掉）");
                        return false;
                    }

                    if (PlaylistMenuTexts(form).Any(t => t.Contains("覆盖保存", StringComparison.Ordinal)))
                    {
                        Log(6, "启动开关检查：空列表却给出了「覆盖保存」入口，点下去会把那份歌单换成空列表");
                        return false;
                    }

                    // 状态栏那句"空列表 + 歌单在哪儿"是给用户看的指路牌。
                    // 启动警告（比如某个媒体键被别的程序占着）会把它整句换掉，那种情况不算失败。
                    var status = ReadStatus(form);

                    if (!status.Contains("空列表", StringComparison.Ordinal) &&
                        !status.Contains("没注册上", StringComparison.Ordinal))
                    {
                        Log(6, $"启动开关检查：空着启动，但状态栏没说（「{status}」）");
                        return false;
                    }

                    var item = FindRestorePlaylistMenuItem(form);

                    if (item == null)
                    {
                        Log(6, "启动开关检查：播放菜单里没有「启动时恢复上次播放列表」这一项");
                        return false;
                    }

                    if (item.Checked)
                    {
                        Log(6, "启动开关检查：开关关着，菜单却勾着");
                        return false;
                    }

                    // 勾上只写设置，不能当场动这张列表（那会盖掉用户正开着的东西）
                    SetMenuChecked(item, true);

                    if (playlist.Count != 0)
                    {
                        Log(6, "启动开关检查：勾上开关当场就把列表改了（应当下次启动才生效）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(200);
                }

                if (!AppSettings.Load().RestoreLastPlaylist)
                {
                    Log(6, "启动开关检查：菜单里勾上之后没有写进设置（关窗时应当落盘）");
                    return false;
                }

                // ---- 开：同一份设置，这次开关是开的，必须真的恢复 ----
                var on = AppSettings.Load();
                on.RestoreLastPlaylist = true;
                on.LastPlaylist = new List<string> { keep };
                on.LastPlaylistIndex = 0;
                on.CurrentPlaylistName = name;
                on.Save();

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(200);

                    var playlist = PlaylistOf(form);

                    if (playlist.Count != 1 ||
                        !string.Equals(playlist.Items[0].FilePath, keep, StringComparison.OrdinalIgnoreCase))
                    {
                        Log(6, $"启动开关检查：开关开着却没恢复上次的列表（{playlist.Count} 项）");
                        return false;
                    }

                    if (!PlaylistsOf(form).IsCurrent(name))
                    {
                        Log(6, "启动开关检查：开关开着却没恢复「当前歌单」关联"
                                + $"（现在挂着「{PlaylistsOf(form).CurrentName}」）");
                        return false;
                    }

                    if (!PlaylistMenuTexts(form).Any(t => t.Contains("覆盖保存", StringComparison.Ordinal)))
                    {
                        Log(6, "启动开关检查：恢复了列表和歌单关联，菜单里却没有「覆盖保存」入口");
                        return false;
                    }

                    var item = FindRestorePlaylistMenuItem(form);

                    if (item == null || !item.Checked)
                    {
                        Log(6, "启动开关检查：开关开着，菜单却没勾上");
                        return false;
                    }

                    if (ReadStatus(form).Contains("空列表", StringComparison.Ordinal))
                    {
                        Log(6, $"启动开关检查：开关开着却说自己是空列表（「{ReadStatus(form)}」）");
                        return false;
                    }

                    form.Close();
                    PumpMessages(150);
                }

                Log(6, "启动开关检查正常：默认关（打开就是空列表、不挂歌单名、不给「覆盖保存」），"
                        + "勾上会写进设置、下次启动真的恢复列表与歌单关联（歌单文件全程不动）");
                return true;
            }
            finally
            {
                PlaylistLibrary.Delete(name, out _);
                PlaylistLibrary.Invalidate();
                TryDelete(keep);

                // 这一步改过设置，必须还原：后面还有十几步各自建窗体读这份设置
                var restore = AppSettings.Load();
                restore.RestoreLastPlaylist = before.RestoreLastPlaylist;
                restore.LastPlaylist = before.LastPlaylist;
                restore.LastPlaylistIndex = before.LastPlaylistIndex;
                restore.CurrentPlaylistName = before.CurrentPlaylistName;
                restore.Save();
            }
        }

        /// <summary>播放菜单里的「启动时恢复上次播放列表」那一项（找不到返回 <c>null</c>）。</summary>
        private static ToolStripMenuItem? FindRestorePlaylistMenuItem(Form form)
        {
            var play = FindTopMenuItem(form, "播放");
            return play == null ? null : FindMenuItem(play.DropDownItems, "启动时恢复上次播放列表");
        }

        /// <summary>把「文件 → 播放列表」那份菜单重建一遍，返回里面的菜单文字（判据：有没有哪个入口）。</summary>
        private static List<string> PlaylistMenuTexts(播放器.MainForm form)
        {
            form.RebuildPlaylistLibraryMenu(form.MenuFilePlaylists.DropDownItems);

            return form.MenuFilePlaylists.DropDownItems.OfType<ToolStripMenuItem>()
                .Select(item => item.Text)
                .ToList();
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
                var now = new PerFileAdjustments(PlaybackHistory.Load()).Get(diskPath);

                Log(8, "按文件调整：改了延迟没有马上落盘（等定时器的话，用户当场关程序就丢了）"
                        + $"——磁盘上现在是 {now.AudioDelayMilliseconds} / {now.SubtitleDelayMilliseconds}，"
                        + $"history.json 存在？ {File.Exists(PlaybackHistory.FilePath)}"
                        + $"（{PlaybackHistory.FilePath}）");
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
