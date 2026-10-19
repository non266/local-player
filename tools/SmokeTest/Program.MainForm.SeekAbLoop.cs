
// 进度条拖动（B5）与 A-B 循环。

namespace SmokeTest
{
    internal static partial class Program
    {
        /// <summary>
        /// B5：播放中拖动进度条——拖动期间"当前时间"必须显示**要跳到的位置**，
        /// 滑块取值不能被界面计时器抢回去，引擎也不能被中途 seek；松手才真正跳。
        /// <para>
        /// 以前 <c>UpdateProgress()</c> 里的 <c>if (!_userSeeking)</c> 只护住了滑块取值，
        /// <b>时间标签在护罩外面</b>：计时器每 200 ms 用真实播放时间把它写回去，
        /// 而拖动时 <c>OnSeekScroll</c> 又把它写成拖动目标——两个写者互相盖，
        /// 用户看到的就是"拖动时反复跳回真实进度"。顺带两条同源毛病：
        /// 按住拖到控件外面不跟手（<c>OnMouseLeave</c> 在捕获期间清了拖动状态）、
        /// 右键点一下也会 seek（<c>_userSeeking</c> 不分按键）。
        /// </para>
        /// <para>
        /// 时序全靠 <c>SendMessage</c> 发鼠标消息（不走合成真鼠标），所以不受"有没有人在动鼠标"影响；
        /// 断言也不拿"播放位置刚好是多少"当基准，只比拖动前后。
        /// </para>
        /// </summary>
        private static bool CheckSeekBarDragging()
        {
            var wav = Path.Combine(AppContext.BaseDirectory, "seek-drag.wav");

            // 60 秒：拖到中间之后还要留出"按住右键 1.5 秒"的余量，不然片子会在第四段之前播完
            // （播完之后引擎时间回到 0，会被误读成"右键把位置拽回去了"）。
            WriteWav(wav, seconds: 60, frequency: 440);

            try
            {
                using var form = new 播放器.MainForm(new[] { wav });
                form.Show();
                PumpMessages(400);

                var slider = Find(form, "trackSeek") as FlatSlider;
                var label = Find(form, "lblCurrentTime");
                var engine = form.Engine;

                if (slider == null || label == null)
                {
                    Log(6, "进度条拖动检查：找不到进度条或时间标签");
                    return false;
                }

                // 等它真的在播、且时长已经知道（拖动位置是按总时长换算的）
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && (engine.Length <= 0 || !engine.IsPlaying)) PumpMessages(100);

                if (engine.Length <= 0 || !engine.IsPlaying)
                {
                    Log(6, $"进度条拖动检查：60 秒的 WAV 没播起来（时长 {engine.Length}，在播 {engine.IsPlaying}）");
                    return false;
                }

                // 探针：只记现场、不改判据（两次偶发红都是"时间突然回到 0"，见 EngineProbe 的注释）
                using var probe = new EngineProbe(engine, form.Playlist);

                bool Fail(string message)
                {
                    Journal("诊断：" + probe.Dump());
                    Log(6, message);
                    return false;
                }

                var mid = slider.Width / 2;
                var y = slider.Height / 2;

                // ---- ① 按住拖到中间，然后**保持不动** ----
                SendMouseTo(slider, WmLButtonDown, 10, y, MkLButton);
                SendMouseTo(slider, WmMouseMove, mid, y, MkLButton);
                PumpMessages(120);

                var draggedValue = slider.Value;
                var draggedLabel = label.Text;
                var expectedLabel = TimeFormatter.FormatWithHours(
                    TimeSpan.FromMilliseconds(engine.Length * draggedValue / 1000.0));

                if (Math.Abs(draggedValue - 500) > 20)
                {
                    return Fail($"进度条拖动检查：拖到中间之后取值不对（{draggedValue}/1000）");
                }

                if (draggedLabel != expectedLabel)
                {
                    return Fail("进度条拖动检查：拖动时时间标签没有显示要跳到的位置"
                            + $"（「{draggedLabel}」，期望「{expectedLabel}」）");
                }

                var timeBeforeHold = engine.Time;

                PumpMessages(600);          // 够 3 个界面计时器周期

                if (label.Text != draggedLabel)
                {
                    return Fail("进度条拖动检查：按住不动时时间标签被播放时间盖回去了"
                            + $"（「{draggedLabel}」→「{label.Text}」）");
                }

                if (slider.Value != draggedValue)
                {
                    return Fail($"进度条拖动检查：按住不动时滑块取值被抢回去了（{draggedValue} → {slider.Value}）");
                }

                var timeAfterHold = engine.Time;

                if (timeAfterHold < timeBeforeHold + 300 || timeAfterHold > timeBeforeHold + 3000)
                {
                    return Fail("进度条拖动检查：按住不动这 600 ms 里播放位置不对（既没照常往前走、也没跳走才怪）"
                            + $"（{timeBeforeHold} → {timeAfterHold} ms）");
                }

                // ---- ② 按住拖到进度条外面（下方）：仍然要跟手 ----
                var beforeOutside = slider.Value;
                var outsideX = Math.Min(slider.Width - 4, mid + 20);   // 只往右挪一点点：松手之后离片尾还远

                SendMouseTo(slider, WmMouseMove, outsideX, slider.Height + 40, MkLButton);
                SendMouseTo(slider, WmMouseLeave, outsideX, slider.Height + 40);   // 系统在光标离开控件时会发的消息
                PumpMessages(80);

                var afterLeave = slider.Value;

                SendMouseTo(slider, WmMouseMove, Math.Min(slider.Width - 4, outsideX + 40), slider.Height + 40, MkLButton);
                PumpMessages(80);

                if (slider.Value <= beforeOutside || slider.Value <= afterLeave)
                {
                    return Fail("进度条拖动检查：按住拖到进度条外面之后不跟手了"
                            + $"（{beforeOutside} → 离开后 {afterLeave} → 再移动 {slider.Value}）");
                }

                // ---- ③ 松手：这时候才真的跳 ----
                var releaseValue = slider.Value;
                SendMouseTo(slider, WmLButtonUp, Math.Min(slider.Width - 4, outsideX + 40), slider.Height + 40);
                PumpMessages(500);

                var expectedSeek = (long)(engine.Length * releaseValue / 1000.0);
                var seekedTo = engine.Time;

                if (Math.Abs(seekedTo - expectedSeek) > 800)
                {
                    return Fail($"进度条拖动检查：松手之后没有跳到目标（在 {seekedTo} ms，期望约 {expectedSeek} ms）");
                }

                // ---- ④ 右键点一下：不许 seek，也不许让界面计时器停摆 ----
                PumpMessages(300);

                var labelsWhileHoldingRight = new List<string>();
                SendMouseTo(slider, WmRButtonDown, slider.Width / 5, y);

                for (var i = 0; i < 15; i++)     // 按住 1.5 秒：这期间显示的时间必然要走过至少一秒
                {
                    PumpMessages(100);

                    if (!labelsWhileHoldingRight.Contains(label.Text)) labelsWhileHoldingRight.Add(label.Text);
                }

                SendMouseTo(slider, WmRButtonUp, slider.Width / 5, y);
                PumpMessages(200);

                if (labelsWhileHoldingRight.Count < 2)
                {
                    return Fail("进度条拖动检查：按住右键时界面计时器停摆了"
                            + $"（1.5 秒里时间标签一直是「{labelsWhileHoldingRight.FirstOrDefault()}」）");
                }

                if (engine.Time < seekedTo - 300)
                {
                    return Fail($"进度条拖动检查：右键点进度条把播放位置拽回去了（{seekedTo} → {engine.Time} ms）");
                }

                Log(6, "进度条拖动正常：拖动期间标签显示要跳到的位置、滑块不被抢、引擎不中途 seek，"
                        + "拖出控件仍跟手，松手才跳，右键点一下既不 seek 也不让计时器停摆");
                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>
        /// A-1：A-B 循环。
        /// <para>
        /// 三段：<b>纯逻辑</b>（越界判断的边界——引擎时间<b>正好等于 B</b> 那一拍就该回跳；
        /// 以及 A/B 互比时的拒绝规则）→ <b>真的回跳</b>（起播一段 30 秒的 WAV，
        /// 用「播放 → A-B 循环」里真实的菜单项设点，泵 3 秒看时间是不是一直被压在 B 点以内、
        /// 并且确实跳回过 A 点）→ <b>拒绝 / 清除 / 停止</b>（A 不早于 B、B 不晚于 A 都要如实拒绝
        /// 并写出原因；清除与停止之后不再回跳）。
        /// </para>
        /// <para>
        /// 边界那一条只能写在纯函数上：<c>Time</c> 是浮动的，靠"泵到某一刻再看它落在哪"
        /// 永远碰不到"正好等于 B"，判据里的 <c>&gt;=</c> 被改成 <c>&gt;</c> 也照样能过。
        /// </para>
        /// <para>
        /// 素材用 30 秒而不是计划里写的 6 秒：这一段总共要泵十几秒，6 秒的片子会在中途播完，
        /// 而播完之后引擎时间会回到 0（B5 那一步踩过这个坑），看上去就像"还在回跳"。
        /// </para>
        /// </summary>
        private static bool CheckAbLoop()
        {
            // ---- ① 纯逻辑：越界判断的边界与拒绝规则 ----
            if (!AbLoop.ShouldWrap(2000, 1000, 2000))
            {
                Log(6, "A-B 循环：引擎时间正好等于 B 点时没有回跳（边界判据写成了 >）");
                return false;
            }

            if (AbLoop.ShouldWrap(1999, 1000, 2000))
            {
                Log(6, "A-B 循环：还没到 B 点就回跳了");
                return false;
            }

            if (AbLoop.ShouldWrap(2000, null, 2000) || AbLoop.ShouldWrap(2000, 1000, null))
            {
                Log(6, "A-B 循环：只设了一个点也回跳了");
                return false;
            }

            if (AbLoop.RejectStart(2000, 2000) == null || AbLoop.RejectStart(2500, 1000) == null)
            {
                Log(6, "A-B 循环：A 点不早于 B 点时没有拒绝");
                return false;
            }

            if (AbLoop.RejectStart(500, 1000) != null)
            {
                Log(6, "A-B 循环：A 点明明早于 B 点却被拒绝了");
                return false;
            }

            if (AbLoop.RejectEnd(1000, 1000) == null || AbLoop.RejectEnd(500, 1000) == null)
            {
                Log(6, "A-B 循环：B 点不晚于 A 点时没有拒绝");
                return false;
            }

            if (AbLoop.RejectEnd(2000, 1000) != null)
            {
                Log(6, "A-B 循环：B 点明明晚于 A 点却被拒绝了");
                return false;
            }

            var wav = Path.Combine(AppContext.BaseDirectory, "ab-loop.wav");
            WriteWav(wav, seconds: 30, frequency: 440);

            try
            {
                using var form = new 播放器.MainForm(new[] { wav });
                form.Show();
                PumpMessages(400);

                var engine = form.Engine;
                var setStart = FindAbLoopItem(form, "menuAbSetStart");
                var setEnd = FindAbLoopItem(form, "menuAbSetEnd");
                var clear = FindAbLoopItem(form, "menuAbClear");
                var play = FindTopMenuItem(form, "播放");
                var stop = play == null ? null : FindMenuItem(play.DropDownItems, "停止");

                if (setStart == null || setEnd == null || clear == null || stop == null)
                {
                    Log(6, "A-B 循环：找不到「播放 → A-B 循环」里的菜单项（或「停止」）");
                    return false;
                }

                if (!PumpUntil(() => engine.Length > 0 && engine.IsPlaying, 10000))
                {
                    Log(6, $"A-B 循环：30 秒的 WAV 没播起来（时长 {engine.Length}，在播 {engine.IsPlaying}）");
                    return false;
                }

                // ---- ② 拒绝：B 点设在 A 点之前 ----
                engine.SeekTo(3000);
                if (!PumpUntil(() => engine.Time >= 2900, 4000))
                {
                    Log(6, $"A-B 循环：定位到 3 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setStart.PerformClick();
                var a = form.AbLoopStartMilliseconds;

                if (a == null || a < 2900)
                {
                    Log(6, $"A-B 循环：「把 A 点设在当前时间」没设上（A = {(a.HasValue ? a.Value + " ms" : "null")}，"
                            + $"状态栏：{form.StatusText}）");
                    return false;
                }

                if (!form.StatusText.Contains("B 点还没设"))
                {
                    Log(6, "A-B 循环：只设了 A 点，状态栏却没说明 B 点还没设（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(1000);
                if (!PumpUntil(() => engine.Time < a.Value - 500, 4000))
                {
                    Log(6, $"A-B 循环：定位回 1 秒没成功（现在 {engine.Time} ms，A 点是 {a.Value} ms）");
                    return false;
                }

                setEnd.PerformClick();

                if (form.AbLoopEndMilliseconds != null)
                {
                    Log(6, $"A-B 循环：B 点早于 A 点却被设上了（B = {form.AbLoopEndMilliseconds} ms，A = {a.Value} ms）");
                    return false;
                }

                if (!form.StatusText.Contains("不晚于"))
                {
                    Log(6, "A-B 循环：B 点被拒绝了，但状态栏没说清原因（" + form.StatusText + "）");
                    return false;
                }

                // ---- ③ 拒绝：A 点设在 B 点之后 ----
                clear.PerformClick();

                if (form.AbLoopStartMilliseconds != null || form.AbLoopEndMilliseconds != null)
                {
                    Log(6, "A-B 循环：点了「清除 A-B」，点还在");
                    return false;
                }

                engine.SeekTo(1000);
                if (!PumpUntil(() => engine.Time >= 900 && engine.Time < 2500, 4000))
                {
                    Log(6, $"A-B 循环：定位回 1 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setEnd.PerformClick();
                var b = form.AbLoopEndMilliseconds;

                if (b == null)
                {
                    Log(6, "A-B 循环：先设 B 点没设上（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(3000);
                if (!PumpUntil(() => engine.Time >= 2900, 4000))
                {
                    Log(6, $"A-B 循环：定位到 3 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                if (engine.Time < b.Value + 500)
                {
                    Log(6, $"A-B 循环：只有 B 点没有 A 点的时候回跳了（B = {b.Value} ms，现在 {engine.Time} ms）");
                    return false;
                }

                setStart.PerformClick();

                if (form.AbLoopStartMilliseconds != null || form.AbLoopActive)
                {
                    Log(6, $"A-B 循环：A 点晚于 B 点却被设上了（A = {form.AbLoopStartMilliseconds} ms，B = {b.Value} ms）");
                    return false;
                }

                if (!form.StatusText.Contains("不早于"))
                {
                    Log(6, "A-B 循环：A 点被拒绝了，但状态栏没说清原因（" + form.StatusText + "）");
                    return false;
                }

                // ---- ④ 真的回跳 ----
                clear.PerformClick();

                engine.SeekTo(1000);
                if (!PumpUntil(() => engine.Time >= 900 && engine.Time < 2500, 4000))
                {
                    Log(6, $"A-B 循环：定位回 1 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setStart.PerformClick();
                var loopStart = form.AbLoopStartMilliseconds;

                if (loopStart == null)
                {
                    Log(6, "A-B 循环：A 点没设上（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(loopStart.Value + 1000);
                if (!PumpUntil(() => engine.Time >= loopStart.Value + 900, 4000))
                {
                    Log(6, $"A-B 循环：定位到 A 点后面 1 秒没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setEnd.PerformClick();
                var loopEnd = form.AbLoopEndMilliseconds;

                if (loopEnd == null || !form.AbLoopActive)
                {
                    Log(6, $"A-B 循环：B 点没设上（{form.StatusText}）");
                    return false;
                }

                if (loopEnd.Value - loopStart.Value < 500)
                {
                    Log(6, $"A-B 循环：A/B 两点离得太近（A {loopStart.Value} → B {loopEnd.Value} ms），这段验不出回跳");
                    return false;
                }

                if (!form.StatusText.Contains("跳回 A"))
                {
                    Log(6, "A-B 循环：两个点都设好了，状态栏却没说明「播放到 B 就跳回 A」（" + form.StatusText + "）");
                    return false;
                }

                // 窗口只有约 1 秒：泵 3 秒，不循环的话时间会一路走到 B + 2000 ms 以外。
                var samples = new List<long>();
                var wrappedBack = false;
                var overshoot = 0L;

                for (var i = 0; i < 30; i++)
                {
                    PumpMessages(100);

                    var now = engine.Time;
                    samples.Add(now);

                    if (now - loopEnd.Value > overshoot) overshoot = now - loopEnd.Value;
                    if (samples.Count >= 2 && samples[^2] - now >= 300) wrappedBack = true;
                }

                if (overshoot > 900)
                {
                    Log(6, $"A-B 循环：播放越过了 B 点却没有回跳（最多越到 B + {overshoot} ms，"
                            + $"A {loopStart.Value} → B {loopEnd.Value} ms）");
                    return false;
                }

                if (!wrappedBack)
                {
                    Log(6, $"A-B 循环：3 秒里没看到跳回 A 点（A {loopStart.Value} → B {loopEnd.Value} ms，"
                            + $"采样最少 {samples.Min()}、最多 {samples.Max()} ms）");
                    return false;
                }

                // ---- ⑤ 清除之后不再回跳 ----
                clear.PerformClick();

                if (form.AbLoopActive || form.AbLoopStartMilliseconds != null || form.AbLoopEndMilliseconds != null)
                {
                    Log(6, "A-B 循环：点了「清除 A-B」，点还在");
                    return false;
                }

                var clearedAt = engine.Time;

                if (!PumpUntil(() => engine.Time > loopEnd.Value + 800, 6000))
                {
                    Log(6, $"A-B 循环：清除之后还在回跳（清除时 {clearedAt} ms，现在 {engine.Time} ms，"
                            + $"B 点是 {loopEnd.Value} ms）");
                    return false;
                }

                // ---- ⑥ 停止时清除 ----
                setStart.PerformClick();
                var stopStart = form.AbLoopStartMilliseconds;

                if (stopStart == null)
                {
                    Log(6, "A-B 循环：准备验「停止时清除」时 A 点没设上（" + form.StatusText + "）");
                    return false;
                }

                engine.SeekTo(stopStart.Value + 1000);

                if (!PumpUntil(() => engine.Time >= stopStart.Value + 900, 4000))
                {
                    Log(6, $"A-B 循环：准备验「停止时清除」时定位没成功（现在 {engine.Time} ms）");
                    return false;
                }

                setEnd.PerformClick();

                if (!form.AbLoopActive)
                {
                    Log(6, "A-B 循环：准备验「停止时清除」时两个点没设上（" + form.StatusText + "）");
                    return false;
                }

                stop.PerformClick();

                if (form.AbLoopActive || form.AbLoopStartMilliseconds != null || form.AbLoopEndMilliseconds != null)
                {
                    Log(6, "A-B 循环：停止播放之后循环点还在");
                    return false;
                }

                Log(6, "A-B 循环正常：越过 B 点（含正好到 B 那一拍）跳回 A，"
                        + "A 不早于 B / B 不晚于 A 都如实拒绝并写出原因，清除与停止之后不再回跳");
                return true;
            }
            finally
            {
                TryDelete(wav);
            }
        }

        /// <summary>A-B 循环子菜单里按 <c>Name</c> 找一项：文字里带着"当前时间"，不能按文字找。</summary>
        private static ToolStripMenuItem? FindAbLoopItem(播放器.MainForm form, string name)
        {
            var menu = form.MenuAbLoop;
            if (menu == null) return null;

            foreach (ToolStripItem item in menu.DropDownItems)
            {
                if (item is ToolStripMenuItem menuItem && menuItem.Name == name)
                    return menuItem;
            }

            return null;
        }
    }
}
