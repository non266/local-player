// 检查点 13：视频功能与逐帧。需要 播放器_TEST_VIDEO 指定真实视频，否则跳过。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 13) 视频功能（需要真实视频，通过环境变量指定，否则跳过）
        //
        // 画面调整、去隔行、延迟、逐帧这些都必须落到真正的视频输出上才有意义——
        // 用 WAV 或者假数据是验不出来的，所以这一步要求外部给一个视频文件。
        // -----------------------------------------------------------------

        private static bool TestVideoFeatures()
        {
            var video = Environment.GetEnvironmentVariable("播放器_TEST_VIDEO");

            if (string.IsNullOrWhiteSpace(video) || !File.Exists(video))
            {
                Log(13, "未指定 播放器_TEST_VIDEO（或文件不存在），跳过视频功能检查");
                return true;
            }

            try
            {
                using var form = new Form
                {
                    ClientSize = new Size(720, 440),
                    ShowInTaskbar = false,
                    Text = "冒烟测试 - 视频功能"
                };

                using var view = new LibVLCSharp.WinForms.VideoView { Dock = DockStyle.Fill };
                form.Controls.Add(view);
                form.Show();

                using var engine = new PlayerEngine(form);
                view.MediaPlayer = engine.Player;

                if (!PlayVideo(engine, video, out var error))
                {
                    Log(13, "播放视频失败：" + error);
                    return false;
                }

                var fps = engine.FrameRate;
                if (fps <= 1)
                {
                    Log(13, $"没有读到视频帧率（{fps}），逐帧功能没法工作");
                    return false;
                }

                // ---- 音画 / 字幕延迟（界面上是毫秒，libvlc 收的是微秒）----
                if (!engine.SetAudioDelayMilliseconds(150) ||
                    Math.Abs(engine.AudioDelayMilliseconds - 150) > 30)
                {
                    Log(13, $"音频延迟往返不正确：设 150，读回 {engine.AudioDelayMilliseconds}");
                    return false;
                }

                if (!engine.SetSubtitleDelayMilliseconds(-250) ||
                    Math.Abs(engine.SubtitleDelayMilliseconds + 250) > 30)
                {
                    Log(13, $"字幕延迟往返不正确：设 −250，读回 {engine.SubtitleDelayMilliseconds}");
                    return false;
                }

                // ---- 去隔行：每个模式都要能设进去且不抛 ----
                foreach (var (label, mode) in DeinterlaceModes.All)
                {
                    if (!engine.SetDeinterlace(mode))
                    {
                        Log(13, $"设置去隔行「{label}」失败");
                        return false;
                    }

                    PumpMessages(120);
                }

                // ---- 对照实验：先确认截图抓到的是实时画面 ----
                // 如果播放中连拍两张都一模一样，那说明 TakeSnapshot 给的是缓存帧，
                // 后面用像素判断"有没有换帧"就完全不成立，必须先排除这种情况。
                var liveA = Path.Combine(AppContext.BaseDirectory, "shot-live-a.png");
                var liveB = Path.Combine(AppContext.BaseDirectory, "shot-live-b.png");

                engine.TakeSnapshot(liveA);
                PumpMessages(700);
                engine.TakeSnapshot(liveB);

                var liveDiff = MeanPixelDifference(liveA, liveB);

                // 阈值放到 0.05：静态画面下相邻帧的差异本来就很微小（实测有个视频 700ms 才 0.9），
                // 而"完全没换帧"时像素差是精确的 0.00，两者区分得很清楚。
                if (liveDiff < 0.05)
                {
                    Log(13, $"对照失败：播放中两次快照的像素差只有 {liveDiff:0.00}，"
                            + "说明截图不是实时画面，无法据此判断逐帧是否生效");
                    return false;
                }

                Log(13, $"对照：播放中两次快照的像素差 {liveDiff:0.00}（截图是实时画面）");
                // ---- 逐帧：libvlc 的 NextFrame 只在暂停状态下有效 ----
                engine.Pause();

                // 暂停是异步生效的：没停稳就调 NextFrame，会被静默丢掉。
                var pauseDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < pauseDeadline && engine.IsPlaying)
                    PumpMessages(100);

                if (engine.IsPlaying)
                {
                    Log(13, "暂停一直没生效，无法测试逐帧");
                    return false;
                }

                PumpMessages(300);

                // ⚠ 这里有两个坑，都是实测出来的，不绕开就一定会误报：
                //
                // 1) **暂停时画面上显示的并不是"时钟所指的那一帧"。** 暂停后连拍两张（间隔 700ms）
                //    像素差是 0.00，说明画面本身是稳的；但再 seek 到时钟所指的同一时刻，
                //    画面却变了（本机实测差 11 左右）——vout 比时钟滞后约 100~200ms。
                //    所以"暂停时截的那一张"不能当作"上一帧应该回到的那一帧"。
                //
                // 2) **本测试视频是白板讲解，相邻帧常常几乎一样**（实测一帧只差 0.00~0.54），
                //    一次走一帧根本分辨不出"走没走"，用 0.05 当阈值等于在量重绘噪声。
                //
                // 因此这里的做法是：先用一次"前进 + 后退"把基准落到 seek 落点上，
                // 再一次走 5 帧——前进时画面必须有明显变化，后退时**必须像素级回到基准**
                // （seek 落点是可复现的，本机实测同一个时刻来回 seek 的像素差为 0.00）。
                var reference = Path.Combine(AppContext.BaseDirectory, "shot-frame-base.png");
                var steppedShot = Path.Combine(AppContext.BaseDirectory, "shot-frame-forward.png");
                var restoredShot = Path.Combine(AppContext.BaseDirectory, "shot-frame-back.png");

                // 基准：走一个来回，让 base 那一帧真正落到画面上
                StepFrames(engine, forward: true, count: 1);
                StepFrames(engine, forward: false, count: 1);

                if (!engine.TakeSnapshot(reference))
                {
                    Log(13, "暂停时截图失败");
                    return false;
                }

                StepFrames(engine, forward: true, count: FramesPerStepRun);
                engine.TakeSnapshot(steppedShot);

                StepFrames(engine, forward: false, count: FramesPerStepRun);
                engine.TakeSnapshot(restoredShot);

                var forwardDiff = MeanPixelDifference(reference, steppedShot);
                var backDiff = MeanPixelDifference(steppedShot, restoredShot);
                var restoreDiff = MeanPixelDifference(reference, restoredShot);

                if (forwardDiff < 0.5)
                {
                    Log(13, $"下一帧没有换画面（前进 {FramesPerStepRun} 帧之后与基准的像素差只有 {forwardDiff:0.00}，"
                            + $"fps={fps:0.###}，帧长={engine.FrameDurationMilliseconds:0.#}ms）");
                    return false;
                }

                if (restoreDiff >= 0.05)
                {
                    Log(13, $"上一帧没有退回原来那一帧（后退 {FramesPerStepRun} 帧之后与基准的像素差还有 "
                            + $"{restoreDiff:0.00}；这一步画面确实动了 {backDiff:0.00}）");
                    return false;
                }

                // 连按（两次之间不留静置）不能丢定位：逐帧每次发的都是"基准 + 第几帧"这样的绝对时刻，
                // 最后那一次定位落到哪儿，画面就该在哪儿。实测 5 连按与慢速走 5 帧落在同一帧（差 0.00）。
                for (var i = 0; i < FramesPerStepRun; i++) engine.StepFrame(true);

                PumpMessages(1500);

                var rapidShot = Path.Combine(AppContext.BaseDirectory, "shot-frame-rapid.png");
                engine.TakeSnapshot(rapidShot);

                var rapidDiff = MeanPixelDifference(steppedShot, rapidShot);

                if (rapidDiff >= 0.05)
                {
                    Log(13, $"连按 {FramesPerStepRun} 次之后没有停在「前进 {FramesPerStepRun} 帧」那一帧上"
                            + $"（与慢速前进那一帧差 {rapidDiff:0.00}，与基准差 "
                            + $"{MeanPixelDifference(reference, rapidShot):0.00}）");
                    return false;
                }

                Log(13, $"视频功能正常：{fps:0.###} fps（帧长 {engine.FrameDurationMilliseconds:0.#} ms）、"
                        + $"去隔行 {DeinterlaceModes.All.Count} 个模式可设、延迟往返正确、"
                        + $"逐帧换帧正常（前进 {FramesPerStepRun} 帧差 {forwardDiff:0.0}，"
                        + $"后退 {FramesPerStepRun} 帧回到基准的差 {restoreDiff:0.00}，"
                        + $"连按 {FramesPerStepRun} 次落点差 {rapidDiff:0.00}）");

                form.Close();
                PumpMessages(200);
                return true;
            }
            catch (Exception ex)
            {
                Log(13, "视频功能检查失败: " + ex);
                return false;
            }
        }

        /// <summary>
        /// 检查侧栏与播放列表是不是真的各自独立：互不牵连，而且都能脱离主窗口再归位。
        /// <para>
        /// 以前两者挤在同一个面板里，关掉播放列表会把侧栏一起收走——那是个实打实的耦合缺陷，
        /// 所以这里专门盯着它。
        /// </para>
        /// </summary>
        private static bool CheckSeparation(Form form)
        {
            var splitVideo = Find(form, "splitVideo") as SplitContainer;
            var splitMain = Find(form, "splitMain") as SplitContainer;
            var sidebar = Find(form, "sidebarPanel");
            var playlistHost = Find(form, "playlistHost");

            if (splitVideo == null || splitMain == null || sidebar == null || playlistHost == null)
            {
                Log(11, "分离检查：找不到分隔条或承载面板");
                return false;
            }

            var showPlaylist = FindViewMenuItem(form, "显示播放列表");
            var showSidebar = FindViewMenuItem(form, "显示侧栏");
            var floatSidebar = FindViewMenuItem(form, "侧栏分离为悬浮窗");
            var floatPlaylist = FindViewMenuItem(form, "播放列表分离为独立窗口");

            if (showPlaylist == null || showSidebar == null || floatSidebar == null || floatPlaylist == null)
            {
                Log(11, "分离检查：「视图」菜单里缺少相关菜单项");
                return false;
            }

            // ---- 1) 关掉播放列表，不应该连带收起侧栏 ----
            SetMenuChecked(showPlaylist, false);
            PumpMessages(250);

            if (splitVideo.Panel2Collapsed)
            {
                Log(11, "分离检查：关掉播放列表把侧栏一起收起来了（两者仍然耦合）");
                return false;
            }

            SetMenuChecked(showPlaylist, true);
            PumpMessages(250);

            // ---- 2) 关掉侧栏，不应该连带收起播放列表 ----
            SetMenuChecked(showSidebar, false);
            PumpMessages(250);

            if (splitMain.Panel2Collapsed)
            {
                Log(11, "分离检查：关掉侧栏把播放列表一起收起来了");
                return false;
            }

            SetMenuChecked(showSidebar, true);
            PumpMessages(250);

            // ---- 3) 侧栏分离为悬浮窗，再归位 ----
            SetMenuChecked(floatSidebar, true);
            PumpMessages(400);

            var sidebarForm = sidebar.FindForm();
            if (sidebarForm == null || ReferenceEquals(sidebarForm, form) || !sidebarForm.Visible)
            {
                Log(11, "分离检查：侧栏没有搬进独立的悬浮窗");
                return false;
            }

            if (!splitVideo.Panel2Collapsed)
            {
                Log(11, "分离检查：侧栏分离之后原停靠位置没有收起来");
                return false;
            }

            SetMenuChecked(floatSidebar, false);
            PumpMessages(400);

            if (!ReferenceEquals(sidebar.Parent, splitVideo.Panel2))
            {
                Log(11, "分离检查：侧栏没有归位到原来的分隔条");
                return false;
            }

            // ---- 4) 播放列表分离为独立窗口，再归位 ----
            SetMenuChecked(floatPlaylist, true);
            PumpMessages(400);

            var playlistForm = playlistHost.FindForm();
            if (playlistForm == null || ReferenceEquals(playlistForm, form) || !playlistForm.Visible)
            {
                Log(11, "分离检查：播放列表没有搬进独立窗口");
                return false;
            }

            SetMenuChecked(floatPlaylist, false);
            PumpMessages(400);

            if (!ReferenceEquals(playlistHost.Parent, splitMain.Panel2))
            {
                Log(11, "分离检查：播放列表没有归位到原来的面板");
                return false;
            }

            // ---- 5) 把两者都留在分离状态：吸附检查需要它，收尾的设置检查也需要 ----
            SetMenuChecked(floatSidebar, true);
            PumpMessages(300);
            SetMenuChecked(floatPlaylist, true);
            PumpMessages(400);

            // ---- 6) 吸附到主窗口边上：贴齐、跟着走、拖开脱离 ----
            if (!CheckSnapping(form, sidebar)) return false;

            Log(11, "侧栏与播放列表相互独立，且都能分离为独立窗口再归位");
            return true;
        }

        /// <summary>
        /// 悬浮窗吸附在主窗口边上。
        /// <para>
        /// 验三件事，缺一不可：
        /// <list type="number">
        ///   <item>拖到主窗口边缘附近会<b>贴齐</b>（左边界正好等于主窗口右边界）；</item>
        ///   <item>贴住之后移动主窗口，它<b>跟着走</b>（这才是"吸附"而不是"碰巧对齐"）；</item>
        ///   <item>拖开足够远会<b>脱离</b>，不再被拽回去。</item>
        /// </list>
        /// 另外顺带验下边贴齐（说明不只支持左右两侧）。
        /// </para>
        /// <para>调用时两者都处于分离状态；结束时把侧栏留在"吸附在右侧"。</para>
        /// </summary>
        /// <param name="form">主窗口。</param>
        /// <param name="sidebar">
        /// 侧栏控件。<b>必须由调用方在它还没被搬走时先取好引用</b>——
        /// 分离之后它的父级是悬浮窗（不是主窗口的子孙），
        /// 这时候再用 <c>Find(form, "sidebarPanel")</c> 是找不到它的。
        /// </param>
        private static bool CheckSnapping(Form form, Control sidebar)
        {
            var sidebarForm = sidebar?.FindForm();

            if (sidebarForm == null || ReferenceEquals(sidebarForm, form))
            {
                Log(11, "吸附检查：此刻侧栏不在悬浮窗状态");
                return false;
            }

            string Edge() => ((播放器.MainForm)form).SidebarSnap.ToString();

            // ---- 1) 先拖远：应当是未吸附状态 ----
            sidebarForm.Location = new Point(form.Left - sidebarForm.Width - 260, form.Top + 60);
            PumpMessages(250);

            if (Edge() != "None")
            {
                Log(11, $"吸附检查：拖远之后仍然是「{Edge()}」");
                return false;
            }

            // ---- 2) 拖到主窗口右侧附近：应当贴齐 ----
            sidebarForm.Location = new Point(form.Right + 8, form.Top + 90);
            PumpMessages(250);

            if (Edge() != "Right")
            {
                Log(11, $"吸附检查：拖到右侧附近没有被吸附（当前「{Edge()}」）");
                return false;
            }

            if (sidebarForm.Left != form.Right)
            {
                Log(11, $"吸附检查：吸附后没有贴齐（窗口左边界 {sidebarForm.Left}，主窗口右边界 {form.Right}）");
                return false;
            }

            // ---- 3) 移动主窗口：悬浮窗要跟着走 ----
            var before = sidebarForm.Location;
            var mainBefore = form.Location;
            const int deltaX = -60;
            const int deltaY = 40;

            form.Location = new Point(mainBefore.X + deltaX, mainBefore.Y + deltaY);
            PumpMessages(300);

            if (sidebarForm.Left != form.Right)
            {
                Log(11, $"吸附检查：主窗口移动后没有保持贴齐（{sidebarForm.Left} vs {form.Right}）");
                return false;
            }

            if (sidebarForm.Top != before.Y + deltaY)
            {
                Log(11, $"吸附检查：主窗口移动后悬浮窗没有跟着走"
                        + $"（上边界 {sidebarForm.Top}，期望 {before.Y + deltaY}）");
                return false;
            }

            // ---- 4) 拖开：应当脱离 ----
            sidebarForm.Location = new Point(form.Left - sidebarForm.Width - 200, form.Top + 60);
            PumpMessages(250);

            if (Edge() != "None")
            {
                Log(11, $"吸附检查：拖开之后没有脱离（当前「{Edge()}」）");
                return false;
            }

            // ---- 5) 下边也要能贴（说明不是只支持左右） ----
            // 先把窗口改矮，否则贴到主窗口下边会被工作区下沿钳回来，测不出贴齐。
            sidebarForm.Size = new Size(300, 170);
            PumpMessages(200);

            sidebarForm.Location = new Point(form.Left + 80, form.Bottom + 6);
            PumpMessages(250);

            if (Edge() != "Bottom")
            {
                Log(11, $"吸附检查：拖到下方附近没有被吸附（当前「{Edge()}」）");
                return false;
            }

            if (sidebarForm.Top != form.Bottom)
            {
                Log(11, $"吸附检查：下方吸附没有贴齐（窗口上边界 {sidebarForm.Top}，主窗口下边界 {form.Bottom}）");
                return false;
            }

            // ---- 6) 最后留在"吸附在右侧"，用来验证这个状态能存进设置 ----
            sidebarForm.Location = new Point(form.Right + 4, form.Top + 120);
            PumpMessages(250);

            if (Edge() != "Right" || sidebarForm.Left != form.Right)
            {
                Log(11, $"吸附检查：收尾时没能回到右侧吸附（「{Edge()}」，左边界 {sidebarForm.Left}）");
                return false;
            }

            Log(11, "悬浮窗吸附正常：贴齐、跟着主窗口移动、拖开脱离，左右下三边都能贴");
            return true;
        }

        /// <summary>
        /// 一次"逐帧"检查里连续走几帧。
        /// <para>
        /// 不能用 1 帧：本项目用的测试视频是白板讲解，相邻两帧常常一模一样
        /// （实测像素差 0.00~0.54），走一帧根本分辨不出"走没走"，
        /// 那样的断言实际上是在量 vout 的重绘噪声。走 5 帧（约 166ms）之后
        /// 画面差异稳定在 10 上下，和噪声拉开两个数量级。
        /// </para>
        /// </summary>
        private const int FramesPerStepRun = 5;

        /// <summary>
        /// 两次逐帧之间要留的静置时间。
        /// <para>
        /// 逐帧是靠 seek 实现的（libvlc 的 <c>NextFrame()</c> 在本项目里完全不生效），
        /// 而"定位到某时刻"要解码并重新送显，实测要几百毫秒才落地。
        /// 太短的话下一次定位会落在上一次还没排空的窗口里。
        /// </para>
        /// </summary>
        private const int SettleAfterStepMs = 600;

        /// <summary>连续走若干帧，每帧之间留出落地时间。</summary>
        private static void StepFrames(PlayerEngine engine, bool forward, int count)
        {
            for (var i = 0; i < count; i++)
            {
                engine.StepFrame(forward);
                PumpMessages(SettleAfterStepMs);
            }
        }

        /// <summary>打开视频并等到真正开始播放。</summary>
        private static bool PlayVideo(PlayerEngine engine, string video, out string error)
        {
            error = string.Empty;

            var playing = new ManualResetEventSlim(false);
            var failed = new ManualResetEventSlim(false);

            // ⚠ 订阅底层 MediaPlayer 的事件，而不是 PlayerEngine 的：
            // 后者会把事件派发回 UI 线程，而等待期间主线程被占住，消息循环停了就永远收不到，
            // 表现为"一直没进入 Playing"。这里改成边等边泵消息。
            EventHandler<EventArgs> onPlaying = (s, e) => playing.Set();
            EventHandler<EventArgs> onError = (s, e) => failed.Set();

            engine.Player.Playing += onPlaying;
            engine.Player.EncounteredError += onError;

            try
            {
                engine.Open(video);

                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < deadline && !playing.IsSet && !failed.IsSet)
                    PumpMessages(200);

                if (failed.IsSet) { error = "播放报错（格式不支持或文件损坏）"; return false; }
                if (!playing.IsSet) { error = "25 秒内没有进入 Playing 状态"; return false; }

                PumpMessages(500);
                return true;
            }
            finally
            {
                engine.Player.Playing -= onPlaying;
                engine.Player.EncounteredError -= onError;
            }
        }
    }
}
