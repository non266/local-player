using System;
using System.IO;

namespace 播放器.Core
{
    /// <summary>
    /// 同目录的外挂封面：<c>cover.jpg</c> / <c>folder.jpg</c> / <c>front.png</c> 这类常见命名。
    /// <para>
    /// 三处地方都得认同一份判定：侧栏封面（<c>CoverView</c>）、系统媒体控件（<c>SmtcSession</c>）、
    /// 以及"本地没有才去网上找封面"（<c>OnlineLyricsService</c>）。
    /// 各写一套的话就会出现"侧栏明明有封面，音量弹窗里却是空的"这种自己跟自己不一致的情况，
    /// 所以命名表与查找只写在这里一处。
    /// </para>
    /// <para>
    /// 只枚举一次目录，而不是对着四十来种文件名挨个 <c>File.Exists</c>。
    /// 命中多张时按 <see cref="Names"/> 的先后排：<c>cover.jpg</c> 赢过 <c>folder.jpg</c>，
    /// <b>不看文件系统的枚举顺序</b>（那个顺序没有任何保证，同一目录在不同机器上可能给出不同结果）。
    /// </para>
    /// </summary>
    internal static class SidecarCover
    {
        /// <summary>认的命名（不含扩展名，大小写不敏感），越靠前越优先。</summary>
        private static readonly string[] Names =
            { "cover", "folder", "front", "album", "albumart", "artwork", "cd" };

        /// <summary>认的图片扩展名（大小写不敏感），越靠前越优先。</summary>
        private static readonly string[] Extensions =
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        /// <summary>目录里现成的封面文件；没有（或目录读不了）返回 <c>null</c>。</summary>
        internal static string? FindInFolder(string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;

            try
            {
                if (!Directory.Exists(folder)) return null;

                string? best = null;
                var bestScore = int.MaxValue;

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var nameIndex = IndexOf(Names, Path.GetFileNameWithoutExtension(file));
                    if (nameIndex < 0) continue;

                    var extensionIndex = IndexOf(Extensions, Path.GetExtension(file));
                    if (extensionIndex < 0) continue;

                    // 命名优先于扩展名（cover.png 赢过 folder.jpg），同命名内再看扩展名
                    var score = nameIndex * Extensions.Length + extensionIndex;
                    if (score >= bestScore) continue;

                    best = file;
                    bestScore = score;
                }

                return best;
            }
            catch (Exception ex)
            {
                // 目录不可读就当作没有封面
                AppLog.Swallowed("目录不可读就当作没有封面", ex);
                return null;
            }
        }

        /// <summary>这个媒体文件旁边有没有外挂封面。</summary>
        internal static string? FindFor(string? mediaPath) => FindInFolder(FolderOf(mediaPath));

        /// <summary>这个目录里有没有外挂封面（只问有没有，不要路径）。</summary>
        internal static bool HasInFolder(string? folder) => FindInFolder(folder) != null;

        private static string? FolderOf(string? mediaPath)
        {
            if (string.IsNullOrEmpty(mediaPath)) return null;

            try
            {
                return Path.GetDirectoryName(mediaPath);
            }
            catch (Exception)
            {
                // 路径本身就不合法（比如带非法字符）：当作没有
                return null;
            }
        }

        private static int IndexOf(string[] candidates, string value)
        {
            for (var i = 0; i < candidates.Length; i++)
            {
                if (string.Equals(candidates[i], value, StringComparison.OrdinalIgnoreCase)) return i;
            }

            return -1;
        }
    }
}
