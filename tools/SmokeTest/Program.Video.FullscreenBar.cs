// 检查点 13 的一部分：全屏进度条（1.11.0 F14；不依赖 播放器_TEST_VIDEO，用自造 WAV 就能跑）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 全屏时浮在画面底部的那条进度条。
        /// <para>
        /// 为什么必须验：它是<b>独立置顶窗口</b>（视频是 libvlc 的子窗口，WinForms 控件盖不上去），
        /// 所以"摆没摆到屏幕里、贴不贴底、会不会跟着时间走"全靠断言，人眼很难每次都看一眼。
        /// </para>
        /// </summary>
        private static bool CheckFullscreenProgressBar()
        {
            var wav = Path.Combine(AppContext.BaseDirectory, "fullscreen-bar.wav");
            WriteWav(wav, seconds: 60, frequency: 440);

            try
            {
                using var form = new 播放器.MainForm(new[] { wav });

                // 套件开头把 WindowMode 钉成 Video，但前面几步可能切过形态；这里再钉一次，
                // 否则 WAV 可能进音频窗口，而音频窗口按设计没有全屏。
                form.Settings.WindowMode = WindowModes.Video;
                form.Show();
                PumpMessages(600);

                if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.IsPlaying, 20000))
                {
                    Log(13, "全屏进度条：测试音频没播起来");
                    return false;
                }

                form.EnterFullscreen();
                PumpMessages(400);

                if (!form.IsFullscreen)
                {
                    Log(13, "全屏进度条：没有进入全屏");
                    return false;
                }

                var bar = form.FullscreenBar;

                if (bar == null || !bar.Visible)
                {
                    Log(13, "全屏进度条：进全屏之后画面底部没有出现进度条"
                            + $"（条={(bar == null ? "没建" : bar.Visible ? "显示着" : "藏着")}，"
                            + $"有媒体={form.Engine.HasMedia}，状态栏：「{form.StatusText}」）");
                    return false;
                }

                var screen = Screen.FromControl(form).Bounds;
                var bounds = bar.Bounds;

                if (bounds.Left < screen.Left || bounds.Right > screen.Right ||
                    bounds.Top < screen.Top || bounds.Bottom > screen.Bottom)
                {
                    Log(13, $"全屏进度条：那条窗口跑到屏幕外面去了（{bounds}，屏幕 {screen}）");
                    return false;
                }

                if (bounds.Bottom < screen.Bottom - 160 || bounds.Bottom > screen.Bottom - 4)
                {
                    Log(13, $"全屏进度条：没有贴在屏幕底部（底边 {bounds.Bottom}，屏幕底边 {screen.Bottom}）");
                    return false;
                }

                if (bounds.Width < screen.Width / 2)
                {
                    Log(13, $"全屏进度条：宽度只有 {bounds.Width}，屏幕宽 {screen.Width}，太窄了");
                    return false;
                }

                if (!bar.TopMost || bar.ShowInTaskbar)
                {
                    Log(13, "全屏进度条：它必须是置顶且不出现在任务栏上的（否则会被画面盖住 / 多出一个任务栏按钮）");
                    return false;
                }

                // ---- 跟着播放进度走 ----
                var valueBefore = bar.Slider.Value;
                PumpMessages(900);

                var length = form.Engine.Length;
                if (length <= 0)
                {
                    Log(13, "全屏进度条：媒体总时长读不出来");
                    return false;
                }

                var time = form.Engine.Time;
                var expected = (int)Math.Clamp(
                    time * FullscreenProgressBar.Resolution / length, 0, FullscreenProgressBar.Resolution);

                if (bar.Slider.Value <= valueBefore)
                {
                    Log(13, $"全屏进度条：滑块没有跟着时间前进（0.9 秒前是 {valueBefore}，现在还是 "
                            + $"{bar.Slider.Value}）");
                    return false;
                }

                if (Math.Abs(bar.Slider.Value - expected) > 30)
                {
                    Log(13, $"全屏进度条：滑块没有跟着播放进度走（滑块 {bar.Slider.Value}，"
                            + $"按 {time}/{length} 应该是 {expected}）");
                    return false;
                }

                // ---- 松手定位 ----
                var target = length / 2;
                bar.Slider.Value = 500;
                bar.CommitSeek();

                if (!PumpUntil(() => Math.Abs(form.Engine.Time - target) <= 2000, 10000))
                {
                    Log(13, $"全屏进度条：拖到中间松手后播放位置没有跳过去（现在 {form.Engine.Time}，"
                            + $"期望约 {target}）");
                    return false;
                }

                // ---- 空闲收起 / 再动又出现 ----
                bar.ForceIdleForTest();

                if (bar.Visible)
                {
                    Log(13, "全屏进度条：空闲之后没有自动收起");
                    return false;
                }

                var center = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
                form.NotifyFullscreenPointer(center);

                if (!bar.Visible)
                {
                    Log(13, "全屏进度条：鼠标再动一下之后它没有回来");
                    return false;
                }

                if (GetForegroundWindow() == bar.Handle)
                {
                    Log(13, "全屏进度条：它自己变成了前台窗口（会把视频的键盘抢走）");
                    return false;
                }

                // ---- 退出全屏要整个放掉 ----
                form.ExitFullscreen();
                PumpMessages(300);

                if (form.IsFullscreen)
                {
                    Log(13, "全屏进度条：退出全屏失败");
                    return false;
                }

                if (form.FullscreenBar != null)
                {
                    Log(13, "全屏进度条：退出全屏之后那条窗口还留着");
                    return false;
                }

                Log(13, "全屏进度条正常：进全屏就出现、贴底居中且不超屏、滑块跟着时间走、"
                        + "拖到中间松手会跳过去、空闲会收起再动又回来、不抢前台、退出全屏就放掉");
                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }
    }
}
