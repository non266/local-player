
// 音量均衡与「下一首播放」队列。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// A-2：音量均衡（normalize）。
        /// <para>
        /// 两段：<b>纯函数</b>（选项串里该不该有 <c>:audio-filter=normvol</c>）→
        /// <b>真切换</b>（用「播放 → 音量均衡（normalize）」那一项真的点一下：
        /// 路径不变、媒体重载过、位置回到开头、状态栏把"从头开始重播"说出来）。
        /// </para>
        /// <para>
        /// <b>响度效果不做数字断言</b>：进程里量不到输出电平（要量化得给引擎加一条只在测试里用的
        /// PCM 回调，那是另一档工作量）。这里验的是"滤镜确实挂进了媒体选项"，
        /// 效果本身靠耳朵——README 里也是这么写的。
        /// </para>
        /// <para>
        /// "重载过"的判据用<b>采样到的最小位置</b>：不重载的话时间只会往前走（采样最小值仍在 4 秒附近），
        /// 重载之后必然经过 0 秒附近。
        /// </para>
        /// </summary>
        private static bool CheckNormalizeVolume()
        {
            // ---- ① 纯函数：选项串 ----
            var off = MediaOptions.For(isStream: false, normalizeVolume: false);
            var on = MediaOptions.For(isStream: false, normalizeVolume: true);

            if (off.Any(option => option.Contains(MediaOptions.NormalizeAudioFilter, StringComparison.Ordinal)))
            {
                Log(6, "音量均衡：关着的时候选项串里却有 normvol（" + string.Join(" ", off) + "）");
                return false;
            }

            if (!on.Any(option => option == ":audio-filter=" + MediaOptions.NormalizeAudioFilter))
            {
                Log(6, "音量均衡：开着的时候选项串里没有 :audio-filter=normvol（" + string.Join(" ", on) + "）");
                return false;
            }

            if (!on.Any(option => option.StartsWith(":file-caching=", StringComparison.Ordinal)))
            {
                Log(6, "音量均衡：开着的时候把本地文件的缓存选项弄丢了（" + string.Join(" ", on) + "）");
                return false;
            }

            var stream = MediaOptions.For(isStream: true, normalizeVolume: false);

            if (!stream.Any(option => option.StartsWith(":network-caching=", StringComparison.Ordinal)))
            {
                Log(6, "音量均衡：网络流没走网络缓存（" + string.Join(" ", stream) + "）");
                return false;
            }

            var wav = Path.Combine(AppContext.BaseDirectory, "normalize.wav");
            WriteWav(wav, seconds: 30, frequency: 440);

            try
            {
                using var form = new 播放器.MainForm(new[] { wav });
                form.Show();
                PumpMessages(400);

                var engine = form.Engine;
                var menu = form.MenuNormalizeVolume;
                var play = FindTopMenuItem(form, "播放");

                if (menu == null || play == null || !play.DropDownItems.Contains(menu))
                {
                    Log(6, "音量均衡：找不到「播放 → 音量均衡（normalize）」这一项");
                    return false;
                }

                if (!PumpUntil(() => engine.Length > 0 && engine.IsPlaying, 10000))
                {
                    Log(6, $"音量均衡：30 秒的 WAV 没播起来（时长 {engine.Length}，在播 {engine.IsPlaying}）");
                    return false;
                }

                if (!PumpUntil(() => engine.Time >= 3900, 8000))
                {
                    Log(6, $"音量均衡：播放没有推进到 4 秒（现在 {engine.Time} ms）");
                    return false;
                }

                var path = engine.CurrentPath;
                var before = engine.Time;

                // ---- ② 真的点一下（开）----
                menu.PerformClick();

                var statusAfterOn = form.StatusText;

                if (!form.Settings.NormalizeVolume || !engine.NormalizeVolume)
                {
                    Log(6, $"音量均衡：点开了开关，设置 / 引擎却没跟上（设置 {form.Settings.NormalizeVolume}，"
                            + $"引擎 {engine.NormalizeVolume}）");
                    return false;
                }

                if (!statusAfterOn.Contains("从头开始重播"))
                {
                    Log(6, "音量均衡：切换之后状态栏没说明「从头开始重播以应用」（" + statusAfterOn + "）");
                    return false;
                }

                if (!PumpUntil(() => engine.CurrentPath != null && engine.CurrentPath == path, 5000))
                {
                    Log(6, $"音量均衡：切换之后播放的换成了别的文件（{path} → {engine.CurrentPath}）");
                    return false;
                }

                // 判据是"重载过的话位置会掉回开头附近"：**等到就停**，不用固定采 20 次 ×100 ms
                // （采到的"最小位置"一旦满足判据，再采也不可能变差——判据强度不变；
                // 固定采样是第 6 步最费时的几处之一，见"每步耗时"时间线）。
                if (!PumpUntil(() => engine.Time <= before - 2000, 3000))
                {
                    Log(6, $"音量均衡：开了均衡之后没有重载媒体（切换前 {before} ms，"
                            + $"等了 3 秒还停在 {engine.Time} ms；重载过的话会经过 0 秒附近）");
                    return false;
                }

                // ---- ③ 再点一下（关）----
                // 先让播放重新走到 2 秒以上：这一段是真播放，和"开"那一侧的 3.9 秒同性质——
                // 位置本来就在开头附近时，"有没有重载回开头"根本无从判断。
                if (!PumpUntil(() => engine.Time >= 2000, 8000))
                {
                    Log(6, $"音量均衡：点关之前播放没有推进到 2 秒（现在 {engine.Time} ms）");
                    return false;
                }

                var beforeOff = engine.Time;

                menu.PerformClick();

                var statusAfterOff = form.StatusText;

                if (form.Settings.NormalizeVolume || engine.NormalizeVolume)
                {
                    Log(6, $"音量均衡：点关了开关，设置 / 引擎还开着（设置 {form.Settings.NormalizeVolume}，"
                            + $"引擎 {engine.NormalizeVolume}）");
                    return false;
                }

                if (!statusAfterOff.Contains("从头开始重播"))
                {
                    Log(6, "音量均衡：关掉之后状态栏没说明「从头开始重播以应用」（" + statusAfterOff + "）");
                    return false;
                }

                // ⚠ 这一段原来是**永远不可能红**的：`minAfterOff` 一开始就等于 `beforeOff`，
                // 而判据是 `minAfterOff > beforeOff + 100`——最小值只会更小，所以它永远成立，
                // 白等 2 秒（2026-10-15 看"每步耗时"时间线时发现的：一条死断言）。
                // 现在改成和"开"那一侧同性质的真判据：等到位置真的掉回 1.5 秒以内。
                if (!PumpUntil(() => engine.Time <= beforeOff - 1500, 3000))
                {
                    Log(6, $"音量均衡：关掉均衡之后没有重载媒体（切换前 {beforeOff} ms，"
                            + $"等了 3 秒还停在 {engine.Time} ms）");
                    return false;
                }

                if (engine.CurrentPath != path)
                {
                    Log(6, $"音量均衡：关掉之后播放的不是同一首（{path} → {engine.CurrentPath}）");
                    return false;
                }

                Log(6, "音量均衡正常：选项串按开关正确拼出 :audio-filter=normvol（关着时没有、缓存选项没丢），"
                        + "真切换会重载当前这一首并从头开始（路径不变），状态栏说明「已从头开始重播以应用」");
                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>
        /// A-3：「下一首播放」队列。
        /// <para>
        /// 三段：<b>纯逻辑</b>（入队去重、条数上限、出队顺序）→ <b>两个入口</b>
        /// （列表右键「下一首播放」把选中的行排进队、列表标题前出现 <c>▶</c>；
        /// 「播放 → 清空下一首队列」把队清掉、标记跟着消失）→ <b>真的插播</b>
        /// （起播列表第 1 项、把第 3 项排进队，等<span>自然播完</span>：
        /// 引擎换到第 3 项而 <c>CurrentIndex</c> <b>没动</b>；第 3 项播完再回到列表里第 1 项的下一项）。
        /// </para>
        /// <para>
        /// 素材是 3 秒的小 WAV：这一段就是要等"自然播完"，靠真的 EndReached 触发，
        /// 不自己造事件（造事件等于把被测的那条路绕过去了）。
        /// </para>
        /// </summary>
        private static bool CheckPlayNextQueue()
        {
            // ---- ① 纯逻辑 ----
            var queue = new PlaybackQueue();

            if (queue.Enqueue("a.mp3") != QueueAddResult.Added)
            {
                Log(6, "下一首播放队列：第一次入队没成功");
                return false;
            }

            if (queue.Enqueue("a.mp3") != QueueAddResult.AlreadyQueued || queue.Count != 1)
            {
                Log(6, $"下一首播放队列：重复入队没有去重（队里 {queue.Count} 条）");
                return false;
            }

            if (queue.Enqueue("A.MP3") != QueueAddResult.AlreadyQueued)
            {
                Log(6, "下一首播放队列：同一个文件的路径大小写不同就被排了两遍（Windows 上是一份）");
                return false;
            }

            queue.Enqueue("b.mp3");

            if (queue.Dequeue() != "a.mp3" || queue.Dequeue() != "b.mp3")
            {
                Log(6, "下一首播放队列：出队顺序不是入队顺序");
                return false;
            }

            if (queue.Dequeue() != null || queue.Count != 0)
            {
                Log(6, "下一首播放队列：空队还能取出东西来");
                return false;
            }

            for (var i = 0; i < PlaybackQueue.Capacity; i++) queue.Enqueue("f" + i);

            if (queue.Count != PlaybackQueue.Capacity || queue.Enqueue("over.mp3") != QueueAddResult.Full)
            {
                Log(6, $"下一首播放队列：条数上限不对（最多 {PlaybackQueue.Capacity} 条，队里 {queue.Count} 条）");
                return false;
            }

            if (!queue.Remove("f5") || queue.Remove("never-there.mp3") || queue.Count != PlaybackQueue.Capacity - 1)
            {
                Log(6, "下一首播放队列：Remove 没有正确移掉一条（或移掉了不存在的）");
                return false;
            }

            if (queue.Enqueue("after-remove.mp3") != QueueAddResult.Added)
            {
                Log(6, "下一首播放队列：腾出位置之后还是进不去");
                return false;
            }

            queue.Clear();

            if (queue.Count != 0) { Log(6, "下一首播放队列：Clear 之后还有东西"); return false; }

            // ---- ② 两个入口 + 真的插播 ----
            var first = Path.Combine(AppContext.BaseDirectory, "queue-a.wav");
            var second = Path.Combine(AppContext.BaseDirectory, "queue-b.wav");
            var third = Path.Combine(AppContext.BaseDirectory, "queue-c.wav");

            WriteWav(first, seconds: 3, frequency: 440);
            WriteWav(second, seconds: 3, frequency: 480);
            WriteWav(third, seconds: 3, frequency: 520);

            try
            {
                using var form = new 播放器.MainForm(new[] { first, second, third });
                form.Show();
                PumpMessages(400);

                var engine = form.Engine;
                var playlist = form.Playlist;
                var list = Find(form, "listViewPlaylist") as ListView;

                if (list == null)
                {
                    Log(6, "下一首播放队列：找不到播放列表控件");
                    return false;
                }

                var queueItem = list.ContextMenuStrip == null
                    ? null
                    : FindMenuItem(list.ContextMenuStrip.Items, "下一首播放");

                if (queueItem == null)
                {
                    Log(6, "下一首播放队列：列表右键菜单里没有「下一首播放」");
                    return false;
                }

                if (form.MenuClearQueue == null || form.MenuPlayNext == null)
                {
                    Log(6, "下一首播放队列：「播放」菜单里没有「下一首播放 / 清空下一首队列」");
                    return false;
                }

                var play = FindTopMenuItem(form, "播放");

                if (play == null || !play.DropDownItems.Contains(form.MenuClearQueue) ||
                    !play.DropDownItems.Contains(form.MenuPlayNext))
                {
                    Log(6, "下一首播放队列：那两项没挂在「播放」菜单下");
                    return false;
                }

                if (!PumpUntil(() => engine.IsPlaying && playlist.CurrentIndex == 0, 10000))
                {
                    Log(6, $"下一首播放队列：列表第 1 项没播起来（当前项 {playlist.CurrentIndex}，"
                            + $"在播 {engine.IsPlaying}）");
                    return false;
                }

                // 选中第 3 项，走真正的右键菜单
                list.SelectedIndices.Clear();
                list.Items[2].Selected = true;
                queueItem.PerformClick();
                PumpMessages(120);

                if (form.Queue.Count != 1 || form.Queue.Paths[0] != third)
                {
                    Log(6, $"下一首播放队列：右键「下一首播放」没把第 3 项排进队（队里 {form.Queue.Count} 条，"
                            + $"状态栏：{form.StatusText}）");
                    return false;
                }

                if (!form.StatusText.Contains("已排入"))
                {
                    Log(6, "下一首播放队列：排进队之后状态栏没说（" + form.StatusText + "）");
                    return false;
                }

                if (!list.Items[2].SubItems[1].Text.StartsWith("▶ ", StringComparison.Ordinal))
                {
                    Log(6, "下一首播放队列：排进队的行标题前没有 ▶（「" + list.Items[2].SubItems[1].Text + "」）");
                    return false;
                }

                if (list.Items[0].SubItems[1].Text.StartsWith("▶ ", StringComparison.Ordinal))
                {
                    Log(6, "下一首播放队列：没排进队的行也被标上了 ▶");
                    return false;
                }

                // 清空：走「播放」菜单那一项
                form.MenuClearQueue.PerformClick();
                PumpMessages(120);

                if (form.Queue.Count != 0)
                {
                    Log(6, $"下一首播放队列：「清空下一首队列」没清掉（还剩 {form.Queue.Count} 条）");
                    return false;
                }

                if (list.Items[2].SubItems[1].Text.StartsWith("▶ ", StringComparison.Ordinal))
                {
                    Log(6, "下一首播放队列：清空之后 ▶ 标记还在");
                    return false;
                }

                // 再排一次，这次真的等它播完
                list.SelectedIndices.Clear();
                list.Items[2].Selected = true;
                queueItem.PerformClick();
                PumpMessages(120);

                if (!form.Queue.Contains(third))
                {
                    Log(6, "下一首播放队列：第二次排进队没成功（" + form.StatusText + "）");
                    return false;
                }

                var indexBefore = playlist.CurrentIndex;

                // 判据是"第 1 项播完的那一刻被换成了哪一份媒体"：看队列就该换成排进队的第 3 项，
                // 不看队列就换成列表里的第 2 项。不拿"几秒之内出现"当判据——列表本来就排着第 3 项，
                // 时间放宽一点它自己也会播到那儿（第一版就是这么把变异放过去的）。
                if (!PumpUntil(() => engine.CurrentPath != null &&
                                     !string.Equals(engine.CurrentPath, first, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(6, "下一首播放队列：第 1 项自然播完之后什么都没接着播");
                    return false;
                }

                var nextAfterFirst = engine.CurrentPath;

                if (!string.Equals(nextAfterFirst, third, StringComparison.OrdinalIgnoreCase))
                {
                    Log(6, $"下一首播放队列：第 1 项播完之后播的是「{Path.GetFileName(nextAfterFirst ?? string.Empty)}」，"
                            + $"而不是排进队的「{Path.GetFileName(third)}」（排完队却没被用上）");
                    return false;
                }

                if (playlist.CurrentIndex != indexBefore)
                {
                    Log(6, $"下一首播放队列：插播把列表的当前项也改了（{indexBefore} → {playlist.CurrentIndex}）");
                    return false;
                }

                if (form.Queue.Count != 0)
                {
                    Log(6, $"下一首播放队列：插播开始之后队里还有 {form.Queue.Count} 条（该出队了）");
                    return false;
                }

                if (!PumpUntil(() => engine.CurrentPath != null &&
                                     string.Equals(engine.CurrentPath, second, StringComparison.OrdinalIgnoreCase), 15000))
                {
                    Log(6, "下一首播放队列：插播的那一首播完之后没回到列表里第 1 项的下一项（现在在播 "
                            + $"{Path.GetFileName(engine.CurrentPath ?? string.Empty)}）");
                    return false;
                }

                if (playlist.CurrentIndex != indexBefore + 1)
                {
                    Log(6, $"下一首播放队列：回到列表继续时当前项不对（期望 {indexBefore + 1}，"
                            + $"实际 {playlist.CurrentIndex}）");
                    return false;
                }

                Log(6, "下一首播放队列正常：入队去重、上限 20 条、出队按入队顺序；"
                        + "右键「下一首播放」会打上 ▶、菜单能清空；自然播完时插播队首且列表当前项不动，"
                        + "插播完再回到列表的下一项");
                return true;
            }
            finally
            {
                TryDelete(first);
                TryDelete(second);
                TryDelete(third);
            }
        }
    }
}
