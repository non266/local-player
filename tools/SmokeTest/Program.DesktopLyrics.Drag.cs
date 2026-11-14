// 桌面歌词 —— 拖动与命中（合成鼠标真拖一把、点在哪一格、能不能合成点击）。
//
// 从 Program.DesktopLyrics.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

using System.Diagnostics;

namespace SmokeTest
{
    internal static partial class Program
    {
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
            // 1.21.0：先把"环境不成立"与"功能坏了"的分界本身断言一遍（不依赖真前台）。
            if (!CheckLyricsDragDecision()) return false;

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

            try
            {
                // ---- 1.21.0 三态：最多试 3 次，只有"光标在我们窗口上、也静止"才真的拖 ----
                // 1.20.0 装机验收里出现过："光标底下不是歌词窗口（0x…），所以按下去没人收到"
                // 直接判红——而当时只是别的程序盖在歌词上面。那不是功能坏了，是环境不成立。
                var proceed = false;
                var lastReason = string.Empty;

                for (var attempt = 0; attempt < 3 && !proceed; attempt++)
                {
                    // 先落到歌词上，等悬停背景出来
                    Cursor.Position = window.PointToScreen(new Point(window.Width / 2, window.Height / 2));
                    PumpMessages(350);

                    var cursorWindow = WindowFromPoint(Cursor.Position);
                    var hitOurs = cursorWindow == window.Handle;
                    var hitOther = false;
                    uint hitPid = 0;
                    var hitName = "?";

                    if (!hitOurs)
                    {
                        hitOther = IsOtherProcessWindow(cursorWindow, out hitPid, out hitName);
                    }

                    var verdict = DecideLyricsDragHit(
                        window.Bounds.Contains(Cursor.Position),
                        IsCursorIdle(400),
                        hitOurs,
                        hitOther,
                        out var reason);

                    if (verdict == LyricsDragVerdict.Fail)
                    {
                        Log(15, "桌面歌词检查：真鼠标拖不动——光标底下既不是歌词窗口、也不属于别的程序"
                                + $"（光标 {Cursor.Position}，窗口 {window.Bounds}，命中 "
                                + $"0x{cursorWindow.ToInt64():X}）");
                        return false;
                    }

                    if (verdict == LyricsDragVerdict.SkipEnvironment)
                    {
                        lastReason = reason;
                        Log(15, $"桌面歌词检查：真鼠标拖动这一条这次不计——{reason}"
                                + $"（尝试 {attempt + 1}/3；光标 {Cursor.Position}，窗口 {window.Bounds}，"
                                + $"命中 0x{cursorWindow.ToInt64():X}（pid {hitPid}，{hitName}），"
                                + $"{DescribeForeground()}）");
                        PumpMessages(250);
                        continue;
                    }

                    proceed = true;
                }

                if (!proceed)
                {
                    Log(15, $"桌面歌词检查：真鼠标拖动这一条跳过（环境三次都不成立：{lastReason}）；"
                            + "拖动本身由「给窗口发消息」那一条覆盖");
                    return true;
                }

                if (!TryReadAlpha(window, 0.5, 0.5, out var panelAlpha) || panelAlpha == 0)
                {
                    Log(15, $"桌面歌词检查：光标已经停在歌词上了，悬停背景却没铺出来"
                            + $"（中间 alpha = {panelAlpha}）——那这个框就不是整块可抓的");
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
                            + "（曾经就是「把前台还回去」把点击一起吃掉，见 README「桌面歌词：一个\"不该被注意到\"的窗口」）");
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
        /// 真鼠标拖动的三态判定（1.21.0）。
        /// <para>
        /// 为什么不"点不到就失败"：歌词窗口是置顶的，但别人的置顶窗口 / 前台窗口一样能盖住它；
        /// 那时候光标底下本来就不是歌词窗口，按下去当然没人收到——这是<b>环境</b>。
        /// 只有"光标就在我们窗口上、也不属于别的程序，却还是拖不动"才是<b>bug</b>。
        /// </para>
        /// <para>
        /// 1.20.0 装机验收里混过一次这两种情况（`光标（…）底下不是歌词窗口…` 直接判红，
        /// 紧接着串行重跑就绿）。跳过必须打印原因，不许静默通过。
        /// </para>
        /// </summary>
        private enum LyricsDragVerdict
        {
            /// <summary>光标在我们窗口上、也静止：可以真的按下去拖。</summary>
            Proceed,

            /// <summary>环境不成立（光标不在窗口里 / 有人在动鼠标 / 被别的程序盖住）：跳过并出声。</summary>
            SkipEnvironment,

            /// <summary>光标在窗口里、也不是别人的窗口，却收不到点击：真失败。</summary>
            Fail
        }

        /// <summary>纯判定：不依赖真实前台，冒烟直接调它把三种结论都走一遍。</summary>
        private static LyricsDragVerdict DecideLyricsDragHit(
            bool cursorInWindow,
            bool cursorIdle,
            bool hitIsOurs,
            bool hitOtherProcess,
            out string reason)
        {
            reason = string.Empty;

            if (!cursorInWindow)
            {
                reason = "光标已经不在歌词窗口里";
                return LyricsDragVerdict.SkipEnvironment;
            }

            if (!cursorIdle)
            {
                reason = "有人在动真鼠标（合成输入和真人抢同一个通道）";
                return LyricsDragVerdict.SkipEnvironment;
            }

            if (hitIsOurs) return LyricsDragVerdict.Proceed;

            if (hitOtherProcess)
            {
                reason = "被别的程序盖住";
                return LyricsDragVerdict.SkipEnvironment;
            }

            reason = "光标底下既不是歌词窗口、也不属于别的程序";
            return LyricsDragVerdict.Fail;
        }

        /// <summary>三态判定本身的自检（不依赖真的前台窗口，任何机器上都跑得出来）。</summary>
        private static bool CheckLyricsDragDecision()
        {
            if (DecideLyricsDragHit(true, true, true, false, out _) != LyricsDragVerdict.Proceed)
            {
                Log(15, "桌面歌词检查：三态判定不对（光标在我们窗口上时不算「可以拖」）");
                return false;
            }

            if (DecideLyricsDragHit(false, true, false, false, out var outOfWindow) != LyricsDragVerdict.SkipEnvironment ||
                outOfWindow.Length == 0)
            {
                Log(15, "桌面歌词检查：三态判定不对（光标不在窗口里时没有跳过，也没有说原因）");
                return false;
            }

            if (DecideLyricsDragHit(true, false, false, false, out var moving) != LyricsDragVerdict.SkipEnvironment ||
                moving.Length == 0)
            {
                Log(15, "桌面歌词检查：三态判定不对（有人在动鼠标时没有跳过，也没有说原因）");
                return false;
            }

            if (DecideLyricsDragHit(true, true, false, true, out var covered) != LyricsDragVerdict.SkipEnvironment ||
                covered.Length == 0 || !covered.Contains("别的程序", StringComparison.Ordinal))
            {
                Log(15, "桌面歌词检查：三态判定不对（光标被别的程序盖住时会被判成功能坏了）");
                return false;
            }

            if (DecideLyricsDragHit(true, true, false, false, out _) != LyricsDragVerdict.Fail)
            {
                Log(15, "桌面歌词检查：三态判定不对（光标底下是我们自己的窗口时被当成了环境问题）");
                return false;
            }

            return true;
        }

        /// <summary>命中的窗口是不是别的进程的（是 → 环境问题；我们自己进程的 → 功能问题）。</summary>
        private static bool IsOtherProcessWindow(IntPtr window, out uint pid, out string processName)
        {
            pid = 0;
            processName = "?";

            if (window == IntPtr.Zero) return false;

            GetWindowThreadProcessId(window, out pid);

            if (pid == 0) return false;

            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName;
            }
            catch (Exception)
            {
                processName = "?";
            }

            return pid != (uint)Environment.ProcessId;
        }

        /// <summary>跳过时把前台窗口写进日志（排查"到底是谁盖住了它"）。</summary>
        private static string DescribeForeground()
        {
            var foreground = GetForegroundWindow();

            if (foreground == IntPtr.Zero) return "前台：无";

            GetWindowThreadProcessId(foreground, out var pid);

            return $"前台 0x{foreground.ToInt64():X}（pid {pid}）";
        }
    }
}
