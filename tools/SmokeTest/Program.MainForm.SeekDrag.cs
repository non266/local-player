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

    }
}
