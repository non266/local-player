// 检查点 7：时长扫描，以及"解析结果早于窗口句柄"那条回归。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 3) 时长扫描（回归测试）
        //
        // 这里挡住的是一个已经真实发生过的崩溃：Media.Parse 返回的 Task 是在
        // libvlc 自己的解析回调线程上完成的，如果在其 await 续体里立刻释放 Media，
        // libvlc 会在回调返回后访问已释放内存，抛出无法捕获的原生访问违例
        // （0xC0000005，进程直接消失，连 Application.ThreadException 都拦不住）。
        // -----------------------------------------------------------------

        private static bool TestDurationScanner()
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "scan-regression");
            const int fileCount = 25;

            try
            {
                Directory.CreateDirectory(folder);

                var files = new List<string>();
                for (var i = 0; i < fileCount; i++)
                {
                    var path = Path.Combine(folder, $"tone-{i:00}.wav");
                    WriteWav(path, seconds: 1 + i % 4, frequency: 300 + i * 15);
                    files.Add(path);
                }

                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var libvlc = new LibVLC("--quiet"))
                using (var scanner = new DurationScanner(libvlc))
                {
                    scanner.DurationFound += (s, e) =>
                    {
                        lock (found) found.Add(e.FilePath);
                    };

                    scanner.Enqueue(files);

                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline)
                    {
                        lock (found)
                        {
                            if (found.Count >= fileCount) break;
                        }

                        PumpMessages(50);
                    }
                }

                int resolved;
                lock (found) resolved = found.Count;

                if (resolved < fileCount)
                {
                    Log(7, $"时长扫描只完成了 {resolved}/{fileCount}");
                    return false;
                }

                Log(7, $"时长扫描 {resolved}/{fileCount} 全部完成，未触发原生崩溃");
                return true;
            }
            catch (Exception ex)
            {
                Log(7, "时长扫描测试失败: " + ex.Message);
                return false;
            }
            finally
            {
                TryDeleteDirectory(folder);
            }
        }
    }
}
