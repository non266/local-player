using System;
using System.IO;
using System.Text;

namespace 播放器.Core
{
    /// <summary>
    /// 原子写文本文件：先在同一个目录里写临时文件，写透到磁盘之后再整体改名覆盖目标。
    /// <para>
    /// 为什么不用 <see cref="File.WriteAllText(string, string)"/>：它是"先截断、再写"，
    /// 写到一半断电或进程被杀就会留下半截文件。而设置与播放历史的读取端都是
    /// <b>读坏了就静默退回默认值</b>，于是一次坏运气就等于把用户的窗口尺寸、主题、
    /// 音量、播放列表和所有续播位置全部无声清零。改名覆盖是同一卷上的原子操作，
    /// 要么是旧内容、要么是新内容，不存在半截状态。
    /// </para>
    /// </summary>
    internal static class SafeFile
    {
        /// <summary>进程内串行化所有原子写，避免同一目标的两次写入互相踩到。</summary>
        private static readonly object WriteGate = new object();

        /// <summary>写入失败时留下的临时文件名后缀，用于事后清理。</summary>
        private const string TempSuffix = ".tmp";

        public static void WriteAllText(string filePath, string contents)
        {
            var full = Path.GetFullPath(filePath);
            var folder = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            // 临时文件名加上随机后缀：即使有两个写者撞在一起，也不会互相覆盖半个文件。
            var temp = full + "." + Guid.NewGuid().ToString("N") + TempSuffix;

            lock (WriteGate)
            {
                try
                {
                    // FileShare.None：写入期间不允许别人碰这个临时文件。
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(contents);
                        writer.Flush();

                        // flushToDisk: true —— 把数据真正推到底层设备，
                        // 否则内容可能还停在系统缓存里，断电一样会丢。
                        stream.Flush(true);
                    }

                    File.Move(temp, full, overwrite: true);
                }
                catch (Exception)
                {
                    TryDelete(temp);
                    throw;
                }
            }
        }

        /// <summary>
        /// 把读不出来的文件改名成 <c>xxx.corrupt</c> 留档，返回备份路径；失败返回 <c>null</c>。
        /// <para>目的是不再"静默把用户的数据丢掉"——坏文件留着，至少还能人工找回一部分。</para>
        /// </summary>
        public static string? TryBackupCorrupt(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                var backup = filePath + ".corrupt";
                File.Move(filePath, backup, overwrite: true);
                return backup;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
// 清理失败无所谓，下次覆盖即可。
                AppLog.Swallowed("清理失败无所谓，下次覆盖即可。", ex);
            }
        }
    }
}
