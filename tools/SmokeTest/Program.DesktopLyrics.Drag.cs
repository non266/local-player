// 桌面歌词 —— 拖动与命中（合成鼠标真拖一把、点在哪一格、能不能合成点击）。
//
// 从 Program.DesktopLyrics.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

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
    }
}
