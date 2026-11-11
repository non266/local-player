// 绘制基准（1.18.0）：只在 播放器_TEST_PAINT=1 时跑，只量不判。
//
// 为什么要单独一个探针：反馈里说 CoverView「同一帧把文字量了两遍」、LyricsView「每 15ms 重建
// 高亮路径与画刷」。两句话都要有数字才好下结论——量的是"一帧到底多贵、改完之后便宜多少"。
// 用 DrawToBitmap 驱动（它会把 WM_PRINT 转成一次 OnPaint），比截图省事，比"看代码猜"可靠。

using System.Diagnostics;

namespace SmokeTest
{
    internal static partial class Program
    {
        private static bool RunPaintProbe()
        {
            if (Environment.GetEnvironmentVariable("播放器_TEST_PAINT") is not ("1" or "true"))
            {
                Log(11, "绘制基准：没设 播放器_TEST_PAINT，跳过（设成 1 再跑「侧栏」这一步）");
                return true;
            }

            try
            {
                // 封面：标题 / 艺术家 / 专辑三行，CoverView 会把它们排版并测量。
                using (var cover = new CoverView { Size = new Size(360, 470) })
                {
                    cover.ShowTrack(null, new MediaTags
                    {
                        Title = "一段中等长度的曲目标题",
                        Artist = "某位艺术家",
                        Album = "某张专辑的名字"
                    });

                    MeasurePaint("封面 CoverView", cover, 200);
                }

                // 歌词：30 行带轴，停在中间——当前行高亮（圆角路径 + 画刷）与右上角偏移提示都在画。
                var lrc = new StringBuilder();

                for (var i = 0; i < 30; i++)
                    lrc.AppendLine($"[00:{i:00}.00]第 {i + 1} 句歌词，长度中等，够换一行看看");

                using (var lyrics = new LyricsView { Size = new Size(420, 620) })
                {
                    lyrics.SetOffsetHint("+1.5s");
                    lyrics.SetDocument(LyricsDocument.Parse(lrc.ToString(), "probe"));
                    lyrics.UpdatePosition(TimeSpan.FromSeconds(20));

                    MeasurePaint("歌词 LyricsView", lyrics, 200);
                }

                Log(11, "绘制基准完成（只量不判）");
                return true;
            }
            catch (Exception ex)
            {
                Log(11, "绘制基准失败: " + ex);
                return false;
            }
        }

        /// <summary>画 <paramref name="iterations"/> 帧，打印平均 / 最慢一帧。</summary>
        private static void MeasurePaint(string label, Control control, int iterations)
        {
            var _ = control.Handle;   // 先建句柄：创建窗口那一次不算进第一帧

            using var bitmap = new Bitmap(control.Width, control.Height);
            var bounds = new Rectangle(0, 0, control.Width, control.Height);

            // 预热：第一帧总是带着字体 / 画刷 / 路径的初始化
            for (var i = 0; i < 5; i++) control.DrawToBitmap(bitmap, bounds);

            var total = Stopwatch.StartNew();
            long slowestTicks = 0;

            for (var i = 0; i < iterations; i++)
            {
                var one = Stopwatch.StartNew();
                control.DrawToBitmap(bitmap, bounds);
                one.Stop();

                if (one.ElapsedTicks > slowestTicks) slowestTicks = one.ElapsedTicks;
            }

            total.Stop();

            var average = total.Elapsed.TotalMilliseconds / iterations;
            var slowest = slowestTicks * 1000.0 / Stopwatch.Frequency;

            Log(11, $"绘制基准 {label}：{iterations} 帧，平均 {average:0.###} ms，最慢 {slowest:0.###} ms");
        }
    }
}
