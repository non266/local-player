// 桌面歌词 —— 控制条与托盘（按钮布局 / 点击，托盘子菜单，解锁热键）。
//
// 从 Program.DesktopLyrics.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
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
    }
}
