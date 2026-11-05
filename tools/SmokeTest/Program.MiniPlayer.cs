// 检查点 23：音频迷你模式（1.12.0 F15）。
//
// 迷你模式是第 3 种窗口形态：一张 440×160 左右的小卡片，没有菜单 / 工具栏 / 状态栏 /
// 传输条，窗口置顶；Ctrl+Alt+N、菜单、卡片上的「还原」与双击都能进出。
// 这里验的是"形态与几何"，不验"看着顺不顺眼"（那要人眼看）。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool CheckMiniPlayer()
        {
            var defaults = AppSettings.Load();
            var first = Path.Combine(AppContext.BaseDirectory, "mini-first.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "mini-second.wav");

            try
            {
                WriteWav(first, seconds: 8, frequency: 440);
                WriteWav(second, seconds: 8, frequency: 660);

                var setup = AppSettings.Load();
                setup.WindowMode = WindowModes.Video;
                setup.MiniMode = false;
                setup.Save();

                var form = new 播放器.MainForm(Array.Empty<string>());
                form.Show();
                PumpMessages(250);

                form.AddPathsForTest(new[] { first });

                if (!SendShortcut(form, Keys.Space) ||
                    !PumpUntil(() => form.Engine.HasMedia && form.Engine.IsPlaying, 15000))
                {
                    Log(23, "迷你模式：没能先播起来（检查前置条件没准备好）");
                    return false;
                }

                // 进迷你之前的窗口几何：还原时要回到这里
                var videoBounds = form.Bounds;

                if (!CheckEnterMini(form)) return false;
                if (!CheckMiniCard(form)) return false;
                if (!CheckMiniBlockedShortcuts(form)) return false;
                if (!CheckMiniProgress(form)) return false;
                if (!CheckMiniRestore(form, videoBounds)) return false;
                if (!CheckMiniFromAudio(form)) return false;
                if (!CheckMiniStaysMini(form, second)) return false;
                if (!CheckMiniPersistence(form)) return false;

                Log(23, "迷你模式正常：进入后置顶 / 只留卡片 / 收起菜单工具栏状态栏传输条，"
                        + "全屏与播放列表被挡且说明原因，卡片按钮与进度都作用到引擎，还原回进入前的形态与几何，"
                        + "从音频窗口进来也能回音频窗口，自动形态不推翻它，退出后重启还是迷你");
                return true;
            }
            catch (Exception ex)
            {
                Log(23, "迷你模式检查失败: " + ex);
                return false;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                defaults.Save();
            }
        }

        private static bool CheckEnterMini(播放器.MainForm form)
        {
            if (!SendShortcut(form, Keys.Control | Keys.Alt | Keys.N))
            {
                Log(23, "迷你模式：Ctrl+Alt+N 没有被接住（ProcessCmdKey 返回 false）");
                return false;
            }

            if (!PumpUntil(() => form.IsMiniWindow, 8000))
            {
                Log(23, "迷你模式：按了 Ctrl+Alt+N 却没有进迷你模式");
                return false;
            }

            if (!form.TopMost)
            {
                Log(23, "迷你模式：进了迷你模式但窗口没有置顶");
                return false;
            }

            if (form.MaximizeBox)
            {
                Log(23, "迷你模式：迷你窗口还留着最大化按钮（一张小卡片没有最大化可言）");
                return false;
            }

            if (form.WindowState != FormWindowState.Normal)
            {
                Log(23, $"迷你模式：迷你窗口不是普通状态（现在是 {form.WindowState}）");
                return false;
            }

            var menu = Find(form, "menuStripMain");
            var toolbar = Find(form, "toolStripMain");
            var status = Find(form, "statusStripMain");
            var transport = Find(form, "pnlTransport");

            if (menu == null || toolbar == null || status == null || transport == null)
            {
                Log(23, "迷你模式：找不到菜单 / 工具栏 / 状态栏 / 传输条，没法验它们收没收起来");
                return false;
            }

            if (menu.Visible || toolbar.Visible || status.Visible || transport.Visible)
            {
                Log(23, "迷你模式：菜单 / 工具栏 / 状态栏 / 传输条还有露着的"
                        + $"（菜单 {menu.Visible}、工具栏 {toolbar.Visible}、"
                        + $"状态栏 {status.Visible}、传输条 {transport.Visible}）");
                return false;
            }

            var splitMain = Find(form, "splitMain") as SplitContainer;
            var splitVideo = Find(form, "splitVideo") as SplitContainer;

            if (splitMain == null || splitVideo == null)
            {
                Log(23, "迷你模式：找不到两条分隔条");
                return false;
            }

            if (!splitMain.Panel2Collapsed || !splitVideo.Panel2Collapsed)
            {
                Log(23, "迷你模式：播放列表或侧栏那一栏还开着（迷你模式只该留卡片）");
                return false;
            }

            if (form.Size.Width < form.MinimumSize.Width || form.Size.Height < form.MinimumSize.Height)
            {
                Log(23, $"迷你模式：迷你窗口比最小值还小（{form.Size}，最小 {form.MinimumSize}）");
                return false;
            }

            // 菜单里那一项应该带着勾，用户才知道自己在什么形态里。
            var miniMenu = form.CommandBindings
                .FirstOrDefault(binding => binding.Id == PlayerCommandId.ToggleMiniMode)
                ?.Item as ToolStripMenuItem;

            if (miniMenu == null || !miniMenu.Checked)
            {
                Log(23, "迷你模式：视图菜单里的「迷你模式」没有勾上");
                return false;
            }

            return true;
        }

        private static bool CheckMiniCard(播放器.MainForm form)
        {
            var card = form.MiniPlayer;

            if (card == null || !card.Visible)
            {
                Log(23, "迷你模式：小卡片没建出来或没显示");
                return false;
            }

            var splitVideo = Find(form, "splitVideo") as SplitContainer;
            if (splitVideo == null)
            {
                Log(23, "迷你模式：找不到 splitVideo");
                return false;
            }

            var expected = new Rectangle(Point.Empty, splitVideo.Panel1.ClientSize);

            if (Math.Abs(card.Width - expected.Width) > 2 || Math.Abs(card.Height - expected.Height) > 2 ||
                card.Left != 0 || card.Top != 0)
            {
                Log(23, $"迷你模式：小卡片没有盖满窗口客户区（卡片 {card.Bounds}，期望 {expected}）");
                return false;
            }

            if (string.IsNullOrWhiteSpace(card.TitleText))
            {
                Log(23, "迷你模式：卡片上的曲名是空的");
                return false;
            }

            if (card.CoverBounds.Width <= 0 || card.CoverBounds.Height <= 0 ||
                !card.ClientRectangle.Contains(card.CoverBounds))
            {
                Log(23, $"迷你模式：封面没有画在卡片里（封面 {card.CoverBounds}，卡片 {card.ClientRectangle}）");
                return false;
            }

            // 卡片上的播放 / 暂停必须也走命令层（不是直接调引擎）：暂停 → 继续。
            card.Transport.PlayPauseButton.PerformClick();

            if (!PumpUntil(() => !form.Engine.IsPlaying, 8000))
            {
                Log(23, "迷你模式：点卡片上的播放 / 暂停没有暂停");
                return false;
            }

            card.Transport.PlayPauseButton.PerformClick();

            if (!PumpUntil(() => form.Engine.IsPlaying, 8000))
            {
                Log(23, "迷你模式：再点一次卡片上的播放 / 暂停没有继续");
                return false;
            }

            return true;
        }

        private static bool CheckMiniBlockedShortcuts(播放器.MainForm form)
        {
            if (!SendShortcut(form, Keys.F11) || form.IsFullscreen)
            {
                Log(23, "迷你模式：按 F11 还是进了全屏");
                return false;
            }

            if (!form.StatusText.Contains("迷你模式没有全屏", StringComparison.Ordinal))
            {
                Log(23, "迷你模式：F11 被挡住了，但状态栏没说原因（用户会以为坏了）");
                return false;
            }

            if (!SendShortcut(form, Keys.F9))
            {
                Log(23, "迷你模式：F9 没有被接住");
                return false;
            }

            if (!form.StatusText.Contains("迷你模式没有播放列表", StringComparison.Ordinal))
            {
                Log(23, "迷你模式：F9 被挡住了，但状态栏没说原因");
                return false;
            }

            return true;
        }

        private static bool CheckMiniProgress(播放器.MainForm form)
        {
            var card = form.MiniPlayer;
            if (card == null) return false;

            // 让播放推进一下，卡片上的进度与当前时间要跟着走。
            if (!PumpUntil(() => form.Engine.Time > 1200, 10000))
            {
                Log(23, "迷你模式：播放时间没有推进（进度条验不了）");
                return false;
            }

            // 界面计时器 200 ms 一拍：等一拍再读卡片上的数字，否则看到的是上一拍的值。
            PumpMessages(400);

            if (card.Progress.Value <= 0 || card.PositionText == "00:00:00")
            {
                Log(23, $"迷你模式：卡片上的进度没有跟着时间走（滑块 {card.Progress.Value}，时间「{card.PositionText}」）");
                return false;
            }

            // 拖到中间松手 → 应该跳到总时长一半附近。
            card.Progress.Value = 500;
            card.CommitSeekForTest();

            var length = form.Engine.Length;
            var expected = length / 2;

            if (!PumpUntil(() => Math.Abs(form.Engine.Time - expected) <= 2000, 10000))
            {
                Log(23, $"迷你模式：卡片上拖到中间松手没有跳过去（现在 {form.Engine.Time}，期望约 {expected}）");
                return false;
            }

            return true;
        }

        private static bool CheckMiniRestore(播放器.MainForm form, Rectangle videoBounds)
        {
            var restore = Find(form, "miniRestore") as Button;

            if (restore == null)
            {
                Log(23, "迷你模式：卡片上找不到「还原」按钮");
                return false;
            }

            restore.PerformClick();

            if (!PumpUntil(() => !form.IsMiniWindow && !form.IsAudioWindow, 8000))
            {
                Log(23, "迷你模式：点「还原」之后没有回到视频窗口");
                return false;
            }

            if (form.TopMost)
            {
                Log(23, "迷你模式：还原之后窗口还是置顶的（进入迷你前的置顶状态没还原）");
                return false;
            }

            var toolbar = Find(form, "toolStripMain");
            var menu = Find(form, "menuStripMain");
            var transport = Find(form, "pnlTransport");

            if (toolbar == null || menu == null || transport == null ||
                !toolbar.Visible || !menu.Visible || !transport.Visible)
            {
                Log(23, "迷你模式：还原之后菜单 / 工具栏 / 传输条没有回来");
                return false;
            }

            if (Math.Abs(form.Width - videoBounds.Width) > 2 || Math.Abs(form.Height - videoBounds.Height) > 2)
            {
                Log(23, $"迷你模式：还原之后窗口尺寸没回到进入前（现在 {form.Size}，原来 {videoBounds.Size}）");
                return false;
            }

            return true;
        }

        private static bool CheckMiniFromAudio(播放器.MainForm form)
        {
            form.SetWindowMode(WindowModes.Audio);

            if (!PumpUntil(() => form.IsAudioWindow && form.NowPlaying != null, 8000))
            {
                Log(23, "迷你模式：没有切进音频窗口（从音频进迷你这一段验不到）");
                return false;
            }

            if (!SendShortcut(form, Keys.Control | Keys.Alt | Keys.N) ||
                !PumpUntil(() => form.IsMiniWindow, 8000))
            {
                Log(23, "迷你模式：从音频窗口按 Ctrl+Alt+N 没有进迷你模式");
                return false;
            }

            var restore = Find(form, "miniRestore") as Button;
            if (restore == null)
            {
                Log(23, "迷你模式：从音频窗口进来后找不到「还原」按钮");
                return false;
            }

            restore.PerformClick();

            // 还原是同步完成的：直接看"现在是不是音频窗口"，不能给自动形态纠正留时间——
            // 那会把"还原回错了地方"掩盖成"后来自己又切回去了"。
            if (!PumpUntil(() => !form.IsMiniWindow, 8000))
            {
                Log(23, "迷你模式：从音频窗口进迷你之后，点「还原」还停在迷你");
                return false;
            }

            if (!form.IsAudioWindow)
            {
                Log(23, "迷你模式：从音频窗口进迷你之后，还原没有回到音频窗口"
                        + $"（现在 {form.Surfaces.Surface}）");
                return false;
            }

            if (form.NowPlaying == null || !form.NowPlaying.Visible)
            {
                Log(23, "迷你模式：回到音频窗口之后卡片没回来");
                return false;
            }

            form.SetWindowMode(WindowModes.Video);

            if (!PumpUntil(() => !form.IsAudioWindow && !form.IsMiniWindow, 8000))
            {
                Log(23, "迷你模式：切回视频窗口失败");
                return false;
            }

            return true;
        }

        /// <summary>迷你模式是用户选的形态：换一首（自动模式本该切到音频窗口）也不该把它推翻。</summary>
        private static bool CheckMiniStaysMini(播放器.MainForm form, string otherFile)
        {
            if (!SendShortcut(form, Keys.Control | Keys.Alt | Keys.N) ||
                !PumpUntil(() => form.IsMiniWindow, 8000))
            {
                Log(23, "迷你模式：第二次进迷你失败");
                return false;
            }

            form.Engine.Open(otherFile);
            form.Engine.Play();

            if (!PumpUntil(() => form.Engine.GetTracks().Count > 0, 20000))
            {
                Log(23, "迷你模式：换的那一首没解析出轨道（这一段跳过判据）");
                return true;
            }

            PumpMessages(800);

            if (!form.IsMiniWindow)
            {
                Log(23, "迷你模式：换了一首歌（自动形态本该是音频窗口）之后迷你模式被推翻了");
                return false;
            }

            return true;
        }

        /// <summary>关窗时记下"下次还是迷你"与迷你几何；新开一个窗体要直接进迷你。</summary>
        private static bool CheckMiniPersistence(播放器.MainForm form)
        {
            var beforeAudioWidth = form.Settings.AudioWindowWidth;
            var beforeAudioHeight = form.Settings.AudioWindowHeight;
            var miniBounds = form.Bounds;

            form.Close();
            PumpMessages(300);

            var saved = AppSettings.Load();

            if (!saved.MiniMode)
            {
                Log(23, "迷你模式：关窗时没有记住「下次还是迷你模式」");
                return false;
            }

            if (saved.MiniWindowWidth != miniBounds.Width ||
                saved.MiniWindowHeight != miniBounds.Height ||
                saved.MiniWindowX != miniBounds.X || saved.MiniWindowY != miniBounds.Y)
            {
                Log(23, "迷你模式：关窗时迷你窗口的位置 / 大小没写进设置"
                        + $"（界面 {miniBounds}；设置 {saved.MiniWindowX},{saved.MiniWindowY} "
                        + $"{saved.MiniWindowWidth}×{saved.MiniWindowHeight}）");
                return false;
            }

            if (saved.AudioWindowWidth != beforeAudioWidth || saved.AudioWindowHeight != beforeAudioHeight)
            {
                Log(23, "迷你模式：保存几何时把迷你尺寸写进了音频窗口"
                        + $"（音频窗口 {beforeAudioWidth}×{beforeAudioHeight} → "
                        + $"{saved.AudioWindowWidth}×{saved.AudioWindowHeight}）");
                return false;
            }

            using var second = new 播放器.MainForm(Array.Empty<string>());
            second.Show();
            PumpMessages(500);

            if (!second.IsMiniWindow)
            {
                Log(23, "迷你模式：上次退出时是迷你模式，重新启动却没有进迷你");
                return false;
            }

            if (Math.Abs(second.Width - miniBounds.Width) > 2 ||
                Math.Abs(second.Height - miniBounds.Height) > 2)
            {
                Log(23, $"迷你模式：重新启动没有用记住的迷你尺寸（现在 {second.Size}，期望 {miniBounds.Size}）");
                return false;
            }

            return true;
        }
    }
}
