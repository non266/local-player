// 视频 —— 应力测试与四象限快照（环境变量开关、连续旋转 / 换内核，以及读像素判色）。
//
// 从 Program.Video.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// M8 的第二个探针：**"截一张图，马上换一首"**重复若干轮。
        /// <para>
        /// 为什么是这个时序：加上细日志之后崩点定到了换内核里的 <c>old.Stop()</c>
        /// （产品日志每次都是写到"② 停旧播放器"就没下文），而崩之前最后一个动作永远是
        /// **刚截过一张图**。所以这个探针截完图一个字都不等，立刻按「下一个」去换内核——
        /// 比"六种旋转 + 截图"那个探针短得多（一次约一秒），逼起来便宜。
        /// </para>
        /// <para>
        /// 环境变量 <c>播放器_TEST_STRESS_SNAPSHOT</c>（值 = 轮数，写 <c>1</c> 或非数字按 20 轮）。
        /// </para>
        /// </summary>
        private static bool RunSnapshotStressIfRequested(string clip, string second)
        {
            var setting = Environment.GetEnvironmentVariable("播放器_TEST_STRESS_SNAPSHOT");

            if (string.IsNullOrWhiteSpace(setting)) return true;

            var rounds = int.TryParse(setting, out var parsed) && parsed > 1 ? parsed : 20;

            Log(13, $"截图换歌压测：开跑（{rounds} 次「截一张图 → 马上换一首」）");

            using var form = new 播放器.MainForm(new[] { clip, second });
            form.Show();
            PumpMessages(400);

            var engine = form.Engine;
            var play = FindTopMenuItem(form, "播放");
            var nextItem = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");

            if (nextItem == null)
            {
                Log(13, "截图换歌压测：找不到「播放 → 下一个」");
                return false;
            }

            if (!PumpUntil(() => engine.HasMedia && engine.HasVideo, 15000))
            {
                Log(13, $"截图换歌压测：短片没播起来（有媒体 {engine.HasMedia}，有画面 {engine.HasVideo}）");
                return false;
            }

            for (var round = 1; round <= rounds; round++)
            {
                var target = round % 2 == 1 ? second : clip;

                if (round > 1 && !PumpUntil(() => engine.HasMedia && engine.IsPlaying, 15000))
                {
                    Log(13, $"截图换歌压测：第 {round}/{rounds} 次之前没有在播");
                    return false;
                }

                engine.SeekTo(1000);
                PumpMessages(200);

                Journal($"截图换歌压测：第 {round}/{rounds} 次 → 截图");
                if (SnapshotQuadrants(engine, "stress-snap") == null)
                {
                    Log(13, $"截图换歌压测：第 {round}/{rounds} 次截不到画面");
                    return false;
                }

                Journal($"截图换歌压测：第 {round}/{rounds} 次 → 立刻换一首");
                nextItem.PerformClick();

                if (!PumpUntil(() => engine.CurrentPath != null &&
                                     string.Equals(engine.CurrentPath, target, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(13, $"截图换歌压测：第 {round}/{rounds} 次没换过去（现在在播 "
                            + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                Log(13, $"截图换歌压测：第 {round}/{rounds} 次跑完（" +
                        (round == 1 ? "第一次，目标内核是新建的" : "又是复用缓存里的内核") + "）");
            }

            Log(13, $"截图换歌压测通过：{rounds} 次「截图 → 立刻换一首」都跑完，进程没有崩");
            return true;
        }

        /// <summary>
        /// M8 的第四个探针：**不给喘气的连续换内核**（环境变量 <c>播放器_TEST_STRESS_TIGHT</c>，
        /// 值 = 次数，默认 200）。
        /// <para>
        /// 前两个探针每次切换都要等"重新播起来 / 位置回来 / 截一张图"，一次 ~1 秒；
        /// 这个探针点完旋转菜单只泵 60 毫秒就点下一项（每 6 次顺带换一首），
        /// 把换内核的频率拉到每秒好几次，专冲"上一次换内核还没收尾、下一次就来了"这种竞态。
        /// </para>
        /// </summary>
        private static bool RunTightStressIfRequested(string clip, string second)
        {
            var setting = Environment.GetEnvironmentVariable("播放器_TEST_STRESS_TIGHT");

            if (string.IsNullOrWhiteSpace(setting)) return true;

            var rounds = int.TryParse(setting, out var parsed) && parsed > 0 ? parsed : 200;

            Log(13, $"连续换内核压测：开跑（{rounds} 次旋转点击，每次只泵 60 毫秒）");

            using var form = new 播放器.MainForm(new[] { clip, second });
            form.Show();
            PumpMessages(400);

            var engine = form.Engine;
            var menu = form.MenuRotation;
            var play = FindTopMenuItem(form, "播放");
            var nextItem = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");

            if (menu == null || nextItem == null)
            {
                Log(13, "连续换内核压测：找不到「视图 → 画面旋转」或「播放 → 下一个」");
                return false;
            }

            if (!PumpUntil(() => engine.HasMedia && engine.HasVideo, 15000))
            {
                Log(13, $"连续换内核压测：短片没播起来（有媒体 {engine.HasMedia}，有画面 {engine.HasVideo}）");
                return false;
            }

            var values = ScreenRotations.All.Select(pair => pair.Value).ToArray();

            for (var i = 0; i < rounds; i++)
            {
                var value = values[i % values.Length];

                if (i % 10 == 0) Journal($"连续换内核压测：第 {i + 1}/{rounds} 次，目标「{value}」");

                // 每次换内核都会新建一个播放器、放掉旧的，还要重建时长扫描器——
                // "崩在第 240 多次"这种位置很像**资源累积**（句柄 / 原生对象泄漏），
                // 所以每隔一段把进程的资源量也记进崩溃日志：句柄数一路涨上去就是泄漏。
                if (i % 25 == 0)
                {
                    using var current = System.Diagnostics.Process.GetCurrentProcess();

                    Journal($"连续换内核压测：第 {i + 1}/{rounds} 次的资源——句柄 {current.HandleCount}、"
                            + $"线程 {current.Threads.Count}、托管内存 {GC.GetTotalMemory(false) / 1024} KB");
                }

                var item = RotationMenuItem(menu, value);
                // 这里**故意**不等菜单可用：真实用户连点时也不会等。
                // 菜单被禁用时 PerformClick 是空操作——那正好也是"连续点击"的一种真实情形。
                if (item != null) item.PerformClick();

                PumpMessages(60);

                if (i % 6 == 5)
                {
                    Journal($"连续换内核压测：第 {i + 1}/{rounds} 次之后换一首");
                    nextItem.PerformClick();
                    PumpMessages(60);
                }
            }

            Log(13, $"连续换内核压测通过：{rounds} 次连续切换都跑完，进程没有崩");
            return true;
        }

        /// <summary>
        /// M8 的第三个探针：**先"折腾"出若干套主窗体 / 引擎（建了又放），再跑旋转循环**。
        /// <para>
        /// 为什么这么试：M8 只在**全量**里出现过（单跑第 13 步 11 次全绿、各种"前几步 + 第 13 步"
        /// 的组合也几乎全绿），而全量与"只跑第 13 步"最大的差别是——进程里**已经建过、又放过**
        /// 好几套窗体 / 引擎 / libvlc 实例。这个探针把这个差别单独搬出来：
        /// 先造若干套窗体（各自起播、泵一会儿、**按冒烟平时的写法只 Dispose 不 Close**），
        /// 再跑"六种旋转 + 换一首"。
        /// </para>
        /// <para>环境变量 <c>播放器_TEST_STRESS_CHURN</c>（值 = 套数，默认 3）。</para>
        /// </summary>
        private static bool RunChurnStressIfRequested(string clip, string second)
        {
            var setting = Environment.GetEnvironmentVariable("播放器_TEST_STRESS_CHURN");

            if (string.IsNullOrWhiteSpace(setting)) return true;

            var forms = int.TryParse(setting, out var parsed) && parsed > 0 ? parsed : 3;

            Log(13, $"先折腾压测：先建 / 放 {forms} 套主窗体，再跑旋转循环");

            for (var i = 1; i <= forms; i++)
            {
                using var form = new 播放器.MainForm(new[] { clip, second });
                form.Show();
                PumpMessages(300);

                if (!PumpUntil(() => form.Engine.HasMedia && form.Engine.HasVideo, 15000))
                {
                    Log(13, $"先折腾压测：第 {i}/{forms} 套窗体没播起来");
                    return false;
                }

                // 动它一下（seek），别只是"开着"——真实使用里这些窗体是播过东西的
                form.Engine.SeekTo(500);
                PumpMessages(200);

                Journal($"先折腾压测：放掉第 {i}/{forms} 套窗体");
            }

            Journal("先折腾压测：折腾完了，开始旋转循环");
            return CheckRotationStress(clip, second, rounds: 6);
        }

        /// <summary>
        /// M8 的复现探针：**只在设了 <c>播放器_TEST_STRESS_ROTATION</c> 时才跑**
        /// （默认不跑，所以正常全量冒烟的行为一字不变、步数仍是 20）。
        /// <para>
        /// 为什么要有它：换画面旋转内核那一段**偶发原生硬崩**（`0xC0000005`，整个进程直接没）——
        /// 1.4.0 验收时全量 5 次里崩了 2 次、安装目录那次第 1 遍、2026-10-15 又是第 1 遍就崩，
        /// 崩的位置都一样：六种旋转切完之后**换一首**那一刻（也就是第一次**复用**
        /// 已经建好的内核）。原生崩不是"失败返回"，写不成断言，所以它只能是个探针：
        /// 判据是"**跑完 20 轮、进程还活着**"，崩了就靠 <c>smoke-journal.txt</c> 的最后一行定位。
        /// </para>
        /// <para>
        /// 用法（在输出目录里）：
        /// <c>$env:播放器_TEST_STRESS_ROTATION='20'; dotnet SmokeTest.dll 视频功能与逐帧</c>——
        /// 值写轮数，写 <c>1</c> 或别的非数字都按默认 20 轮。
        /// </para>
        /// </summary>
        private static bool RunRotationStressIfRequested(string clip, string second)
        {
            var setting = Environment.GetEnvironmentVariable("播放器_TEST_STRESS_ROTATION");

            if (string.IsNullOrWhiteSpace(setting)) return true;

            var rounds = int.TryParse(setting, out var parsed) && parsed > 1 ? parsed : 20;

            Log(13, $"画面旋转压测：开跑（{rounds} 轮，同一个进程内；崩了就看 smoke-journal.txt 的最后一行）");

            return CheckRotationStress(clip, second, rounds);
        }

        /// <summary>
        /// "六种旋转各切一次 → 切到另一首"重复若干轮（M8 的复现探针，见
        /// <see cref="RunRotationStressIfRequested"/>）。
        /// <para>
        /// 第一轮每次设旋转都是**新建**内核；从第二轮起六个取值的内核都已建好，
        /// 每次切换都是**复用**——也就是 1.4.0 验收时崩的那一种。
        /// </para>
        /// </summary>
        private static bool CheckRotationStress(string clip, string second, int rounds)
        {
            using var form = new 播放器.MainForm(new[] { clip, second });
            form.Show();
            PumpMessages(400);

            var engine = form.Engine;
            var menu = form.MenuRotation;
            var play = FindTopMenuItem(form, "播放");
            var nextItem = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");

            if (menu == null || nextItem == null)
            {
                Log(13, "画面旋转压测：找不到「视图 → 画面旋转」或「播放 → 下一个」");
                return false;
            }

            // 必须先等出画面：没有画面时 SetRotation 会**如实拒掉**整组菜单
            // （"这个文件没有画面（音频），旋转对它没有作用"），那样压测就变成空转了。
            if (!PumpUntil(() => engine.HasMedia && engine.HasVideo, 15000))
            {
                Log(13, $"画面旋转压测：短片没播起来（有媒体 {engine.HasMedia}，有画面 {engine.HasVideo}）");
                return false;
            }

            for (var round = 1; round <= rounds; round++)
            {
                foreach (var (label, value) in ScreenRotations.All)
                {
                    Journal($"旋转压测：第 {round}/{rounds} 轮 → 设「{label}」");

                    if (!ClickRotationMenuItem(menu, value, engine, out var why))
                    {
                        Log(13, $"画面旋转压测：第 {round}/{rounds} 轮设「{label}」没生效（{why}）");
                        return false;
                    }

                    // 给换内核 + 重新起播留一点时间，下一轮点的时候状态是稳的
                    PumpMessages(200);

                    // 和正式检查**一样**：定位到固定一帧再截一张图。
                    // 少了这一步就不是同一个场景——正式检查每次都在六个取值上各截一张，
                    // 而 `TakeSnapshot` 是直接敲 vout 的（换内核时正对着它动刀）。
                    engine.SeekTo(1000);
                    PumpMessages(700);

                    if (SnapshotQuadrants(engine, "stress-rotate-" + value) == null)
                    {
                        Log(13, $"画面旋转压测：第 {round}/{rounds} 轮「{label}」截不到画面");
                        return false;
                    }
                }

                // 这一轮该停在谁身上：第 1 轮从第一首切到第二首，之后来回切
                var target = round % 2 == 1 ? second : clip;

                Journal($"旋转压测：第 {round}/{rounds} 轮 → 切到「{Path.GetFileName(target)}」");
                nextItem.PerformClick();
                PumpMessages(200);

                if (!PumpUntil(() => engine.CurrentPath != null &&
                                     string.Equals(engine.CurrentPath, target, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(13, $"画面旋转压测：第 {round}/{rounds} 轮没切到「{Path.GetFileName(target)}」"
                            + $"（现在在播 {Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                // 换完这一首也要截一张（正式检查就是"换过去 → 定位 → 截图"再换回来）
                engine.SeekTo(1000);
                PumpMessages(700);

                if (SnapshotQuadrants(engine, "stress-other") == null)
                {
                    Log(13, $"画面旋转压测：第 {round}/{rounds} 轮换过去之后截不到画面");
                    return false;
                }

                Log(13, $"画面旋转压测：第 {round}/{rounds} 轮跑完"
                        + (round == 1 ? "（这一轮六个取值都是新建内核）" : "（这一轮全是复用缓存里的内核）"));

                using (var current = System.Diagnostics.Process.GetCurrentProcess())
                {
                    Journal($"画面旋转压测：第 {round}/{rounds} 轮之后资源——句柄 {current.HandleCount}、"
                            + $"线程 {current.Threads.Count}");
                }
            }

            Log(13, $"画面旋转压测通过：{rounds} 轮「六种旋转 + 换一首」都跑完，进程没有崩");
            return true;
        }

        /// <summary>
        /// 点一次旋转菜单，并确认**真的生效了**。
        /// <para>
        /// 换内核那一瞬间「视图 → 画面旋转」整组是**禁用**的（<c>HasVideo</c> 短暂为假），
        /// 而 <c>ToolStripItem.PerformClick()</c> 对禁用项是空操作——探针如果不问这一句，
        /// "没崩"就可能只是"压根没切过"，那是最糟的一种假绿。
        /// </para>
        /// </summary>
        private static bool ClickRotationMenuItem(
            ToolStripMenuItem menu, ScreenRotation value, PlayerEngine engine, out string why)
        {
            var item = RotationMenuItem(menu, value);

            if (item == null)
            {
                why = "菜单里没有这一项";
                return false;
            }

            if (!PumpUntil(() => menu.Enabled, 15000))
            {
                why = "等 15 秒菜单还是禁用的（一直没有画面？）";
                return false;
            }

            item.PerformClick();

            if (!PumpUntil(() => engine.Rotation == value, 15000))
            {
                why = $"点了之后引擎的旋转值还是 {engine.Rotation}"
                      + "（没有画面时 SetRotation 会拒掉这一次，所以这也是它在说话）";
                return false;
            }

            why = string.Empty;
            return true;
        }

        /// <summary>在「视图 → 画面旋转」里按取值找菜单项。</summary>
        private static ToolStripMenuItem? RotationMenuItem(ToolStripMenuItem menu, ScreenRotation rotation)
        {
            foreach (var item in menu.DropDownItems.OfType<ToolStripMenuItem>())
            {
                if (item.Tag is ScreenRotation tag && tag == rotation) return item;
            }

            return null;
        }
    }
}
