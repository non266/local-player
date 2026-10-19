// 桌面歌词 —— 内容变化时的锚点（没拖过保左上角，拖过之后以中心为锚）。
//
// 从 Program.DesktopLyrics.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
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
    }
}
