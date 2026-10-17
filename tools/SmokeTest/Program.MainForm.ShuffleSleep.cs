
// 洗牌袋随机播放与睡眠定时器（1.5.0）。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// 1.5.0 F2：随机播放改成"洗牌袋"（一轮之内每首恰好一次，一轮完了重洗）。
        /// <para>
        /// 三段：**纯逻辑**（固定种子：一轮是排列、第二轮的第一个不是上一轮最后那个、单曲不卡死）
        /// → **菜单真的能开**（这条顺手抓到一个真 bug：菜单项原来没有 CheckOnClick，
        /// 点了什么也不会发生）→ **真播放**（4 首、连按 4 次「下一个」拿到 4 首互不相同的）。
        /// </para>
        /// </summary>
        private static bool CheckShuffleBag()
        {
            // ---- ① 纯逻辑：固定种子，结果完全确定 ----
            // 按真实用法模拟：当前在播 0，然后一路"取下一个、它就是新的当前"。
            var bag = new ShuffleBag(new Random(20261016));
            bag.Reset(6);

            var current = 0;
            var round = new List<int> { current };

            for (var i = 0; i < 5; i++)
            {
                current = bag.Next(current);
                round.Add(current);
            }

            if (round.Distinct().Count() != 6)
            {
                Log(6, "洗牌袋：一轮里没有把 6 首都播到（" + string.Join(",", round) + "）");
                return false;
            }

            // 一轮结束：下一个会触发重洗，第一个不能是刚播过的那一首
            var lastOfRound = current;
            var firstOfNextRound = bag.Next(lastOfRound);

            if (firstOfNextRound == lastOfRound)
            {
                Log(6, "洗牌袋：换一轮之后第一个还是上一轮最后播的那一首（听着像连着播了两遍）");
                return false;
            }

            // 再多抽一些：相邻两次永远不能是同一个下标
            var previous = firstOfNextRound;

            for (var i = 0; i < 30; i++)
            {
                var next = bag.Next(previous);

                if (next == previous)
                {
                    Log(6, $"洗牌袋：连着抽到了同一个下标（{next}）");
                    return false;
                }

                previous = next;
            }

            var single = new ShuffleBag(new Random(1));
            single.Reset(1);

            if (single.Next(0) != 0)
            {
                Log(6, "洗牌袋：只有一首的时候没有返回 0（会卡死）");
                return false;
            }

            // 列表长度变了要按新的池子重洗，不能拿到越界或者不存在的下标
            bag.Reset(3);

            var shortCurrent = 0;

            for (var i = 0; i < 6; i++)
            {
                var next = bag.Next(shortCurrent);

                if (next == shortCurrent || next is < 0 or > 2)
                {
                    Log(6, $"洗牌袋：列表变短之后拿到了不该拿的下标（{shortCurrent} → {next}）");
                    return false;
                }

                shortCurrent = next;
            }

            // ---- ② 菜单真的能开（这里原来是个真 bug：菜单项没有 CheckOnClick）----
            // 5 首：正在播第 1 首，一轮还能再播 4 首——所以连按 4 次必须都不重样；
            // 第 5 次会开始新的一轮，那时只要求"不立刻重复上一首"。
            var wavs = new List<string>();

            for (var i = 0; i < 5; i++)
            {
                var path = Path.Combine(AppContext.BaseDirectory, $"shuffle-{i}.wav");
                // 5 秒足够：这几首只需要"能播起来、能被换掉"，播得越长这一步越慢
                WriteWav(path, seconds: 5, frequency: 300 + i * 90);
                wavs.Add(path);
            }

            try
            {
                using var form = new 播放器.MainForm(wavs.ToArray());
                form.Show();
                PumpMessages(400);

                var engine = form.Engine;
                var play = FindTopMenuItem(form, "播放");
                var shuffleItem = play == null ? null : FindMenuItem(play.DropDownItems, "随机播放");
                var nextItem = play == null ? null : FindMenuItem(play.DropDownItems, "下一个");

                if (shuffleItem == null || nextItem == null)
                {
                    Log(6, "洗牌袋：找不到「播放 → 随机播放 / 下一个」");
                    return false;
                }

                if (!PumpUntil(() => engine.HasMedia && engine.IsPlaying, 10000))
                {
                    Log(6, "洗牌袋：4 首 WAV 没播起来");
                    return false;
                }

                shuffleItem.PerformClick();

                if (!form.Settings.Shuffle)
                {
                    Log(6, "洗牌袋：点了「随机播放」却没打开（菜单项没有 CheckOnClick，点了什么也不会发生）");
                    return false;
                }

                // ---- ③ 真播放：一轮之内连按 4 次「下一个」，拿到 4 首互不相同的 ----
                var visited = new List<string>();

                for (var i = 0; i < 4; i++)
                {
                    var before = engine.CurrentPath;

                    nextItem.PerformClick();

                    if (!PumpUntil(() => engine.CurrentPath != null && engine.CurrentPath != before, 15000))
                    {
                        Log(6, $"洗牌袋：第 {i + 1} 次「下一个」没有换歌（还是在播 "
                                + Path.GetFileName(engine.CurrentPath ?? string.Empty) + "）");
                        return false;
                    }

                    visited.Add(engine.CurrentPath!);
                }

                var distinct = visited.Distinct(StringComparer.OrdinalIgnoreCase).Count();

                if (distinct != visited.Count)
                {
                    Log(6, "洗牌袋：这一轮里连按 4 次「下一个」出现了重复的一首（"
                            + string.Join(" / ", visited.Select(Path.GetFileName)) + "）");
                    return false;
                }

                // 第 5 次：一轮结束、重洗，只要求"不立刻重复刚播过的那一首"
                var previousPath = engine.CurrentPath;
                nextItem.PerformClick();

                if (!PumpUntil(() => engine.CurrentPath != null && engine.CurrentPath != previousPath, 15000))
                {
                    Log(6, "洗牌袋：第 5 次「下一个」没有换歌（还是在播 "
                            + Path.GetFileName(engine.CurrentPath ?? string.Empty) + "）");
                    return false;
                }

                Log(6, "随机播放正常：洗牌袋一轮之内每首恰好一次、换一轮不会立刻重复上一首，"
                        + "菜单能开关（原来点不动，是个真 bug），"
                        + "连按 4 次「下一个」拿到 4 首互不相同的（"
                        + string.Join(" / ", visited.Select(Path.GetFileName)) + "）");
                return true;
            }
            finally
            {
                foreach (var wav in wavs) TryDelete(wav);
            }
        }

        private static ToolStripMenuItem? FindSleepTimerItem(播放器.MainForm form, string name)
        {
            var menu = form.MenuSleepTimer;
            if (menu == null) return null;

            foreach (ToolStripItem item in menu.DropDownItems)
            {
                if (item is ToolStripMenuItem menuItem && menuItem.Name == name)
                    return menuItem;
            }

            return null;
        }

        /// <summary>
        /// 1.5.0 F1：睡眠定时器。
        /// <para>
        /// 三段：**纯逻辑**（到点判定 / 剩余时间怎么写 / "听完这一首"不看到点时间）→
        /// **按时间到点**（暂停，而且不切歌）→ **听完这一首就停**（这一首放完不再往下播）。
        /// </para>
        /// <para>
        /// ⚠ 判据挑的是"**不播完也能验**"的场景：到点那一段用 30 秒的素材 + 1.2 秒的定时器，
        /// 不能靠"等它播完再看到底停没停"——那样"播完自动停"和"定时器让它停"结果一样，
        /// 定时器那一支不动作的变异也会照样绿（1.3.0 的 A-3 就栽在这种判据上）。
        /// </para>
        /// </summary>
        private static bool CheckSleepTimer()
        {
            // ---- ① 纯逻辑 ----
            const long now = 10_000;

            var timer = SleepTimer.After(now, TimeSpan.FromSeconds(90));

            if (timer.IsDue(now) || timer.IsDue(now + 89_999))
            {
                Log(6, "睡眠定时器：还没到点就说到了");
                return false;
            }

            if (!timer.IsDue(now + 90_000))
            {
                Log(6, "睡眠定时器：到点了却说没到（边界判据写成了 >）");
                return false;
            }

            if (timer.RemainingMilliseconds(now + 30_000) != 60_000)
            {
                Log(6, $"睡眠定时器：剩余时间算错了（{timer.RemainingMilliseconds(now + 30_000)}，期望 60000）");
                return false;
            }

            if (timer.RemainingMilliseconds(now + 200_000) != 0)
            {
                Log(6, "睡眠定时器：过了点之后剩余时间没有归零");
                return false;
            }

            if (SleepTimer.DescribeRemaining(90_000) != "还剩 1 分 30 秒"
                || SleepTimer.DescribeRemaining(45_000) != "还剩 45 秒"
                || SleepTimer.DescribeRemaining(120_000) != "还剩 2 分钟"
                || SleepTimer.DescribeRemaining(0) != "马上就到")
            {
                Log(6, "睡眠定时器：剩余时间的写法不对（" + SleepTimer.DescribeRemaining(90_000) + " / "
                        + SleepTimer.DescribeRemaining(45_000) + " / "
                        + SleepTimer.DescribeRemaining(120_000) + " / "
                        + SleepTimer.DescribeRemaining(0) + "）");
                return false;
            }

            var endOfTrack = SleepTimer.EndOfTrack();

            if (!endOfTrack.IsEndOfTrack || endOfTrack.IsDue(now + 10_000_000))
            {
                Log(6, "睡眠定时器：「听完这一首就停」被当成按时间到点了");
                return false;
            }

            // ---- ② 按时间到点：暂停，不切歌 ----
            var longWav = Path.Combine(AppContext.BaseDirectory, "sleep-timer.wav");
            var shortWav = Path.Combine(AppContext.BaseDirectory, "sleep-timer-next.wav");

            WriteWav(longWav, seconds: 30, frequency: 440);
            WriteWav(shortWav, seconds: 2, frequency: 660);

            try
            {
                using (var form = new 播放器.MainForm(new[] { longWav, shortWav }))
                {
                    form.Show();
                    PumpMessages(400);

                    var engine = form.Engine;

                    if (!PumpUntil(() => engine.Length > 0 && engine.IsPlaying, 10000))
                    {
                        Log(6, $"睡眠定时器：30 秒的 WAV 没播起来（时长 {engine.Length}，在播 {engine.IsPlaying}）");
                        return false;
                    }

                    var pathBefore = engine.CurrentPath;
                    var indexBefore = PlaylistOf(form).CurrentIndex;

                    form.StartSleepTimer(
                        SleepTimer.After(Environment.TickCount64, TimeSpan.FromMilliseconds(1200)),
                        "睡眠定时器：1 秒后停");

                    if (!PumpUntil(() => engine.State == PlayerState.Paused, 8000))
                    {
                        Log(6, $"睡眠定时器：到点了没有暂停（状态 {engine.State}，期望 Paused）");
                        return false;
                    }

                    if (engine.CurrentPath != pathBefore || PlaylistOf(form).CurrentIndex != indexBefore)
                    {
                        Log(6, "睡眠定时器：到点之后还切了歌（应该原地暂停）");
                        return false;
                    }

                    if (!form.StatusText.Contains("睡眠定时器"))
                    {
                        Log(6, "睡眠定时器：到点之后状态栏没说是定时器让它停的（" + form.StatusText + "）");
                        return false;
                    }

                    // 暂停之后计时器要自己退场：再等两拍，它不该又把播放拉回去
                    PumpMessages(600);

                    if (engine.State != PlayerState.Paused)
                    {
                        Log(6, $"睡眠定时器：到点之后状态又变了（{engine.State}）");
                        return false;
                    }
                }

                // ---- ③ 听完这一首就停：这一首放完不再往下播 ----
                using (var form = new 播放器.MainForm(new[] { shortWav, longWav }))
                {
                    form.Show();
                    PumpMessages(400);

                    var engine = form.Engine;
                    var item = FindSleepTimerItem(form, "menuSleepEndOfTrack");

                    if (item == null)
                    {
                        Log(6, "睡眠定时器：找不到「播放 → 睡眠定时器 → 听完这一首就停」");
                        return false;
                    }

                    if (!PumpUntil(() => engine.HasMedia && engine.IsPlaying, 10000))
                    {
                        Log(6, "睡眠定时器：2 秒的 WAV 没播起来");
                        return false;
                    }

                    item.PerformClick();

                    if (!form.SleepTimerActive || !form.SleepTimerIsEndOfTrack)
                    {
                        Log(6, "睡眠定时器：点了「听完这一首就停」却没记下来");
                        return false;
                    }

                    if (!engine.IsPlaying)
                    {
                        Log(6, "睡眠定时器：设了「听完这一首就停」之后当场就不播了（应该等这一首放完）");
                        return false;
                    }

                    if (!PumpUntil(() => engine.State is PlayerState.Ended or PlayerState.Stopped, 12000))
                    {
                        Log(6, $"睡眠定时器：这一首没等到结束（状态 {engine.State}）");
                        return false;
                    }

                    // 关键：列表里明明还有一首，但"听完这一首就停"不许往下播
                    PumpMessages(1500);

                    if (engine.IsPlaying)
                    {
                        Log(6, "睡眠定时器：这一首放完之后又往下播了（当前在播 "
                                + Path.GetFileName(engine.CurrentPath ?? string.Empty) + "）");
                        return false;
                    }

                    if (string.Equals(engine.CurrentPath, longWav, StringComparison.OrdinalIgnoreCase))
                    {
                        Log(6, "睡眠定时器：这一首放完之后切到了下一首");
                        return false;
                    }

                    if (!form.StatusText.Contains("睡眠定时器"))
                    {
                        Log(6, "睡眠定时器：放完之后状态栏没说是定时器让它停的（" + form.StatusText + "）");
                        return false;
                    }
                }
            }
            finally
            {
                TryDelete(longWav);
                TryDelete(shortWav);
            }

            Log(6, "睡眠定时器正常：纯逻辑（到点 / 剩余时间写法 / 「听完这一首」不看到点时间）都对；"
                    + "按时间到点会**原地暂停**（不切歌，状态栏说明是它停的），"
                    + "「听完这一首就停」在这一首放完之后**不再往下播**");
            return true;
        }
    }
}
