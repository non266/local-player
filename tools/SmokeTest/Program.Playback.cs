// 检查点 3~4：播放内核（libvlc 真的能播、能播完）。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 1) 播放内核
        // -----------------------------------------------------------------

        private static bool TestPlayback()
        {
            var wavPath = Path.Combine(AppContext.BaseDirectory, "smoke-tone.wav");
            WriteWav(wavPath, seconds: 2, frequency: 440);
            Log(2, "已生成测试音频，共 " + new FileInfo(wavPath).Length + " 字节");

            using (var libvlc = new LibVLC("--aout=dummy", "--vout=dummy", "--no-video-title-show", "--quiet"))
            using (var player = new MediaPlayer(libvlc))
            {
                var playing = new ManualResetEventSlim(false);
                var ended = new ManualResetEventSlim(false);
                var failed = new ManualResetEventSlim(false);

                player.Playing += (s, e) => playing.Set();
                player.EndReached += (s, e) => ended.Set();
                player.EncounteredError += (s, e) => failed.Set();

                Log(3, "libvlc 版本: " + libvlc.Version);

                using (var media = new Media(libvlc, wavPath, FromType.FromPath))
                {
                    if (!player.Play(media))
                    {
                        Log(3, "Play() 返回 false: " + libvlc.LastLibVLCError);
                        return false;
                    }

                    if (failed.Wait(TimeSpan.FromSeconds(5)))
                    {
                        Log(3, "播放立即报错");
                        return false;
                    }

                    if (!playing.Wait(TimeSpan.FromSeconds(15)))
                    {
                        Log(3, "15 秒内没有进入 Playing 状态");
                        return false;
                    }
                }

                Log(3, "已进入 Playing 状态，媒体长度 = " + player.Length + " ms");

                Thread.Sleep(800);
                var time = player.Time;
                Log(4, "播放 800ms 后的位置 = " + time + " ms");
                if (time <= 0)
                {
                    Log(4, "播放位置没有推进");
                    return false;
                }

                if (!ended.Wait(TimeSpan.FromSeconds(20)))
                {
                    Log(4, "未收到 EndReached 事件");
                    return false;
                }

                Log(4, "收到 EndReached，播放完整走完");
            }

            TryDelete(wavPath);
            return true;
        }
    }
}
