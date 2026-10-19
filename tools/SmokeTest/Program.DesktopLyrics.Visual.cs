// 桌面歌词 —— 画面（悬停背景、真的在屏幕上、位图与 alpha）。
//
// 从 Program.DesktopLyrics.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
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
