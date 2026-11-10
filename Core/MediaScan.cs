using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

// 目录遍历：把"这个目录里有哪些媒体文件"做成一件有界、可取消、能报进度的事。
//
// 以前只有 MediaFormats.EnumerateMediaFiles(folder, recursive)：递归到底、没有条数上限、
// 中途不能停、也不告诉你已经找到多少。本地目录还无所谓，网络目录（离线的 NAS / SMB）
// 上跑起来就是"几分钟没有任何反馈，也没有停的手段"（1.17.0 的外部评审第 3 条）。
//
// 这里把遍历本身收进一个地方，本地那一路继续走"无限"参数，行为与以前一致；
// 网络那一路按 MediaScanLimits 收口。

namespace 播放器.Core
{
    /// <summary>目录遍历的上限。根目录算第 0 层。</summary>
    internal sealed class MediaScanLimits
    {
        /// <summary>最多向下几层（16 层足够放 歌手 / 专辑 / CD1 / 曲目 这种结构）。</summary>
        internal int MaxDepth { get; set; } = 16;

        /// <summary>最多收多少个媒体文件。超了就地停，并置 <see cref="MediaScanResult.Truncated"/>。</summary>
        internal int MaxFiles { get; set; } = 100_000;

        /// <summary>不设上限（本地目录沿用的旧行为）。</summary>
        internal static MediaScanLimits Unlimited() => new MediaScanLimits
        {
            MaxDepth = int.MaxValue,
            MaxFiles = int.MaxValue
        };
    }

    /// <summary>一次遍历的结果。取消 / 触到上限时也保留已经找到的条目。</summary>
    internal sealed class MediaScanResult
    {
        /// <summary>找到的媒体文件，已按文件名排序（<c>CurrentCultureIgnoreCase</c>，与以前一致）。</summary>
        internal List<string> Files { get; } = new List<string>();

        /// <summary>触到了深度或条数上限：结果不完整。</summary>
        internal bool Truncated { get; set; }

        /// <summary>被取消：结果不完整，但已经找到的都在。</summary>
        internal bool Cancelled { get; set; }

        /// <summary>真正打开过的目录数（只为日志 / 断言，不显示给用户）。</summary>
        internal int FoldersVisited { get; set; }
    }

    /// <summary>
    /// 有界、可取消的目录遍历。<b>阻塞调用</b>：不做任何界面动作，调用方负责丢到后台。
    /// <para>
    /// 顺序与旧的 <c>Directory.EnumerateFiles(…, AllDirectories)</c> 不同（这里是显式栈的
    /// 深度优先），但结果在返回前统一排序，所以对调用方没有差别。
    /// </para>
    /// <para>
    /// 单个子目录读不出来（没有权限 / 中途掉线 / 目录消失）只跳过<b>这一层</b>，与旧实现
    /// 的"整棵树都放弃"相比更宽容——这是有意的，不是顺手改出来的。
    /// </para>
    /// </summary>
    internal static class MediaScanner
    {
        /// <param name="onFile">每找到一个媒体文件调一次（做进度用）；可以在里面读共享计数。</param>
        internal static MediaScanResult Walk(
            string folder,
            MediaScanLimits limits,
            CancellationToken token,
            Action<string>? onFile)
        {
            var result = new MediaScanResult();
            if (string.IsNullOrWhiteSpace(folder)) return result;

            limits ??= MediaScanLimits.Unlimited();

            if (!SafeExists(folder)) return result;

            // 显式栈：深度优先，元素带上"这一层算第几层"。
            var pending = new Stack<(string Folder, int Depth)>();
            pending.Push((folder, 0));

            while (pending.Count > 0)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }

                var (current, depth) = pending.Pop();
                result.FoldersVisited++;

                foreach (var file in SafeFiles(current))
                {
                    if (!MediaFormats.IsMediaFile(file)) continue;

                    if (result.Files.Count >= limits.MaxFiles)
                    {
                        result.Truncated = true;
                        pending.Clear();
                        break;
                    }

                    result.Files.Add(file);
                    onFile?.Invoke(file);

                    // 回调里可能就把 token 取消了（测试与"用户点了取消"都是这样）：
                    // 每个文件之后都看一眼，不然这一层剩下的文件会继续往里加。
                    if (token.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        pending.Clear();
                        break;
                    }
                }

                if (result.Truncated || result.Cancelled) break;

                // 还能往下走吗：这一层已经是上限了就不再进子目录，但要记下"没扫全"。
                if (depth >= limits.MaxDepth)
                {
                    if (HasSubdirectory(current)) result.Truncated = true;
                    continue;
                }

                foreach (var child in SafeDirectories(current))
                    pending.Push((child, depth + 1));
            }

            result.Files.Sort(StringComparer.CurrentCultureIgnoreCase);
            return result;
        }

        private static bool SafeExists(string folder)
        {
            try
            {
                return Directory.Exists(folder);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string[] SafeFiles(string folder)
        {
            try
            {
                return Directory.GetFiles(folder, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
            catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }

        private static string[] SafeDirectories(string folder)
        {
            try
            {
                return Directory.GetDirectories(folder, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
            catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }

        /// <summary>这一层还有没有子目录（判"深度上限"是不是真的截掉了东西）。</summary>
        private static bool HasSubdirectory(string folder)
        {
            try
            {
                using var walker = Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly)
                    .GetEnumerator();
                return walker.MoveNext();
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return false; }
        }
    }
}
