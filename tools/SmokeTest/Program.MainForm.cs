// 检查点 5/6：主窗体的调度、布局、主题与时长回填；列表 / 拖动 / 队列 / 系统 / 会话 / 历史各在 Program.MainForm.*.cs。

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

                // 1.5.0 F1：睡眠定时器——到点**暂停**（不退出、不切歌）；"听完这一首就停"不往下播
                if (!CheckSleepTimer()) return false;

                // 1.5.0 F2：随机播放换成"洗牌袋"——一轮之内每首一次；顺手验菜单真的能开关
                if (!CheckShuffleBag()) return false;

                // 1.3.0 A-3：「下一首播放」队列——插播时列表当前项不动，插播完回到列表继续
                if (!CheckPlayNextQueue()) return false;

                // 1.3.0：只 Dispose 没 Close 的表单必须把计时器停掉（僵尸会写旧历史）
                if (!CheckDisposedFormStopsTimers()) return false;

                // 1.3.0 C-1：系统媒体控件（音量弹窗里那一块）——喂进去的曲名 / 状态读得回来
                if (!CheckSystemMediaControls()) return false;

                // 列表里"正在播的那一行"必须跟着换歌走（所有者踩到过：加粗的是上一首）
                if (!CheckNowPlayingRowFollowsTrack()) return false;

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
    }
}
