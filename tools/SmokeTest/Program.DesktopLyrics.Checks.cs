// 桌面歌词 —— 主检查（置顶、换句、排版、锁定、全屏、超长句、配色）。
// 从 Program.DesktopLyrics.cs 按 ARCHITECTURE 17.2 整块搬来，成员一个字没动。
namespace SmokeTest
{
    internal static partial class Program
    {        /// <summary>
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
            // R2：这一步有时 20 秒、有时 160 秒（见维护手册 R2 / M1）。慢块计时只记"超过 1 秒"的段落，
            // 正常跑不留噪音；日志里出现"第15步慢块"就知道时间花在哪一段。
            var sectionClock = System.Diagnostics.Stopwatch.StartNew();

            void Section(string name)
            {
                var ms = sectionClock.ElapsedMilliseconds;
                if (ms >= 1000) Journal($"第15步慢块：{name} {ms} ms");
                sectionClock.Restart();
            }

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

            Section("开窗、置顶、拖动");
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

            Section("跟着进度换句");
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

            Section("双语歌词");
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

            Section("排版与不透明度");
            // ---- 1.1.0：控制条（播放 / 暂停、上一首、下一首、停止）----
            if (!CheckDesktopLyricsControls(window)) return false;
            Section("控制条（按钮与点击）");

            if (!CheckDesktopLyricsControlsSetting(form, window)) return false;
            Section("控制条（设置与联动）");

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

            Section("控制条（字号）");
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

            Section("不透明度钳制");
            // ---- 画面：背景必须真的透明，字必须真的画上去了 ----
            if (!CheckLyricsSurface(window, out var opaquePixels)) return false;

            Section("画面透明（截屏）");
            // ---- 鼠标停在歌词上时要铺一块背景（否则用户看不出该抓哪儿）----
            if (!CheckLyricsHover(window)) return false;

            Section("悬停背景");
            // ---- 字上那一点真的能被点中（"能不能拖动"就靠这个）----
            if (!CheckLyricsHitTest(window)) return false;

            Section("命中测试");
            // ---- 它是不是真的显示在屏幕上（而不只是我们内存里有张位图）----
            if (!CheckLyricsOnScreen(window)) return false;

            Section("截屏比对");
            // ---- 真鼠标拖一把：这才叫"能移动" ----
            if (!CheckLyricsRealDrag(window)) return false;

            Section("真鼠标拖动");
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

            Section("拖动");
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

            Section("锁定");
            // ---- 解锁热键（Ctrl+Alt+D）：锁定之后托盘之外的另一个出口 ----
            if (!CheckDesktopLyricsLockHotKey(form, window)) return false;

            Section("解锁热键");
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

            Section("右键菜单");
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

            Section("全屏");
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

            Section("超长句");
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

            Section("配色");
            // ---- 歌词字体：两处歌词要一起换，坏字体名不能把界面搞坏 ----
            if (!CheckLyricsFont(form, window)) return false;

            Section("字体");
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

            Section("关掉再开");
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
    }
}
