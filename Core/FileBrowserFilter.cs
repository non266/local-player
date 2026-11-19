using System;
using System.Collections.Generic;

namespace 播放器.Core
{
    /// <summary>文件页「只看类型」的取值；文件夹与磁盘不受它影响。</summary>
    internal enum FileBrowserTypeFilter
    {
        All,
        Video,
        Audio,
        Subtitle,
        Playlist
    }

    /// <summary>
    /// 文件页的搜索 / 类型过滤：纯逻辑，不碰磁盘。
    /// <para>
    /// 匹配只针对<b>单条条目的名字</b>（含扩展名），按空白切词、所有词都要命中；
    /// 不把完整路径算进去——否则输入当前文件夹名时每一项都会命中。
    /// 递归遍历不在这里：<see cref="FileBrowserSearch"/> 负责走目录，本类只判断“这一条算不算命中”。
    /// </para>
    /// </summary>
    internal static class FileBrowserFilter
    {
        internal static bool Matches(FileBrowserEntry entry, string? query)
        {
            if (entry == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;

            return Matches(entry, Split(query));
        }

        /// <summary>
        /// 已经切好词的版本：递归搜索要对最多 200,000 条复用同一组词，不能每一条都重新 Split。
        /// </summary>
        internal static bool Matches(FileBrowserEntry entry, IReadOnlyList<string> terms)
        {
            if (entry == null) return false;
            if (terms.Count == 0) return true;

            foreach (var term in terms)
            {
                if (!entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        internal static bool MatchesType(FileBrowserEntry entry, FileBrowserTypeFilter filter)
        {
            if (entry == null) return false;

            // 文件夹与磁盘永远保留：筛选的是文件，不是把可导航的入口一起藏掉。
            if (entry.IsDirectory) return true;

            return filter switch
            {
                FileBrowserTypeFilter.Video => entry.Kind == FileBrowserKind.Video,
                FileBrowserTypeFilter.Audio => entry.Kind == FileBrowserKind.Audio,
                FileBrowserTypeFilter.Subtitle => entry.Kind == FileBrowserKind.Subtitle,
                FileBrowserTypeFilter.Playlist => entry.Kind == FileBrowserKind.Playlist,
                _ => true
            };
        }

        internal static string[] Split(string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

            return query.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
