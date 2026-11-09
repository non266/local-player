// 第 17 步（检查点 27）：DVD 菜单的规则与回退 —— 纯逻辑 + 假盘符，不需要真盘。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 1.16.0 的「光盘菜单」。这一段<b>不需要真盘</b>：按键映射、菜单判定、MRL、回退判据、
        /// 就地换地址都是纯逻辑；最后用不存在的盘符 <c>Z:</c> 真走一遍"菜单打不开 → 退主标题"。
        /// 真盘上的菜单有没有真的动，在 <see cref="CheckOpticalDiscPlayback"/> 那一段验
        /// （需要 <c>播放器_TEST_DISC</c>）。
        /// </summary>
        private static bool CheckDiscMenuRules()
        {
            // ---- 按键 → 菜单动作 ----
            var keys = new (Keys Key, DvdMenuAction Expected, string Text)[]
            {
                (Keys.Up, DvdMenuAction.Up, "↑"),
                (Keys.Down, DvdMenuAction.Down, "↓"),
                (Keys.Left, DvdMenuAction.Left, "←"),
                (Keys.Right, DvdMenuAction.Right, "→"),
                (Keys.Enter, DvdMenuAction.Activate, "回车")
            };

            foreach (var item in keys)
            {
                var actual = DvdMenuInput.For(item.Key);

                if (actual != item.Expected)
                {
                    Log(27, $"光盘菜单：{item.Text} 没有映射成「{item.Expected}」（映射成了 {actual?.ToString() ?? "无"}）");
                    return false;
                }
            }

            if (DvdMenuInput.For(Keys.A) != null || DvdMenuInput.For(Keys.Space) != null)
            {
                Log(27, "光盘菜单：普通字符键 / 空格被菜单吃了（空格是播放暂停，不该拿来选菜单）");
                return false;
            }

            if (DvdMenuInput.For(Keys.Escape) != null)
            {
                Log(27, "光盘菜单：Esc 被菜单吃了——全屏时它是「退出全屏」，不该改成回菜单根");
                return false;
            }

            // ---- 是不是菜单（必须连 MRL 一起看）----
            if (!DvdMenu.IsOnScreen("dvd:///L:", 0, 13) ||
                DvdMenu.IsOnScreen("dvd:///L:", 2, 13) ||
                DvdMenu.IsOnScreen("dvd:///L:", 0, 1) ||
                DvdMenu.IsOnScreen("dvdsimple:///L:", 0, 12) ||
                DvdMenu.IsOnScreen(@"C:\music\a.mp3", 0, 3))
            {
                Log(27, "光盘菜单：「这是不是菜单」判错了（dvd:// 的 title 0 才是菜单，"
                        + "dvdsimple:// 的 title 0 是正片）");
                return false;
            }

            // ---- 状态栏文案 ----
            if (DvdMenu.DescribePosition("dvd:///L:", 0, 13, 1) != "菜单")
            {
                Log(27, "光盘菜单：停在菜单上时位置文案不是「菜单」（得到「"
                        + DvdMenu.DescribePosition("dvd:///L:", 0, 13, 1) + "」）");
                return false;
            }

            if (DvdMenu.DescribePosition("dvd:///L:", 2, 13, 0) != "标题 2 / 12 · 第 1 章")
            {
                Log(27, "光盘菜单：正片的位置文案不对（得到「"
                        + DvdMenu.DescribePosition("dvd:///L:", 2, 13, 0) + "」，期望「标题 2 / 12 · 第 1 章」）");
                return false;
            }

            if (DvdMenu.DescribePosition("dvdsimple:///L:", 0, 12, 0).Length != 0)
            {
                Log(27, "光盘菜单：dvdsimple:// 不是菜单，却给出了菜单文案");
                return false;
            }

            // ---- MRL ----
            if (OpticalDisc.BuildMrl(DiscKind.DvdVideo, @"L:\") != "dvd:///L:")
            {
                Log(27, "光盘菜单：DVD 打开时没有走菜单（MRL 是「"
                        + OpticalDisc.BuildMrl(DiscKind.DvdVideo, @"L:\") + "」，期望 dvd:///L:）");
                return false;
            }

            if (OpticalDisc.BuildSimpleMrl(DiscKind.DvdVideo, @"L:\") != "dvdsimple:///L:")
            {
                Log(27, "光盘菜单：「直接播放主标题」的 MRL 不对（得到「"
                        + OpticalDisc.BuildSimpleMrl(DiscKind.DvdVideo, @"L:\") + "」）");
                return false;
            }

            if (OpticalDisc.LetterOfMrl("dvd:///L:") != "L:" ||
                OpticalDisc.LetterOfMrl("dvdsimple:///L:") != "L:" ||
                OpticalDisc.LetterOfMrl(@"C:\music\a.mp3").Length != 0)
            {
                Log(27, "光盘菜单：从 MRL 认盘符不对（dvd:///L: 应该认成 L:，普通路径应该认不出）");
                return false;
            }

            // ---- 回退判据 ----
            if (!DvdMenu.ShouldFallBack("dvd:///L:", alreadyTried: false, error: true))
            {
                Log(27, "光盘菜单：菜单打不开（错误事件到了）却没有退回主标题");
                return false;
            }

            if (DvdMenu.ShouldFallBack("dvd:///L:", alreadyTried: true, error: true))
            {
                Log(27, "光盘菜单：同一个盘会退两次（第二次还是 dvd:// 的状态）");
                return false;
            }

            if (DvdMenu.ShouldFallBack("dvdsimple:///L:", alreadyTried: false, error: true) ||
                DvdMenu.ShouldFallBack("dvd:///L:", alreadyTried: false, error: false))
            {
                Log(27, "光盘菜单：回退判据太宽（主标题出错了也退 / 没出错也退）");
                return false;
            }

            // ---- 就地换地址：列表不能多出一行 ----
            var playlist = new Playlist();
            playlist.Add(@"C:\smoke\a.wav");
            playlist.Add(@"C:\smoke\b.wav");
            playlist.Add(@"C:\smoke\c.wav");
            playlist.SetCurrent(1);

            var changed = 0;
            var contentChanged = 0;
            playlist.Changed += (s, e) => changed++;
            playlist.ContentChanged += (s, e) => contentChanged++;

            var order = playlist.Items[1].AddedOrder;
            playlist.SetPath(1, "dvdsimple:///L:");

            if (playlist.Count != 3 ||
                playlist.Items[0].FilePath != @"C:\smoke\a.wav" ||
                playlist.Items[2].FilePath != @"C:\smoke\c.wav")
            {
                Log(27, "光盘菜单：换地址把别的条目挤掉了（现在 " + playlist.Count + " 条）");
                return false;
            }

            if (playlist.Items[1].FilePath != "dvdsimple:///L:" || playlist.IndexOfPath("dvdsimple:///L:") != 1)
            {
                Log(27, "光盘菜单：换地址之后条目路径没变（还是「" + playlist.Items[1].FilePath + "」）");
                return false;
            }

            if (playlist.IndexOfPath(@"C:\smoke\b.wav") != -1)
            {
                Log(27, "光盘菜单：旧地址还留在索引里（按旧路径还能找到原来那一项）");
                return false;
            }

            if (playlist.Items[1].AddedOrder != order)
            {
                Log(27, "光盘菜单：换地址把「加入顺序」改了（排序会跟着乱）");
                return false;
            }

            if (playlist.CurrentIndex != 1 || changed != 1 || contentChanged != 1)
            {
                Log(27, $"光盘菜单：换地址的通知不对（当前项 {playlist.CurrentIndex}、Changed {changed}、ContentChanged {contentChanged}）");
                return false;
            }

            playlist.SetPath(1, "dvdsimple:///L:");

            if (changed != 1)
            {
                Log(27, "光盘菜单：地址没变也发了一次通知（白刷一遍列表）");
                return false;
            }

            Log(27, "光盘菜单规则正常：方向键 / 回车映射对，dvd:// 的 title 0 才认成菜单，"
                    + "MRL 是 dvd:// 与 dvdsimple://，回退只退一次，换地址不新增条目");
            return true;
        }

        /// <summary>
        /// 用不存在的盘符 <c>Z:</c> 走一遍"菜单打不开 → 自动退回主标题"。
        /// <para>
        /// 探测出来的事实：<c>dvd:///Z:</c> 的错误事件 1 秒内就到，所以这里 15 秒足够；
        /// 退完之后再等一会儿，确认它<b>没有再退第二次</b>（也不会在列表里多出一行）。
        /// </para>
        /// </summary>
        private static bool CheckDiscMenuFallback()
        {
            if (DriveInfo.GetDrives().Any(d => string.Equals(d.Name, "Z:\\", StringComparison.OrdinalIgnoreCase)))
            {
                Log(27, "光盘菜单回退检查：这台机器上有 Z: 这个盘，跳过假盘符那一段");
                return true;
            }

            using var form = new 播放器.MainForm(Array.Empty<string>());
            form.Show();
            PumpMessages(300);

            form.PlayLocation("dvd:///Z:", "假 DVD");

            if (!PumpUntil(() => (form.Engine.CurrentPath ?? string.Empty)
                    .StartsWith("dvdsimple://", StringComparison.OrdinalIgnoreCase), 15000))
            {
                Log(27, "光盘菜单回退：菜单打不开之后没有退回主标题（现在「"
                        + (form.Engine.CurrentPath ?? "（空）") + "」，状态栏「" + form.StatusText + "」）");
                return false;
            }

            if (!form.HasFallenBackFromDiscMenu("Z:"))
            {
                Log(27, "光盘菜单回退：退是退了，但没记下「这个盘退过」（下次错误还会再退一遍）");
                return false;
            }

            if (form.Playlist.Count != 1 || form.Playlist.Items[0].FilePath != "dvdsimple:///Z:")
            {
                Log(27, "光盘菜单回退：列表里多了 / 少了条目（现在 " + form.Playlist.Count + " 条，第一条是「"
                        + (form.Playlist.Count > 0 ? form.Playlist.Items[0].FilePath : "（空）") + "」）");
                return false;
            }

            if (!form.DiscFallbackNotice.Contains("菜单打不开", StringComparison.Ordinal))
            {
                Log(27, "光盘菜单回退：退回主标题之后没有留下说明（现在「" + form.DiscFallbackNotice + "」）");
                return false;
            }

            // 再等 2 秒：主标题这一路也会失败（Z: 根本没有盘），它不该再触发第二次回退。
            PumpMessages(2000);

            if (form.Playlist.Count != 1)
            {
                Log(27, "光盘菜单回退：同一个盘回退了两遍（列表里现在 " + form.Playlist.Count + " 条）");
                return false;
            }

            Log(27, "光盘菜单回退正常：dvd:///Z: 打不开 → 自动换成 dvdsimple:///Z:（列表仍只有 1 条），只退一次");
            return true;
        }
    }
}
