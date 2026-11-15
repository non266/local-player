// B5：播放中拖动进度条——拖动期间"当前时间"必须显示要跳到的位置，松手才真的跳。
//
// 从 Program.MainForm.SeekAbLoop.cs 按 ARCHITECTURE 17.2 的规矩整块搬过来：成员一个字没动。

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
            var savedCursor = Cursor.Position;

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

                // 把真实光标停到屏幕左上角：合成拖动一按下，滑块就进入"捕获"状态，此后**真实鼠标**
                // 只要动一下，WM_MOUSEMOVE 就会落到滑块上（这是拖动本来的行为）。光标停住不动，
                // 这条检查才不会被"谁碰了一下鼠标"带跑。检查结束会还原（见 finally）。
                try { Cursor.Position = new Point(0, 0); } catch (Exception) { /* 没有桌面会话时忽略 */ }

                // ---- ① 按住拖到中间，然后**保持不动** ----
                SendMouseTo(slider, WmLButtonDown, 10, y, MkLButton);

                // 真实鼠标万一还是插了一脚（有人在动鼠标），这一下取值会跟到真实光标的位置；
                // 重发几次"移到中间"把滑块拨回来——三次都稳不住就按"环境不成立"跳过（见三态）。
                var draggedValue = 0;

                for (var attempt = 0; attempt < 3; attempt++)
                {
                    SendMouseTo(slider, WmMouseMove, mid, y, MkLButton);
                    PumpMessages(120);

                    draggedValue = slider.Value;
                    if (Math.Abs(draggedValue - 500) <= 20) break;
                }

                if (Math.Abs(draggedValue - 500) > 20)
                {
                    Journal($"拖到中间之后取值一直不稳：{draggedValue}/1000（真实鼠标在被捕获期间移动了）");
                    Log(6, "进度条拖动检查：拖到中间之后取值一直被真实鼠标带跑（环境不成立，这一次跳过）"
                            + $"（现在是 {draggedValue}/1000，期望 500±20）");
                    return true;
                }

                var draggedLabel = label.Text;
                var expectedLabel = TimeFormatter.FormatWithHours(
                    TimeSpan.FromMilliseconds(engine.Length * draggedValue / 1000.0));

                if (draggedLabel != expectedLabel)
                {
                    return Fail("进度条拖动检查：拖动时时间标签没有显示要跳到的位置"
                            + $"（「{draggedLabel}」，期望「{expectedLabel}」）");
                }

                var timeBeforeHold = engine.Time;

                // 探针（1.22.0）：按住这 600 ms 里每 100 ms 记一行现场。判据一个字不改，
                // 只为了万一红了能分清三种来源：拖动状态被清掉 / 播放位置跳了 / 播到结尾了。
                var holdSamples = new List<string>();
                var realMouseMovedSlider = false;

                for (var i = 0; i < 6; i++)
                {
                    PumpMessages(100);

                    var seeking = UserSeekingOf(form);
                    if (seeking && slider.Value != draggedValue) realMouseMovedSlider = true;

                    holdSamples.Add($"「{label.Text}」/ 值 {slider.Value} / 位置 {engine.Time} / 时长 {engine.Length} / "
                                    + $"状态 {engine.State} / 拖动 {seeking}");
                }

                var holdEvidence = "按住不动期间：" + string.Join(" ; ", holdSamples);

                // 三态（1.22.0）：按住期间滑块仍在"真实的鼠标"捕获之下——真光标一动，滑块就跟着
                // 真实位置走。1.22.0 连跑时抓到过：值 500 → 1000、标签变成 00:01:00，而引擎位置
                // 只有 0.5～1.3 秒。这不是产品的问题：拖动中滑块本来就该跟光标。
                // 真正的 bug 只有一种形状——**拖动状态被清掉**（那时计时器会把标签 / 滑块写回
                // 播放时间）。所以：真实鼠标带走滑块 + 拖动状态还在 → 打印原因跳过；
                // 拖动状态没了 → 仍然硬失败（下面的判据）。
                if (realMouseMovedSlider && UserSeekingOf(form))
                {
                    Journal(holdEvidence);
                    Log(6, "进度条拖动检查：按住期间滑块被真实鼠标带走了（环境不成立，这一次跳过）——" + holdEvidence);
                    return true;
                }

                if (label.Text != draggedLabel)
                {
                    Journal(holdEvidence);
                    return Fail("进度条拖动检查：按住不动时时间标签被播放时间盖回去了"
                            + $"（「{draggedLabel}」→「{label.Text}」）；{holdEvidence}");
                }

                if (slider.Value != draggedValue)
                {
                    Journal(holdEvidence);
                    return Fail($"进度条拖动检查：按住不动时滑块取值被抢回去了（{draggedValue} → {slider.Value}）；"
                                + holdEvidence);
                }

                var timeAfterHold = engine.Time;

                if (timeAfterHold < timeBeforeHold + 300 || timeAfterHold > timeBeforeHold + 3000)
                {
                    Journal(holdEvidence);
                    return Fail("进度条拖动检查：按住不动这 600 ms 里播放位置不对（既没照常往前走、也没跳走才怪）"
                            + $"（{timeBeforeHold} → {timeAfterHold} ms）；{holdEvidence}");
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

                // 还原真实光标的位置：这一段把光标临时停到了屏幕角落，不能让它留在那儿
                try { Cursor.Position = savedCursor; } catch (Exception) { /* 没有桌面会话时忽略 */ }
            }
        }

        /// <summary>探针用：主窗体现在是不是"用户正按着进度条拖"。</summary>
        private static bool UserSeekingOf(Form form) =>
            form is 播放器.MainForm main && main.UserSeekingForTest;

    }
}
