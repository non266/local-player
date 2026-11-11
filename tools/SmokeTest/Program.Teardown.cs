// 收尾压测探针（1.18.0 排 0xCFFFFFFF 用）：只在 播放器_TEST_TEARDOWN=N 时跑，跑完才判定。
//
// 复现的是第 11 项检查里出问题的那条路：先放"只有音轨的 mkv"进音频卡片，再把只有视频轨的
// mkv 塞进同一个窗体（窗口形态要从卡片切回三栏），然后立刻 停引擎 → 泵 250ms → Close → Dispose。
// 全量冒烟里这一条偶发把进程打挂（Windows 事件日志记的是 Application Hang，日志停在
// "按真实轨道判形态正常"那一行），单跑又正常——所以做成"同一进程里连做 N 次"的压测。

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool RunTeardownStress()
        {
            var raw = Environment.GetEnvironmentVariable("播放器_TEST_TEARDOWN");

            if (raw is not ("1" or "true") && !(int.TryParse(raw, out var parsed) && parsed > 0))
            {
                Log(11, "收尾压测：没设 播放器_TEST_TEARDOWN，跳过（设成轮数，例如 20）");
                return true;
            }

            var rounds = 20;
            if (int.TryParse(raw, out var value) && value > 0) rounds = value;

            if (!TryCreateAudioOnlyMkv(out var audioOnly, out var audioReason))
            {
                Log(11, $"收尾压测：跳过——{audioReason}");
                return true;
            }

            if (!TryCreateTinyVideo(out var video, out var videoReason))
            {
                Log(11, $"收尾压测：跳过——{videoReason}");
                TryDelete(audioOnly);
                return true;
            }

            try
            {
                for (var round = 1; round <= rounds; round++)
                {
                    // 每一轮都新建窗体：真实关窗路径（OnFormClosed → ShutdownForm → 引擎释放）
                    播放器.MainForm form = null!;

                    try
                    {
                        form = new 播放器.MainForm(Array.Empty<string>());
                        form.Show();
                        PumpMessages(150);

                        form.Engine.Open(audioOnly);
                        form.Engine.Play();
                        PumpUntil(() => form.Engine.GetTracks().Count > 0, 15000);
                        PumpUntil(() => form.IsAudioWindow, 8000);

                        form.Engine.Open(video);
                        form.Engine.Play();
                        PumpUntil(() => form.Engine.GetTracks().Count > 0, 15000);
                        PumpUntil(() => !form.IsAudioWindow, 8000);
                    }
                    finally
                    {
                        if (form != null)
                        {
                            // 和冒烟里那一模一样的收尾顺序
                            form.Engine.Stop();
                            PumpMessages(250);
                            form.Close();
                            form.Dispose();
                        }
                    }

                    Journal($"收尾压测 {round}/{rounds} 完成");
                    if (round % 5 == 0) Log(11, $"收尾压测：已做 {round}/{rounds} 轮，进程还活着");
                }

                Log(11, $"收尾压测完成：{rounds} 轮都活着");
                return true;
            }
            finally
            {
                TryDelete(audioOnly);
                TryDelete(video);
            }
        }
    }
}
