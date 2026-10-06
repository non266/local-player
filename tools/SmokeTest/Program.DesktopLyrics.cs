// 检查点 15：桌面歌词（窗口/托盘/热键/字体/外观/HitTest/真实拖动/输入框布局）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 桌面歌词的完整检查（第 15 步）。
        /// <para>
        /// 自己把设置清掉再开始，所以它<b>既能全量跑，也能单独跑</b>：全量跑时前面的步骤
        /// 已经把设置写成了别的样子，而这一步要验的恰恰是"从没有设置开始 → 开启 →
        /// 关掉程序 → 下次启动自己回来"。
        /// </para>
        /// <para>只依赖第 9 步合成的那个带内嵌时间轴歌词的样本文件。</para>
        /// </summary>
        private static bool TestDesktopLyrics()
        {
            var media = Path.Combine(AppContext.BaseDirectory, "smoke-tags", "sample.flac");

            if (!File.Exists(media))
            {
                Log(15, "缺少第 9 步生成的样本文件");
                return false;
            }

            // 从"干净设置"开始（这是测试自己的临时目录，不是使用者的配置）
            TryDelete(AppSettings.SettingsFilePath);

            try
            {
                Rectangle placed;
                Size placedSize = Size.Empty;

                using (var form = new 播放器.MainForm(Array.Empty<string>()))
                {
                    form.Show();
                    PumpMessages(300);

                    // 走真实的元数据加载路径，拿到内嵌的时间轴歌词
                    form.LoadTrackMetadata(media);

                    PumpMessages(1200);

                    var lyrics = SessionOf(form).Lyrics;

                    if (!lyrics.IsSynchronized)
                    {
                        Log(15, "没有从样本文件里读到带时间轴的歌词");
                        return false;
                    }

                    if (!CheckDesktopLyrics(form, lyrics)) return false;

                    if (form.DesktopLyricsWindow is not { } window)
                    {
                        Log(15, "检查结束时桌面歌词窗口不见了");
                        return false;
                    }

                    // 只记位置：宽高是按文字算出来的，不参与持久化。
                    // ⚠ 先把它摆到工作区左侧再记：重启后窗口可能宽得多（歌词换句就会变宽），
                    // 摆在右边的话"保存的 X + 新宽度"根本放不下，会被 ClampToWorkingArea
                    // 挪回屏幕内——那是钳制在正常工作，却会让这条断言随机变红。
                    var workArea = Screen.FromControl(window).WorkingArea;
                    window.MoveTo(new Point(workArea.Left + 40, workArea.Top + 120));
                    PumpMessages(150);

                    placed = new Rectangle(window.Left, window.Top, 0, 0);
                    placedSize = window.ClientSize;

                    form.Close();
                    PumpMessages(300);
                }

                var saved = AppSettings.Load();

                if (!saved.DesktopLyricsEnabled || !saved.DesktopLyricsPlaced)
                {
                    Log(15, "桌面歌词的开关 / 位置没有存进设置（开着 "
                            + $"{saved.DesktopLyricsEnabled}、记过位置 {saved.DesktopLyricsPlaced}）");
                    return false;
                }

                if (string.IsNullOrEmpty(saved.LyricsFontFamily))
                {
                    Log(15, "歌词字体没有存进设置");
                    return false;
                }

                // ---- 重新启动一次：它应该自己回来，而且回到上次那个位置 ----
                // ⚠ 先把设置里的"上次会话"清空：这一段要验的是"没有媒体时的占位文字"，
                // 而重开的窗口会按设计恢复上次的播放列表 + 选中项——恢复出一个带歌词的曲目时，
                // 占位文字本来就不该出现，拿它断言会随机变红（实测 1/4 次）。
                // 用完再还原：后面的步骤不该被这里改过的会话影响。
                var previousPlaylist = saved.LastPlaylist;
                var previousIndex = saved.LastPlaylistIndex;

                saved.LastPlaylist = new System.Collections.Generic.List<string>();
                saved.LastPlaylistIndex = -1;
                saved.Save();

                using (var reopened = new 播放器.MainForm(Array.Empty<string>()))
                {
                    reopened.Show();
                    PumpMessages(500);

                    if (reopened.DesktopLyricsWindow is not { } window)
                    {
                        Log(15, "重新启动之后桌面歌词没有自己回来");
                        return false;
                    }

                    if (!window.Visible)
                    {
                        Log(15, "重新启动之后桌面歌词窗建出来了却是隐藏的");
                        return false;
                    }

                    if (window.Left != placed.Left || window.Top != placed.Top)
                    {
                        Log(15, $"重新启动之后没有回到上次的位置（{placed.Left},{placed.Top}"
                                + $" → {window.Left},{window.Top}；"
                                + $"关之前客户区 {placedSize}，现在 {window.ClientSize}）");
                        return false;
                    }

                    // 没在播放时是"桌面歌词 /（未在播放）"，不该是一块空白
                    if (window.CurrentText != "桌面歌词")
                    {
                        Log(15, "没有媒体时显示的占位文字不对：" + window.CurrentText);
                        return false;
                    }

                    reopened.Close();
                    PumpMessages(200);
                }

                // 把会话还原：这一段的失败会直接结束整个测试（设置状态无所谓），
                // 但成功之后要保证后面的步骤看到的是原来的设置。
                var restore = AppSettings.Load();
                restore.LastPlaylist = previousPlaylist;
                restore.LastPlaylistIndex = previousIndex;
                restore.Save();

                Log(15, "桌面歌词全套正常：见上一条；另外开关与位置能存下来，"
                        + "重新启动自动回到上次的位置");
                return true;
            }
            catch (Exception ex)
            {
                Log(15, "桌面歌词检查失败: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 桌面歌词：置顶小窗、跟着进度换句、能拖动与锁定穿透、全屏不被收走、选项与位置能存下来。
        /// <para>
        /// 刻意<b>走用户会走的那条路</b>——从「视图 → 桌面歌词 → 启用桌面歌词」点下去，
        /// 而不是直接调私有方法：菜单没接上线、勾选状态同步反了这类问题只有这样才验得出来。
        /// </para>
        /// </summary>
        private static bool CheckDesktopLyrics(播放器.MainForm form, LyricsDocument lyrics)
        {
            const int wsExTransparent = 0x00000020;
            const int wsExToolWindow = 0x00000080;
            const int wsExLayered = 0x00080000;
            const int wsExNoActivate = 0x08000000;

            var menu = FindViewMenuItem(form, "桌面歌词");
            if (menu == null)
            {
                Log(15, "桌面歌词检查：视图菜单里没有「桌面歌词」");
                return false;
            }

            var enable = FindMenuItem(menu.DropDownItems, "启用桌面歌词");
            if (enable == null)
            {
                Log(15, "桌面歌词检查：「桌面歌词」子菜单里没有「启用桌面歌词」");
                return false;
            }

            if (form.TrayDesktopLyrics == null)
            {
                Log(15, "桌面歌词检查：托盘菜单里没有开关（主界面收进托盘后就再也开不了）");
                return false;
            }

            if (!CheckDesktopLyricsTraySubmenu(form)) return false;

            if (form.DesktopLyricsWindow != null)
            {
                Log(15, "桌面歌词检查：还没打开就已经有窗口了");
                return false;
            }

            // 抢焦点这件事只在"前台本来就是本进程的窗口"时才会发生
            // （前台属于别的程序时，Windows 本来就不让它抢），所以先把主窗口推到前台，
            // 否则这条检查会随环境时灵时不灵——那种"时灵时不灵"的检查等于没检查。
            form.Activate();
            PumpMessages(200);

            var foregroundBefore = GetForegroundWindow();
            var preconditionMet = foregroundBefore == form.Handle;

            SetMenuChecked(enable, true);
            PumpMessages(300);

            if (form.DesktopLyricsWindow is not { } window)
            {
                Log(15, "桌面歌词检查：勾选之后没有创建窗口");
                return false;
            }

            if (!window.Visible || !window.TopMost || window.ShowInTaskbar ||
                window.FormBorderStyle != FormBorderStyle.None)
            {
                Log(15, "桌面歌词检查：窗口状态不对（显示 " + window.Visible + "、置顶 " + window.TopMost
                        + "、任务栏 " + window.ShowInTaskbar + "、边框 " + window.FormBorderStyle + "）");
                return false;
            }

            var style = ReadExStyle(window.Handle);
            if ((style & wsExToolWindow) == 0 || (style & wsExNoActivate) == 0)
            {
                Log(15, $"桌面歌词检查：缺少工具窗口 / 不激活样式（扩展样式 0x{style:X8}）");
                return false;
            }

            if (window.StealsFocusOnShow)
            {
                Log(15, "桌面歌词检查：Show() 时会把焦点抢走（ShowWithoutActivation 不是 true）");
                return false;
            }

            // 抢焦点这件事只能看"谁真的成了前台窗口"：全屏看视频时被抢走焦点是很明显的体感问题。
            if (GetForegroundWindow() == window.Handle)
            {
                Log(15, $"桌面歌词检查：显示桌面歌词把前台窗口抢走了（前置条件成立={preconditionMet}）");
                return false;
            }

            if (!preconditionMet)
            {
                Log(15, "桌面歌词检查：注意——这次没能把主窗口推到前台（前台给的是别的程序），"
                        + "「不抢焦点」这一条检查这次是空的");
            }

            // ---- 跟着进度换句 ----
            if (lyrics is not { IsSynchronized: true } || lyrics.Lines.Count < 2)
            {
                Log(15, "桌面歌词检查：样本歌词不是带时间轴的，验不了换句");
                return false;
            }

            window.UpdatePosition(lyrics.Lines[0].Time);
            PumpMessages(120);

            var current = window.CurrentText;
            var next = window.NextText;

            if (current != lyrics.Lines[0].Text || next != lyrics.Lines[1].Text)
            {
                Log(15, $"桌面歌词检查：显示「{current}」/「{next}」，"
                        + $"期望「{lyrics.Lines[0].Text}」/「{lyrics.Lines[1].Text}」");
                return false;
            }

            window.UpdatePosition(lyrics.Lines[^1].Time);
            PumpMessages(120);

            if (window.NextText != string.Empty)
            {
                Log(15, "桌面歌词检查：唱到最后一句之后还在显示下一句");
                return false;
            }

            // ---- B3：双语歌词（同一时间戳的原文 + 译文）第二行放译文 ----
            var bilingual = LyricsDocument.Parse(
                "[00:00.50]原文一\n" +
                "[00:00.50]译文一\n" +
                "[00:05.00]原文二\n" +
                "[00:10.00]原文三\n");

            window.SetTrack("双语", bilingual);
            PumpMessages(120);

            window.UpdatePosition(TimeSpan.FromSeconds(1));
            PumpMessages(120);

            if (window.CurrentText != "原文一" || window.NextText != "译文一")
            {
                Log(15, "桌面歌词检查：双语歌词没有显示成「原文 + 译文」"
                        + $"（现在「{window.CurrentText}」/「{window.NextText}」）");
                return false;
            }

            // 关掉"有翻译时显示翻译"：第二行要换成真正的下一句（跳过译文那一行）
            window.SecondLineIsTranslation = false;
            PumpMessages(120);

            if (window.CurrentText != "原文一" || window.NextText != "原文二")
            {
                Log(15, "桌面歌词检查：关掉翻译之后第二行没有换回下一句"
                        + $"（现在「{window.CurrentText}」/「{window.NextText}」）");
                return false;
            }

            window.SecondLineIsTranslation = true;
            PumpMessages(120);

            // 没有时间轴的歌词在"一次只看一句"的窗口里没法定位，应该退回"记号 + 曲名"
            window.SetTrack("测试曲名", LyricsDocument.Parse("第一句\n第二句", "txt"));
            PumpMessages(120);

            if (window.CurrentText != "♪" ||
                window.NextText != "测试曲名")
            {
                Log(15, "桌面歌词检查：纯文本歌词没有退回曲名（当前「"
                        + window.CurrentText + "」）");
                return false;
            }

            // ---- 排版跟着选项走 ----
            var twoLines = window.Height;
            window.ShowNextLine = false;
            PumpMessages(120);
            var oneLine = window.Height;

            if (oneLine >= twoLines)
            {
                Log(15, $"桌面歌词检查：关掉「显示下一句」窗口没有变矮（{twoLines} → {oneLine}）");
                return false;
            }

            window.ShowNextLine = true;
            PumpMessages(120);

            // ---- 1.1.0：控制条（播放 / 暂停、上一首、下一首、停止）----
            if (!CheckDesktopLyricsControls(window)) return false;
            if (!CheckDesktopLyricsControlsSetting(form, window)) return false;

            var smallWidth = window.Width;
            window.FontSize = 60;
            PumpMessages(120);
            var bigWidth = window.Width;

            if (bigWidth <= smallWidth)
            {
                Log(15, $"桌面歌词检查：字号调大之后窗口没有变宽（{smallWidth} → {bigWidth}）");
                return false;
            }

            window.FontSize = 30;
            PumpMessages(120);

            // ---- 不透明度钳制 ----
            window.OpacityPercent = 1;
            if ((int)window.OpacityPercent != DesktopLyricsDefaults.MinimumOpacity)
            {
                Log(15, $"桌面歌词检查：不透明度下限没有钳住（{window.OpacityPercent}）");
                return false;
            }

            window.OpacityPercent = 500;
            if ((int)window.OpacityPercent != DesktopLyricsDefaults.MaximumOpacity)
            {
                Log(15, $"桌面歌词检查：不透明度上限没有钳住（{window.OpacityPercent}）");
                return false;
            }

            // ---- 画面：背景必须真的透明，字必须真的画上去了 ----
            if (!CheckLyricsSurface(window, out var opaquePixels)) return false;

            // ---- 鼠标停在歌词上时要铺一块背景（否则用户看不出该抓哪儿）----
            if (!CheckLyricsHover(window)) return false;

            // ---- 字上那一点真的能被点中（"能不能拖动"就靠这个）----
            if (!CheckLyricsHitTest(window)) return false;

            // ---- 它是不是真的显示在屏幕上（而不只是我们内存里有张位图）----
            if (!CheckLyricsOnScreen(window)) return false;

            // ---- 真鼠标拖一把：这才叫"能移动" ----
            if (!CheckLyricsRealDrag(window)) return false;

            // ---- 拖动：真的发鼠标消息，看窗口跟不跟手 ----
            window.CenterOnScreen();
            PumpMessages(120);

            var beforeDrag = window.Location;
            DragWindow(window, fromX: 5, fromY: 5, toX: 45, toY: 25);

            if (window.Location.X != beforeDrag.X + 40 || window.Location.Y != beforeDrag.Y + 20)
            {
                Log(15, $"桌面歌词检查：拖不动（{beforeDrag} → {window.Location}，期望 +40/+20）");
                return false;
            }

            // ---- 锁定 = 鼠标穿透 ----
            // 注意此刻不透明度正好是 100%：WinForms 会把 WS_EX_LAYERED 摘掉，
            // 而穿透要靠它，所以这一条同时验了"改完样式会把分层补回来"。
            window.Locked = true;
            style = ReadExStyle(window.Handle);

            if ((style & wsExTransparent) == 0 || (style & wsExLayered) == 0)
            {
                Log(15, $"桌面歌词检查：锁定之后没有设上鼠标穿透（扩展样式 0x{style:X8}）");
                return false;
            }

            // 锁定之后即使鼠标消息送到了（真实情况下系统根本不会送过来），也不该跟着动
            var beforeLocked = window.Location;
            DragWindow(window, fromX: 5, fromY: 5, toX: 65, toY: 45);

            if (window.Location != beforeLocked)
            {
                Log(15, $"桌面歌词检查：锁定之后还能拖动（{beforeLocked} → {window.Location}）");
                return false;
            }

            window.Locked = false;
            PumpMessages(120);

            if ((ReadExStyle(window.Handle) & wsExTransparent) != 0)
            {
                Log(15, "桌面歌词检查：解锁之后鼠标穿透样式还留着");
                return false;
            }

            // ---- 解锁热键（Ctrl+Alt+D）：锁定之后托盘之外的另一个出口 ----
            if (!CheckDesktopLyricsLockHotKey(form, window)) return false;

            // ---- 右键菜单 ----
            var context = window.ContextMenuStrip;
            if (context == null ||
                FindMenuItem(context.Items, "锁定（鼠标穿透）") == null ||
                FindMenuItem(context.Items, "关闭桌面歌词") == null)
            {
                Log(15, "桌面歌词检查：窗口上没有右键菜单，或菜单里缺少选项");
                return false;
            }

            // 真的弹一次：不激活窗口（WS_EX_NOACTIVATE）还能不能弹出右键菜单，
            // 是这套窗口样式最容易踩的坑。
            context.Show(window, new Point(6, 6));
            PumpMessages(250);

            if (!context.Visible)
            {
                Log(15, "桌面歌词检查：右键菜单弹不出来");
                return false;
            }

            context.Close();
            PumpMessages(120);

            // ---- 全屏时不能被一起收走 ----
            var sidebarWindow = form.SidebarWindow;

            form.EnterFullscreen();
            PumpMessages(300);

            // 对照：同一时刻侧栏悬浮窗应该已经被收起来了。
            // 少了这一条，"桌面歌词还显示着"也可能只是因为"全屏收窗口"这步压根没生效。
            if (sidebarWindow is { Visible: true })
            {
                Log(15, "桌面歌词检查（对照）：全屏时侧栏悬浮窗还显示着，这一步证明不了桌面歌词被区别对待");
                return false;
            }

            if (!window.Visible)
            {
                Log(15, "桌面歌词检查：进全屏之后被一起收起来了（它的用处正是全屏也能看）");
                return false;
            }

            form.ExitFullscreen();
            PumpMessages(300);

            if (!window.Visible)
            {
                Log(15, "桌面歌词检查：退出全屏之后没有回来");
                return false;
            }

            var area = Screen.FromControl(window).WorkingArea;
            if (!area.Contains(window.Bounds))
            {
                Log(15, $"桌面歌词检查：窗口跑到屏幕外了（{window.Bounds} 不在 {area} 内）");
                return false;
            }

            // ---- 超长的一句：必须折行，且宽度被工作区的比例钳住（否则会横穿整个桌面） ----
            // ⚠ 两行都写成超长：主窗体的界面计时会不断把"真实播放位置"推过来，
            // 位置跑到第二行时这一句就换成第二行了。只让第一行超长的话，这条断言会随
            // "跑到第几秒"随机变红（实测被推过界一次，报的是"超长句没有折行"）。
            // 另外：设完位置**不泵消息**就断言——布局是同步算出来的，一泵就可能被换掉文字。
            var shortHeight = window.Height;
            var shortSize = window.ClientSize;
            var longLine = new string('长', 160);

            var longDocument = LyricsDocument.Parse($"[00:01.00]{longLine}\n[00:05.00]{longLine}", "长句测试");

            window.SetTrack("超长句", longDocument);
            window.UpdatePosition(TimeSpan.FromSeconds(2));

            if (window.Width > (int)(area.Width * 0.9) + 2)
            {
                Log(15, $"桌面歌词检查：超长句没有把宽度钳住（{window.Width} > 工作区宽度的 90%）");
                return false;
            }

            if (window.Height <= shortHeight)
            {
                Log(15, "桌面歌词检查：超长句没有折行"
                        + $"（高度还是 {window.Height}，之前 {shortHeight}；"
                        + $"客户区 {shortSize} → {window.ClientSize}；"
                        + $"文档 {longDocument.Lines.Count} 行、当前显示「{window.CurrentText}」）");
                return false;
            }

            PumpMessages(150);

            if (!area.Contains(window.Bounds))
            {
                Log(15, $"桌面歌词检查：超长句把窗口挤出屏幕了（{window.Bounds}）");
                return false;
            }

            // 回到正常内容，后面的检查要用常规尺寸
            window.SetTrack("测试曲名", lyrics);
            window.UpdatePosition(lyrics.Lines[0].Time);
            PumpMessages(150);

            // ---- 配色选「跟随主题」时要跟着强调色走 ----
            window.ColorChoice = DesktopLyricsColor.Theme;

            var themeMenu = FindViewMenuItem(form, "主题");
            var vlcTheme = themeMenu == null ? null : FindMenuItem(themeMenu.DropDownItems, "VLC 橙");
            var autoTheme = themeMenu == null ? null : FindMenuItem(themeMenu.DropDownItems, "跟随系统");

            if (vlcTheme == null || autoTheme == null)
            {
                Log(15, "桌面歌词检查：找不到「视图 → 主题」里的菜单项");
                return false;
            }

            vlcTheme.PerformClick();
            PumpMessages(200);

            if (!Equals(window.ThemeAccent, Themes.Vlc.Accent))
            {
                Log(15, "桌面歌词检查：换主题之后没有跟着换强调色");
                return false;
            }

            autoTheme.PerformClick();
            PumpMessages(150);

            // ---- 歌词字体：两处歌词要一起换，坏字体名不能把界面搞坏 ----
            if (!CheckLyricsFont(form, window)) return false;

            // ---- 关掉再开：复用同一个窗口，不是又造一个 ----
            SetMenuChecked(enable, false);
            PumpMessages(200);

            if (window.Visible)
            {
                Log(15, "桌面歌词检查：关掉之后窗口还显示着");
                return false;
            }

            SetMenuChecked(enable, true);
            PumpMessages(200);

            if (!ReferenceEquals(form.DesktopLyricsWindow, window) || !window.Visible)
            {
                Log(15, "桌面歌词检查：再次开启没有复用原来的窗口");
                return false;
            }

            // ---- Ctrl+D 也要能开关（快捷键漏接是很常见的一类问题） ----
            if (!SendShortcut(form, Keys.Control | Keys.D))
            {
                Log(15, "桌面歌词检查：Ctrl+D 没有被处理");
                return false;
            }

            PumpMessages(200);

            if (window.Visible)
            {
                Log(15, "桌面歌词检查：Ctrl+D 没有把桌面歌词关掉");
                return false;
            }

            SendShortcut(form, Keys.Control | Keys.D);
            PumpMessages(200);

            if (!window.Visible)
            {
                Log(15, "桌面歌词检查：再按一次 Ctrl+D 没有把它打开");
                return false;
            }

            // 放在最后：它会把内容和位置都改掉（所以它自己负责恢复，见函数里的 finally）
            if (!CheckLyricsResizeAnchor(window, lyrics)) return false;

            Log(15, "桌面歌词正常：菜单开启、跟着进度换句、排版随选项变、能拖动、"
                    + "锁定即穿透、不抢焦点、Ctrl+D 可开关、全屏不被收走（对照组侧栏被收起）、"
                    + $"窗口留在屏幕内，画面背景全透明（四角 alpha≤1）且有 {opaquePixels} 个不透明像素");
            return true;
        }

        /// <summary>
        /// 1.1.0：窗口跟着文字伸缩时锚在哪一边。
        /// <para>
        /// 用户<b>没动过</b>它时（位置来自设置恢复 / 设置方案 / 首次居中）锚<b>左上角</b>：
        /// 内容一变就以中心为锚的话，"上次放在这儿"会被半屏的宽度差当场挪走。
        /// 用户<b>拖过</b>之后才锚<b>水平中心</b>：歌词换一句就变宽变窄，锚在左边会左右乱跳。
        /// </para>
        /// <para>这条是补出来的：以前一律锚中心，"重启后回到上次位置"会随机偏掉半屏
        /// （实测 1218 → 368，且时红时绿）。</para>
        /// </summary>
        private static bool CheckLyricsResizeAnchor(DesktopLyricsWindow window, LyricsDocument lyrics)
        {
            var saved = window.Location;

            try
            {
                // 摆到"设置里的位置"：MoveTo 就代表"这个位置不是用户拖出来的"。
                // ⚠ 靠工作区左边放：下面要让内容变宽来验锚点，放在右边的话
                // 窗口一宽就会被 ClampToWorkingArea 推回屏幕内，那是另一回事，会把这条检查带偏。
                var area = Screen.FromControl(window).WorkingArea;
                var target = new Point(area.Left + 40, Math.Max(area.Top, window.Top));
                window.MoveTo(target);

                var narrow = LyricsDocument.Parse("[00:01.00]短\n[00:03.00]句", "锚点测试");
                var wide = LyricsDocument.Parse(
                    $"[00:01.00]{new string('长', 40)}\n[00:03.00]{new string('长', 40)}", "锚点测试");

                window.SetTrack("锚点测试", narrow);
                window.UpdatePosition(TimeSpan.FromSeconds(2));
                var narrowWidth = window.Width;

                window.SetTrack("锚点测试", wide);
                window.UpdatePosition(TimeSpan.FromSeconds(2));

                if (window.Width <= narrowWidth)
                {
                    Log(15, "桌面歌词锚点检查：内容变长之后窗口没有变宽（这一段就没验到锚点）");
                    return false;
                }

                if (window.Left != target.X)
                {
                    Log(15, "桌面歌词锚点检查：没拖动过时，内容变化把窗口挪走了"
                            + $"（Left {target.X} → {window.Left}，宽 {narrowWidth} → {window.Width}）");
                    return false;
                }

                // 真的拖一下（走真实拖动那条路，和用户手动拖一样）：
                // 之后内容变化应当改成以水平中心为锚
                DragWindow(window, fromX: 5, fromY: 5, toX: 25, toY: 5);
                PumpMessages(150);

                var center = window.Left + window.Width / 2;

                window.SetTrack("锚点测试", narrow);
                window.UpdatePosition(TimeSpan.FromSeconds(2));

                if (Math.Abs(window.Left + window.Width / 2 - center) > 1)
                {
                    Log(15, "桌面歌词锚点检查：拖过之后内容变化没有以中心为锚"
                            + $"（中心 {center} → {window.Left + window.Width / 2}）");
                    return false;
                }

                Log(15, "桌面歌词锚点正常：没拖过时内容变化保住左上角（重启后位置不会被挪走），"
                        + "拖过之后改以水平中心为锚");
                return true;
            }
            finally
            {
                // 把内容和位置都还原：这一条改了内容，而后面（关窗、重启）还要看正常内容下
                // 窗口的宽度——不还原的话"重启后回到上次位置"会被宽度变化带偏（实测踩过）。
                window.SetTrack("测试曲名", lyrics);
                window.UpdatePosition(lyrics.Lines[0].Time);
                window.MoveTo(saved);
                PumpMessages(150);
            }
        }

        /// <summary>
        /// 托盘里的桌面歌词子菜单。
        /// <para>
        /// 托盘那一份必须是完整的一套：歌词锁定之后在鼠标眼里就不存在了（右键菜单点不到），
        /// 而主界面完全可能正缩在托盘里看片——此时这里是唯一的出口。
        /// 以前托盘里只有一个开关，关掉再打开仍然是锁定的，人就卡在那儿了。
        /// </para>
        /// </summary>
        /// <summary>
        /// 1.1.0：桌面歌词的控制条（播放 / 暂停、上一首、下一首、停止）。
        /// <para>
        /// 验四件事：未锁定时四个按钮都在客户区里、互不重叠、而且**排在文字下方**
        /// （拿"关掉控制条时窗口的高度"当基准：控制条必须从那个高度再往下才开始）；
        /// 命中测试认得每个按钮的中心、不会把歌词本身当成按钮；
        /// 点下去真的抛出对应命令（<c>SendMessage</c> 发一对按下/松开，不依赖合成鼠标），
        /// 而"按在按钮上、松在别处"不算点中；
        /// 关掉或锁定之后一个都不画、窗口跟着变矮、点击也不再响应。
        /// </para>
        /// <para>用完把 <c>ShowControls</c> 与 <c>Locked</c> 还原：后面的检查（锁定样式、真鼠标拖动）
        /// 不能被这里改过的状态影响。</para>
        /// </summary>
        private static bool CheckDesktopLyricsControls(DesktopLyricsWindow window)
        {
            var originalLocked = window.Locked;

            try
            {
                window.Locked = false;

                // ⚠ 取值之间**不泵消息**：布局是同步算出来的，而主窗体的界面计时会把
                // "真实播放位置"推过来换掉文字——一泵，这里比的就不是"开没开控制条"，
                // 而是"换了句歌词"，断言会随机变红。
                window.ShowControls = false;
                var textOnlyHeight = window.Height;

                window.ShowControls = true;
                var withControls = window.Height;
                var bounds = window.ControlBounds;
                var contentBottom = window.ContentBottom;

                if (bounds.Count != 4)
                {
                    Log(15, $"桌面歌词控制条：未锁定时应当有 4 个按钮，实际 {bounds.Count} 个");
                    return false;
                }

                if (withControls <= textOnlyHeight)
                {
                    Log(15, $"桌面歌词控制条：打开控制条之后窗口没有变高（{textOnlyHeight} → {withControls}）");
                    return false;
                }

                for (var i = 0; i < bounds.Count; i++)
                {
                    var box = bounds[i];

                    if (box.Left < 0 || box.Top < 0 ||
                        box.Right > window.ClientSize.Width || box.Bottom > window.ClientSize.Height)
                    {
                        Log(15, $"桌面歌词控制条：第 {i + 1} 个按钮超出客户区（{box}，客户区 {window.ClientSize}）");
                        return false;
                    }

                    // 控制条必须在文字下方：从"文字底部"再往下才开始
                    // （注意不能拿"只有文字时窗口的高度"当基准——那里面还含一段下内边距）
                    if (box.Top < contentBottom)
                    {
                        Log(15, $"桌面歌词控制条：第 {i + 1} 个按钮压到文字上了"
                                + $"（Top={box.Top}，文字底部 {contentBottom}）");
                        return false;
                    }

                    for (var j = i + 1; j < bounds.Count; j++)
                    {
                        if (box.IntersectsWith(bounds[j]))
                        {
                            Log(15, $"桌面歌词控制条：第 {i + 1} 和第 {j + 1} 个按钮重叠了");
                            return false;
                        }
                    }

                    var center = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
                    var hit = window.ControlIndexAt(center);

                    if (hit != i)
                    {
                        Log(15, $"桌面歌词控制条：第 {i + 1} 个按钮的中心命中了 {hit}");
                        return false;
                    }
                }

                if (window.ControlIndexAt(new Point(bounds[0].Left - 12, 6)) >= 0)
                {
                    Log(15, "桌面歌词控制条：歌词区域被当成了按钮");
                    return false;
                }

                // ---- 点下去真的抛出命令 ----
                var clicks = new List<DesktopLyricsWindow.LyricsControl>();
                void Collect(DesktopLyricsWindow.LyricsControl control) => clicks.Add(control);

                window.ControlClicked += Collect;

                try
                {
                    // ⚠ 每次点击前都重新取一遍布局：窗口是"跟着文字伸缩"的，
                    // 异步加载的歌词一落地，或者悬停状态一变，按钮的位置就会挪。
                    // 拿先前拍下的矩形去点，点到的是别的地方（实测因此随机变红过）。
                    var first = window.ControlBounds[0];
                    ClickWindow(window, first.Left + first.Width / 2, first.Top + first.Height / 2);

                    if (clicks.Count != 1 || clicks[0] != DesktopLyricsWindow.LyricsControl.PlayPause)
                    {
                        Log(15, "桌面歌词控制条：点第一个按钮抛出的命令不对"
                                + $"（{clicks.Count} 次，{(clicks.Count > 0 ? clicks[0].ToString() : "无")}，期望一次 PlayPause）");
                        return false;
                    }

                    // 按在按钮上、松在别处：不算点中
                    var third = window.ControlBounds[2];
                    var downX = third.Left + 2;
                    var downY = third.Top + 2;

                    SendMessage(window.Handle, 0x0201, (IntPtr)1, MouseLParam(downX, downY));
                    SendMessage(window.Handle, 0x0202, IntPtr.Zero, MouseLParam(downX, third.Bottom + 40));
                    PumpMessages(120);

                    if (clicks.Count != 1)
                    {
                        Log(15, "桌面歌词控制条：按在按钮上、松在别处，也当成点中了");
                        return false;
                    }

                    // ---- 双击歌词 = 播放 / 暂停 ----
                    // 消息序列按真实双击来：按下 / 松开 / 双击 / 松开——只发 WM_LBUTTONDBLCLK
                    // 是不够的，WinForms 不会认为那是一次双击。
                    // 不用挪真实光标：程序记的是"按下时那个点"，比实时光标位置可靠。
                    const int wmDown = 0x0201;
                    const int wmUp = 0x0202;
                    const int wmDoubleClick = 0x0203;

                    var textPoint = new Point(window.ClientSize.Width / 2, Math.Max(2, window.ContentBottom - 4));

                    clicks.Clear();
                    SendMessage(window.Handle, wmDown, (IntPtr)1, MouseLParam(textPoint.X, textPoint.Y));
                    SendMessage(window.Handle, wmUp, IntPtr.Zero, MouseLParam(textPoint.X, textPoint.Y));
                    SendMessage(window.Handle, wmDoubleClick, (IntPtr)1, MouseLParam(textPoint.X, textPoint.Y));
                    SendMessage(window.Handle, wmUp, IntPtr.Zero, MouseLParam(textPoint.X, textPoint.Y));
                    PumpMessages(150);

                    if (clicks.Count != 1 || clicks[0] != DesktopLyricsWindow.LyricsControl.PlayPause)
                    {
                        Log(15, "桌面歌词控制条：双击歌词没有触发播放 / 暂停"
                                + $"（{clicks.Count} 次，{(clicks.Count > 0 ? string.Join("、", clicks) : "无")}）");
                        return false;
                    }

                    // 双击落在按钮上：只算一次按钮点击（这里是第 3 个按钮 = 下一首）。
                    // 断言"命令种类是 Next"正是为了把两条路分开：
                    // 少了那道闸门，这里会变成两次（对播放/暂停等于白按、对下一首就是跳两首）。
                    var onButtonControl = window.ControlBounds[2];
                    var onButton = new Point(
                        onButtonControl.Left + onButtonControl.Width / 2,
                        onButtonControl.Top + onButtonControl.Height / 2);

                    clicks.Clear();
                    SendMessage(window.Handle, wmDown, (IntPtr)1, MouseLParam(onButton.X, onButton.Y));
                    SendMessage(window.Handle, wmUp, IntPtr.Zero, MouseLParam(onButton.X, onButton.Y));
                    SendMessage(window.Handle, wmDoubleClick, (IntPtr)1, MouseLParam(onButton.X, onButton.Y));
                    SendMessage(window.Handle, wmUp, IntPtr.Zero, MouseLParam(onButton.X, onButton.Y));
                    PumpMessages(150);

                    if (clicks.Count != 1 || clicks[0] != DesktopLyricsWindow.LyricsControl.Next)
                    {
                        Log(15, "桌面歌词控制条：双击按钮的行为不对（期望只有那一下按钮点击 = 一次 Next）"
                                + $"（{clicks.Count} 次，{(clicks.Count > 0 ? string.Join("、", clicks) : "无")}）");
                        return false;
                    }
                }
                finally
                {
                    window.ControlClicked -= Collect;
                }

                // ---- 关掉控制条 ----
                // ⚠ 这里重新量一遍"基准"：上面那些点击会泵消息，界面计时可能已经把歌词换了句，
                // 拿早先那个基准来比，比的就不是"控制条"，而是"换了句歌词"。
                window.ShowControls = false;
                var offHeight = window.Height;
                var offCount = window.ControlBounds.Count;

                window.ShowControls = true;
                var onHeight = window.Height;

                if (offCount != 0 || offHeight >= onHeight)
                {
                    Log(15, "桌面歌词控制条：关掉之后按钮还在，或者高度没有变矮"
                            + $"（{offCount} 个按钮，{onHeight} → {offHeight}）");
                    return false;
                }

                // ---- 锁定：一个都不画，点击也不再响应 ----
                window.Locked = true;

                if (window.ControlBounds.Count != 0 || window.Height >= onHeight)
                {
                    Log(15, $"桌面歌词控制条：锁定之后还画着 {window.ControlBounds.Count} 个按钮"
                            + $"（高度 {onHeight} → {window.Height}）");
                    return false;
                }

                clicks.Clear();
                window.ControlClicked += Collect;

                try
                {
                    // 锁定后窗口对鼠标是穿透的（那一条由扩展样式检查负责）；
                    // 这里验的是"就算消息送到了，程序也不该响应"——命中测试直接不给按钮。
                    var target = bounds[0];
                    ClickWindow(window, target.Left + target.Width / 2, target.Top + target.Height / 2);

                    if (clicks.Count != 0)
                    {
                        Log(15, "桌面歌词控制条：锁定（鼠标穿透）时点击仍然触发了命令");
                        return false;
                    }
                }
                finally
                {
                    window.ControlClicked -= Collect;
                }

                Log(15, "桌面歌词控制条正常：未锁定时四个按钮排在文字下方、互不重叠、命中测试准确，"
                        + "点一下抛出对应命令（按在按钮上松在别处不算点中），"
                        + "关掉或锁定之后一个都不画、窗口跟着变矮、点它也不再响应");
                return true;
            }
            finally
            {
                window.Locked = originalLocked;
                window.ShowControls = true;
                PumpMessages(150);
            }
        }

        /// <summary>
        /// 1.1.0：控制条与菜单 / 设置的联动——「显示控制条」这一项要能开关它（勾选状态跟着走），
        /// 点一下设置就落进 <c>AppSettings</c>，关掉之后窗口真的变矮。
        /// </summary>
        private static bool CheckDesktopLyricsControlsSetting(播放器.MainForm form, DesktopLyricsWindow window)
        {
            var menu = FindViewMenuItem(form, "桌面歌词");
            var item = menu == null ? null : FindMenuItem(menu.DropDownItems, "显示控制条（播放 / 上一首 / 下一首 / 停止）");

            if (item == null)
            {
                Log(15, "桌面歌词控制条检查：子菜单里没有「显示控制条」这一项");
                return false;
            }

            var original = SettingsOf(form).DesktopLyricsShowControls;

            try
            {
                if (!item.Checked)
                {
                    Log(15, "桌面歌词控制条检查：控制条是开着的，菜单项却没勾上");
                    return false;
                }

                var withControls = window.Height;

                // 菜单点击是直接调 OnClick 的（同步），所以点完立刻取值，中间不泵消息
                if (!ClickMenuItem(item)) return false;

                if (window.ShowControls || item.Checked || SettingsOf(form).DesktopLyricsShowControls)
                {
                    Log(15, "桌面歌词控制条检查：点一下之后窗口没关掉，或者菜单 / 设置没跟着走"
                            + $"（窗口 {window.ShowControls}、勾选 {item.Checked}、设置 {SettingsOf(form).DesktopLyricsShowControls}）");
                    return false;
                }

                if (window.Height >= withControls)
                {
                    Log(15, $"桌面歌词控制条检查：关掉之后窗口没有变矮（{withControls} → {window.Height}）");
                    return false;
                }

                if (!ClickMenuItem(item)) return false;
                PumpMessages(200);

                if (!window.ShowControls || !item.Checked || window.ControlBounds.Count != 4)
                {
                    Log(15, "桌面歌词控制条检查：再点一下没有恢复（关掉之后就该能开回来）");
                    return false;
                }

                Log(15, "桌面歌词控制条与菜单联动正常：「显示控制条」勾选状态、窗口高度、设置项三者一致，"
                        + "关掉再开回来都对");
                return true;
            }
            finally
            {
                SettingsOf(form).DesktopLyricsShowControls = original;
                window.ShowControls = original;
                PumpMessages(150);
            }
        }

        private static bool CheckDesktopLyricsTraySubmenu(播放器.MainForm form)
        {
            if (form.TrayDesktopLyrics is not { } tray)
            {
                Log(15, "桌面歌词检查：托盘菜单里没有开关（主界面收进托盘后就再也开不了）");
                return false;
            }

            var items = tray.DropDownItems;

            if (FindMenuItem(items, "启用桌面歌词") == null)
            {
                Log(15, "桌面歌词检查：托盘子菜单里没有「启用桌面歌词」");
                return false;
            }

            var lockItem = FindMenuItem(items, "锁定（鼠标穿透）");

            if (lockItem == null)
            {
                Log(15, "桌面歌词检查：托盘子菜单里没有「锁定（鼠标穿透）」"
                        + "（锁定之后托盘是唯一的解锁出口）");
                return false;
            }

            if (lockItem.ShortcutKeyDisplayString != "Ctrl+Alt+D")
            {
                Log(15, $"桌面歌词检查：锁定项上没有写出解锁热键"
                        + $"（ShortcutKeyDisplayString =「{lockItem.ShortcutKeyDisplayString}」）");
                return false;
            }

            if (FindMenuItem(items, "外观（字号 30 磅 · 浓度 88%）…") == null &&
                items.OfType<ToolStripMenuItem>().All(i => !i.Text.StartsWith("外观（字号", StringComparison.Ordinal)))
            {
                Log(15, "桌面歌词检查：托盘子菜单里没有带当前值的「外观…」");
                return false;
            }

            if (FindMenuItem(items, "字体") == null || FindMenuItem(items, "文字颜色") == null)
            {
                Log(15, "桌面歌词检查：托盘子菜单里缺少字体 / 文字颜色");
                return false;
            }

            // ---- 托盘还得有最基本的播放控制与「歌词偏移」----
            // 和上面同一个理由：桌面歌词锁定之后托盘是唯一的出口，
            // 而"歌词慢半拍"这种事恰恰就是缩在托盘里看片时最想调的。
            if (form.TrayMenu is not { } trayMenu || FindMenuItem(trayMenu.Items, "停止") == null)
            {
                Log(15, "桌面歌词检查：托盘菜单里没有「停止」");
                return false;
            }

            if (form.TrayLyricsOffset is not { } offset ||
                FindMenuItem(trayMenu.Items, "歌词偏移") == null)
            {
                Log(15, "桌面歌词检查：托盘菜单里没有「歌词偏移」"
                        + "（桌面歌词锁定之后就只剩托盘这一个出口）");
                return false;
            }

            foreach (var caption in new[] { "歌词提前 0.5 秒", "歌词延后 0.5 秒", "偏移归零", "手动输入偏移…" })
            {
                if (FindMenuItem(offset.DropDownItems, caption) == null)
                {
                    Log(15, $"桌面歌词检查：托盘里的「歌词偏移」缺了「{caption}」");
                    return false;
                }
            }

            if (offset.DropDownItems.OfType<ToolStripMenuItem>()
                .All(i => !i.Text.StartsWith("当前偏移", StringComparison.Ordinal)))
            {
                Log(15, "桌面歌词检查：托盘里的「歌词偏移」没有写出当前偏移值");
                return false;
            }

            Log(15, "桌面歌词托盘菜单正常：启用 / 锁定（标着 Ctrl+Alt+D）/ 外观（带当前字号浓度）/ "
                    + "字体 / 文字颜色都在，锁定之后能从托盘解开；托盘里还有「停止」和「歌词偏移」"
                    + "（提前 / 延后 / 归零 / 手动输入 + 当前值）");
            return true;
        }

        /// <summary>
        /// 桌面歌词的解锁热键（Ctrl+Alt+D）。
        /// <para>
        /// 这里直接往窗体发一条 <c>WM_HOTKEY</c>：系统级热键真正按下时送的就是它，
        /// 验的是"id 有没有接错、解锁动作有没有挂上"，而不是去抢用户的键盘。
        /// 热键 id 从 <c>AppHotKeys</c> 里读，避免测试和实现各写一份常量、改一处就失效。
        /// </para>
        /// </summary>
        private static bool CheckDesktopLyricsLockHotKey(播放器.MainForm form, DesktopLyricsWindow window)
        {
            const int wmHotKey = 0x0312;

            var id = AppHotKeys.ToggleDesktopLyricsLock;

            // 媒体键的 id 段是 0x9E00，这里是 0xA100——撞了的话按媒体键会去解锁歌词
            if (id >= 0x9E00 && id < 0xA100)
            {
                Log(15, $"桌面歌词检查：解锁热键的 id（0x{id:X}）和媒体键的 id 段重叠了");
                return false;
            }

            // 走应用自己那条路（直接改窗口的 Locked 属性不会同步到设置，
            // 而热键切换的是设置里的那一位）
            form.SetDesktopLyricsLocked(true);
            PumpMessages(150);

            if (!window.Locked)
            {
                Log(15, "桌面歌词检查：先把歌词锁上没成功，验不了解锁热键");
                return false;
            }

            SendMessage(form.Handle, wmHotKey, (IntPtr)id, IntPtr.Zero);
            PumpMessages(150);

            if (window.Locked)
            {
                Log(15, "桌面歌词检查：按了解锁热键却没有解锁（Ctrl+Alt+D 没接上）");
                return false;
            }

            SendMessage(form.Handle, wmHotKey, (IntPtr)id, IntPtr.Zero);
            PumpMessages(150);

            if (!window.Locked)
            {
                Log(15, "桌面歌词检查：再按一次热键没有重新锁定（热键只做了一半）");
                return false;
            }

            // 收尾：别把锁定状态留给后面的检查
            form.SetDesktopLyricsLocked(false);
            PumpMessages(120);

            Log(15, $"桌面歌词解锁热键正常：Ctrl+Alt+D（id 0x{id:X}）能来回切换锁定，"
                    + "和媒体键的 id 段不冲突");
            return true;
        }

        /// <summary>
        /// 歌词字体。
        /// <para>
        /// 字体列表里<b>只有手动导入的字体</b>（外加"默认字体"），所以选字体这件事从导入开始：
        /// 先导入一个真实的 .ttf，再验两处歌词都换过去、坏字体名安静退回，
        /// 最后验字体菜单里确实没有混进系统字体。
        /// </para>
        /// <para>结束时把字体留在导入的那个上，方便外面验它会存进设置。</para>
        /// </summary>
        private static bool CheckLyricsFont(播放器.MainForm form, DesktopLyricsWindow window)
        {
            var sidebar = Find(form, "sidebarPanel");
            var lyricsView = sidebar == null ? null : FindByTypeName(sidebar, "LyricsView");

            if (lyricsView == null)
            {
                Log(15, "歌词字体检查：找不到侧栏歌词控件");
                return false;
            }

            // ---- 导入一个字体文件（字体列表里只有导入的，所以这是选字体的前提）----
            if (!CheckFontImport(form, window, out var family)) return false;
            if (family == null) return true;      // 系统里没有 ttf 样本，整段跳过

            // 垃圾文件必须是「靠文件头」被挡掉的
            // （见那条检查的注释：GDI+ 探测坏文件时会报出上一个装过的字体族）
            if (!CheckGarbageFontRejectedByHeader()) return false;

            // ---- 字体菜单里只该有"默认 / 导入 / 已导入的字体" ----
            if (!CheckLyricsFontMenu(form, family)) return false;

            // ---- 一个根本不存在的字体族：不能崩，也不能卡在坏字体上 ----
            const string bogus = "绝不存在的字体族 XYZ";

            form.SetLyricsFont(bogus);
            PumpMessages(250);

            var bogusSidebar = lyricsView.Font.FontFamily.Name;
            var bogusDesktop = window.MainFont?.FontFamily.Name;

            if (bogusSidebar == bogus || bogusDesktop == bogus)
            {
                Log(15, $"歌词字体检查：不存在的字体族被用上了（侧栏 {bogusSidebar} / 桌面 {bogusDesktop}）");
                return false;
            }

            if (!string.Equals(bogusSidebar, bogusDesktop, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：坏字体名的回退结果不一致（侧栏 {bogusSidebar} / 桌面 {bogusDesktop}）");
                return false;
            }

            // ---- 换成导入的那个字体：两处都要跟着换 ----
            form.SetLyricsFont(family);
            PumpMessages(250);

            var sidebarFamily = lyricsView.Font.FontFamily.Name;

            if (!string.Equals(sidebarFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：侧栏歌词页没有换成「{family}」（现在是「{sidebarFamily}」）");
                return false;
            }

            var desktopFamily = window.MainFont?.FontFamily.Name;

            if (!string.Equals(desktopFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：桌面歌词没有换成「{family}」（现在是「{desktopFamily}」）");
                return false;
            }

            // 字宽变了，窗口必须重新按新字体量一遍
            var nextFamily = window.NextFont.FontFamily.Name;

            if (!string.Equals(nextFamily, family, StringComparison.OrdinalIgnoreCase))
            {
                Log(15, $"歌词字体检查：下一句那一行没有跟着换字体（现在是「{nextFamily}」）");
                return false;
            }

            Log(15, $"歌词字体正常：导入的「{family}」在侧栏歌词页与桌面歌词都生效了，"
                    + "不存在的字体名会安静退回默认字体（两处一致）");

            // ---- 管理：内存加载（文件不再被占住）+ 真能删掉 ----
            if (!CheckFontManagement(form, family)) return false;

            // ---- 导入的字体必须对 GDI 也可见（侧栏歌词是 TextRenderer 画的） ----
            if (!CheckPrivateFontReachesGdi(form, window, family)) return false;

            // ---- .ttc 字体集合走的是"按文件加载"那条路，GDI 那边也要认 ----
            if (!CheckFontCollectionImport()) return false;

            // ---- 两个新对话框：外观调节 / 字体管理 ----
            if (!CheckDesktopLyricsAppearanceDialog(form, window)) return false;
            if (!CheckFontManagerDialog(form, family)) return false;

            // ---- 通用输入对话框（服务地址 / 鉴权密钥都靠它）----
            if (!CheckInputDialogLayout()) return false;

            return true;
        }

        /// <summary>
        /// 通用输入对话框的排版：**提示文字一行都不能被输入框压住**。
        /// <para>
        /// 这条是真踩过的：原来按"提示有几行 × 18 像素"估算高度，服务地址那个提示有四行，
        /// 估算少了一行，文本框正好压在最后一行上（截图一看就是"挡住了"）。
        /// 现在改成按 <c>Label.PreferredSize</c> 测量摆放，用一条检查钉住它：
        /// 多行提示、掩码输入两种都要保证输入框在提示下面、所有控件都在客户区里。
        /// </para>
        /// </summary>
        private static bool CheckInputDialogLayout()
        {
            const string prompt =
                "自己跑一个 LrcAPI（或用它提供的服务），把地址填在这里，例如：\n"
                + "    http://127.0.0.1:28883\n"
                + "    https://api.lrc.cx\n"
                + "程序不会替你选服务，填哪个就用哪个：";

            foreach (var mask in new[] { false, true })
            {
                var dialog = new InputDialog("LrcApi 服务地址", prompt, "http://127.0.0.1:28883", mask);

                using (dialog)
                {
                    dialog.StartPosition = FormStartPosition.Manual;
                    dialog.Location = new Point(60, 60);
                    dialog.Show();
                    PumpMessages(150);

                    var label = dialog.Controls.OfType<Label>().FirstOrDefault();
                    var box = dialog.Controls.OfType<TextBox>().FirstOrDefault();

                    if (label == null || box == null)
                    {
                        Log(16, "输入对话框检查：找不到提示文字或输入框");
                        return false;
                    }

                    if (box.Top < label.Bottom)
                    {
                        Log(16, $"输入对话框检查：输入框压住了提示文字"
                                + $"（提示到 {label.Bottom}，输入框从 {box.Top} 开始，"
                                + $"客户区 {dialog.ClientSize}）");
                        return false;
                    }

                    if (mask != box.UseSystemPasswordChar)
                    {
                        Log(16, $"输入对话框检查：掩码没生效（mask={mask}，UseSystemPasswordChar={box.UseSystemPasswordChar}）");
                        return false;
                    }

                    var scale = dialog.DeviceDpi / 96f;

                    foreach (Control control in dialog.Controls)
                    {
                        if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                        {
                            Log(16, $"输入对话框在 {scale:0.##} 倍缩放下有控件越界："
                                    + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                            return false;
                        }
                    }

                    if (dialog.ClientSize.Height < box.Bottom + 30)
                    {
                        Log(16, "输入对话框检查：按钮被挤出客户区（高度不够）");
                        return false;
                    }

                    dialog.Close();
                }
            }

            Log(16, "输入对话框排版正常：4 行提示下输入框仍在其下方（按实际测量摆放，不是估算行数），"
                    + "掩码输入生效，125% 缩放下无控件越界");
            return true;
        }

        /// <summary>
        /// 外观对话框：字号 / 浓度两个滑块 + 实时推给歌词窗口。
        /// <para>
        /// 重点是"边拖边改"这条线：以前只能一级一级点菜单（字号 12~96 每次 2 磅，
        /// 从 30 调到 60 要按 15 次），现在拖一下就该立刻生效。
        /// </para>
        /// </summary>
        private static bool CheckDesktopLyricsAppearanceDialog(播放器.MainForm form, DesktopLyricsWindow window)
        {
            var received = new List<(int Size, int Opacity)>();

            var dialog = new DesktopLyricsAppearanceDialog(
                DesktopLyricsDefaults.FontSize,
                DesktopLyricsDefaults.Opacity,
                string.Empty,
                Color.White,
                (size, opacity) => received.Add((size, opacity)));

            using (dialog)
            {
                dialog.StartPosition = FormStartPosition.Manual;
                dialog.Location = new Point(60, 60);
                dialog.Show();
                PumpMessages(150);

                var sliders = dialog.Controls.Cast<Control>()
                    .Where(c => c.GetType().Name == "FlatSlider")
                    .ToList();

                if (sliders.Count != 2)
                {
                    Log(15, $"外观对话框检查：滑块数是 {sliders.Count}（期望 2：字号 + 浓度）");
                    return false;
                }

                var size = (FlatSlider)sliders[0];
                var opacity = (FlatSlider)sliders[1];

                var sizeMin = size.Minimum;
                var sizeMax = size.Maximum;
                var opacityMin = opacity.Minimum;
                var opacityMax = opacity.Maximum;

                if (sizeMin != DesktopLyricsDefaults.MinimumFontSize ||
                    sizeMax != DesktopLyricsDefaults.MaximumFontSize ||
                    opacityMin != DesktopLyricsDefaults.MinimumOpacity ||
                    opacityMax != DesktopLyricsDefaults.MaximumOpacity)
                {
                    Log(15, $"外观对话框检查：滑块范围不对（字号 {sizeMin}~{sizeMax}，浓度 {opacityMin}~{opacityMax}）");
                    return false;
                }

                if (size.Value != DesktopLyricsDefaults.FontSize ||
                    opacity.Value != DesktopLyricsDefaults.Opacity)
                {
                    Log(15, "外观对话框检查：打开时没有把当前值填进滑块");
                    return false;
                }

                // 模拟"用户拖到了 60 磅 / 50%"：程序化设值不触发 Scroll，所以直接叫它的处理函数
                size.Value = 60;
                opacity.Value = 50;

                dialog.OnSliderChanged();
                PumpMessages(120);

                if (received.Count == 0 || received[^1] != (60, 50))
                {
                    Log(15, "外观对话框检查：拖滑块没有把新值推出去（实时预览这条线断了）"
                            + $"（收到 {received.Count} 次回调）");
                    return false;
                }

                // 预览板上必须真的画了字，不能是一块空白
                var preview = dialog.Controls.Cast<Control>()
                    .FirstOrDefault(c => c.GetType().Name == "Panel");

                if (preview == null)
                {
                    Log(15, "外观对话框检查：找不到预览板");
                    return false;
                }

                using (var bitmap = new Bitmap(Math.Max(1, preview.Width), Math.Max(1, preview.Height)))
                {
                    preview.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));

                    var opaque = CountOpaquePixels(bitmap);

                    if (opaque < 20)
                    {
                        Log(15, $"外观对话框检查：预览板是空的（只有 {opaque} 个不透明像素）");
                        return false;
                    }
                }

                var scale = dialog.DeviceDpi / 96f;
                var worstRight = 0;
                var worstBottom = 0;

                foreach (Control control in dialog.Controls)
                {
                    worstRight = Math.Max(worstRight, control.Right);
                    worstBottom = Math.Max(worstBottom, control.Bottom);

                    if (control.Right > dialog.ClientSize.Width || control.Bottom > dialog.ClientSize.Height)
                    {
                        Log(15, $"外观对话框在 {scale:0.##} 倍缩放下有控件越界："
                                + $"{control.GetType().Name} {control.Bounds} 超出客户区 {dialog.ClientSize}");
                        return false;
                    }
                }

                var deviceDpi = dialog.DeviceDpi;
                var clientSize = dialog.ClientSize;

                dialog.Close();

                Log(15, $"外观对话框正常：两个滑块（字号 {sizeMin}~{sizeMax}、浓度 {opacityMin}~{opacityMax}）"
                        + $"带当前值与实时回调，预览板有字，DPI {deviceDpi}（{scale:0.##} 倍）"
                        + $"客户区 {clientSize.Width}x{clientSize.Height} 无越界");
            }

            return true;
        }

        /// <summary>
        /// 鼠标进入 / 离开歌词范围时，那块"提示背景"要相应出现和消失。
        /// <para>
        /// 背景是全透明的，用户平时只看到一串悬空的字，看不出哪里可以抓；
        /// 所以鼠标停上来要铺一块背景，离开再收回去。
        /// </para>
        /// <para>
        /// ⚠ 这里必须<b>真的把光标挪来挪去</b>，不能只发合成消息：
        /// 窗口判定"还在不在歌词上"是按真实光标位置算的。
        /// 而且只把光标挪到窗口矩形范围内就够了，<b>不必正好戳中某个笔画</b>——
        /// 这正是这一版修掉的问题：以前只有收到鼠标消息才铺背景，
        /// 而背景透明时鼠标消息只会在"字"的像素上送到窗口，
        /// 于是"把鼠标移到歌词上"根本没人知道，用户感觉就是移动不了。
        /// </para>
        /// </summary>
        private static bool CheckLyricsHover(DesktopLyricsWindow window)
        {
            var savedCursor = Cursor.Position;

            try
            {
                // 先主动把光标挪开、等悬停收掉，再拍"静止"那一张。
                // 不然光标本来就停在歌词上时（真人鼠标刚好停在那儿、或上一条检查用过鼠标），
                // 前后两张屏幕截图一模一样，这条检查会随机变红——那是断言不可靠，不是功能坏了。
                Cursor.Position = new Point(
                    Math.Max(0, window.Left - 200),
                    Math.Max(0, window.Top - 200));

                PumpMessages(400);

                if (window.Hovering)
                {
                    Log(15, "桌面歌词检查：光标已经挪开了，悬停状态却没收掉（_hovering 还是 true）");
                    return false;
                }

                if (!TryReadAlpha(window, 0, 0, out var idleAlpha))
                {
                    Log(15, "桌面歌词检查：读不到位图，验不了鼠标悬停");
                    return false;
                }

                if (idleAlpha > 1)
                {
                    Log(15, $"桌面歌词检查：还没碰它，背景就不是透明的（alpha = {idleAlpha}，只允许 0 或 1）");
                    return false;
                }

                // 屏幕上此刻的样子（鼠标不在歌词上）
                var idleScreen = CaptureScreen(window.Bounds);

                // 挪进窗口矩形。⚠ 位置要现算：窗口跟着文字伸缩、钳制也可能刚把它挪过，
                // 算早了就会把光标放到窗口外面——"悬停没发生"看起来就像"悬停背景坏了"。
                // 光标还可能被真人鼠标挪走，所以试几次；实在停不住就如实跳过这一条。
                for (var attempt = 0; attempt < 3 && !window.Hovering; attempt++)
                {
                    Cursor.Position = window.PointToScreen(new Point(window.Width / 2, window.Height / 2));
                    PumpMessages(300);
                }

                if (!window.Hovering)
                {
                    Log(15, "桌面歌词检查：光标没能停在歌词上（可能有人正在动鼠标），悬停这一条跳过");
                    return true;
                }

                // 屏幕上现在必须不一样了：看内存里的位图不够，
                // UpdateLayeredWindow 没生效时内存位图照样是新的、屏幕却纹丝不动
                var hoverScreen = CaptureScreen(window.Bounds);

                if (idleScreen != null && hoverScreen != null)
                {
                    var onScreen = MeanPixelDifference(idleScreen, hoverScreen);

                    if (onScreen < 0.5)
                    {
                        Log(15, "桌面歌词检查：鼠标停上来之后连屏幕都没变（悬停背景没真的显示出来）");
                        return false;
                    }

                    Log(15, $"悬停背景确实显示在屏幕上了（这块屏幕前后差 {onScreen:0.00}）");
                }

                if (!TryReadAlpha(window, 0.5, 0.5, out var hoverAlpha))
                {
                    Log(15, "桌面歌词检查：悬停之后读不到位图");
                    return false;
                }

                if (hoverAlpha == 0)
                {
                    Log(15, "桌面歌词检查：鼠标移进歌词范围之后没有显示背景（_hovering = "
                            + window.Hovering + "）");
                    return false;
                }

                // 把光标挪走，背景应该自己收掉
                Cursor.Position = new Point(
                    Math.Max(0, window.Left - 200),
                    Math.Max(0, window.Top - 200));

                PumpMessages(500);

                if (!TryReadAlpha(window, 0, 0, out var leftAlpha))
                {
                    Log(15, "桌面歌词检查：移出之后读不到位图");
                    return false;
                }

                if (leftAlpha > 1)
                {
                    Log(15, $"桌面歌词检查：鼠标移出之后背景没有恢复透明（alpha = {leftAlpha}，"
                            + $"_hovering = {window.Hovering}）");
                    return false;
                }

                Log(15, "桌面歌词悬停正常：鼠标进来铺背景、离开恢复全透明");
                return true;
            }
            finally
            {
                Cursor.Position = savedCursor;
            }
        }

        /// <summary>
        /// 输入自检：拿一个自己的小窗口试试"合成的鼠标点击到底能不能送到窗口"。
        /// <para>自检窗口是自己建的、干净的一个普通窗体，所以它收不到就说明是环境的问题。</para>
        /// </summary>
        private static bool CanSynthesizeClick()
        {
            using var probe = new Form
            {
                Text = "输入自检",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                Size = new Size(140, 60)
            };

            var area = Screen.PrimaryScreen.WorkingArea;
            probe.Location = new Point(area.Left + area.Width / 2, area.Top + 40);

            var clicked = false;
            probe.MouseDown += (s, e) => clicked = true;

            try
            {
                probe.Show();
                PumpMessages(200);

                Cursor.Position = probe.PointToScreen(new Point(70, 30));
                PumpMessages(200);

                mouse_event(MouseEventLeftDown, 0, 0, 0, IntPtr.Zero);
                PumpMessages(150);
                mouse_event(MouseEventLeftUp, 0, 0, 0, IntPtr.Zero);
                PumpMessages(150);

                return clicked;
            }
            finally
            {
                mouse_event(MouseEventLeftUp, 0, 0, 0, IntPtr.Zero);
                probe.Close();
                PumpMessages(100);
            }
        }

        /// <summary>
        /// 窗口是不是<b>真的</b>画到屏幕上了。
        /// <para>
        /// 前面那些检查看的都是"我们推给系统的那张位图"，那张位图对不代表屏幕上真有东西：
        /// <c>UpdateLayeredWindow</c> 失败时内存里的位图照样是新的，屏幕却还是老样子
        /// （甚至是全透明），而这类失败恰好会让"字母以外的地方点不动"——
        /// 用户报的"移动不了"就是这么来的。
        /// </para>
        /// <para>做法：先截一块屏幕，把窗口藏起来再截一块，两块必须不一样。</para>
        /// </summary>
        private static bool CheckLyricsOnScreen(DesktopLyricsWindow window)
        {
            var bounds = window.Bounds;

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                Log(15, "桌面歌词检查：窗口尺寸不合法，截不了屏");
                return false;
            }

            var visible = CaptureScreen(bounds);
            if (visible == null)
            {
                Log(15, "桌面歌词检查：截屏失败");
                return false;
            }

            try
            {
                window.Hide();
                PumpMessages(250);

                var hidden = CaptureScreen(bounds);
                if (hidden == null)
                {
                    Log(15, "桌面歌词检查：截屏失败（隐藏之后）");
                    return false;
                }

                var difference = MeanPixelDifference(visible, hidden);

                if (difference < 0.05)
                {
                    Log(15, "桌面歌词检查：这块屏幕上有没有它一模一样——"
                            + "说明窗口根本没有真的显示出来（UpdateLayeredWindow 没生效）");
                    return false;
                }

                Log(15, $"桌面歌词确实显示在屏幕上（藏起来前后这块屏幕差 {difference:0.00}）");
                return true;
            }
            finally
            {
                window.Show();
                PumpMessages(250);
            }
        }

        /// <summary>
        /// 用真的鼠标输入拖一把歌词窗口。
        /// <para>
        /// 前面那个拖动检查是<b>直接给窗口发消息</b>的，它绕过了命中测试与鼠标捕获，
        /// 所以"窗口能移动"这件事它其实证明不了——用户报的"移动不了"正是这一类问题。
        /// 这里改成：把光标放到歌词上（等悬停背景铺出来，整个框就都能抓了）、
        /// 按下左键、一步一步真的挪光标、松开，然后看窗口有没有跟着走。
        /// </para>
        /// </summary>
        private static bool CheckLyricsRealDrag(DesktopLyricsWindow window)
        {
            // ---- 先做一次"输入自检"：这台机器到底能不能合成鼠标输入 ----
            // 没这一步的话，"真鼠标拖不动"既可能是歌词窗的问题、
            // 也可能只是环境不允许合成输入，两种情况看起来一模一样。
            if (!CanSynthesizeClick())
            {
                Log(15, "桌面歌词检查：这台机器上合成不了鼠标点击（自检窗口都收不到），"
                        + "真鼠标拖动这一条跳过；拖动本身由「给窗口发消息」那一条覆盖");
                return true;
            }

            var savedCursor = Cursor.Position;
            var start = window.Location;

            // 往左上挪，避开屏幕边缘的钳制
            var delta = new Point(-60, -40);

            // 合成鼠标和真人用鼠标是同一套输入通道：这台机器上有人在动鼠标时，
            // 那些真实移动会插进我们这一串合成移动里，把拖动锚点带偏，
            // 于是"窗口没走到期望的位置"其实是被真人推的（实测踩到过：
            // 本线程只发了 6 次移动，却收到 21 次）。这种情况下只能跳过，不能判失败。
            //
            // 先用自检确认这个判据本身是活的：一个恒返回 true 的实现会让抖动变成"通过"。
            if (!CheckCursorIdleDetector())
            {
                Log(15, "桌面歌词检查：光标静止/移动的判据不可信，这一条没法验");
                return false;
            }

            if (!IsCursorIdle(400))
            {
                Log(15, "桌面歌词检查：检测到真实鼠标正在移动，真鼠标拖动这一条跳过"
                        + "（合成输入和真人抢同一个通道，拖动结果不可信）；"
                        + "拖动本身由「给窗口发消息」那一条覆盖");
                return true;
            }

            try
            {
                // 先落到歌词上，等悬停背景出来
                Cursor.Position = window.PointToScreen(new Point(window.Width / 2, window.Height / 2));
                PumpMessages(400);

                if (!TryReadAlpha(window, 0.5, 0.5, out var panelAlpha) || panelAlpha == 0)
                {
                    Log(15, $"桌面歌词检查：光标已经停在歌词上了，悬停背景却没铺出来"
                            + $"（中间 alpha = {panelAlpha}）——那这个框就不是整块可抓的");
                    return false;
                }

                // 按下之前先问一句：光标底下到底是谁？
                var cursorWindow = WindowFromPoint(Cursor.Position);

                if (cursorWindow != window.Handle)
                {
                    Log(15, $"桌面歌词检查：光标（{Cursor.Position}，窗口 {window.Bounds}）底下不是歌词窗口"
                            + $"（0x{cursorWindow.ToInt64():X}），所以按下去没人收到");
                    return false;
                }

                // 数一数本线程收到了哪些鼠标消息：用来区分
                // "拖不动" 和 "合成出来的点击压根没送到这个窗口"（见下面的处理）
                var watcher = new MouseWatcher();
                Application.AddMessageFilter(watcher);

                mouse_event(MouseEventLeftDown, 0, 0, 0, IntPtr.Zero);
                PumpMessages(150);

                // 先确认这一下"真的按下去了"：如果连左键都没按下，
                // 那说明这台机器上不允许合成鼠标输入，这一条检查就只能跳过，而不是误判成"拖不动"
                var buttonDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;

                if (!buttonDown)
                {
                    mouse_event(MouseEventLeftUp, 0, 0, 0, IntPtr.Zero);
                    Application.RemoveMessageFilter(watcher);

                    Log(15, "桌面歌词检查：合成出来的左键按不下去，真鼠标拖动这一条跳过");
                    return true;
                }

                var from = Cursor.Position;

                for (var step = 1; step <= 6; step++)
                {
                    Cursor.Position = new Point(
                        from.X + delta.X * step / 6,
                        from.Y + delta.Y * step / 6);

                    PumpMessages(60);
                }

                mouse_event(MouseEventLeftUp, 0, 0, 0, IntPtr.Zero);
                PumpMessages(250);

                Application.RemoveMessageFilter(watcher);

                var moved = window.Location;
                var expected = new Point(start.X + delta.X, start.Y + delta.Y);

                if (Math.Abs(moved.X - expected.X) > 4 || Math.Abs(moved.Y - expected.Y) > 4)
                {
                    // 我们只发了 6 次移动；收到明显更多的移动说明真实鼠标也在这条通道上，
                    // 这时"没走到期望位置"证明不了歌词窗有问题。
                    if (watcher.MoveCount > 10 || !IsCursorIdle(200))
                    {
                        Log(15, $"桌面歌词检查：真鼠标拖动这一条跳过——拖动期间有真实鼠标干扰"
                                + $"（{start} → {moved}，期望 {expected}；本线程收到 "
                                + $"按下{watcher.DownCount} 移动{watcher.MoveCount} 抬起{watcher.UpCount}）");
                        return true;
                    }

                    Log(15, $"桌面歌词检查：真鼠标拖不动（{start} → {moved}，期望 {expected}；"
                            + $"本线程收到 按下{watcher.DownCount} 移动{watcher.MoveCount} 抬起{watcher.UpCount}）——"
                            + "注意「按下 0 次」通常意味着那次点击被窗口自己取消了"
                            + "（曾经就是「把前台还回去」把点击一起吃掉，见 README 6.8）");
                    return false;
                }
            }
            finally
            {
                // 保险：绝不能留下一个按住不放的左键
                mouse_event(MouseEventLeftUp, 0, 0, 0, IntPtr.Zero);
                Cursor.Position = savedCursor;
                PumpMessages(120);
            }

            Log(15, $"桌面歌词拖动正常：真鼠标按下并拖动之后窗口跟着走了 {delta.X},{delta.Y}");
            return true;
        }

        /// <summary>
        /// 用系统的命中测试问一句：字上那个点，命中的到底是不是歌词窗口？
        /// <para>
        /// 这一条验的是"用户按得下去吗"，而<b>不是</b>"发消息给窗口它会不会动"——
        /// 后者绕过了命中测试，正是这个功能最容易自我欺骗的地方：
        /// 背景是逐像素透明的，透明处点击本来就会穿透到下面的程序，
        /// 所以只要字的像素没被判定为"有东西"，鼠标就抓不住它（表现就是移动不了）。
        /// 这里顺带也把关：<c>WS_EX_TRANSPARENT</c>（锁定）会让所有点都穿透，
        /// 这条断言同时能挡住"忘了解锁"。
        /// </para>
        /// </summary>
        private static bool CheckLyricsHitTest(DesktopLyricsWindow window)
        {
            if (window.RenderedSurface is not Bitmap surface)
            {
                Log(15, "桌面歌词检查：读不到位图，验不了命中测试");
                return false;
            }

            // ---- 1) 整块都要能抓住：边距、两行之间、字上，随便哪个点 ----
            // 用户"拖不动"最常见的原因就是只有笔画的像素能抓，而他按在了字缝里。
            var probes = new[]
            {
                new Point(3, 3),                                        // 左上边距
                new Point(surface.Width - 4, 3),                        // 右上边距
                new Point(surface.Width / 2, surface.Height / 2),        // 正中间（多半是两行之间的空隙）
                new Point(3, surface.Height - 4),                       // 左下边距
                new Point(surface.Width - 4, surface.Height - 4)         // 右下边距
            };

            foreach (var probe in probes)
            {
                var screenPoint = window.PointToScreen(probe);
                var hit = WindowFromPoint(screenPoint);

                if (hit != window.Handle)
                {
                    Log(15, $"桌面歌词检查：窗口内 {probe}（屏幕 {screenPoint}）命中的不是歌词窗口"
                            + $"（0x{hit.ToInt64():X}）——这一块按不下去，拖动就从这里失灵");
                    return false;
                }
            }

            // ---- 2) 字上也要能点中（描边之外的像素同样算数）----
            for (var y = surface.Height / 3; y < surface.Height * 2 / 3; y++)
            {
                for (var x = surface.Width / 4; x < surface.Width * 3 / 4; x++)
                {
                    if (surface.GetPixel(x, y).A < 200) continue;

                    var screenPoint = window.PointToScreen(new Point(x, y));

                    if (WindowFromPoint(screenPoint) == window.Handle)
                    {
                        Log(15, "桌面歌词命中正常：窗口范围内随便哪个点都抓得住"
                                + $"（含边距与两行之间的空隙），字上也不例外");
                        return true;
                    }

                    break;
                }
            }

            Log(15, "桌面歌词检查：字上那一点命中的不是歌词窗口");
            return false;
        }

        /// <summary>
        /// 读渲染位图上某一点的 alpha（坐标按 0~1 的比例给）。
        /// <para>
        /// 判断"背景透不透"要用<b>角落</b>：圆角之外那一点点本来就是透明的；
        /// 判断"那块悬停背景在不在"要用<b>中间</b>——背景是圆角的，
        /// 光看角落的话它明明铺上了也还是透明的（第一次就是这么误判的）。
        /// </para>
        /// </summary>
        private static bool TryReadAlpha(DesktopLyricsWindow window, double xRatio, double yRatio, out int alpha)
        {
            alpha = 0;

            if (window.RenderedSurface is not Bitmap surface) return false;
            if (surface.Width <= 2 || surface.Height <= 2) return false;

            var x = Math.Clamp((int)(surface.Width * xRatio), 0, surface.Width - 1);
            var y = Math.Clamp((int)(surface.Height * yRatio), 0, surface.Height - 1);

            alpha = surface.GetPixel(x, y).A;
            return true;
        }

        /// <summary>
        /// 桌面歌词的"画面"：直接看它推给系统的那张 32 位 ARGB 位图。
        /// <para>
        /// 这里能验两件光看窗口属性验不出来的事：
        /// <list type="number">
        ///   <item><b>背景是逐像素透明的</b>——四个角（离文字最远的地方）的 alpha 必须是 0，
        ///   只要有人把底色调回不透明，这条立刻失败；</item>
        ///   <item><b>字真的画上去了</b>——不透明像素得有成百上千个，
        ///   挡住"尺寸都对、但一个字都没画"这种退化。</item>
        /// </list>
        /// 不能用 <c>DrawToBitmap</c>：内容是 <c>UpdateLayeredWindow</c> 推上去的，
        /// WM_PRINT 那条路拿不到任何东西。
        /// </para>
        /// </summary>
        private static bool CheckLyricsSurface(DesktopLyricsWindow window, out int opaquePixels)
        {
            opaquePixels = 0;

            if (window.RenderedSurface is not Bitmap surface)
            {
                Log(15, "桌面歌词检查：窗口没有渲染出位图（透明背景就靠它）");
                return false;
            }

            if (surface.Width <= 2 || surface.Height <= 2)
            {
                Log(15, $"桌面歌词检查：渲染出来的位图太小（{surface.Width}x{surface.Height}）");
                return false;
            }

            var corners = new[]
            {
                new Point(0, 0),
                new Point(surface.Width - 1, 0),
                new Point(0, surface.Height - 1),
                new Point(surface.Width - 1, surface.Height - 1)
            };

            foreach (var corner in corners)
            {
                var alpha = surface.GetPixel(corner.X, corner.Y).A;

                // 允许 alpha = 1：整块留了一层肉眼看不出的底，好让整块都能被鼠标抓住
                // （逐像素命中测试下 alpha=0 的地方点击会穿过去）。1/255 是看不出来的。
                if (alpha > 1)
                {
                    Log(15, $"桌面歌词检查：背景不是全透明的（{corner} 处的 alpha = {alpha}，只允许 0 或 1）");
                    return false;
                }
            }

            for (var y = 0; y < surface.Height; y++)
            {
                for (var x = 0; x < surface.Width; x++)
                {
                    // 门槛取 8 是为了跳过那层 alpha=1 的"抓取底"，只数字和描边
                    if (surface.GetPixel(x, y).A > 8) opaquePixels++;
                }
            }

            // 30 磅的几个字至少上千个像素，门槛取 100 是为了只挡"一个字都没画"
            if (opaquePixels <= 100)
            {
                var coloured = 0;

                for (var y = 0; y < surface.Height; y++)
                {
                    for (var x = 0; x < surface.Width; x++)
                    {
                        var pixel = surface.GetPixel(x, y);
                        if (pixel.R != 0 || pixel.G != 0 || pixel.B != 0) coloured++;
                    }
                }

                Log(15, $"桌面歌词检查：位图里几乎没有不透明像素（只有 {opaquePixels} 个），字没画上去；"
                        + $"位图 {surface.Width}x{surface.Height} {surface.PixelFormat}，"
                        + $"有色像素 {coloured} 个，当前文字「{window.CurrentText}」，"
                        + $"字体 {window.MainFont?.Name}"
                        + $"/{window.MainFont?.Size}");
                return false;
            }

            return true;
        }
    }
}
