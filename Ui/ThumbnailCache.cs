using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using 播放器.Core;

namespace 播放器.Ui
{
    /// <summary>
    /// 缩略图的磁盘缓存（1.28.0）：<c>&lt;数据目录&gt;\thumbnails\&lt;sha256&gt;.png</c>。
    /// <para>
    /// 为什么要有它：视频首帧一次要几百毫秒到几秒，每进一次目录重做一遍是不能接受的；
    /// 而 56×56 的 PNG 只有 1~3 KB，128 MB 能放几万张。缓存键含**路径 + 大小 + 修改时间 + 边长 + 格式版本**：
    /// 换了封面 / 换了裁剪尺寸都会重做（1.27.0 的教训：缓存键少一项就永远拿旧图）。
    /// </para>
    /// <para>
    /// 只在缩略图队列的线程上调用（不加锁也不会撞；这里仍然上锁，因为清空菜单在界面线程上）。
    /// 目录不可写时安静降级：读不到就当没有，写失败只记一行日志。
    /// </para>
    /// </summary>
    internal sealed class ThumbnailCache
    {
        /// <summary>缓存总上限（字节）：128 MB。</summary>
        internal const long LimitBytesDefault = 128L * 1024 * 1024;

        /// <summary>超限时删到 75%，避免每存一张就要再扫一遍目录。</summary>
        private const double TrimRatio = 0.75;

        /// <summary>两次"检查上限"之间至少隔 5 分钟：扫目录本身也要花时间。</summary>
        private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(5);

        private readonly object _gate = new object();
        private readonly string _folder;

        private DateTime _lastPruneUtc = DateTime.MinValue;

        internal ThumbnailCache(string? folder = null)
        {
            _folder = folder ?? Path.Combine(AppSettings.SettingsDirectory, "thumbnails");
        }

        /// <summary>给测试用：把上限调小来验"按最旧删到 75%"。</summary>
        internal long LimitBytes { get; set; } = LimitBytesDefault;

        internal string Folder => _folder;

        internal string FullPathFor(string key) => Path.Combine(_folder, key + ".png");

        /// <summary>缓存键：路径（大小写不敏感）+ 大小 + 修改时间 + 边长 + 格式版本。</summary>
        internal static string Key(string path, long size, long modifiedTicks, int edge)
        {
            var text = "v1|" + (path ?? string.Empty).ToUpperInvariant() + "|" + size + "|" + modifiedTicks + "|" + edge;

            using var sha = SHA256.Create();

            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        }

        /// <summary>读一张；没有 / 坏了 / 尺寸不对都返回 <c>null</c>（坏文件顺手删掉，让它重做）。</summary>
        internal Bitmap? TryLoad(string key, int edge)
        {
            if (string.IsNullOrEmpty(key)) return null;

            var path = FullPathFor(key);

            try
            {
                if (!File.Exists(path)) return null;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                using var source = Image.FromStream(stream);

                if (source.Width != edge || source.Height != edge) return null;

                var bitmap = new Bitmap(edge, edge, PixelFormat.Format32bppArgb);

                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.DrawImage(source, 0, 0, edge, edge);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("缩略图缓存：读不出来，删掉重做 " + path, ex);

                TryDelete(path);
                return null;
            }
        }

        /// <summary>写一张；失败只记日志（缓存写不进去不该影响缩略图本身）。</summary>
        internal void Save(string key, Bitmap bitmap)
        {
            if (string.IsNullOrEmpty(key) || bitmap == null) return;

            try
            {
                using var memory = new MemoryStream();

                bitmap.Save(memory, ImageFormat.Png);

                SafeFile.WriteAllBytes(FullPathFor(key), memory.ToArray());

                PruneIfNeeded();
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("缩略图缓存：写不进去（不影响缩略图本身）", ex);
            }
        }

        /// <summary>清空缓存目录，返回删掉的文件数与字节数。</summary>
        internal (int Count, long Bytes) Clear()
        {
            lock (_gate)
            {
                var count = 0;
                long bytes = 0;

                try
                {
                    if (!Directory.Exists(_folder)) return (0, 0);

                    foreach (var file in new DirectoryInfo(_folder).GetFiles("*.png"))
                    {
                        try
                        {
                            bytes += file.Length;
                            file.Delete();
                            count++;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Swallowed("缩略图缓存：删一个文件失败 " + file.FullName, ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("缩略图缓存：清空失败", ex);
                }

                _lastPruneUtc = DateTime.MinValue;

                return (count, bytes);
            }
        }

        /// <summary>给测试 / 探针看的：当前文件数与总字节数。</summary>
        internal (int Count, long Bytes) Stats()
        {
            lock (_gate)
            {
                try
                {
                    if (!Directory.Exists(_folder)) return (0, 0);

                    var files = new DirectoryInfo(_folder).GetFiles("*.png");

                    return (files.Length, files.Sum(file => file.Length));
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("缩略图缓存：统计失败", ex);
                    return (0, 0);
                }
            }
        }

        private void PruneIfNeeded()
        {
            lock (_gate)
            {
                var limit = LimitBytes;

                if (limit <= 0) return;

                var now = DateTime.UtcNow;

                if (now - _lastPruneUtc < PruneInterval) return;

                _lastPruneUtc = now;

                try
                {
                    if (!Directory.Exists(_folder)) return;

                    var files = new DirectoryInfo(_folder).GetFiles("*.png");

                    var total = files.Sum(file => file.Length);

                    if (total <= limit) return;

                    var target = (long)(limit * TrimRatio);

                    foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
                    {
                        if (total <= target) break;

                        try
                        {
                            var length = file.Length;
                            file.Delete();
                            total -= length;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Swallowed("缩略图缓存：删旧文件失败 " + file.FullName, ex);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Swallowed("缩略图缓存：清理上限失败", ex);
                }
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
                AppLog.Swallowed("缩略图缓存：删坏文件失败 " + path, ex);
            }
        }
    }
}
