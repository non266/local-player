using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace 播放器.Core
{
    /// <summary>一次"列子目录"的结果（文件夹树用；只要文件夹，不要文件）。</summary>
    internal sealed class FolderTreeChildren
    {
        internal List<FileBrowserEntry> Folders { get; } = new List<FileBrowserEntry>();

        /// <summary>目录联接 / 符号链接：照常列出来，但调用方<b>不给它加展开箭头</b>（不跟进去）。</summary>
        internal HashSet<string> Links { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool Truncated { get; set; }

        internal string? Error { get; set; }

        internal bool Cancelled { get; set; }
    }

    /// <summary>
    /// 文件夹树的纯逻辑（1.28.0）：把路径拆成"从根到它自己"的链、列一层子目录。
    /// <para>
    /// 与 <see cref="FileBrowserService"/> 的区别：这里<b>只要子目录</b>，而且把
    /// 目录联接 / 符号链接单独标出来（树里不展开它们，与 1.24 / 1.26 的递归口径一致）。
    /// 上限默认 <see cref="MaxChildren"/>，触顶如实标 <see cref="FolderTreeChildren.Truncated"/>。
    /// </para>
    /// </summary>
    internal static class FolderTreeModel
    {
        /// <summary>一个节点最多列多少个子目录。</summary>
        internal const int MaxChildren = 2000;

        /// <summary>
        /// 路径的祖先链（含它自己）：<c>D:\a\b → [D:\, D:\a, D:\a\b]</c>；
        /// <c>\\srv\share\a → [\\srv\share, \\srv\share\a]</c>；此电脑 / 坏路径 → 空。
        /// </summary>
        internal static List<string> ChainOf(string? path)
        {
            var chain = new List<string>();

            if (string.IsNullOrWhiteSpace(path)) return chain;

            if (!FileBrowserPaths.TryNormalize(path, out var normalized, out _) || normalized == null) return chain;

            var current = normalized;

            // 最多 256 层：真出现环（不该有）也不会把界面挂死。
            for (var guard = 0; guard < 256 && current != null; guard++)
            {
                chain.Insert(0, current);
                current = FileBrowserPaths.ParentOf(current);
            }

            return chain;
        }

        /// <summary>列一层子目录（按名称排序、跳过隐藏 / 系统项、标出联接）。</summary>
        internal static FolderTreeChildren ChildFolders(
            string? path, bool showAllFiles, int maximum, CancellationToken token)
        {
            var result = new FolderTreeChildren();

            if (string.IsNullOrWhiteSpace(path)) return result;

            var limit = Math.Max(1, maximum);

            DirectoryInfo directory;

            try
            {
                directory = new DirectoryInfo(path);

                if (!directory.Exists)
                {
                    result.Error = "文件夹不存在（可能已拔出或离线）";
                    return result;
                }
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件夹树：打不开 " + path, ex);
                result.Error = "打不开这个文件夹";
                return result;
            }

            try
            {
                foreach (var info in directory.EnumerateDirectories())
                {
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }

                    if (result.Folders.Count >= limit)
                    {
                        result.Truncated = true;
                        break;
                    }

                    FileAttributes attributes;

                    try
                    {
                        attributes = info.Attributes;
                    }
                    catch (Exception)
                    {
                        // 断掉的符号链接 / 正在拔出的盘：跳过这一条。
                        continue;
                    }

                    if (!showAllFiles && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;

                    var modified = DateTime.MinValue;

                    try
                    {
                        modified = info.LastWriteTime;
                    }
                    catch (Exception)
                    {
                        // 读不到时间不影响列出这一项。
                    }

                    var entry = new FileBrowserEntry(
                        info.Name, info.FullName, FileBrowserKind.Folder, 0, modified);

                    result.Folders.Add(entry);

                    if ((attributes & FileAttributes.ReparsePoint) != 0) result.Links.Add(entry.FullPath);
                }
            }
            catch (UnauthorizedAccessException)
            {
                result.Error = "没有权限读取这个文件夹";
            }
            catch (DirectoryNotFoundException)
            {
                result.Error = "文件夹不存在（可能已拔出或离线）";
            }
            catch (Exception ex)
            {
                AppLog.Swallowed("文件夹树：列子目录失败 " + path, ex);
                result.Error = "读取失败，详情见日志";
            }

            result.Folders.Sort((left, right) => string.Compare(
                left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase));

            return result;
        }
    }
}
