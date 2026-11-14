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
            // 1.21.0：先把"环境不成立"与"功能坏了"的分界本身断言一遍。
            if (!CheckFullscreenBarDecision()) return false;

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

                // 1.21.0 三态：1.20.0 的 release-check 里这一条红过一次
                // （"鼠标再动一下之后它没有回来"），当时旁边有别的程序在抢前台；
                // 现在最多试 3 次、每次先把主窗口抢回前台，仍然不回来才按前置条件判。
                var visible = bar.Visible;

                for (var attempt = 0; attempt < 3 && !visible; attempt++)
                {
                    if (GetForegroundWindow() != form.Handle)
                    {
                        form.Activate();
                        PumpMessages(150);
                    }

                    form.NotifyFullscreenPointer(center);
                    PumpMessages(250);

                    visible = bar.Visible;
                }

                var verdict = DecideFullscreenBarReturn(
                    visible,
                    form.IsFullscreen,
                    form.Engine.HasMedia,
                    form.Engine.IsPlaying,
                    GetForegroundWindow() == form.Handle,
                    out var reason);

                if (verdict == FullscreenBarVerdict.Fail)
                {
                    Log(13, "全屏进度条：鼠标再动一下之后它没有回来"
                            + $"（全屏={form.IsFullscreen}、有媒体={form.Engine.HasMedia}、"
                            + $"在播={form.Engine.IsPlaying}、前台 0x{GetForegroundWindow().ToInt64():X}、"
                            + $"窗口 0x{form.Handle.ToInt64():X}；{reason}）");
                    return false;
                }

                if (verdict == FullscreenBarVerdict.SkipEnvironment)
                {
                    Log(13, $"全屏进度条：「再动又出现」这一条这次不计——{reason}"
                            + "（前置条件成立时它必须回来）");
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

        /// <summary>
        /// 全屏进度条"收起来之后再动又出现"的三态判定（1.21.0）。
        /// <para>
        /// 1.20.0 的 release-check 里这一条红过一次：旁边有别的程序在抢前台，
        /// 主窗口已经不是前台了，进度条按设计就不该再冒出来——那是<b>环境</b>。
        /// 只有"还在全屏、媒体还在播、前台也是本程序，条却不回来"才是 <b>bug</b>。
        /// </para>
        /// </summary>
        private enum FullscreenBarVerdict
        {
            /// <summary>进度条回来了。</summary>
            Ok,

            /// <summary>前置条件不成立（不在全屏 / 媒体不在了 / 前台不是本程序）：跳过并出声。</summary>
            SkipEnvironment,

            /// <summary>前置条件成立，条却没回来：真失败。</summary>
            Fail
        }

        /// <summary>纯判定：不依赖真实前台，冒烟直接调它把三种结论都走一遍。</summary>
        private static FullscreenBarVerdict DecideFullscreenBarReturn(
            bool visible,
            bool isFullscreen,
            bool hasMedia,
            bool isPlaying,
            bool foregroundOwned,
            out string reason)
        {
            reason = string.Empty;

            if (visible) return FullscreenBarVerdict.Ok;

            if (!isFullscreen)
            {
                reason = "已经不是全屏了";
                return FullscreenBarVerdict.SkipEnvironment;
            }

            if (!hasMedia)
            {
                reason = "媒体已经不在了";
                return FullscreenBarVerdict.SkipEnvironment;
            }

            if (!isPlaying)
            {
                reason = "媒体不在播了";
                return FullscreenBarVerdict.SkipEnvironment;
            }

            if (!foregroundOwned)
            {
                reason = "前台不是本程序";
                return FullscreenBarVerdict.SkipEnvironment;
            }

            reason = "全屏、在播、前台也是本程序，条却没回来";
            return FullscreenBarVerdict.Fail;
        }

        /// <summary>三态判定本身的自检（不依赖真的前台窗口，任何机器上都跑得出来）。</summary>
        private static bool CheckFullscreenBarDecision()
        {
            if (DecideFullscreenBarReturn(true, false, false, false, false, out _) != FullscreenBarVerdict.Ok)
            {
                Log(13, "全屏进度条：三态判定不对（条已经回来了还算失败）");
                return false;
            }

            if (DecideFullscreenBarReturn(false, false, true, true, true, out var notFullscreen) !=
                    FullscreenBarVerdict.SkipEnvironment || notFullscreen.Length == 0)
            {
                Log(13, "全屏进度条：三态判定不对（不在全屏时没有跳过，也没有说原因）");
                return false;
            }

            if (DecideFullscreenBarReturn(false, true, false, true, true, out var noMedia) !=
                    FullscreenBarVerdict.SkipEnvironment || noMedia.Length == 0)
            {
                Log(13, "全屏进度条：三态判定不对（没有媒体时没有跳过，也没有说原因）");
                return false;
            }

            if (DecideFullscreenBarReturn(false, true, true, false, true, out var notPlaying) !=
                    FullscreenBarVerdict.SkipEnvironment || notPlaying.Length == 0)
            {
                Log(13, "全屏进度条：三态判定不对（媒体不在播时没有跳过，也没有说原因）");
                return false;
            }

            if (DecideFullscreenBarReturn(false, true, true, true, false, out var notForeground) !=
                    FullscreenBarVerdict.SkipEnvironment || notForeground.Length == 0)
            {
                Log(13, "全屏进度条：三态判定不对（窗口不是前台时会被判成功能坏了）");
                return false;
            }

            if (DecideFullscreenBarReturn(false, true, true, true, true, out _) != FullscreenBarVerdict.Fail)
            {
                Log(13, "全屏进度条：三态判定不对（前置条件成立时被当成了环境问题）");
                return false;
            }

            return true;
        }
    }
}
