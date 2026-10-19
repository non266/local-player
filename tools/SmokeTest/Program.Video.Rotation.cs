// 视频 —— 旋转（内核级 transform 滤镜）：一个片源六种旋转 + 主窗体菜单联动。
//
// 从 Program.Video.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// B-2：画面旋转 / 翻转（按文件记住）。
        /// <para>
        /// 三段：<b>纯函数</b>（选项串按取值拼对、历史里那串字读得回来、认不出来的当不旋转）→
        /// <b>真的转了</b>（合成四象限短片，用「视图 → 画面旋转」真实的菜单项逐个切，
        /// 每次截一帧，断言四个象限的颜色按预期搬家）→ <b>按文件记住</b>
        /// （切走再切回来，旋转自己回来）。
        /// </para>
        /// <para>
        /// 每一次观测到的排布都打进日志：libvlc 的 <c>transform-type=90</c> 究竟是顺时针还是逆时针
        /// <b>不靠猜</b>——实测出来是哪一边，菜单上的字就跟哪一边（C1 的教训："有插件"不等于"真的有效"）。
        /// </para>
        /// </summary>
        private static bool CheckVideoRotation()
        {
            // ---- ① 纯函数：内核参数与那串字 ----
            // 🔴 旋转只能在**内核**这一级设（媒体级四组写法实测全不生效），
            // 所以判据落在 CoreArguments 上——那才是真正决定画面转不转的地方。
            var clockwise = PlayerEngine.CoreArguments(ScreenRotation.Clockwise90);

            if (!clockwise.Contains("--video-filter=transform") || !clockwise.Contains("--transform-type=90"))
            {
                Log(13, "画面旋转检查：顺时针 90° 的内核参数不对（" + string.Join(" ", clockwise) + "）");
                return false;
            }

            var upsideDown = PlayerEngine.CoreArguments(ScreenRotation.UpsideDown);

            if (!upsideDown.Contains("--transform-type=180"))
            {
                Log(13, "画面旋转检查：180° 的内核参数不对（" + string.Join(" ", upsideDown) + "）");
                return false;
            }

            if (PlayerEngine.CoreArguments(ScreenRotation.None).Any(arg => arg.Contains("transform", StringComparison.Ordinal)))
            {
                Log(13, "画面旋转检查：不旋转的时候内核上还挂着 transform 滤镜");
                return false;
            }

            if (ScreenRotations.Parse(string.Empty) != ScreenRotation.None ||
                ScreenRotations.Parse("Clockwise90") != ScreenRotation.Clockwise90 ||
                ScreenRotations.Parse("hflip") != ScreenRotation.FlipHorizontal ||
                ScreenRotations.Parse("顺时针") != ScreenRotation.None)
            {
                Log(13, "画面旋转检查：历史里那串字读回来不对"
                        + $"（空 → {ScreenRotations.Parse(string.Empty)}、"
                        + $"Clockwise90 → {ScreenRotations.Parse("Clockwise90")}、"
                        + $"hflip → {ScreenRotations.Parse("hflip")}、"
                        + $"顺时针 → {ScreenRotations.Parse("顺时针")}）");
                return false;
            }

            // 每个取值都要有自己的 libvlc 取值，且互不重复
            var types = ScreenRotations.All
                .Where(entry => entry.Value != ScreenRotation.None)
                .Select(entry => ScreenRotations.TransformType(entry.Value))
                .ToList();

            if (types.Any(type => string.IsNullOrEmpty(type)) || types.Distinct().Count() != types.Count)
            {
                Log(13, "画面旋转检查：取值表里有两项对应同一个 transform-type（" + string.Join(" / ", types) + "）");
                return false;
            }

            // ---- ② 真视频：四个象限的颜色按预期搬家 ----
            if (!TryCreateQuadrantClip(out var clip, out var reason))
            {
                Log(13, "画面旋转检查：跳过（" + reason + "）");
                return true;
            }

            // ===== 临时实验结束 =====
            try
            {
                // 两首：第二首是同一个片子的副本（路径不同 = 一份独立的"按文件记住"）
                var second = Path.Combine(AppContext.BaseDirectory, "rotate-quadrants-2.mp4");
                File.Copy(clip, second, overwrite: true);

                try
                {
                    if (!CheckRotationOnMainForm(clip, second)) return false;

                    // M8 的三个复现探针：只在设了对应环境变量时才跑（默认不跑，行为一字不变）
                    if (!RunRotationStressIfRequested(clip, second)) return false;

                    if (!RunChurnStressIfRequested(clip, second)) return false;

                    if (!RunTightStressIfRequested(clip, second)) return false;

                    return RunSnapshotStressIfRequested(clip, second);
                }
                finally
                {
                    TryDelete(second);
                }
            }
            finally
            {
                TryDelete(clip);
            }
        }

        /// <summary>
        /// B-2 的真视频那一段：在真窗体上用「视图 → 画面旋转」的菜单项逐个切，
        /// 每次截一帧读四个象限；最后验"按文件记住"（另一首不受影响、切回来自己回来）。
        /// </summary>
        private static bool CheckRotationOnMainForm(string clip, string second)
        {
            using var form = new 播放器.MainForm(new[] { clip, second });
            form.Show();
            PumpMessages(400);

            var engine = form.Engine;
            var menu = form.MenuRotation;

            // M8 的心跳实验（只在设了 播放器_TEST_HEARTBEAT 时跑）：每次换内核之后多泵 2 秒、
            // 每 250 ms 往崩溃日志写一行心跳。崩的时候最后一行**是心跳**就说明那次不是崩在
            // `old.Stop()` 这一句里，而是上一次 `old.Dispose()` 留下的原生线程晚了一步炸
            // （两次都停在 `停旧播放器` 那一行，但换内核本来就 1 秒一次，"晚一秒炸"也会落在
            // 下一句 `Stop` 上——所以要专门在"没人动它"的空档里等一等）。
            var heartbeat = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("播放器_TEST_HEARTBEAT"));

            void SettleAfterSwap(string where)
            {
                if (!heartbeat) return;

                for (var tick = 1; tick <= 8; tick++)
                {
                    Journal($"心跳（{where} 之后）{tick * 250} ms");
                    PumpMessages(250);
                }
            }

            if (menu == null)
            {
                Log(13, "画面旋转检查：找不到「视图 → 画面旋转」");
                return false;
            }

            if (!PumpUntil(() => engine.HasMedia && engine.HasVideo, 15000))
            {
                Log(13, $"画面旋转检查：四象限短片没播起来（有媒体 {engine.HasMedia}，有画面 {engine.HasVideo}）");
                return false;
            }

            // 期望的排布：左上 / 右上 / 左下 / 右下（实测出来的，见 docs/1.3.0计划.md 的 B-2）
            var expected = new Dictionary<ScreenRotation, string[]>
            {
                [ScreenRotation.None] = new[] { "红", "绿", "蓝", "黄" },
                [ScreenRotation.Clockwise90] = new[] { "蓝", "红", "黄", "绿" },
                [ScreenRotation.UpsideDown] = new[] { "黄", "蓝", "绿", "红" },
                [ScreenRotation.CounterClockwise90] = new[] { "绿", "黄", "红", "蓝" },
                [ScreenRotation.FlipHorizontal] = new[] { "绿", "红", "黄", "蓝" },
                [ScreenRotation.FlipVertical] = new[] { "蓝", "黄", "红", "绿" }
            };

            var first = true;
            var observed = new Dictionary<ScreenRotation, string>();

            foreach (var (label, value) in ScreenRotations.All)
            {
                var item = RotationMenuItem(menu, value);

                if (item == null)
                {
                    Log(13, $"画面旋转检查：菜单里没有「{label}」这一项");
                    return false;
                }

                if (first)
                {
                    // 还没切过：先确认素材本身与截图链路是好的（否则后面全是在量别的东西）
                    if (!PumpUntil(() => engine.Time >= 300, 8000))
                    {
                        Log(13, $"画面旋转检查：短片没有推进（{engine.Time} ms）");
                        return false;
                    }
                }
                else
                {
                    var before = engine.Time;

                    // 点下去必须**真的生效**：换内核那一瞬间整组菜单是禁用的，而
                    // PerformClick 对禁用项是空操作——不问这一句，"压根没切过"会伪装成
                    // 后面那句"位置没回来"或者"四个象限没按预期搬家"，白查半天。
                    if (!ClickRotationMenuItem(menu, value, engine, out var why))
                    {
                        Log(13, $"画面旋转检查：切到「{label}」没生效（{why}）");
                        return false;
                    }

                    // 和以前一样先泵 200 ms：换内核是同步做完的，但"媒体真的播起来"要等一会儿。
                    // 不泵这一下，下面那句 HasMedia && IsPlaying 可能读到的还是**换之前**的状态。
                    PumpMessages(200);

                    if (!PumpUntil(() => engine.HasMedia && engine.IsPlaying, 15000))
                    {
                        Log(13, $"画面旋转检查：切到「{label}」之后没有重新播起来");
                        return false;
                    }

                    // 位置保持：换个画面不该把进度丢回开头（重载之后要回到切之前那一刻附近）
                    if (!PumpUntil(() => Math.Abs(engine.Time - before) < 1500, 10000))
                    {
                        Log(13, $"画面旋转检查：切到「{label}」之后位置没回来"
                                + $"（切之前在 {before} ms，现在 {engine.Time} ms）");
                        return false;
                    }

                    SettleAfterSwap($"切到「{label}」");
                }

                first = false;

                // 停在固定的一帧再截图：素材是静态图案，所以 vout 的滞后不影响判据
                engine.SeekTo(1000);
                PumpMessages(700);

                var actual = SnapshotQuadrants(engine, "rotate-" + value);

                if (actual == null)
                {
                    Log(13, $"画面旋转检查：「{label}」截不到画面");
                    return false;
                }

                observed[value] = string.Join(string.Empty, actual);

                Log(13, $"画面旋转观测：「{label}」→ 左上{actual[0]} 右上{actual[1]} "
                        + $"左下{actual[2]} 右下{actual[3]}");

                if (!actual.SequenceEqual(expected[value]))
                {
                    Log(13, $"画面旋转检查：「{label}」的四个象限没有按预期搬家"
                            + $"（观测 左上{actual[0]} 右上{actual[1]} 左下{actual[2]} 右下{actual[3]}；"
                            + $"期望 左上{expected[value][0]} 右上{expected[value][1]} "
                            + $"左下{expected[value][2]} 右下{expected[value][3]}）");
                    return false;
                }
            }

            if (observed.Values.Distinct().Count() < 5)
            {
                Log(13, "画面旋转检查：几种旋转截出来的画面几乎一样（"
                        + string.Join(" / ", observed.Select(pair => pair.Key + "=" + pair.Value)) + "）");
                return false;
            }

            // ---- 换内核**不该漏原生线程**（M8 的根因，1.5.0）----
            // `MediaPlayer.Stop()` 会漏掉一整套输入 / 解码 / 视频输出线程（这台机器上实测 ~35 个），
            // 换到 240 次左右进程就以 `0xC0000005` 崩掉。这里**再切一遍六个取值**：
            // 这一遍六套内核与播放器都已经建好，不存在"新建播放器"的正常增长，
            // 所以线程数必须基本不动（实测第二遍 ~0，用 Stop 的话是 +200 上下）。
            // ⚠ 每次都要新取一个 Process 对象：`Process.Threads` 是**取过一次就缓存**的，
            // 同一个对象上前后读两次会拿到同一份快照（第一版就是这么假绿的）。
            static int CurrentThreadCount()
            {
                using var current = System.Diagnostics.Process.GetCurrentProcess();
                return current.Threads.Count;
            }

            var threadsBefore = CurrentThreadCount();

            foreach (var (label, value) in ScreenRotations.All)
            {
                if (!ClickRotationMenuItem(menu, value, engine, out var againWhy))
                {
                    Log(13, $"画面旋转检查：第二遍切「{label}」没生效（{againWhy}）");
                    return false;
                }

                // 和真的切换一样等到重新播起来：泄漏出来的那批原生线程是**播放时**起来的，
                // 只泵 200 毫秒的话它们还没到位，这一遍就量不出差别（第一版就栽在这儿）。
                if (!PumpUntil(() => engine.HasMedia && engine.IsPlaying, 15000))
                {
                    Log(13, $"画面旋转检查：第二遍切「{label}」之后没有重新播起来");
                    return false;
                }

                PumpMessages(300);
            }

            var threadsAfter = CurrentThreadCount();

            if (threadsAfter - threadsBefore > 60)
            {
                Log(13, $"画面旋转检查：换内核在漏原生线程（第二遍六次切换之后线程从 {threadsBefore} "
                        + $"涨到 {threadsAfter}）：`Stop()` 一个播放器会漏掉一整套输入 / 解码 / 视频输出"
                        + "线程，攒到上千就会以 0xC0000005 崩掉（M8）");
                return false;
            }

            // ---- ③ 按文件记住：切到另一首（没转过）不受影响，切回来自己回来 ----
            var play = FindTopMenuItem(form, "播放");
            var nextItem = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");
            var previousItem = play == null ? null : FindMenuItem(play.DropDownItems, "上一个");

            if (nextItem == null || previousItem == null)
            {
                Log(13, "画面旋转检查：找不到「播放 → 下一个 / 上一个」");
                return false;
            }

            var remembered = engine.Rotation;

            if (remembered == ScreenRotation.None)
            {
                Log(13, "画面旋转检查：六种都切完了，最后一轮却没留下旋转值");
                return false;
            }

            nextItem.PerformClick();
            PumpMessages(300);

            if (!PumpUntil(() => engine.CurrentPath != null &&
                                 string.Equals(engine.CurrentPath, second, StringComparison.OrdinalIgnoreCase), 15000))
            {
                Log(13, "画面旋转检查：切到另一首没成功（现在在播 "
                        + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                return false;
            }

            // M8 就是崩在这一步之后的换内核上（第一次复用缓存内核），空档里等一等看它会不会晚一步炸
            SettleAfterSwap("换到另一首");

            if (engine.Rotation != ScreenRotation.None)
            {
                Log(13, $"画面旋转检查：另一首（从没转过）却带着旋转 {engine.Rotation}");
                return false;
            }

            engine.SeekTo(1000);
            PumpMessages(700);

            var otherQuadrants = SnapshotQuadrants(engine, "rotate-other");

            if (otherQuadrants == null)
            {
                Log(13, "画面旋转检查：另一首截不到画面");
                return false;
            }

            if (!otherQuadrants.SequenceEqual(expected[ScreenRotation.None]))
            {
                Log(13, "画面旋转检查：另一首的画面也被转了（"
                        + $"左上{otherQuadrants[0]} 右上{otherQuadrants[1]} "
                        + $"左下{otherQuadrants[2]} 右下{otherQuadrants[3]}）");
                return false;
            }

            // 回到第一首：它自己记着的旋转要回来（先退到 3 秒以内，否则"上一个"是回本曲开头）
            engine.SeekTo(500);
            PumpMessages(300);
            previousItem.PerformClick();
            PumpMessages(300);

            if (!PumpUntil(() => engine.CurrentPath != null &&
                                 string.Equals(engine.CurrentPath, clip, StringComparison.OrdinalIgnoreCase), 15000))
            {
                Log(13, "画面旋转检查：切不回第一首（现在在播 "
                        + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                return false;
            }

            SettleAfterSwap("切回第一首");

            if (engine.Rotation != remembered)
            {
                Log(13, $"画面旋转检查：切回来之后旋转没跟着回来（记着的是 {remembered}，现在是 {engine.Rotation}）");
                return false;
            }

            engine.SeekTo(1000);
            PumpMessages(700);

            var backQuadrants = SnapshotQuadrants(engine, "rotate-back");

            if (backQuadrants == null || !backQuadrants.SequenceEqual(expected[remembered]))
            {
                Log(13, "画面旋转检查：切回来之后画面没转（"
                        + (backQuadrants == null
                            ? "截不到画面"
                            : $"左上{backQuadrants[0]} 右上{backQuadrants[1]} "
                              + $"左下{backQuadrants[2]} 右下{backQuadrants[3]}") + "）");
                return false;
            }

            Log(13, "画面旋转正常：内核参数按取值拼对（不旋转时不挂滤镜）、历史里那串字读得回来，"
                    + "六种取值各自把四个象限搬到该去的位置（顺时针 90° / 180° / 逆时针 90° / "
                    + "水平翻转 / 垂直翻转 互不相同），切换会重载并停在原位置，"
                    + "旋转按文件各记各的（另一首不受影响、切回来自己回来）");
            return true;
        }
    }
}
