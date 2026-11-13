using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// 在线歌词 / 封面的磁盘缓存（1.20.0）。
//
// 会话缓存本来就在 OnlineLyricsService 里（按路径），关掉程序就没了，于是同一首歌
// 每次启动都要再联网要一遍——而歌词和封面从服务端取回来以后基本不会变。
// 这里把"已经取回来的东西"落到数据目录里，重启直接读。

namespace 播放器.Core
{
    /// <summary>
    /// 在线歌词与封面的磁盘缓存。
    /// <para>
    /// <b>为什么按路径做键、而不是按"歌手 + 曲名"</b>：本程序所有"按文件记住"的东西
    /// （进度、音轨、字幕轨、歌词偏移）都是按路径记的；在线结果也照这个口径，
    /// 换一套键只会在"同一首歌不同压制"上更容易挑错。代价是文件挪走 / 改名之后
    /// 旧条目命中不到——它们会占着空间，直到超过上限被最旧的先清掉。
    /// </para>
    /// <para>
    /// <b>一条一个 JSON</b>：歌词存 LRC 原文（这样才能原样还原时间轴），封面存 base64。
    /// 好处是"坏一条只坏一条"、清一条不用重写整份索引；代价是封面会大三分之一左右，
    /// 上限按实际字节算，所以这一点已经算进去了。
    /// </para>
    /// <para>
    /// 所有失败都<b>只记日志、不抛</b>：缓存是纯优化，它坏了最多是"再联网要一次"，
    /// 绝不能因为磁盘满 / 目录只读让播放或界面出问题。
    /// </para>
    /// </summary>
    public sealed class OnlineCache
    {
        /// <summary>默认上限：128 MB（一张封面几十 KB～一两 MB，够存上千首）。</summary>
        public const long DefaultMaximumBytes = 128L * 1024 * 1024;

        /// <summary>超过这个大小的封面不落盘：多半是服务端给了一张原图，没必要为它挤掉几十首。</summary>
        public const int MaximumCoverBytes = 4 * 1024 * 1024;

        private const int FormatVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            // 缓存文件是给程序自己看的，不需要缩进；省一半字节
            WriteIndented = false
        };

        private readonly object _sync = new object();
        private readonly long _maximumBytes;
        private string? _lastFault;

        public OnlineCache(string root, long maximumBytes = DefaultMaximumBytes)
        {
            Root = Path.GetFullPath(root ?? throw new ArgumentNullException(nameof(root)));
            _maximumBytes = Math.Max(0, maximumBytes);
        }

        /// <summary>缓存根目录（下面是 <c>lyrics</c> 与 <c>covers</c> 两个子目录）。</summary>
        public string Root { get; }

        private string LyricsFolder => Path.Combine(Root, "lyrics");

        private string CoversFolder => Path.Combine(Root, "covers");

        /// <summary>缓存里现在有多少条（歌词 + 封面）。</summary>
        public int Count
        {
            get
            {
                lock (_sync)
                {
                    try
                    {
                        return EnumerateFiles().Count();
                    }
                    catch (Exception)
                    {
                        return 0;
                    }
                }
            }
        }

        /// <summary>缓存一共占多少字节。</summary>
        public long Bytes
        {
            get
            {
                lock (_sync)
                {
                    try
                    {
                        return EnumerateFiles().Sum(file => new FileInfo(file).Length);
                    }
                    catch (Exception)
                    {
                        return 0;
                    }
                }
            }
        }

        /// <summary>
        /// 路径 → 缓存键（大写规范化之后的 SHA-256 前 32 位十六进制）。
        /// <para>Windows 路径不区分大小写，所以先统一大写：同一个文件写成 <c>C:\A.mp3</c>
        /// 还是 <c>c:\a.mp3</c> 都命中同一条。</para>
        /// </summary>
        public static string KeyFor(string path)
        {
            var full = path ?? string.Empty;

            try
            {
                full = Path.GetFullPath(full);
            }
            catch (Exception)
            {
                // 路径都拼不出来就按原样做键（读不到也写不进去，但不会抛）
            }

            var bytes = Encoding.UTF8.GetBytes(full.ToUpperInvariant());
            var hash = SHA256.HashData(bytes);

            return Convert.ToHexString(hash, 0, 16);
        }

