// 测试骨架：日志打印、消息泵、反射取值与调用、临时文件清理、等待工具。

namespace SmokeTest
{
    internal static partial class Program
    {
        // -----------------------------------------------------------------
        // 辅助
        // -----------------------------------------------------------------

        private static void Log(int step, string message) =>
            Console.WriteLine($"[{step}/{StepCount}] {message}");

        private static Control Find(Control root, string name) =>
            root.Controls.Find(name, searchAllChildren: true).FirstOrDefault();

        /// <summary>
        /// 主窗体里"当前这一首"的会话（路径 / 标签 / 歌词 / 歌词偏移）。
        /// <para>
        /// 测试里到处是 <see cref="Form"/> 形参，这里统一转一次类型。
        /// 这几样以前是主窗体的私有字段，测试靠反射去挖——内部一改名测试就红，
        /// 安全网反过来挡住了重构。现在主工程把它们收进
        /// <see cref="TrackLyricsSession"/>，并给了测试一个 internal 只读入口。
        /// </para>
        /// </summary>
        private static TrackLyricsSession SessionOf(Form form) => ((播放器.MainForm)form).Track;

        /// <summary>主窗体的设置对象（测试要摆开 / 关在线获取、换地址这些场景）。</summary>
        private static AppSettings SettingsOf(Form form) => ((播放器.MainForm)form).Settings;

        /// <summary>主窗体的在线查找服务（测试要"假装这次运行还没取过"时清它的缓存）。</summary>
        private static OnlineLyricsService OnlineOf(Form form) => ((播放器.MainForm)form).Online;

        /// <summary>主窗体的歌单库状态（当前歌单名 / 未保存标记 / 替换前的询问规则）。</summary>
        private static PlaylistLibraryController PlaylistsOf(Form form) => ((播放器.MainForm)form).Playlists;

        /// <summary>主窗体的播放列表模型（摆"列表里有哪些项"的场景用）。</summary>
        private static Playlist PlaylistOf(Form form) => ((播放器.MainForm)form).Playlist;

        private static void PumpMessages(int milliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }

        /// <summary>
        /// 泵消息直到条件成立（或超时）。
        /// <para>
        /// 判据里不含"必须多快成立"：慢机器上定位就是要多花几百毫秒，
        /// 拿固定等待时间当基准会变成抽风式红（维护手册第 1 节的经验）。
        /// </para>
        /// </summary>
        private static bool PumpUntil(Func<bool> condition, int milliseconds)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

            while (!condition())
            {
                if (DateTime.UtcNow >= deadline) return false;
                PumpMessages(50);
            }

            return true;
        }

        private static int ReadUInt16(byte[] data, int offset) => (data[offset] << 8) | data[offset + 1];

        private static long ReadUInt32(byte[] data, int offset) =>
            ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) | ((long)data[offset + 2] << 8) | data[offset + 3];

        private static Control FindByTypeName(Control root, string typeName) =>
            Enumerate(root).FirstOrDefault(c => c.GetType().Name == typeName);

        private static IEnumerable<Control> Enumerate(Control root)
        {
            foreach (Control child in root.Controls)
            {
                yield return child;

                foreach (var nested in Enumerate(child)) yield return nested;
            }
        }


        /// <summary>数一张位图里"真的有内容"的像素（alpha 大于 8），用来判断某块画布是不是空白的。</summary>
        private static int CountOpaquePixels(Bitmap bitmap)
        {
            var opaque = 0;

            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A > 8) opaque++;
                }
            }

            return opaque;
        }



        /// <summary>等到条件成立，返回是否成立，并带出实际等待时长。</summary>
        private static bool WaitForCount(Func<int> read, int expected, out int elapsedMs)
        {
            // 放宽到 60 秒：连续跑多轮时，杀毒软件扫描刚生成的 WAV 会让解析慢得离谱，
            // 卡在这里的偶发失败会掩盖真正的回归。
            const int timeoutMs = 60000;
            var start = Environment.TickCount64;

            while (Environment.TickCount64 - start < timeoutMs)
            {
                if (read() >= expected)
                {
                    elapsedMs = (int)(Environment.TickCount64 - start);
                    return true;
                }

                PumpMessages(50);
            }

            elapsedMs = (int)(Environment.TickCount64 - start);
            return read() >= expected;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch { }
        }


        /// <summary>轮询等待某个东西出现（期间保持消息泵转）。</summary>
        private static T? WaitFor<T>(Func<T?> read, Func<T, bool> accept, int seconds) where T : class
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);

            while (DateTime.UtcNow < deadline)
            {
                PumpMessages(120);

                var value = read();

                if (value != null && accept(value)) return value;
            }

            return null;
        }

    }
}
