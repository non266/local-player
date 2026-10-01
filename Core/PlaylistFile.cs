using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace 播放器.Core
{
    /// <summary>
    /// 扩展 M3U 播放列表的读写。
    /// </summary>
    public static class PlaylistFile
    {
        private const string Header = "#EXTM3U";

        /// <summary>把播放列表保存为 m3u/m3u8 文件。</summary>
        public static void Save(string filePath, IReadOnlyList<PlaylistItem> items)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("路径不能为空。", nameof(filePath));
            if (items == null) throw new ArgumentNullException(nameof(items));

            var full = Path.GetFullPath(filePath);
            var folder = Path.GetDirectoryName(full) ?? string.Empty;

            // 走临时文件 + 改名覆盖：这是"用户点了保存"的操作，写到一半失败不该
            // 把他原来的歌单截断成半截。BOM 仍然保留（见下）。
            SafeFile.WriteAllText(full, "\uFEFF" + BuildText(items, folder));
        }

        /// <summary>
        /// 渲染成 m3u8 文本。
        /// <para><paramref name="folder"/> 用来把同目录（同盘）下的路径写成相对路径。</para>
        /// </summary>
        public static string BuildText(IReadOnlyList<PlaylistItem> items, string folder)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            var builder = new StringBuilder();
            builder.AppendLine(Header);

            foreach (var item in items)
            {
                if (item.IsStream)
                {
                    builder.AppendLine("#EXTINF:-1," + item.DisplayName);
                    builder.AppendLine(item.FilePath);
                    continue;
                }

                var seconds = item.Duration.HasValue ? (int)item.Duration.Value.TotalSeconds : -1;
                builder.AppendLine($"#EXTINF:{seconds},{item.DisplayName}");

                // 同盘/同目录下写入相对路径，便于整个文件夹一起搬移。
                var path = item.FilePath;
                if (!string.IsNullOrEmpty(folder) && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = path.Substring(folder.Length).TrimStart('\\', '/');

                    // 这里只可能得到"目标的后代路径"，本来就不会含 ..
                    // （folder 是 filePath 的目录，path 又在 folder 下面）。
                    // 以前用 relative.Contains("..") 判断，会把名字里带连续两个点的
                    // 正常文件/目录（"01 Outro...mp3"、"My..Folder"）误判成目录穿越，
                    // 于是悄悄退回绝对路径。改成只在首段真的是 .. 时才放弃。
                    if (relative.Length > 0 && !StartsWithParentSegment(relative))
                        path = relative;
                }

                builder.AppendLine(path);
            }

            return builder.ToString();
        }

        /// <summary>
        /// 读取 m3u/m3u8 文件里的<b>全部</b>条目（相对路径已解析为绝对路径），
        /// <b>不检查文件是否还在</b>。
        /// <para>
        /// <see cref="Load"/> 会把"已经不存在的文件"直接丢掉。这对"打开别人的歌单"没问题，
        /// 但对<b>自己存的歌单</b>不行：丢了几首必须让用户知道（歌单库据此报"有 2 项文件不在了"）。
        /// </para>
        /// </summary>
        public static List<string> ReadPaths(string filePath)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(filePath)) return result;
            if (!File.Exists(filePath)) return result;

            var folder = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch (IOException)
            {
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                return result;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                if (MediaFormats.IsStreamUri(line))
                {
                    result.Add(line);
                    continue;
                }

                var path = line;

                try
                {
                    if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(folder))
                        path = Path.GetFullPath(Path.Combine(folder, path));
                }
                catch (Exception)
                {
                    // 非法字符、超长路径等等：这一行解析不出来就跳过，
                    // 不要让整个歌单因为一行坏数据全部读不出来。
                    continue;
                }

                result.Add(path);
            }

            return result;
        }

        /// <summary>读取 m3u/m3u8 文件，返回其中<b>现在还能打开</b>的媒体路径（相对路径已解析为绝对路径）。</summary>
        public static List<string> Load(string filePath) =>
            ReadPaths(filePath).Where(MediaFormats.IsOpenable).ToList();

        /// <summary>相对路径的第一段是不是 <c>..</c>（真正的目录穿越）。</summary>
        private static bool StartsWithParentSegment(string relativePath)
        {
            var firstSeparator = relativePath.IndexOfAny(new[] { '\\', '/' });

            var firstSegment = firstSeparator < 0
                ? relativePath
                : relativePath.Substring(0, firstSeparator);

            return firstSegment == "..";
        }
    }
}