        // =====================================================================
        // 歌词
        // =====================================================================

        /// <summary>读一条歌词缓存；文件坏了就删掉它并返回 <c>false</c>（由调用方重新联网）。</summary>
        public bool TryReadLyrics(string path, out LyricsDocument? document)
        {
            document = null;

            lock (_sync)
            {
                var file = LyricsFile(path);

                var entry = ReadEntry(file, expectedKind: "lyrics");
                if (entry == null) return false;

                LyricsDocument parsed;

                try
                {
                    parsed = LyricsDocument.Parse(entry.Text, entry.Source);
                }
                catch (Exception)
                {
                    DeleteFile(file);
                    return false;
                }

                if (parsed.IsEmpty)
                {
                    // 缓存里存的是一份"空歌词"：当坏掉处理，下次联网重新要
                    DeleteFile(file);
                    return false;
                }

                document = parsed;
                return true;
            }
        }

        public void WriteLyrics(string path, LyricsDocument document)
        {
            if (document == null || document.IsEmpty) return;

            string text;

            try
            {
                text = document.ToLrcText();
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            WriteEntry(LyricsFile(path), new CacheEntry
            {
                Version = FormatVersion,
                Kind = "lyrics",
                Source = document.Source,
                Text = text
            });
        }

        // =====================================================================
        // 封面
        // =====================================================================

        public bool TryReadCover(string path, out byte[]? data, out string? mime)
        {
            data = null;
            mime = null;

            lock (_sync)
            {
                var file = CoverFile(path);

                var entry = ReadEntry(file, expectedKind: "cover");
                if (entry == null) return false;

                byte[] bytes;

                try
                {
                    bytes = Convert.FromBase64String(entry.Data ?? string.Empty);
                }
                catch (FormatException)
                {
                    DeleteFile(file);
                    return false;
                }

                if (!LrcApi.LooksLikeImage(bytes))
                {
                    DeleteFile(file);
                    return false;
                }

                data = bytes;
                mime = string.IsNullOrWhiteSpace(entry.Mime) ? LrcApi.ImageMimeType(bytes) : entry.Mime;
                return true;
            }
        }

        public void WriteCover(string path, byte[] data, string mime)
        {
            if (data is not { Length: > 0 }) return;

            if (data.Length > MaximumCoverBytes)
            {
                ReportFault($"封面超过 {MaximumCoverBytes / (1024 * 1024)} MB，不写进磁盘缓存（{data.Length} 字节）");
                return;
            }

            WriteEntry(CoverFile(path), new CacheEntry
            {
                Version = FormatVersion,
                Kind = "cover",
                Mime = string.IsNullOrWhiteSpace(mime) ? LrcApi.ImageMimeType(data) : mime,
                Data = Convert.ToBase64String(data)
            });
        }

        // =====================================================================
        // 清理
        // =====================================================================

        /// <summary>忘掉某一首的歌词与封面。</summary>
        public void ClearEntry(string path)
        {
            lock (_sync)
            {
                DeleteFile(LyricsFile(path));
                DeleteFile(CoverFile(path));
            }
        }

        /// <summary>只清歌词（封面那份不动）。</summary>
        public void ClearLyrics()
        {
            lock (_sync) ClearFolder(LyricsFolder);
        }

        /// <summary>只清封面（歌词那份不动）。</summary>
        public void ClearCovers()
        {
            lock (_sync) ClearFolder(CoversFolder);
        }

        /// <summary>把整个缓存删掉（两个子目录都清）。</summary>
        public void Clear()
        {
            lock (_sync)
            {
                ClearFolder(LyricsFolder);
                ClearFolder(CoversFolder);
            }
        }

        private void ClearFolder(string folder)
        {
            foreach (var file in EnumerateFilesIn(folder).ToArray()) DeleteFile(file);

            TryDeleteEmptyFolder(folder);
        }

        // =====================================================================
        // 内部
        // =====================================================================

        private string LyricsFile(string path) => Path.Combine(LyricsFolder, KeyFor(path) + ".json");

        private string CoverFile(string path) => Path.Combine(CoversFolder, KeyFor(path) + ".json");

        private static IEnumerable<string> EnumerateFilesIn(string folder)
        {
            try
            {
                return System.IO.Directory.Exists(folder)
                    ? System.IO.Directory.EnumerateFiles(folder, "*.json")
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private IEnumerable<string> EnumerateFiles() =>
            EnumerateFilesIn(LyricsFolder).Concat(EnumerateFilesIn(CoversFolder));

        private CacheEntry? ReadEntry(string file, string expectedKind)
        {
            string json;

            try
            {
                if (!File.Exists(file)) return null;

                json = File.ReadAllText(file);
            }
            catch (Exception)
            {
                // 读不出来可能是别人正写着（极少见）：不删，下次再说
                return null;
            }

            CacheEntry? entry;

            try
            {
                entry = JsonSerializer.Deserialize<CacheEntry>(json, JsonOptions);
            }
            catch (JsonException)
            {
                DeleteFile(file);
                return null;
            }

            if (entry == null ||
                entry.Version != FormatVersion ||
                !string.Equals(entry.Kind, expectedKind, StringComparison.Ordinal))
            {
                DeleteFile(file);
                return null;
            }

            return entry;
        }

        private void WriteEntry(string file, CacheEntry entry)
        {
            try
            {
                SafeFile.WriteAllText(file, JsonSerializer.Serialize(entry, JsonOptions));
            }
            catch (Exception ex)
            {
                ReportFault("写不进磁盘缓存（" + ex.GetType().Name + "），这次只在内存里用");
                return;
            }

            try
            {
                Prune();
            }
            catch (Exception ex)
            {
                ReportFault("清理磁盘缓存失败（" + ex.GetType().Name + "）");
            }
        }

        /// <summary>
        /// 超过上限就按最旧的先删，删到 3/4 为止（留一截空间，免得每写一条都要清一次）。
        /// </summary>
        private void Prune()
        {
            var files = EnumerateFiles()
                .Select(file => new { File = file, Info = new FileInfo(file) })
                .Where(item => item.Info.Exists)
                .ToList();

            var total = files.Sum(item => item.Info.Length);

            if (total <= _maximumBytes) return;

            var target = _maximumBytes * 3 / 4;

            foreach (var item in files.OrderBy(item => item.Info.LastWriteTimeUtc))
            {
                if (total <= target) break;

                DeleteFile(item.File);
                total -= item.Info.Length;
            }
        }

        private void DeleteFile(string file)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception)
            {
                // 删不掉就留着，不耽误这次（下次还会尝试清理）
            }
        }

        private static void TryDeleteEmptyFolder(string folder)
        {
            try
            {
                if (!System.IO.Directory.Exists(folder)) return;
                if (System.IO.Directory.EnumerateFileSystemEntries(folder).Any()) return;

                System.IO.Directory.Delete(folder);
            }
            catch (Exception)
            {
                // 目录删不掉无所谓：留着是个空目录
            }
        }

        /// <summary>同一条原因只记一次日志（缓存出问题不该每切一首刷一行）。</summary>
        private void ReportFault(string reason)
        {
            lock (_sync)
            {
                if (string.Equals(_lastFault, reason, StringComparison.Ordinal)) return;

                _lastFault = reason;
                AppLog.Warn("在线缓存：" + reason);
            }
        }

        private sealed class CacheEntry
        {
            [JsonPropertyName("v")] public int Version { get; set; }

            [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;

            [JsonPropertyName("source")] public string? Source { get; set; }

            [JsonPropertyName("text")] public string? Text { get; set; }

            [JsonPropertyName("mime")] public string? Mime { get; set; }

            [JsonPropertyName("data")] public string? Data { get; set; }
        }
    }
}
