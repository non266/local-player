using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace 播放器.Core
{
    /// <summary>文件页递归搜索的上限。根目录算第 0 层。</summary>
    internal sealed class FileBrowserSearchLimits
    {
        /// <summary>最多向下几层。本地 32 层足够放 歌手 / 专辑 / CD1 / 曲目。</summary>
        internal int MaxDepth { get; set; } = 32;

        /// <summary>最多收多少条命中结果。</summary>
        internal int MaxResults { get; set; } = 20_000;

        /// <summary>最多看多少条目录项（命中与不命中都算）。</summary>
        internal int MaxEntriesVisited { get; set; } = 200_000;

        /// <summary>本地目录：与「整个文件夹」导入同一套层数，但按“结果 / 已看条目”分别计数。</summary>
        internal static FileBrowserSearchLimits LocalBounded() => new FileBrowserSearchLimits();

        /// <summary>网络路径：层数、结果与已看条目都收紧，掉线的 SMB 不能扫上几分钟。</summary>
        internal static FileBrowserSearchLimits NetworkBounded() => new FileBrowserSearchLimits
        {
            MaxDepth = 16,
            MaxResults = 10_000,
            MaxEntriesVisited = 100_000
        };

        /// <summary>按路径选上限；只判前缀 / 盘类型，不碰服务器。</summary>
        internal static FileBrowserSearchLimits ForPath(string? root) =>
            MediaFormats.IsNetworkPath(root) ? NetworkBounded() : LocalBounded();
    }

    /// <summary>搜索进度：给状态行用，不参与结果。</summary>
    internal sealed class FileBrowserSearchProgress
    {
        internal FileBrowserSearchProgress(int foldersVisited, int entriesVisited, int results)
        {
            FoldersVisited = foldersVisited;
            EntriesVisited = entriesVisited;
            Results = results;
        }

        internal int FoldersVisited { get; }

        internal int EntriesVisited { get; }

        internal int Results { get; }
    }

    /// <summary>一次递归搜索的结果。取消 / 触到上限时也保留已经找到的条目。</summary>
    internal sealed class FileBrowserSearchResult
    {
        internal List<FileBrowserEntry> Entries { get; } = new List<FileBrowserEntry>();

        /// <summary>触到了深度 / 结果 / 已看条目上限：结果不完整。</summary>
        internal bool Truncated { get; set; }

        /// <summary>被取消：结果不完整，调用方应当丢弃。</summary>
        internal bool Cancelled { get; set; }

        /// <summary>根目录本身打不开时给用户看的错误；子目录打不开只计数。</summary>
        internal string? Error { get; set; }

        internal int FoldersVisited { get; set; }

        internal int EntriesVisited { get; set; }

        /// <summary>子目录读不了的数量：结果可能少，状态行要说明。</summary>
        internal int UnreadableFolders { get; set; }
    }

    /// <summary>
    /// 文件页的递归搜索：<b>阻塞调用</b>，不做界面动作，调用方负责丢到后台。
    /// <para>
    /// 只读，不删 / 不改 / 不移动。匹配规则复用 <see cref="FileBrowserFilter.Matches"/>，
    /// 条目分类 / 隐藏项判断复用 <see cref="FileBrowserService.CreateEntry"/>——不另写一套，
    /// 否则“搜索里看得见、列表里看不见”会变成两个真值。
    /// </para>
    /// <para>
    /// 目录联接 / 符号链接不向下走：它们可能指回上层，跟进就是无限递归。名字命中的链接
    /// 仍可以作为一条结果出现，只是不展开。
    /// </para>
    /// </summary>
    internal class FileBrowserSearch
    {
        /// <summary>测试可以换一套上限；为 <c>null</c> 时按根路径自动选本地 / 网络。</summary>
        internal FileBrowserSearchLimits? Limits { get; set; }

        internal virtual FileBrowserSearchResult Search(
            string root,
            string? query,
            bool showAllFiles,
            CancellationToken token,
            IProgress<FileBrowserSearchProgress>? progress)
        {
            var result = new FileBrowserSearchResult();

            if (string.IsNullOrWhiteSpace(root))
            {
                result.Error = "没有指定文件夹";
                return result;
            }

            if (!Directory.Exists(root))
            {
                result.Error = File.Exists(root)
                    ? "这是一个文件，不是文件夹"
                    : "文件夹不存在（可能已拔出或离线）";
                return result;
            }

            var limits = Limits ?? FileBrowserSearchLimits.ForPath(root);
            var maxDepth = Math.Max(0, limits.MaxDepth);
            var maxResults = Math.Max(1, limits.MaxResults);
            var maxEntries = Math.Max(1, limits.MaxEntriesVisited);
            var terms = FileBrowserFilter.Split(query);

            var pending = new Stack<(string Folder, int Depth)>();
            pending.Push((root, 0));

            var lastReport = Environment.TickCount64;

            void Report(bool force)
            {
                if (progress == null) return;

                var now = Environment.TickCount64;
                if (!force && now - lastReport < 100) return;

                lastReport = now;
                progress.Report(new FileBrowserSearchProgress(
                    result.FoldersVisited, result.EntriesVisited, result.Entries.Count));
            }

            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    Report(true);
                    return result;
                }

                var (current, depth) = pending.Pop();
                result.FoldersVisited++;

                IEnumerable<FileSystemInfo> entries;

                try
                {
                    entries = new DirectoryInfo(current).EnumerateFileSystemInfos();
                }
                catch (Exception ex)
                {
                    if (depth == 0)
                    {
                        result.Error = DescribeRootError(ex);
                        Report(true);
                        return result;
                    }

                    result.UnreadableFolders++;
                    Report(false);
                    continue;
                }

                try
                {
                    foreach (var info in entries)
                    {
                        if (token.IsCancellationRequested)
                        {
                            result.Cancelled = true;
                            Report(true);
                            return result;
                        }

                        if (result.EntriesVisited >= maxEntries)
                        {
                            result.Truncated = true;
                            Report(true);
                            return result;
                        }

                        result.EntriesVisited++;

                        var entry = FileBrowserService.CreateEntry(info, showAllFiles);
                        if (entry == null)
                        {
                            Report(false);
                            continue;
                        }

                        if (FileBrowserFilter.Matches(entry, terms))
                        {
                            if (result.Entries.Count >= maxResults)
                            {
                                result.Truncated = true;
                                Report(true);
                                return result;
                            }

                            result.Entries.Add(entry);
                        }

                        if (entry.IsDirectory && !IsReparsePoint(info))
                        {
                            if (depth >= maxDepth)
                                result.Truncated = true;
                            else
                                pending.Push((entry.FullPath, depth + 1));
                        }

                        Report(false);
                    }
                }
                catch (Exception ex)
                {
                    if (depth == 0 && result.EntriesVisited == 0)
                    {
                        result.Error = DescribeRootError(ex);
                        Report(true);
                        return result;
                    }

                    result.UnreadableFolders++;
                    Report(false);
                }

                Report(false);
            }

            Report(true);
            return result;
        }

        /// <summary>是不是目录联接 / 符号链接；读不到属性时按“不是”处理，最多多扫一层。</summary>
        private static bool IsReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string DescribeRootError(Exception ex) => ex switch
        {
            UnauthorizedAccessException => "没有权限读取这个文件夹",
            DirectoryNotFoundException => "文件夹不存在（可能已拔出或离线）",
            PathTooLongException => "路径太长了",
            IOException => "读取失败（驱动器可能未就绪或已断开）",
            ArgumentException => "路径里有不能用的字符",
            _ => "读取失败，详情见日志"
        };
    }
}
