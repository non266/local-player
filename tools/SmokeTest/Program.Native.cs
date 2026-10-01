// Win32 互操作与输入合成：窗口样式、发消息、鼠标键盘合成、截图比对、渲染小工具。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 往指定窗口投一次真实的"按下 + 抬起"（走消息队列，和用户按键同一条路）。
        /// <para>
        /// ⚠ <c>lParam</c> 必须按真实按键填：扫描码 + 抬起时的 bit30/bit31。
        /// 曾经图省事写成 <c>lParam = 0</c>，结果一次"按下+抬起"往文本框里塞进了两个字符——
        /// 全 0 的 lParam 让那条 WM_KEYUP 看起来像又一次按下（实测：
        /// 只发 KEYUP 不进字；KEYDOWN + 真实 KEYUP 进一个字；KEYDOWN + lParam=0 的 KEYUP 进两个字）。
        /// </para>
        /// </summary>
        private static void TypeKey(IntPtr hwnd, Keys key)
        {
            var code = key & Keys.KeyCode;
            var virtualKey = (uint)code;
            var scan = MapVirtualKey(virtualKey, 0);   // MAPVK_VK_TO_VSC

            // 方向键 / Delete / Home 这类是"扩展键"，lParam 的第 24 位要置上
            var extended = code is Keys.Delete or Keys.Insert or Keys.Left or Keys.Right
                or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown;

            var baseValue = 1L | ((long)scan << 16) | (extended ? 0x01000000L : 0L);

            PostMessage(hwnd, 0x0100, (IntPtr)virtualKey, (IntPtr)baseValue);                  // WM_KEYDOWN
            PostMessage(hwnd, 0x0101, (IntPtr)virtualKey, (IntPtr)(baseValue | 0xC0000000L));   // WM_KEYUP
        }

        /// <summary>把一个控件画进位图（失败返回 <c>null</c>）。</summary>
        private static Bitmap? RenderControl(Control control)
        {
            try
            {
                var width = Math.Max(1, control.Width);
                var height = Math.Max(1, control.Height);

                var bitmap = new Bitmap(width, height);
                control.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));

                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>往窗口里发一串鼠标消息，模拟一次真实的拖动。</summary>
        private static void DragWindow(Form window, int fromX, int fromY, int toX, int toY)
        {
            const int wmLButtonDown = 0x0201;
            const int wmMouseMove = 0x0200;
            const int wmLButtonUp = 0x0202;
            const int mkLButton = 0x0001;

            SendMessage(window.Handle, wmLButtonDown, (IntPtr)mkLButton, MouseLParam(fromX, fromY));
            SendMessage(window.Handle, wmMouseMove, (IntPtr)mkLButton, MouseLParam(toX, toY));
            SendMessage(window.Handle, wmLButtonUp, IntPtr.Zero, MouseLParam(toX, toY));
            PumpMessages(150);
        }

        private static IntPtr MouseLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        /// <summary>
        /// 这段时间里光标有没有动过（用来判断"这台机器上有没有真人正在用鼠标"）。
        /// <para>
        /// 合成鼠标输入和真人鼠标走的是同一条通道，分不清来源；所以只能在动手之前
        /// 先确认这段时间没人碰鼠标，否则结果不可信。
        /// </para>
        /// </summary>
        private static bool IsCursorIdle(int milliseconds)
        {
            var first = Cursor.Position;
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);

                if (Cursor.Position != first) return false;
            }

            return true;
        }

        /// <summary>
        /// 自检 <see cref="IsCursorIdle"/>：它必须<b>真的能发现光标被挪动</b>。
        /// <para>
        /// 不然一个"恒返回 true"的实现会让上面那条跳过逻辑永远不生效——
        /// 抖动看起来好了，其实只是判据瞎了。
        /// </para>
        /// </summary>
        private static bool CheckCursorIdleDetector()
        {
            var saved = Cursor.Position;

            try
            {
                // 1) 光标不动时：必须说"没人动"
                if (!IsCursorIdle(120))
                {
                    Log(15, "桌面歌词检查：光标没动，IsCursorIdle 却说动了（判据太敏感）");
                    return false;
                }

                // 2) 从另一个线程挪一下光标：必须说"有人在动"
                var moved = new ManualResetEventSlim(false);

                var thread = new Thread(() =>
                {
                    moved.Wait(TimeSpan.FromSeconds(2));
                    Cursor.Position = new Point(saved.X + 37, saved.Y + 23);
                })
                {
                    IsBackground = true
                };

                thread.Start();
                moved.Set();

                var detected = !IsCursorIdle(600);

                if (!detected)
                {
                    Log(15, "桌面歌词检查：光标被挪动了，IsCursorIdle 却说没动——"
                            + "那这条「跳过」的判据是坏的，抖动会被当成通过");
                    return false;
                }

                return true;
            }
            finally
            {
                Cursor.Position = saved;
                PumpMessages(60);
            }
        }

        /// <summary>
        /// 真的"点"一个菜单项。
        /// <para>
        /// 菜单没展开时 <c>PerformClick()</c> 什么也不做（<c>ToolStripItem.Available</c> 是 false），
        /// 所以这里直接调它受保护的 <c>OnClick</c>——要验的是"点下去接的是什么"，
        /// 而不是 WinForms 有没有把菜单展开。
        /// </para>
        /// <para>
        /// ⚠ 带 <c>CheckOnClick</c> 的项在 <c>OnClick</c> 里会先翻转 <c>Checked</c> 再抛事件，
        /// 所以调用方<b>不要</b>先手动改 <c>Checked</c>，否则翻回来的正好相反。
        /// </para>
        /// </summary>
        private static bool ClickMenuItem(ToolStripMenuItem item)
        {
            var onClick = item.GetType().GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance);

            if (onClick == null) return false;

            onClick.Invoke(item, new object[] { EventArgs.Empty });
            return true;
        }

        /// <summary>走真正的 <c>ProcessCmdKey</c>，验证快捷键真的接上了。</summary>
        private static bool SendShortcut(Form form, Keys keyData) =>
            form is 播放器.MainForm main && main.ProcessShortcut(keyData);

        /// <summary>
        /// 数一数本线程收到的鼠标消息（<c>WM_*BUTTON*</c> / <c>WM_MOUSEMOVE</c>）。
        /// <para>用来区分"窗口拖不动"和"合成出来的点击根本没送到这个窗口"。</para>
        /// </summary>
        private sealed class MouseWatcher : IMessageFilter
        {
            public int DownCount;
            public int MoveCount;
            public int UpCount;

            public bool PreFilterMessage(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x0201: DownCount++; break;
                    case 0x0200: MoveCount++; break;
                    case 0x0202: UpCount++; break;
                }

                return false;
            }
        }

        private static Bitmap CaptureScreen(Rectangle bounds)
        {
            try
            {
                var bitmap = new Bitmap(bounds.Width, bounds.Height);
                using var g = Graphics.FromImage(bitmap);
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(bounds.Width, bounds.Height));
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double MeanPixelDifference(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return 255;

            long total = 0;
            var count = 0;

            for (var y = 0; y < a.Height; y++)
            {
                for (var x = 0; x < a.Width; x++)
                {
                    var left = a.GetPixel(x, y);
                    var right = b.GetPixel(x, y);

                    total += Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);
                    count += 3;
                }
            }

            return count == 0 ? 0 : total / (double)count;
        }

        /// <summary>取窗口的扩展样式（32/64 位各用各的入口）。</summary>
        private static int ReadExStyle(IntPtr handle)
        {
            const int gwlExStyle = -20;

            return IntPtr.Size == 8
                ? (int)GetWindowLongPtr64(handle, gwlExStyle).ToInt64()
                : GetWindowLong32(handle, gwlExStyle);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetTextFaceW(IntPtr hdc, int count, System.Text.StringBuilder faceName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);

        private const uint MouseEventLeftDown = 0x0002;

        private const uint MouseEventLeftUp = 0x0004;

        /// <summary>合成真实鼠标输入（拖动的检查必须走真实输入，否则绕过了命中测试与鼠标捕获）。</summary>
        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extraInfo);

        private const int VkLeftButton = 0x01;

        /// <summary>问系统"左键现在是不是真的按着"——用来确认上面那次合成输入生效了。</summary>
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// 抢占一个全局热键（系统级）。
        /// <para>
        /// 用来验"媒体键被别人占了的时候，程序有没有说清是哪几个"：同一个虚拟键系统只允许
        /// 一个窗口注册，所以测试先占住它，再开主窗体，那边必然少注册一个。
        /// </para>
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private static bool TryRender(Control control, int width, int height, out string error)
        {
            error = string.Empty;

            if (width <= 0 || height <= 0) return true;

            try
            {
                using var bitmap = new Bitmap(width, height);
                control.DrawToBitmap(bitmap, new Rectangle(0, 0, width, height));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>勾选 / 取消勾选一个 CheckOnClick 的菜单项（PerformClick 自己会翻转状态）。</summary>
        private static void SetMenuChecked(ToolStripMenuItem item, bool value)
        {
            if (item.Checked != value) item.PerformClick();
        }

        /// <summary>
        /// 在「视图」菜单里按文字找菜单项。
        /// <para>设计器给菜单文字加了助记符后缀（<c>显示播放列表(&amp;L)</c>），
        /// 所以比较前要把它去掉，否则永远匹配不上。</para>
        /// </summary>
        private static ToolStripMenuItem FindViewMenuItem(Form form, string text)
        {
            var menu = form.MainMenuStrip;
            if (menu == null) return null;

            foreach (ToolStripItem top in menu.Items)
            {
                if (top is not ToolStripMenuItem topItem) continue;
                if (!topItem.Text.StartsWith("视图", StringComparison.Ordinal)) continue;

                foreach (ToolStripItem item in topItem.DropDownItems)
                {
                    if (item is ToolStripMenuItem menuItem && StripMnemonic(menuItem.Text) == text)
                        return menuItem;
                }
            }

            return null;
        }

        /// <summary>在一层菜单项里按文字找（比较前先去掉助记符）。</summary>
        private static ToolStripMenuItem FindMenuItem(ToolStripItemCollection items, string text)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem menuItem && StripMnemonic(menuItem.Text) == text)
                    return menuItem;
            }

            return null;
        }

        /// <summary>去掉菜单文字里的助记符与快捷键提示：<c>显示播放列表(&amp;L)</c> → <c>显示播放列表</c>。</summary>
        private static string StripMnemonic(string text)
        {
            var plain = text.Replace("&", string.Empty);
            var index = plain.LastIndexOf('(');

            return index > 0 ? plain[..index] : plain;
        }

        /// <summary>两张图的平均像素差（0~255），用来判断画面到底换没换。</summary>
        private static double MeanPixelDifference(string pathA, string pathB)        {
            using var a = new Bitmap(pathA);
            using var b = new Bitmap(pathB);

            if (a.Width != b.Width || a.Height != b.Height) return 255;

            double sum = 0;
            var count = 0;

            for (var y = 0; y < a.Height; y += 4)
            {
                for (var x = 0; x < a.Width; x += 4)
                {
                    var ca = a.GetPixel(x, y);
                    var cb = b.GetPixel(x, y);

                    sum += (Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B)) / 3.0;
                    count++;
                }
            }

            return count == 0 ? 0 : sum / count;
        }
    }
}
